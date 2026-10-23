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

    /// <summary>Reads messages until the client exits or the input ends.</summary>
    public int Run(MessageChannel channel)
    {
        while (!Exited)
        {
            var msg = channel.Read();
            if (msg == null) break;
            Handle(msg);
        }
        return Exited ? ExitCode : 1;
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
            },
            serverInfo = new { name = "PyMagic", version = "0.1" },
        };
    }

    // ------------------------------------------------------------ notifications

    void Notify(string method, JsonNode? p)
    {
        switch (method)
        {
            case "exit":
                Exited = true;
                ExitCode = _shutdown ? 0 : 1;
                break;
            case "textDocument/didOpen":
            {
                var td = p!["textDocument"]!;
                _service.Open(td["uri"]!.GetValue<string>(), td["text"]!.GetValue<string>(), td["version"]?.GetValue<int>() ?? 0);
                PublishAll();
                break;
            }
            case "textDocument/didChange":
            {
                var td = p!["textDocument"]!;
                var changes = p["contentChanges"]!.AsArray();
                // full-document sync: the last change carries the whole text
                string text = changes[^1]!["text"]!.GetValue<string>();
                _service.Change(td["uri"]!.GetValue<string>(), text, td["version"]?.GetValue<int>() ?? 0);
                PublishAll();
                break;
            }
            case "textDocument/didClose":
            {
                string uri = p!["textDocument"]!["uri"]!.GetValue<string>();
                _service.Close(uri);
                Send(new { jsonrpc = "2.0", method = "textDocument/publishDiagnostics", @params = new { uri, diagnostics = Array.Empty<object>() } });
                PublishAll();
                break;
            }
        }
    }

    /// <summary>An edit can change what other open files see, so every open document is re-analysed.</summary>
    void PublishAll()
    {
        foreach (var uri in _service.OpenUris.ToList())
        {
            var diags = _service.Diagnostics(uri);
            Send(new
            {
                jsonrpc = "2.0",
                method = "textDocument/publishDiagnostics",
                @params = new { uri, version = _service.VersionOf(uri), diagnostics = diags },
            });
        }
    }

    // ------------------------------------------------------------ plumbing

    static string UriOf(JsonNode? p) => p!["textDocument"]!["uri"]!.GetValue<string>();
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
