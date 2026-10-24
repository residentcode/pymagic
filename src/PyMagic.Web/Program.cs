using System.Text.RegularExpressions;
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

// Only this page may talk to the host. Browsers let any web page open a WebSocket or send a POST to localhost, and the
// host can run programs and install packages, so requests must come from our own address (this also stops DNS rebinding).
var allowedAuthorities = new[] { $"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}" };
app.Use(async (ctx, next) =>
{
    bool Allowed(string authority) => allowedAuthorities.Contains(authority, StringComparer.OrdinalIgnoreCase);
    string? origin = ctx.Request.Headers.Origin.ToString();
    bool originOk = string.IsNullOrEmpty(origin)
        || (Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Scheme == "http" && Allowed(o.Authority));
    if (!Allowed(ctx.Request.Host.Value ?? "") || !originOk)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("forbidden: requests must come from the PyMagic page itself");
        return;
    }
    await next();
});

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

// Find in files: the page sends the texts of its unsaved editors so that results match what is on screen.
app.MapPost("/api/search", async (HttpRequest req) =>
{
    try
    {
        var body = await JsonNode.ParseAsync(req.Body) as JsonObject ?? throw new ServiceException("bad request");
        string? Str(string key) => body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        bool Flag(string key) => body[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var unsaved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body["unsaved"] is JsonObject open)
            foreach (var (path, text) in open) if (text is JsonValue tv && tv.TryGetValue<string>(out var t)) unsaved[path] = t;
        var options = new SearchOptions(Str("query") ?? "", Flag("regex"), Flag("caseSensitive"), Flag("wholeWord"), Str("include"), Str("exclude"), Str("replace"));
        return Results.Json(WorkspaceSearch.Run(root, options, unsaved), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
    catch (ServiceException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "bad request" }); }
    catch (RegexMatchTimeoutException) { return Results.BadRequest(new { error = "The pattern is too slow on this project; make it more specific." }); }
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


// ---- packages ----------------------------------------------------------------------------------------------------

app.MapGet("/api/packages", async () =>
{
    var current = interpreters.Current;
    if (current == null) return Results.Json(new { error = "no interpreter selected", hasPip = false, packages = Array.Empty<object>() });
    var list = await Task.Run(() => PackageManager.List(current.Path));
    return Results.Json(new
    {
        interpreter = Describe(current), hasPip = list.HasPip, error = list.Error, packages = list.Packages,
        isVenv = current.IsVenv, canInstall = PackageManager.CanInstallGlobally(current),
    });
});

app.MapGet("/api/packages/outdated", async () =>
    interpreters.Current is { } c ? Results.Json(await Task.Run(() => PackageManager.Outdated(c.Path))) : Results.Json(Array.Empty<object>()));

app.MapGet("/api/packages/info", async (string name) =>
{
    if (interpreters.Current is not { } c || !PackageManager.IsSafeSpec(name)) return Results.NotFound();
    var info = await Task.Run(() => PackageManager.Show(c.Path, name));
    return info == null ? Results.NotFound() : Results.Json(info);
});

app.MapGet("/api/requirements", async () =>
{
    var files = RequirementsScanner.Scan(root);
    var installed = interpreters.Current is { } c ? (await Task.Run(() => PackageManager.List(c.Path))).Packages : Array.Empty<InstalledPackage>();
    var byName = installed.ToDictionary(p => PackageManager.Normalize(p.Name), p => p.Version);
    return files.Select(f => new
    {
        file = f.File,
        requirements = f.Requirements.Select(r => new
        {
            name = r.Name, spec = r.Spec,
            installed = byName.ContainsKey(PackageManager.Normalize(r.Name)),
            version = byName.GetValueOrDefault(PackageManager.Normalize(r.Name)),
        }),
    });
});

var pypiCache = new ConcurrentDictionary<string, (DateTime At, object? Info)>(StringComparer.OrdinalIgnoreCase);
using var pypi = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
app.MapGet("/api/pypi", async (string name) =>
{
    if (!PackageManager.IsSafeSpec(name) || name.Contains('[') || name.Contains(',') || name.Contains('=')) return Results.BadRequest(new { error = "not a package name" });
    if (pypiCache.TryGetValue(name, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
        return hit.Info == null ? Results.NotFound() : Results.Json(hit.Info);
    try
    {
        using var res = await pypi.GetAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/json");
        if (!res.IsSuccessStatusCode) { pypiCache[name] = (DateTime.UtcNow, null); return Results.NotFound(); }
        var info = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["info"]!;
        object result = new { name = info["name"]!.GetValue<string>(), version = info["version"]!.GetValue<string>(), summary = info["summary"]?.GetValue<string>() ?? "" };
        pypiCache[name] = (DateTime.UtcNow, result);
        return Results.Json(result);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Results.Json(new { error = "the package index is not reachable" }, statusCode: 502); }
});
app.MapGet("/api/tests", () => TestDiscovery.Discover(root));

app.Map("/run", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var outbox = new BlockingCollection<string>();
    bool packageOperation = false;
    using var session = new RunSession(msg =>
    {
        if (!outbox.IsAddingCompleted) try { outbox.Add(msg.ToJsonString()); } catch (InvalidOperationException) { }
        // new or removed packages change what the editor can resolve: announce the interpreter again
        if (packageOperation && msg["type"]?.GetValue<string>() == "exited") Task.Run(() => interpreters.Reload());
    }) { Python = python };

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
                var bps = new Dictionary<string, JsonArray>();
                if (msg["breakpoints"] is JsonObject bo)
                    foreach (var (file, lines) in bo)
                        bps[file] = lines is JsonArray arr ? (JsonArray)JsonNode.Parse(arr.ToJsonString())! : new JsonArray();
                var args = (msg["args"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                string file0 = msg["file"]!.GetValue<string>();
                ResolveInRoot(file0);   // refuses paths outside the workspace
                session.Python = interpreters.Current?.Path ?? python;
                session.StartWithBreakpoints(root, file0, msg["debug"]?.GetValue<bool>() ?? false, args, bps);
                if (msg["exceptionBreak"]?.GetValue<string>() == "raised")
                    session.Handle(new JsonObject { ["type"] = "setExceptionBreak", ["mode"] = "raised" });
            }
            else if (msg["type"]?.GetValue<string>() == "startTests")
            {
                var ids = (msg["ids"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                session.Python = interpreters.Current?.Path ?? python;
                session.StartTests(root, ids, msg["framework"]?.GetValue<string>() ?? "auto");
            }
            else if (msg["type"]?.GetValue<string>() == "startPip")
            {
                if (interpreters.Current is not { } target) { outbox.Add(new JsonObject { ["type"] = "error", ["message"] = "no interpreter selected" }.ToJsonString()); continue; }
                string op = msg["op"]?.GetValue<string>() ?? "";
                var names = (msg["packages"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                var pipArgs = new List<string>();
                string module = "pip";
                string? refusal = null;
                if (names.Any(n => !PackageManager.IsSafeSpec(n))) refusal = "a package name contains characters that are not allowed";
                else switch (op)
                {
                    case "install":
                        if (names.Count == 0) { refusal = "no packages given"; break; }
                        pipArgs.AddRange(new[] { "install", "--disable-pip-version-check", "--no-input", "--progress-bar", "off" });
                        if (msg["upgrade"]?.GetValue<bool>() == true) pipArgs.Add("--upgrade");
                        if (msg["user"]?.GetValue<bool>() == true && !target.IsVenv) pipArgs.Add("--user");
                        pipArgs.AddRange(names);
                        break;
                    case "uninstall":
                        if (names.Count == 0) { refusal = "no packages given"; break; }
                        pipArgs.AddRange(new[] { "uninstall", "-y", "--disable-pip-version-check" });
                        pipArgs.AddRange(names);
                        break;
                    case "installRequirements":
                    {
                        // only files that the scanner lists (inside the project) can be installed from
                        string wanted = msg["file"]?.GetValue<string>() ?? "";
                        var file = RequirementsScanner.Scan(root).FirstOrDefault(f => f.File == wanted && wanted.EndsWith(".txt"));
                        if (file == null) { refusal = "that is not a requirements file of this project"; break; }
                        pipArgs.AddRange(new[] { "install", "--disable-pip-version-check", "--no-input", "--progress-bar", "off", "-r", Path.Combine(root, wanted) });
                        break;
                    }
                    case "ensurepip":
                        module = "ensurepip";
                        pipArgs.Add("--upgrade");
                        break;
                    default: refusal = "unknown operation"; break;
                }
                if (refusal != null) { outbox.Add(new JsonObject { ["type"] = "error", ["message"] = refusal }.ToJsonString()); continue; }
                packageOperation = true;
                session.Python = target.Path;
                session.StartModule(root, module, pipArgs);
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
