using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>attrs as the real package describes itself (its .pyi files), not a stand-in. Skipped where attrs is not installed.</summary>
public class RealAttrsTests
{
    static readonly string? Site = FindSite();

    static string? FindSite()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PYMAGIC_REAL_SITE"),
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".unsloth", "studio", "unsloth_studio", "Lib", "site-packages"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "attr", "__init__.pyi")) && File.Exists(Path.Combine(c, "attrs", "__init__.pyi")));
    }

    static (LanguageService Svc, string Uri, string[] Lines) Open(string code)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_realattrs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new LanguageService { ReportTypeMismatches = true };
        svc.SourceRoots.Add(dir);
        svc.LibraryRoots.Add(Site!);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "main.py"));
        svc.Open(uri, code, 1);
        return (svc, uri, code.Split('\n'));
    }

    /// <summary>The problems by line (1-based), each with its message.</summary>
    static Dictionary<int, List<string>> Problems(string code)
    {
        var (svc, uri, _) = Open(code);
        var result = new Dictionary<int, List<string>>();
        foreach (var d in svc.Diagnostics(uri).Where(d => d.Code is "call-arguments" or "argument-type" or "dataclass-frozen" or "missing-attribute"))
        {
            int line = d.Range.Start.Line + 1;
            if (!result.TryGetValue(line, out var list)) result[line] = list = new List<string>();
            list.Add(d.Message);
        }
        return result;
    }

    static string Hover(string code, string marker, int? offset = null)
    {
        var (svc, uri, lines) = Open(code);
        int line = Array.FindIndex(lines, l => l.Contains(marker));
        int col = lines[line].IndexOf(marker, StringComparison.Ordinal);
        return svc.Hover(uri, new Position(line, col + (offset ?? marker.Length - 1)))?.Contents.Value ?? "";
    }

    const string Classic =
        "import attr\n\n\n@attr.s\nclass P:\n    x = attr.ib()\n    y = attr.ib(default=0)\n    z = attr.ib(init=False, default=3)\n\n\n" +
        "P()\nP(1, 2, 3)\nP(1, z=3)\nP(1, y=2)\nP(1)\n";

    [Fact]
    public void ClassicFieldsFormTheInit()
    {
        if (Site == null) return;
        var p = Problems(Classic);
        Assert.True(p.ContainsKey(11) && p[11].Any(m => m.Contains("'x'") && m.Contains("unfilled")), string.Join(" / ", p.SelectMany(kv => kv.Value)));
        Assert.True(p.ContainsKey(12), "too many");
        Assert.True(p.ContainsKey(13), "z is not in the init");
        Assert.True(!p.ContainsKey(14) && !p.ContainsKey(15), "the valid calls");
    }

    const string AutoAttribs =
        "import attr\n\n\n@attr.s(auto_attribs=True)\nclass Q:\n    a: int\n    b: str = \"x\"\n\n\nQ()\nQ(\"s\")\nQ(1, 2)\nQ(1, \"t\")\n";

    [Fact]
    public void AutoAttribsUsesTheAnnotations()
    {
        if (Site == null) return;
        var p = Problems(AutoAttribs);
        Assert.True(p.ContainsKey(10), "a is required");
        Assert.True(p.ContainsKey(11) && p[11].Any(m => m.Contains("'int'")), "a string for an int");
        Assert.True(p.ContainsKey(12) && p[12].Any(m => m.Contains("'str'")), "an int for a str");
        Assert.True(!p.ContainsKey(13));
    }

    const string Define =
        "import attrs\n\n\n@attrs.define\nclass D:\n    a: int\n    b: list[int] = attrs.field(factory=list)\n    c: str = attrs.field(default=\"k\", kw_only=True)\n\n\n" +
        "D()\nD(1, [1], 3)\nD(a=\"x\")\nD(1, c=\"z\")\nD(1, [], \"z\")\n\n\n@attrs.define\nclass Child(D):\n    extra: int = 0\n\n\nChild(1, [1], extra=2)\nChild()\n";

    [Fact]
    public void DefineFieldsDefaultsKeywordOnlyAndBases()
    {
        if (Site == null) return;
        var p = Problems(Define);
        Assert.True(p.ContainsKey(11), "a is required");
        Assert.True(p.ContainsKey(12), "too many");
        Assert.True(p.ContainsKey(13) && p[13].Any(m => m.Contains("'int'")), "a string for a");
        Assert.True(!p.ContainsKey(14), "c by keyword");
        Assert.True(p.ContainsKey(15), "c is keyword-only");
        Assert.True(!p.ContainsKey(23), "a valid call of the subclass");
        Assert.True(p.ContainsKey(24), "the base's a is required");
    }

    [Fact]
    public void AFrozenClassCannotBeAssignedTo()
    {
        if (Site == null) return;
        var p = Problems("import attrs\n\n\n@attrs.frozen\nclass F:\n    n: int\n\n\nf = F(1)\nf.n = 3\nf.n += 1\n\n\n@attrs.define\nclass M:\n    n: int\n\n\nm = M(1)\nm.n = 3\n");
        Assert.True(p.ContainsKey(10) && p[10].Any(m => m.Contains("frozen")), string.Join(" / ", p.SelectMany(kv => kv.Value)));
        Assert.True(p.ContainsKey(11), "an augmented assignment too");
        Assert.True(!p.ContainsKey(20), "a class that is not frozen");
    }

    [Fact]
    public void FrozenArgumentOfTheDecoratorIsRead()
    {
        if (Site == null) return;
        var p = Problems("import attr\n\n\n@attr.s(frozen=True, auto_attribs=True)\nclass F:\n    n: int\n\n\nF(1).n = 2\n");
        Assert.True(p.ContainsKey(9) && p[9].Any(m => m.Contains("frozen")), string.Join(" / ", p.SelectMany(kv => kv.Value)));
    }

    [Fact]
    public void UnannotatedFieldsGetTheirTypeFromTheCall()
    {
        if (Site == null) return;
        string code = "import attr\n\n\n@attr.s\nclass T:\n    n = attr.ib(type=int)\n    m = attr.ib(default=\"s\")\n    k = attr.ib(factory=list)\n    z = attr.ib()\n\n\nt = T(1)\nt.n\nt.m\nt.z\n";
        Assert.Contains("n: int", Hover(code, "t.n"));
        Assert.Contains("m: str", Hover(code, "t.m"));
        Assert.Contains("z: Any", Hover(code, "t.z"));
    }

    [Fact]
    public void TheFieldFunctionsOfBothSpellingsAreOverloadedCorrectly()
    {
        if (Site == null) return;
        string code = "import attr\nimport attrs\n\na = attr.ib(default=3)\nb = attr.attrib(default=3)\nc = attrs.field(default=3)\nd = attr.ib(type=str)\ne = attr.ib(default=\"x\", type=str)\n";
        Assert.Contains("a: int", Hover(code, "a = attr.ib(default=3)", 0));
        Assert.Contains("b: int", Hover(code, "b = attr.attrib(default=3)", 0));
        Assert.Contains("c: int", Hover(code, "c = attrs.field(default=3)", 0));
        Assert.Contains("d: str", Hover(code, "d = attr.ib(type=str)", 0));
        Assert.Contains("e: str", Hover(code, "e = attr.ib(default=", 0));
    }

    [Fact]
    public void TheseGivesTheFieldsAndTheirAttributes()
    {
        if (Site == null) return;
        string code = "import attr\nimport attrs\n\n\n@attr.s(these={\"x\": attr.ib(), \"y\": attr.ib(default=0, type=int)})\nclass C:\n    z = attr.ib()\n\n" +
            "    def total(self) -> int:\n        return self.x + self.y\n\n\n" +
            "@attrs.define(these={\"p\": attrs.field(type=str)})\nclass G:\n    pass\n\n\n" +
            "C(1, 2)\nC(x=1)\nC(z=1)\nc = C(1)\nc.y\nG(\"s\").p\nG(1)\n";
        var p = Problems(code);
        Assert.Equal("20 24", string.Join(" ", p.Keys.OrderBy(k => k)));
        Assert.True(p[20].Any(m => m.Contains("'z'")), string.Join(" / ", p[20]));
        Assert.True(p[24].Any(m => m.Contains("Expected type 'str'")), string.Join(" / ", p[24]));
        Assert.Contains("y: int", Hover(code, "c.y"));
    }
}
