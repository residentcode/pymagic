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
            try { while (channel.Read() is { } msg) { NoteCancel(msg); inbox.Add(msg); } }
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
            foreach (var msg in batch) NoteCancel(msg);
            foreach (var msg in Coalesce(batch))
            {
                Handle(msg);
                if (Exited) break;
            }
            bool was = LineMap.Utf8.Value;
            LineMap.Utf8.Value = _utf8;
            try { FlushDiagnostics(); }
            catch (Exception e) { Log($"error while publishing diagnostics: {e}"); }
            finally { LineMap.Utf8.Value = was; }
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

        // a request the client cancelled before it was reached is not worked out at all
        if (id != null && _cancelled.TryRemove(id.ToJsonString(), out _)) { Fail(id, RequestCancelled, "the request was cancelled"); return; }

        // the whole project's problems take seconds: they are worked out beside the loop, on a copy of the documents, so typing and hovering go on
        if (id != null && method == "pymagic/problems" && Deferred) { StartProblems(id, args); return; }

        bool was = LineMap.Utf8.Value;
        LineMap.Utf8.Value = _utf8;
        // a cancel that arrives while the request runs trips its token (NoteCancel), and the long work checks it
        using var cancel = new CancellationTokenSource();
        if (id != null) { Volatile.Write(ref _running, new Running(id.ToJsonString(), cancel)); PyMagic.Core.RequestCancellation.Current = cancel.Token; }
        try
        {
            if (id != null)
            {
                var result = Request(method, args);
                // cancelled while it was worked out: the answer is not wanted any more
                if (_cancelled.TryRemove(id.ToJsonString(), out _)) Fail(id, RequestCancelled, "the request was cancelled");
                else Respond(id, result);
            }
            else Notify(method, args);
        }
        catch (OperationCanceledException) when (id != null && cancel.IsCancellationRequested) { _cancelled.TryRemove(id.ToJsonString(), out _); Fail(id, RequestCancelled, "the request was cancelled"); }
        catch (RpcException e) when (id != null) { Fail(id, e.Code, e.Message); }
        catch (ServiceException e) when (id != null) { Fail(id, -32803, e.Message); }
        catch (Exception e) when (id != null) { Fail(id, -32603, e.Message); }
        catch (Exception e) { Log($"error in {method}: {e}"); }
        finally
        {
            LineMap.Utf8.Value = was;
            if (id != null) { Volatile.Write(ref _running, null); PyMagic.Core.RequestCancellation.Current = default; }
        }
    }

    const int RequestCancelled = -32800;

    /// <summary>The request being worked out, by its id, with the source that stops it.</summary>
    Running? _running;
    sealed record Running(string Id, CancellationTokenSource Cancel);

    /// <summary>The id of the request being worked out (as JSON: 7 or "s9"), or null between requests.</summary>
    public string? RunningRequest => Volatile.Read(ref _running)?.Id;

    /// <summary>The ids of requests the client cancelled (`$/cancelRequest`) that have not been answered yet.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _cancelled = new();

    /// <summary>Whether positions count UTF-8 bytes: the client offered `utf-8` among its `general.positionEncodings`.</summary>
    bool _utf8;

    /// <summary>
    /// Notes a `$/cancelRequest` as soon as it arrives (the readers call this before queueing), so that a request still waiting behind others is dropped,
    /// and the one being worked out stops at the next module or function it reaches.
    /// A cancel of a request that was answered already stays until the set is cleared.
    /// </summary>
    public void NoteCancel(JsonNode? message)
    {
        if (message?["method"]?.GetValue<string>() != "$/cancelRequest" || message["params"]?["id"] is not { } id) return;
        if (_cancelled.Count > 1000) _cancelled.Clear();
        string key = id.ToJsonString();
        _cancelled[key] = true;
        // the request running now is stopped at its next check
        if (Volatile.Read(ref _running) is { } running && running.Id == key)
            try { running.Cancel.Cancel(); } catch (ObjectDisposedException) { /* it ended just now */ }
    }

    int _problemsTicket;
    readonly SemaphoreSlim _problemsGate = new(1, 1);

    /// <summary>Answers a request for the problems of the project later. A request that a newer one has replaced while it waited is answered with nothing (the page drops stale answers).</summary>
    void StartProblems(JsonNode id, JsonNode? p)
    {
        int least = p?["leastSevere"]?.GetValue<int>() ?? 3;
        var copy = _service.ForkForAnalysis();
        int mine = Interlocked.Increment(ref _problemsTicket);
        var idCopy = id.DeepClone();
        Task.Run(() =>
        {
            _problemsGate.Wait();
            try
            {
                if (mine != Volatile.Read(ref _problemsTicket)) Respond(idCopy, Array.Empty<ProblemEntry>());
                else Respond(idCopy, copy.Problems(least));
            }
            catch (Exception e) { Fail(idCopy, -32603, e.Message); }
            finally { _problemsGate.Release(); }
        });
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
            case "textDocument/signatureHelp":
                return _service.SignatureHelp(UriOf(p), PositionOf(p));
            case "textDocument/completion":
                return new { isIncomplete = false, items = _service.Completion(UriOf(p), PositionOf(p)) };
            case "textDocument/implementation":
                return _service.Implementation(UriOf(p), PositionOf(p));
            case "textDocument/prepareCallHierarchy":
                return _service.PrepareCallHierarchy(UriOf(p), PositionOf(p));
            case "callHierarchy/incomingCalls":
                return _service.IncomingCalls(HierarchyItemOf(p!["item"]));
            case "callHierarchy/outgoingCalls":
                return _service.OutgoingCalls(HierarchyItemOf(p!["item"]));
            case "textDocument/prepareTypeHierarchy":
                return _service.PrepareTypeHierarchy(UriOf(p), PositionOf(p));
            case "typeHierarchy/supertypes":
                return _service.Supertypes(HierarchyItemOf(p!["item"]));
            case "typeHierarchy/subtypes":
                return _service.Subtypes(HierarchyItemOf(p!["item"]));
            case "pymagic/fixtures":
                return _service.Fixtures();
            case "pymagic/routes":
                return _service.Routes();
            case "pymagic/problems":
                return _service.Problems(p?["leastSevere"]?.GetValue<int>() ?? 3);
            case "pymagic/todos":
                return _service.Todos();
            case "pymagic/inheritance":
                return _service.InheritanceMarkers(UriOf(p));
            case "textDocument/references":
                return _service.References(UriOf(p), PositionOf(p), p!["context"]?["includeDeclaration"]?.GetValue<bool>() ?? true);
            case "textDocument/prepareRename":
                return _service.PrepareRename(UriOf(p), PositionOf(p));
            case "textDocument/rename":
                return _service.Rename(UriOf(p), PositionOf(p), p!["newName"]!.GetValue<string>(), p["renameBase"]?.GetValue<bool>() ?? false);
            case "pymagic/signature":
                return _service.GetSignature(UriOf(p), PositionOf(p));
            case "pymagic/changeSignature":
                return _service.ChangeSignature(UriOf(p), PositionOf(p), p!["newName"]?.GetValue<string>(), ParseParameters(p["parameters"]));
            case "pymagic/moveTargets":
                return _service.MoveTargets(UriOf(p));
            case "pymagic/moveModule":
                return _service.PlanModuleMove(p!["from"]!.GetValue<string>(), p["to"]!.GetValue<string>());
            case "pymagic/generate":
                return _service.Generate(UriOf(p), PositionOf(p));
            case "pymagic/expressionType":
                return _service.ExpressionType(UriOf(p), RangeOf(p));
            case "pymagic/externalDoc":
                return _service.ExternalDoc(UriOf(p), PositionOf(p));
            case "pymagic/structuralSearch":
                return _service.StructuralSearch(p!["pattern"]!.GetValue<string>(), p["replacement"]?.GetValue<string>());
            case "pymagic/duplicates":
                return _service.Duplicates(p?["minLines"]?.GetValue<int>() ?? 6);
            case "pymagic/diagram":
                return _service.Diagram(p?["kind"]?.GetValue<string>() ?? "classes", p?["textDocument"]?["uri"]?.GetValue<string>());
            case "pymagic/djangoStructure":
                return _service.DjangoStructure();
            case "pymagic/logicalStructure":
                return _service.LogicalStructure(UriOf(p));
            case "pymagic/templateLinks":
                return _service.TemplateLinks(UriOf(p));
            case "pymagic/templates":
                return _service.TemplateList();
            case "pymagic/inlayHints":
                return _service.InlayHints(UriOf(p), RangeOf(p));
            case "pymagic/semanticTokens":
                return new { data = _service.SemanticTokens(UriOf(p)) };
            case "pymagic/fileUsages":
                return _service.FileUsages(UriOf(p));
            case "pymagic/move":
                return _service.MoveSymbol(UriOf(p), PositionOf(p), p!["target"]!.GetValue<string>());
            case "pymagic/refactor":
                return _service.Refactor(UriOf(p), p!["kind"]!.GetValue<string>(), RangeOf(p), p["name"]?.GetValue<string>(), p["all"]?.GetValue<bool>() ?? false,
                    p["members"] is System.Text.Json.Nodes.JsonArray ms ? ms.Select(x => x!.GetValue<string>()).ToList() : null);
            case "textDocument/codeAction":
                return _service.CodeActions(UriOf(p), RangeOf(p));
            case "textDocument/formatting":
                return _service.FormatDocument(UriOf(p), engine: EngineOf(p));
            case "textDocument/rangeFormatting":
                return _service.FormatRange(UriOf(p), RangeOf(p), EngineOf(p));
            case "pymagic/formatters":
                return _service.Formatters();
            case "pymagic/inspections":
                return _service.Inspections();
            case "pymagic/setInspection":
                _service.SetInspection(p!["code"]!.GetValue<string>(), p["level"] is { } lv && lv.GetValueKind() == System.Text.Json.JsonValueKind.String ? lv.GetValue<string>() : null);
                RepublishAll();
                return null;
            case "pymagic/resetInspections":
                _service.ResetInspections();
                RepublishAll();
                return null;
            case "pymagic/setFormatDefaults":
                // the page's quote setting: the quotes strings get when the project's configuration does not name them
                _service.DefaultQuotes = p?["quotes"]?.GetValue<string>() switch { "single" => Formatting.QuoteStyle.Single, "preserve" => Formatting.QuoteStyle.Preserve, "double" => Formatting.QuoteStyle.Double, _ => null };
                return null;
            case "pymagic/formatConfig":
                return _service.FormatConfigOf(UriOf(p));
            case "pymagic/formatText":
                return new { text = _service.FormatText(p!["text"]!.GetValue<string>(), UriOf(p), EngineOf(p), p["range"] != null ? RangeOf(p) : null) };
            case "pymagic/organizeImports":
                return _service.OrganizeImports(UriOf(p));
            case "workspace/executeCommand":
            {
                string command = p?["command"]?.GetValue<string>() ?? "";
                var args = (p?["arguments"] as JsonArray)?.Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : a?.ToJsonString() ?? "").ToList() ?? new List<string>();
                string said = _service.ExecuteCommand(command, args);
                // pip changed what the interpreter has: read it again; either way every open file is checked again
                if (command == "pymagic.installPackage") ApplyInterpreter(_service.Interpreter?.Path);
                RepublishAll();
                if (!Deferred) FlushDiagnostics();
                _send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "window/showMessage", ["params"] = new JsonObject { ["type"] = 3, ["message"] = said } });
                return null;
            }
            case "workspace/symbol":
                return _service.WorkspaceSymbols(p?["query"]?.GetValue<string>() ?? "");
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
                _service.AddSourceRoot(new Uri(u).LocalPath);

        if (p?["initializationOptions"] is JsonObject opts)
        {
            if (opts["typeshedPath"]?.GetValue<string>() is { } ts) _service.TypeshedPath = ts;
            if (opts["libraryRoots"] is JsonArray libs)
                foreach (var l in libs) if (l?.GetValue<string>() is { } s) _service.LibraryRoots.Add(s);
            if (opts["reportTypeMismatches"]?.GetValue<bool>() is { } r) _service.ReportTypeMismatches = r;
            if (opts["pythonPath"]?.GetValue<string>() is { Length: > 0 } python) ApplyInterpreter(python);
        }
        _ = _service.StartIndexing();   // the names of every file of the project, in the background

        // UTF-8 positions when the client can take them (LSP 3.17): editors that keep their text as bytes then need no conversion
        _utf8 = p?["capabilities"]?["general"]?["positionEncodings"] is JsonArray encodings && encodings.Any(e => e?.GetValue<string>() == "utf-8");
        LineMap.Utf8.Value = _utf8;

        return new
        {
            capabilities = new
            {
                positionEncoding = _utf8 ? "utf-8" : "utf-16",
                textDocumentSync = new { openClose = true, change = 1 },
                hoverProvider = true,
                definitionProvider = true,
                completionProvider = new { triggerCharacters = new[] { "." } },
                signatureHelpProvider = new { triggerCharacters = new[] { "(", "," } },
                documentSymbolProvider = true,
                workspaceSymbolProvider = true,
                implementationProvider = true,
                callHierarchyProvider = true,
                typeHierarchyProvider = true,
                documentFormattingProvider = true,
                documentRangeFormattingProvider = true,
                codeActionProvider = new { codeActionKinds = new[] { LanguageService.QuickFixKind, LanguageService.OrganizeImportsKind, LanguageService.RefactorKind, LanguageService.RefactorExtractKind, LanguageService.RefactorInlineKind } },
                referencesProvider = true,
                renameProvider = new { prepareProvider = true },
                // the commands of the import quick fixes, for clients that send them back (the PyMagic page runs the install itself)
                executeCommandProvider = new { commands = new[] { "pymagic.ignoreImport", "pymagic.unignoreImport", "pymagic.installPackage" } },
            },
            serverInfo = new { name = "PyMagic", version = "0.1" },
        };
    }

    // ------------------------------------------------------------ notifications

    void Notify(string method, JsonNode? p)
    {
        switch (method)
        {
            case "pymagic/foldersChanged":
                // a folder was marked or unmarked: the sources roots are read again and every open file looked at again
                _service.SetMarkedSourceRoots(ProjectFolders.SourceRoots);
                RepublishAll();
                if (!Deferred) FlushDiagnostics();
                break;
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

    /// <summary>Looks at every open document again (the profile changed what is reported).</summary>
    void RepublishAll()
    {
        foreach (var uri in _service.AllOpenUris) _dirty.Add(uri);
        _published.Clear();
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
        foreach (var d in _service.DependentsOf(edited).Concat(_service.BddDependentsOf(edited)))
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
    /// <summary>The formatter the page selected: builtin, black or ruff.</summary>
    static string EngineOf(JsonNode? p) => p?["options"]?["pymagicEngine"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : "builtin";
    static Position PositionAt(JsonNode? n) => new(n!["line"]!.GetValue<int>(), n["character"]!.GetValue<int>());

    static Range RangeAt(JsonNode? n) => new(PositionAt(n!["start"]), PositionAt(n["end"]));

    /// <summary>A call or type hierarchy item as the client sends it back.</summary>
    static HierarchyItem HierarchyItemOf(JsonNode? n) => new(n!["name"]!.GetValue<string>(), n["kind"]!.GetValue<int>(), n["detail"]?.GetValue<string>(),
        n["uri"]!.GetValue<string>(), RangeAt(n["range"]), RangeAt(n["selectionRange"]));

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
