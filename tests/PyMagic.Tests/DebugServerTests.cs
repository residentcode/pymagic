using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The debug server: the IDE listens and a real Python program started on its own connects back (skipped when no Python is on the PATH).</summary>
public class DebugServerTests
{
    static readonly bool HavePython = Probe();

    static bool Probe()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    sealed class Rig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_dbs_" + Guid.NewGuid().ToString("N"));
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        readonly List<Process> _programs = new();
        int _id;

        public Rig() { Directory.CreateDirectory(Root); Session = new RunSession(Events.Add); }

        public JsonNode Next(Func<JsonNode, bool> match, int timeoutMs = 20000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Events.TryTake(out var e, left))
                    throw new AssertionException("timed out; saw: " + string.Join(" ", Seen.Select(s => s.ToJsonString())));
                Seen.Add(e);
                if (match(e)) return e;
            }
        }

        public JsonNode Type(string type) => Next(e => e["type"]?.GetValue<string>() == type);

        public JsonNode Evaluate(string expression)
        {
            int id = ++_id;
            Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = id, ["frameId"] = 0, ["expression"] = expression });
            return Next(e => e["type"]?.GetValue<string>() == "evaluated" && e["id"]?.GetValue<int>() == id);
        }

        /// <summary>Starts a program the way a user does in a shell of their own: nothing of the IDE but the snippet.</summary>
        public Process Program(string dir, string file, string text)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), text.Replace("\r\n", "\n"));
            var psi = new ProcessStartInfo("python", "-u " + file) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var p = Process.Start(psi)!;
            _programs.Add(p);
            return p;
        }

        public void Dispose()
        {
            Session.Dispose();
            foreach (var p in _programs) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    // the program of the user: the snippet the IDE showed (three lines), then its own work
    static string Body(string snippet, string work) => snippet.Replace("\r\n", "\n") + "\n" + work;

    // lines 4 to 8 once the three lines of the snippet are above it
    const string Work = "def total(items):\n    s = 0\n    for i in items:\n        s += i\n    return s\n\nresult = total([1, 2, 3])\nprint('result', result)\n";

    static int Line(JsonNode stopped) => stopped["frames"]![0]!["line"]!.GetValue<int>();

    static string PyPath(string p) => "'" + p.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    [Fact]
    public void AProgramStartedElsewhereConnectsBackAndStopsAtTheIdesBreakpoint()
    {
        if (!HavePython) return;
        using var r = new Rig();
        // the same file names on both sides: no mapping
        r.Session.Listen(r.Root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { ["prog.py"] = new JsonArray(7) });
        var listening = r.Type("listening");
        string snippet = listening["snippet"]!.GetValue<string>();
        Assert.Contains("pm_debug.connect('127.0.0.1', " + listening["port"], snippet);
        Assert.Contains(listening["token"]!.GetValue<string>(), snippet);
        // the user copies pm_debug.py beside the program (the snippet says where it is)
        File.Copy(Path.Combine(listening["helper"]!.GetValue<string>(), "pm_debug.py"), Path.Combine(r.Root, "pm_debug.py"));
        var program = r.Program(r.Root, "prog.py", Body(snippet, Work));

        Assert.Contains("127.0.0.1", r.Type("connected")["from"]!.GetValue<string>());
        var stopped = r.Type("stopped");
        Assert.Equal(7, Line(stopped));
        Assert.Equal("total", stopped["frames"]![0]!["name"]!.GetValue<string>());
        Assert.True(stopped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("prog.py"));
        Assert.Equal("0", r.Evaluate("s")["result"]!.GetValue<string>());
        // stepping works like any session
        r.Session.Handle(new JsonObject { ["type"] = "next" });
        Assert.Equal(6, Line(r.Type("stopped")));
        r.Session.Handle(new JsonObject { ["type"] = "continue" });
        Assert.Equal(7, Line(r.Type("stopped")));   // the loop's second round
        Assert.Equal("1", r.Evaluate("s")["result"]!.GetValue<string>());
        r.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = "prog.py", ["lines"] = new JsonArray() });
        r.Session.Handle(new JsonObject { ["type"] = "continue" });
        Assert.True(program.WaitForExit(15000));
        Assert.Contains("result 6", program.StandardOutput.ReadToEnd());
        r.Type("disconnected");

        // a second program connects to the same server afterwards, and gets the breakpoints as they are now (none)
        var second = r.Program(r.Root, "prog.py", Body(snippet, Work));
        r.Type("connected");
        Assert.True(second.WaitForExit(15000));
        Assert.Contains("result 6", second.StandardOutput.ReadToEnd());
        r.Type("disconnected");
        r.Session.Handle(new JsonObject { ["type"] = "stop" });
        Assert.True(r.Type("exited")["detached"]!.GetValue<bool>());
    }

    [Fact]
    public void StopDetachesFromAConnectedProgramWhichGoesOn()
    {
        if (!HavePython) return;
        using var r = new Rig();
        r.Session.Listen(r.Root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { ["prog.py"] = new JsonArray(7) });
        var listening = r.Type("listening");
        File.Copy(Path.Combine(listening["helper"]!.GetValue<string>(), "pm_debug.py"), Path.Combine(r.Root, "pm_debug.py"));
        var program = r.Program(r.Root, "prog.py", Body(listening["snippet"]!.GetValue<string>(), Work));
        Assert.Equal(7, Line(r.Type("stopped")));
        r.Session.Handle(new JsonObject { ["type"] = "stop" });
        Assert.True(r.Type("exited")["detached"]!.GetValue<bool>());
        Assert.True(program.WaitForExit(15000), "the program runs on to its end after the IDE detached");
        Assert.Contains("result 6", program.StandardOutput.ReadToEnd());
    }

    [Fact]
    public void ANewConnectionNeedsTheTokenAndFilesAreMappedBothWays()
    {
        if (!HavePython) return;
        using var r = new Rig();
        // "their" machine: a copy of the project in another folder; the mapping says which folder is which
        string theirs = Path.Combine(r.Root, "remote_copy");
        r.Session.Listen(r.Root, "127.0.0.1", 0, "", new[] { (Path.Combine(r.Root, "src"), theirs) }, new Dictionary<string, JsonArray> { ["src/prog.py"] = new JsonArray(8) });
        var listening = r.Type("listening");
        int port = listening["port"]!.GetValue<int>();
        Assert.Equal(1, listening["mapped"]!.GetValue<int>());
        Assert.Contains("remote_copy", listening["snippet"]!.GetValue<string>());
        string helper = listening["helper"]!.GetValue<string>();
        string head = "import sys\nsys.path.insert(0, " + PyPath(helper) + ")\nimport pm_debug\n";

        // a program with the wrong token is turned away, and the server goes on listening
        var wrong = r.Program(Path.Combine(r.Root, "w"), "w.py", head + "pm_debug.connect('127.0.0.1', " + port + ", 'nope')\nprint('x')\n");
        var refused = r.Next(e => e["type"]?.GetValue<string>() == "output" && e["text"]!.GetValue<string>().Contains("refused"));
        Assert.Contains("token", refused["text"]!.GetValue<string>());
        wrong.Kill(true);

        var program = r.Program(theirs, "prog.py", head + "pm_debug.connect('127.0.0.1', " + port + ", '" + listening["token"] + "', root=" + PyPath(theirs) + ")\n" + Work);
        r.Type("connected");
        // the breakpoint was set on src/prog.py of this computer; the program has the file as remote_copy/prog.py
        var stopped = r.Type("stopped");
        Assert.Equal(8, Line(stopped));
        Assert.Equal(Path.Combine(r.Root, "src", "prog.py"), stopped["frames"]![0]!["file"]!.GetValue<string>());
        r.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = "src/prog.py", ["lines"] = new JsonArray() });
        r.Session.Handle(new JsonObject { ["type"] = "continue" });
        Assert.True(program.WaitForExit(15000));
    }

    [Fact]
    public void ADebugServerThatCannotListenSaysSo()
    {
        using var r = new Rig();
        r.Session.Listen(r.Root, "not-an-address", 5678, "", Array.Empty<(string, string)>(), null);
        Assert.Contains("not an address", r.Type("error")["message"]!.GetValue<string>());
        using var busy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        busy.Start();
        int port = ((System.Net.IPEndPoint)busy.LocalEndpoint).Port;
        using var r2 = new Rig();
        r2.Session.Listen(r2.Root, "127.0.0.1", port, "", Array.Empty<(string, string)>(), null);
        Assert.Contains("cannot listen", r2.Type("error")["message"]!.GetValue<string>());
    }

    [Fact]
    public void PathMappingsGoByTheLongestFolderAndKeepTheOtherSidesSeparators()
    {
        var map = new PathMap(new[] { new PathMap.Entry(@"C:\proj", "/app"), new PathMap.Entry(@"C:\proj\vendor", "/opt/vendor") });
        Assert.Equal("/app/src/a.py", map.ToRemote(@"C:\proj\src\a.py"));
        Assert.Equal("/opt/vendor/x.py", map.ToRemote(@"C:\proj\vendor\x.py"));
        Assert.Equal(@"C:\proj\src\a.py", map.ToLocal("/app/src/a.py"));
        Assert.Equal(@"C:\proj\vendor\x.py", map.ToLocal("/opt/vendor/x.py"));
        Assert.Equal("/elsewhere/a.py", map.ToLocal("/elsewhere/a.py"));          // outside every mapping: unchanged
        Assert.Equal("/applications/a.py", map.ToLocal("/applications/a.py"));    // a folder with the same start is not inside
        Assert.Equal("/app/src/a.py", map.ToRemote(@"c:\PROJ\src\a.py"));         // a Windows path ignores case
        Assert.Equal(@"C:\proj", map.ToLocal("/app"));
        var whole = new PathMap(new[] { new PathMap.Entry(@"C:\proj", "/") });
        Assert.Equal(@"C:\proj\app\a.py", whole.ToLocal("/app/a.py"));
        Assert.Equal("/app/a.py", whole.ToRemote(@"C:\proj\app\a.py"));
        var relative = PathMap.Parse(@"C:\root", new[] { ("src", "/app/src"), ("", "/app"), (".", "/x"), (@"D:\other", "/o"), ("lib", "") });
        Assert.Equal(@"C:\root\src|C:\root|C:\root|D:\other", string.Join("|", relative.Entries.Select(e => e.Local)));
    }
}
