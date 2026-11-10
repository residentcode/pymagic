using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PyMagic.Server;

/// <summary>
/// PHP under PyMagic's debugger through Xdebug. The program (and every PHP process it starts: `php artisan serve` starts PHP's own server,
/// whose every request is a run of its own) is started with Xdebug's step debugger, which connects to this bridge's port and speaks DBGp
/// (https://xdebug.org/docs/dbgp). The bridge speaks to the RunSession the same JSON lines pm_debug.py and pm_nodedebug.mjs do (hello,
/// setBreakpoints, go, stopped, scopes, variables, evaluate, setVariable, continue / next / stepIn / stepOut, ...), over a connection of its
/// own to the session's port, so the debugger's panes, the values in the editor and the value popup work for PHP as they do for Python.
///
/// Each Xdebug connection is a PHP process or request; one is shown stopped at a time, and another that stops meanwhile is shown when the
/// shown one goes on. DBGp tells Xdebug nothing while PHP runs, so breakpoints changed meanwhile reach a run when it next stops, and every
/// new request has them all; Pause goes through Xdebug's control socket instead (Xdebug 3.5 on Windows, 3.3 on Linux). Library code (the project's vendor folder) is stepped over as pm_debug.py steps over libraries, unless
/// the run is with all code.
/// </summary>
public sealed class XdebugBridge : IDisposable
{
    readonly string _root;
    readonly bool _allCode;
    readonly TcpListener _xdebug;
    readonly object _gate = new();
    NetworkStream? _channel;
    TcpClient? _channelClient;
    bool _going, _muted, _disposed;
    string _exceptionMode = "uncaught";
    List<string> _exceptionClasses = new();
    readonly Dictionary<string, (string File, List<JsonObject> Specs)> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    readonly List<DbgpSession> _sessions = new();
    readonly List<TcpClient> _early = new();     // Xdebug's connections before "go": they wait until the breakpoints are known
    DbgpSession? _shown;
    readonly List<DbgpSession> _waiting = new();
    int _nextRef = 1;
    readonly Dictionary<int, Ref> _refs = new();

    /// <summary>A value the panes can open: a context of a frame (Locals, Superglobals) or a property of one (an array, an object).</summary>
    internal sealed record Ref(DbgpSession Session, int Depth, int Context, string? FullName)
    {
        /// <summary>The full names of the members last listed (name → fullname), which Set Value needs.</summary>
        public Dictionary<string, string> Children { get; } = new();
    }

    readonly ExecutionTarget? _target;
    readonly RemoteBladeViews? _views;

    /// <summary>The compiled files of a Blade template that PHP runs: in the project, or the copies of another machine's.</summary>
    internal List<string> CompiledOf(string template)
    {
        if (_views == null) return BladeViews.CompiledOf(_root, template);
        _views.Refresh();
        return BladeViews.CompiledIn(_views.LocalDir, template);
    }

    /// <summary><paramref name="target"/>: the machine PHP runs on when it is not this one (its paths are mapped to the project's).</summary>
    public XdebugBridge(string root, bool allCode, ExecutionTarget? target = null)
    {
        _root = Path.GetFullPath(root);
        _allCode = allCode;
        _target = target;
        // PHP on another machine compiles its Blade views there: copies of them here
        if (target is SshTarget ssh) _views = new RemoteBladeViews(ssh);
        _xdebug = new TcpListener(IPAddress.Loopback, 0);
        _xdebug.Start();
        Task.Run(AcceptXdebug);
    }

    /// <summary>The port Xdebug connects to.</summary>
    public int Port => ((IPEndPoint)_xdebug.LocalEndpoint).Port;

    /// <summary>
    /// The settings that turn Xdebug's step debugger on for every PHP process of the run, as an .ini file of a folder PHP_INI_SCAN_DIR names
    /// (so the processes the program starts, which get no -d of ours, have them too). <paramref name="dll"/>: Xdebug's DLL when php.ini does not
    /// load it already.
    /// </summary>
    public string IniText(string? dll) =>
        (dll != null ? $"zend_extension=\"{dll}\"\n" : "") +
        "xdebug.mode=debug\n" +
        "xdebug.start_with_request=yes\n" +
        "xdebug.client_host=127.0.0.1\n" +
        $"xdebug.client_port={Port}\n" +
        "xdebug.connect_timeout_ms=2000\n" +
        "xdebug.idekey=PYMAGIC\n" +
        // Pause's way in: polled by the clock (the default needs the processor's TSC clock, which a container or VM may not show)
        "xdebug.control_socket=time\n" +
        "xdebug.log_level=0\n";

    /// <summary>Connects to the RunSession's port as its debugger helper does, and serves its messages until either end goes.</summary>
    public void Connect(int port, string token) => Task.Run(() =>
    {
        try
        {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            lock (_gate) { _channelClient = client; _channel = client.GetStream(); }
            Send(new JsonObject { ["type"] = "hello", ["token"] = token });
            var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { continue; }
                if (msg is JsonObject o) Handle(o);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        Dispose();
    });

    void Send(JsonNode message)
    {
        lock (_gate)
        {
            if (_channel == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
                _channel.Write(bytes);
                _channel.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    void Say(string text) => Send(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = text });

    // ------------------------------------------------------------ messages from PyMagic

    static readonly Dictionary<string, string> Answers = new()
    {
        ["scopes"] = "scopes", ["variables"] = "variables", ["evaluate"] = "evaluated", ["setVariable"] = "variableSet", ["values"] = "values",
        ["table"] = "table", ["jump"] = "jumped", ["setName"] = "setName", ["reprOf"] = "reprOf", ["typeSource"] = "typeSource",
    };

    void Handle(JsonObject msg)
    {
        string kind = msg["type"]?.GetValue<string>() ?? "";
        switch (kind)
        {
            case "setBreakpoints":
            {
                string file = msg["file"]?.GetValue<string>() ?? "";
                var specs = new List<JsonObject>();
                if (msg["lines"] is JsonArray lines)
                    foreach (var l in lines)
                        specs.Add(l is JsonObject spec ? (JsonObject)spec.DeepClone() : new JsonObject { ["line"] = l?.GetValue<int>() ?? 0 });
                lock (_gate) _breakpoints[Canonical(file)] = (file, specs);
                foreach (var s in Sessions()) s.BreakpointsChanged(file);
                return;
            }
            case "setExceptionBreak":
                lock (_gate)
                {
                    _exceptionMode = msg["mode"]?.GetValue<string>() == "raised" ? "raised" : "uncaught";
                    _exceptionClasses = (msg["classes"] as JsonArray)?.Select(c => c?.ToString().Trim() ?? "").Where(c => c != "").ToList() ?? new();
                }
                foreach (var s in Sessions()) s.ExceptionsChanged();
                return;
            case "mute":
                _muted = msg["on"]?.GetValue<bool>() == true;
                return;
            case "go":
            {
                List<TcpClient> early;
                lock (_gate) { _going = true; early = _early.ToList(); _early.Clear(); }
                foreach (var c in early) StartSession(c);
                return;
            }
            case "pause":
                _ = Task.Run(Pause);
                return;
            case "continue": _ = _shown?.Resume("run"); return;
            case "next": _ = _shown?.Resume("step_over"); return;
            case "stepIn": case "stepInto": _ = _shown?.Resume("step_into"); return;
            case "stepOut": _ = _shown?.Resume("step_out"); return;
            case "runTo": _ = _shown?.RunTo(msg["file"]?.GetValue<string>() ?? "", Num(msg["line"])); return;
        }
        if (!Answers.TryGetValue(kind, out var answer))
        {
            if (msg["id"] != null) Send(new JsonObject { ["type"] = "evaluated", ["id"] = msg["id"]!.DeepClone(), ["error"] = $"{kind} is not there for PHP" });
            return;
        }
        var id = msg["id"]?.DeepClone();
        _ = Task.Run(async () =>
        {
            JsonObject reply;
            try { reply = await Answer(kind, msg); }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or DbgpException) { reply = new JsonObject { ["error"] = e.Message }; }
            reply["type"] = answer;
            reply["id"] = id;
            Send(reply);
        });
    }

    async Task<JsonObject> Answer(string kind, JsonObject msg)
    {
        int frameId = Num(msg["frameId"]);
        switch (kind)
        {
            case "scopes": return new JsonObject { ["scopes"] = await Need().Scopes(frameId) };
            case "variables": return new JsonObject { ["ref"] = msg["ref"]?.DeepClone(), ["items"] = await Variables(Num(msg["ref"])) };
            case "evaluate":
                return await Need().Evaluate(frameId, msg["expression"]?.GetValue<string>() ?? "", msg["evalOnly"]?.GetValue<bool>() == true, msg["detail"]?.GetValue<bool>() == true);
            case "setVariable": return await SetVariable(Num(msg["ref"]), msg["name"]?.GetValue<string>() ?? "", msg["expression"]?.GetValue<string>() ?? "");
            case "setName": return await Need().SetName(frameId, msg["name"]?.GetValue<string>() ?? "", msg["expression"]?.GetValue<string>() ?? "");
            case "values":
                return new JsonObject { ["values"] = await Need().Values(frameId, (msg["names"] as JsonArray)?.Select(n => n?.ToString() ?? "").ToList() ?? new()) };
            case "reprOf": return new JsonObject { ["error"] = "The value is shown already." };
            case "table": return new JsonObject { ["error"] = "View as table is for Python values (pandas, NumPy, lists of rows); View value shows a PHP value whole." };
            case "jump": return new JsonObject { ["error"] = "Xdebug cannot move the execution point to another line." };
            default: return new JsonObject { ["error"] = "Jump to type source is for Python classes." };
        }
    }

    static int Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return 0;
    }

    DbgpSession Need() => _shown ?? throw new InvalidOperationException("The program is not stopped.");

    /// <summary>
    /// Sends Xdebug's control command (ps, pause) to a PHP process of the machine it runs on and returns Xdebug's XML answer, or null when the
    /// process has no control socket. Set for PHP on another machine (its PHP connects to the socket there); this computer's is the default.
    /// </summary>
    public Func<string, string, string?>? Control { get; set; }

    /// <summary>
    /// Pause: every PHP run under the debugger that runs is told to stop where it is, through Xdebug's control socket (Xdebug 3.5 has it on
    /// Windows, a named pipe \\.\pipe\xdebug-ctrl.PID, and Linux, an abstract socket @xdebug-ctrl.PID; Xdebug polls it every 25 ms). Xdebug then
    /// answers the run command it was given with a break, at the line it is on.
    /// </summary>
    void Pause()
    {
        var running = Sessions().Where(s => !s.IsStopped && s.Pid != "").ToList();
        // a server's requests running: those (artisan serve's own process only waits on PHP's server meanwhile)
        if (running.Any(s => s.IsRequest)) running = running.Where(s => s.IsRequest).ToList();
        if (running.Count == 0)
        {
            Say(Sessions().Count == 0
                ? "No PHP is running under the debugger now: a server's request is paused while it runs (open the page, then Pause), or set a breakpoint.\n"
                : "The program is stopped already.\n");
            return;
        }
        bool any = false;
        foreach (var s in running)
        {
            s.Pausing = true;
            string? answer;
            try { answer = (Control ?? LocalControl)(s.Pid, "pause"); }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or ServiceException or InvalidOperationException) { answer = null; }
            if (answer != null && answer.Contains("success=\"1\"")) { any = true; continue; }
            s.Pausing = false;
            if (answer != null && Regex.Match(answer, @"<message>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</message>") is { Success: true } m) Say($"Xdebug did not pause {s.Label}: {m.Groups[1].Value}\n");
        }
        if (!any)
            Say("Xdebug did not answer the pause: it needs Xdebug 3.5 or later (on Windows) or 3.3 or later (on Linux), with xdebug.control_socket not off. " +
                "Set a breakpoint where it should stop instead.\n");
    }

    /// <summary>A PHP process of this computer: its control socket is the named pipe xdebug-ctrl.PID (Windows).</summary>
    static string? LocalControl(string pid, string command)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "xdebug-ctrl." + pid, System.IO.Pipes.PipeDirection.InOut);
        try { pipe.Connect(1000); }
        catch (TimeoutException) { return null; }
        var bytes = Encoding.ASCII.GetBytes(command);
        pipe.Write(bytes);
        pipe.Flush();
        // Xdebug looks at the pipe between statements, every 25 ms: its answer is one write, then it closes its end
        var answer = new MemoryStream();
        var buf = new byte[4096];
        var read = pipe.ReadAsync(buf).AsTask();
        if (!read.Wait(2000)) return null;
        answer.Write(buf, 0, read.Result);
        while (read.Result > 0 && answer.Length < 65536)
        {
            read = pipe.ReadAsync(buf).AsTask();
            if (!read.Wait(500)) break;
            answer.Write(buf, 0, read.Result);
        }
        return Encoding.UTF8.GetString(answer.ToArray());
    }

    /// <summary>
    /// A PHP process of another machine: a PHP command line run there, which connects to the abstract socket Xdebug opened (@xdebug-ctrl.PID)
    /// and prints the answer. <paramref name="remote"/> runs a shell command there and returns its exit code and output.
    /// </summary>
    public static Func<string, string, string?> RemoteControl(string php, Func<string, (int Code, string Out)> remote) => (pid, command) =>
    {
        if (!Regex.IsMatch(pid, @"^\d+$") || !Regex.IsMatch(command, @"^[a-z]+$")) return null;
        string code = "$s=@stream_socket_client('unix://'.chr(0).'xdebug-ctrl." + pid + "',$n,$e,2);if(!$s)exit(3);fwrite($s,'" + command + "');" +
                      "stream_set_timeout($s,2);echo stream_get_contents($s);";
        var (exit, output) = remote($"{SshTarget.Quote(php)} -n -r {SshTarget.Quote(code)}");
        return exit == 0 && output.Contains("<ctrl-response") ? output : null;
    };

    List<DbgpSession> Sessions() { lock (_gate) return _sessions.ToList(); }

    // ------------------------------------------------------------ Xdebug's connections

    async Task AcceptXdebug()
    {
        while (!_disposed)
        {
            TcpClient client;
            try { client = await _xdebug.AcceptTcpClientAsync(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            client.NoDelay = true;
            bool now;
            lock (_gate) { now = _going; if (!now) _early.Add(client); }
            if (now) StartSession(client);
        }
    }

    void StartSession(TcpClient client)
    {
        var session = new DbgpSession(this, client);
        lock (_gate) _sessions.Add(session);
        _ = Task.Run(session.Begin);
    }

    void Ended(DbgpSession s)
    {
        bool wasShown;
        lock (_gate)
        {
            _sessions.Remove(s);
            _waiting.Remove(s);
            wasShown = _shown == s;
            if (wasShown) _shown = null;
            DropRefs(s);
        }
        if (wasShown) { Send(new JsonObject { ["type"] = "running" }); ShowNext(); }
    }

    /// <summary>A run that stopped: shown, or kept until the shown one goes on.</summary>
    void Stopped(DbgpSession s)
    {
        bool show;
        string? other = null;
        lock (_gate)
        {
            show = _shown == null || _shown == s;
            if (show) _shown = s;
            else { if (!_waiting.Contains(s)) _waiting.Add(s); other = _shown!.Label; }
        }
        if (show) s.Show();
        else Say($"{s.Label} stopped too; it is shown when {other} goes on.\n");
    }

    void Resumed(DbgpSession s)
    {
        bool wasShown;
        lock (_gate)
        {
            wasShown = _shown == s;
            if (wasShown) _shown = null;
            DropRefs(s);
        }
        if (wasShown) { Send(new JsonObject { ["type"] = "running" }); ShowNext(); }
    }

    void ShowNext()
    {
        DbgpSession? next = null;
        lock (_gate)
        {
            if (_shown != null) return;
            while (_waiting.Count > 0 && next == null)
            {
                var w = _waiting[0];
                _waiting.RemoveAt(0);
                if (w.IsStopped) next = w;
            }
            if (next != null) _shown = next;
        }
        next?.Show();
    }

    int NewRef(Ref r) { lock (_gate) { int n = _nextRef++; _refs[n] = r; return n; } }

    void DropRefs(DbgpSession s)
    {
        foreach (var k in _refs.Where(kv => kv.Value.Session == s).Select(kv => kv.Key).ToList()) _refs.Remove(k);
    }

    async Task<JsonArray> Variables(int refId)
    {
        Ref? r;
        lock (_gate) _refs.TryGetValue(refId, out r);
        if (r == null) return new JsonArray();
        return await r.Session.Members(r);
    }

    async Task<JsonObject> SetVariable(int refId, string name, string expression)
    {
        Ref? r;
        lock (_gate) _refs.TryGetValue(refId, out r);
        if (r == null) return new JsonObject { ["error"] = "That value is gone; the program has moved on." };
        string full = r.Children.TryGetValue(name, out var f) ? f : r.FullName == null ? (name.StartsWith('$') ? name : "$" + name) : $"{r.FullName}[{PhpString(name)}]";
        return await r.Session.Set(r.Depth, r.Context, full, name, expression);
    }

    // ------------------------------------------------------------ paths, values

    internal static string Canonical(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>
    /// A file of this computer from Xdebug's file:// address (file:///C:/proj/a.php), or null for code that is not a file (eval). PHP on another
    /// machine names its own paths (file:///root/.pymagic/projects/…/a.php): they are the project's files here.
    /// </summary>
    internal string? FileOf(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var u = new Uri(uri);
            if (_target == null || _target.IsLocal) return u.LocalPath;
            string there = Uri.UnescapeDataString(u.AbsolutePath);
            return _views?.LocalOf(there) ?? _target.ToHost(there);
        }
        catch (UriFormatException) { return null; }
    }

    /// <summary>Xdebug's address of a file of this computer: file:///C:/proj/a%20b.php (on another machine, the path the file has there).</summary>
    internal string UriOf(string file)
    {
        string full = Path.GetFullPath(file);
        if (_target == null || _target.IsLocal) return new Uri(full).AbsoluteUri;
        string remote = _views?.RemoteOf(full) ?? _target.ToTarget(full);
        return "file://" + string.Join("/", remote.Split('/').Select(Uri.EscapeDataString));
    }

    bool IsLibrary(string? file) => !_allCode && file != null && Regex.IsMatch(file, @"[\\/]vendor[\\/]", RegexOptions.IgnoreCase);

    static string PhpString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>The text of a property: its own, decoded when it is base64.</summary>
    static string TextOf(XElement p)
    {
        var value = p.Elements().FirstOrDefault(e => e.Name.LocalName == "value");
        string raw = value != null ? value.Value : p.Nodes().OfType<XText>().Aggregate("", (a, t) => a + t.Value);
        string? enc = value != null ? (string?)value.Attribute("encoding") : (string?)p.Attribute("encoding");
        if (enc == "base64")
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim())); }
            catch (FormatException) { return raw; }
        }
        return raw;
    }

    /// <summary>A property as the panes show a value: its short text, its type (the class of an object), and a ref when it has members.</summary>
    internal (string Value, string Type, bool Members) Describe(XElement p, int limit = 1000)
    {
        string type = (string?)p.Attribute("type") ?? "";
        string cls = (string?)p.Attribute("classname") ?? "";
        bool children = (string?)p.Attribute("children") == "1";
        int.TryParse((string?)p.Attribute("numchildren"), out int count);
        string text = TextOf(p);
        switch (type)
        {
            case "string":
            {
                int.TryParse((string?)p.Attribute("size"), out int size);
                bool cut = size > text.Length || text.Length > limit;
                if (text.Length > limit) text = text[..limit];
                return ("\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + (cut ? "…" : "") + "\"", "string", false);
            }
            case "bool": return (text == "1" || text == "true" ? "true" : "false", "bool", false);
            case "null": return ("null", "null", false);
            case "int": case "float": return (text, type, false);
            case "array": return ($"array({count})", "array", children);
            case "object":
            {
                // an enum's case (Status::Active) and an object's class, as PhpStorm shows them: {App\Models\User}
                string facet = (string?)p.Attribute("facet") ?? "";
                if (facet.Contains("enum")) return (cls + (text != "" ? "::" + text : ""), cls, children);
                return ("{" + cls + "}", cls, children);
            }
            case "uninitialized": return ("(not set)", "uninitialized", false);
            case "resource": return (text != "" ? text : "resource", "resource", false);
            default: return (text, type, children);
        }
    }

    internal JsonObject Item(DbgpSession s, int depth, int context, XElement p, Ref? parent)
    {
        string name = (string?)p.Attribute("name") ?? "";
        string full = (string?)p.Attribute("fullname") ?? name;
        var (value, type, members) = Describe(p);
        if (parent != null) parent.Children[name] = full;
        int refId = members ? NewRef(new Ref(s, depth, context, full)) : 0;
        return new JsonObject { ["name"] = name, ["value"] = value, ["type"] = type, ["ref"] = refId };
    }

    // ------------------------------------------------------------ one PHP process or request

    sealed class DbgpException(string message) : Exception(message);

    /// <summary>One connection of Xdebug: a PHP process, or a request of PHP's server.</summary>
    internal sealed class DbgpSession
    {
        readonly XdebugBridge _bridge;
        readonly TcpClient _client;
        readonly NetworkStream _stream;
        readonly object _write = new();
        int _tx = 1;
        readonly Dictionary<int, TaskCompletionSource<XElement>> _pending = new();
        readonly Dictionary<string, List<string>> _lineIds = new(StringComparer.OrdinalIgnoreCase);   // file -> Xdebug's ids of its breakpoints
        readonly List<string> _exceptionIds = new();
        readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
        bool _exceptionsChanged;
        readonly Dictionary<string, int> _hits = new();
        List<(int Depth, JsonObject Frame)> _frames = new();
        string _reason = "breakpoint";
        string? _message;
        volatile bool _stopped;
        (string File, int Line)? _runTo;   // Run to Cursor's place, whose stop is wanted though no breakpoint of PyMagic's is there

        public string Label { get; private set; } = "PHP";
        public bool IsStopped => _stopped;
        /// <summary>The process id of the PHP process (Xdebug's appid), whose control socket Pause uses.</summary>
        public string Pid { get; private set; } = "";
        /// <summary>A request of PHP's server (artisan serve's server.php), not a program of its own.</summary>
        public bool IsRequest { get; private set; }
        /// <summary>Pause was asked for: the next break is the pause's, wherever it is.</summary>
        public volatile bool Pausing;

        public DbgpSession(XdebugBridge bridge, TcpClient client)
        {
            _bridge = bridge;
            _client = client;
            _stream = client.GetStream();
        }

        public async Task Begin()
        {
            try
            {
                var init = ReadPacket();
                if (init == null || init.Name.LocalName != "init") { Close(); return; }
                string? file = _bridge.FileOf((string?)init.Attribute("fileuri"));
                string pid = (string?)init.Attribute("appid") ?? "";
                Pid = pid;
                IsRequest = file != null && Path.GetFileName(file).Equals("server.php", StringComparison.OrdinalIgnoreCase);
                Label = file == null ? $"PHP {pid}"
                    // a request of PHP's server (artisan serve's): its server.php is the same for all of them
                    : Path.GetFileName(file).Equals("server.php", StringComparison.OrdinalIgnoreCase) ? $"request {pid}"
                    : $"{Path.GetFileName(file)} ({pid})";
                _ = Task.Run(ReadLoop);
                await Command("feature_set", "-n max_children -v 100");
                await Command("feature_set", "-n max_data -v 2048");
                await Command("feature_set", "-n max_depth -v 1");
                await Command("feature_set", "-n extended_properties -v 0");
                List<string> files;
                lock (_bridge._gate) files = _bridge._breakpoints.Values.Select(b => b.File).ToList();
                foreach (var f in files) await SetLineBreakpoints(f);
                await SetExceptionBreakpoints();
                await Go("run");
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or DbgpException or InvalidOperationException) { Close(); }
        }

        // ---- the wire: <length>\0<xml>\0 from Xdebug, "command -i id args -- data\0" to it

        XElement? ReadPacket()
        {
            var len = new StringBuilder();
            int b;
            while ((b = _stream.ReadByte()) > 0) len.Append((char)b);
            if (b < 0) return null;
            if (!int.TryParse(len.ToString(), out int n)) return null;
            var buf = new byte[n];
            int read = 0;
            while (read < n)
            {
                int got = _stream.Read(buf, read, n - read);
                if (got <= 0) return null;
                read += got;
            }
            _stream.ReadByte();   // the \0 after it
            return XDocument.Parse(Encoding.UTF8.GetString(buf)).Root;
        }

        void ReadLoop()
        {
            try
            {
                XElement? packet;
                while ((packet = ReadPacket()) != null)
                {
                    if (packet.Name.LocalName != "response") continue;   // stream and notify packets
                    if (!int.TryParse((string?)packet.Attribute("transaction_id"), out int tx)) continue;
                    TaskCompletionSource<XElement>? waiter;
                    lock (_pending) { _pending.Remove(tx, out waiter); }
                    waiter?.TrySetResult(packet);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or System.Xml.XmlException) { }
            Close();
        }

        bool _closed;
        void Close()
        {
            lock (_pending)
            {
                if (_closed) return;
                _closed = true;
                foreach (var w in _pending.Values) w.TrySetException(new IOException("The PHP program has ended."));
                _pending.Clear();
            }
            _stopped = false;
            try { _client.Dispose(); } catch (SocketException) { }
            _bridge.Ended(this);
        }

        public void Detach()
        {
            try { _ = Command("detach", ""); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }

        public Task<XElement> Command(string command, string args, string? data = null)
        {
            var tcs = new TaskCompletionSource<XElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            int tx;
            lock (_pending)
            {
                if (_closed) throw new IOException("The PHP program has ended.");
                tx = _tx++;
                _pending[tx] = tcs;
            }
            string line = $"{command} -i {tx}" + (args != "" ? " " + args : "") + (data != null ? " -- " + B64(data) : "");
            var bytes = Encoding.UTF8.GetBytes(line + "\0");
            lock (_write) { _stream.Write(bytes); _stream.Flush(); }
            return tcs.Task;
        }

        /// <summary>A command's answer, its error raised as one.</summary>
        async Task<XElement> Ask(string command, string args, string? data = null)
        {
            var r = await Command(command, args, data);
            var error = r.Elements().FirstOrDefault(e => e.Name.LocalName == "error");
            if (error != null)
            {
                string text = error.Elements().FirstOrDefault(e => e.Name.LocalName == "message")?.Value ?? $"error {(string?)error.Attribute("code")}";
                throw new DbgpException(text);
            }
            return r;
        }

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---- breakpoints

        public void BreakpointsChanged(string file)
        {
            lock (_changed) _changed.Add(file);
            if (_stopped) _ = ApplyChanges();
        }

        public void ExceptionsChanged()
        {
            _exceptionsChanged = true;
            if (_stopped) _ = ApplyChanges();
        }

        async Task ApplyChanges()
        {
            List<string> files;
            lock (_changed) { files = _changed.ToList(); _changed.Clear(); }
            try
            {
                foreach (var f in files) await SetLineBreakpoints(f);
                if (_exceptionsChanged) { _exceptionsChanged = false; await SetExceptionBreakpoints(); }
            }
            catch (Exception e) when (e is IOException or DbgpException or ObjectDisposedException) { }
        }

        async Task SetLineBreakpoints(string file)
        {
            if (_lineIds.Remove(file, out var old))
                foreach (var id in old) { try { await Ask("breakpoint_remove", $"-d {id}"); } catch (DbgpException) { } }
            List<JsonObject> specs;
            lock (_bridge._gate) specs = _bridge._breakpoints.TryGetValue(Canonical(file), out var b) ? b.Specs.ToList() : new();
            var ids = new List<string>();
            // a Blade template runs as the files Laravel compiled it into: its breakpoints are set there (none until it is compiled)
            var places = BladeViews.IsTemplate(file) ? _bridge.CompiledOf(file) : new List<string> { file };
            foreach (var spec in specs)
            foreach (var place in places)
            {
                int line = Num(spec["line"]);
                if (line <= 0) continue;
                if (!ReferenceEquals(place, file)) line = BladeViews.ToCompiled(place, line);
                string? condition = spec["condition"]?.GetValue<string>()?.Trim();
                string uri = Quote(_bridge.UriOf(place));
                try
                {
                    var r = !string.IsNullOrEmpty(condition)
                        ? await Ask("breakpoint_set", $"-t conditional -f {uri} -n {line}", condition)
                        : await Ask("breakpoint_set", $"-t line -f {uri} -n {line}");
                    if ((string?)r.Attribute("id") is { } id) ids.Add(id);
                }
                catch (DbgpException) { }
            }
            _lineIds[file] = ids;
        }

        async Task SetExceptionBreakpoints()
        {
            foreach (var id in _exceptionIds) { try { await Ask("breakpoint_remove", $"-d {id}"); } catch (DbgpException) { } }
            _exceptionIds.Clear();
            string mode;
            List<string> classes;
            lock (_bridge._gate) { mode = _bridge._exceptionMode; classes = _bridge._exceptionClasses.ToList(); }
            // Xdebug stops where an exception is thrown; a stop for an uncaught one alone it has not (Laravel's handler catches them all anyway)
            if (mode != "raised") return;
            foreach (var cls in classes.Count > 0 ? classes : new List<string> { "*" })
            {
                try
                {
                    var r = await Ask("breakpoint_set", $"-t exception -x {Quote(cls)}");
                    if ((string?)r.Attribute("id") is { } id) _exceptionIds.Add(id);
                }
                catch (DbgpException) { }
            }
        }

        /// <summary>The spec of a line breakpoint of PyMagic's at a place, or null.</summary>
        JsonObject? SpecAt(string? file, int line)
        {
            if (file == null) return null;
            lock (_bridge._gate)
                return _bridge._breakpoints.TryGetValue(Canonical(file), out var b) ? b.Specs.FirstOrDefault(s => Num(s["line"]) == line) : null;
        }

        // ---- running and stopping

        public Task Resume(string command)
        {
            if (!_stopped) return Task.CompletedTask;
            _stopped = false;
            _bridge.Resumed(this);
            return Go(command);
        }

        public async Task RunTo(string file, int line)
        {
            if (!_stopped) return;
            string path = Path.GetFullPath(Path.Combine(_bridge._root, file));
            _runTo = (path, line);
            // in a Blade template: in each file Laravel compiled it into
            var places = BladeViews.IsTemplate(path) ? _bridge.CompiledOf(path) : new List<string> { path };
            foreach (var place in places)
            {
                int atLine = ReferenceEquals(place, path) ? line : BladeViews.ToCompiled(place, line);
                try { await Ask("breakpoint_set", $"-t line -f {Quote(_bridge.UriOf(place))} -n {atLine} -r 1"); }
                catch (Exception e) when (e is DbgpException or IOException) { }
            }
            await Resume("run");
        }

        /// <summary>Runs or steps until Xdebug stops the program again, then shows the stop (or goes on, for a stop PyMagic does not want).</summary>
        async Task Go(string command)
        {
            try
            {
                string last = command;
                for (int guard = 0; guard < 200; guard++)
                {
                    var r = await Command(last, "");
                    string status = (string?)r.Attribute("status") ?? "";
                    if (status != "break") { Detach(); return; }   // stopping: the script has ended, and is let end
                    if (_changed.Count > 0 || _exceptionsChanged) await ApplyChanges();
                    var message = r.Elements().FirstOrDefault(e => e.Name.LocalName == "message");
                    string? exception = (string?)message?.Attribute("exception");
                    var stack = await Ask("stack_get", "");
                    var levels = stack.Elements().Where(e => e.Name.LocalName == "stack").ToList();
                    string? topFile = _bridge.FileOf((string?)levels.FirstOrDefault()?.Attribute("filename"));
                    int topLine = int.TryParse((string?)levels.FirstOrDefault()?.Attribute("lineno"), out var tl) ? tl : 0;
                    // a compiled Blade view: the place in its template, where its breakpoints are
                    if (topFile != null) (topFile, topLine) = BladeViews.ToTemplate(topFile, topLine);

                    if (Pausing && exception == null)
                    {
                        // Pause's break: where PHP was, whatever the line
                        Pausing = false;
                        _runTo = null;
                        _reason = "pause";
                        _message = null;
                    }
                    else if (exception != null)
                    {
                        // as "only my code": an exception thrown and caught inside the libraries, with none of the project's code on the stack
                        bool mine = levels.Any(l => _bridge.FileOf((string?)l.Attribute("filename")) is { } f && !_bridge.IsLibrary(f));
                        if (!mine) { last = "run"; continue; }
                        _reason = "exception";
                        _message = exception + (message!.Value is { Length: > 0 } m ? ": " + m.Trim() : "");
                    }
                    else if (command == "run")
                    {
                        var spec = SpecAt(topFile, topLine);
                        bool runTo = _runTo is { } to && topFile != null && Canonical(topFile).Equals(Canonical(to.File), StringComparison.OrdinalIgnoreCase) && topLine == to.Line;
                        _runTo = null;
                        // a breakpoint taken out meanwhile, a hit count not reached, a log message, or breakpoints muted: on it goes
                        if (spec == null && !runTo) { last = "run"; continue; }
                        if (_bridge._muted && !runTo) { last = "run"; continue; }
                        if (spec != null && !await Wanted(spec, topFile!, topLine)) { last = "run"; continue; }
                        _reason = "breakpoint";
                        _message = null;
                    }
                    else
                    {
                        // a step into the libraries: out of them again, to the project's code that called them; a step out to them: on to the
                        // next breakpoint (Laravel's own code runs the rest of the request)
                        if (_bridge.IsLibrary(topFile) && levels.Any(l => !_bridge.IsLibrary(_bridge.FileOf((string?)l.Attribute("filename")))))
                        {
                            if (command == "step_into" && last != "run") { last = "step_out"; continue; }
                            last = "run";
                            command = "run";
                            continue;
                        }
                        _reason = "step";
                        _message = null;
                    }
                    _frames = Frames(levels);
                    _stopped = true;
                    _bridge.Stopped(this);
                    return;
                }
                await Command("run", "");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or DbgpException or InvalidOperationException) { }
        }

        /// <summary>Whether a breakpoint's stop is wanted: its hit count reached; a log message is written instead of stopping.</summary>
        async Task<bool> Wanted(JsonObject spec, string file, int line)
        {
            string key = Canonical(file) + ":" + line;
            int hits = _hits[key] = (_hits.TryGetValue(key, out var h) ? h : 0) + 1;
            if (spec["hitCondition"]?.GetValue<string>() is { Length: > 0 } hc)
            {
                var m = Regex.Match(hc, @"^\s*(==|>=|<=|>|<|%)?\s*(\d+)\s*$");
                if (m.Success)
                {
                    int n = int.Parse(m.Groups[2].Value);
                    bool ok = (m.Groups[1].Success ? m.Groups[1].Value : "==") switch
                    {
                        ">=" => hits >= n, "<=" => hits <= n, ">" => hits > n, "<" => hits < n, "%" => n > 0 && hits % n == 0, _ => hits == n,
                    };
                    if (!ok) return false;
                }
            }
            if (spec["logMessage"]?.GetValue<string>() is { Length: > 0 } log)
            {
                var text = new StringBuilder();
                int at = 0;
                foreach (Match m in Regex.Matches(log, @"\{([^{}]+)\}"))
                {
                    text.Append(log, at, m.Index - at);
                    try
                    {
                        var r = await Ask("eval", "", m.Groups[1].Value);
                        var p = r.Elements().FirstOrDefault(e => e.Name.LocalName == "property");
                        if (p != null)
                        {
                            var (v, t, _) = _bridge.Describe(p);
                            text.Append(t == "string" ? TextOf(p) : v);
                        }
                    }
                    catch (DbgpException e) { text.Append('<').Append(e.Message).Append('>'); }
                    at = m.Index + m.Length;
                }
                text.Append(log, at, log.Length - at);
                _bridge.Say(text + "\n");
                return false;
            }
            return true;
        }

        List<(int Depth, JsonObject Frame)> Frames(List<XElement> levels)
        {
            var frames = new List<(int, JsonObject)>();
            foreach (var l in levels)
            {
                int depth = int.TryParse((string?)l.Attribute("level"), out var d) ? d : frames.Count;
                string? file = _bridge.FileOf((string?)l.Attribute("filename"));
                if (file == null) continue;
                if (_bridge.IsLibrary(file) && frames.Count > 0) continue;
                int line = int.TryParse((string?)l.Attribute("lineno"), out var n) ? n : 0;
                string where = (string?)l.Attribute("where") ?? "";
                // a compiled Blade view is shown as its template
                var (shownFile, shownLine) = BladeViews.ToTemplate(file, line);
                if (!ReferenceEquals(shownFile, file) && shownFile != file) where = Path.GetFileName(shownFile);
                frames.Add((depth, new JsonObject { ["id"] = frames.Count, ["name"] = where == "{main}" ? "(main)" : where, ["file"] = shownFile, ["line"] = shownLine, ["first"] = shownLine }));
            }
            return frames;
        }

        public void Show()
        {
            var frames = new JsonArray(_frames.Select(f => (JsonNode)f.Frame.DeepClone()).ToArray());
            _bridge.Send(new JsonObject
            {
                ["type"] = "stopped", ["reason"] = _reason, ["frames"] = frames, ["message"] = _message, ["thread"] = Label, ["suspended"] = "thread",
                ["threads"] = new JsonArray(),
            });
        }

        int DepthOf(int frameId)
        {
            if (!_stopped) throw new InvalidOperationException("The program is not stopped.");
            if (frameId < 0 || frameId >= _frames.Count) throw new InvalidOperationException("That frame is gone; the program has moved on.");
            return _frames[frameId].Depth;
        }

        // ---- values

        public async Task<JsonArray> Scopes(int frameId)
        {
            int depth = DepthOf(frameId);
            var r = await Ask("context_names", $"-d {depth}");
            var result = new JsonArray();
            foreach (var c in r.Elements().Where(e => e.Name.LocalName == "context"))
            {
                int id = int.TryParse((string?)c.Attribute("id"), out var i) ? i : 0;
                string name = (string?)c.Attribute("name") ?? "";
                name = name == "Locals" ? "Locals" : name == "Superglobals" ? "Superglobals" : name.Contains("onstant") ? "Constants" : name;
                result.Add(new JsonObject { ["name"] = name, ["ref"] = _bridge.NewRef(new Ref(this, depth, id, null)) });
            }
            return result;
        }

        public async Task<JsonArray> Members(Ref r)
        {
            var items = new JsonArray();
            if (r.FullName == null)
            {
                var c = await Ask("context_get", $"-d {r.Depth} -c {r.Context}");
                foreach (var p in c.Elements().Where(e => e.Name.LocalName == "property")) items.Add(_bridge.Item(this, r.Depth, r.Context, p, r));
                return items;
            }
            // a value's members, a page (100) at a time, to 500
            for (int page = 0; page < 5; page++)
            {
                var res = await Ask("property_get", $"-d {r.Depth} -c {r.Context} -n {Quote(r.FullName)} -p {page}");
                var top = res.Elements().FirstOrDefault(e => e.Name.LocalName == "property");
                if (top == null) break;
                var children = top.Elements().Where(e => e.Name.LocalName == "property").ToList();
                foreach (var p in children) items.Add(_bridge.Item(this, r.Depth, r.Context, p, r));
                int.TryParse((string?)top.Attribute("numchildren"), out int total);
                if (children.Count == 0 || items.Count >= total) break;
            }
            return items;
        }

        static readonly Regex Chain = new(@"^\$[A-Za-z_]\w*((\??->[A-Za-z_]\w*)|\[('[^']*'|""[^""]*""|\d+)\])*$");

        /// <summary>An expression's value: a variable and its members read as they are; anything else run by PHP (in the innermost frame).</summary>
        public async Task<JsonObject> Evaluate(int frameId, string expression, bool evalOnly, bool detail)
        {
            int depth = DepthOf(frameId);
            expression = expression.Trim().TrimEnd(';');
            XElement? p = null;
            if (Chain.IsMatch(expression.Replace("?->", "->")))
            {
                string name = expression.Replace("?->", "->");
                foreach (int ctx in new[] { 0, 1 })
                {
                    try
                    {
                        var r = await Ask("property_get", $"-d {depth} -c {ctx} -n {Quote(name)}" + (detail ? " -m 0" : ""));
                        p = r.Elements().FirstOrDefault(e => e.Name.LocalName == "property");
                        if (p != null) return Result(p, depth, ctx, detail);
                    }
                    catch (DbgpException) { }
                }
                if (evalOnly) return new JsonObject { ["error"] = $"{expression} is not set here." };
            }
            if (evalOnly) return new JsonObject { ["error"] = "This expression may change the program; evaluate it in the Evaluate window (Alt+F8)." };
            if (frameId != 0) return new JsonObject { ["error"] = "Xdebug runs expressions in the innermost frame only: select it, or name a variable of this frame." };
            var e = await Ask("eval", "", expression);
            p = e.Elements().FirstOrDefault(x => x.Name.LocalName == "property");
            if (p == null) return new JsonObject { ["result"] = "null", ["valueType"] = "null", ["ref"] = 0, ["output"] = "" };
            return Result(p, depth, 0, detail);
        }

        JsonObject Result(XElement p, int depth, int ctx, bool detail)
        {
            var (value, type, members) = _bridge.Describe(p, detail ? int.MaxValue : 1000);
            string full = (string?)p.Attribute("fullname") ?? "";
            int refId = members && full != "" ? _bridge.NewRef(new Ref(this, depth, ctx, full)) : 0;
            var o = new JsonObject { ["result"] = value, ["valueType"] = type, ["ref"] = refId, ["output"] = "" };
            if (detail && type == "string") o["text"] = TextOf(p);
            return o;
        }

        public async Task<JsonObject> Set(int depth, int context, string fullName, string name, string expression)
        {
            try
            {
                // without a type, Xdebug runs the value as PHP: `$a['k'] = <expression>`
                var r = await Ask("property_set", $"-d {depth} -c {context} -n {Quote(fullName)}", expression);
                if ((string?)r.Attribute("success") == "0") return new JsonObject { ["error"] = $"{fullName} could not be set to {expression}." };
                var g = await Ask("property_get", $"-d {depth} -c {context} -n {Quote(fullName)}");
                var p = g.Elements().FirstOrDefault(e => e.Name.LocalName == "property");
                if (p == null) return new JsonObject { ["name"] = name, ["value"] = expression, ["valueType"] = "", ["ref"] = 0 };
                var (value, type, members) = _bridge.Describe(p);
                return new JsonObject { ["name"] = name, ["value"] = value, ["valueType"] = type, ["ref"] = members ? _bridge.NewRef(new Ref(this, depth, context, fullName)) : 0 };
            }
            catch (DbgpException e) { return new JsonObject { ["error"] = e.Message }; }
        }

        public async Task<JsonObject> SetName(int frameId, string name, string expression)
        {
            int depth = DepthOf(frameId);
            string variable = name.StartsWith('$') ? name : "$" + name;
            if (!Regex.IsMatch(variable, @"^\$[A-Za-z_]\w*$")) return new JsonObject { ["error"] = $"{name} is not a name." };
            var r = await Set(depth, 0, variable, name, expression);
            if (r["error"] == null) r.Remove("ref");
            return r;
        }

        /// <summary>The short values of the frame's variables by name (with or without their $), for the values at the ends of the editor's lines.</summary>
        public async Task<JsonObject> Values(int frameId, List<string> names)
        {
            int depth = DepthOf(frameId);
            var c = await Ask("context_get", $"-d {depth} -c 0");
            var have = new Dictionary<string, XElement>();
            foreach (var p in c.Elements().Where(e => e.Name.LocalName == "property"))
                if ((string?)p.Attribute("name") is { } n) have[n] = p;
            var found = new JsonObject();
            foreach (var n in names.Distinct())
            {
                if (!have.TryGetValue(n.StartsWith('$') ? n : "$" + n, out var p)) continue;
                if ((string?)p.Attribute("type") == "uninitialized") continue;
                var (value, _, _) = _bridge.Describe(p, 60);
                found[n] = value;
            }
            return found;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _xdebug.Stop(); } catch (SocketException) { }
        _views?.Dispose();
        List<TcpClient> early;
        lock (_gate) { early = _early.ToList(); _early.Clear(); }
        foreach (var c in early) c.Dispose();
        lock (_gate)
        {
            try { _channelClient?.Dispose(); } catch (SocketException) { }
            _channel = null;
        }
    }
}
