using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Full line completion: where a line may be completed, the window of code the model is shown, the checks a suggestion must pass, and (with
/// the bundled model and PYMAGIC_TEST_FULLLINE=1) lines the model writes.</summary>
public class FullLineTests
{
    /// <summary>The caret at the first `|` (taken out).</summary>
    static (string Text, int Offset) At(string marked) => (marked.Replace("|", ""), marked.IndexOf('|'));

    static bool Can(string marked, string language = "python")
    {
        var (text, offset) = At(marked);
        return FullLineChecks.CanComplete(text, offset, language);
    }

    static string? Clean(string marked, string generated, string language = "python")
    {
        var (text, offset) = At(marked);
        return FullLineChecks.Clean(generated, text[..offset], text[offset..], language);
    }

    [Fact]
    public void ALineIsCompletedAtTheEndOfItsCodeOutsideCommentsAndStrings()
    {
        Assert.True(Can("with open(path) as f:\n    config = |\n"));
        Assert.True(Can("print(|)"));                 // only closers after the caret
        Assert.True(Can("x = foo(\"a\", |)\n"));
        Assert.True(!Can("def f():\n    |\n"));       // nothing typed on the line
        Assert.True(!Can("x = |y + 1"));              // code after the caret
        Assert.True(!Can("x = 1  # set |"));          // a comment
        Assert.True(!Can("name = \"Bob |"));          // a string
        Assert.True(!Can("def f():\n    \"\"\"Load the |\n"));   // a docstring
        Assert.True(Can("def f():\n    \"\"\"Doc.\"\"\"\n    x = |\n"));
        Assert.True(!Can("const a = 1; // note |", "javascript"));
        Assert.True(Can("const total = items.|", "javascript"));
        Assert.True(!Can("const s = `hello |", "javascript"));
    }

    [Fact]
    public void TheWindowKeepsWholeLinesNearestTheCaret()
    {
        string text = string.Join("\n", Enumerable.Range(0, 2000).Select(i => "line" + i)) ;
        int offset = text.IndexOf("line1000") + 4;
        var (prefix, suffix) = FullLineChecks.Window(text, offset);
        Assert.True(prefix.Length <= 6000 && prefix.EndsWith("line") && prefix.StartsWith("line"));
        Assert.True(suffix.Length <= 1500 && suffix.StartsWith("1000\n") && !suffix.EndsWith("\n"));
        Assert.Equal(("ab", "cd"), FullLineChecks.Window("abcd", 2));
    }

    [Fact]
    public void ASuggestionIsTheFirstLineWithoutTrailingSpacesAndStopsBeforeWhatIsAlreadyThere()
    {
        Assert.Equal("json.load(f)", Clean("import json\nwith open(p) as f:\n    config = |\n", "json.load(f)  \nreturn config"));
        Assert.True(Clean("x = |\n", "   \n") == null);
        Assert.True(Clean("x = |\n", "") == null);
        // print(|) and the model wrote the closing bracket too: it is there already
        Assert.Equal("\"done\"", Clean("print(|)", "\"done\")"));
    }

    const string Defined = "path = foo = bar = add = items = a = b = c = f = x = dict = 0\n";

    [Fact]
    public void BracketsMustFit()
    {
        Assert.Equal("open(path, \"r\") as f:", Clean(Defined + "with |\n", "open(path, \"r\") as f:"));
        Assert.True(Clean(Defined + "x = |\n", "foo(a))") == null);               // closes what nothing opened
        Assert.True(Clean(Defined + "x = |\n", "foo(a]") == null);                // the wrong closer
        Assert.True(Clean(Defined + "x = |\n", "foo(bar(a)") == null);            // opens and leaves open, in the middle
        Assert.Equal("dict(", Clean(Defined + "x = |\n", "dict("));       // ...but a last opener is a call that goes on below
        Assert.Equal("{", Clean(Defined + "data = |\n", "{"));
        Assert.Equal("c)", Clean(Defined + "total = add(\n    a,\n    b, |\n", "c)"));   // the last argument closes the call of an earlier line
        Assert.Equal("\")\"", Clean(Defined + "s = |\n", "\")\""));       // a bracket in a string is text
        Assert.Equal("items[0]  # (", Clean(Defined + "first = |\n", "items[0]  # ("));  // ...and in a comment
        Assert.True(Clean(Defined + "const a = |", "f(x));", "javascript") == null);
    }

    [Fact]
    public void TheLineBelowIsNotRepeated()
    {
        Assert.True(Clean("    x = |\n    x = compute()\n", "compute()") == null);
        Assert.Equal("compute()", Clean("    x = |\n    y = compute()\n", "compute()"));
    }

    [Fact]
    public void APythonSuggestionUsesOnlyKnownNames()
    {
        string file = "import json\nfrom shop import Cart\n\ndef load(path):\n    with open(path) as f:\n        |\n";
        Assert.Equal("return json.load(f)", Clean(file, "return json.load(f)"));
        Assert.True(Clean(file, "return yaml.safe_load(f)") == null);            // yaml is not imported
        Assert.Equal("cart = Cart(items=list(f))", Clean(file, "cart = Cart(items=list(f))"));   // assigned, a keyword argument, a builtin
        Assert.True(Clean(file, "return parse_config(f)") == null);              // a function the file does not have
        Assert.Equal("data = f.read().splitlines()", Clean(file, "data = f.read().splitlines()"));   // attributes
        Assert.Equal("for i, line in enumerate(f):", Clean(file, "for i, line in enumerate(f):"));   // loop targets
        Assert.Equal("return [x for x in f]", Clean(file, "return [x for x in f]"));
        Assert.Equal("print(f\"{path} loaded\", \"unknown words\")", Clean(file, "print(f\"{path} loaded\", \"unknown words\")"));
        Assert.True(Clean(file, "print(f\"{nowhere}\")") == null);                // an f-string's expression is code
        Assert.Equal("handler = lambda event, ctx: ctx", Clean(file, "handler = lambda event, ctx: ctx"));
        Assert.Equal("import yaml", Clean(file, "import yaml"));
        Assert.Equal("def parse(text, strict=False):", Clean(file, "def parse(text, strict=False):"));
        // a word the person started is finished by the model: "js" + "on.load(f)"
        Assert.Equal("on.load(f)", Clean("import json\nwith open(p) as f:\n    data = js|\n", "on.load(f)"));
        // other languages are not checked for names
        Assert.Equal("yaml.parse(text);", Clean("const data = |", "yaml.parse(text);", "javascript"));
    }

    [Fact]
    public void TheCpuBuildIsTheBestOneItRuns()
    {
        Assert.True(new[] { "avx512", "avx2", "avx", "noavx" }.Contains(FullLine.Variant()));
    }

    /// <summary>With PYMAGIC_TEST_FULLLINE=1 and the model fetched (node models/fetch.mjs): the model writes lines that pass the checks, fast.</summary>
    [Fact]
    public void TheBundledModelWritesTheLine()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_FULLLINE") != "1") return;
        Assert.True(FullLine.ModelPath() != null, "models/" + FullLine.ModelFile + " is missing: node models/fetch.mjs");
        static FullLine.Suggestion Suggest(string marked, string language = "python")
        {
            var (text, offset) = At(marked);
            return FullLine.SuggestAsync(text, offset, language, CancellationToken.None).GetAwaiter().GetResult();
        }
        var first = Suggest("import json\n\ndef load_config(path):\n    with open(path) as f:\n        config = |\n    return config\n");
        Assert.True(first.Error == null, first.Error);
        Assert.Equal("json.load(f)", first.Text);
        // typed on: the work for the text before is kept, so the next one is quicker than the first (which loaded the model)
        var next = Suggest("import json\n\ndef load_config(path):\n    with open(path) as f:\n        config = json.|\n    return config\n");
        Assert.True(next.Text != null && next.Text.StartsWith("load"), next.Text);
        Assert.True(next.Ms < 2000, next.Ms + " ms");
        var js = Suggest("function total(items) {\n  let sum = 0;\n  for (const item of items) {\n    sum += |\n  }\n  return sum;\n}\n", "javascript");
        Assert.True(js.Text != null && js.Text.StartsWith("item"), js.Text);
        // where no line may be completed, the model is not asked
        Assert.True(Suggest("x = 1  # a |").Text == null);
    }
}
