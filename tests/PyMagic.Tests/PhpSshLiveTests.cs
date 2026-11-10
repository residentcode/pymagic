using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PHP on another machine reached by SSH (Settings > PHP: ssh:root@host?i=KEY&amp;kh=KNOWN|php): what it is, a file run and debugged there with
/// Xdebug (its paths are the project's here), Laravel's server there reached on the same port here and a request stopped at a route's
/// breakpoint, and an Artisan command's new file copied back into the project. Runs only when PYMAGIC_CT_HOST, PYMAGIC_CT_KEY and
/// PYMAGIC_CT_KNOWN_HOSTS name the test machine (PHP 8.3, php-xdebug there); the Laravel ones with PYMAGIC_TEST_LARAVEL_DIR too.
/// </summary>
public class PhpSshLiveTests
{
    static string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    static string? Spec => V("PYMAGIC_CT_HOST") is { } host && V("PYMAGIC_CT_KEY") is { } key && V("PYMAGIC_CT_KNOWN_HOSTS") is { } known ? $"ssh:root@{host}?i={key}&kh={known}|php" : null;
    static string? LaravelDir => V("PYMAGIC_TEST_LARAVEL_DIR") is { } d && File.Exists(Path.Combine(d, "artisan")) ? d : null;

    sealed class Run : IDisposable
    {
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        public Run() { Session = new RunSession(Events.Add); }
        public JsonNode Next(Func<JsonNode, bool> match, int ms = 90000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (true)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Events.TryTake(out var e, left)) throw new AssertionException("timed out; saw: " + string.Join(" ", Seen.Select(s => s.ToJsonString()).TakeLast(20)));
                Seen.Add(e);
                if (match(e)) return e;
            }
        }
        public string Output => string.Concat(Seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]!.GetValue<string>()));
        public void Dispose() => Session.Dispose();
    }

    static bool Is(JsonNode e, string type) => e["type"]?.GetValue<string>() == type;

    static string Project(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_phpssh_" + name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ThePhpOfAnSshMachineIsTold()
    {
        if (Spec is not { } spec) return;
        var remote = PhpTools.Remote(spec);
        Assert.True(remote != null, spec);
        Assert.Equal("php", remote!.Value.Php);
        Assert.Equal(spec, PhpTools.FindPhp(spec)!);
        var facts = PhpTools.RemoteFacts(remote.Value.Target, remote.Value.Php);
        Assert.True(facts.Version?.StartsWith("8.") == true, facts.Version ?? "none");
        Assert.True(facts.Xdebug, "xdebug");
        Assert.True(facts.Composer?.StartsWith('/') == true, facts.Composer ?? "no composer");
        Assert.True(PhpTools.Remote(@"C:\php\php.exe") == null && PhpTools.Remote(null) == null, "a local PHP");
    }

    [Fact]
    public void PauseStopsPhpRunningThere()
    {
        if (Spec is not { } spec) return;
        string dir = Project("pause");
        File.WriteAllText(Path.Combine(dir, "spin.php"), "<?php\necho 'spinning' . PHP_EOL;\n$i = 0;\nwhile (true) {\n    $i++;\n    usleep(2000);\n}\n");
        using var run = new Run();
        run.Session.Php = spec;
        run.Session.StartPhp(dir, "spin.php", null, true, Array.Empty<string>(), new Dictionary<string, JsonArray>());
        run.Next(_ => run.Output.Contains("spinning"));
        Thread.Sleep(500);
        // Xdebug's abstract socket there (@xdebug-ctrl.PID), reached by the PHP there
        run.Session.Handle(new JsonObject { ["type"] = "pause" });
        var stop = run.Next(e => Is(e, "stopped"), 30000);
        Assert.Equal("pause", stop["reason"]!.GetValue<string>());
        int line = stop["frames"]![0]!["line"]!.GetValue<int>();
        Assert.True(line >= 4 && line <= 6, stop.ToJsonString());
        Assert.True(stop["frames"]![0]!["file"]!.GetValue<string>().EndsWith("spin.php", StringComparison.OrdinalIgnoreCase), stop.ToJsonString());
        run.Session.Handle(new JsonObject { ["type"] = "stop" });
        run.Next(e => Is(e, "exited"), 30000);
    }

    [Fact]
    public void AFileRunsAndIsDebuggedThere()
    {
        if (Spec is not { } spec) return;
        string dir = Project("debug");
        File.WriteAllText(Path.Combine(dir, "shop.php"), string.Join("\n", new[]
        {
            "<?php",                                                       // 1
            "function total(array $prices): int {",                        // 2
            "    $sum = array_sum($prices);",                              // 3
            "    return $sum;",                                            // 4
            "}",
            "$prices = [3, 12];",                                          // 6
            "echo 'on ' . php_uname('s') . ' at ' . __DIR__ . PHP_EOL;",   // 7
            "echo 'total ' . total($prices) . PHP_EOL;",                   // 8
            "echo 'end' . PHP_EOL;",                                       // 9
            "",
        }));

        // run: it prints where it ran
        using (var run = new Run())
        {
            run.Session.Php = spec;
            run.Session.StartPhp(dir, "shop.php", null, false, Array.Empty<string>(), null);
            Assert.Equal(0, run.Next(e => Is(e, "exited"))["code"]!.GetValue<int>());
            Assert.Contains("on Linux at /root/.pymagic/projects/", run.Output);
            Assert.Contains("total 15", run.Output);
        }

        // debug: stopped in total(), the frame's file is the project's here, its values, an expression, a step out, the end
        using var dbg = new Run();
        dbg.Session.Php = spec;
        dbg.Session.StartPhp(dir, "shop.php", null, true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["shop.php"] = new JsonArray(new JsonObject { ["line"] = 4 }) });
        var stop = dbg.Next(e => Is(e, "stopped"));
        var frames = stop["frames"]!.AsArray();
        Assert.Equal(4, frames[0]!["line"]!.GetValue<int>());
        Assert.Equal("total", frames[0]!["name"]!.GetValue<string>());
        Assert.True(string.Equals(Path.Combine(dir, "shop.php"), frames[0]!["file"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), frames.ToJsonString());
        dbg.Session.Handle(new JsonObject { ["type"] = "values", ["id"] = 1, ["frameId"] = 0, ["names"] = new JsonArray("sum") });
        Assert.Equal("15", dbg.Next(e => Is(e, "values"))["values"]!["sum"]!.GetValue<string>());
        dbg.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 2, ["frameId"] = 0, ["expression"] = "PHP_OS_FAMILY" });
        Assert.Equal("\"Linux\"", dbg.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
        dbg.Session.Handle(new JsonObject { ["type"] = "stepOut" });
        var stepped = dbg.Next(e => Is(e, "stopped"));
        Assert.Equal("(main)", stepped["frames"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(9, stepped["frames"]![0]!["line"]!.GetValue<int>());
        dbg.Session.Handle(new JsonObject { ["type"] = "continue" });
        Assert.Equal(0, dbg.Next(e => Is(e, "exited"))["code"]!.GetValue<int>());
        Assert.Contains("total 15", dbg.Output);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void PhpactorThereGivesCompletionHoverDefinitionAndProblems()
    {
        if (Spec is not { } spec) return;
        string dir = Project("insight");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        string item = Path.Combine(dir, "src", "Item.php");
        File.WriteAllText(item, string.Join("\n", new[]
        {
            "<?php",
            "namespace Shop;",
            "",
            "/** One thing of the shop. */",
            "class Item",
            "{",
            "    public function __construct(public string $name, public int $price) {}",
            "",
            "    /** The price with the tax. */",
            "    public function gross(float $rate): float { return $this->price * (1 + $rate); }",
            "}",
            "",
        }));
        string main = Path.Combine(dir, "main.php");
        string text = string.Join("\n", new[]
        {
            "<?php",                                        // 0
            "use Shop\\Item;",                               // 1
            "require __DIR__ . '/src/Item.php';",           // 2
            "$pen = new Item('pen', 3);",                   // 3
            "echo $pen->gross(0.2);",                       // 4
            "$pen->",                                       // 5
            "",
        });
        File.WriteAllText(main, text);
        var toPage = new BlockingCollection<JsonNode>();
        var log = new ConcurrentQueue<string>();
        var remote = PhpTools.Remote(spec)!.Value;
        using var php = new PhpLanguageServer(remote.Target, remote.Php, dir, toPage.Add, log.Enqueue);
        string uri = LanguageService.UriOfPath(main);
        php.Notify("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "php", ["version"] = 1, ["text"] = text } });
        JsonObject At(int line, int character) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };
        string Log() => string.Join(" | ", log.TakeLast(10));

        // completion after `$pen->`: the class's members
        // (the first answers come before Phpactor has indexed the project: asked again as typing would)
        JsonNode? completion = null;
        var labels = new List<string>();
        for (var indexed = DateTime.UtcNow.AddSeconds(90); labels.Count == 0 && DateTime.UtcNow < indexed; Thread.Sleep(labels.Count == 0 ? 2000 : 0))
        {
            completion = php.Request("textDocument/completion", At(5, 6)).GetAwaiter().GetResult();
            labels = (completion?["items"] as JsonArray ?? new JsonArray()).Select(i => i!["label"]!.GetValue<string>()).ToList();
        }
        Assert.True(labels.Contains("gross") && labels.Contains("name") && labels.Contains("price"), string.Join(",", labels) + " " + completion?.ToJsonString() + " " + Log());

        // hover on gross: its signature and doc comment
        var hover = php.Request("textDocument/hover", At(4, 12)).GetAwaiter().GetResult();
        string shown = hover?["contents"]?["value"]?.GetValue<string>() ?? "";
        Assert.True(shown.Contains("gross") && shown.Contains("The price with the tax"), shown + " " + Log());

        // go to definition of Item: src/Item.php here, not the machine's path
        var defs = php.Request("textDocument/definition", At(3, 12)).GetAwaiter().GetResult()!.AsArray();
        Assert.True(defs.Count > 0, Log());
        Assert.Equal(LanguageService.UriOfPath(item), defs[0]!["uri"]!.GetValue<string>());
        Assert.Equal(4, defs[0]!["range"]!["start"]!["line"]!.GetValue<int>());

        // a problem: a class that is not there, told for the file of this machine
        string broken = text.Replace("$pen->\n", "$x = new Missing();\n");
        php.Notify("textDocument/didChange", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 }, ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = broken }) });
        php.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
        JsonNode? found = null;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (found == null && DateTime.UtcNow < until && toPage.TryTake(out var msg, TimeSpan.FromSeconds(5)))
            if (msg["params"]?["uri"]?.GetValue<string>() == uri && (msg["params"]!["diagnostics"] as JsonArray)!.Any(d => d!["message"]!.GetValue<string>().Contains("Missing"))) found = msg;
        Assert.True(found != null, "no problem told for Missing; " + Log());
        Assert.True(php.Failed == null, php.Failed ?? "");
    }

    static string CopyOfLaravel(string source)
    {
        string dir = Project("laravel");
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, f);
            if (rel.StartsWith("node_modules") || rel.StartsWith(".git" + Path.DirectorySeparatorChar)) continue;
            string to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dir;
    }

    [Fact]
    public void LaravelsServerThereStopsAtARouteBreakpointAndArtisanFilesComeBack()
    {
        if (Spec is not { } spec || LaravelDir is not { } source) return;
        string dir = CopyOfLaravel(source);
        int port;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        var ssh = PhpTools.Remote(spec)!.Value.Target;

        // Artisan's commands, listed there
        var commands = PhpTools.ArtisanCommands(spec, dir);
        Assert.True(commands.Any(c => c!["name"]!.GetValue<string>() == "make:model"), "make:model");

        // an Artisan command that makes a file there: it comes back into the project
        using (var make = new Run())
        {
            make.Session.Php = spec;
            make.Session.StartPhp(dir, null, new[] { "make:model", "Order" }, false, Array.Empty<string>(), null);
            Assert.Equal(0, make.Next(e => Is(e, "exited"), 120000)["code"]!.GetValue<int>());
            Assert.Contains("Copied into the project: app/Models/Order.php", make.Output);
            Assert.Contains("class Order extends Model", File.ReadAllText(Path.Combine(dir, "app", "Models", "Order.php")));
        }

        // the server there under the debugger, reached here on the same port; a request stops at the route
        using var serve = new Run();
        serve.Session.Php = spec;
        var bps = new Dictionary<string, JsonArray> { [Path.Combine("routes", "web.php")] = new JsonArray(new JsonObject { ["line"] = 6 }) };
        serve.Session.StartPhp(dir, null, new[] { "serve", "--host=127.0.0.1", $"--port={port}" }, true, Array.Empty<string>(), bps);
        try
        {
            serve.Next(_ => serve.Output.Contains($"127.0.0.1:{port}"), 180000);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var page = http.GetAsync($"http://127.0.0.1:{port}/");
            var stop = serve.Next(e => Is(e, "stopped"), 90000);
            var frames = stop["frames"]!.AsArray();
            Assert.True(string.Equals(Path.Combine(dir, "routes", "web.php"), frames[0]!["file"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), frames.ToJsonString());
            Assert.Equal(6, frames[0]!["line"]!.GetValue<int>());
            serve.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "request()->path()" });
            Assert.Equal("\"/\"", serve.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
            serve.Session.Handle(new JsonObject { ["type"] = "continue" });
            var answer = page.GetAwaiter().GetResult();
            Assert.Equal(200, (int)answer.StatusCode);
            Assert.Contains("Laravel", answer.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        finally
        {
            serve.Session.Handle(new JsonObject { ["type"] = "stop" });
            serve.Next(e => Is(e, "exited"), 30000);
        }
        // Stop ended the server there too (artisan serve and the PHP server it started)
        Thread.Sleep(3000);
        var (_, left, _) = ssh.Remote($"pgrep -af 'ph[p].*-S 127.0.0.1:{port}' || true");
        Assert.True(left.Trim().Length == 0, left);
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
