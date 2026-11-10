using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// PHP's code insight from Phpactor (its language server, `phpactor language-server`), with the PHP of Settings > PHP: on this computer the
/// phpactor.phar PyMagic carries, started through pm_phpactor.php (Windows' PHP has no posix extension, which Phpactor's own start asks for) and
/// spoken to over a local TCP connection (PHP on Windows cannot wait on a pipe, so over stdin Phpactor would answer only when the next message came),
/// its index and settings in PyMagic's own folder; on a machine reached by SSH the Phpactor installed there, over the connection's stdin and stdout,
/// the project (vendor included) copied there first and again a moment after files change, the files named as the project's here. The page's
/// .php files go to it as they are typed, and its completion, hover, signature help, go to definition, references and problems come back.
/// One per editor page; started on the first PHP file opened, ended with the page.
/// </summary>
public sealed class PhpLanguageServer : IDisposable
{
    readonly SshTarget? _ssh;
    readonly string _php, _root;
    readonly Action<JsonNode> _toPage;
    readonly Action<string> _log;
    Process? _process;
    ProcessJob? _job;
    System.Net.Sockets.TcpClient? _socket;
    MessageChannel? _channel;
    string? _runId;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _waiting = new();
    int _nextId;
    readonly BlockingCollection<JsonNode> _outgoing = new();
    readonly Task _started;
    volatile string? _failed;
    bool _disposed;
    Timer? _copyAgain;

    /// <summary>Why Phpactor is not running (not installed there, the machine cannot be reached), or null while it is or is starting.</summary>
    public string? Failed => _failed;

    /// <summary>Phpactor on the machine of <paramref name="ssh"/>, with its PHP <paramref name="php"/>.</summary>
    public PhpLanguageServer(SshTarget ssh, string php, string root, Action<JsonNode> toPage, Action<string>? log = null) : this(ssh, php, root, toPage, log, 0) { }

    /// <summary>Phpactor on this computer, run by the php.exe <paramref name="php"/>.</summary>
    public PhpLanguageServer(string php, string root, Action<JsonNode> toPage, Action<string>? log = null) : this(null, php, root, toPage, log, 0) { }

    PhpLanguageServer(SshTarget? ssh, string php, string root, Action<JsonNode> toPage, Action<string>? log, int _)
    {
        _ssh = ssh; _php = php; _root = Path.GetFullPath(root); _toPage = toPage; _log = log ?? (_ => { });
        if (_ssh != null) _ssh.Project = _root;
        _started = Task.Run(Start);
        new Thread(Write) { IsBackground = true, Name = "phpactor out" }.Start();
    }

    /// <summary>Whether a document is one Phpactor reads: a .php file of the project (not a Blade template).</summary>
    public static bool Reads(string? uri) => uri != null && uri.EndsWith(".php", StringComparison.OrdinalIgnoreCase) && !uri.EndsWith(".blade.php", StringComparison.OrdinalIgnoreCase);

    /// <summary>The phpactor.phar PyMagic carries: PYMAGIC_PHPACTOR, or phpactor/phpactor.phar in a folder above the engine (the repo, the app's resources).</summary>
    public static string? Phar()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_PHPACTOR") is { Length: > 0 } env) return File.Exists(env) ? env : null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "phpactor", "phpactor.phar");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Where Phpactor keeps its index and settings on this computer: PYMAGIC_PHPACTOR_HOME, else PyMagic's own folder of the user's local data.</summary>
    public static string Home() => Environment.GetEnvironmentVariable("PYMAGIC_PHPACTOR_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "PyMagic", "phpactor");

    void Start()
    {
        try
        {
            if (_ssh != null) StartThere(_ssh); else StartHere();
            new Thread(Read) { IsBackground = true, Name = "phpactor in" }.Start();

            string rootUri = RemoteUri(_root);
            var options = new JsonObject { ["language_server_phpstan.enabled"] = false, ["language_server_psalm.enabled"] = false, ["indexer.poll_time"] = 2000 };
            if (_ssh == null)
            {
                // on Windows: no inotify or find for the index to watch with (PHP's own polling instead), and no process of the phar for problems
                options["indexer.enabled_watchers"] = new JsonArray("php");
                options["language_server.diagnostic_outsource"] = false;
            }
            var init = Ask("initialize", new JsonObject
            {
                ["processId"] = null,
                ["rootUri"] = rootUri,
                ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(_root) }),
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["completion"] = new JsonObject { ["completionItem"] = new JsonObject { ["snippetSupport"] = true, ["documentationFormat"] = new JsonArray("markdown", "plaintext") } },
                        ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                        ["signatureHelp"] = new JsonObject { ["signatureInformation"] = new JsonObject { ["documentationFormat"] = new JsonArray("markdown", "plaintext") } },
                        ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false },
                        ["synchronization"] = new JsonObject { ["didSave"] = true },
                    },
                    ["workspace"] = new JsonObject { ["workspaceFolders"] = true, ["configuration"] = false },
                },
                ["initializationOptions"] = options,
            }, raw: true);
            if (!init.Wait(TimeSpan.FromSeconds(120))) throw new ServiceException("Phpactor did not answer");
            _channel!.Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() });
        }
        catch (Exception e) when (e is ServiceException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or System.Net.Sockets.SocketException)
        {
            _failed = e is AggregateException a ? a.InnerException?.Message ?? a.Message : e.Message;
            _log("phpactor: " + _failed);
            foreach (var w in _waiting.Values) w.TrySetResult(null);
        }
    }

    void StartThere(SshTarget ssh)
    {
        var (_, found, _) = ssh.Remote("command -v phpactor || ls /usr/local/bin/phpactor ~/.composer/vendor/bin/phpactor ~/.config/composer/vendor/bin/phpactor 2>/dev/null | head -1", 30_000);
        string phpactor = found.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith('/')) ?? "";
        if (phpactor.Length == 0)
            throw new ServiceException($"Phpactor is not on {ssh.Destination}: install it there (phpactor.readthedocs.io: its phpactor.phar in /usr/local/bin/phpactor) for PHP's code insight.");
        ssh.Prepare(_root, null, null);
        var psi = ssh.StartProgram(_php, _root, id => new[] { "-d", "pymagic=" + id, "-d", "memory_limit=1G" }, new[] { phpactor, "language-server" });
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        _runId = psi.Environment["PYMAGIC_RUN_ID"];
        _process = Process.Start(psi) ?? throw new ServiceException("ssh did not start");
        _channel = new MessageChannel(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        var err = _process.StandardError;
        Task.Run(() => { string? line; while ((line = err.ReadLine()) != null) _log("phpactor: " + line); });
    }

    void StartHere()
    {
        string phar = Phar() ?? throw new ServiceException("PyMagic's Phpactor (phpactor/phpactor.phar) is not there: PHP's code insight needs it.");
        string launcher = RunSession.ExtractHelper("pm_phpactor.php");
        int port;
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start(); port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port; free.Stop();
        var psi = new ProcessStartInfo(_php) { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-d", "memory_limit=1G", launcher, "language-server", $"--address=127.0.0.1:{port}" }) psi.ArgumentList.Add(a);
        psi.Environment["PYMAGIC_PHPACTOR_PHAR"] = phar;
        // Phpactor's index, cache and settings (XDG's folders) in PyMagic's own folder, not in the user's home
        string home = Home();
        foreach (var (variable, folder) in new[] { ("XDG_CACHE_HOME", "cache"), ("XDG_CONFIG_HOME", "config"), ("XDG_DATA_HOME", "data") })
        {
            Directory.CreateDirectory(Path.Combine(home, folder));
            psi.Environment[variable] = Path.Combine(home, folder);
        }
        // `php -l` of its problems, and its index workers, with the same PHP
        psi.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(_php)) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var path) ? path : "");
        _process = Process.Start(psi) ?? throw new ServiceException("PHP did not start");
        _job = ProcessJob.For(_process);       // its index workers end with it
        _process.StandardInput.Close();
        var output = _process.StandardOutput;
        var err = _process.StandardError;
        Task.Run(() => { string? line; while ((line = output.ReadLine()) != null) _log("phpactor: " + line); });
        Task.Run(() => { string? line; while ((line = err.ReadLine()) != null) _log("phpactor: " + line); });
        for (var until = DateTime.UtcNow.AddSeconds(60); _socket == null; Thread.Sleep(200))
        {
            if (_process.HasExited) throw new ServiceException($"Phpactor ended at its start (exit code {_process.ExitCode})");
            if (DateTime.UtcNow > until) throw new ServiceException("Phpactor did not listen");
            var client = new System.Net.Sockets.TcpClient();
            try { client.Connect(System.Net.IPAddress.Loopback, port); client.NoDelay = true; _socket = client; }
            catch (System.Net.Sockets.SocketException) { client.Dispose(); }
        }
        var stream = _socket.GetStream();
        _channel = new MessageChannel(stream, stream);
    }

    // ------------------------------------------------------------ the page's messages

    /// <summary>A notification of the page (didOpen, didChange, didClose, didSave): to Phpactor, the files named as they are there.</summary>
    public void Notify(string method, JsonNode? p)
    {
        if (_disposed) return;
        // the open files' text (full sync), for what is added to Phpactor's answers (Eloquent's columns)
        if (p?["textDocument"]?["uri"]?.GetValue<string>() is { } docUri)
        {
            if (method == "textDocument/didOpen" && p["textDocument"]?["text"]?.GetValue<string>() is { } opened) _texts[docUri] = opened;
            else if (method == "textDocument/didChange" && (p["contentChanges"] as JsonArray)?.LastOrDefault()?["text"]?.GetValue<string>() is { } changed) _texts[docUri] = changed;
            else if (method == "textDocument/didClose") _texts.TryRemove(docUri, out _);
        }
        _outgoing.Add(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = Map(p?.DeepClone(), RemoteUri) });
        // a file saved here is copied there a moment later: Phpactor's index reads the files there
        if (_ssh != null && method is "textDocument/didSave" or "textDocument/didClose" or "textDocument/didChange")
        {
            _copyAgain?.Dispose();
            _copyAgain = new Timer(_ => { try { _ssh!.Prepare(_root, null, null); } catch (ServiceException e) { _log("phpactor copy: " + e.Message); } }, null, 3000, Timeout.Infinite);
        }
    }

    /// <summary>A request of the page, answered in the shape the page reads (as PyMagic's own language server answers).</summary>
    public async Task<JsonNode?> Request(string method, JsonNode? p, CancellationToken cancel = default)
    {
        if (_disposed) return null;
        var answer = await Ask(method, Map(p?.DeepClone(), RemoteUri)).WaitAsync(TimeSpan.FromSeconds(method == "textDocument/references" ? 120 : 30), cancel).ConfigureAwait(false);
        var shaped = Shape(method, Map(answer, HostUri), p);
        if (method == "textDocument/completion" && shaped?["items"] is JsonArray items)
            try { await AddModelMembers(p, items, cancel).ConfigureAwait(false); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { }
        return shaped;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _texts = new();

    /// <summary>
    /// After <c>$model-></c>: the columns and relations of the project's Eloquent model the receiver is (Phpactor's hover of the receiver names
    /// its class), added to Phpactor's items.
    /// </summary>
    async Task AddModelMembers(JsonNode? p, JsonArray items, CancellationToken cancel)
    {
        if (p?["textDocument"]?["uri"]?.GetValue<string>() is not { } uri || !_texts.TryGetValue(uri, out var text)) return;
        int lineNo = p["position"]?["line"]?.GetValue<int>() ?? -1, character = p["position"]?["character"]?.GetValue<int>() ?? -1;
        var lines = text.Split('\n');
        if (lineNo < 0 || lineNo >= lines.Length) return;
        string before = lines[lineNo].TrimEnd('\r');
        before = before[..Math.Min(character, before.Length)];
        var arrow = System.Text.RegularExpressions.Regex.Match(before, @"(\S)\s*->\s*\w*$");
        if (!arrow.Success) return;
        // the receiver's last character: a call's (…) is the method's name before it
        int at = arrow.Groups[1].Index;
        if (before[at] == ')')
        {
            int depth = 0;
            for (int i = at; i >= 0; i--)
            {
                if (before[i] == ')') depth++;
                else if (before[i] == '(' && --depth == 0) { at = i - 1; break; }
            }
        }
        if (at < 0 || !(char.IsLetterOrDigit(before[at]) || before[at] == '_')) return;
        var hoverAsk = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = lineNo, ["character"] = at } };
        var hover = await Ask("textDocument/hover", Map(hoverAsk, RemoteUri)).WaitAsync(TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
        foreach (var cls in EloquentModels.ClassesIn(Text(hover?["contents"]), text))
        {
            if (EloquentModels.Items(_root, cls) is not { } added) continue;
            // a relation is Phpactor's method posts() and also the property posts: a name and kind Phpactor has already is left out
            string Key(JsonNode? i) => (i?["label"]?.GetValue<string>() ?? "") + "\0" + (i?["kind"]?.ToJsonString() ?? "");
            var have = new HashSet<string>(items.Select(Key), StringComparer.Ordinal);
            foreach (var item in added) if (!have.Contains(Key(item))) items.Add(item!.DeepClone());
            return;
        }
    }

    Task<JsonNode?> Ask(string method, JsonNode? p, bool raw = false)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_failed != null) { tcs.SetResult(null); return tcs.Task; }
        _waiting[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p };
        if (raw) _channel!.Write(msg); else _outgoing.Add(msg);
        return tcs.Task;
    }

    void Write()
    {
        try
        {
            foreach (var msg in _outgoing.GetConsumingEnumerable())
            {
                _started.Wait();
                if (_failed != null || _channel == null)
                {
                    if (msg["id"] is JsonValue v && _waiting.TryRemove(v.GetValue<int>(), out var w)) w.TrySetResult(null);
                    continue;
                }
                _channel.Write(msg);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    void Read()
    {
        try
        {
            JsonNode? msg;
            while ((msg = _channel!.Read()) != null)
            {
                if (msg["method"]?.GetValue<string>() is { } method)
                {
                    // Phpactor's own requests (workspace/configuration, window/workDoneProgress/create): answered with nothing
                    if (msg["id"] is { } ask) { _channel.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ask.DeepClone(), ["result"] = null }); continue; }
                    if (method == "textDocument/publishDiagnostics")
                    {
                        var p = Map(msg["params"]?.DeepClone(), HostUri) as JsonObject;
                        if (p?["uri"]?.GetValue<string>() is { } uri && uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) && !uri.Contains("/vendor/", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var d in p["diagnostics"] as JsonArray ?? new JsonArray()) if (d is JsonObject o) o["source"] ??= "phpactor";
                            _toPage(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });
                        }
                    }
                    else if (method == "window/logMessage" || method == "window/showMessage") _log("phpactor: " + msg["params"]?["message"]);
                    continue;
                }
                if (msg["id"] is JsonValue idv && idv.TryGetValue<int>(out int id) && _waiting.TryRemove(id, out var tcs))
                {
                    if (msg["error"] is { } error) _log($"phpactor error: {error["message"]}");
                    tcs.TrySetResult(msg["result"]?.DeepClone());
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or System.Text.Json.JsonException or InvalidOperationException) { }
        if (!_disposed) _failed ??= "Phpactor stopped";
        foreach (var w in _waiting.Values) w.TrySetResult(null);
    }

    // ------------------------------------------------------------ paths

    string RemoteUri(string uriOrPath)
    {
        if (_ssh == null) return uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? uriOrPath : LanguageService.UriOfPath(Path.GetFullPath(uriOrPath));
        string local = uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uriOrPath).LocalPath : uriOrPath;
        string remote = _ssh!.ToTarget(Path.GetFullPath(local));
        return remote.StartsWith('/') ? "file://" + string.Join("/", remote.Split('/').Select(Uri.EscapeDataString)) : uriOrPath;
    }

    string HostUri(string uri)
    {
        if (_ssh == null) return uri;
        try
        {
            var u = new Uri(uri);
            if (!u.IsFile) return uri;
            string host = _ssh!.ToHost(Uri.UnescapeDataString(u.AbsolutePath));
            return host.StartsWith('/') ? uri : LanguageService.UriOfPath(host);
        }
        catch (UriFormatException) { return uri; }
    }

    /// <summary>Every file:// address of a message, mapped.</summary>
    static JsonNode? Map(JsonNode? node, Func<string, string> map)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) o[key] = map(s);
                    else Map(o[key], map);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) a[i] = map(s);
                    else Map(a[i], map);
                break;
        }
        return node;
    }

    // ------------------------------------------------------------ answers in the page's shape

    static string? Text(JsonNode? doc) => doc switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when o["value"] is { } value => o["language"] is { } lang ? $"```{lang}\n{value}\n```" : value.ToString(),
        JsonArray a => string.Join("\n\n", a.Select(Text).Where(t => !string.IsNullOrEmpty(t))),
        _ => doc.ToString(),
    };

    static JsonNode? Shape(string method, JsonNode? answer, JsonNode? p)
    {
        switch (method)
        {
            case "textDocument/completion":
            {
                var items = answer is JsonArray list ? list : answer?["items"] as JsonArray ?? new JsonArray();
                var shaped = new JsonArray();
                foreach (var i in items.OfType<JsonObject>())
                {
                    var item = new JsonObject
                    {
                        ["label"] = i["label"]?.DeepClone(), ["kind"] = i["kind"]?.DeepClone(), ["detail"] = i["detail"]?.DeepClone(), ["documentation"] = Text(i["documentation"]),
                        ["insertText"] = i["insertText"]?.DeepClone() ?? i["label"]?.DeepClone(), ["filterText"] = i["filterText"]?.DeepClone(), ["sortText"] = i["sortText"]?.DeepClone(),
                        ["isSnippet"] = i["insertTextFormat"]?.GetValue<int>() == 2, ["additionalTextEdits"] = i["additionalTextEdits"]?.DeepClone(),
                    };
                    if (i["textEdit"] is JsonObject edit)
                        item["textEdit"] = new JsonObject { ["newText"] = edit["newText"]?.DeepClone(), ["range"] = (edit["range"] ?? edit["replace"])?.DeepClone() };
                    // the page puts a snippet's insertText in, not its textEdit: the edit's text is the one to insert
                    if (item["isSnippet"]!.GetValue<bool>() && item["textEdit"] is JsonObject te) { item["insertText"] = te["newText"]?.DeepClone(); item.Remove("textEdit"); }
                    shaped.Add(item);
                }
                return new JsonObject { ["isIncomplete"] = answer?["isIncomplete"]?.DeepClone() ?? false, ["items"] = shaped };
            }
            case "textDocument/hover":
            {
                string? text = Text(answer?["contents"]);
                if (string.IsNullOrWhiteSpace(text)) return null;
                var at = p?["position"]?.DeepClone();
                return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = text }, ["range"] = answer!["range"]?.DeepClone() ?? new JsonObject { ["start"] = at, ["end"] = at?.DeepClone() } };
            }
            case "textDocument/definition" or "textDocument/implementation" or "textDocument/typeDefinition" or "textDocument/references":
            {
                var all = answer is JsonArray many ? many.ToList() : answer is JsonObject one ? new List<JsonNode?> { one } : new List<JsonNode?>();
                return new JsonArray(all.OfType<JsonObject>().Select(l => (JsonNode)new JsonObject
                {
                    ["uri"] = (l["uri"] ?? l["targetUri"])?.DeepClone(), ["range"] = (l["range"] ?? l["targetSelectionRange"] ?? l["targetRange"])?.DeepClone(),
                }).ToArray());
            }
            case "textDocument/signatureHelp":
            {
                if (answer?["signatures"] is not JsonArray sigs || sigs.Count == 0) return null;
                return new JsonObject
                {
                    ["signatures"] = new JsonArray(sigs.OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                    {
                        ["label"] = s["label"]?.DeepClone(), ["documentation"] = Text(s["documentation"]),
                        ["parameters"] = new JsonArray((s["parameters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(x => (JsonNode)new JsonObject { ["label"] = x["label"]?.DeepClone(), ["documentation"] = Text(x["documentation"]) }).ToArray()),
                    }).ToArray()),
                    ["activeSignature"] = answer["activeSignature"]?.DeepClone() ?? 0, ["activeParameter"] = answer["activeParameter"]?.DeepClone() ?? 0,
                };
            }
            default:
                return answer;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _copyAgain?.Dispose();
        _outgoing.CompleteAdding();
        try { if (_channel != null && _failed == null) _channel.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = -1, ["method"] = "shutdown" }); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        try { _socket?.Dispose(); } catch (IOException) { }
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch (InvalidOperationException) { }
        _job?.Kill();
        _job?.Dispose();
        if (_runId is { } id && _ssh is { } ssh) Task.Run(() => ssh.StopGroup(id));
    }
}
