using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Generate… (Alt+Insert, item 156): a constructor from the fields, properties for `self._x`, overrides of the bases' methods, and a test.</summary>
public class GenerateTests
{
    sealed record Run(string Dir, List<GenerateOption> Options, string Text);

    static Run At((string Rel, string Text)[] files, string rel, string marker)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_generate_" + Guid.NewGuid().ToString("N"));
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        string? uri = null, text = null;
        foreach (var (r, t) in files)
        {
            string full = Path.Combine(dir, r.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, t);
            if (!r.EndsWith(".py", StringComparison.Ordinal)) continue;
            svc.Open(LanguageService.UriOfPath(full), t, 1);
            if (r == rel) { uri = LanguageService.UriOfPath(full); text = t; }
        }
        var lines = new LineMap(text!);
        return new Run(dir, svc.Generate(uri!, lines.PositionOf(text!.IndexOf(marker, StringComparison.Ordinal))), text!);
    }

    static string Applied(Run run, string kind, string? title = null)
    {
        var o = run.Options.First(o => o.Kind == kind && (title == null || o.Title.Contains(title)));
        var lines = new LineMap(run.Text);
        string text = run.Text;
        foreach (var e in o.Changes!.Single().Value.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    [Fact]
    public void AConstructorTakesTheFieldsWithDefaultsLast()
    {
        var run = At(new[] { ("m.py", "class Point:\n    x: int = 0\n    label: str\n\n    def show(self):\n        return self.x\n") }, "m.py", "label");
        Assert.Equal("class Point:\n    x: int = 0\n    label: str\n\n    def show(self):\n        return self.x\n\n" +
            "    def __init__(self, label: str, x: int = 0):\n        self.x = x\n        self.label = label\n", Applied(run, "init"));
    }

    [Fact]
    public void NoConstructorWhereOneIsMadeOrThere()
    {
        Assert.True(!At(new[] { ("m.py", "from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: int\n") }, "m.py", "x:").Options.Any(o => o.Kind == "init"));
        Assert.True(!At(new[] { ("m.py", "class P:\n    x: int\n\n    def __init__(self):\n        self.x = 1\n") }, "m.py", "x:").Options.Any(o => o.Kind == "init"));
    }

    [Fact]
    public void PropertiesForPrivateAttributes()
    {
        var run = At(new[] { ("m.py", "class Box:\n    def __init__(self):\n        self._size = 1\n        self.name = 'b'\n") }, "m.py", "_size");
        Assert.True(run.Options.Any(o => o.Kind == "getters" && o.Title == "Getters of size"));
        Assert.Equal("class Box:\n    def __init__(self):\n        self._size = 1\n        self.name = 'b'\n\n" +
            "    @property\n    def size(self):\n        return self._size\n\n    @size.setter\n    def size(self, value):\n        self._size = value\n", Applied(run, "properties"));
    }

    [Fact]
    public void TheBasesMethodsCanBeOverridden()
    {
        var run = At(new[] { ("m.py", "class Base:\n    def area(self, scale: float, *, exact=False) -> float:\n        return 1.0\n\n    def done(self) -> None:\n        pass\n\n\nclass Square(Base):\n    def done(self) -> None:\n        pass\n") }, "m.py", "class Square");
        var titles = run.Options.Where(o => o.Kind == "override").Select(o => o.Title).ToList();
        Assert.True(titles.Contains("Override area() of Base"), string.Join("; ", titles));
        Assert.True(!titles.Any(t => t.Contains("done()")), "done is defined already");
        Assert.True(titles.Contains("Override __repr__() of object") && !titles.Any(t => t.Contains("__init_subclass__")), string.Join("; ", titles));
        Assert.True(Applied(run, "override", "area").EndsWith(
            "\n\n    def area(self, scale: float, *, exact=False) -> float:\n        return super().area(scale, exact=exact)\n", StringComparison.Ordinal), Applied(run, "override", "area"));
    }

    [Fact]
    public void ATestGoesIntoTheTestsFolder()
    {
        var run = At(new[] { ("shop/__init__.py", ""), ("shop/cart.py", "def total(items):\n    return sum(items)\n"), ("tests/conftest.py", "") }, "shop/cart.py", "sum");
        var test = run.Options.Single(o => o.Kind == "test");
        Assert.Equal(Path.Combine(run.Dir, "tests", "test_cart.py"), new Uri(test.NewFile!).LocalPath);
        Assert.Equal("from shop.cart import total\n\n\ndef test_total():\n    assert total(items=None)\n", test.NewText);
    }

    [Fact]
    public void ATestIsAddedToTheFileThatIsThere()
    {
        var run = At(new[] { ("cart.py", "class Cart:\n    pass\n"), ("test_cart.py", "import pytest\n\n\ndef test_other():\n    pass\n") }, "cart.py", "pass");
        Assert.Equal("import pytest\nfrom cart import Cart\n\n\ndef test_other():\n    pass\n\n\ndef test_cart():\n    obj = Cart()\n    assert obj\n", run.Options.Single(o => o.Kind == "test").NewText);
    }

    [Fact]
    public void ATestPassesTheRequiredParametersWithValuesOfTheirTypes()
    {
        var run = At(new[] { ("cart.py", "class Cart:\n    def __init__(self, owner: str, size: int, tags: list[str], note=None, *rest, limit: float):\n        pass\n") }, "cart.py", "pass");
        Assert.True(run.Options.Single(o => o.Kind == "test").NewText!.Contains("obj = Cart(owner=\"\", size=0, tags=[], limit=0.0)"));
    }

    [Fact]
    public void EqualityAndReprFromTheFields()
    {
        var run = At(new[] { ("m.py", "class Point:\n    def __init__(self, x, y):\n        self.x = x\n        self.y = y\n        self._cache = None\n") }, "m.py", "_cache");
        string eq = Applied(run, "eq");
        Assert.True(eq.EndsWith("\n    def __eq__(self, other):\n        if not isinstance(other, Point):\n            return NotImplemented\n        return (self.x, self.y) == (other.x, other.y)\n"), eq);
        Assert.True(Applied(run, "repr").EndsWith("\n    def __repr__(self):\n        return f\"Point(x={self.x!r}, y={self.y!r})\"\n"));
        Assert.True(!At(new[] { ("m.py", "from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: int\n") }, "m.py", "x:").Options.Any(o => o.Kind is "eq" or "repr"));
    }

    [Fact]
    public void PropertiesForPublicAttributesKeepThemAsPrivateOnes()
    {
        var run = At(new[] { ("m.py", "class Box:\n    def __init__(self):\n        self.size = 1\n\n    def grow(self):\n        self.size += 1\n") }, "m.py", "grow");
        Assert.Equal("class Box:\n    def __init__(self):\n        self._size = 1\n\n    def grow(self):\n        self._size += 1\n\n" +
            "    @property\n    def size(self):\n        return self._size\n\n    @size.setter\n    def size(self, value):\n        self._size = value\n", Applied(run, "publicProperties"));
    }
}
