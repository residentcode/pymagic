using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using PyMagic.Core.Types;
using PyMagic.Server;
using PyMagic.Server.Git;

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
    // when the engine's own file was written: a build that did not happen shows here as an old date
    engineBuiltAt = File.GetLastWriteTimeUtc(typeof(LanguageService).Assembly.Location).ToString("o"),
});

// the settings of the person (theme, editor font, ...): the same in every project, kept in their own folder
var userSettings = new UserSettings();
var history = new LocalHistory(root);
app.MapGet("/api/settings", () => Results.Text(userSettings.Read(), "application/json"));
app.MapPut("/api/settings", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    try { userSettings.Write(await reader.ReadToEndAsync()); return Results.NoContent(); }
    catch (ServiceException e) { return Results.BadRequest(e.Message); }
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

// everything the explorer shows: folders and files (what the language server needs is /api/files)
app.MapGet("/api/tree", () =>
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "__pycache__", "node_modules", ".venv", "venv", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".pymagic", ".idea", ".vs", ".tox", ".eggs" };
    var dirs = new List<string>();
    var files = new List<(string Path, long Size)>();
    int budget = 20000;
    void Walk(string dir)
    {
        IEnumerable<string> subdirs, names;
        try { subdirs = Directory.EnumerateDirectories(dir).ToList(); names = Directory.EnumerateFiles(dir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var f in names)
        {
            if (budget-- <= 0) return;
            long size = 0;
            try { size = new FileInfo(f).Length; } catch (IOException) { }
            files.Add((Path.GetRelativePath(root, f).Replace('\\', '/'), size));
        }
        foreach (var d in subdirs)
        {
            string name = Path.GetFileName(d);
            if (skip.Contains(name) || name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)) continue;
            dirs.Add(Path.GetRelativePath(root, d).Replace('\\', '/'));
            Walk(d);
        }
    }
    Walk(root);
    return Results.Json(new { dirs = dirs.OrderBy(x => x, StringComparer.Ordinal), files = files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { path = f.Path, size = f.Size }) });
});

app.MapGet("/api/file", (string path) =>
{
    try
    {
        string full = ResolveInRoot(path);
        var bytes = File.ReadAllBytes(full);
        // notebooks carry their images and tables inside the file, so they may be larger than source files
        if (bytes.Length > (full.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) ? 60 : 5) * 1024 * 1024) return Results.StatusCode(413);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) >= 0) return Results.StatusCode(415);
        return Results.Text(new UTF8Encoding(false).GetString(bytes), "text/plain; charset=utf-8");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.NotFound(); }
});

app.MapPut("/api/file", async (string path, HttpRequest req) =>
{
    try
    {
        string full = ResolveInRoot(path);
        if (IsInsideGit(full)) return Results.BadRequest("the .git folder is not edited here");
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        string text = await reader.ReadToEndAsync();
        string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        // what the file held before this save is kept first (once), then what it holds after
        if (File.Exists(full) && new FileInfo(full).Length <= LocalHistory.MaxFileBytes) { try { history.Baseline(rel, File.ReadAllText(full)); } catch (IOException) { } }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text, new UTF8Encoding(false));
        history.Record(rel, text);
        return Results.NoContent();
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.BadRequest(e.Message); }
});

// the local history of a file: its versions at each save, and the text of one
app.MapGet("/api/history", (string path) =>
{
    try
    {
        string rel = Path.GetRelativePath(root, ResolveInRoot(path)).Replace('\\', '/');
        return Results.Json(history.List(rel).Select(e => new { id = e.Id, time = e.Time.ToString("o"), bytes = e.Bytes }).ToList());
    }
    catch (UnauthorizedAccessException) { return Results.NotFound(); }
});
app.MapGet("/api/history/text", (string path, string id) =>
{
    try
    {
        string rel = Path.GetRelativePath(root, ResolveInRoot(path)).Replace('\\', '/');
        return history.Read(rel, id) is { } text ? Results.Text(text, "text/plain; charset=utf-8") : Results.NotFound();
    }
    catch (UnauthorizedAccessException) { return Results.NotFound(); }
});

bool IsInsideGit(string full) =>
    Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => string.Equals(p, ".git", StringComparison.OrdinalIgnoreCase));

// create, rename and delete files and folders of the project
app.MapPost("/api/fs", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
    string? op = body?["op"]?.GetValue<string>();
    try
    {
        switch (op)
        {
            case "create":
            {
                string full = ResolveInRoot(body!["path"]!.GetValue<string>());
                if (IsInsideGit(full)) return Results.BadRequest(new { error = "not inside .git" });
                if (File.Exists(full) || Directory.Exists(full)) return Results.Conflict(new { error = "it exists already" });
                if (body["dir"]?.GetValue<bool>() == true) Directory.CreateDirectory(full);
                else { Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllTextAsync(full, "", new UTF8Encoding(false)); }
                return Results.NoContent();
            }
            case "rename":
            {
                string from = ResolveInRoot(body!["from"]!.GetValue<string>()), to = ResolveInRoot(body["to"]!.GetValue<string>());
                if (IsInsideGit(from) || IsInsideGit(to)) return Results.BadRequest(new { error = "not inside .git" });
                if (File.Exists(to) || Directory.Exists(to)) return Results.Conflict(new { error = "the target exists already" });
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(from)) Directory.Move(from, to);
                else if (File.Exists(from)) File.Move(from, to);
                else return Results.NotFound();
                return Results.NoContent();
            }
            case "delete":
            {
                string full = ResolveInRoot(body!["path"]!.GetValue<string>());
                if (IsInsideGit(full)) return Results.BadRequest(new { error = "not inside .git" });
                if (Directory.Exists(full)) Directory.Delete(full, true);
                else if (File.Exists(full)) File.Delete(full);
                else return Results.NotFound();
                return Results.NoContent();
            }
        }
        return Results.BadRequest(new { error = "unknown operation" });
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NullReferenceException) { return Results.BadRequest(new { error = e is NullReferenceException ? "bad request" : e.Message }); }
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
    service.AddSourceRoot(root);
    service.LibraryRoots.AddRange(libs);
    _ = service.StartIndexing();
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
    implementation = i.Implementation, prefix = i.Prefix, isVenv = i.IsVenv, target = i.Target.Kind, where = i.Target.Name,
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

// WSL distributions and Docker images an interpreter can be taken from
app.MapGet("/api/interpreter/targets", async () => await Task.Run(() => new
{
    wsl = WslTarget.Distros(),
    ssh = new { installed = SshTarget.Installed(), hosts = SshConfigHosts() },
    docker = new { available = DockerTarget.Available() },
    dockerImages = DockerTarget.Available() ? DockerTarget.Images() : Array.Empty<string>(),
}));

// the names in the user's ~/.ssh/config that can be connected to (wildcard entries are patterns, not hosts)
string[] SshConfigHosts()
{
    try
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(file)) return Array.Empty<string>();
        return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => l[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(h => !h.Contains('*') && !h.Contains('?') && !h.Contains('!')).Distinct().Take(200).ToArray();
    }
    catch (IOException) { return Array.Empty<string>(); }
}

// add an interpreter of a WSL distribution ({kind:"wsl", name:"Ubuntu-24.04", python:"python3"}) or a Docker image and use it
app.MapPost("/api/interpreter/remote", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string kind = body?["kind"]?.GetValue<string>() ?? "", name = body?["name"]?.GetValue<string>()?.Trim() ?? "";
    string py = body?["python"]?.GetValue<string>()?.Trim() is { Length: > 0 } given ? given : kind == "wsl" ? "python3" : "python";
    if (name.Length == 0 || name.Contains('|') || py.Contains('|') || py.StartsWith('-')) return Results.BadRequest(new { error = "give the distribution or image, and the interpreter in it" });
    string identity = body?["identity"]?.GetValue<string>()?.Trim() ?? "", knownHosts = body?["knownHosts"]?.GetValue<string>()?.Trim() ?? "";
    ExecutionTarget target = kind switch
    {
        "wsl" => new WslTarget(name),
        "docker" => new DockerTarget(name),
        "ssh" => SshTarget.FromHead(name + (identity.Length > 0 || knownHosts.Length > 0 ? "?" + string.Join("&", new[] { identity.Length > 0 ? "i=" + identity : "", knownHosts.Length > 0 ? "kh=" + knownHosts : "" }.Where(x => x.Length > 0)) : ""))!,
        _ => null!,
    };
    if (target == null) return Results.BadRequest(new { error = kind == "ssh" ? "that is not an address: write user@host or user@host:port" : "the kind is wsl, docker or ssh" });
    try { return Results.Json(Describe(await Task.Run(() => interpreters.AddRemote(target, py)))); }
    catch (ServiceException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapDelete("/api/interpreter/remote", (string spec) => { interpreters.ForgetRemote(spec); return Results.NoContent(); });

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
    var list = await Task.Run(() => PackageManager.List(current.Python, target: current.Target));
    return Results.Json(new
    {
        interpreter = Describe(current), hasPip = list.HasPip, error = list.Error, packages = list.Packages,
        isVenv = current.IsVenv, canInstall = PackageManager.CanInstallGlobally(current),
    });
});

app.MapGet("/api/packages/outdated", async () =>
    interpreters.Current is { } c ? Results.Json(await Task.Run(() => PackageManager.Outdated(c.Python, target: c.Target))) : Results.Json(Array.Empty<object>()));

app.MapGet("/api/packages/info", async (string name) =>
{
    if (interpreters.Current is not { } c || !PackageManager.IsSafeSpec(name)) return Results.NotFound();
    var info = await Task.Run(() => PackageManager.Show(c.Python, name, target: c.Target));
    return info == null ? Results.NotFound() : Results.Json(info);
});

app.MapGet("/api/requirements", async () =>
{
    var files = RequirementsScanner.Scan(root);
    var installed = interpreters.Current is { } c ? (await Task.Run(() => PackageManager.List(c.Python, target: c.Target))).Packages : Array.Empty<InstalledPackage>();
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
app.MapGet("/api/django", () => DjangoProject.Scan(root));

// the lines the last coverage run executed, from .pymagic/coverage.json
app.MapGet("/api/coverage", () =>
{
    string path = RunSession.CoveragePath(root);
    if (!File.Exists(path)) return Results.NotFound();
    try
    {
        var data = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (data == null) return Results.NotFound();
        data["when"] = File.GetLastWriteTimeUtc(path).ToString("o");
        return Results.Json(data);
    }
    catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return Results.NotFound(); }
});

// what the last profiler run measured, from .pymagic/profile.json; "rel" is the path inside the project (null for other code)
app.MapGet("/api/profile", () =>
{
    string path = RunSession.ProfilePath(root);
    if (!File.Exists(path)) return Results.NotFound();
    try
    {
        var data = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (data == null) return Results.NotFound();
        string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string? Rel(string? file)
        {
            if (file == null || !Path.IsPathRooted(file)) return null;
            string full = Path.GetFullPath(file);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !full.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !full.Contains($"{Path.DirectorySeparatorChar}site-packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? full[prefix.Length..].Replace('\\', '/') : null;
        }
        foreach (var f in data["functions"]!.AsArray())
        {
            f!["rel"] = Rel(f["file"]?.GetValue<string>());
            foreach (var c in f["callers"]!.AsArray()) c!["rel"] = Rel(c["file"]?.GetValue<string>());
        }
        data["rel"] = Rel(data["script"]?.GetValue<string>());
        data["when"] = File.GetLastWriteTimeUtc(path).ToString("o");
        return Results.Json(data);
    }
    catch (Exception e) when (e is IOException or System.Text.Json.JsonException or NullReferenceException) { return Results.NotFound(); }
});

// ---- the terminal: one command line at a time in the project folder -------------------------------------------------

app.Map("/term", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var outbox = new BlockingCollection<string>();
    void Send(JsonObject o) { if (!outbox.IsAddingCompleted) try { outbox.Add(o.ToJsonString()); } catch (InvalidOperationException) { } }
    using var term = new TerminalSession(root, () => interpreters.Current is { Target.IsLocal: true } c ? c.Path : null);   // the terminal is this computer's shell
    var sender = Task.Run(async () =>
    {
        foreach (var text in outbox.GetConsumingEnumerable())
        {
            if (ws.State != WebSocketState.Open) break;
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (WebSocketException) { break; }
        }
    });
    Send(new JsonObject { ["type"] = "ready", ["cwd"] = term.Cwd, ["shell"] = OperatingSystem.IsWindows() ? "PowerShell" : "sh" });

    var buffer = new byte[16 * 1024];
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
            switch (msg?["type"]?.GetValue<string>())
            {
                case "run":
                    try
                    {
                        term.Run(msg["command"]?.GetValue<string>() ?? "",
                            (stream, text) => Send(new JsonObject { ["type"] = stream, ["text"] = text }),
                            (code, cwd) => Send(new JsonObject { ["type"] = "exit", ["code"] = code, ["cwd"] = cwd }));
                    }
                    catch (InvalidOperationException e) { Send(new JsonObject { ["type"] = "err", ["text"] = e.Message + "\n" }); }
                    break;
                case "stdin": term.WriteLine(msg["text"]?.GetValue<string>() ?? ""); break;
                case "kill": term.Kill(); break;
            }
        }
    }
    catch (WebSocketException) { }
    done:
    term.Kill();
    outbox.CompleteAdding();
    await sender;
    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { }
});

// ---- notebooks: one kernel per open notebook; execute / interrupt / restart in, the kernel's messages out -------------------

app.Map("/nb", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var outbox = new BlockingCollection<string>();
    void Send(JsonNode msg) { if (!outbox.IsAddingCompleted) try { outbox.Add(msg.ToJsonString()); } catch (InvalidOperationException) { } }
    NotebookKernel? kernel = null;
    int generation = 0;
    void StartKernel()
    {
        kernel?.Dispose();
        int mine = ++generation;
        // messages of a kernel that was replaced (restart) must not reach the page
        kernel = new NotebookKernel(msg => { if (mine == generation) Send(msg); }) { Python = interpreters.Current?.Python ?? python, Target = interpreters.Current?.Target ?? ExecutionTarget.Local };
        kernel.Start(root);
    }

    var sender = Task.Run(async () =>
    {
        foreach (var text in outbox.GetConsumingEnumerable())
        {
            if (ws.State != WebSocketState.Open) break;
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (WebSocketException) { break; }
        }
    });

    StartKernel();
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
            switch (msg?["type"]?.GetValue<string>())
            {
                case "execute": kernel?.Execute(msg["id"]?.GetValue<string>() ?? "", msg["code"]?.GetValue<string>() ?? ""); break;
                case "interrupt": kernel?.Interrupt(); break;
                case "restart": StartKernel(); break;
            }
        }
    }
    catch (WebSocketException) { }
    done:
    generation++;
    kernel?.Dispose();
    outbox.CompleteAdding();
    await sender;
    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { }
});

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
                session.Python = interpreters.Current?.Python ?? python; session.Target = interpreters.Current?.Target ?? ExecutionTarget.Local;
                // what a run configuration adds: variables, and a folder inside the project to start in
                session.ExtraEnvironment = RunConfigurations.CleanEnvironment(msg["env"] as JsonObject);
                session.WorkingDirectory = msg["cwd"]?.GetValue<string>() is { Length: > 0 } cwd && Directory.Exists(ResolveInRoot(cwd)) ? ResolveInRoot(cwd) : null;
                if (msg["profile"]?.GetValue<bool>() == true) session.StartProfile(root, file0, args);
                else session.StartWithBreakpoints(root, file0, msg["debug"]?.GetValue<bool>() ?? false, args, bps);
                if (msg["exceptionBreak"]?.GetValue<string>() == "raised")
                    session.Handle(new JsonObject { ["type"] = "setExceptionBreak", ["mode"] = "raised" });
            }
            else if (msg["type"]?.GetValue<string>() == "startTests")
            {
                var ids = (msg["ids"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                session.Python = interpreters.Current?.Python ?? python; session.Target = interpreters.Current?.Target ?? ExecutionTarget.Local;
                session.StartTests(root, ids, msg["framework"]?.GetValue<string>() ?? "auto", msg["coverage"]?.GetValue<bool>() == true);
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
                session.Python = target.Python; session.Target = target.Target;
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
// ---- git ------------------------------------------------------------------------------------------------------

// The workspace folder's repository, looked up again while there is none (the user may create one from the page).
GitRepository? gitRepo = null;
string? gitReason = null;
object gitLock = new();
GitRepository? FindGit()
{
    lock (gitLock)
    {
        gitRepo ??= GitRepository.Locate(root, out gitReason);
        return gitRepo;
    }
}

async Task<IResult> GitCall(Func<GitRepository, object?> work)
{
    var repo = FindGit();
    if (repo == null) return Results.BadRequest(new { error = gitReason ?? "no repository" });
    try { return Results.Json(await Task.Run(() => work(repo))); }
    catch (GitException e) { return Results.BadRequest(new { error = e.Message }); }
}

string[] StringsOf(JsonNode? node) => node is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToArray() : Array.Empty<string>();

app.MapGet("/api/git/status", async () =>
{
    var repo = FindGit();
    if (repo == null) return Results.Json(new { available = false, reason = gitReason, canInit = gitReason == "not a git repository" });
    try
    {
        var status = await Task.Run(repo.Status);
        return Results.Json(new { available = true, status.Branch, status.Upstream, status.Ahead, status.Behind, status.Detached, status.HasCommits, status.Files });
    }
    catch (GitException e) { return Results.Json(new { available = false, reason = e.Message, canInit = false }); }
});

app.MapPost("/api/git/init", async () =>
{
    try
    {
        var created = await Task.Run(() => GitRepository.Init(root));
        lock (gitLock) gitRepo = created;
        return Results.Json(new { ok = true });
    }
    catch (GitException e) { return Results.BadRequest(new { error = e.Message }); }
});

// a file as committed (HEAD by default); 204 when the revision does not have it (a 404 would only fill the browser console)
app.MapGet("/api/git/file", async (string path, string? rev) =>
{
    var repo = FindGit();
    if (repo == null) return Results.NoContent();
    try
    {
        var text = await Task.Run(() => repo.FileAt(rev ?? "HEAD", path));
        return text == null ? Results.NoContent() : Results.Text(text, "text/plain; charset=utf-8");
    }
    catch (GitException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapGet("/api/git/log", (string? path, int? limit, int? skip) => GitCall(r => r.Log(string.IsNullOrEmpty(path) ? null : path, limit ?? 100, skip ?? 0)));
app.MapGet("/api/git/commit", (string hash) => GitCall(r => r.Show(hash)));
app.MapGet("/api/git/blame", (string path) => GitCall(r => r.Blame(path)));
app.MapGet("/api/git/branches", () => GitCall(r => r.Branches()));

// a file's changes as hunks and lines, for choosing which ones to commit
app.MapGet("/api/git/diff", (string path) => GitCall(r => r.DiffOf(path)));

// "paths" are committed whole; "partial" maps a path to { id, hunks: { "0": [line, ...] } } for the lines of it to commit
app.MapPost("/api/git/commit", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string message = body?["message"]?.GetValue<string>() ?? "";
    bool amend = body?["amend"]?.GetValue<bool>() ?? false;
    var partial = new Dictionary<string, LineSelection>();
    if (body?["partial"] is JsonObject chosen)
        foreach (var (file, node) in chosen)
        {
            var sel = new LineSelection { Id = node?["id"]?.GetValue<string>() ?? "" };
            if (node?["hunks"] is JsonObject hunks)
                foreach (var (index, lines) in hunks)
                    if (int.TryParse(index, out int h) && lines is JsonArray arr) sel.Hunks[h] = arr.Select(x => x?.GetValue<int>() ?? -1).ToHashSet();
            partial[file] = sel;
        }
    if (partial.Count == 0)
        return await GitCall(r => new { hash = r.Commit(StringsOf(body?["paths"]), message, amend) });
    return await GitCall(r => new { hash = r.CommitSelection(StringsOf(body?["paths"]), partial, message, amend) });
});

app.MapPost("/api/git/discard", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    return await GitCall(r => { r.Discard(StringsOf(body?["paths"])); return new { ok = true }; });
});

app.MapPost("/api/git/checkout", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    return await GitCall(r => { r.Checkout(body?["branch"]?.GetValue<string>() ?? ""); return new { ok = true }; });
});

app.MapPost("/api/git/branch", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    return await GitCall(r => { r.CreateBranch(body?["name"]?.GetValue<string>() ?? ""); return new { ok = true }; });
});

// ---- remotes, branch management, conflicts, stash, tags, commit actions ------------------------------------------------

app.MapGet("/api/git/remotes", () => GitCall(r => r.Remotes()));
// the commits after one, oldest first: what an interactive rebase onto it would replay
app.MapGet("/api/git/rebase/plan", (string baseHash) => GitCall(r => r.RebasePlan(baseHash)));
app.MapGet("/api/git/stashes", () => GitCall(r => r.Stashes()));
app.MapGet("/api/git/tags", () => GitCall(r => r.Tags()));
app.MapGet("/api/git/stash/files", (int index) => GitCall(r => r.StashFiles(index)));
// what is waiting for you: a merge, rebase, cherry-pick or revert that stopped, and the files that have conflicts
app.MapGet("/api/git/operation", () => GitCall(r => new { operation = r.Operation(), conflicts = r.Conflicts() }));
app.MapGet("/api/git/conflict", (string path) => GitCall(r => r.ConflictStages(path)));

app.MapPost("/api/git/action", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string op = body?["op"]?.GetValue<string>() ?? "";
    string Str(string key) => body?[key]?.GetValue<string>() ?? "";
    bool Flag(string key) => body?[key]?.GetValue<bool>() ?? false;
    int Int(string key) => body?[key]?.GetValue<int>() ?? -1;
    return await GitCall(r =>
    {
        object? Ok() => new { ok = true };
        switch (op)
        {
            case "fetch": return new { ok = true, output = r.Fetch(string.IsNullOrEmpty(Str("remote")) ? null : Str("remote"), Flag("prune")) };
            case "pull": return r.Pull(Str("mode"));
            case "push": return new { ok = true, output = r.Push(Flag("force")) };
            case "addRemote": r.AddRemote(Str("name"), Str("url")); return Ok();
            case "removeRemote": r.RemoveRemote(Str("name")); return Ok();
            case "deleteBranch": r.DeleteBranch(Str("name"), Flag("force")); return Ok();
            case "renameBranch": r.RenameBranch(Str("name"), Str("newName")); return Ok();
            case "checkoutRemote": r.CheckoutRemoteBranch(Str("branch")); return Ok();
            case "merge": return r.Merge(Str("branch"), Flag("noFf"));
            case "abort": r.Abort(); return Ok();
            case "continue": return r.Continue();
            case "resolve": r.Resolve(Str("path"), Str("side")); return Ok();
            case "markResolved": r.MarkResolved(Str("path")); return Ok();
            case "stash": return new { ok = true, output = r.Stash(Str("message"), Flag("untracked")) };
            case "applyStash": return r.ApplyStash(Int("index"), Flag("drop"));
            case "dropStash": r.DropStash(Int("index")); return Ok();
            case "revert": return r.Revert(Str("hash"));
            case "rebaseInteractive":
                return r.RebaseInteractive(Str("base"), (body?["steps"] as JsonArray ?? new JsonArray())
                    .Select(x => new GitRebaseStep(x?["hash"]?.GetValue<string>() ?? "", x?["action"]?.GetValue<string>() ?? "", x?["message"]?.GetValue<string>())).ToList());
            case "cherryPick": return r.CherryPick(Str("hash"));
            case "reset": r.Reset(Str("hash"), Str("mode")); return Ok();
            case "createTag": r.CreateTag(Str("name"), Str("hash"), Str("message")); return Ok();
            case "deleteTag": r.DeleteTag(Str("name")); return Ok();
            default: throw new GitException($"'{op}' is not something to do");
        }
    });
});

Console.WriteLine($"PyMagic on http://127.0.0.1:{port}/   workspace: {root}   typeshed: {typeshed ?? "(bundled stubs)"}");
app.Run();
