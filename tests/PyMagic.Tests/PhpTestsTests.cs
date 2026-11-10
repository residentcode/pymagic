using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PHP's tests in the Tests panel (PhpTests, TeamCityReader, RunSession.StartPhpTests): PHPUnit's classes and Pest's tests found in the files,
/// the --teamcity messages of real runs read as the panel's, the command line of a choice of tests, and real runs of PHPUnit (Laravel's app,
/// PYMAGIC_TEST_LARAVEL_DIR) and Pest (PYMAGIC_TEST_PEST_DIR) with the PHP PYMAGIC_PHP names.
/// </summary>
public class PhpTestsTests
{
    const string PhpUnitFile = """
        <?php

        namespace Tests\Unit;

        use PHPUnit\Framework\Attributes\DataProvider;
        use PHPUnit\Framework\Attributes\Test;
        use PHPUnit\Framework\TestCase;

        abstract class BaseTest extends TestCase
        {
            public function test_in_the_base(): void {}
        }

        class MathTest extends TestCase
        {
            public function test_adds(): void
            {
                $this->assertSame(4, 2 + 2); // a } in a comment
            }

            #[Test]
            public function it_is_marked(): void
            {
                $this->assertTrue(true);
            }

            /** @test */
            public function docblock_marked(): void {}

            public function helper(): string { return '{'; }

            #[DataProvider('pairs')]
            public function test_pairs(int $a, int $b): void
            {
                $this->assertSame($a, $b);
            }

            public static function pairs(): array
            {
                return ['same' => [1, 1]];
            }
        }
        """;

    const string PestFile = """
        <?php

        test('adds numbers', function () {
            expect(2 + 2)->toBe(4);
        });

        it('fails on purpose', function () {
            expect(2 + 2)->toBe(5);
        });

        it('is skipped')->skip('not today');

        describe('math', function () {
            it('multiplies', function () {
                expect(2 * 3)->toBe(6);
            });
        });

        it('checks pairs', function (int $a, int $b) {
            expect($a)->toBe($b);
        })->with(['same' => [1, 1], 'different' => [1, 2]]);
        """;

    static void Same(IEnumerable<string> expected, IEnumerable<string> actual) => Assert.Equal(string.Join(" | ", expected), string.Join(" | ", actual));

    [Fact]
    public void PhpUnitClassesAndPestTestsAreFoundInTheirFiles()
    {
        var unit = PhpTests.DiscoverFile("tests/Unit/MathTest.php", PhpUnitFile)!;
        Assert.Equal("tests/Unit/MathTest.php", unit.Id);
        Assert.Equal(1, unit.Children.Count);   // the abstract base is no test class
        var cls = unit.Children[0];
        Assert.Equal("tests/Unit/MathTest.php::MathTest", cls.Id);
        Same(new[] { "test_adds", "it_is_marked", "docblock_marked", "test_pairs" }, cls.Children.Select(c => c.Name).ToArray());
        var adds = cls.Children[0];
        Assert.Equal("tests/Unit/MathTest.php::MathTest::test_adds", adds.Id);
        Assert.Equal(16, adds.Line);
        Assert.Equal(19, adds.EndLine);

        var pest = PhpTests.DiscoverFile("tests/Unit/PestSampleTest.php", PestFile)!;
        Same(new[] { "adds numbers", "it fails on purpose", "it is skipped", "`math` → it multiplies", "it checks pairs" }, pest.Children.Select(c => c.Name).ToArray());
        Assert.Equal("tests/Unit/PestSampleTest.php::`math` → it multiplies", pest.Children[3].Id);
        Assert.Equal(14, pest.Children[3].Line);
        // PHPUnit 12 reads attributes only: a docblock's @test is no test there
        Same(new[] { "test_adds", "it_is_marked", "test_pairs" }, PhpTests.DiscoverFile("tests/Unit/MathTest.php", PhpUnitFile, docblocks: false)!.Children[0].Children.Select(c => c.Name));
        Assert.True(PhpTests.DiscoverFile("app/Models/User.php", "<?php\nclass User extends Model { public function posts() {} }") == null, "a class without tests");
    }

    [Fact]
    public void TeamCityMessagesOfPhpUnitAndPestAreThePanels()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_tc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "tests", "Unit"));
        File.WriteAllText(Path.Combine(root, "tests", "Unit", "MathTest.php"), PhpUnitFile);
        File.WriteAllText(Path.Combine(root, "tests", "Unit", "PestSampleTest.php"), PestFile);
        string? Rel(string p) { string full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root, p)); return File.Exists(full) ? Path.GetRelativePath(root, full).Replace('\\', '/') : null; }
        try
        {
            string file = Path.Combine(root, "tests", "Unit", "MathTest.php");
            // PHPUnit 12's own lines (a run of the sample in Laravel's app), the project's path in them
            string phpunit = string.Join("\n", new[]
            {
                "PHPUnit 12.5.43 by Sebastian Bergmann and contributors.",
                $"##teamcity[testSuiteStarted name='Tests\\Unit\\MathTest' locationHint='php_qn://{file}::\\Tests\\Unit\\MathTest' flowId='36152']",
                $"##teamcity[testStarted name='test_adds' locationHint='php_qn://{file}::\\Tests\\Unit\\MathTest::test_adds' flowId='36152']",
                "##teamcity[testFinished name='test_adds' duration='1' flowId='36152']",
                $"##teamcity[testStarted name='test_fails' locationHint='php_qn://{file}::\\Tests\\Unit\\MathTest::test_fails' flowId='36152']",
                $"##teamcity[testFailed name='test_fails' message='two and two|nFailed asserting that 4 is identical to 5.' details='{file}:18|n' duration='14' flowId='36152']",
                "##teamcity[testFinished name='test_fails' duration='14' flowId='36152']",
                $"##teamcity[testStarted name='it_is_skipped' locationHint='php_qn://{file}::\\Tests\\Unit\\MathTest::it_is_skipped' flowId='36152']",
                "##teamcity[testIgnored name='it_is_skipped' message='not today' duration='1' flowId='36152']",
                "##teamcity[testFinished name='it_is_skipped' duration='1' flowId='36152']",
                $"##teamcity[testStarted name='test_pairs with data set \"same\"' locationHint='php_qn://{file}::\\Tests\\Unit\\MathTest::test_pairs with data set \"same\"' flowId='36152']",
                "##teamcity[testFinished name='test_pairs with data set \"same\"' duration='1' flowId='36152']",
                "Time: 00:00.021, Memory: 20.00 MB",
                "",
            });
            var reader = new TeamCityReader(Rel);
            // in pieces, as a pipe gives them: a message cut in two is read when its line is whole
            var events = new List<JsonObject>();
            for (int i = 0; i < phpunit.Length; i += 37) events.AddRange(reader.Feed(phpunit.Substring(i, Math.Min(37, phpunit.Length - i))));
            events.AddRange(reader.Flush());
            var results = events.Where(e => e["type"]!.GetValue<string>() == "testResult").ToList();
            Same(new[] { "tests/Unit/MathTest.php::MathTest::test_adds", "tests/Unit/MathTest.php::MathTest::test_fails", "tests/Unit/MathTest.php::MathTest::it_is_skipped", "tests/Unit/MathTest.php::MathTest::test_pairs[same]" },
                results.Select(r => r["id"]!.GetValue<string>()).ToArray());
            Same(new[] { "passed", "failed", "skipped", "passed" }, results.Select(r => r["outcome"]!.GetValue<string>()).ToArray());
            Assert.Equal("two and two\nFailed asserting that 4 is identical to 5.", results[1]["message"]!.GetValue<string>());
            Assert.Equal("tests/Unit/MathTest.php", results[1]["file"]!.GetValue<string>());
            Assert.Equal(18, results[1]["line"]!.GetValue<int>());
            Assert.True(Math.Abs(results[1]["duration"]!.GetValue<double>() - 0.014) < 1e-9, "the duration in seconds");
            Assert.Equal(4, events.Count(e => e["type"]!.GetValue<string>() == "testStarted"));
            string output = string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "output").Select(e => e["text"]!.GetValue<string>()));
            Assert.Contains("PHPUnit 12.5.43", output);
            Assert.Contains("Time: 00:00.021", output);
            Assert.True(!output.Contains("##teamcity"), output);

            // Pest 5's lines (a run on Windows: its paths relative, with backslashes)
            string pest = string.Join("\n", new[]
            {
                "##teamcity[testStarted name='adds numbers' locationHint='pest_qn://tests\\Unit\\PestSampleTest.php::adds numbers' flowId='1']",
                "##teamcity[testFinished name='adds numbers' duration='6' flowId='1']",
                "##teamcity[testStarted name='it fails on purpose' locationHint='pest_qn://tests\\Unit\\PestSampleTest.php::it fails on purpose' flowId='1']",
                "##teamcity[testFailed name='it fails on purpose' message='Failed asserting that 4 is identical to 5.' details='at tests\\Unit\\PestSampleTest.php:8' flowId='1']",
                "##teamcity[testFinished name='it fails on purpose' duration='6' flowId='1']",
                "##teamcity[testStarted name='`math` → it multiplies' locationHint='pest_qn://tests\\Unit\\PestSampleTest.php::`math` → it multiplies' flowId='1']",
                "##teamcity[testFinished name='`math` → it multiplies' duration='0' flowId='1']",
                "##teamcity[testStarted name='it checks pairs with data set \"dataset \"different\"\"' locationHint='pest_qn://tests\\Unit\\PestSampleTest.php::it checks pairs with data set \"dataset \"different\"\"' flowId='1']",
                "##teamcity[testFailed name='it checks pairs with data set \"dataset \"different\"\"' message='Failed asserting that 1 is identical to 2.' details='at tests\\Unit\\PestSampleTest.php:20' flowId='1']",
                "##teamcity[testFinished name='it checks pairs with data set \"dataset \"different\"\"' duration='1' flowId='1']",
                "",
            });
            var pestResults = new TeamCityReader(Rel).Feed(pest).Where(e => e["type"]!.GetValue<string>() == "testResult").ToList();
            Same(new[] { "tests/Unit/PestSampleTest.php::adds numbers", "tests/Unit/PestSampleTest.php::it fails on purpose", "tests/Unit/PestSampleTest.php::`math` → it multiplies", "tests/Unit/PestSampleTest.php::it checks pairs[different]" },
                pestResults.Select(r => r["id"]!.GetValue<string>()).ToArray());
            Assert.Equal(8, pestResults[1]["line"]!.GetValue<int>());
            Assert.Equal("tests/Unit/PestSampleTest.php", pestResults[1]["file"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AChoiceOfTestsIsItsFilesAndAFilterOfTheirNames()
    {
        Same(new[] { "--teamcity", "--colors=never" }, PhpTests.Arguments("phpunit", Array.Empty<string>()));
        Same(new[] { "--teamcity", "--colors=never", "tests/Unit/MathTest.php", "tests/Feature/ExampleTest.php" },
            PhpTests.Arguments("phpunit", new[] { "tests/Unit/MathTest.php", "tests/Feature/ExampleTest.php::ExampleTest" }));
        var some = PhpTests.Arguments("phpunit", new[] { "tests/Unit/MathTest.php::MathTest::test_adds", "tests/Unit/MathTest.php::MathTest::test_pairs[same]" });
        Assert.Equal("tests/Unit/MathTest.php", some[2]);
        Assert.Equal("--filter", some[3]);
        Assert.Equal("/(?:MathTest::test_adds(?: with data set .*)?|MathTest::test_pairs with data set \"same\")$/", some[4]);
        var pest = PhpTests.Arguments("pest", new[] { "tests/Unit/PestSampleTest.php::it checks pairs[same]" });
        Assert.Equal("/(?:it\\ checks\\ pairs(?: with data set .*)?)$/", pest[4]);
    }

    // ------------------------------------------------------------ real runs

    static string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    sealed class Run : IDisposable
    {
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        public Run() { Session = new RunSession(Events.Add); }
        public List<JsonNode> UntilExit(int ms = 180000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (true)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Events.TryTake(out var e, left)) throw new AssertionException("timed out; saw: " + string.Join(" ", Seen.Select(s => s.ToJsonString()).TakeLast(20)));
                Seen.Add(e);
                if (e["type"]?.GetValue<string>() is "exited" or "error") return Seen;
            }
        }
        public void Dispose() => Session.Dispose();
    }

    static string CopyOf(string source, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_phptests_" + name + "_" + Guid.NewGuid().ToString("N")[..8]);
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

    static Dictionary<string, string> Outcomes(List<JsonNode> seen) =>
        seen.Where(e => e["type"]?.GetValue<string>() == "testResult").ToDictionary(e => e["id"]!.GetValue<string>(), e => e["outcome"]!.GetValue<string>());

    [Fact]
    public void PhpUnitRunsALaravelAppsTestsAndAChoiceOfThem()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_LARAVEL_DIR") is not { } source || !File.Exists(Path.Combine(source, "vendor", "bin", "phpunit"))) return;
        string dir = CopyOf(source, "phpunit");
        File.WriteAllText(Path.Combine(dir, "tests", "Unit", "MathTest.php"), PhpUnitFile.Replace("$this->assertSame(4, 2 + 2); // a } in a comment", "$this->assertSame(5, 2 + 2, 'two and two');"));
        try
        {
            // found: Laravel's two examples and the sample
            var found = PhpTests.Discover(dir).Select(f => f.Id).ToList();
            Assert.True(found.Contains("tests/Unit/MathTest.php") && found.Contains("tests/Feature/ExampleTest.php"), string.Join(",", found));
            Assert.True(RunSession.IsPhpTestRun(dir, new[] { "tests/Unit/MathTest.php::MathTest::test_adds" }, "auto"));
            Assert.True(RunSession.IsPhpTestRun(dir, Array.Empty<string>(), "auto"), "a PHP project without Python tests");
            Assert.True(!RunSession.IsPhpTestRun(dir, new[] { "tests/test_a.py::test_x" }, "auto"));

            // everything
            using (var all = new Run())
            {
                all.Session.Php = php;
                all.Session.StartPhpTests(dir, Array.Empty<string>(), "auto");
                var outcomes = Outcomes(all.UntilExit());
                Assert.Equal("failed", outcomes["tests/Unit/MathTest.php::MathTest::test_adds"]);
                Assert.Equal("passed", outcomes["tests/Unit/MathTest.php::MathTest::it_is_marked"]);
                // Laravel's PHPUnit is 12: the docblock's @test is neither found nor run
                Assert.True(!outcomes.ContainsKey("tests/Unit/MathTest.php::MathTest::docblock_marked"), "docblock_marked ran");
                Assert.True(!PhpTests.Discover(dir).Single(f => f.Id == "tests/Unit/MathTest.php").Children[0].Children.Any(c => c.Name == "docblock_marked"), "docblock_marked found");
                Assert.Equal("passed", outcomes["tests/Unit/MathTest.php::MathTest::test_pairs[same]"]);
                Assert.Equal("passed", outcomes["tests/Feature/ExampleTest.php::ExampleTest::test_the_application_returns_a_successful_response"]);
                Assert.True(!outcomes.Keys.Any(k => k.Contains("BaseTest")), string.Join(",", outcomes.Keys));
                var failed = all.Seen.First(e => e["type"]?.GetValue<string>() == "testResult" && e["outcome"]!.GetValue<string>() == "failed");
                Assert.Equal("tests/Unit/MathTest.php", failed["file"]!.GetValue<string>());
                Assert.Equal(18, failed["line"]!.GetValue<int>());
                Assert.Contains("two and two", failed["message"]!.GetValue<string>());
            }
            // one test and one case of a data set
            using var some = new Run();
            some.Session.Php = php;
            some.Session.StartPhpTests(dir, new[] { "tests/Unit/MathTest.php::MathTest::it_is_marked", "tests/Unit/MathTest.php::MathTest::test_pairs[same]" }, "auto");
            var chosen = Outcomes(some.UntilExit());
            Same(new[] { "tests/Unit/MathTest.php::MathTest::it_is_marked", "tests/Unit/MathTest.php::MathTest::test_pairs[same]" }, chosen.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void ATestDebuggedStopsAtItsBreakpointAndItsResultArrives()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_LARAVEL_DIR") is not { } source || !File.Exists(Path.Combine(source, "vendor", "bin", "phpunit"))) return;
        if (PhpTools.FindXdebug(php, null) is not { Error: null }) return;
        string dir = CopyOf(source, "phpdebug");
        File.WriteAllText(Path.Combine(dir, "tests", "Unit", "MathTest.php"), PhpUnitFile);
        try
        {
            using var run = new Run();
            run.Session.Php = php;
            // line 18: $this->assertSame(4, 2 + 2) of test_adds
            var bps = new Dictionary<string, JsonArray> { [Path.Combine("tests", "Unit", "MathTest.php")] = new JsonArray(new JsonObject { ["line"] = 18 }) };
            run.Session.StartPhpTests(dir, new[] { "tests/Unit/MathTest.php::MathTest::test_adds" }, "auto", debug: true, bps);
            JsonNode Next(string type, int ms = 120000)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (true)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !run.Events.TryTake(out var e, left)) throw new AssertionException("timed out waiting for " + type + "; saw: " + string.Join(" ", run.Seen.Select(s => s.ToJsonString()).TakeLast(15)));
                    run.Seen.Add(e);
                    if (e["type"]?.GetValue<string>() == type) return e;
                }
            }
            var stop = Next("stopped");
            var top = stop["frames"]![0]!;
            Assert.True(top["file"]!.GetValue<string>().EndsWith(Path.Combine("tests", "Unit", "MathTest.php"), StringComparison.OrdinalIgnoreCase), stop.ToJsonString());
            Assert.Equal(18, top["line"]!.GetValue<int>());
            run.Session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "$this->name()" });
            Assert.Equal("\"test_adds\"", Next("evaluated")["result"]!.GetValue<string>());
            run.Session.Handle(new JsonObject { ["type"] = "continue" });
            var result = Next("testResult");
            Assert.Equal("tests/Unit/MathTest.php::MathTest::test_adds", result["id"]!.GetValue<string>());
            Assert.Equal("passed", result["outcome"]!.GetValue<string>());
            Next("exited");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void PestRunsItsTestsAndTheOnesOfADescribe()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_PEST_DIR") is not { } source || !File.Exists(Path.Combine(source, "vendor", "bin", "pest"))) return;
        string dir = CopyOf(source, "pest");
        File.WriteAllText(Path.Combine(dir, "tests", "Unit", "PestSampleTest.php"), PestFile);
        try
        {
            Assert.Equal("pest", PhpTests.Runner(dir));
            var file = PhpTests.Discover(dir).Single(f => f.Id == "tests/Unit/PestSampleTest.php");
            Assert.Equal(5, file.Children.Count);
            using (var all = new Run())
            {
                all.Session.Php = php;
                all.Session.StartPhpTests(dir, new[] { "tests/Unit/PestSampleTest.php" }, "auto");
                var outcomes = Outcomes(all.UntilExit());
                Assert.Equal("passed", outcomes["tests/Unit/PestSampleTest.php::adds numbers"]);
                Assert.Equal("failed", outcomes["tests/Unit/PestSampleTest.php::it fails on purpose"]);
                Assert.Equal("skipped", outcomes["tests/Unit/PestSampleTest.php::it is skipped"]);
                Assert.Equal("passed", outcomes["tests/Unit/PestSampleTest.php::`math` → it multiplies"]);
                Assert.Equal("passed", outcomes["tests/Unit/PestSampleTest.php::it checks pairs[same]"]);
                Assert.Equal("failed", outcomes["tests/Unit/PestSampleTest.php::it checks pairs[different]"]);
            }
            using var one = new Run();
            one.Session.Php = php;
            one.Session.StartPhpTests(dir, new[] { "tests/Unit/PestSampleTest.php::`math` → it multiplies" }, "auto");
            Same(new[] { "tests/Unit/PestSampleTest.php::`math` → it multiplies" }, Outcomes(one.UntilExit()).Keys.ToArray());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void CloverReportsAreTheTestsPanelsCoverage()
    {
        const string clover = """
            <?xml version="1.0" encoding="UTF-8"?>
            <coverage generated="1"><project timestamp="1">
              <package name="App"><file name="/var/www/html/app/Support/Calc.php">
                <class name="App\Support\Calc" namespace="App\Support"><metrics/></class>
                <line num="7" type="method" name="sign" visibility="public" complexity="2" crap="2" count="1"/>
                <line num="9" type="stmt" count="1"/>
                <line num="10" type="stmt" count="0"/>
                <line num="12" type="stmt" count="3"/>
                <metrics loc="14"/>
              </file></package>
              <file name="/somewhere/else/vendor/x.php"><line num="1" type="stmt" count="1"/></file>
            </project></coverage>
            """;
        var data = PhpTests.CloverToCoverage(clover, path => path.StartsWith("/var/www/html/") ? path["/var/www/html/".Length..] : null);
        var files = data["files"]!.AsObject();
        Assert.Equal(1, files.Count);
        Assert.Equal("[7,9,10,12]", files["app/Support/Calc.php"]!["executable"]!.ToJsonString());
        Assert.Equal("[7,9,12]", files["app/Support/Calc.php"]!["executed"]!.ToJsonString());

        // the code measured: phpunit.xml's <source>, else the app/ (or src/) folder named on the command line
        string dir = Path.Combine(Path.GetTempPath(), "pm_phpcov_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "app"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", "Unit"));
        File.WriteAllText(Path.Combine(dir, "tests", "Unit", "ATest.php"), "<?php class ATest extends PHPUnit\\Framework\\TestCase { public function test_a(): void {} }");
        try
        {
            File.WriteAllText(Path.Combine(dir, "phpunit.xml"), "<phpunit><testsuites/></phpunit>");
            Assert.Equal("--coverage-clover .pymagic/php-clover.xml --coverage-filter app", string.Join(" ", PhpTests.CoverageArguments(dir)));
            File.WriteAllText(Path.Combine(dir, "phpunit.xml"), "<phpunit><source>\n<include><directory>app</directory></include></source></phpunit>");
            Assert.Equal("--coverage-clover .pymagic/php-clover.xml", string.Join(" ", PhpTests.CoverageArguments(dir)));
            // a PHP tests configuration's folder is a PHP run; a folder without PHP tests is not
            Assert.True(RunSession.IsPhpTestRun(dir, new[] { "tests/Unit" }, "auto"));
            Assert.True(!RunSession.IsPhpTestRun(dir, new[] { "app" }, "auto"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void ACoverageRunKeepsTheLinesItsTestsRan()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_LARAVEL_DIR") is not { } source || !File.Exists(Path.Combine(source, "vendor", "bin", "phpunit"))) return;
        if (PhpTools.FindXdebug(php, null).Error != null) return;   // a PHP without Xdebug measures nothing
        string dir = CopyOf(source, "coverage");
        Directory.CreateDirectory(Path.Combine(dir, "app", "Support"));
        File.WriteAllText(Path.Combine(dir, "app", "Support", "Calc.php"), """
            <?php

            namespace App\Support;

            class Calc
            {
                public static function sign(int $n): string
                {
                    if ($n < 0) {
                        return 'negative';
                    }
                    return 'positive';
                }
            }
            """.Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(dir, "tests", "Unit", "CalcTest.php"), """
            <?php

            namespace Tests\Unit;

            use App\Support\Calc;
            use PHPUnit\Framework\TestCase;

            class CalcTest extends TestCase
            {
                public function test_positive(): void
                {
                    $this->assertSame('positive', Calc::sign(3));
                }
            }
            """);
        try
        {
            using var run = new Run();
            run.Session.Php = php;
            run.Session.StartPhpTests(dir, new[] { "tests/Unit/CalcTest.php" }, "auto", coverage: true);
            var seen = run.UntilExit();
            Assert.Equal("passed", Outcomes(seen)["tests/Unit/CalcTest.php::CalcTest::test_positive"]);
            var data = JsonNode.Parse(File.ReadAllText(RunSession.CoveragePath(dir)))!;
            var calc = data["files"]!["app/Support/Calc.php"];
            Assert.True(calc != null, data.ToJsonString()[..Math.Min(400, data.ToJsonString().Length)]);
            var executed = calc!["executed"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
            var executable = calc["executable"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
            // the if and the positive return ran; the negative one did not
            Assert.True(executed.Contains(9) && executed.Contains(12) && !executed.Contains(10) && executable.Contains(10), $"{string.Join(",", executable)} / {string.Join(",", executed)}");
            Assert.True(CoverageSuites.List(dir).Count >= 1, "kept as a suite");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
