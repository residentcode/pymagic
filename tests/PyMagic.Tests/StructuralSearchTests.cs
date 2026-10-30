using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Structural search and replace, and finding duplicated code (LanguageService.StructuralSearch.cs).</summary>
public class StructuralSearchTests
{
    const string A = "file:///c:/proj/a.py";
    const string B = "file:///c:/proj/b.py";

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static LanguageService Service(string a, string? b = null)
    {
        var svc = new LanguageService();
        svc.Open(A, a, 1);
        if (b != null) svc.Open(B, b, 1);
        return svc;
    }

    static string Found(LanguageService svc, string pattern, string? replacement = null) =>
        string.Join(" | ", svc.StructuralSearch(pattern, replacement).Select(m =>
            $"{m.Range.Start.Line + 1}:{m.Range.Start.Character}-{m.Range.End.Line + 1}:{m.Range.End.Character}" + (m.Replacement == null ? "" : " => " + m.Replacement)));

    [Fact]
    public void FindsExpressionsByTheirShape()
    {
        var svc = Service(Src("x = len(items) == 0", "y = len( names )==0", "z = len(items) == 1", "w = size(items) == 0"));
        Assert.Equal("1:4-1:19 | 2:4-2:19", Found(svc, "len($x$) == 0"));
        // the replacement takes the variables
        Assert.Equal("1:4-1:19 => not items | 2:4-2:19 => not names", Found(svc, "len($x$) == 0", "not $x$"));
        // a variable used twice must be the same both times
        var same = Service(Src("a = x + x", "b = x + y"));
        Assert.Equal("1:4-1:9", Found(same, "$a$ + $a$"));
    }

    [Fact]
    public void ManyVariablesTakeAnyNumberOfArgumentsAndConditionsFilter()
    {
        var svc = Service(Src("print()", "print(1)", "print(1, 2, sep='')", "log(1)"));
        Assert.Equal("1:0-1:7 | 2:0-2:8 | 3:0-3:19", Found(svc, "print($args*$)"));
        Assert.Equal("2:0-2:8", Found(svc, "print($a$)"));
        var tests = Service(Src("def test_one():", "    pass", "", "def helper():", "    pass", "", "def test_two(x):", "    return x"));
        Assert.Equal("1:0-2:8 | 7:0-8:12", Found(tests, "def $f:^test_$($p*$):\n    $body*$"));
    }

    [Fact]
    public void FindsRunsOfStatementsAndKeepsTheIndentationOfTheReplacement()
    {
        string src = Src("def f(path):", "    fh = open(path)", "    data = fh.read()", "    fh.close()", "    return data");
        var svc = Service(src);
        Assert.Equal("2:4-4:14 => with open(path) as fh:\n        data = fh.read()",
            Found(svc, "$f$ = open($p$)\n$d$ = $f$.read()\n$f$.close()", "with open($p$) as $f$:\n    $d$ = $f$.read()"));
        Assert.True(Found(svc, "$f$ = open($p$)\n$d$ = $f$.write()") == "");
    }

    [Fact]
    public void ABadPatternSaysWhy()
    {
        var svc = Service(Src("x = 1"));
        try { svc.StructuralSearch("def (", null); throw new AssertionException("no error"); }
        catch (ServiceException e) { Assert.True(e.Message.StartsWith("The pattern is not Python")); }
        try { svc.StructuralSearch("$x:[$", null); throw new AssertionException("no error"); }
        catch (ServiceException e) { Assert.True(e.Message.Contains("regular expression") || e.Message.StartsWith("The pattern"), e.Message); }
    }

    [Fact]
    public void FindsDuplicatedCodeWithOtherNames()
    {
        string one = Src("def total(rows):", "    result = 0", "    for row in rows:", "        if row.ok:", "            result += row.value", "    print(result)", "    return result");
        string two = Src("def amount(items):", "    acc = 0", "    for it in items:", "        if it.ok:", "            acc += it.value", "    print(acc)", "    return acc", "",
            "def other():", "    return 1");
        var groups = Service(one, two).Duplicates(minLines: 5);
        Assert.Equal(1, groups.Count);
        Assert.Equal(6, groups[0].Lines);
        Assert.Equal("a.py:2 | b.py:2", string.Join(" | ", groups[0].Places.Select(p => p.Uri[(p.Uri.LastIndexOf('/') + 1)..] + ":" + (p.Range.Start.Line + 1))));
        Assert.Equal(0, Service(one, Src("x = 1")).Duplicates(minLines: 5).Count);
    }
}
