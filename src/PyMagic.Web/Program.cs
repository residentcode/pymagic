using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using PyMagic.Core.Types;
using PyMagic.Server;

// pymagic-web [--root <dir>] [--typeshed <dir>] [--lib <dir>]... [--www <dir>] [--port <n>]
// Serves the Monaco front-end, a small file API for the workspace folder, and /lsp: one language server per browser tab.
string root = Directory.GetCurrentDirectory();
string? typeshed = Workspace.DefaultTypeshedPath;
string? www = null;
int port = 5180;
var libs = new List<string>();
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(args[++i]); break;
        case "--typeshed": typeshed = args[++i]; break;
        case "--lib": libs.Add(args[++i]); break;
        case "--www": www = Path.GetFullPath(args[++i]); break;
        case "--port": port = int.Parse(args[++i]); break;
    }
}
root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
www ??= FindWww();

static string FindWww()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        string candidate = Path.Combine(d.FullName, "web", "dist");
        if (Directory.Exists(candidate)) return candidate;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "web", "dist");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.UseWebSockets();
if (Directory.Exists(www))
{
    var files = new PhysicalFileProvider(www);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

// ---- workspace files -------------------------------------------------------------------------------------------

string ResolveInRoot(string rel)
{
    string full = Path.GetFullPath(Path.Combine(root, rel));
    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("outside the workspace");
    return full;
}

app.MapGet("/api/info", () => new
{
    root,
    rootUri = LanguageService.UriOfPath(root + Path.DirectorySeparatorChar),
    typeshed,
});

app.MapGet("/api/files", () =>
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "__pycache__", "node_modules", ".venv", "venv", "bin", "obj" };
    var list = new List<string>();
    void Walk(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.py")) list.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!skip.Contains(Path.GetFileName(d))) Walk(d);
    }
    Walk(root);
    list.Sort(StringComparer.Ordinal);
    return list;
});

app.MapGet("/api/file", (string path) =>
{
    try { return Results.Text(File.ReadAllText(ResolveInRoot(path)), "text/plain; charset=utf-8"); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.NotFound(); }
});

app.MapPut("/api/file", async (string path, HttpRequest req) =>
{
    try
    {
        string full = ResolveInRoot(path);
        if (!full.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest("only .py files");
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, await reader.ReadToEndAsync(), new UTF8Encoding(false));
        return Results.NoContent();
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.BadRequest(e.Message); }
});

// ---- language server over WebSocket ---------------------------------------------------------------------------

app.Map("/lsp", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    var service = new LanguageService { TypeshedPath = typeshed };
    service.SourceRoots.Add(root);
    service.LibraryRoots.AddRange(libs);
    var outbox = new BlockingCollection<string>();
    var server = new PyMagic.Server.Server(service, msg => outbox.Add(msg.ToJsonString()));
    var inbox = new BlockingCollection<JsonNode?>();

    // analysis recurses deeply on real code: a dedicated thread with a large stack handles this client's messages
    var worker = new Thread(() =>
    {
        foreach (var msg in inbox.GetConsumingEnumerable())
        {
            if (msg == null) break;
            try { server.Handle(msg); }
            catch (Exception e) { Console.Error.WriteLine(e); }
            if (server.Exited) break;
        }
        outbox.CompleteAdding();
    }, 512 * 1024 * 1024) { IsBackground = true };
    worker.Start();

    var sender = Task.Run(async () =>
    {
        foreach (var text in outbox.GetConsumingEnumerable())
        {
            if (ws.State != WebSocketState.Open) break;
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    });

    var buffer = new byte[64 * 1024];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) goto done;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            inbox.Add(JsonNode.Parse(ms.ToArray()));
        }
    }
    catch (WebSocketException) { }
    done:
    inbox.Add(null);
    worker.Join(TimeSpan.FromSeconds(5));
    outbox.CompleteAdding();
    await sender;
});

Console.WriteLine($"PyMagic on http://127.0.0.1:{port}/   workspace: {root}   typeshed: {typeshed ?? "(bundled stubs)"}");
app.Run();
