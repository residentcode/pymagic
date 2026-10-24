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
string? python = null;
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(args[++i]); break;
        case "--typeshed": typeshed = args[++i]; break;
        case "--lib": libs.Add(args[++i]); break;
        case "--www": www = Path.GetFullPath(args[++i]); break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--python": python = args[++i]; break;
    }
}
root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
www ??= FindWww();

// ---- the project's interpreter ---------------------------------------------------------------------------------

var interpreters = new InterpreterManager(root);
var lspInboxes = new ConcurrentDictionary<BlockingCollection<JsonNode?>, byte>();
interpreters.Changed += chosen =>
{
    // every open editor session follows the new interpreter (its libraries and version change what the code means)
    foreach (var inbox in lspInboxes.Keys)
        try
        {
            inbox.Add(new JsonObject
            {
                ["method"] = "workspace/didChangeConfiguration",
                ["params"] = new JsonObject { ["settings"] = new JsonObject { ["pymagic"] = new JsonObject { ["pythonPath"] = chosen?.Path ?? "" } } },
            });
        }
        catch (InvalidOperationException) { /* the session just ended */ }
};
Task.Run(() =>
{
    try { if (python != null) interpreters.Select(python, save: false); else interpreters.InitializeDefault(); }
    catch (ServiceException e) { Console.Error.WriteLine(e.Message); }
});

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
    lspInboxes[inbox] = 0;
    if (interpreters.Current is { } currentInterpreter) service.ConfigureInterpreter(currentInterpreter);

    // analysis recurses deeply on real code: a dedicated thread with a large stack handles this client's messages
    var worker = new Thread(() =>
    {
        try { server.Pump(inbox); }
        catch (Exception e) { Console.Error.WriteLine(e); }
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
    lspInboxes.TryRemove(inbox, out _);
    worker.Join(TimeSpan.FromSeconds(5));
    outbox.CompleteAdding();
    await sender;
});


// ---- running and debugging ---------------------------------------------------------------------------------------

object Describe(PythonInterpreter i) => new
{
    path = i.Path, label = i.Label, kind = i.Kind, source = i.Source, version = i.Version, platform = i.Platform,
    implementation = i.Implementation, prefix = i.Prefix, isVenv = i.IsVenv,
    current = interpreters.Current != null && string.Equals(interpreters.Current.Path, i.Path, StringComparison.OrdinalIgnoreCase),
};

app.MapGet("/api/interpreter", () => interpreters.Current is { } c ? Results.Json(Describe(c)) : Results.Json(new { }));

app.MapGet("/api/interpreters", async () => (await interpreters.ListAsync()).Select(Describe).ToList());

app.MapPost("/api/interpreter", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string? path = body?["path"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
    try { return Results.Json(Describe(await Task.Run(() => interpreters.Select(path)))); }
    catch (ServiceException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapPost("/api/venv", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string? basePython = body?["base"]?.GetValue<string>() ?? interpreters.Current?.Path;
    if (basePython == null) return Results.BadRequest(new { error = "no interpreter to create the environment with" });
    try
    {
        string exe = await Task.Run(() => interpreters.CreateVenv(basePython, body?["name"]?.GetValue<string>() ?? ".venv"));
        return Results.Json(new { path = exe });
    }
    catch (ServiceException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapGet("/api/tests", () => TestDiscovery.Discover(root));

app.Map("/run", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var outbox = new BlockingCollection<string>();
    using var session = new RunSession(msg => { if (!outbox.IsAddingCompleted) try { outbox.Add(msg.ToJsonString()); } catch (InvalidOperationException) { } }) { Python = python };

    var sender = Task.Run(async () =>
    {
        foreach (var text in outbox.GetConsumingEnumerable())
        {
            if (ws.State != WebSocketState.Open) break;
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (WebSocketException) { break; }
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

            var msg = JsonNode.Parse(ms.ToArray());
            if (msg == null) continue;
            if (msg["type"]?.GetValue<string>() == "start")
            {
                var bps = new Dictionary<string, List<int>>();
                if (msg["breakpoints"] is JsonObject bo)
                    foreach (var (file, lines) in bo)
                        bps[file] = (lines as JsonArray)?.Select(l => l!.GetValue<int>()).ToList() ?? new List<int>();
                var args = (msg["args"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                string file0 = msg["file"]!.GetValue<string>();
                ResolveInRoot(file0);   // refuses paths outside the workspace
                session.Python = interpreters.Current?.Path ?? python;
                session.Start(root, file0, msg["debug"]?.GetValue<bool>() ?? false, args, bps);
            }
            else if (msg["type"]?.GetValue<string>() == "startTests")
            {
                var ids = (msg["ids"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                session.Python = interpreters.Current?.Path ?? python;
                session.StartTests(root, ids, msg["framework"]?.GetValue<string>() ?? "auto");
            }
            else session.Handle(msg);
        }
    }
    catch (WebSocketException) { }
    catch (UnauthorizedAccessException) { }
    done:
    session.Dispose();
    outbox.CompleteAdding();
    await sender;
});
Console.WriteLine($"PyMagic on http://127.0.0.1:{port}/   workspace: {root}   typeshed: {typeshed ?? "(bundled stubs)"}");
app.Run();
