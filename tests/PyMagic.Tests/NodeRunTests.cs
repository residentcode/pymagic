using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// JavaScript and TypeScript run and debugged by Node (RunSession.StartNode, pm_nodedebug.mjs): a file's output and exit code, and under the
/// debugger the same messages as Python's (a breakpoint's stop with its frames, scopes, variables to open, evaluate, Set Value, stepping,
/// conditions, log messages, hit counts, exceptions), an npm script, and the Node processes a program starts. Skipped without Node.
/// </summary>
public class NodeRunTests
{
    static readonly bool HaveNode = Probe();

    static bool Probe()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    sealed class Harness : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_node_" + Guid.NewGuid().ToString("N"));
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        int _id;

        public Harness() { Directory.CreateDirectory(Root); Session = new RunSession(Events.Add); }

        public void Script(string name, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, name))!);
            File.WriteAllText(Path.Combine(Root, name), text.Replace("\r\n", "\n"));
        }

        public JsonNode Next(Func<JsonNode, bool> match, int timeoutMs = 30000)
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

        public JsonNode Type(string type, int timeoutMs = 30000) => Next(e => e["type"]?.GetValue<string>() == type, timeoutMs);
        public string AllOutput() => string.Concat(Seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]!.GetValue<string>()));

        public JsonNode Request(string type, JsonObject body)
        {
            int id = ++_id;
            body["type"] = type; body["id"] = id;
            Session.Handle(body);
            string reply = type switch { "scopes" => "scopes", "variables" => "variables", "setVariable" => "variableSet", "values" => "values", "setName" => "setName", _ => "evaluated" };
            return Next(e => e["type"]?.GetValue<string>() == reply && e["id"]?.GetValue<int>() == id);
        }

        public void Command(string type) => Session.Handle(new JsonObject { ["type"] = type });

        public void Dispose()
        {
            Session.Dispose();
            for (int i = 0; i < 5; i++)
            {
                try { Directory.Delete(Root, true); return; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }

    static int Line(JsonNode stopped) => stopped["frames"]![0]!["line"]!.GetValue<int>();
    static Dictionary<string, JsonArray> Breaks(string file, params JsonNode[] lines) => new() { [file] = new JsonArray(lines) };

    [Fact]
    public void AJavaScriptFileRunsWithNode()
    {
        if (!HaveNode) return;
        using var h = new Harness();
        h.Script("hello.js", "const total = [1, 2, 3].reduce((a, b) => a + b, 0);\nconsole.log('total', total);\nconsole.error('to stderr');\nprocess.exit(3);\n");
        h.Session.StartNode(h.Root, "hello.js", null, null, false, Array.Empty<string>(), null);
        var exited = h.Type("exited");
        Assert.Equal(3, exited["code"]!.GetValue<int>());
        Assert.Contains("total 6", h.AllOutput());
        Assert.Contains("to stderr", h.AllOutput());
        Assert.True(!h.AllOutput().Contains("Debugger listening"), h.AllOutput());
    }

    const string Program = """
        const prices = [10, 20, 12];
        function total(items, tax) {
          let sum = 0;
          for (const p of items) sum += p;
          const withTax = sum * (1 + tax);
          return withTax;
        }
        const order = { id: 7, customer: { name: 'Ada', tags: ['vip'] }, lines: new Map([['a', 1]]) };
        const result = total(prices, 0.5);
        console.log('result', result, order.id);
        """;   // line 9: `const result = ...`; line 5: `const withTax = ...`

    [Fact]
    public void ABreakpointStopsAndTheValuesOpen()
    {
        if (!HaveNode) return;
        using var h = new Harness();
        h.Script("order.js", Program);
        h.Session.StartNode(h.Root, "order.js", null, null, true, Array.Empty<string>(), Breaks("order.js", 5));
        var stopped = h.Type("stopped");
        Assert.Equal("breakpoint", stopped["reason"]!.GetValue<string>());
        Assert.Equal(5, Line(stopped));
        Assert.Equal(Path.Combine(h.Root, "order.js").ToLowerInvariant(), stopped["frames"]![0]!["file"]!.GetValue<string>().ToLowerInvariant());
        Assert.Equal("total", stopped["frames"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(9, stopped["frames"]![1]!["line"]!.GetValue<int>());

        // the function's own names, and what the value of an expression is
        var scopes = h.Request("scopes", new JsonObject { ["frameId"] = 0 })["scopes"]!.AsArray();
        Assert.Equal("Locals", scopes[0]!["name"]!.GetValue<string>());
        var locals = h.Request("variables", new JsonObject { ["ref"] = scopes[0]!["ref"]!.GetValue<int>() })["items"]!.AsArray();
        string Of(JsonArray items, string name) { var v = items.First(i => i!["name"]!.GetValue<string>() == name)!; return $"{v["value"]} ({v["type"]})"; }
        Assert.Equal("42 (number)", Of(locals, "sum"));
        Assert.Equal("[10, 20, 12] (Array)", Of(locals, "items"));
        Assert.Equal("0.5 (number)", Of(locals, "tax"));
        var e = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "sum * 2", ["evalOnly"] = true });
        Assert.Equal("84", e["result"]!.GetValue<string>());

        // the caller's frame: an object opened as deep as it goes
        var order = h.Request("evaluate", new JsonObject { ["frameId"] = 1, ["expression"] = "order", ["evalOnly"] = true });
        Assert.Equal("{id: 7, customer: {…}, lines: Map(1)}", order["result"]!.GetValue<string>());
        var members = h.Request("variables", new JsonObject { ["ref"] = order["ref"]!.GetValue<int>() })["items"]!.AsArray();
        Assert.Equal("7 (number)", Of(members, "id"));
        var customer = members.First(m => m!["name"]!.GetValue<string>() == "customer")!;
        var inner = h.Request("variables", new JsonObject { ["ref"] = customer["ref"]!.GetValue<int>() })["items"]!.AsArray();
        Assert.Equal("\"Ada\" (string)", Of(inner, "name"));
        Assert.Equal("[\"vip\"] (Array)", Of(inner, "tags"));

        // Set Value of a local, and of a member
        var set = h.Request("setVariable", new JsonObject { ["ref"] = scopes[0]!["ref"]!.GetValue<int>(), ["frameId"] = 0, ["name"] = "sum", ["expression"] = "100" });
        Assert.True(set["error"] == null, set.ToJsonString());
        var setMember = h.Request("setVariable", new JsonObject { ["ref"] = customer["ref"]!.GetValue<int>(), ["frameId"] = 1, ["name"] = "name", ["expression"] = "'Grace'" });
        Assert.True(setMember["error"] == null, setMember.ToJsonString());
        Assert.Equal("\"Grace\"", h.Request("evaluate", new JsonObject { ["frameId"] = 1, ["expression"] = "order.customer.name" })["result"]!.GetValue<string>());

        // the values at the ends of the lines
        var values = h.Request("values", new JsonObject { ["frameId"] = 0, ["names"] = new JsonArray("sum", "tax", "nothing") })["values"]!.AsObject();
        Assert.Equal("100", values["sum"]!.GetValue<string>());
        Assert.True(!values.ContainsKey("nothing"), "a name the frame does not have");

        // a step over, out to the caller, and the end with the changed sum
        h.Command("next");
        var step = h.Type("stopped");
        Assert.Equal("step", step["reason"]!.GetValue<string>());
        Assert.Equal(6, Line(step));
        h.Command("stepOut");
        // V8 stops out in the caller at its next statement (the call's line is done), where Python stops on the call's line again
        Assert.Equal(10, Line(h.Type("stopped")));
        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("result 150 7", h.AllOutput());
    }

    [Fact]
    public void ConditionsLogMessagesAndHitCounts()
    {
        if (!HaveNode) return;
        using var h = new Harness();
        h.Script("loop.js", "let seen = 0;\nfor (let i = 0; i < 10; i++) {\n  seen += i;\n}\nconsole.log('seen', seen);\n");
        var lines = new JsonArray(
            new JsonObject { ["line"] = 3, ["condition"] = "i === 7" });
        h.Session.StartNode(h.Root, "loop.js", null, null, true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["loop.js"] = lines });
        var stopped = h.Type("stopped");
        Assert.Equal("7", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "i" })["result"]!.GetValue<string>());
        // the breakpoint becomes a log message: it writes and goes on
        h.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.Combine(h.Root, "loop.js"), ["lines"] = new JsonArray(new JsonObject { ["line"] = 3, ["logMessage"] = "i is {i}, seen {seen}" }) });
        Thread.Sleep(300);
        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("i is 8, seen 28", h.AllOutput());
        Assert.Contains("i is 9, seen 36", h.AllOutput());
        Assert.True(!h.AllOutput().Contains("i is 6"), "only after the condition's stop");

        // a hit count: the third time
        using var h2 = new Harness();
        h2.Script("loop.js", "let seen = 0;\nfor (let i = 0; i < 10; i++) {\n  seen += i;\n}\nconsole.log('seen', seen);\n");
        h2.Session.StartNode(h2.Root, "loop.js", null, null, true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["loop.js"] = new JsonArray(new JsonObject { ["line"] = 3, ["hitCondition"] = "== 3" }) });
        h2.Type("stopped");
        Assert.Equal("2", h2.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "i" })["result"]!.GetValue<string>());
        h2.Command("continue");
        Assert.Equal(0, h2.Type("exited")["code"]!.GetValue<int>());
    }

    [Fact]
    public void AnUncaughtExceptionStopsWithItsMessage()
    {
        if (!HaveNode) return;
        using var h = new Harness();
        h.Script("boom.js", "function parse(text) {\n  return JSON.parse(text);\n}\nparse('{not json');\n");
        h.Session.StartNode(h.Root, "boom.js", null, null, true, Array.Empty<string>(), null);
        var stopped = h.Type("stopped");
        Assert.Equal("exception", stopped["reason"]!.GetValue<string>());
        Assert.Contains("SyntaxError", stopped["message"]!.GetValue<string>());
        Assert.Equal(2, Line(stopped));
        h.Command("continue");
        Assert.True(h.Type("exited")["code"]!.GetValue<int>() != 0, "it ends with the error");
    }

    [Fact]
    public void AnNpmScriptAndTheNodeProcessesItStartsAreDebugged()
    {
        if (!HaveNode) return;
        using var h = new Harness();
        h.Script("package.json", "{ \"name\": \"probe\", \"version\": \"1.0.0\", \"scripts\": { \"start\": \"node server.js\" } }\n");
        // a program that starts another Node process, as a framework's dev server starts its workers
        h.Script("server.js", "const { fork } = require('node:child_process');\nconst worker = fork(__dirname + '/worker.js');\nworker.on('exit', (code) => { console.log('worker done', code); });\n");
        h.Script("worker.js", "const jobs = ['a', 'b'];\nconst count = jobs.length;\nconsole.log('jobs', count);\n");
        h.Session.StartNode(h.Root, null, "start", null, true, Array.Empty<string>(), Breaks("worker.js", 3));
        var stopped = h.Type("stopped", 60000);
        Assert.Equal(Path.Combine(h.Root, "worker.js").ToLowerInvariant(), stopped["frames"]![0]!["file"]!.GetValue<string>().ToLowerInvariant());
        Assert.Equal(3, Line(stopped));
        Assert.Contains("worker.js", stopped["thread"]!.GetValue<string>());
        Assert.Equal("2", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "count" })["result"]!.GetValue<string>());
        h.Command("continue");
        Assert.Equal(0, h.Type("exited", 60000)["code"]!.GetValue<int>());
        Assert.Contains("jobs 2", h.AllOutput());
        Assert.Contains("worker done 0", h.AllOutput());
        Assert.True(!h.AllOutput().Contains("Debugger listening") && !h.AllOutput().Contains("Debugger attached"), h.AllOutput());
    }

    /// <summary>esbuild of the page's packages (web/node_modules), to compile TypeScript with a source map as tsc does; null without it.</summary>
    static string? Esbuild()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "web", "node_modules", "esbuild");
            if (File.Exists(Path.Combine(candidate, "package.json"))) return candidate;
        }
        return null;
    }

    [Fact]
    public void ACompiledTypeScriptFileStopsOnTheLinesOfItsSourceThroughItsMap()
    {
        if (!HaveNode || Esbuild() is not { } esbuild) return;
        using var h = new Harness();
        h.Script("src/calc.ts", "export interface Line {\n  price: number;\n  qty: number;\n}\n\nexport function total(lines: Line[]): number {\n  let sum: number = 0;\n  for (const l of lines) {\n    sum += l.price * l.qty;\n  }\n  return sum;\n}\n\nconsole.log('total', total([{ price: 2, qty: 3 }, { price: 5, qty: 1 }]));\n");
        // compiled to dist/calc.js with dist/calc.js.map beside it, as tsc --sourceMap writes them (the types gone, the lines moved)
        var psi = new ProcessStartInfo("node") { WorkingDirectory = h.Root, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"require({System.Text.Json.JsonSerializer.Serialize(esbuild)}).buildSync({{ entryPoints: ['src/calc.ts'], outfile: 'dist/calc.js', sourcemap: true, format: 'cjs', platform: 'node' }})");
        using (var build = Process.Start(psi)!) { build.WaitForExit(60000); Assert.True(build.ExitCode == 0, build.StandardError.ReadToEnd()); }
        Assert.True(File.Exists(Path.Combine(h.Root, "dist", "calc.js.map")), "the map");

        h.Session.StartNode(h.Root, "dist/calc.js", null, null, true, Array.Empty<string>(), Breaks("src/calc.ts", 9));
        var stopped = h.Type("stopped");
        Assert.Equal(Path.Combine(h.Root, "src", "calc.ts").ToLowerInvariant(), stopped["frames"]![0]!["file"]!.GetValue<string>().ToLowerInvariant());
        Assert.Equal(9, Line(stopped));
        Assert.Equal("total", stopped["frames"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("0", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "sum" })["result"]!.GetValue<string>());
        // a step stays on the source's lines
        h.Command("next");
        var step = h.Type("stopped");
        Assert.Equal(Path.Combine(h.Root, "src", "calc.ts").ToLowerInvariant(), step["frames"]![0]!["file"]!.GetValue<string>().ToLowerInvariant());
        Assert.True(Line(step) is 8 or 9 or 11, "a line of calc.ts: " + Line(step));
        h.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.Combine(h.Root, "src", "calc.ts"), ["lines"] = new JsonArray() });
        Thread.Sleep(200);
        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("total 11", h.AllOutput());
    }

    /// <summary>
    /// Next.js's dev server under the debugger (npm run dev; Turbopack builds the route while it runs, its map names turbopack:///[project]/…):
    /// a request to an API route stops at a breakpoint of its route.ts. Runs only when PYMAGIC_TEST_NEXT_DIR names a Next.js project with
    /// app/api/hello/route.ts (its node_modules installed).
    /// </summary>
    [Fact]
    public void ANextJsRouteStopsAtItsBreakpoint()
    {
        if (!HaveNode || Environment.GetEnvironmentVariable("PYMAGIC_TEST_NEXT_DIR") is not { Length: > 0 } dir || !File.Exists(Path.Combine(dir, "app", "api", "hello", "route.ts"))) return;
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<JsonNode>();
        using var session = new RunSession(events.Add);
        JsonNode Next(Func<JsonNode, bool> match, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (true)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !events.TryTake(out var e, left)) throw new AssertionException("timed out; saw: " + string.Join(" ", seen.Select(s => s.ToJsonString()).TakeLast(30)));
                seen.Add(e);
                if (match(e)) return e;
            }
        }
        string Output() => string.Concat(seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]!.GetValue<string>()));
        const int port = 3917;
        session.StartNode(dir, null, "dev", null, true, new[] { "--port", port.ToString() }, new Dictionary<string, JsonArray> { [Path.Combine(dir, "app", "api", "hello", "route.ts")] = new JsonArray(4) });
        try
        {
            Next(_ => Output().Contains("Ready") || Output().Contains($"localhost:{port}"), 180000);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var answer = http.GetStringAsync($"http://localhost:{port}/api/hello?name=Ada");
            var stopped = Next(e => e["type"]?.GetValue<string>() == "stopped", 180000);
            Assert.True(stopped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("route.ts", StringComparison.OrdinalIgnoreCase), stopped.ToJsonString().Substring(0, Math.Min(3000, stopped.ToJsonString().Length)));
            Assert.Equal(Path.Combine(dir, "app", "api", "hello", "route.ts").ToLowerInvariant(), stopped["frames"]![0]!["file"]!.GetValue<string>().ToLowerInvariant());
            Assert.Equal(4, stopped["frames"]![0]!["line"]!.GetValue<int>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "name" });
            Assert.Equal("\"Ada\"", Next(e => e["type"]?.GetValue<string>() == "evaluated", 30000)["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "continue" });
            Assert.Contains("hello Ada", answer.GetAwaiter().GetResult());
        }
        finally { session.Handle(new JsonObject { ["type"] = "stop" }); Thread.Sleep(1000); }
    }

    /// <summary>
    /// JavaScript Debug: a Vite React app's page opened in Edge or Chrome under the debugger stops at a breakpoint of its src/App.tsx (Vite
    /// serves it compiled, with its map), its values read there. Runs only when PYMAGIC_TEST_VITE_DIR names a Vite React TypeScript project
    /// (its node_modules installed) and a browser is found.
    /// </summary>
    [Fact]
    public void AVitePageInTheBrowserStopsInItsTsxFile()
    {
        if (!HaveNode || Environment.GetEnvironmentVariable("PYMAGIC_TEST_VITE_DIR") is not { Length: > 0 } dir || !File.Exists(Path.Combine(dir, "src", "App.tsx"))) return;
        const int port = 5917;
        var vite = new ProcessStartInfo("node") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { Path.Combine(dir, "node_modules", "vite", "bin", "vite.js"), "--port", port.ToString(), "--strictPort" }) vite.ArgumentList.Add(a);
        using var server = Process.Start(vite)!;
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<JsonNode>();
        using var session = new RunSession(events.Add);
        try
        {
            var ready = DateTime.UtcNow.AddSeconds(60);
            using (var http = new HttpClient())
                while (true)
                {
                    try { if (http.GetAsync($"http://localhost:{port}/").GetAwaiter().GetResult().IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                    if (DateTime.UtcNow > ready) throw new AssertionException("Vite did not start");
                    Thread.Sleep(300);
                }
            JsonNode Next(Func<JsonNode, bool> match, int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (true)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !events.TryTake(out var e, left)) throw new AssertionException("timed out; saw: " + string.Join(" ", seen.Select(s => s.ToJsonString()).TakeLast(20)));
                    seen.Add(e);
                    if (match(e)) return e;
                }
            }
            session.StartBrowser(dir, $"http://localhost:{port}/", null, null, new Dictionary<string, JsonArray> { [Path.Combine(dir, "src", "App.tsx")] = new JsonArray(8) });
            var stopped = Next(e => e["type"]?.GetValue<string>() is "stopped" or "exited", 90000);
            Assert.True(stopped["type"]!.GetValue<string>() == "stopped", string.Join(" ", seen.Select(s => s.ToJsonString())));
            Assert.True(stopped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("App.tsx", StringComparison.OrdinalIgnoreCase), stopped.ToJsonString()[..Math.Min(2500, stopped.ToJsonString().Length)]);
            Assert.Equal(Path.Combine(dir, "src", "App.tsx").ToLowerInvariant(), stopped["frames"]![0]!["file"]!.GetValue<string>().ToLowerInvariant());
            Assert.Equal(8, stopped["frames"]![0]!["line"]!.GetValue<int>());
            Assert.Equal("App", stopped["frames"]![0]!["name"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "typeof useState" });
            Assert.Equal("\"function\"", Next(e => e["type"]?.GetValue<string>() == "evaluated", 30000)["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "next" });
            var step = Next(e => e["type"]?.GetValue<string>() == "stopped", 30000);
            Assert.True(step["frames"]![0]!["file"]!.GetValue<string>().EndsWith("App.tsx", StringComparison.OrdinalIgnoreCase), step.ToJsonString());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 2, ["frameId"] = 0, ["expression"] = "count" });
            Assert.Equal("0", Next(e => e["type"]?.GetValue<string>() == "evaluated", 30000)["result"]!.GetValue<string>());
        }
        finally
        {
            session.Handle(new JsonObject { ["type"] = "stop" });
            Thread.Sleep(1500);
            try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// JavaScript Debug of a Next.js page: its client component (app/counter.tsx, 'use client') stops in the browser at its breakpoint, through
    /// the map of the chunk Next.js serves. The dev server runs without the debugger. Runs only with PYMAGIC_TEST_NEXT_DIR (app/counter.tsx in it).
    /// </summary>
    [Fact]
    public void ANextJsClientComponentStopsInTheBrowser()
    {
        if (!HaveNode || Environment.GetEnvironmentVariable("PYMAGIC_TEST_NEXT_DIR") is not { Length: > 0 } dir || !File.Exists(Path.Combine(dir, "app", "counter.tsx"))) return;
        const int port = 3918;
        var serverEvents = new BlockingCollection<JsonNode>();
        using var server = new RunSession(serverEvents.Add);
        server.StartNode(dir, null, "dev", null, false, new[] { "--port", port.ToString() }, null);
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<JsonNode>();
        using var session = new RunSession(events.Add);
        try
        {
            var ready = DateTime.UtcNow.AddSeconds(120);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                while (true)
                {
                    try { if (http.GetAsync($"http://localhost:{port}/").GetAwaiter().GetResult().IsSuccessStatusCode) break; } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                    if (DateTime.UtcNow > ready) throw new AssertionException("Next.js did not start");
                    Thread.Sleep(500);
                }
            JsonNode Next(Func<JsonNode, bool> match, int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (true)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !events.TryTake(out var e, left)) throw new AssertionException("timed out; saw: " + string.Join(" ", seen.Select(s => s.ToJsonString()).TakeLast(20)));
                    seen.Add(e);
                    if (match(e)) return e;
                }
            }
            session.StartBrowser(dir, $"http://localhost:{port}/", null, null, new Dictionary<string, JsonArray> { [Path.Combine(dir, "app", "counter.tsx")] = new JsonArray(6) });
            var stopped = Next(e => e["type"]?.GetValue<string>() is "stopped" or "exited", 120000);
            Assert.True(stopped["type"]!.GetValue<string>() == "stopped" && stopped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("counter.tsx", StringComparison.OrdinalIgnoreCase),
                stopped.ToJsonString()[..Math.Min(2500, stopped.ToJsonString().Length)]);
            Assert.Equal(6, stopped["frames"]![0]!["line"]!.GetValue<int>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "count" });
            Assert.Equal("3", Next(e => e["type"]?.GetValue<string>() == "evaluated", 30000)["result"]!.GetValue<string>());
        }
        finally
        {
            session.Handle(new JsonObject { ["type"] = "stop" });
            server.Handle(new JsonObject { ["type"] = "stop" });
            Thread.Sleep(1500);
        }
    }

    [Fact]
    public void ATypeScriptFileStopsOnItsOwnLines()
    {
        if (!HaveNode) return;
        using var h = new Harness();
        h.Script("greet.ts", "interface Person {\n  name: string;\n  age: number;\n}\nfunction greet(p: Person): string {\n  const text: string = `hi ${p.name}`;\n  return text;\n}\nconsole.log(greet({ name: 'Ada', age: 36 }));\n");
        h.Session.StartNode(h.Root, "greet.ts", null, null, true, Array.Empty<string>(), Breaks("greet.ts", 7));
        var stopped = h.Type("stopped");
        Assert.True(Line(stopped) == 7, stopped.ToJsonString());
        Assert.Equal("\"hi Ada\"", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "text" })["result"]!.GetValue<string>());
        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("hi Ada", h.AllOutput());
    }
}
