using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Change signature and inline method applied to real programs: the output must not change (needs python).</summary>
public class SignatureBehaviourTests
{
    const string Main = "file:///c:/proj/main.py";

    static string ChangeSignature(string marked, string? newName, params SignatureParam[] parameters)
    {
        string text = marked.Replace("|", "");
        int cursor = marked.IndexOf('|');
        var lm = new LineMap(text);
        var svc = new LanguageService();
        svc.Open(Main, text, 1);
        var result = svc.ChangeSignature(Main, lm.RangeOf(cursor, cursor).Start, newName, parameters);
        foreach (var e in result.Changes[Main].OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    static void SameAfter(string marked, string? newName, params SignatureParam[] parameters)
    {
        if (RefactorBehaviourTests.Python == null) return;
        string after = ChangeSignature(marked, newName, parameters);
        Assert.Equal(RefactorBehaviourTests.RunPython(marked.Replace("|", "")), RefactorBehaviourTests.RunPython(after));
    }

    [Fact]
    public void ReorderedAndRenamedParametersKeepTheOutput()
    {
        SameAfter(string.Join("\n",
            "def |area(width, height, unit='cm'):",
            "    return f'{width * height} {unit}'",
            "",
            "print(area(2, 3))",
            "print(area(2, 3, 'mm'))",
            "print(area(height=4, width=5))",
            ""), "surface",
            new SignatureParam(1, "height", "normal", null, null),
            new SignatureParam(0, "width", "normal", null, null),
            new SignatureParam(2, "unit", "normal", null, "'cm'"));
    }

    [Fact]
    public void AddedParameterWithACallValueKeepsTheOutput()
    {
        SameAfter(string.Join("\n",
            "class Box:",
            "    def __init__(self, size):",
            "        self.size = size",
            "    def |grow(self, by):",
            "        return Box(self.size + by)",
            "",
            "print(Box(1).grow(2).size)",
            "print(Box(5).grow(by=1).size)",
            ""), null,
            new SignatureParam(0, "by", "normal", null, null),
            new SignatureParam(null, "times", "normal", null, "1", "1"));
    }

    [Fact]
    public void InlinedMethodsKeepTheOutput()
    {
        if (RefactorBehaviourTests.Python == null) return;
        string marked = string.Join("\n",
            "def double(x):",
            "    return x * 2",
            "",
            "def total(a, b):",
            "    return |double(a + b) - 1",
            "",
            "print(total(1, 2))",
            "print(total(5, 7))",
            "");
        string before = marked.Replace("|", "");
        string after = RefactorBehaviourTests.Refactor(marked, "inlineMethod", null);
        Assert.Contains("(a + b) * 2", after);
        Assert.Equal(RefactorBehaviourTests.RunPython(before), RefactorBehaviourTests.RunPython(after));
    }
}
