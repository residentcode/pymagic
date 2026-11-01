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
// without --typeshed or PYMAGIC_TYPESHED: the typeshed shipped with PyMagic (the repo's typeshed/ when run from Visual Studio)
typeshed ??= FindTypeshed();

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

static string? FindTypeshed()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        string candidate = Path.Combine(d.FullName, "typeshed");
        if (File.Exists(Path.Combine(candidate, "stdlib", "VERSIONS"))) return candidate;
    }
    return null;
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
    // the page's own files are asked for again at each load (an unchanged one answers 304), so a rebuilt PyMagic never shows an old stylesheet
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
}

// ---- workspace files -------------------------------------------------------------------------------------------

// the folders marked as sources, tests or excluded (Mark Directory As)
ProjectFolders.Load(root);

string ResolveInRoot(string rel)
{
    // a scratch file is kept outside the project and reached as .scratches/name
    if (Scratches.IsScratchPath(rel)) return Scratches.PathOf(rel);
    string full = Path.GetFullPath(Path.Combine(root, rel));
    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("outside the workspace");
    return full;
}

// Attach to Process: the Python programs running on this computer
app.MapGet("/api/processes", () => Results.Json(RunSession.PythonProcesses()));

// scratch files: kept outside the project, opened as .scratches/name (see Scratches)
app.MapGet("/api/scratches", () => Results.Json(Scratches.List().Select(f => new { name = f.Name, size = f.Size, modified = f.Modified.ToString("o") }).ToList()));
app.MapPost("/api/scratches", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try { return Results.Json(new { name = Scratches.Create(body?["name"]?.GetValue<string>() ?? "", body?["text"]?.GetValue<string>()) }); }
    catch (UnauthorizedAccessException) { return Results.BadRequest("That is not the name of a scratch file."); }
    catch (IOException e) { return Results.BadRequest(e.Message); }
});
app.MapDelete("/api/scratches", (string name) =>
{
    try { return Scratches.Delete(name) ? Results.NoContent() : Results.NotFound(); }
    catch (UnauthorizedAccessException) { return Results.BadRequest("That is not the name of a scratch file."); }
});
app.MapPost("/api/scratches/rename", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try { return Scratches.Rename(body?["from"]?.GetValue<string>() ?? "", body?["to"]?.GetValue<string>() ?? "") ? Results.NoContent() : Results.BadRequest("There is no such scratch, or that name is taken."); }
    catch (UnauthorizedAccessException) { return Results.BadRequest("That is not the name of a scratch file."); }
});

// spelling: Windows' own spell checker for the words of a file, and the project's dictionary (.pymagic/dictionary.txt)
string? spellDefault = null;
string SpellLanguage(string? lang) => string.IsNullOrWhiteSpace(lang) ? (spellDefault ??= Spelling.DefaultLanguage()) : lang;
app.MapGet("/api/spell", (string? lang) => Results.Json(new { available = Spelling.Available(SpellLanguage(lang)), language = SpellLanguage(lang), words = Spelling.ProjectWords(root).OrderBy(w => w).ToList() }));
app.MapPost("/api/spell", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    var words = (body?["words"] as JsonArray)?.Select(w => w?.GetValue<string>() ?? "").Take(3000).ToList() ?? new List<string>();
    string language = SpellLanguage(body?["lang"]?.GetValue<string>());
    return Results.Json(new { language, bad = Spelling.Misspelled(root, language, words) });
});
app.MapGet("/api/spell/suggest", (string word, string? lang) => Results.Json(Spelling.Suggest(SpellLanguage(lang), word)));
app.MapPost("/api/spell/dictionary", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try { Spelling.AddWord(root, body?["word"]?.GetValue<string>() ?? ""); return Results.NoContent(); }
    catch (ArgumentException) { return Results.BadRequest("That is not a word."); }
});
app.MapDelete("/api/spell/dictionary", (string word) => Spelling.RemoveWord(root, word) ? Results.NoContent() : Results.NotFound());
// an image of the project, as bytes, for the Markdown preview (the other files are text)
app.MapGet("/api/raw", (string path) =>
{
    try
    {
        string full = ResolveInRoot(path);
        string? type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", ".bmp" => "image/bmp", ".ico" => "image/x-icon", _ => null,
        };
        if (type == null) return Results.StatusCode(415);
        var info = new FileInfo(full);
        if (!info.Exists) return Results.NotFound();
        if (info.Length > 20 * 1024 * 1024) return Results.StatusCode(413);
        return Results.File(File.ReadAllBytes(full), type);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.NotFound(); }
});
// reStructuredText as HTML by docutils, when the project's Python has it: {text}
app.MapPost("/api/rst", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try { return Results.Json(Databases.Ask(DatabasePython(), new JsonObject { ["text"] = body?["text"]?.GetValue<string>() ?? "" }, 30000, "pm_rst.py")); }
    catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
});

// the macros of the project (.pymagic/macros.json): [{name, key, steps}]
app.MapGet("/api/macros", () =>
{
    string file = Path.Combine(root, ".pymagic", "macros.json");
    try { return File.Exists(file) ? Results.Text(File.ReadAllText(file), "application/json") : Results.Text("[]", "application/json"); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.Text("[]", "application/json"); }
});
app.MapPut("/api/macros", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    string text = await reader.ReadToEndAsync();
    if (text.Length > 2_000_000) return Results.StatusCode(413);
    try { if (JsonNode.Parse(text) is not JsonArray) return Results.BadRequest("a list of macros"); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest("not JSON"); }
    string file = Path.Combine(root, ".pymagic", "macros.json");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
    return Results.NoContent();
});

// Deployment: servers the project is uploaded to (a folder, FTP or SFTP), and upload, download, compare and sync
object ServerJson(DeployServer sv) => new { id = sv.Id, name = sv.Name, type = sv.Type, host = sv.Host, port = sv.Port, user = sv.User, root = sv.Root, keyFile = sv.KeyFile, uploadOnSave = sv.UploadOnSave, mappings = sv.Mappings, excluded = sv.Excluded, hostKey = sv.HostKey, hasPassword = DeployStore.PasswordOf(root, sv.Id).Length > 0 };
IResult DeployFail(Exception e) => Results.Json(new { error = e is ServiceException ? e.Message : e is System.Net.WebException we && we.Response is System.Net.FtpWebResponse fr ? fr.StatusDescription.Trim() : e.Message });
string BrowsePath(DeployServer sv, string path) => sv.Type == "folder" ? path.Trim('/') : string.Join("/", new[] { sv.Root.Trim('/'), path.Trim('/') }.Where(x => x.Length > 0));
var deployLog = new List<object>();
void DeployNote(string server, string path, bool ok, string? error) { lock (deployLog) { deployLog.Add(new { time = DateTime.UtcNow.ToString("o"), server, path, ok, error }); if (deployLog.Count > 100) deployLog.RemoveAt(0); } }

app.MapGet("/api/deploy/servers", () => Results.Json(DeployStore.Load(root).Select(ServerJson).ToList()));
// {…server fields, password?, test?: false}: the connection is tried first; a server that does not answer is not kept
app.MapPost("/api/deploy/servers", async (HttpRequest req) =>
{
    if (await JsonNode.ParseAsync(req.Body) is not JsonObject body) return Results.BadRequest("unknown request");
    try
    {
        var existing = body["id"]?.GetValue<string>() is { Length: > 0 } id ? DeployStore.Find(root, id) : null;
        var sv = DeployStore.Parse(body, existing?.Id);
        if (existing != null && existing.Host == sv.Host && existing.Port == sv.Port && existing.User == sv.User) sv = sv with { HostKey = body["forgetKey"]?.GetValue<bool>() == true ? "" : existing.HostKey };
        string? password = body["password"]?.GetValue<string>();
        DeployStore.Save(root, sv, password);                       // kept first, so a host key learned while testing lands in the saved record
        if (body["test"]?.GetValue<bool>() != false)
        {
            try { using var remote = Deployment.Open(root, DeployStore.Find(root, sv.Id)!); remote.List(BrowsePath(sv, "")); }
            catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException)
            {
                if (existing == null) DeployStore.Remove(root, sv.Id);
                return DeployFail(e);
            }
        }
        return Results.Json(ServerJson(DeployStore.Find(root, sv.Id)!));
    }
    catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
});
app.MapDelete("/api/deploy/servers", (string id) => { DeployStore.Remove(root, id); return Results.NoContent(); });
// the folder of a server: what is in it, each with the file of the project it is (when a mapping says)
app.MapGet("/api/deploy/list", (string server, string? path) =>
{
    if (DeployStore.Find(root, server) is not { } sv) return Results.NotFound();
    try
    {
        string dir = BrowsePath(sv, path ?? "");
        using var remote = Deployment.Open(root, sv);
        var entries = remote.List(dir).Select(e => new { name = e.Name, isDir = e.IsDir, size = e.Size, modified = e.Modified.ToString("o"), local = Deployment.LocalOf(sv, dir.Length == 0 ? e.Name : dir + "/" + e.Name) }).ToList();
        return Results.Json(new { path = (path ?? "").Trim('/'), entries });
    }
    catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException) { return DeployFail(e); }
});
// paths are those of the project (files or folders); what goes up or comes down is reported file by file
app.MapPost("/api/deploy/upload", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    if (DeployStore.Find(root, body?["server"]?.GetValue<string>() ?? "") is not { } sv) return Results.NotFound();
    var paths = (body?["paths"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new();
    try
    {
        var results = Deployment.Upload(root, sv, paths);
        foreach (var r in results) DeployNote(sv.Name, r.Path, r.Ok, r.Error);
        return Results.Json(new { results });
    }
    catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException) { return DeployFail(e); }
});
app.MapPost("/api/deploy/download", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    if (DeployStore.Find(root, body?["server"]?.GetValue<string>() ?? "") is not { } sv) return Results.NotFound();
    var paths = (body?["paths"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new();
    try { return Results.Json(new { results = Deployment.Download(root, sv, paths) }); }
    catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException) { return DeployFail(e); }
});
app.MapGet("/api/deploy/compare", (string server, string path) =>
{
    if (DeployStore.Find(root, server) is not { } sv) return Results.NotFound();
    try
    {
        var c = Deployment.Compare(root, sv, path);
        return Results.Json(new { localExists = c.LocalExists, remoteExists = c.RemoteExists, local = c.Local, remote = c.Remote, same = c.Same });
    }
    catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException) { return DeployFail(e); }
});
app.MapGet("/api/deploy/sync", (string server, string? path) =>
{
    if (DeployStore.Find(root, server) is not { } sv) return Results.NotFound();
    try { return Results.Json(new { items = Deployment.SyncPlan(root, sv, path ?? "") }); }
    catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException) { return DeployFail(e); }
});
// {server, actions: [{path, action: upload | download}]}: what a sync window chose
app.MapPost("/api/deploy/sync", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    if (DeployStore.Find(root, body?["server"]?.GetValue<string>() ?? "") is not { } sv) return Results.NotFound();
    var actions = (body?["actions"] as JsonArray)?.Select(a => (Path: a?["path"]?.GetValue<string>() ?? "", Action: a?["action"]?.GetValue<string>() ?? "")).ToList() ?? new();
    try
    {
        var results = new List<TransferResult>();
        results.AddRange(Deployment.Upload(root, sv, actions.Where(a => a.Action == "upload").Select(a => a.Path)));
        results.AddRange(Deployment.Download(root, sv, actions.Where(a => a.Action == "download").Select(a => a.Path)));
        foreach (var r in results) DeployNote(sv.Name, r.Path, r.Ok, r.Error);
        return Results.Json(new { results });
    }
    catch (Exception e) when (e is ServiceException or System.Net.WebException or Renci.SshNet.Common.SshException or IOException) { return DeployFail(e); }
});
app.MapGet("/api/deploy/log", () => { lock (deployLog) return Results.Json(deployLog.AsEnumerable().Reverse().ToList()); });

// grammar: LanguageTool, downloaded once and run as a small server on this machine (see Grammar)
object GrammarStatus(string? lang) => new
{
    installed = Grammar.InstalledFolder() != null, java = Grammar.JavaVersion(), running = Grammar.Running, folder = Grammar.Folder, url = Grammar.DownloadUrl,
    installing = new { phase = Grammar.Installing.Phase, bytes = Grammar.Installing.Bytes, total = Grammar.Installing.Total, error = Grammar.Installing.Error },
    ignored = Grammar.IgnoredRules(root),
};
app.MapGet("/api/grammar", (string? lang) => Results.Json(GrammarStatus(lang)));
app.MapPost("/api/grammar/install", () => { Grammar.StartInstall(); return Results.Json(GrammarStatus(null)); });
// {text, lang?}: the problems of one piece of prose, without the rules this project switched off
app.MapPost("/api/grammar/check", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string text = body?["text"]?.GetValue<string>() ?? "";
    string lang = body?["lang"]?.GetValue<string>() is { Length: > 0 } l && l.All(c => char.IsLetter(c) || c == '-') ? l : "en-US";
    if (text.Length > 20000) return Results.BadRequest("too long");
    try { return Results.Json(new { matches = await Grammar.Check(text, lang, Grammar.IgnoredRules(root)) }); }
    catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
});
app.MapPost("/api/grammar/ignore", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try { Grammar.IgnoreRule(root, body?["rule"]?.GetValue<string>() ?? ""); return Results.NoContent(); }
    catch (ArgumentException) { return Results.BadRequest("not a rule"); }
});
app.MapDelete("/api/grammar/ignore", (string rule) => Grammar.UnignoreRule(root, rule) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/grammar/stop", () => { Grammar.Stop(); return Results.NoContent(); });
app.Lifetime.ApplicationStopping.Register(Grammar.Stop);

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
            if (!skip.Contains(Path.GetFileName(d)) && !ProjectFolders.IsExcluded(d)) Walk(d);
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
    var marks = ProjectFolders.Marks;
    return Results.Json(new { marks = new { sources = marks.Sources, tests = marks.Tests, excluded = marks.Excluded, templates = marks.Templates ?? new(), resources = marks.Resources ?? new(), autoTemplates = ProjectFolders.AutoTemplates }, dirs = dirs.OrderBy(x => x, StringComparer.Ordinal), files = files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { path = f.Path, size = f.Size }) });
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
        // a server that takes what is saved gets this file, in the background (what happened is in the deployment log)
        foreach (var sv in DeployStore.Load(root).Where(x => x.UploadOnSave && Deployment.RemoteOf(x, rel) != null && !Deployment.IsExcluded(x, rel)).ToList())
            _ = Task.Run(() => { try { foreach (var r in Deployment.Upload(root, sv, new[] { rel })) DeployNote(sv.Name, r.Path, r.Ok, r.Error); } catch (Exception e) { DeployNote(sv.Name, rel, false, e.Message); } });
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
app.MapGet("/api/history/folder", (string? path) =>
{
    try
    {
        string rel = string.IsNullOrEmpty(path) ? "" : Path.GetRelativePath(root, ResolveInRoot(path)).Replace('\\', '/');
        return Results.Json(history.ListFolder(rel == "." ? "" : rel).Select(x => new { path = x.Path, id = x.Entry.Id, time = x.Entry.Time.ToString("o"), bytes = x.Entry.Bytes, exists = File.Exists(Path.Combine(root, x.Path)) }).ToList());
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

// labels of the whole project (by hand, or put by PyMagic before a refactoring, a test run, a Git update) and what they allow
IEnumerable<(string Rel, string Text)> TrackedFiles()
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "__pycache__", "node_modules", ".venv", "venv", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".pymagic", ".idea", ".vs", ".tox", ".eggs", "bin", "obj", "dist", "build" };
    var binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".zip", ".gz", ".tar", ".whl", ".pdf", ".dll", ".exe", ".pyc", ".pyd", ".so", ".sqlite3", ".sqlite", ".db", ".woff", ".woff2", ".ttf", ".mp3", ".mp4", ".pstats" };
    int count = 0;
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0 && count < 4000)
    {
        string dir = stack.Pop();
        IEnumerable<string> files, dirs;
        try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
        foreach (var f in files)
        {
            if (count >= 4000) break;
            if (binary.Contains(Path.GetExtension(f))) continue;
            string text;
            try { var info = new FileInfo(f); if (info.Length > LocalHistory.MaxFileBytes) continue; text = File.ReadAllText(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            count++;
            yield return (Path.GetRelativePath(root, f).Replace('\\', '/'), text);
        }
        foreach (var d in dirs)
            if (!skip.Contains(Path.GetFileName(d)) && !d.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase) && !ProjectFolders.IsExcluded(d)) stack.Push(d);
    }
}
void AutoLabel(string name) { try { history.PutLabel(name, true, TrackedFiles()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the operation goes on without its label */ } }
object LabelJson(HistoryLabel l) => new { id = l.Id, time = l.Time.ToString("o"), name = l.Name, auto = l.Auto };

app.MapGet("/api/history/labels", () => Results.Json(history.Labels().Select(LabelJson).ToList()));
app.MapPost("/api/history/label", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string name = body?["name"]?.GetValue<string>() ?? "";
    bool auto = body?["auto"]?.GetValue<bool>() ?? false;
    return Results.Json(LabelJson(history.PutLabel(name, auto, TrackedFiles())));
});
app.MapDelete("/api/history/label", (string id) => history.RemoveLabel(id) ? Results.NoContent() : Results.NotFound());
// the text of the version before one (what that save changed is the difference); 404 when it was the first
app.MapGet("/api/history/before", (string path, string id) =>
{
    try
    {
        string rel = Path.GetRelativePath(root, ResolveInRoot(path)).Replace('\\', '/');
        return history.Before(rel, id) is { } text ? Results.Text(text, "text/plain; charset=utf-8") : Results.NotFound();
    }
    catch (UnauthorizedAccessException) { return Results.NotFound(); }
});
// the project as a label had it: {label, dry} lists the files that would change; without dry they are written (a label of its own is put first, so it can be undone)
app.MapPost("/api/history/revert", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    string id = body?["label"]?.GetValue<string>() ?? "";
    bool dry = body?["dry"]?.GetValue<bool>() ?? false;
    var label = history.Labels().FirstOrDefault(l => l.Id == id);
    if (label == null) return Results.NotFound();
    var plan = new List<(string Rel, string Text, bool Missing)>();
    foreach (var (rel, text) in history.StateAt(id))
    {
        if (text == null) continue;                                  // made after the label: it stays
        string full = Path.Combine(root, rel);
        bool exists = File.Exists(full);
        string? now = null;
        if (exists) { try { now = File.ReadAllText(full); } catch (IOException) { continue; } }
        if (now != text) plan.Add((rel, text, !exists));
    }
    if (!dry)
    {
        AutoLabel($"Before going back to '{label.Name}'");
        foreach (var (rel, text, _) in plan)
        {
            string full = ResolveInRoot(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text, new UTF8Encoding(false));
            history.Record(rel, text);
        }
    }
    return Results.Json(new { label = LabelJson(label), files = plan.Select(x => new { path = x.Rel, missing = x.Missing }).ToList(), done = !dry });
});

// the files that change on disk, as server-sent events: "kind|path" (changed, created, deleted; "all|*" when there were too many to tell)
var projectWatcher = new ProjectWatcher(root);
app.Lifetime.ApplicationStopping.Register(projectWatcher.Dispose);
app.MapGet("/api/watch", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var reader = projectWatcher.Subscribe(out var handle);
    try
    {
        await ctx.Response.WriteAsync(": watching\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        await foreach (var rel in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync("data: " + System.Text.Json.JsonSerializer.Serialize(rel) + "\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { /* the page went away */ }
    finally { projectWatcher.Unsubscribe(handle); }
});

// a folder and everything in it, for Explorer's Copy and Paste
static void CopyFolder(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (var dir in Directory.EnumerateDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
}

bool IsInsideGit(string full) =>
    Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => string.Equals(p, ".git", StringComparison.OrdinalIgnoreCase));

// create, rename (or move), copy and delete files and folders of the project
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
                else { Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllTextAsync(full, body["content"]?.GetValue<string>() ?? "", new UTF8Encoding(false)); }
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
            case "copy":
            {
                string from = ResolveInRoot(body!["from"]!.GetValue<string>()), to = ResolveInRoot(body["to"]!.GetValue<string>());
                if (IsInsideGit(from) || IsInsideGit(to)) return Results.BadRequest(new { error = "not inside .git" });
                if (File.Exists(to) || Directory.Exists(to)) return Results.Conflict(new { error = "the target exists already" });
                string sep = Path.DirectorySeparatorChar.ToString();
                if (Directory.Exists(from) && (to + sep).StartsWith(from + sep, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "a folder cannot be copied into itself" });
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (File.Exists(from)) File.Copy(from, to);
                else if (Directory.Exists(from)) CopyFolder(from, to);
                else return Results.NotFound();
                return Results.NoContent();
            }
            case "mark":
            {
                // Mark Directory As: sources, tests, excluded, or none; the page starts again to use it
                string rel = body!["path"]!.GetValue<string>();
                if (!Directory.Exists(ResolveInRoot(rel))) return Results.NotFound();
                var marks = ProjectFolders.Mark(root, rel, body["kind"]?.GetValue<string>() ?? "none");
                return Results.Json(new { sources = marks.Sources, tests = marks.Tests, excluded = marks.Excluded, templates = marks.Templates ?? new(), resources = marks.Resources ?? new() });
            }
            case "clean":
            {
                // Clean Python Compiled Files of a folder, or of the whole project when the path is empty
                string rel = body?["path"]?.GetValue<string>() ?? "";
                string full = rel.Length == 0 ? root : ResolveInRoot(rel);
                if (!Directory.Exists(full)) return Results.NotFound();
                return Results.Json(new { removed = ProjectFiles.CleanCompiled(full) });
            }
            case "gitignore":
            {
                string rel = body!["path"]!.GetValue<string>();
                string full = ResolveInRoot(rel);
                if (IsInsideGit(full)) return Results.BadRequest(new { error = "not inside .git" });
                string file = Path.Combine(root, ".gitignore");
                string text = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
                string line = ProjectFiles.IgnoreLine(rel.Replace('\\', '/'), Directory.Exists(full));
                string? next = ProjectFiles.WithIgnoreLine(text, line);
                if (next is null) return Results.Json(new { line, added = false });
                await File.WriteAllTextAsync(file, next, new UTF8Encoding(false));
                return Results.Json(new { line, added = true });
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
// the RegExp checker: a pattern tried on a text by Python's own re (the rules of JavaScript's are not the same)
// the Endpoints tab's requests: sent by the engine (the page could not read another port's answers), to this computer only;
// the HTTP client's (a request of the user's own .http file, run by its ▶ Run), to any address
var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
app.MapPost("/api/http", async (HttpRequest req) =>
{
    var b = await JsonNode.ParseAsync(req.Body);
    string method = (b?["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant();
    if (!Uri.TryCreate(b?["url"]?.GetValue<string>() ?? "", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        return Results.BadRequest(new { error = "an http:// or https:// address" });
    bool fromFile = b?["fromFile"]?.GetValue<bool>() == true;
    if (!url.IsLoopback && !fromFile) return Results.BadRequest(new { error = "only servers on this computer (localhost, 127.0.0.1)" });
    if (!System.Text.RegularExpressions.Regex.IsMatch(method, "^[A-Z]{3,7}$")) return Results.BadRequest(new { error = "not a method" });
    var message = new HttpRequestMessage(new HttpMethod(method), url);
    string? body = b?["body"]?.GetValue<string>();
    if (!string.IsNullOrEmpty(body)) message.Content = new StringContent(body);
    foreach (var (k, v) in b?["headers"] as JsonObject ?? new JsonObject())
    {
        string value = v?.GetValue<string>() ?? "";
        if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { if (message.Content != null) message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(value); }
        else message.Headers.TryAddWithoutValidation(k, value);
    }
    var watch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var res = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var headers = new JsonObject();
        foreach (var h in res.Headers.Concat(res.Content.Headers)) headers[h.Key] = string.Join(", ", h.Value);
        const int max = 1024 * 1024;
        return Results.Json(new
        {
            status = (int)res.StatusCode, reason = res.ReasonPhrase, headers, ms = watch.ElapsedMilliseconds,
            body = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, max)), truncated = bytes.Length > max,
            size = bytes.Length, url = res.RequestMessage?.RequestUri?.ToString(),   // where it ended, after redirects
        });
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Results.BadRequest(new { error = e is TaskCanceledException ? "no answer in 30 seconds" : e.Message + (url.IsLoopback ? " (is the server running?)" : "") }); }
    catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); }
});

// the live preview of templates: the server running on this computer, relayed under /preview/ so the page can show it in a frame
// (Django forbids frames by default, and its pages link to /static/... of their own server): the frame refusal is dropped and the
// page's own links are pointed back through /preview/
string? previewBase = null;
app.MapPost("/api/preview/base", async (HttpRequest req) =>
{
    var b = await JsonNode.ParseAsync(req.Body);
    if (!Uri.TryCreate(b?["url"]?.GetValue<string>() ?? "", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !url.IsLoopback)
        return Results.BadRequest(new { error = "a server on this computer (http://127.0.0.1:8000)" });
    previewBase = url.GetLeftPart(UriPartial.Authority);
    return Results.NoContent();
});
app.Map("/preview/{**rest}", async (HttpContext ctx, string? rest) =>
{
    if (previewBase == null) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsync("No server address: set it in the preview."); return; }
    var target = new Uri(previewBase + "/" + (rest ?? "") + ctx.Request.QueryString);
    var message = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
    if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        message.Content = new ByteArrayContent(ms.ToArray());
        if (ctx.Request.ContentType is { } ct) message.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
    }
    foreach (var h in ctx.Request.Headers)
        if (h.Key is "Cookie" or "Accept" or "Accept-Language" or "X-CSRFToken" or "Referer")
            message.Headers.TryAddWithoutValidation(h.Key, h.Key == "Referer" ? previewBase + "/" : h.Value.ToString());
    try
    {
        using var res = await httpClient.SendAsync(message);
        ctx.Response.StatusCode = (int)res.StatusCode;
        foreach (var h in res.Headers.Concat(res.Content.Headers))
        {
            if (h.Key is "X-Frame-Options" or "Content-Security-Policy" or "Transfer-Encoding" or "Content-Length" or "Connection") continue;
            ctx.Response.Headers[h.Key] = h.Key == "Location" ? new Microsoft.Extensions.Primitives.StringValues(h.Value.Select(v => v.StartsWith('/') ? "/preview" + v : v.Replace(previewBase, "/preview")).ToArray()) : new Microsoft.Extensions.Primitives.StringValues(h.Value.ToArray());
        }
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (res.Content.Headers.ContentType?.MediaType is "text/html" or "text/css")
        {
            // the page's own absolute links go through the preview too: href="/x", src='/x', action="/x", url(/x)
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"((?:href|src|action)\s*=\s*[""'])/(?!/|preview/)", "$1/preview/");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"url\((['""]?)/(?!/|preview/)", "url($1/preview/");
            bytes = System.Text.Encoding.UTF8.GetBytes(text);
        }
        await ctx.Response.Body.WriteAsync(bytes);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        ctx.Response.StatusCode = 502;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync($"<p style=\"font:14px sans-serif;color:#888\">The server at {System.Net.WebUtility.HtmlEncode(previewBase)} does not answer: is it running?</p>");
    }
});

app.MapPost("/api/regex", async (HttpRequest req) =>
{
    string body = await new StreamReader(req.Body).ReadToEndAsync();
    string py = interpreters.Current is { Target.IsLocal: true } local ? local.Python : python ?? "python";
    const string script =
        "import json, re, sys\n" +
        "d = json.load(sys.stdin)\n" +
        "try:\n" +
        "    r = re.compile(d['pattern'], sum(getattr(re, f) for f in d.get('flags', []) if f in ('IGNORECASE', 'MULTILINE', 'DOTALL', 'VERBOSE', 'ASCII')))\n" +
        "except re.error as e:\n" +
        "    print(json.dumps({'error': str(e), 'pos': e.pos})); sys.exit()\n" +
        "found = []\n" +
        "for m in r.finditer(d['text']):\n" +
        "    found.append({'start': m.start(), 'end': m.end(), 'groups': [[m.start(i), m.end(i)] for i in range(1, r.groups + 1)]})\n" +
        "    if len(found) >= 500: break\n" +
        "print(json.dumps({'matches': found, 'groups': r.groups, 'names': {v: k for k, v in r.groupindex.items()}}))\n";
    var psi = new System.Diagnostics.ProcessStartInfo(py) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add(script);
    psi.Environment["PYTHONIOENCODING"] = "utf-8";
    psi.StandardInputEncoding = new System.Text.UTF8Encoding(false);
    psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
    try
    {
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardInput.WriteAsync(body);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } return Results.Json(new { error = "the pattern took more than 5 seconds on this text" }); }
        string text = await output;
        return Results.Text(string.IsNullOrWhiteSpace(text) ? "{\"error\": \"Python gave no answer\"}" : text, "application/json");
    }
    catch (System.ComponentModel.Win32Exception e) { return Results.Json(new { error = $"cannot start {py}: {e.Message}" }); }
});

// the libraries a search may also look in, and the only folders outside the project whose files the page may read: the interpreter's
// site-packages and standard library, and the folders given with --lib
List<string> SearchLibraryRoots() => libs.Concat(interpreters.Current?.LibraryRoots(includeStdlib: true) ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

app.MapGet("/api/library-file", (string path) =>
{
    try
    {
        string full = Path.GetFullPath(path);
        bool inside = SearchLibraryRoots().Any(r => full.StartsWith(Path.GetFullPath(r).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!inside || Path.GetExtension(full) is not (".py" or ".pyi") || !File.Exists(full)) return Results.NotFound();
        if (new FileInfo(full).Length > 5 * 1024 * 1024) return Results.StatusCode(413);
        return Results.Text(File.ReadAllText(full), "text/plain; charset=utf-8");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Results.NotFound(); }
});

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
        var options = new SearchOptions(Str("query") ?? "", Flag("regex"), Flag("caseSensitive"), Flag("wholeWord"), Str("include"), Str("exclude"), Str("replace"), Libraries: Flag("libraries"));
        return Results.Json(WorkspaceSearch.Run(root, options, unsaved, SearchLibraryRoots()), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
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

    var service = new LanguageService { TypeshedPath = typeshed, PackageIndexLookup = LanguageService.AskPyPI };   // an Install package fix is offered for a guessed name only when PyPI has it
    service.AddSourceRoot(root);
    // the folders marked as sources roots (Mark Directory As), read again for each page
    ProjectFolders.Load(root);
    foreach (var src in ProjectFolders.SourceRoots) if (Directory.Exists(src)) service.AddSourceRoot(src);
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
            var incoming = JsonNode.Parse(ms.ToArray());
            server.NoteCancel(incoming);   // a cancel counts at once, before the requests queued ahead of it are reached
            inbox.Add(incoming);
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

// New Project: a folder anywhere the person chose, with the starting files of its kind, Git and its environment
app.MapPost("/api/newproject", async (HttpRequest req) =>
{
    var b = await JsonNode.ParseAsync(req.Body);
    string? Str(string k) => b?[k]?.GetValue<string>();
    var request = new NewProjectRequest(Str("location") ?? "", Str("kind") ?? "python", b?["git"]?.GetValue<bool>() == true, b?["welcome"]?.GetValue<bool>() == true,
        Str("environment") ?? "none", Str("python"), Str("uvPath"), Str("pythonVersion"),
        b?["django"] is JsonObject dj ? new DjangoProjectOptions(dj["templateLanguage"]?.GetValue<string>() ?? "django", dj["templatesFolder"]?.GetValue<string>() ?? "templates",
            dj["appName"]?.GetValue<string>(), dj["admin"]?.GetValue<bool>() != false) : null);
    try { return Results.Json(await Task.Run(() => NewProject.Create(request))); }
    catch (ServiceException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.BadRequest(new { error = e.Message }); }
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
// SchemaStore's schemas (package.json, pyproject.toml, GitHub workflows): kept ones, one kept one, and the download (the page asks the user first)
app.MapGet("/api/schemas", () => Results.Json(Schemas.List(root)));
app.MapGet("/api/schemas/get", (string name) => Schemas.Kept(root, name) is { } kept ? Results.Text(kept, "application/json") : Results.NotFound());
app.MapPost("/api/schemas/download", async (string name) =>
{
    try { return Results.Text(await Schemas.Download(root, name, pypi), "application/json"); }
    catch (ServiceException e) { return Results.Json(new { error = e.Message }, statusCode: 502); }
});
// a TOML text as JSON, for checking pyproject.toml against its schema
app.MapPost("/api/toml", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    string? json = Schemas.TomlToJson(DatabasePython(), await reader.ReadToEndAsync());
    return json == null ? Results.Json(new { error = "not TOML, or a Python older than 3.11" }) : Results.Text(json, "application/json");
});
app.MapGet("/api/tests", () => TestDiscovery.Discover(root));
DjangoConfig.Load(root);
ProjectFolders.Load(root);
ProjectFolders.AutoTemplates = DjangoProject.Scan(root).TemplateDirs?.ToList() ?? new();
app.MapGet("/api/django", () => DjangoProject.Scan(root));
// the Django page of the settings (.pymagic/django.json)
app.MapGet("/api/django/config", () => Results.Json(DjangoConfig.Current, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
app.MapPut("/api/django/config", async (HttpRequest req) =>
{
    try
    {
        var config = await System.Text.Json.JsonSerializer.DeserializeAsync<DjangoConfig>(req.Body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var saved = DjangoConfig.Save(root, config ?? new DjangoConfig());
        ProjectFolders.AutoTemplates = DjangoProject.Scan(root).TemplateDirs?.ToList() ?? new();
        return Results.Json(saved, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    }
    catch (System.Text.Json.JsonException e) { return Results.BadRequest(new { error = e.Message }); }
});
// the manage.py console's completion and documentation: the project's own `manage.py help --commands`, or `manage.py help <command>`
var djangoHelp = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
app.MapGet("/api/django/help", async (string? command) =>
{
    var project = DjangoProject.Scan(root);
    if (project.Manage == null) return Results.NotFound(new { error = "no manage.py" });
    if (command != null && !System.Text.RegularExpressions.Regex.IsMatch(command, @"^[A-Za-z_][\w-]{0,60}$")) return Results.BadRequest(new { error = "not a command name" });
    string key = (interpreters.Current?.Path ?? "") + "|" + System.Text.Json.JsonSerializer.Serialize(DjangoConfig.Current) + "|" + (command ?? "--commands");   // another configuration asks again
    if (djangoHelp.TryGetValue(key, out var cached)) return Results.Text(cached);
    string manage = Path.Combine(root, project.Manage);
    var psi = new System.Diagnostics.ProcessStartInfo(interpreters.Current?.Python ?? python) { WorkingDirectory = Path.GetDirectoryName(manage)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    psi.ArgumentList.Add(manage);
    psi.ArgumentList.Add("help");
    psi.ArgumentList.Add(command ?? "--commands");
    psi.Environment["PYTHONIOENCODING"] = "utf-8";
    try
    {
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => p.WaitForExit(30000))) { try { p.Kill(true); } catch (InvalidOperationException) { } return Results.BadRequest(new { error = "manage.py help took too long" }); }
        if (p.ExitCode != 0) return Results.BadRequest(new { error = (await stderr).Trim().Split('\n').LastOrDefault() ?? "manage.py help failed" });
        string text = await stdout;
        djangoHelp[key] = text;
        return Results.Text(text);
    }
    catch (System.ComponentModel.Win32Exception e) { return Results.BadRequest(new { error = e.Message }); }
});

// the kept coverage runs (suites), the newest first; one of them; forgetting one
app.MapGet("/api/coverage/suites", () => Results.Json(CoverageSuites.List(root).Select(s => new { name = s.Name, label = s.Label, when = s.When.ToUniversalTime().ToString("o"), percent = s.Percent })));
app.MapGet("/api/coverage/suite", (string name) => CoverageSuites.Read(root, name) is { } data ? Results.Json(data) : Results.NotFound());
app.MapDelete("/api/coverage/suite", (string name) => CoverageSuites.Delete(root, name) ? Results.Ok() : Results.NotFound());
// the HTML report the page wrote (Export report), opened in the browser
app.MapGet("/api/coverage/report", () =>
{
    string path = Path.Combine(root, "htmlcov", "index.html");
    return File.Exists(path) ? Results.File(path, "text/html; charset=utf-8") : Results.NotFound();
});

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

// ---- the Database panel: the project's SQLite files, read and changed through the project's Python (sqlite3)

string DatabasePython() => interpreters.Current is { Target.IsLocal: true } dbLocal ? dbLocal.Python : python ?? "python";

app.MapGet("/api/db/sources", () => Results.Json(Databases.Find(root)));

// a data source added by hand (a new file is made); {path} inside the project
app.MapPost("/api/db/sources", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try
    {
        string full = ResolveInRoot(body?["path"]?.GetValue<string>() ?? "");
        Databases.AddSource(root, full, DatabasePython());
        return Results.Json(Databases.Find(root));
    }
    catch (ServiceException e) { return Results.BadRequest(e.Message); }
    catch (UnauthorizedAccessException) { return Results.BadRequest("The database has to be inside the project."); }
});

// a PostgreSQL or MySQL server: {kind, host, port, user, password, database, name}; it is connected to before it is kept
app.MapPost("/api/db/servers", async (HttpRequest req) =>
{
    if (await JsonNode.ParseAsync(req.Body) is not JsonObject body) return Results.BadRequest("unknown request");
    try
    {
        var server = DbServers.Parse(body);
        string password = body["password"]?.GetValue<string>() ?? "";
        var answer = Databases.Ask(DatabasePython(), new JsonObject { ["op"] = "test", ["server"] = DbServers.Describe(server, password) }, 30000, "pm_dbserver.py");
        if (answer["error"] is { } error)
            return Results.Json(new { error = error.GetValue<string>(), missingDriver = answer["missingDriver"]?.GetValue<bool>() ?? false });
        DbServers.Save(root, server, password);
        return Results.Json(new { path = DbServers.PathOf(server.Id), version = answer["version"]?.GetValue<string>() });
    }
    catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
});
app.MapDelete("/api/db/servers", (string id) => { DbServers.Remove(root, id); return Results.Json(Databases.Find(root)); });

app.MapDelete("/api/db/sources", (string path) => { Databases.RemoveSource(root, path); return Results.Json(Databases.Find(root)); });

// one request on a data source: {op: schema | rows | apply | run, path (from the project root), ...}; the answer has "error" when SQLite refused
app.MapPost("/api/db", async (HttpRequest req) =>
{
    if (await JsonNode.ParseAsync(req.Body) is not JsonObject body || body["op"]?.GetValue<string>() is not ("schema" or "rows" or "apply" or "run" or "import"))
        return Results.BadRequest("unknown request");
    try
    {
        // a CSV file to import: a file of the project too
        if (body["csv"]?.GetValue<string>() is { } csv)
        {
            string csvFull = ResolveInRoot(csv);
            if (!File.Exists(csvFull)) return Results.Json(new { error = $"There is no file {csv} in the project." });
            body["csv"] = csvFull;
        }
        // a database server of the project: its settings and password are added here, never sent by the page
        if (body["path"]?.GetValue<string>() is { } serverPath && DbServers.IsServerPath(serverPath))
        {
            if (DbServers.Connection(root, serverPath) is not { } connection) return Results.BadRequest("No such database server in the project.");
            body.Remove("path");
            body["server"] = connection;
            return Results.Json(Databases.Ask(DatabasePython(), body, 60000, "pm_dbserver.py"));
        }
        string full = ResolveInRoot(body["path"]?.GetValue<string>() ?? "");
        if (!File.Exists(full) || !Databases.IsSqlite(full)) return Results.BadRequest("Not an SQLite database of the project.");
        body["path"] = full;
        return Results.Json(Databases.Ask(DatabasePython(), body));
    }
    catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
    catch (UnauthorizedAccessException) { return Results.BadRequest("The database has to be inside the project."); }
});

// the profiler runs kept, newest first
app.MapGet("/api/profile/snapshots", () => Results.Json(ProfileSnapshots.List(root)));

// a .pstats file of the project (cProfile's, yappi's, PyCharm's) read into the Profiler panel: {path}
app.MapPost("/api/profile/open", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body);
    try
    {
        string full = ResolveInRoot(body?["path"]?.GetValue<string>() ?? "");
        if (!File.Exists(full)) return Results.BadRequest(new { error = $"There is no file {body?["path"]} in the project." });
        return Results.Json(new { snapshot = ProfileSnapshots.Open(root, DatabasePython(), full) });
    }
    catch (ServiceException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (UnauthorizedAccessException) { return Results.BadRequest(new { error = "The file has to be inside the project." }); }
});

// what the last profiler run measured, from .pymagic/profile.json; "rel" is the path inside the project (null for other code)
// ?snapshot=<name>: a run kept in .pymagic/profiles instead of the last one
app.MapGet("/api/profile", (string? snapshot) =>
{
    string? path = snapshot is { Length: > 0 } ? ProfileSnapshots.PathOf(root, snapshot) : RunSession.ProfilePath(root);
    if (path == null || !File.Exists(path)) return Results.NotFound();
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
            foreach (var c in f["callees"]?.AsArray() ?? new JsonArray()) c!["rel"] = Rel(c["file"]?.GetValue<string>());
        }
        data["snapshot"] = snapshot;
        data["rel"] = Rel(data["script"]?.GetValue<string>());
        data["when"] = File.GetLastWriteTimeUtc(path).ToString("o");
        return Results.Json(data);
    }
    catch (Exception e) when (e is IOException or System.Text.Json.JsonException or NullReferenceException) { return Results.NotFound(); }
});

// ---- the terminal: one command line at a time in the project folder -------------------------------------------------

// the shells the Terminal panel can start on this PC, and the one "auto" stands for
app.MapGet("/api/shells", () =>
{
    var found = ConsoleShells.Found();
    var auto = ConsoleShells.Choose(ConsoleShells.Auto, found);
    return Results.Json(new { auto = auto.Name, shells = found.Select(s => new { id = s.Id, name = s.Name }) });
});

// a real console for each terminal tab: the page sends {type:"input", data} and {type:"resize", cols, rows}, and gets {type:"output", data},
// {type:"exit", code} or {type:"error", text}; the size of the first screen comes as ?cols=&rows=
app.Map("/console", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var outbox = new BlockingCollection<string>();
    void Send(JsonObject o) { if (!outbox.IsAddingCompleted) try { outbox.Add(o.ToJsonString()); } catch (InvalidOperationException) { } }
    var sender = Task.Run(async () =>
    {
        foreach (var text in outbox.GetConsumingEnumerable())
        {
            if (ws.State != WebSocketState.Open) break;
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (WebSocketException) { break; }
        }
    });
    if (!OperatingSystem.IsWindows())
    {
        Send(new JsonObject { ["type"] = "error", ["text"] = "The console needs Windows." });
        outbox.CompleteAdding();
        await sender;
        return;
    }
    int Query(string name, int fallback) => int.TryParse(ctx.Request.Query[name], out var n) ? n : fallback;
    using var console = new ConsoleSession();
    var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value;
    // the host's own settings are not the user's (the garbage collector's, the server's URLs)
    foreach (var k in env.Keys.Where(k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToList()) env.Remove(k);
    TerminalSession.WithInterpreter(env, interpreters.Current is { Target.IsLocal: true } c ? c.Path : null);
    // the page names a shell by its id (?shell=); the command line is the one found here
    var shell = ConsoleShells.Choose(ctx.Request.Query["shell"], ConsoleShells.Found());
    // an active virtual environment shows in the prompt, as its activation script would make it: `(.venv) PS C:\proj>`
    var (commandLine, shellEnv) = shell.WithVenv(ConsoleShells.VenvPrompt(env.TryGetValue("VIRTUAL_ENV", out var venv) ? venv : null));
    foreach (var (k, v) in shellEnv) env[k] = v;
    Send(new JsonObject { ["type"] = "shell", ["id"] = shell.Id, ["name"] = shell.Name });
    try
    {
        // Open In > Terminal starts it in a folder of the project (?cwd=)
        string cwd = root;
        if (ctx.Request.Query["cwd"].ToString() is { Length: > 0 } wanted)
            try { if (Directory.Exists(ResolveInRoot(wanted))) cwd = ResolveInRoot(wanted); } catch (UnauthorizedAccessException) { }
        console.Start(commandLine, cwd, env, Query("cols", 100), Query("rows", 30),
            data => Send(new JsonObject { ["type"] = "output", ["data"] = data }),
            code => { Send(new JsonObject { ["type"] = "exit", ["code"] = code }); outbox.CompleteAdding(); });
    }
    catch (System.ComponentModel.Win32Exception e)
    {
        Send(new JsonObject { ["type"] = "error", ["text"] = e.Message });
        outbox.CompleteAdding();
        await sender;
        return;
    }

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
                case "input": console.Write(msg["data"]?.GetValue<string>() ?? ""); break;
                case "resize": console.Resize(msg["cols"]?.GetValue<int>() ?? 100, msg["rows"]?.GetValue<int>() ?? 30); break;
            }
        }
    }
    catch (Exception e) when (e is WebSocketException or System.Text.Json.JsonException) { }
    done:
    outbox.CompleteAdding();
    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (Exception e) when (e is WebSocketException or InvalidOperationException) { }
    await sender;
});

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
                string? module = msg["module"]?.GetValue<string>();
                string file0 = msg["file"]?.GetValue<string>() ?? "";
                var debugTests = msg["tests"] as JsonObject;   // Debug of tests: the test runner under the debugger
                var profileTests = msg["profileTests"] as JsonObject;   // Profile of tests: pytest or unittest under the profiler
                if (module == null && debugTests == null && profileTests == null) ResolveInRoot(file0);   // refuses paths outside the workspace
                if (Scratches.IsScratchPath(file0)) file0 = Scratches.PathOf(file0);                         // a scratch runs where it is kept, in the project's folder
                session.Python = interpreters.Current?.Python ?? python; session.Target = interpreters.Current?.Target ?? ExecutionTarget.Local;
                // a run configuration may name an interpreter of its own (a path, or a spec of WSL, Docker or SSH) instead of the project's
                if (msg["interpreter"]?.GetValue<string>()?.Trim() is { Length: > 0 } own0)
                {
                    var spec = ExecutionTarget.Parse(own0);
                    string py = spec?.Python ?? own0;
                    if (spec == null && Path.IsPathRooted(py) && !File.Exists(py)) { session.Reject($"The interpreter of this configuration is not there: {own0}"); continue; }
                    session.Python = py;
                    session.Target = spec?.Target ?? ExecutionTarget.Local;
                }
                // what a run configuration adds: variables, and a folder inside the project to start in
                // .env files of the project first, the configuration's own variables over them
                var variables = new JsonObject();
                foreach (var f in (msg["envFiles"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>() ?? Enumerable.Empty<string>())
                {
                    string envPath = ResolveInRoot(f);
                    if (File.Exists(envPath))
                        foreach (var (k, v) in RunConfigurations.ParseDotEnv(File.ReadAllText(envPath))) variables[k] = v;
                }
                if (file0.EndsWith("manage.py", StringComparison.OrdinalIgnoreCase))
                    foreach (var (k, v) in DjangoConfig.Current.Env ?? new()) variables[k] = v;
                if (msg["env"] is JsonObject own) foreach (var (k, v) in own) variables[k] = v?.DeepClone();
                session.ExtraEnvironment = RunConfigurations.CleanEnvironment(variables);
                session.WorkingDirectory = msg["cwd"]?.GetValue<string>() is { Length: > 0 } cwd && Directory.Exists(ResolveInRoot(cwd)) ? ResolveInRoot(cwd) : null;
                // PYTHONPATH: the project folder (content root) and the marked sources roots, each when the configuration keeps it
                bool content = msg["contentRoots"]?.GetValue<bool>() ?? true, sources = msg["sourceRoots"]?.GetValue<bool>() ?? true;
                session.PythonPath = content && sources ? null
                    : (content ? new[] { root } : Array.Empty<string>()).Concat(sources ? ProjectFolders.Load(root).Sources.Select(s => Path.Combine(root, s)) : Array.Empty<string>()).ToList();
                session.AllCode = msg["allCode"]?.GetValue<bool>() == true;
                session.UvProgram = msg["uv"]?.GetValue<bool>() == true ? (msg["uvPath"]?.GetValue<string>() is { Length: > 0 } uvPath ? uvPath : "uv") : null;
                if (debugTests != null)
                    session.StartDebugTests(root, (debugTests["ids"] as JsonArray)?.Select(i => i!.GetValue<string>()).ToList() ?? new List<string>(),
                        debugTests["framework"]?.GetValue<string>() ?? "auto", bps);   // pm_tests checks the name
                else if (profileTests != null)
                    session.StartProfile(root, null, (profileTests["ids"] as JsonArray)?.Select(i => i!.GetValue<string>()).ToList() ?? new List<string>(),
                        tests: profileTests["framework"]?.GetValue<string>() is "pytest" or "unittest" ? profileTests["framework"]!.GetValue<string>() : "auto");
                else if (msg["profile"]?.GetValue<bool>() == true) session.StartProfile(root, module == null ? file0 : null, args, module);
                else if (module != null) session.StartModule(root, module, msg["debug"]?.GetValue<bool>() ?? false, args, bps);
                else session.StartWithBreakpoints(root, file0, msg["debug"]?.GetValue<bool>() ?? false, args, bps);
                if (msg["exceptionBreak"]?.GetValue<string>() == "raised")
                    session.Handle(new JsonObject { ["type"] = "setExceptionBreak", ["mode"] = "raised", ["classes"] = msg["exceptionClasses"]?.DeepClone() });
                if (msg["muted"]?.GetValue<bool>() == true)
                    session.Handle(new JsonObject { ["type"] = "mute", ["on"] = true });
                if (msg["loadPolicy"]?.GetValue<string>() is "onDemand")
                    session.Handle(new JsonObject { ["type"] = "setLoadPolicy", ["policy"] = "onDemand" });
                if (msg["functionBreaks"] is JsonArray { Count: > 0 } functions)
                    session.Handle(new JsonObject { ["type"] = "setFunctionBreaks", ["names"] = functions.DeepClone() });
            }
            else if (msg["type"]?.GetValue<string>() == "console")
            {
                // the Python console runs with the project's interpreter, in the project folder
                session.Python = interpreters.Current?.Python ?? python; session.Target = interpreters.Current?.Target ?? ExecutionTarget.Local;
                string? startup = msg["startup"]?.GetValue<string>() is { Length: > 0 } st ? st : null;
                if (startup != null) ResolveInRoot(startup);
                session.StartConsole(root, startup, msg["django"]?.GetValue<bool>() == true);
            }
            else if (msg["type"]?.GetValue<string>() == "attach" && msg["pid"]?.GetValue<int>() is int attachPid)
            {
                var bps = new Dictionary<string, JsonArray>();
                if (msg["breakpoints"] is JsonObject bo)
                    foreach (var (file, lines) in bo)
                        bps[file] = lines is JsonArray arr ? (JsonArray)JsonNode.Parse(arr.ToJsonString())! : new JsonArray();
                // the program runs on this computer: the interpreter that sends the debugger is the selected one, run here
                session.Python = interpreters.Current?.Target.IsLocal != false ? interpreters.Current?.Python ?? python : python;
                session.Attach(root, attachPid, bps);
            }
            else if (msg["type"]?.GetValue<string>() == "startTests")
            {
                var ids = (msg["ids"] as JsonArray)?.Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
                session.Python = interpreters.Current?.Python ?? python; session.Target = interpreters.Current?.Target ?? ExecutionTarget.Local;
                session.StartTests(root, ids, msg["framework"]?.GetValue<string>() ?? "auto", msg["coverage"]?.GetValue<bool>() == true,
                    msg["settings"]?.GetValue<string>() is { Length: > 0 } djangoSettings ? djangoSettings : DjangoConfig.Current.Settings, msg["failfast"]?.GetValue<bool>() == true,
                    djangoRunner: DjangoConfig.Current.TestRunner);
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
    AutoLabel($"Before Git checkout of {body?["branch"]?.GetValue<string>()}");
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
    string? labelled = op switch
    {
        "pull" => "Git pull", "merge" => $"Git merge of {Str("branch")}", "applyStash" => "Git: stash applied", "revert" => "Git revert", "rebaseInteractive" => "Git rebase",
        "cherryPick" => "Git cherry-pick", "reset" => "Git reset", "checkoutRemote" => $"Git checkout of {Str("branch")}", _ => null,
    };
    if (labelled != null) AutoLabel("Before " + labelled);
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
