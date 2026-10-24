using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PyMagic.Server;

/// <summary>JSON-RPC 2.0 over the LSP base protocol (Content-Length framed messages).</summary>
public sealed class MessageChannel
{
    readonly Stream _in;
    readonly Stream _out;

    public MessageChannel(Stream input, Stream output) { _in = input; _out = output; }

    /// <summary>The next message, or null at end of input.</summary>
    public JsonNode? Read()
    {
        int length = -1;
        while (true)
        {
            string? line = ReadHeaderLine();
            if (line == null) return null;
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[(colon + 1)..].Trim());
        }
        if (length < 0) return null;
        var buf = new byte[length];
        int got = 0;
        while (got < length)
        {
            int n = _in.Read(buf, got, length - got);
            if (n <= 0) return null;
            got += n;
        }
        return JsonNode.Parse(buf);
    }

    string? ReadHeaderLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = _in.ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    public void Write(JsonNode message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_out)
        {
            _out.Write(header);
            _out.Write(body);
            _out.Flush();
        }
    }
}

/// <summary>Maps LSP requests and notifications onto the <see cref="LanguageService"/>.</summary>
public sealed class Server
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly LanguageService _service;
    readonly Action<JsonNode> _send;
    bool _shutdown;

    public int ExitCode { get; private set; }
    public bool Exited { get; private set; }

    public Server(LanguageService service, Action<JsonNode> send) { _service = service; _send = send; }

    /// <summary>
    /// When true, edits only mark documents as changed and <see cref="FlushDiagnostics"/> publishes later (the message
    /// loop calls it once the input queue is empty, so a burst of keystrokes costs one analysis, not one per key).
    /// </summary>
    public bool Deferred { get; set; }

    /// <summary>Reads messages until the client exits or the input ends.</summary>
    public int Run(MessageChannel channel)
    {
        var inbox = new BlockingCollection<JsonNode?>();
        var reader = new Thread(() =>
        {
            try { while (channel.Read() is { } msg) inbox.Add(msg); }
            catch (IOException) { }
            inbox.Add(null);
        }) { IsBackground = true };
        reader.Start();
        Pump(inbox);
        return Exited ? ExitCode : 1;
    }

    /// <summary>
    /// Handles queued messages until <paramref name="inbox"/> yields null (input ended), the client exits, or the queue is
    /// completed. Everything that is waiting is taken at once so superseded edits can be dropped, requests are answered
    /// before diagnostics are computed, and diagnostics are published only when no further message is waiting.
    /// </summary>
    public void Pump(BlockingCollection<JsonNode?> inbox)
    {
        Deferred = true;
        while (!Exited)
        {
            if (!inbox.TryTake(out var first, Timeout.Infinite) || first == null) break;
            var batch = new List<JsonNode> { first };
            bool ended = false;
            while (inbox.TryTake(out var more))
            {
                if (more == null) { ended = true; break; }
                batch.Add(more);
            }
            foreach (var msg in Coalesce(batch))
            {
                Handle(msg);
                if (Exited) break;
            }
            try { FlushDiagnostics(); }
            catch (Exception e) { Log($"error while publishing diagnostics: {e}"); }
            if (ended) break;
        }
    }

    /// <summary>Drops a full-text edit when a later edit of the same document is queued and no request sits between them.</summary>
    public static List<JsonNode> Coalesce(IReadOnlyList<JsonNode> batch)
    {
        var keep = new bool[batch.Count];
        var nextEdit = new Dictionary<string, int>();   // uri -> index of a later didChange with no request in between
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            var m = batch[i];
            string? method = m["method"]?.GetValue<string>();
            keep[i] = true;
            if (m["id"] != null) { nextEdit.Clear(); continue; }   // a request observes the text as of this point
            if (method == "textDocument/didChange" && m["params"]?["textDocument"]?["uri"]?.GetValue<string>() is { } uri)
            {
                if (nextEdit.ContainsKey(uri)) keep[i] = false;
                nextEdit[uri] = i;
            }
            else if (method is "textDocument/didOpen" or "textDocument/didClose" && m["params"]?["textDocument"]?["uri"]?.GetValue<string>() is { } other)
                nextEdit.Remove(other);
        }
        return batch.Where((_, i) => keep[i]).ToList();
    }

    public void Handle(JsonNode message)
    {
        string? method = message["method"]?.GetValue<string>();
        var id = message["id"];
        var args = message["params"];
        if (method == null) return; // a response to something we asked; we never ask

        try
        {
            if (id != null) Respond(id, Request(method, args));
            else Notify(method, args);
        }
        catch (RpcException e) when (id != null) { Fail(id, e.Code, e.Message); }
        catch (ServiceException e) when (id != null) { Fail(id, -32803, e.Message); }
        catch (Exception e) when (id != null) { Fail(id, -32603, e.Message); }
        catch (Exception e) { Log($"error in {method}: {e}"); }
    }

    sealed class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }

    // ------------------------------------------------------------ requests

    object? Request(string method, JsonNode? p)
    {
        if (_shutdown && method != "shutdown") throw new RpcException(-32600, "server is shutting down");
        switch (method)
        {
            case "initialize": return Initialize(p);
            case "shutdown": _shutdown = true; return null;

            case "textDocument/hover":
                return _service.Hover(UriOf(p), PositionOf(p));
            case "textDocument/definition":
                return _service.Definition(UriOf(p), PositionOf(p));
            case "textDocument/completion":
                return new { isIncomplete = false, items = _service.Completion(UriOf(p), PositionOf(p)) };
            case "textDocument/references":
                return _service.References(UriOf(p), PositionOf(p), p!["context"]?["includeDeclaration"]?.GetValue<bool>() ?? true);
            case "textDocument/prepareRename":
                return _service.PrepareRename(UriOf(p), PositionOf(p));
            case "textDocument/rename":
                return _service.Rename(UriOf(p), PositionOf(p), p!["newName"]!.GetValue<string>());
            case "pymagic/signature":
                return _service.GetSignature(UriOf(p), PositionOf(p));
            case "pymagic/changeSignature":
                return _service.ChangeSignature(UriOf(p), PositionOf(p), p!["newName"]?.GetValue<string>(), ParseParameters(p["parameters"]));
            case "pymagic/refactor":
                return _service.Refactor(UriOf(p), p!["kind"]!.GetValue<string>(), RangeOf(p), p["name"]?.GetValue<string>(), p["all"]?.GetValue<bool>() ?? false);
            case "textDocument/codeAction":
                return _service.CodeActions(UriOf(p), RangeOf(p));
            case "textDocument/documentSymbol":
                return _service.DocumentSymbols(UriOf(p));
            default:
                throw new RpcException(-32601, $"method not found: {method}");
        }
    }

    object Initialize(JsonNode? p)
    {
        var folders = new List<string>();
        if (p?["workspaceFolders"] is JsonArray wf)
            foreach (var f in wf) if (f?["uri"]?.GetValue<string>() is { } u) folders.Add(u);
        if (folders.Count == 0 && p?["rootUri"]?.GetValue<string>() is { } root) folders.Add(root);
        foreach (var u in folders)
            if (u.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                _service.SourceRoots.Add(new Uri(u).LocalPath);

        if (p?["initializationOptions"] is JsonObject opts)
        {
            if (opts["typeshedPath"]?.GetValue<string>() is { } ts) _service.TypeshedPath = ts;
            if (opts["libraryRoots"] is JsonArray libs)
                foreach (var l in libs) if (l?.GetValue<string>() is { } s) _service.LibraryRoots.Add(s);
            if (opts["reportTypeMismatches"]?.GetValue<bool>() is { } r) _service.ReportTypeMismatches = r;
            if (opts["pythonPath"]?.GetValue<string>() is { Length: > 0 } python) ApplyInterpreter(python);
        }

        return new
        {
            capabilities = new
            {
                textDocumentSync = new { openClose = true, change = 1 },
                hoverProvider = true,
                definitionProvider = true,
                completionProvider = new { triggerCharacters = new[] { "." } },
                documentSymbolProvider = true,
                codeActionProvider = new { codeActionKinds = new[] { LanguageService.QuickFixKind, LanguageService.OrganizeImportsKind, LanguageService.RefactorKind, LanguageService.RefactorExtractKind, LanguageService.RefactorInlineKind } },
                referencesProvider = true,
                renameProvider = new { prepareProvider = true },
            },
            serverInfo = new { name = "PyMagic", version = "0.1" },
        };
    }

    // ------------------------------------------------------------ notifications

    void Notify(string method, JsonNode? p)
    {
        switch (method)
        {
            case "workspace/didChangeConfiguration":
            {
                // settings may be nested under "pymagic" (the usual shape) or given flat
                var s = p?["settings"];
                s = s?["pymagic"] ?? s;
                if (s?["pythonPath"] is { } pp)
                {
                    ApplyInterpreter(pp.GetValue<string?>());
                    // what the libraries provide changed, so every open document may now look different
                    _published.Clear();
                    foreach (var open in _service.OpenUris) _dirty.Add(open);
                    if (!Deferred) FlushDiagnostics();
                }
                break;
            }
            case "exit":
                Exited = true;
                ExitCode = _shutdown ? 0 : 1;
                break;
            case "textDocument/didOpen":
            {
                var td = p!["textDocument"]!;
                string uri = td["uri"]!.GetValue<string>();
                _service.Open(uri, td["text"]!.GetValue<string>(), td["version"]?.GetValue<int>() ?? 0);
                _dirty.Add(uri);
                if (!Deferred) FlushDiagnostics();
                break;
            }
            case "textDocument/didChange":
            {
                var td = p!["textDocument"]!;
                var changes = p["contentChanges"]!.AsArray();
                // full-document sync: the last change carries the whole text
                string text = changes[^1]!["text"]!.GetValue<string>();
                string uri = td["uri"]!.GetValue<string>();
                _service.Change(uri, text, td["version"]?.GetValue<int>() ?? 0);
                _dirty.Add(uri);
                if (!Deferred) FlushDiagnostics();
                break;
            }
            case "textDocument/didClose":
            {
                string uri = p!["textDocument"]!["uri"]!.GetValue<string>();
                // files that import this one now see the version on disk: they need a fresh look
                foreach (var d in _service.DependentsOf(new[] { uri })) _dirty.Add(d);
                _service.Close(uri);
                _dirty.Remove(uri);
                _published.Remove(uri);
                Send(new { jsonrpc = "2.0", method = "textDocument/publishDiagnostics", @params = new { uri, diagnostics = Array.Empty<object>() } });
                if (!Deferred) FlushDiagnostics();
                break;
            }
        }
    }

    /// <summary>Follows the interpreter at this path (null or empty: back to the defaults).</summary>
    void ApplyInterpreter(string? path)
    {
        if (string.IsNullOrEmpty(path)) { _service.ConfigureInterpreter(null); return; }
        var result = InterpreterInspector.Inspect(path);
        if (result.Interpreter == null) Log($"cannot use interpreter '{path}': {result.Error}");
        else _service.ConfigureInterpreter(result.Interpreter);
    }

    readonly HashSet<string> _dirty = new();
    readonly Dictionary<string, string> _published = new();

    /// <summary>
    /// Publishes diagnostics for the documents edited since the last call and for the open documents that depend on
    /// them (through imports), skipping any whose diagnostics did not change.
    /// </summary>
    public void FlushDiagnostics()
    {
        if (_dirty.Count == 0) return;
        var edited = _dirty.Where(u => _service.VersionOf(u) != null).ToList();
        _dirty.Clear();
        var affected = new List<string>(edited);
        foreach (var d in _service.DependentsOf(edited))
            if (!affected.Contains(d)) affected.Add(d);

        foreach (var uri in affected)
        {
            var diags = _service.Diagnostics(uri);
            string signature = JsonSerializer.Serialize(diags, Json);
            if (_published.TryGetValue(uri, out var last) && last == signature) continue;
            _published[uri] = signature;
            Send(new
            {
                jsonrpc = "2.0",
                method = "textDocument/publishDiagnostics",
                @params = new { uri, version = _service.VersionOf(uri), diagnostics = diags },
            });
        }
    }

    // ------------------------------------------------------------ plumbing


    static List<SignatureParam> ParseParameters(JsonNode? node)
    {
        var list = new List<SignatureParam>();
        foreach (var item in node?.AsArray() ?? new JsonArray())
        {
            string? Str(string key) => item![key] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
            int? origin = item!["origin"] is { } o && o.GetValueKind() == System.Text.Json.JsonValueKind.Number ? o.GetValue<int>() : null;
            list.Add(new SignatureParam(origin, Str("name") ?? "", Str("category") ?? "normal", Str("annotation"), Str("default"), Str("callValue")));
        }
        return list;
    }
    static string UriOf(JsonNode? p) => p!["textDocument"]!["uri"]!.GetValue<string>();
    static Range RangeOf(JsonNode? p) => new(
        new Position(p!["range"]!["start"]!["line"]!.GetValue<int>(), p["range"]!["start"]!["character"]!.GetValue<int>()),
        new Position(p["range"]!["end"]!["line"]!.GetValue<int>(), p["range"]!["end"]!["character"]!.GetValue<int>()));
    static Position PositionOf(JsonNode? p) =>
        new(p!["position"]!["line"]!.GetValue<int>(), p["position"]!["character"]!.GetValue<int>());

    void Respond(JsonNode id, object? result)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        msg["result"] = result == null ? null : JsonSerializer.SerializeToNode(result, Json);
        _send(msg);
    }

    void Fail(JsonNode id, int code, string message) => _send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    });

    void Send(object message) => _send(JsonSerializer.SerializeToNode(message, Json)!);

    static void Log(string text) => Console.Error.WriteLine(text);
}
