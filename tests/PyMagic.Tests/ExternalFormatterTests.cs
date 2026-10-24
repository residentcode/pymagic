using PyMagic.Server;
using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>black and ruff are run through the interpreter; these tests put small stand-ins for them on PYTHONPATH (needs python).</summary>
public class ExternalFormatterTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    const string FakeBlack = """
import os, sys
args = sys.argv[1:]
if "--version" in args:
    print("black, 24.2.0 (compiled: no)")
    sys.exit(0)
data = sys.stdin.read()
if "FAIL" in data:
    sys.stderr.write("error: cannot format <stdin>: Cannot parse: 1:0: FAIL" + chr(10))
    sys.exit(123)
name = os.path.basename(args[args.index("--stdin-filename") + 1])
ranges = [a for a in args if a.startswith("--line-ranges=")]
sys.stdout.write("# black file=" + name + (" " + ranges[0] if ranges else "") + chr(10) + data)
""";

    const string FakeRuff = """
import os, sys
args = sys.argv[1:]
if "--version" in args:
    print("ruff 0.5.0")
    sys.exit(0)
data = sys.stdin.read()
sys.stdout.write("# ruff " + " ".join(args[:1]) + chr(10) + data)
""";

    /// <summary>Runs the body with the stand-in tools importable by the interpreter.</summary>
    static void WithFakeTools(Action<string> body)
    {
        string? python = RefactorBehaviourTests.Python;
        if (python == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_tools_" + Guid.NewGuid().ToString("N"));
        foreach (var (name, code) in new[] { ("black", FakeBlack), ("ruff", FakeRuff) })
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
            File.WriteAllText(Path.Combine(dir, name, "__init__.py"), "");
            File.WriteAllText(Path.Combine(dir, name, "__main__.py"), code);
        }
        string? old = Environment.GetEnvironmentVariable("PYTHONPATH");
        Environment.SetEnvironmentVariable("PYTHONPATH", dir);
        try { body(python); }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONPATH", old);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ProbeFindsTheToolsAndTheirVersions()
    {
        WithFakeTools(python =>
        {
            var black = ExternalFormatter.Probe(python, "black");
            Assert.True(black.Available, "black");
            Assert.Equal("24.2.0", black.Version!);
            Assert.Equal("0.5.0", ExternalFormatter.Probe(python, "ruff").Version!);
            Assert.True(!ExternalFormatter.Probe(python, "no_such_formatter_tool").Available, "an unknown tool");
        });
    }

    [Fact]
    public void TheTextAndTheFileNameGoToTheTool()
    {
        WithFakeTools(python =>
        {
            Assert.Equal("# black file=main.py\nx=1\n", ExternalFormatter.Format(python, "black", "x=1\n", "C:/proj/src/main.py"));
            Assert.Equal("# ruff format\nx=1\n", ExternalFormatter.Format(python, "ruff", "x=1\n", "C:/proj/src/main.py"));
            Assert.Equal("# black file=m.py --line-ranges=2-3\nx=1\n", ExternalFormatter.Format(python, "black", "x=1\n", "m.py", (2, 3)));
        });
    }

    [Fact]
    public void ToolErrorsAreReportedInWords()
    {
        WithFakeTools(python =>
        {
            try { ExternalFormatter.Format(python, "black", "FAIL\n", "m.py"); }
            catch (ServiceException e) { Assert.Contains("Cannot parse", e.Message); return; }
            throw new AssertionException("expected an error");
        });
    }

    [Fact]
    public void AMissingInterpreterOrToolIsExplained()
    {
        string? python = RefactorBehaviourTests.Python;
        if (python == null) return;
        try { ExternalFormatter.Format("C:/no/such/python.exe", "black", "x=1\n", "m.py"); }
        catch (ServiceException e) { Assert.Contains("could not be started", e.Message); }
        try { ExternalFormatter.Format(python, "no_such_formatter_tool", "x=1\n", "m.py"); }
        catch (ServiceException e) { Assert.Contains("no_such_formatter_tool", e.Message); return; }
        throw new AssertionException("expected an error");
    }

    // ------------------------------------------------------------ through the language service

    const string Doc = "file:///c:/proj/src/main.py";

    static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var lm = new LineMap(text);
        foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    [Fact]
    public void TheServiceListsTheFormatters()
    {
        WithFakeTools(_ =>
        {
            var svc = new LanguageService();
            var list = svc.Formatters();
            Assert.Equal("builtin,black,ruff", string.Join(",", list.Select(f => f.Id)));
            Assert.True(list.All(f => f.Available), "all three are available here");
            Assert.Equal("black 24.2.0", list[1].Label);
        });
        // without the stand-ins neither is installed in a plain interpreter (unless the machine has them: then they are listed as available)
        var plain = new LanguageService().Formatters();
        Assert.Equal("Built-in", plain[0].Label);
    }

    [Fact]
    public void TheServiceFormatsWithTheChosenTool()
    {
        WithFakeTools(_ =>
        {
            var svc = new LanguageService();
            string text = Src("import b", "import a", "x=1");
            svc.Open(Doc, text, 1);
            // imports are sorted first (our sorter), then the text goes to the tool
            Assert.Equal("# black file=main.py\n" + Src("import a", "import b", "x=1"), Apply(text, svc.FormatDocument(Doc, true, "black")));
            Assert.Equal("# ruff format\n" + text, Apply(text, svc.FormatDocument(Doc, false, "ruff")));
            Assert.Equal(Src("import b", "import a", "", "x = 1"), Apply(text, svc.FormatDocument(Doc, false, "builtin")));
        });
    }

    [Fact]
    public void ASelectionGoesToBlackAsLineRanges()
    {
        WithFakeTools(_ =>
        {
            var svc = new LanguageService();
            string text = Src("x=1", "y=2", "z=3");
            svc.Open(Doc, text, 1);
            var range = new PyMagic.Server.Range(new PyMagic.Server.Position(1, 0), new PyMagic.Server.Position(2, 3));
            Assert.Equal("# black file=main.py --line-ranges=2-3\n" + text, Apply(text, svc.FormatRange(Doc, range, "black")));
            // a selection that ends at the start of a line leaves that line out
            var upToLine3 = new PyMagic.Server.Range(new PyMagic.Server.Position(0, 0), new PyMagic.Server.Position(2, 0));
            Assert.Contains("--line-ranges=1-2", Apply(text, svc.FormatRange(Doc, upToLine3, "black")));
            // ruff has no ranges here: the built-in formatter does it
            Assert.Equal(Src("x=1", "y = 2", "z=3"), Apply(text, svc.FormatRange(Doc, new PyMagic.Server.Range(new PyMagic.Server.Position(1, 0), new PyMagic.Server.Position(1, 3)), "ruff")));
        });
    }

    [Fact]
    public void AToolThatReturnsBrokenCodeIsNotApplied()
    {
        string? python = RefactorBehaviourTests.Python;
        if (python == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_tools_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "black"));
        File.WriteAllText(Path.Combine(dir, "black", "__init__.py"), "");
        File.WriteAllText(Path.Combine(dir, "black", "__main__.py"), "import sys\nif '--version' in sys.argv:\n    print('black, 24.2.0')\n    sys.exit(0)\nsys.stdout.write('def (:\\n')\n");
        string? old = Environment.GetEnvironmentVariable("PYTHONPATH");
        Environment.SetEnvironmentVariable("PYTHONPATH", dir);
        try
        {
            var svc = new LanguageService();
            svc.Open(Doc, "x=1\n", 1);
            try { svc.FormatDocument(Doc, false, "black"); }
            catch (ServiceException e) { Assert.Contains("does not parse", e.Message); return; }
            throw new AssertionException("expected a refusal");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONPATH", old);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
