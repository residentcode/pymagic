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
