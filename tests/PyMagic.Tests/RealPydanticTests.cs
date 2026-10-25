using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>pydantic as the real package describes itself, not a stand-in. Skipped where pydantic is not installed.</summary>
public class RealPydanticTests
{
    static readonly string? Site = FindSite();

    static string? FindSite()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PYMAGIC_REAL_SITE"),
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".unsloth", "studio", "unsloth_studio", "Lib", "site-packages"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "pydantic", "__init__.py")));
    }

    static (LanguageService Svc, string Uri, string[] Lines) Open(string code)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_realpyd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new LanguageService { ReportTypeMismatches = true };
        svc.SourceRoots.Add(dir);
        svc.LibraryRoots.Add(Site!);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "main.py"));
        svc.Open(uri, code, 1);
        return (svc, uri, code.Split('\n'));
    }

    static Dictionary<int, List<string>> Problems(string code)
    {
        var (svc, uri, _) = Open(code);
        var result = new Dictionary<int, List<string>>();
        foreach (var d in svc.Diagnostics(uri).Where(d => d.Code is "call-arguments" or "argument-type" or "dataclass-frozen" or "missing-attribute" or "unresolved-import"))
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

    const string Model =
        "from pydantic import BaseModel, Field\n\n\nclass User(BaseModel):\n    id: int\n    name: str = \"x\"\n    tags: list[str] = Field(default_factory=list)\n    age: int = Field(0, ge=0)\n\n\n" +
        "User()\nUser(id=1)\nUser(id=\"a\")\nUser(1, \"n\")\nUser(id=1, other=2)\nu = User(id=1)\nu.id\nu.tags\nu.age\nu.nothing\n";

    [Fact]
    public void ModelFieldsFormTheInit()
    {
        if (Site == null) return;
        var p = Problems(Model);
        string all = string.Join(" / ", p.SelectMany(kv => kv.Key + ":" + string.Join(",", kv.Value)));
        Assert.True(p.ContainsKey(11), "id is required: " + all);
        Assert.True(!p.ContainsKey(12), "valid call: " + all);
        Assert.True(p.ContainsKey(13), "a string for an int: " + all);
        Assert.True(p.ContainsKey(14), "fields are keyword-only: " + all);
        Assert.True(p.ContainsKey(15), "unknown keyword: " + all);
        Assert.True(p.ContainsKey(20), "unknown attribute: " + all);
    }

    [Fact]
    public void FieldFunctionGivesTheDeclaredType()
    {
        if (Site == null) return;
        Assert.Contains("id: int", Hover(Model, "u.id"));
        Assert.Contains("tags: list[str]", Hover(Model, "u.tags"));
        Assert.Contains("age: int", Hover(Model, "u.age"));
    }

    [Fact]
    public void ClassMethodsAndConfigAreTyped()
    {
        if (Site == null) return;
        string code = "from pydantic import BaseModel, ConfigDict, field_validator, TypeAdapter\n\n\nclass A(BaseModel):\n    model_config = ConfigDict(frozen=True)\n    n: int\n\n    @field_validator(\"n\")\n    @classmethod\n    def check(cls, v: int) -> int:\n        return v\n\n\n" +
            "a = A.model_validate({\"n\": 1})\nd = a.model_dump()\nj = a.model_dump_json()\nc = a.model_copy()\nta = TypeAdapter(int).validate_python(\"3\")\nA(n=1).n = 2\nA(n=\"q\")\n";
        Assert.Contains("A", Hover(code, "a = A.model_validate", 0));
        Assert.Contains("dict", Hover(code, "d = a.model_dump()", 0));
        Assert.Contains("str", Hover(code, "j = a.model_dump_json()", 0));
        Assert.Contains("A", Hover(code, "c = a.model_copy()", 0));
        Assert.Contains("int", Hover(code, "ta = TypeAdapter", 0));
        var p = Problems(code);
        Assert.True(p.ContainsKey(20), "a string for n: " + string.Join(" / ", p.SelectMany(kv => kv.Key + ":" + string.Join(",", kv.Value))));
    }

    [Fact]
    public void InheritanceAndNestedModels()
    {
        if (Site == null) return;
        string code = "from pydantic import BaseModel\nfrom typing import Optional\n\n\nclass Base(BaseModel):\n    id: int\n\n\nclass Child(Base):\n    name: str\n    parent: Optional[Base] = None\n\n\n" +
            "Child(id=1)\nChild(id=1, name=\"a\", parent=Base(id=2))\nChild(id=1, name=\"a\", parent=3)\nChild(id=1, name=\"a\").parent.id\n";
        var p = Problems(code);
        string all = string.Join(" / ", p.SelectMany(kv => kv.Key + ":" + string.Join(",", kv.Value)));
        Assert.True(p.ContainsKey(14), "name missing: " + all);
        Assert.True(!p.ContainsKey(15), "valid: " + all);
        Assert.True(p.ContainsKey(16), "an int for a Base: " + all);
    }
}
