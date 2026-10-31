using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Docstrings in hover, in `name=` items and in the signature popup (item 210): a parameter's documentation, a class's for its constructor,
/// a library method's from the source behind its stub, an overriding method's from the one it overrides; signature help in a class's bases.
/// </summary>
public class DocumentationHoverTests
{
    const string Main = "file:///c:/proj/main.py";

    static (LanguageService Svc, Position Pos) Open(string marked, Action<LanguageService>? setup = null)
    {
        int at = marked.IndexOf('|');
        string src = marked.Remove(at, 1);
        var svc = new LanguageService();
        setup?.Invoke(svc);
        svc.Open(Main, src, 1);
        return (svc, new LineMap(src).PositionOf(at));
    }

    static string Hover(string marked, Action<LanguageService>? setup = null)
    {
        var (svc, pos) = Open(marked, setup);
        return svc.Hover(Main, pos)?.Contents.Value ?? "";
    }

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    const string Area = "def area(width: int, height: int = 1) -> int:\n    \"\"\"The area of a box.\n\n    Args:\n        width: How wide, in cm.\n        height: How tall.\n    \"\"\"\n    return width * height\n\n";

    [Fact]
    public void AFunctionsHoverCarriesItsDocstring()
    {
        string h = Hover(Area + "ar|ea(2)");
        Assert.Contains("def area(width: int, height: int = ...) -> int", h);
        Assert.Contains("The area of a box.", h);
        Assert.Contains("How wide, in cm.", h);
        Assert.Contains("The area of a box.", Hover(Area.Replace("def area", "def a|rea")));
        // nothing to say: just the code
        Assert.Equal("```python\ndef g() -> None\n```", Hover(Src("def g():", "    pass", "g|()")));
    }

    [Fact]
    public void AParameterShowsWhatTheDocstringSaysAboutIt()
    {
        // where it is declared, where it is used, where a call gives it by keyword
        string declared = Hover(Area.Replace("height: int = 1", "hei|ght: int = 1"));
        Assert.Contains("(parameter) height: int", declared);
        Assert.Contains("How tall.", declared);
        Assert.Contains("How wide, in cm.", Hover(Area.Replace("return width", "return wi|dth")));
        string keyword = Hover(Area + "area(2, hei|ght=3)");
        Assert.Contains("(parameter) height: int", keyword);
        Assert.Contains("How tall.", keyword);
    }

    [Fact]
    public void AClassShowsItsDocstringAndItsParametersComeFromIt()
    {
        string box = Src("class Box:", "    \"\"\"A box to keep things in.", "", "    :param size: How big it is.", "    \"\"\"", "", "    def __init__(self, size):", "        self.size = size", "");
        Assert.Contains("A box to keep things in.", Hover(box + "B|ox(1)"));
        Assert.Contains("How big it is.", Hover(box + "Box(si|ze=1)"));
        var (svc, pos) = Open(box + "Box(|");
        var item = svc.Completion(Main, pos).First(i => i.Label == "size=");
        Assert.Equal("How big it is.", item.Documentation);
    }

    [Fact]
    public void KeywordItemsCarryTheParametersDocumentation()
    {
        var (svc, pos) = Open(Area + "area(|");
        var items = svc.Completion(Main, pos).Where(i => i.Label.EndsWith('=')).ToDictionary(i => i.Label);
        Assert.Equal("How wide, in cm.", items["width="].Documentation);
        Assert.Equal("How tall.", items["height="].Documentation);
    }

    [Fact]
    public void AMethodWithoutADocstringTakesTheOneItOverrides()
    {
        string src = Src("class Shape:", "    def draw(self, scale):", "        \"\"\"Draw the shape.", "", "        :param scale: How much bigger.", "        \"\"\"", "",
            "class Circle(Shape):", "    def draw(self, scale):", "        pass", "", "Circle().dr|aw(2)");
        Assert.Contains("Draw the shape.", Hover(src));
        var (svc, pos) = Open(src.Replace("Circle().dr|aw(2)", "Circle().draw(|"));
        var help = svc.SignatureHelp(Main, pos)!;
        Assert.Equal("Draw the shape.", help.Signatures[0].Documentation);
        Assert.Equal("How much bigger.", help.Signatures[0].Parameters[0].Documentation);
    }

    [Fact]
    public void ALibraryStubTakesTheDocstringsOfTheSourceItDescribes()
    {
        string lib = Path.Combine(Path.GetTempPath(), "pymagic-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(lib, "shapes"));
        Directory.CreateDirectory(Path.Combine(lib, "shapes-stubs"));
        try
        {
            File.WriteAllText(Path.Combine(lib, "shapes", "__init__.py"),
                "\"\"\"Shapes to draw.\"\"\"\nimport sys\n\nif sys.platform:\n    class Base:\n        \"\"\"Something to draw.\"\"\"\n\n        def render(self, dpi):\n            \"\"\"Render it.\n\n            :param dpi: Dots per inch.\n            \"\"\"\n");
            File.WriteAllText(Path.Combine(lib, "shapes-stubs", "__init__.pyi"), "class Base:\n    def render(self, dpi: int) -> bytes: ...\n");
            void Setup(LanguageService s) => s.LibraryRoots.Add(lib);
            string src = Src("from shapes import Base", "", "class Mine(Base):", "    def render(self, dpi):", "        return b''", "");
            Assert.Contains("Something to draw.", Hover(src.Replace("class Mine(Base)", "class Mine(Ba|se)"), Setup));
            Assert.Contains("Render it.", Hover(src + "Base().ren|der(1)", Setup));
            // a project class overriding the library's method, without a docstring of its own
            Assert.Contains("Render it.", Hover(src + "Mine().ren|der(1)", Setup));
            Assert.Contains("Dots per inch.", Hover(src + "Mine().render(dp|i=1)", Setup));
            var (svc, pos) = Open(src + "Base().render(|", Setup);
            Assert.Equal("Dots per inch.", svc.SignatureHelp(Main, pos)!.Signatures[0].Parameters[0].Documentation);
        }
        finally { Directory.Delete(lib, true); }
    }

    [Fact]
    public void SignatureHelpInAClassesBases()
    {
        string src = Src("class Plugin:", "    def __init__(self, name):", "        pass", "", "    def __init_subclass__(cls, tag: str = '', **kw):", "        \"\"\":param tag: What it is filed under.\"\"\"", "", "");
        // a call among the bases is a call
        var (svc, pos) = Open(src + "class A(Plugin(|");
        Assert.Equal("Plugin(name)", svc.SignatureHelp(Main, pos)!.Signatures[0].Label);
        // the bases themselves: the keywords of __init_subclass__
        (svc, pos) = Open(src + "class A(Plugin, |");
        var help = svc.SignatureHelp(Main, pos)!;
        Assert.Equal("class A(*bases, metaclass = ..., tag: str = ..., **kw)", help.Signatures[0].Label);
        (svc, pos) = Open(src + "class A(Plugin, tag=|");
        help = svc.SignatureHelp(Main, pos)!;
        var lit = help.Signatures[0].Parameters[help.ActiveParameter];
        Assert.Equal("tag: str = ...", help.Signatures[0].Label[lit.Label[0]..lit.Label[1]]);
        Assert.Equal("What it is filed under.", lit.Documentation);
        (svc, pos) = Open(src + "class A(Plugin, t|");
        Assert.True(svc.Completion(Main, pos).Any(i => i.Label == "tag="));
        // a class with no bases yet, and a call inside a def's default
        (svc, pos) = Open(src + "class B(|");
        Assert.True(svc.SignatureHelp(Main, pos)!.Signatures[0].Label.StartsWith("class B("));
        (svc, pos) = Open(src + "def f(x=Plugin(|");
        Assert.Equal("Plugin(name)", svc.SignatureHelp(Main, pos)!.Signatures[0].Label);
    }

    [Fact]
    public void DocstringsAreShownAsWritten()
    {
        string md = LanguageService.DocMarkdown("Use ``*args`` and _x_.\n    indented\n\nNext <b>.");
        Assert.Equal("Use `*args` and \\_x\\_.  \n&nbsp;&nbsp;&nbsp;&nbsp;indented\n\nNext \\<b\\>.", md);
        // a span that goes on over a line break, as json.dumps's ``(item_separator,\n key_separator)``
        Assert.Equal("an `(item_separator, key_separator)`  \ntuple", LanguageService.DocMarkdown("an ``(item_separator,\n    key_separator)``\ntuple"));
    }
}
