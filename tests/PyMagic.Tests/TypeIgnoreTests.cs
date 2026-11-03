namespace PyMagic.Tests;

/// <summary>
/// `# type: ignore` as PyCharm reads it: on a line it silences the unresolved references, argument lists, type checks, Final and TypedDict
/// reports there (whatever codes are in brackets); at the top of the file, in the whole file. Other inspections still report.
/// </summary>
public class TypeIgnoreTests
{
    const string Uri = "file:///c:/proj/main.py";

    static List<string> Found(string src)
    {
        var svc = new PyMagic.Server.LanguageService();
        svc.Open(Uri, src, 1);
        return svc.Diagnostics(Uri).Select(d => $"{d.Range.Start.Line}: {d.Code}").ToList();
    }

    const string Body = "class A:\n    _p = 1\n\n\ndef f(x: int) -> None: ...\n\n\n"
        + "A().missing  # type: ignore[attr-defined]\n"   // 7
        + "f()  # type: ignore\n"                         // 8
        + "print(undefined_name)  # type: ignore\n"       // 9
        + "print(A()._p)  # type: ignore\n"               // 10: protected member access is not a type check
        + "A().gone\n";                                   // 11

    [Fact]
    public void ALineWithTypeIgnoreHasNoTypeReports()
    {
        var found = Found(Body);
        Assert.True(!found.Any(f => f.StartsWith("7: ") || f.StartsWith("8: ") || f.StartsWith("9: ")), string.Join(" | ", found));
        Assert.True(found.Contains("10: protected-member"), string.Join(" | ", found));
        Assert.True(found.Contains("11: missing-attribute"), string.Join(" | ", found));
    }

    [Fact]
    public void TypeIgnoreAtTheTopSilencesTheFile()
    {
        var found = Found("# a comment\n# type: ignore\n" + Body);
        Assert.True(!found.Any(f => f.EndsWith("missing-attribute") || f.EndsWith("call-arguments") || f.EndsWith("unresolved-reference")), string.Join(" | ", found));
        Assert.True(found.Contains("12: protected-member"), string.Join(" | ", found));
    }
}
