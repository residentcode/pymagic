using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Laravel Sail as the PHP of a project (SailTarget, RunSession.Sail): its compose file, paths there and here, the command line of a program
/// there; and with Docker running and a Sail app (PYMAGIC_TEST_SAIL_DIR: laravel/sail installed with sail:install, its image built; APP_PORT
/// naming the port it is served on) a script run and debugged there, Artisan's commands, the tests run and debugged, Format, and Laravel's
/// server (the container's own) answering and stopping at a route's breakpoint under the debugger.
/// </summary>
public class SailTests
{
    static string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    public void SailsComposeFilePathsAndCommandLine()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_sail_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "routes"));
        try
        {
            Assert.True(SailTarget.ComposeFileOf(root) == null, "no compose file");
            try { SailTarget.Of(root, "sail"); Assert.True(false, "Sail without its compose file"); }
            catch (ServiceException e) { Assert.Contains("sail:install", e.Message); }
            Assert.True(SailTarget.Of(root, null) == null && SailTarget.Of(root, @"C:\php\php.exe") == null, "not Sail");
            File.WriteAllText(Path.Combine(root, "compose.yaml"), "services:\n    laravel.test:\n        image: 'sail-8.5/app'\n        volumes:\n            - '.:/var/www/html'\n");
            var sail = SailTarget.Of(root, " Sail ")!;
            Assert.Equal("compose.yaml", sail.ComposeFile);
            Assert.Equal("sail", PhpTools.FindPhp("sail"));

            string web = Path.Combine(root, "routes", "web.php");
            Assert.Equal("/var/www/html/routes/web.php", sail.ToTarget(web));
            Assert.Equal("/var/www/html", sail.ToTarget(root));
            Assert.Equal(Path.GetFullPath(web), sail.ToHost("/var/www/html/routes/web.php"));
            Assert.Equal("/usr/lib/php/x.php", sail.ToHost("/usr/lib/php/x.php"));
            Assert.Equal(@"D:\elsewhere\a.php", sail.ToTarget(@"D:\elsewhere\a.php"));

            var psi = sail.Start("php", Path.Combine(root, "routes"), new[] { web, "--x" }, new Dictionary<string, string> { ["XDEBUG_MODE"] = "debug" });
            string id = psi.Environment["PYMAGIC_RUN_ID"]!;
            Assert.Equal("docker", psi.FileName);
            Assert.Equal($"compose -f compose.yaml exec -T -e XDEBUG_MODE=debug -w /var/www/html/routes laravel.test php -d pymagic={id} /var/www/html/routes/web.php --x", string.Join(" ", psi.ArgumentList));
            Assert.Equal(root.TrimEnd('\\'), psi.WorkingDirectory!.TrimEnd('\\'));
            Assert.Equal("http://localhost" + (V("APP_PORT") is { } p && p != "80" ? ":" + p : ""), sail.AppUrl());
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------ with Docker and a Sail app

    sealed class Run : IDisposable
    {
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        public Run() { Session = new RunSession(Events.Add); }
        public JsonNode Next(Func<JsonNode, bool> match, int ms = 180000)
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

    [Fact]
    public void AScriptArtisanAndTheTestsRunAndAreDebuggedInSailsContainer()
    {
        if (V("PYMAGIC_TEST_SAIL_DIR") is not { } dir) return;
        string script = Path.Combine(dir, "pm-sail.php");
        File.WriteAllText(script, "<?php\n$where = PHP_OS_FAMILY . ' ' . getcwd();\necho $where, PHP_EOL;\n$n = 41 + 1;\necho \"n=$n\" . PHP_EOL;\n");   // line 5 one statement (`echo a, b` is two, and Xdebug stops at each)
        try
        {
            // a script: its output, from the container (Linux, in /var/www/html)
            using (var run = new Run())
            {
                run.Session.Php = "sail";
                run.Session.StartPhp(dir, "pm-sail.php", null, false, Array.Empty<string>(), null);
                Assert.Equal(0, run.Next(e => Is(e, "exited"))["code"]!.GetValue<int>());
                Assert.Contains("Linux /var/www/html", run.Output);
                Assert.Contains("n=42", run.Output);
            }
            // debugged: Xdebug there stops at the breakpoint, in the file here
            using (var debug = new Run())
            {
                debug.Session.Php = "sail";
                debug.Session.StartPhp(dir, "pm-sail.php", null, true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["pm-sail.php"] = new JsonArray(new JsonObject { ["line"] = 5 }) });
                var stop = debug.Next(e => Is(e, "stopped"));
                Assert.True(string.Equals(script, stop["frames"]![0]!["file"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), stop.ToJsonString());
                Assert.Equal(5, stop["frames"]![0]!["line"]!.GetValue<int>());
                debug.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "$n" });
                Assert.Equal("42", debug.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
                debug.Session.Handle(new JsonObject { ["type"] = "continue" });
                var after = debug.Next(e => Is(e, "exited") || Is(e, "stopped"));
                Assert.True(Is(after, "exited"), "stopped again: " + after.ToJsonString() + " | output: " + debug.Output);
                Assert.Equal(0, after["code"]!.GetValue<int>());
            }
            // Artisan's commands, listed there
            Assert.True(PhpTools.ArtisanCommands("sail", dir).Any(c => c!["name"]!.GetValue<string>() == "make:model"), "make:model");
            // the tests (Pest, as the app has it), their places the project's files here
            File.WriteAllText(Path.Combine(dir, "tests", "Unit", "PmSailTest.php"), "<?php\n\ntest('runs in sail', function () {\n    expect(getcwd())->toBe('/var/www/html');\n});\n\ntest('fails in sail', function () {\n    expect(1)->toBe(2);\n});\n");
            using (var tests = new Run())
            {
                tests.Session.Php = "sail";
                tests.Session.StartPhpTests(dir, new[] { "tests/Unit/PmSailTest.php" }, "auto");
                tests.Next(e => Is(e, "exited"));
                var results = tests.Seen.Where(e => Is(e, "testResult")).ToDictionary(e => e["id"]!.GetValue<string>(), e => e);
                Assert.Equal("passed", results["tests/Unit/PmSailTest.php::runs in sail"]["outcome"]!.GetValue<string>());
                var failed = results["tests/Unit/PmSailTest.php::fails in sail"];
                Assert.Equal("failed", failed["outcome"]!.GetValue<string>());
                Assert.Equal("tests/Unit/PmSailTest.php", failed["file"]!.GetValue<string>());
                Assert.Equal(8, failed["line"]!.GetValue<int>());
            }
            // Format with the project's Pint, there
            Assert.Equal("<?php\n\n$a = [1, 2];\n", PhpFormat.Format(dir, "sail", "<?php\n$a=array(1,2);\n"));
        }
        finally
        {
            File.Delete(script);
            File.Delete(Path.Combine(dir, "tests", "Unit", "PmSailTest.php"));
        }
    }

    [Fact]
    public void SailsServerAnswersAndStopsAtARouteBreakpointUnderTheDebugger()
    {
        if (V("PYMAGIC_TEST_SAIL_DIR") is not { } dir || V("APP_PORT") is not { } port) return;
        string routes = Path.Combine(dir, "routes", "web.php");
        string before = File.ReadAllText(routes);
        File.WriteAllText(routes, before + "\nRoute::get('/pm-sail', function () {\n    $where = PHP_OS_FAMILY;\n    return 'sail ' . $where;\n});\n");
        int line = before.Split('\n').Length + 3;   // return 'sail ' . $where;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            // Run: the container's server answers
            using (var serve = new Run())
            {
                serve.Session.Php = "sail";
                serve.Session.StartPhp(dir, null, new[] { "serve" }, false, Array.Empty<string>(), null);
                serve.Next(_ => serve.Output.Contains($"localhost:{port}"));
                string body = "";
                for (var until = DateTime.UtcNow.AddSeconds(60); !body.Contains("sail Linux") && DateTime.UtcNow < until; Thread.Sleep(1000))
                    try { body = http.GetStringAsync($"http://localhost:{port}/pm-sail").GetAwaiter().GetResult(); } catch (HttpRequestException) { }
                Assert.Equal("sail Linux", body);
                serve.Session.Handle(new JsonObject { ["type"] = "stop" });
                serve.Next(e => Is(e, "exited"));
            }
            // Debug: the container made again with Xdebug for PyMagic; a request stops at the route, its value read, then answered
            using (var debug = new Run())
            {
                debug.Session.Php = "sail";
                var bps = new Dictionary<string, JsonArray> { [Path.Combine("routes", "web.php")] = new JsonArray(new JsonObject { ["line"] = line }) };
                debug.Session.StartPhp(dir, null, new[] { "serve" }, true, Array.Empty<string>(), bps);
                debug.Next(_ => debug.Output.Contains($"localhost:{port}"));
                // "Laravel runs…" is said once the server started again for Xdebug listens
                var page = http.GetStringAsync($"http://localhost:{port}/pm-sail");
                var stop = debug.Next(e => Is(e, "stopped"), 60000);
                Assert.True(string.Equals(routes, stop["frames"]![0]!["file"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), stop.ToJsonString());
                Assert.Equal(line, stop["frames"]![0]!["line"]!.GetValue<int>());
                debug.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "$where" });
                Assert.Equal("\"Linux\"", debug.Next(e => Is(e, "evaluated"))["result"]!.GetValue<string>());
                debug.Session.Handle(new JsonObject { ["type"] = "continue" });
                Assert.Equal("sail Linux", page!.GetAwaiter().GetResult());
                debug.Session.Handle(new JsonObject { ["type"] = "stop" });
                debug.Next(e => Is(e, "exited"));
                Assert.Contains("without PyMagic's Xdebug", debug.Output);
            }
            // made again as the .env says: Xdebug off in the container
            var sail = SailTarget.Of(dir, "sail")!;
            Assert.Equal("off", sail.Exec("printenv XDEBUG_MODE", 60_000).Out.Trim());
        }
        finally { File.WriteAllText(routes, before); }
    }
}
