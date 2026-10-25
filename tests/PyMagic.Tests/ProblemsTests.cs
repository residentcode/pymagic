using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The project-wide lists behind the Problems and TODO panels.</summary>
public class ProblemsTests
{
    static (LanguageService Svc, string A, string B) Project()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_probs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.py"), "x = undefined_name\n# TODO: fix this\ny = 'TODO not a comment'\n");
        File.WriteAllText(Path.Combine(dir, "b.py"), "def f(:\n    pass\n\n# FIXME later\n# XXX hmm\n");
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        return (svc, LanguageService.UriOfPath(Path.Combine(dir, "a.py")), LanguageService.UriOfPath(Path.Combine(dir, "b.py")));
    }

    [Fact]
    public void ProblemsOfFilesThatAreNotOpen()
    {
        var (svc, a, b) = Project();
        var problems = svc.Problems(2);
        Assert.True(problems.Any(p => p.Uri == a && p.Message.Contains("undefined_name")), string.Join(" / ", problems.Select(p => p.Message)));
        Assert.True(problems.Any(p => p.Uri == b && p.Severity == Severity.Error), "a syntax error in the other file");
    }

    [Fact]
    public void WorstFirstAndFilteredBySeverity()
    {
        var (svc, _, _) = Project();
        var all = svc.Problems(2);
        Assert.True(all[0].Severity == Severity.Error, "errors lead");
        var errorsOnly = svc.Problems(1);
        Assert.True(errorsOnly.Count > 0 && errorsOnly.All(p => p.Severity == Severity.Error));
        Assert.True(errorsOnly.Count < all.Count);
    }

    [Fact]
    public void TodoCommentsAreFoundInCommentsOnly()
    {
        var (svc, a, b) = Project();
        var todos = svc.Todos();
        Assert.Equal(3, todos.Count);
        Assert.True(todos.Any(t => t.Uri == a && t.Tag == "TODO" && t.Text == "fix this" && t.Line == 1));
        Assert.True(todos.Any(t => t.Uri == b && t.Tag == "FIXME" && t.Text == "later"));
        Assert.True(todos.Any(t => t.Tag == "XXX"));
    }
}
