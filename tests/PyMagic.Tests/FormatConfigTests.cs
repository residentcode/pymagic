using PyMagic.Server;
using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>The TOML reader, finding the settings of a project, and what each setting does to the built-in formatter.</summary>
public class FormatConfigTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    // ------------------------------------------------------------ TOML

    [Fact]
    public void TomlTablesKeysAndScalars()
    {
        var root = Toml.Parse(Src(
            "# a comment", "title = \"x\"", "n = 1_000", "pi = 3.5", "yes = true", "a.b = 7", "'quoted key' = 'lit\\eral'",
            "[tool.black]", "line-length = 100   # trailing comment", "skip-string-normalization = true",
            "[tool.ruff.format]", "quote-style = \"single\"")) ?? throw new AssertionException("did not parse");
        Assert.Equal("x", (string)Toml.At(root, "title")!);
        Assert.Equal(1000L, (long)Toml.At(root, "n")!);
        Assert.Equal(3.5, (double)Toml.At(root, "pi")!);
        Assert.Equal(true, (bool)Toml.At(root, "yes")!);
        Assert.Equal(7L, (long)Toml.At(root, "a.b")!);
        Assert.Equal("lit\\eral", (string)Toml.At(root, "quoted key")!);
        Assert.Equal(100L, (long)Toml.At(root, "tool.black.line-length")!);
        Assert.Equal(true, (bool)Toml.At(root, "tool.black.skip-string-normalization")!);
        Assert.Equal("single", (string)Toml.At(root, "tool.ruff.format.quote-style")!);
        Assert.True(Toml.At(root, "tool.ruff.missing") == null, "an absent key");
    }

    [Fact]
    public void TomlArraysInlineTablesAndStrings()
    {
        var root = Toml.Parse(Src(
            "list = [", "  \"a\",", "  \"b\",  # why", "  [1, 2],", "]", "inline = { x = 1, y = \"two\" }", "escaped = \"tab\\there \\u00e9\"",
            "multi = \"\"\"", "first", "second\"\"\"", "[[items]]", "name = \"one\"", "[[items]]", "name = \"two\""))
            ?? throw new AssertionException("did not parse");
        var list = (List<object?>)Toml.At(root, "list")!;
        Assert.Equal(3, list.Count);
        Assert.Equal("b", (string)list[1]!);
        Assert.Equal(2, ((List<object?>)list[2]!).Count);
        Assert.Equal("two", (string)Toml.At(root, "inline.y")!);
        Assert.Equal("tab\there \u00e9", (string)Toml.At(root, "escaped")!);
        Assert.Equal("first\nsecond", ((string)Toml.At(root, "multi")!).Replace("\r\n", "\n"));
        var items = (List<object?>)Toml.At(root, "items")!;
        Assert.Equal("two", (string)((Dictionary<string, object?>)items[1]!)["name"]!);
    }

    [Fact]
    public void BrokenTomlIsNull()
    {
        Assert.True(Toml.Parse("a = ") == null, "no value");
        Assert.True(Toml.Parse("[unclosed\nx = 1") == null, "unclosed table");
        Assert.True(Toml.Parse("s = \"never ends") == null, "unclosed string");
        Assert.True(Toml.Parse("a = 1 b = 2") == null, "two values on a line");
    }

    // ------------------------------------------------------------ settings

    [Fact]
    public void BlackSettings()
    {
        var o = FormatConfig.FromToml(Src("[tool.black]", "line-length = 100", "skip-string-normalization = true", "skip-magic-trailing-comma = true"))!;
        Assert.Equal(100, o.LineLength);
        Assert.Equal(QuoteStyle.Preserve, o.Quotes);
        Assert.Equal(false, o.MagicTrailingComma);
        Assert.Equal(4, o.IndentWidth);
    }

    [Fact]
    public void RuffSettings()
    {
        var o = FormatConfig.FromToml(Src("[tool.ruff]", "line-length = 79", "indent-width = 2", "[tool.ruff.format]", "quote-style = \"single\"", "skip-magic-trailing-comma = true"))!;
        Assert.Equal(79, o.LineLength);
        Assert.Equal(2, o.IndentWidth);
        Assert.Equal(QuoteStyle.Single, o.Quotes);
        Assert.Equal(false, o.MagicTrailingComma);
        // ruff.toml has the same keys without the tool.ruff prefix
        var r = FormatConfig.FromToml(Src("line-length = 110", "[format]", "quote-style = \"preserve\""), isRuffFile: true)!;
        Assert.Equal(110, r.LineLength);
        Assert.Equal(QuoteStyle.Preserve, r.Quotes);
    }

    [Fact]
    public void BlackWinsOverRuffAndOtherTablesAreIgnored()
    {
        var o = FormatConfig.FromToml(Src("[tool.black]", "line-length = 100", "[tool.ruff]", "line-length = 60"))!;
        Assert.Equal(100, o.LineLength);
        Assert.True(FormatConfig.FromToml(Src("[tool.pytest.ini_options]", "addopts = \"-q\"")) == null, "no formatter section");
        Assert.True(FormatConfig.FromToml("[tool.black") == null, "not TOML");
        // nonsense values are ignored
        Assert.Equal(88, FormatConfig.FromToml(Src("[tool.black]", "line-length = \"wide\""))!.LineLength);
        Assert.Equal(88, FormatConfig.FromToml(Src("[tool.black]", "line-length = 0"))!.LineLength);
    }

    // ------------------------------------------------------------ finding the file

    static string Project(params (string Path, string Text)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_cfg_" + Guid.NewGuid().ToString("N"));
        foreach (var (p, t) in files)
        {
            string full = Path.Combine(dir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, t);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TheNearestProjectFileDecides()
    {
        string dir = Project(("pyproject.toml", Src("[tool.black]", "line-length = 100")), ("pkg/sub/mod.py", "x = 1\n"));
        try
        {
            var o = FormatConfig.For(Path.Combine(dir, "pkg", "sub", "mod.py"));
            Assert.Equal(100, o.LineLength);
            Assert.True(o.Source!.EndsWith("pyproject.toml"), "the source is named");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProjectRootWithoutASectionMeansTheDefaults()
    {
        string outer = Project(("pyproject.toml", Src("[tool.black]", "line-length = 100")),
            ("inner/pyproject.toml", Src("[project]", "name = \"inner\"")), ("inner/mod.py", "x = 1\n"));
        try { Assert.Equal(88, FormatConfig.For(Path.Combine(outer, "inner", "mod.py")).LineLength); }
        finally { Directory.Delete(outer, true); }

        string git = Project(("pyproject.toml", Src("[tool.black]", "line-length = 100")), ("repo/.git/HEAD", "ref\n"), ("repo/mod.py", "x = 1\n"));
        try { Assert.Equal(88, FormatConfig.For(Path.Combine(git, "repo", "mod.py")).LineLength); }
        finally { Directory.Delete(git, true); }
    }

    [Fact]
    public void RuffTomlIsFoundToo()
    {
        string dir = Project(("ruff.toml", Src("line-length = 70", "[format]", "quote-style = \"single\"")), ("mod.py", "x = 1\n"));
        try
        {
            var o = FormatConfig.For(Path.Combine(dir, "mod.py"));
            Assert.Equal(70, o.LineLength);
            Assert.Equal(QuoteStyle.Single, o.Quotes);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------ what the settings do

    static string Fmt(string text, FormatOptions options) =>
        PythonFormatter.Format(text, options) ?? throw new AssertionException("the formatter declined (" + PythonFormatter.LastMismatch + "):\n" + text);

    static void Stable(string expected, string input, FormatOptions options)
    {
        string once = Fmt(input, options);
        Assert.Equal(expected, once);
        Assert.Equal(expected, Fmt(once, options));
    }

    [Fact]
    public void TheLineLengthDecidesWhereLinesWrap()
    {
        string call = Src("result = some_function(argument_one, argument_two)");      // 50 characters
        Assert.Equal(call, Fmt(call, new FormatOptions()));
        Stable(Src("result = some_function(", "    argument_one, argument_two", ")"), call, new FormatOptions(LineLength: 40));
        Stable(Src("result = some_function(", "    argument_one,", "    argument_two,", ")"), call, new FormatOptions(LineLength: 25));

        string longCall = Src("result = some_function(first_argument_name, second_argument_name, third_argument_name, fourth_one)");   // 98
        Assert.True(Fmt(longCall, new FormatOptions()).Contains('\n') && Fmt(longCall, new FormatOptions()).Split('\n').Length > 2, "wrapped at 88");
        Assert.Equal(longCall, Fmt(longCall, new FormatOptions(LineLength: 120)));
    }

    [Fact]
    public void TheLineLengthAlsoMovesDocstringClosingQuotes()
    {
        string doc = Src("def f():", "    \"\"\"Summary.", "", "    " + new string('x', 40) + "\"\"\"", "    pass");
        Assert.Equal(doc, Fmt(doc, new FormatOptions()));
        string narrow = Fmt(doc, new FormatOptions(LineLength: 40));
        Assert.Contains("\n    \"\"\"\n", narrow);
    }

    [Fact]
    public void QuoteStyles()
    {
        string text = Src("a = 'one'", "b = \"two\"", "c = \"it's\"", "d = 'say \"hi\"'", "e = f'{a}'", "f = '''triple'''");
        Assert.Equal(Src("a = \"one\"", "b = \"two\"", "c = \"it's\"", "d = 'say \"hi\"'", "e = f\"{a}\"", "f = \"\"\"triple\"\"\""), Fmt(text, new FormatOptions()));
        Assert.Equal(text, Fmt(text, new FormatOptions(Quotes: QuoteStyle.Preserve)));
        Assert.Equal(Src("a = 'one'", "b = 'two'", "c = \"it's\"", "d = 'say \"hi\"'", "e = f'{a}'", "f = \"\"\"triple\"\"\""), Fmt(text, new FormatOptions(Quotes: QuoteStyle.Single)));
    }

    [Fact]
    public void DocstringsKeepDoubleQuotesExceptWhenQuotesAreLeftAlone()
    {
        string text = Src("def f():", "    '''  Doc.  '''", "    return 'x'");
        Assert.Equal(Src("def f():", "    \"\"\"Doc.\"\"\"", "    return 'x'"), Fmt(text, new FormatOptions(Quotes: QuoteStyle.Single)));
        Assert.Equal(Src("def f():", "    '''Doc.'''", "    return 'x'"), Fmt(text, new FormatOptions(Quotes: QuoteStyle.Preserve)));
    }

    [Fact]
    public void SkippingTheMagicTrailingComma()
    {
        string text = Src("foo(", "    a,", "    b,", ")", "t = (1,)", "x = y[1,]");
        Assert.Equal(Src("foo(", "    a,", "    b,", ")", "t = (1,)", "x = y[1,]"), Fmt(text, new FormatOptions()));
        Assert.Equal(Src("foo(a, b)", "t = (1,)", "x = y[1,]"), Fmt(text, new FormatOptions(MagicTrailingComma: false)));
        // lines that do not fit still split, and get their comma back
        string longText = Src("result = some_function(first_argument_name, second_argument_name, third_argument_name, fourth_one,)");
        Assert.Contains("fourth_one,\n)", Fmt(longText, new FormatOptions(LineLength: 40, MagicTrailingComma: false)));
        // comments attached to a dropped comma are kept
        Assert.Equal(Src("foo(a, b)  # why"), Fmt(Src("foo(a, b,  # why", ")"), new FormatOptions(MagicTrailingComma: false)));
    }

    [Fact]
    public void IndentWidth()
    {
        string text = Src("if x:", "    y=1", "    if z:", "        w=2");
        Stable(Src("if x:", "  y = 1", "  if z:", "    w = 2"), text, new FormatOptions(IndentWidth: 2));
        Stable(Src("def f():", "  return some_function(", "    argument_number_one, argument_number_two", "  )"),
            Src("def f():", "    return some_function(argument_number_one, argument_number_two)"), new FormatOptions(LineLength: 50, IndentWidth: 2));
    }

    [Fact]
    public void EverySettingKeepsTheCodeMeaningTheSame()
    {
        string messy = Src("import   os,sys", "class  A( object ) :", "  '''Doc'''", "  x='a'", "  def  f( self,a,b = 2, ):", "    return {'a':a,'b':[1,2,3,]}[ 'a' ]", "def g(*,a):", "  print (a if a else-1) # c");
        foreach (var options in new[]
        {
            new FormatOptions(LineLength: 40), new FormatOptions(LineLength: 200), new FormatOptions(Quotes: QuoteStyle.Single),
            new FormatOptions(Quotes: QuoteStyle.Preserve), new FormatOptions(MagicTrailingComma: false), new FormatOptions(IndentWidth: 2),
            new FormatOptions(LineLength: 30, Quotes: QuoteStyle.Single, MagicTrailingComma: false, IndentWidth: 8),
        })
        {
            string once = Fmt(messy, options);          // the formatter checks the tree itself and declines when it changed
            Assert.Equal(once, Fmt(once, options));
        }
    }

    // ------------------------------------------------------------ through the language service

    [Fact]
    public void TheServiceUsesTheProjectsSettings()
    {
        string dir = Project(("pyproject.toml", Src("[tool.black]", "line-length = 40", "skip-string-normalization = true")), ("mod.py", ""));
        try
        {
            var svc = new LanguageService();
            string uri = new Uri(Path.Combine(dir, "mod.py")).AbsoluteUri;
            string text = Src("x = 'a'", "result = some_function(argument_one, argument_two)");
            svc.Open(uri, text, 1);
            var cfg = svc.FormatConfigOf(uri);
            Assert.Equal(40, cfg.LineLength);
            Assert.Equal(QuoteStyle.Preserve, cfg.Quotes);

            var edits = svc.FormatDocument(uri);
            string result = text;
            var lm = new LineMap(text);
            foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)))
                result = result[..lm.OffsetOf(e.Range.Start)] + e.NewText + result[lm.OffsetOf(e.Range.End)..];
            Assert.Equal(Src("x = 'a'", "result = some_function(", "    argument_one, argument_two", ")"), result);
        }
        finally { Directory.Delete(dir, true); }
    }
}
