using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PHP and Laravel: PHP and Composer found, a Laravel project told and its version, Artisan's commands, a PHP file and Artisan commands run in
/// the Run panel (artisan serve answering), and a new Laravel project made by Composer. Run only when PYMAGIC_PHP names a php.exe; the
/// Laravel ones with PYMAGIC_TEST_LARAVEL_DIR (a Laravel app, its vendor installed), the new project with PYMAGIC_COMPOSER too.
/// </summary>
public class PhpLaravelTests
{
    static string? Php => Environment.GetEnvironmentVariable("PYMAGIC_PHP") is { Length: > 0 } p && File.Exists(p) ? p : null;
    static string? LaravelDir => Environment.GetEnvironmentVariable("PYMAGIC_TEST_LARAVEL_DIR") is { Length: > 0 } d && File.Exists(Path.Combine(d, "artisan")) ? d : null;

    sealed class Run : IDisposable
    {
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        public Run() { Session = new RunSession(Events.Add); }
        public JsonNode Next(Func<JsonNode, bool> match, int ms = 60000)
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

    [Fact]
    public void PhpIsFoundAsSettingsNameItOrOnThePath()
    {
        if (Php is not { } php) return;
        Assert.Equal(php, PhpTools.FindPhp(php));
        Assert.Equal(php, PhpTools.FindPhp(Path.GetDirectoryName(php)));   // the folder that has it
        // not there: the next place is looked at (PYMAGIC_PHP here)
        Assert.Equal(php, PhpTools.FindPhp(@"C:\no\such\php.exe"));
    }

    [Fact]
    public void APhpFileRunsInTheRunPanel()
    {
        if (Php is not { } php) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_php_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hello.php"), "<?php\n$items = [3, 4];\necho 'sum ' . array_sum($items) . PHP_EOL;\nfwrite(STDERR, \"to stderr\\n\");\nexit(4);\n");
        using var run = new Run();
        run.Session.Php = php;
        run.Session.StartPhp(dir, "hello.php", null, false, Array.Empty<string>(), null);
        Assert.Equal(4, run.Next(e => e["type"]?.GetValue<string>() == "exited")["code"]!.GetValue<int>());
        Assert.Contains("sum 7", run.Output);
        Assert.Contains("to stderr", run.Output);
    }

    [Fact]
    public void ALaravelProjectIsToldWithItsVersionAndItsArtisanCommands()
    {
        if (Php is not { } php || LaravelDir is not { } dir) return;
        var info = PhpTools.Laravel(dir)!;
        Assert.True(info != null, "a Laravel project");
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(info["version"]!.GetValue<string>(), @"^\d+\.\d+\.\d+$"), info.ToJsonString());
        Assert.Contains("routes/web.php", info["routes"]!.ToJsonString());
        Assert.True(PhpTools.Laravel(Path.GetTempPath()) == null, "a folder that is not one");
        var commands = PhpTools.ArtisanCommands(php, dir);
        var names = commands.Select(c => c!["name"]!.GetValue<string>()).ToList();
        foreach (var n in new[] { "serve", "migrate", "make:model", "make:controller", "route:list", "tinker" }) Assert.True(names.Contains(n), n + " in " + string.Join(", ", names.Take(20)));
        var model = commands.First(c => c!["name"]!.GetValue<string>() == "make:model")!;
        Assert.Equal("name", model["arguments"]![0]!["name"]!.GetValue<string>());
        Assert.True(model["options"]!.AsArray().Any(o => o!["name"]!.GetValue<string>() == "--migration"), "make:model --migration");
    }

    [Fact]
    public void ArtisanCommandsRunAndServeAnswers()
    {
        if (Php is not { } php || LaravelDir is not { } dir) return;
        using (var run = new Run())
        {
            run.Session.Php = php;
            run.Session.StartPhp(dir, null, new[] { "route:list" }, false, Array.Empty<string>(), null);
            Assert.Equal(0, run.Next(e => e["type"]?.GetValue<string>() == "exited")["code"]!.GetValue<int>());
            Assert.Contains("GET|HEAD", run.Output);
        }
        int port;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        using (var serve = new Run())
        {
            serve.Session.Php = php;
            serve.Session.StartPhp(dir, null, new[] { "serve", "--host=127.0.0.1", $"--port={port}" }, false, Array.Empty<string>(), null);
            serve.Next(_ => serve.Output.Contains($"127.0.0.1:{port}"), 60000);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var page = http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
            Assert.Equal(200, (int)page.StatusCode);
            Assert.Contains("Laravel", page.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            serve.Session.Handle(new JsonObject { ["type"] = "stop" });
            serve.Next(e => e["type"]?.GetValue<string>() == "exited", 30000);
        }
    }

    static bool Is(JsonNode e, string type) => e["type"]?.GetValue<string>() == type;
    static bool HasXdebug(string php) { try { return PhpTools.FindXdebug(php, null).Error == null; } catch (ServiceException) { return false; } }

    [Fact]
    public void APhpFileIsDebuggedWithXdebug()
    {
        if (Php is not { } php || !HasXdebug(php)) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_phpdebug_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shop.php"), string.Join("\n", new[]
        {
            "<?php",                                                       // 1
            "class Item { public function __construct(public string $name, public int $price) {} }",
            "function total(array $items): int {",                         // 3
            "    $sum = 0;",                                               // 4
            "    foreach ($items as $item) {",                             // 5
            "        $sum += $item->price;",                               // 6
            "    }",
            "    return $sum;",                                            // 8
            "}",
            "$items = [new Item('pen', 3), new Item('book', 12)];",        // 10
            "$label = \"total\";",                                         // 11
            "echo $label . ' ' . total($items) . PHP_EOL;",                // 12
            "for ($i = 0; $i < 5; $i++) {",                                // 13
            "    $twice = $i * 2;",                                        // 14
            "}",
            "echo 'done ' . $twice . PHP_EOL;",                            // 16
            "",
        }));
        using var run = new Run();
        run.Session.Php = php;
        string file = Path.Combine(dir, "shop.php");
        var bps = new Dictionary<string, JsonArray>
        {
            ["shop.php"] = new JsonArray(new JsonObject { ["line"] = 8 }, new JsonObject { ["line"] = 14, ["logMessage"] = "i is {$i}" }, new JsonObject { ["line"] = 16 }),
        };
        run.Session.StartPhp(dir, "shop.php", null, true, Array.Empty<string>(), bps);

        // stopped at line 8 in total(), called from the main script
        var stop = run.Next(e => Is(e, "stopped"));
        var frames = stop["frames"]!.AsArray();
        Assert.Equal("breakpoint", stop["reason"]!.GetValue<string>());
        Assert.Equal(8, frames[0]!["line"]!.GetValue<int>());
        Assert.Equal("total", frames[0]!["name"]!.GetValue<string>());
        Assert.True(string.Equals(file, frames[0]!["file"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), frames.ToJsonString());
        Assert.Equal("(main)", frames[1]!["name"]!.GetValue<string>());

        // the frame's variables, an array of objects opened, a property's value
        run.Session.Handle(new JsonObject { ["type"] = "scopes", ["id"] = 1, ["frameId"] = 0 });
        var scopes = run.Next(e => Is(e, "scopes"))["scopes"]!.AsArray();
        Assert.Equal("Locals", scopes[0]!["name"]!.GetValue<string>());
        run.Session.Handle(new JsonObject { ["type"] = "variables", ["id"] = 2, ["ref"] = scopes[0]!["ref"]!.GetValue<int>() });
        var locals = run.Next(e => Is(e, "variables"))["items"]!.AsArray();
        var sum = locals.First(v => v!["name"]!.GetValue<string>() == "$sum")!;
        Assert.Equal("15", sum["value"]!.GetValue<string>());
        var items = locals.First(v => v!["name"]!.GetValue<string>() == "$items")!;
        Assert.Equal("array(2)", items["value"]!.GetValue<string>());
        run.Session.Handle(new JsonObject { ["type"] = "variables", ["id"] = 3, ["ref"] = items["ref"]!.GetValue<int>() });
        var members = run.Next(e => Is(e, "variables"))["items"]!.AsArray();
        Assert.Equal("{Item}", members[0]!["value"]!.GetValue<string>());
        run.Session.Handle(new JsonObject { ["type"] = "variables", ["id"] = 4, ["ref"] = members[1]!["ref"]!.GetValue<int>() });
        var book = run.Next(e => Is(e, "variables"))["items"]!.AsArray();
        Assert.True(book.Any(m => m!["name"]!.GetValue<string>() == "name" && m["value"]!.GetValue<string>() == "\"book\""), book.ToJsonString());

        // the value under the mouse (read, not run), an expression run by PHP, and one the hover will not run
        run.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 5, ["frameId"] = 0, ["expression"] = "$item->name", ["evalOnly"] = true });
        Assert.Equal("\"book\"", run.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
        run.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 6, ["frameId"] = 0, ["expression"] = "count($items) * 10 + $sum" });
        Assert.Equal("35", run.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
        run.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 7, ["frameId"] = 0, ["expression"] = "array_pop($items)", ["evalOnly"] = true });
        Assert.Contains("may change the program", run.Next(e => Is(e, "evaluated"))["error"]!.GetValue<string>());
        // the values at the ends of the lines, by the names the editor reads (without their $)
        run.Session.Handle(new JsonObject { ["type"] = "values", ["id"] = 8, ["frameId"] = 0, ["names"] = new JsonArray("sum", "items", "nothing") });
        var values = run.Next(e => Is(e, "values"))["values"]!.AsObject();
        Assert.Equal("15", values["sum"]!.GetValue<string>());
        Assert.True(!values.ContainsKey("nothing"), values.ToJsonString());

        // Set Value: $sum = 100, which the program then prints
        run.Session.Handle(new JsonObject { ["type"] = "setVariable", ["id"] = 9, ["ref"] = scopes[0]!["ref"]!.GetValue<int>(), ["name"] = "$sum", ["expression"] = "100", ["frameId"] = 0 });
        Assert.Equal("100", run.Next(e => Is(e, "variableSet"))["value"]!.GetValue<string>());

        // a step out of total() stops in the main script at the statement after the call (Xdebug finishes the line that called it)
        run.Session.Handle(new JsonObject { ["type"] = "stepOut" });
        var stepped = run.Next(e => Is(e, "stopped"));
        Assert.Equal("step", stepped["reason"]!.GetValue<string>());
        Assert.Equal(13, stepped["frames"]![0]!["line"]!.GetValue<int>());
        Assert.Equal("(main)", stepped["frames"]![0]!["name"]!.GetValue<string>());

        // on: the log message written five times without stopping, then line 16
        run.Session.Handle(new JsonObject { ["type"] = "continue" });
        var last = run.Next(e => Is(e, "stopped"));
        Assert.Equal(16, last["frames"]![0]!["line"]!.GetValue<int>());
        Assert.Contains("total 100", run.Output);
        for (int i = 0; i < 5; i++) Assert.Contains($"i is {i}", run.Output);
        run.Session.Handle(new JsonObject { ["type"] = "continue" });
        Assert.Equal(0, run.Next(e => Is(e, "exited"))["code"]!.GetValue<int>());
        Assert.Contains("done 8", run.Output);
    }

    [Fact]
    public void PauseStopsRunningPhpWhereItIs()
    {
        if (Php is not { } php || !HasXdebug(php)) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_phppause_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spin.php"), string.Join("\n", new[]
        {
            "<?php",                                         // 1
            "echo 'spinning' . PHP_EOL;",                    // 2
            "$i = 0;",                                       // 3
            "while (true) {",                                // 4
            "    $i++;",                                     // 5
            "    usleep(2000);",                             // 6
            "}",
            "",
        }));
        using var run = new Run();
        run.Session.Php = php;
        run.Session.StartPhp(dir, "spin.php", null, true, Array.Empty<string>(), new Dictionary<string, JsonArray>());
        run.Next(_ => run.Output.Contains("spinning"));
        Thread.Sleep(300);

        // Pause: Xdebug's control socket tells the loop to stop where it is, with no breakpoint there
        for (int round = 0; round < 2; round++)
        {
            run.Session.Handle(new JsonObject { ["type"] = "pause" });
            var stop = run.Next(e => Is(e, "stopped"), 20000);
            Assert.Equal("pause", stop["reason"]!.GetValue<string>());
            int line = stop["frames"]![0]!["line"]!.GetValue<int>();
            Assert.True(line >= 4 && line <= 6, stop.ToJsonString());
            run.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 10 + round, ["frameId"] = 0, ["expression"] = "$i > 0 ? 'on' : 'off'" });
            Assert.Equal("\"on\"", run.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
            // and on again: it runs until the next Pause
            run.Session.Handle(new JsonObject { ["type"] = "continue" });
            run.Next(e => Is(e, "running"), 10000);
            Thread.Sleep(300);
        }
        run.Session.Handle(new JsonObject { ["type"] = "stop" });
        run.Next(e => Is(e, "exited"), 30000);
    }

    [Fact]
    public void ARequestOfArtisanServeStopsAtARouteBreakpoint()
    {
        if (Php is not { } php || LaravelDir is not { } dir || !HasXdebug(php)) return;
        int port;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        using var serve = new Run();
        serve.Session.Php = php;
        var bps = new Dictionary<string, JsonArray> { [Path.Combine("routes", "web.php")] = new JsonArray(new JsonObject { ["line"] = 6 }) };
        serve.Session.StartPhp(dir, null, new[] { "serve", "--host=127.0.0.1", $"--port={port}" }, true, Array.Empty<string>(), bps);
        try
        {
            serve.Next(_ => serve.Output.Contains($"127.0.0.1:{port}"), 90000);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var page = http.GetAsync($"http://127.0.0.1:{port}/");
            var stop = serve.Next(e => Is(e, "stopped"), 90000);
            var frames = stop["frames"]!.AsArray();
            Assert.True(frames[0]!["file"]!.GetValue<string>().EndsWith(Path.Combine("routes", "web.php")), frames.ToJsonString());
            Assert.Equal(6, frames[0]!["line"]!.GetValue<int>());
            Assert.True(stop["thread"]!.GetValue<string>().StartsWith("request"), stop.ToJsonString());
            // the vendor's frames below the route are left out, as library code is
            Assert.True(!frames.Skip(1).Any(f => f!["file"]!.GetValue<string>().Contains(Path.DirectorySeparatorChar + "vendor" + Path.DirectorySeparatorChar)), frames.ToJsonString());
            serve.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "request()->path()" });
            Assert.Equal("\"/\"", serve.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
            serve.Session.Handle(new JsonObject { ["type"] = "continue" });
            Assert.Equal(200, (int)page.GetAwaiter().GetResult().StatusCode);
        }
        finally
        {
            serve.Session.Handle(new JsonObject { ["type"] = "stop" });
            serve.Next(e => Is(e, "exited"), 30000);
        }
    }

    [Fact]
    public void ABladeTemplatesBreakpointStopsInTheTemplate()
    {
        if (Php is not { } php || LaravelDir is not { } dir || !HasXdebug(php)) return;
        string template = Path.Combine(dir, "resources", "views", "welcome.blade.php");
        var lines = File.ReadAllLines(template);
        int at = Array.FindIndex(lines, l => l.Contains("@if (Route::has('login'))")) + 1;
        Assert.True(at > 0, "welcome.blade.php has its @if (Route::has('login'))");
        int port;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        using var serve = new Run();
        serve.Session.Php = php;
        var bps = new Dictionary<string, JsonArray> { [Path.Combine("resources", "views", "welcome.blade.php")] = new JsonArray(new JsonObject { ["line"] = at }) };
        serve.Session.StartPhp(dir, null, new[] { "serve", "--host=127.0.0.1", $"--port={port}" }, true, Array.Empty<string>(), bps);
        try
        {
            serve.Next(_ => serve.Output.Contains($"127.0.0.1:{port}"), 120000);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var page = http.GetAsync($"http://127.0.0.1:{port}/");
            // stopped in the template (not in storage/framework/views' compiled file), at its line, the template's name as the frame's
            var stop = serve.Next(e => Is(e, "stopped"), 90000);
            var frames = stop["frames"]!.AsArray();
            Assert.True(string.Equals(template, frames[0]!["file"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), frames.ToJsonString());
            Assert.Equal(at, frames[0]!["line"]!.GetValue<int>());
            Assert.Equal("welcome.blade.php", frames[0]!["name"]!.GetValue<string>());
            // the template's values: Laravel's view variables
            serve.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "Route::has('login') ? 'yes' : 'no'" });
            Assert.True(serve.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>() is "\"yes\"" or "\"no\"", "evaluated in the view");
            // a step goes on in the template
            serve.Session.Handle(new JsonObject { ["type"] = "next" });
            var stepped = serve.Next(e => Is(e, "stopped"), 30000);
            Assert.True(stepped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("welcome.blade.php", StringComparison.OrdinalIgnoreCase), stepped.ToJsonString());
            Assert.True(stepped["frames"]![0]!["line"]!.GetValue<int>() > at, stepped.ToJsonString());
            serve.Session.Handle(new JsonObject { ["type"] = "continue" });
            Assert.Equal(200, (int)page.GetAwaiter().GetResult().StatusCode);
        }
        finally
        {
            serve.Session.Handle(new JsonObject { ["type"] = "stop" });
            serve.Next(e => Is(e, "exited"), 30000);
        }
    }

    [Fact]
    public void PhpactorOnThisComputerGivesCodeInsightInALaravelProject()
    {
        if (Php is not { } php || LaravelDir is not { } dir || PhpLanguageServer.Phar() == null) return;
        // Phpactor's index and settings in a folder of the test's own
        string home = Path.Combine(Path.GetTempPath(), "pm_phpactor_home_" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("PYMAGIC_PHPACTOR_HOME", home);
        try
        {
            // a document of the editor only (nothing is written into the project)
            string file = Path.Combine(dir, "routes", "pm-insight.php");
            string text = string.Join("\n", new[]
            {
                "<?php",                                          // 0
                "use App\\Models\\User;",                         // 1
                "use Illuminate\\Support\\Str;",                  // 2
                "$user = new User();",                            // 3
                "$slug = Str::slug('Hello World');",              // 4
                "$user->",                                        // 5
                "",
            });
            var toPage = new BlockingCollection<JsonNode>();
            var log = new ConcurrentQueue<string>();
            using var server = new PhpLanguageServer(php, dir, toPage.Add, log.Enqueue);
            string uri = LanguageService.UriOfPath(file);
            server.Notify("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "php", ["version"] = 1, ["text"] = text } });
            JsonObject At(int line, int character) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };
            string Log() => string.Join(" | ", log.TakeLast(8));

            // completion after `$user->`: the model's methods, Laravel's included (Notifiable's notify)
            var labels = new List<string>();
            for (var until = DateTime.UtcNow.AddSeconds(90); !labels.Contains("notify") && DateTime.UtcNow < until; Thread.Sleep(labels.Count == 0 ? 2000 : 0))
            {
                var completion = server.Request("textDocument/completion", At(5, 7)).GetAwaiter().GetResult();
                labels = (completion?["items"] as JsonArray ?? new JsonArray()).Select(i => i!["label"]!.GetValue<string>()).ToList();
            }
            Assert.True(labels.Contains("notify"), string.Join(",", labels.Take(20)) + " " + Log());

            // hover on Str::slug: its signature
            var hover = server.Request("textDocument/hover", At(4, 14)).GetAwaiter().GetResult();
            string shown = hover?["contents"]?["value"]?.GetValue<string>() ?? "";
            Assert.True(shown.Contains("slug"), shown + " " + Log());

            // go to definition of User: the project's app/Models/User.php
            var defs = server.Request("textDocument/definition", At(3, 13)).GetAwaiter().GetResult()!.AsArray();
            Assert.True(defs.Count > 0, Log());
            Assert.Equal(LanguageService.UriOfPath(Path.Combine(dir, "app", "Models", "User.php")), defs[0]!["uri"]!.GetValue<string>());

            // signature help in Str::slug(
            string calling = text.Replace("$user->\n", "Str::slug(\n");
            server.Notify("textDocument/didChange", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 }, ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = calling }) });
            var help = server.Request("textDocument/signatureHelp", At(5, 10)).GetAwaiter().GetResult();
            Assert.True(help?["signatures"]?[0]?["label"]?.GetValue<string>().Contains("title") == true, help?.ToJsonString() + " " + Log());

            // a problem: a class that is not there
            string broken = text.Replace("$user->\n", "$x = new Missing();\n");
            server.Notify("textDocument/didChange", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 3 }, ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = broken }) });
            server.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
            JsonNode? found = null;
            for (var until = DateTime.UtcNow.AddSeconds(60); found == null && DateTime.UtcNow < until && toPage.TryTake(out var msg, TimeSpan.FromSeconds(5));)
                if (msg["params"]?["uri"]?.GetValue<string>() == uri && (msg["params"]!["diagnostics"] as JsonArray)!.Any(d => d!["message"]!.GetValue<string>().Contains("Missing"))) found = msg;
            Assert.True(found != null, "no problem told for Missing; " + Log());
            Assert.True(server.Failed == null, server.Failed ?? "");
            Assert.True(!File.Exists(file), "nothing written into the project");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYMAGIC_PHPACTOR_HOME", null);
            try { Directory.Delete(home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ComposerCommandsAreListedAndRunInTheRunPanel()
    {
        if (Php is not { } php || Environment.GetEnvironmentVariable("PYMAGIC_COMPOSER") is not { Length: > 0 }) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_composer_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "composer.json"), "{\"name\":\"pm/shop\",\"autoload\":{\"psr-4\":{\"Shop\\\\\":\"src/\"}},\"scripts\":{\"hello\":\"@php -r \\\"echo 'hello from composer';\\\"\",\"post-autoload-dump\":\"@php -r \\\"echo 'dumped';\\\"\"}}");
        File.WriteAllText(Path.Combine(dir, "src", "Item.php"), "<?php\nnamespace Shop;\nclass Item {}\n");
        try
        {
            // the list: Composer's own, then composer.json's scripts (not its event hooks)
            var names = PhpTools.ComposerCommands(dir)!.Select(c => c!["name"]!.GetValue<string>()).ToList();
            Assert.True(names.Contains("require") && names.Contains("require --dev") && names.Contains("dump-autoload") && names.Contains("run-script hello"), string.Join(",", names));
            Assert.True(!names.Contains("run-script post-autoload-dump"), "an event hook is Composer's own");
            Assert.True(PhpTools.ComposerCommands(Path.GetTempPath()) == null, "no composer.json");

            // dump-autoload writes vendor/autoload.php (its hook runs too)
            using (var run = new Run())
            {
                run.Session.Php = php;
                run.Session.StartComposer(dir, new[] { "dump-autoload" });
                Assert.Equal(0, run.Next(e => Is(e, "exited"), 120000)["code"]!.GetValue<int>());
                Assert.True(File.Exists(Path.Combine(dir, "vendor", "autoload.php")), run.Output);
                Assert.Contains("dumped", run.Output);
            }
            // a script of composer.json
            using (var run = new Run())
            {
                run.Session.Php = php;
                run.Session.StartComposer(dir, new[] { "run-script", "hello" });
                Assert.Equal(0, run.Next(e => Is(e, "exited"), 120000)["code"]!.GetValue<int>());
                Assert.Contains("hello from composer", run.Output);
            }
            // a command that is not one
            using (var run = new Run())
            {
                run.Session.StartComposer(dir, new[] { "; rm -rf" });
                Assert.Contains("a Composer command", run.Next(e => Is(e, "error"))["message"]!.GetValue<string>());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void NewProjectMakesALaravelProjectWithComposer()
    {
        if (Php is not { } php || Environment.GetEnvironmentVariable("PYMAGIC_COMPOSER") is not { Length: > 0 } composer || !File.Exists(composer)) return;
        Assert.Equal(php, PhpTools.FindComposer(composer, php)!.Value.Program);   // a composer.phar is run by PHP
        string parent = Path.Combine(Path.GetTempPath(), "pm_newlaravel_" + Guid.NewGuid().ToString("N"));
        try
        {
            var made = NewProject.Create(new NewProjectRequest(Path.Combine(parent, "shop"), "laravel", false, false, "none", null, null, null, Php: php, Composer: composer));
            Assert.True(File.Exists(Path.Combine(made.Folder, "artisan")) && File.Exists(Path.Combine(made.Folder, ".env")), "artisan and .env");
            Assert.True(Directory.Exists(Path.Combine(made.Folder, "vendor", "laravel", "framework")), "vendor");
            Assert.Contains("Laravel", made.Notes[0]);
            Assert.True(PhpTools.Laravel(made.Folder) != null, "told as Laravel");
            // a folder that is not empty is refused
            string why = "";
            try { NewProject.Create(new NewProjectRequest(made.Folder, "laravel", false, false, "none", null, null, null, Php: php, Composer: composer)); }
            catch (ServiceException e) { why = e.Message; }
            Assert.Equal("The folder is not empty.", why);
        }
        finally { try { Directory.Delete(parent, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
