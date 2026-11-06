using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// Attach Debugger of a Python console whose interpreter is elsewhere than this computer (SSH, WSL, Docker): the debug server of the Run panel
/// listens on this computer, which the console there cannot reach, so the debugger talks over the console's own connection instead. What the
/// debugger sends comes as <c>debugOut</c> messages of the console and goes to the debug server; what the debug server sends goes back to the
/// console as <c>debugIn</c>. The files named on the way are those of the console's machine on its side and of this computer on the other, as the
/// interpreter's target maps them (the project's folder there and here). <c>pm_debug.py</c> itself is sent with the request, since the folder this
/// computer extracted it to is not on that machine.
/// </summary>
public sealed partial class RunSession
{
    ConsoleDebugRelay? _consoleRelay;

    /// <summary>Attach Debugger goes over the console's own connection: the interpreter is elsewhere (or a test asks for it on this computer).</summary>
    public bool ConsoleDebugOverChannel { get => _consoleDebugOverChannel ?? !Target.IsLocal; set => _consoleDebugOverChannel = value; }
    bool? _consoleDebugOverChannel;

    /// <summary>Called with <c>_gate</c> held, for a consoleAttach on its way to the console: the relay to the debug server, and the debugger's text.</summary>
    void PrepareConsoleAttachLocked(JsonNode msg)
    {
        _consoleRelay?.Dispose();
        _consoleRelay = null;
        if (!ConsoleDebugOverChannel) return;
        string host = msg["host"]?.GetValue<string>() ?? "127.0.0.1";
        int port = msg["port"] is JsonValue p && p.TryGetValue<int>(out var n) ? n : 0;
        _consoleRelay = new ConsoleDebugRelay(this, host, port);
        msg["via"] = "console";
        msg["helperSource"] = HelperText("pm_debug.py");
        msg.AsObject().Remove("helper");
    }

    static string HelperText(string name)
    {
        using var s = typeof(RunSession).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is missing from the assembly");
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>A message of the console for the relay (debugOut, debugEnd): true when it was one.</summary>
    bool TakeConsoleDebugMessage(JsonNode msg)
    {
        switch (msg["type"]?.GetValue<string>())
        {
            case "debugOut":
                _consoleRelay?.FromConsole(msg["text"]?.GetValue<string>() ?? "");
                return true;
            case "debugEnd":
                lock (_gate) { _consoleRelay?.Dispose(); _consoleRelay = null; }
                return true;
            default:
                return false;
        }
    }

    void EndConsoleRelay() { lock (_gate) { _consoleRelay?.Dispose(); _consoleRelay = null; } }

    /// <summary>Every "file" of a line of the debugger's protocol, changed; any other line as it is.</summary>
    internal static string RewriteFiles(string line, Func<string, string> change)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { return line; }
        if (node == null) return line;
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (key == "file" && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) o[key] = change(s);
                    else Walk(o[key]);
                }
            }
            else if (n is JsonArray a) foreach (var item in a) Walk(item);
        }
        Walk(node);
        return node.ToJsonString();
    }

    sealed class ConsoleDebugRelay : IDisposable
    {
        readonly RunSession _session;
        readonly TcpClient _tcp = new();
        readonly Task _connected;
        bool _ended;

        public ConsoleDebugRelay(RunSession session, string host, int port)
        {
            _session = session;
            _connected = Task.Run(async () =>
            {
                try
                {
                    await _tcp.ConnectAsync(host, port);
                    _ = Task.Run(ReadLoop);
                }
                catch (SocketException) { ToConsole(""); }
            });
        }

        // a line of the debug server: to the console, the files named as its machine has them
        void ReadLoop()
        {
            try
            {
                var reader = new StreamReader(_tcp.GetStream(), new UTF8Encoding(false));
                for (string? line; (line = reader.ReadLine()) != null;)
                    ToConsole(RewriteFiles(line, f => Path.IsPathRooted(f) && !f.StartsWith('/') ? _session.Target.ToTarget(f) : f) + "\n");
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            ToConsole("");   // the debug server is gone (Stop in the Run panel): the debugger in the console lets go
        }

        void ToConsole(string text)
        {
            lock (_session._gate)
            {
                if (_ended && text.Length > 0) return;
                if (_session._channel != null) _session.WriteRawLocked(new JsonObject { ["type"] = "debugIn", ["text"] = text }.ToJsonString());
            }
        }

        // what the debugger in the console sends: to the debug server, the files named as this computer has them
        public void FromConsole(string text)
        {
            try { _connected.Wait(15000); } catch (AggregateException) { return; }
            if (!_tcp.Connected) return;
            var sb = new StringBuilder();
            foreach (var line in text.Split('\n'))
                if (line.Length > 0) sb.Append(RewriteFiles(line, f => f.StartsWith('/') ? _session.Target.ToHost(f) : f)).Append('\n');
            try
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                lock (this) { _tcp.GetStream().Write(bytes); _tcp.GetStream().Flush(); }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        public void Dispose()
        {
            _ended = true;
            try { _tcp.Client?.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            _tcp.Dispose();
        }
    }
}
