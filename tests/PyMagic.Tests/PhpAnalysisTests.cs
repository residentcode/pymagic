using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PHPStan's problems of a file (PhpAnalysis): its JSON report read for the one file, and a real run of PHPStan with Larastan (a Laravel app with
/// larastan/larastan and a phpstan.neon that includes it: PYMAGIC_TEST_STAN_DIR, the PHP PYMAGIC_PHP names).
/// </summary>
public class PhpAnalysisTests
{
    static string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    public void TheReportOfTheFileIsItsProblems()
    {
        string report = "Note: Using configuration file C:\\app\\phpstan.neon.\n" +
            "{\"totals\":{\"errors\":0,\"file_errors\":2},\"files\":{\"C:\\\\app\\\\app\\\\Models\\\\Bad.php\":{\"errors\":2,\"messages\":[" +
            "{\"message\":\"Call to an undefined method App\\\\Models\\\\User::nope().\",\"line\":12,\"ignorable\":true,\"identifier\":\"method.notFound\"}," +
            "{\"message\":\"Method App\\\\Models\\\\Bad::count() should return int but returns string.\",\"line\":17,\"ignorable\":true,\"identifier\":\"return.type\",\"tip\":\"Fix the type\"}]}},\"errors\":[]}";
        var problems = PhpAnalysis.Problems(report, 1, "app/Models/Bad.php");
        Assert.Equal(2, problems.Count);
        Assert.Equal(12, problems[0]!["line"]!.GetValue<int>());
        Assert.Equal("method.notFound", problems[0]!["identifier"]!.GetValue<string>());
        Assert.Equal("Fix the type", problems[1]!["tip"]!.GetValue<string>());
        Assert.Equal(0, PhpAnalysis.Problems("{\"totals\":{\"errors\":0,\"file_errors\":0},\"files\":[],\"errors\":[]}", 0, "app/Models/Bad.php").Count);
        // PHPStan 2.3's own report when an AI coding agent started it (AI_AGENT, CLAUDECODE in its environment)
        var agent = PhpAnalysis.Problems("{\"tool\":\"phpstan\",\"result\":\"failed\",\"errors\":1,\"error_details\":{\"C:\\\\app\\\\app\\\\Models\\\\Bad.php\":[{\"line\":9,\"message\":\"Method App\\\\Models\\\\Bad::count() should return int but returns string.\",\"identifier\":\"return.type\"}]},\"instructions\":\"…\"}", 1, "app/Models/Bad.php");
        Assert.Equal(1, agent.Count);
        Assert.Equal(9, agent[0]!["line"]!.GetValue<int>());
        try { PhpAnalysis.Problems("PHP Fatal error: Allowed memory size exhausted\n", 255, "app/Models/Bad.php"); Assert.True(false, "a crash read as a report"); }
        catch (ServiceException e) { Assert.Contains("Allowed memory size exhausted", e.Message); }
    }

    [Fact]
    public void PhpStanWithLarastanFindsTheProblemsOfAFile()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_STAN_DIR") is not { } dir || !PhpAnalysis.Available(dir)) return;
        string rel = "app/Models/PmBad.php";
        string file = Path.Combine(dir, "app", "Models", "PmBad.php");
        File.WriteAllText(file, string.Join("\n", new[]
        {
            "<?php",                                                         // 1
            "",                                                              // 2
            "namespace App\\Models;",                                        // 3
            "",                                                              // 4
            "class PmBad",                                                   // 5
            "{",                                                             // 6
            "    public function count(): int",                              // 7
            "    {",                                                         // 8
            "        return 'many';",                                        // 9
            "    }",                                                         // 10
            "",                                                              // 11
            "    public function first(): ?User",                            // 12
            "    {",                                                         // 13
            "        return User::query()->where('email', 'a@b.c')->nope();",// 14: Larastan knows the Eloquent builder
            "    }",                                                         // 15
            "}",                                                             // 16
            "",
        }));
        try
        {
            var problems = PhpAnalysis.Analyse(dir, php, rel);
            string all = problems.ToJsonString();
            Assert.True(problems.Any(p => p!["line"]!.GetValue<int>() == 9 && p["message"]!.GetValue<string>().Contains("should return int but returns string")), all);
            Assert.True(problems.Any(p => p!["line"]!.GetValue<int>() == 14 && p["message"]!.GetValue<string>().Contains("nope")), all);
            // a clean file has none
            File.WriteAllText(file, "<?php\n\nnamespace App\\Models;\n\nclass PmBad\n{\n    public function count(): int\n    {\n        return 1;\n    }\n}\n");
            Assert.Equal(0, PhpAnalysis.Analyse(dir, php, rel).Count);
            try { PhpAnalysis.Analyse(dir, php, "../outside.php"); Assert.True(false, "a file outside the project"); }
            catch (ServiceException e) { Assert.Contains("not a file of the project", e.Message); }
        }
        finally { File.Delete(file); }
    }
}
