using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>Tabs, line endings and .editorconfig for the built-in formatter.</summary>
public class EditorConfigTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static List<(string Key, string Value)> Section(string text, string relative) =>
        EditorConfig.Parse(text.Split('\n'), relative);

    // ------------------------------------------------------------ .editorconfig

    [Fact]
    public void SectionsAreMatchedByGlob()
    {
        string ec = Src("root = true", "", "[*]", "end_of_line = lf", "", "[*.py]", "indent_style = space", "indent_size = 2", "", "[tests/**.py]", "max_line_length = 100", "", "[*.md]", "indent_size = 8");
        var plain = Section(ec, "pkg/mod.py").ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("lf", plain["end_of_line"]);
        Assert.Equal("2", plain["indent_size"]);
        Assert.True(!plain.ContainsKey("max_line_length"), "only for tests");
        var test = Section(ec, "tests/unit/test_a.py").ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("100", test["max_line_length"]);
        Assert.True(Section(ec, "README.md").Any(x => x.Key == "indent_size" && x.Value == "8"));
    }

    [Fact]
    public void BracesRangesAndAnchoredPatterns()
    {
        Assert.True(EditorConfig.Matches("*.{py,pyi}", "a/b/c.pyi"));
        Assert.True(!EditorConfig.Matches("*.{py,pyi}", "a/b/c.txt"));
        Assert.True(EditorConfig.Matches("/setup.py", "setup.py"));
        Assert.True(!EditorConfig.Matches("/setup.py", "sub/setup.py"));
        Assert.True(EditorConfig.Matches("src/*.py", "src/a.py"));
        Assert.True(!EditorConfig.Matches("src/*.py", "src/deep/a.py"));
        Assert.True(EditorConfig.Matches("src/**.py", "src/deep/a.py"));
        Assert.True(EditorConfig.Matches("v{1..3}.py", "v2.py"));
        Assert.True(!EditorConfig.Matches("v{1..3}.py", "v4.py"));
        Assert.True(EditorConfig.Matches("file?.py", "file1.py"));
    }

    [Fact]
    public void PropertiesBecomeFormatterSettings()
    {
        var s = FormatConfig.FromEditorConfig(new Dictionary<string, string> { ["indent_style"] = "tab", ["tab_width"] = "8", ["end_of_line"] = "crlf", ["max_line_length"] = "100" });
        Assert.Equal(true, s.UseTabs);
        Assert.Equal(8, s.IndentWidth);
        Assert.Equal(LineEnding.Crlf, s.Endings);
        Assert.Equal(100, s.LineLength);
        var spaces = FormatConfig.FromEditorConfig(new Dictionary<string, string> { ["indent_style"] = "space", ["indent_size"] = "2" });
        Assert.Equal(false, spaces.UseTabs);
        Assert.Equal(2, spaces.IndentWidth);
        Assert.True(FormatConfig.FromEditorConfig(new Dictionary<string, string> { ["max_line_length"] = "off" }).LineLength == null);
    }

    [Fact]
    public void TheNearestFileWinsAndRootStopsTheSearch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_ec_" + Guid.NewGuid().ToString("N"));
        string sub = Path.Combine(dir, "pkg");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".editorconfig"), Src("root = true", "[*.py]", "indent_size = 2", "max_line_length = 70"));
            File.WriteAllText(Path.Combine(sub, ".editorconfig"), Src("[*.py]", "indent_size = 3"));
            var o = FormatConfig.For(Path.Combine(sub, "m.py"));
            Assert.Equal(3, o.IndentWidth);
            Assert.Equal(70, o.LineLength);
            Assert.Equal(2, FormatConfig.For(Path.Combine(dir, "top.py")).IndentWidth);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ProjectSettingsBeatEditorConfigWhereTheyAreSet()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_ec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".editorconfig"), Src("[*.py]", "max_line_length = 60", "indent_style = tab"));
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"), Src("[tool.black]", "line-length = 100"));
            var o = FormatConfig.For(Path.Combine(dir, "m.py"));
            Assert.Equal(100, o.LineLength);
            Assert.True(o.UseTabs, "what pyproject does not say comes from .editorconfig");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------ ruff's format table

    [Fact]
    public void RuffIndentStyleAndLineEnding()
    {
        var o = FormatConfig.FromToml(Src("[tool.ruff.format]", "indent-style = \"tab\"", "line-ending = \"lf\""))!;
        Assert.True(o.UseTabs);
        Assert.Equal(LineEnding.Lf, o.Endings);
        var r = FormatConfig.FromToml(Src("[format]", "indent-style = \"space\"", "line-ending = \"cr-lf\""), isRuffFile: true)!;
        Assert.True(!r.UseTabs);
        Assert.Equal(LineEnding.Crlf, r.Endings);
    }

    // ------------------------------------------------------------ the formatter

    [Fact]
    public void TabsReplaceTheIndentation()
    {
        string code = "class A:\n    def f(self, x):\n        if x:\n            return 1\n        return 2\n";
        string expected = "class A:\n\tdef f(self, x):\n\t\tif x:\n\t\t\treturn 1\n\t\treturn 2\n";
        Assert.Equal(expected, PythonFormatter.Format(code, new FormatOptions(UseTabs: true)));
    }

    [Fact]
    public void TabsLeaveStringsAlone()
    {
        string code = "def f():\n    text = \"\"\"a\n    b\n        c\"\"\"\n    return text\n";
        string result = PythonFormatter.Format(code, new FormatOptions(UseTabs: true))!;
        Assert.Contains("\"\"\"a\n    b\n        c\"\"\"", result);
        Assert.True(result.StartsWith("def f():\n\ttext"), result);
    }

    [Fact]
    public void WrappedLinesUseTabsToo()
    {
        string code = "result = some_function(argument_number_one, argument_number_two, argument_number_three, four)\n";
        string result = PythonFormatter.Format(code, new FormatOptions(LineLength: 60, UseTabs: true))!;
        Assert.Contains("\n\targument_number_one", result);
        Assert.True(!result.Contains("    "), result);
    }

    [Fact]
    public void ALineEndingCanBeForced()
    {
        string lf = "x = 1\ny = 2\n";
        Assert.Equal("x = 1\r\ny = 2\r\n", PythonFormatter.Format(lf, new FormatOptions(Endings: LineEnding.Crlf)));
        Assert.Equal(lf, PythonFormatter.Format("x = 1\r\ny = 2\r\n", new FormatOptions(Endings: LineEnding.Lf)));
        Assert.Equal("x = 1\r\ny = 2\r\n", PythonFormatter.Format("x = 1\r\ny = 2\r\n", new FormatOptions()));
    }

    [Fact]
    public void TheResultIsStable()
    {
        string code = "def f(a,b):\n    if a:\n        return b\n";
        var o = new FormatOptions(UseTabs: true, Endings: LineEnding.Crlf);
        string once = PythonFormatter.Format(code, o)!;
        Assert.Equal(once, PythonFormatter.Format(once, o));
    }
}
