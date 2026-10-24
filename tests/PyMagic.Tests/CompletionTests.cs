using PyMagic.Server;

namespace PyMagic.Tests;

public class CompletionTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Lib = "file:///c:/proj/lib.py";

    /// <summary>`|` marks the cursor.</summary>
    static IReadOnlyList<CompletionItem> Complete(string marked, params (string Uri, string Text)[] others) =>
        CompleteIn(null, marked, others);

    static IReadOnlyList<CompletionItem> CompleteIn(Action<LanguageService>? setup, string marked, params (string Uri, string Text)[] others)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        var svc = new LanguageService();
        setup?.Invoke(svc);
        foreach (var (u, t) in others) svc.Open(u, t, 1);
        svc.Open(Main, text, 1);
        return svc.Completion(Main, new LineMap(text).PositionOf(i));
    }

    static List<string> Labels(IEnumerable<CompletionItem> items) => items.Select(i => i.Label).ToList();

    static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var lines = new LineMap(text);
        foreach (var e in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    // ------------------------------------------------------------ where nothing is offered

    [Fact]
    public void NoCompletionInCommentsAndStrings()
    {
        Assert.Equal(0, Complete("value = 1\n# va|\n").Count);
        Assert.Equal(0, Complete("value = 1\nname = 'va|'\n").Count);
        Assert.Equal(0, Complete("value = 1\n\"\"\"Doc with va|\n\"\"\"\n").Count);
        Assert.True(Labels(Complete("value = 1\nname = 'x' + va|\n")).Contains("value"), "after the string it is code again");
    }

    [Fact]
    public void FieldsOfAnFStringCompleteLikeCode()
    {
        Assert.True(Labels(Complete("value = 1\ntext = f'result: {va|}'\n")).Contains("value"));
        Assert.Equal(0, Complete("value = 1\ntext = f'result: va|'\n").Count);
        Assert.Equal(0, Complete("value = 1\ntext = f'{{va|}}'\n").Count);
        var members = Labels(Complete("name = 'x'\ntext = f'{name.up|}'\n"));
        Assert.True(members.Contains("upper"), string.Join(",", members.Take(10)));
    }

    [Fact]
    public void NoCompletionWhereANewNameIsMade()
    {
        Assert.Equal(0, Complete("value = 1\ndef va|\n").Count);
        Assert.Equal(0, Complete("value = 1\nclass Va|\n").Count);
        Assert.Equal(0, Complete("import os as va|\n").Count);
        Assert.Equal(0, Complete("for va| in []:\n    pass\n").Count);
        Assert.Equal(0, Complete("x = 3.|\n").Count);
        Assert.True(Labels(Complete("for item in va|\n")).Count >= 0, "after `in` the iterable is looked up");
    }

    // ------------------------------------------------------------ keywords and templates

    [Fact]
    public void KeywordsFitTheirPlace()
    {
        Assert.True(Labels(Complete("re|")).Contains("return"));
        Assert.True(Labels(Complete("ra|")).Contains("raise"));
        Assert.True(!Labels(Complete("an|")).Contains("and"), "`and` does not start a statement");
        var expression = Labels(Complete("x = 1\ny = x an|"));
        Assert.True(expression.Contains("and"));
        Assert.True(!Labels(Complete("y = re|")).Contains("return"), "`return` does not start an expression");
    }

    [Fact]
    public void CompoundStatementsComeAsSnippets()
    {
        var items = Complete("fo|");
        var forItem = items.Single(i => i.Label == "for");
        Assert.True(forItem.IsSnippet, "for is a template, not a bare keyword");
        Assert.Equal(CompletionKind.Snippet, forItem.Kind);
        Assert.Contains("in ${2:items}", forItem.InsertText!);
        Assert.Equal(1, items.Count(i => i.Label == "for"));
        Assert.True(Complete("x = [1 fo|").Any(i => i.Label == "for" && i.Kind == CompletionKind.Keyword), "inside an expression it stays a keyword");
    }

    [Fact]
    public void TemplatesThatAddAnImport()
    {
        var dc = Complete("dc|").Single(i => i.Label == "dc" && i.Kind == CompletionKind.Snippet);
        Assert.True(dc.IsSnippet && dc.AdditionalTextEdits is { Count: 1 });
        Assert.Contains("from dataclasses import dataclass", dc.AdditionalTextEdits![0].NewText);
        var withImport = Complete("from dataclasses import dataclass\n\n\ndc|").Single(i => i.Label == "dc" && i.Kind == CompletionKind.Snippet);
        Assert.True(withImport.AdditionalTextEdits == null, "already imported");
        Assert.True(Complete("log|").Single(i => i.Label == "log" && i.Kind == CompletionKind.Snippet).AdditionalTextEdits![0].NewText.Contains("import logging"));
    }

    [Fact]
    public void ClassBodyTemplatesStayInClasses()
    {
        Assert.True(Complete("class A:\n    in|").Any(i => i.Label == "init"));
        Assert.True(!Complete("in|").Any(i => i.Label == "init"));
        Assert.True(!Complete("def f():\n    in|").Any(i => i.Label == "init"));
        Assert.True(Complete("class A:\n    pr|").Any(i => i.Label == "prop"));
    }

    [Fact]
    public void TemplatesOnlyAtTheStartOfAStatement()
    {
        Assert.True(!Complete("x = ma|").Any(i => i.Label == "main" && i.Kind == CompletionKind.Snippet));
        Assert.True(Complete("ma|").Any(i => i.Label == "main" && i.Kind == CompletionKind.Snippet));
        Assert.True(!Complete("@pr|").Any(i => i.Kind == CompletionKind.Snippet), "a decorator names a function");
    }

    [Fact]
    public void UsersOwnSnippetsFromTheWorkspace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_snip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".pymagic"));
        try
        {
            File.WriteAllText(Path.Combine(dir, ".pymagic", "snippets.json"),
                "{ // mine\n \"Print debug\": { \"prefix\": [\"pdbg\", \"dbgp\"], \"body\": [\"print(f'{${1:x}=}')\", \"$0\"], \"description\": \"debug print\" } }");
            var items = CompleteIn(s => s.SourceRoots.Add(dir), "pdb|");
            var item = items.Single(i => i.Label == "pdbg");
            Assert.True(item.IsSnippet);
            Assert.Equal("print(f'{${1:x}=}')\n$0", item.InsertText);
            Assert.Equal("debug print", item.Detail);
            Assert.True(CompleteIn(s => s.SourceRoots.Add(dir), "dbg|").Any(i => i.Label == "dbgp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------ call snippets

    [Fact]
    public void FunctionsInsertTheirParentheses()
    {
        string src = "def greet(name):\n    pass\n\n\ndef run():\n    pass\n\n\nclass Thing:\n    pass\n\n\n";
        var items = Complete(src + "gr|");
        Assert.Equal("greet($0)", items.Single(i => i.Label == "greet").InsertText);
        Assert.True(items.Single(i => i.Label == "greet").IsSnippet);
        Assert.Equal("run()", Complete(src + "ru|").Single(i => i.Label == "run").InsertText);
        Assert.True(Complete(src + "Th|").Single(i => i.Label == "Thing").InsertText == null, "classes are also used as types");
    }

    [Fact]
    public void NoParenthesesWhereTheyAreNotWanted()
    {
        string src = "def greet(name):\n    pass\n\n\n";
        Assert.True(Complete(src + "gr|(1)").Single(i => i.Label == "greet").InsertText == null, "there already are some");
        Assert.True(Complete(src + "@gr|\ndef f(): ...\n").Single(i => i.Label == "greet").InsertText == null, "a decorator");
    }

    [Fact]
    public void MethodsInsertParenthesesToo()
    {
        string src = "class A:\n    def go(self, speed):\n        pass\n\n    def stop(self):\n        pass\n\n    @property\n    def size(self):\n        return 1\n\n\na = A()\n";
        var items = Complete(src + "a.|");
        Assert.Equal("go($0)", items.Single(i => i.Label == "go").InsertText);
        Assert.Equal("stop()", items.Single(i => i.Label == "stop").InsertText);
        Assert.True(items.Single(i => i.Label == "size").InsertText == null, "a property is not called");
    }

    // ------------------------------------------------------------ ranking

    [Fact]
    public void NearerNamesRankHigher()
    {
        string src = "alpha_module = 1\n\n\ndef f(alpha_param):\n    alpha_local = 2\n    al|\n";
        var items = Complete(src).Where(i => i.Label.StartsWith("alpha")).ToDictionary(i => i.Label);
        Assert.True(string.CompareOrdinal(items["alpha_local"].SortText, items["alpha_module"].SortText) < 0);
        Assert.True(string.CompareOrdinal(items["alpha_param"].SortText, items["alpha_module"].SortText) < 0);
        var builtin = Complete("al|").Single(i => i.Label == "all");
        Assert.True(string.CompareOrdinal("3alpha", builtin.SortText) < 0, "builtins after names of the file");
    }

    [Fact]
    public void ANameOfTheExpectedTypeRanksFirst()
    {
        string src = "def take(count: int, label: str): ...\n\n\nsize = 1\nsilly = 'text'\n\n\ntake(|)\n";
        var items = Complete(src).ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(items["size"].StartsWith("0"), items["size"]);
        Assert.True(!items["silly"].StartsWith("0"), items["silly"]);
        var second = Complete("def take(count: int, label: str): ...\n\n\nsize = 1\nsilly = 'text'\n\n\ntake(size, |)\n").ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(second["silly"].StartsWith("0") && !second["size"].StartsWith("0"));
    }

    [Fact]
    public void ExpectedTypeWorksBeforeTheClosingBracketIsTyped()
    {
        string head = "def take(count: int, label: str): ...\n\n\nsize = 1\nsilly = 'text'\n\n\n";
        var open = Complete(head + "take(1, |").ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(open["silly"].StartsWith("0") && !open["size"].StartsWith("0"));
        var nested = Complete(head + "print(take(size, |").ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(nested["silly"].StartsWith("0"));
        string cls = "class Box:\n    width: int = 1\n    title: str = 'x'\n\n\ndef take(count: int): ...\n\n\nbox = Box()\ntake(box.|";
        var members = Complete(cls).ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(members["width"].StartsWith("0") && !members["title"].StartsWith("0"));
    }

    [Fact]
    public void ExpectedTypeFromAnAnnotatedAssignmentAndFromReturn()
    {
        var assigned = Complete("number = 1\nword = 'a'\nvalue: str = |\n").ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(assigned["word"].StartsWith("0") && !assigned["number"].StartsWith("0"));
        var returned = Complete("number = 1\nword = 'a'\n\n\ndef f() -> int:\n    return |\n").ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(returned["number"].StartsWith("0") && !returned["word"].StartsWith("0"));
    }

    [Fact]
    public void ExpectedTypeAlsoRanksMembers()
    {
        string src = "class Box:\n    width: int = 1\n    title: str = 'x'\n\n\ndef take(count: int): ...\n\n\nbox = Box()\ntake(box.|)\n";
        var items = Complete(src).ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(items["width"].StartsWith("0") && !items["title"].StartsWith("0"));
    }

    [Fact]
    public void MembersOfTheClassComeBeforeInheritedOnes()
    {
        string src = "class Base:\n    def inherited(self): ...\n\n\nclass Child(Base):\n    def own(self): ...\n\n\nchild = Child()\nchild.|\n";
        var items = Complete(src).ToDictionary(i => i.Label, i => i.SortText!, StringComparer.Ordinal);
        Assert.True(string.CompareOrdinal(items["own"], items["inherited"]) < 0);
    }

    // ------------------------------------------------------------ auto-import

    const string LibSource = "class Widget:\n    pass\n\n\ndef make_widget():\n    pass\n\n\nMAX_SIZE = 3\n\n\ndef _private(): ...\n";

    [Fact]
    public void NamesOfOtherModulesComeWithTheirImport()
    {
        string src = "wid = 1\nWid|\n";
        var items = Complete(src, (Lib, LibSource));
        var widget = items.Single(i => i.Label == "Widget");
        Assert.Equal("from lib import Widget", widget.Detail);
        Assert.Equal(CompletionKind.Class, widget.Kind);
        string text = "Widget\n";
        Assert.Equal("from lib import Widget\nWidget\n", Apply(text, widget.AdditionalTextEdits!).Replace("from lib import Widget\n\n", "from lib import Widget\n"));
    }

    [Fact]
    public void FunctionsAndConstantsAreOfferedPrivateOnesAreNot()
    {
        var items = Complete("make|\n", (Lib, LibSource));
        Assert.True(items.Any(i => i.Label == "make_widget" && i.Detail == "from lib import make_widget"));
        Assert.True(Complete("MAX|\n", (Lib, LibSource)).Any(i => i.Label == "MAX_SIZE" && i.Kind == CompletionKind.Constant));
        Assert.True(!Complete("_pri|\n", (Lib, LibSource)).Any(i => i.Label == "_private"));
    }

    [Fact]
    public void AutoImportMergesIntoAnExistingFromImport()
    {
        string text = "from lib import make_widget\n\nWidget\n";
        int i = text.IndexOf("Widget");
        var svc = new LanguageService();
        svc.Open(Lib, LibSource, 1);
        svc.Open(Main, text.Insert(i + 3, "|").Replace("|", ""), 1);
        var items = svc.Completion(Main, new LineMap(text).PositionOf(i + 3));
        var widget = items.Single(x => x.Label == "Widget" && x.Detail != null);
        Assert.Equal("from lib import make_widget, Widget\n\nWidget\n", Apply(text, widget.AdditionalTextEdits!));
    }

    [Fact]
    public void NoAutoImportForWhatIsAlreadyThereOrForOneLetter()
    {
        Assert.True(!Complete("from lib import Widget\nWid|\n", (Lib, LibSource)).Any(i => i.Label == "Widget" && i.Detail != null && i.Detail.StartsWith("from lib")));
        Assert.True(!Complete("W|\n", (Lib, LibSource)).Any(i => i.Label == "Widget"));
        Assert.True(!Complete("class Widget: ...\nWid|\n", (Lib, LibSource)).Any(i => i.Label == "Widget" && i.AdditionalTextEdits != null));
    }

    [Fact]
    public void ModulesCanBeImportedByName()
    {
        var items = Complete("too|\n", (Root("tools.py"), "def f(): ..."));
        var item = items.Single(i => i.Label == "tools");
        Assert.Equal("import tools", item.Detail);
        Assert.Equal(CompletionKind.Module, item.Kind);
        Assert.Contains("import tools", item.AdditionalTextEdits![0].NewText);
        Assert.True(Complete("jso|\n").Any(i => i.Label == "json" && i.Detail == "import json"), "standard library modules too");
    }

    static string Root(string file) => "file:///c:/proj/" + file;

    [Fact]
    public void ProjectNamesRankBeforeLibraryNames()
    {
        var items = Complete("Pat|\n", (Lib, "class PathFinder:\n    pass\n"));
        var project = items.Single(i => i.Label == "PathFinder");
        Assert.True(project.SortText!.StartsWith("8"));
    }

    // ------------------------------------------------------------ import statements

    [Fact]
    public void ModuleNamesAfterImport()
    {
        var items = Labels(Complete("import o|\n", (Root("tools.py"), "")));
        Assert.True(items.Contains("os") && items.Contains("operator"), string.Join(",", items.Take(8)));
        Assert.True(Labels(Complete("import to|\n", (Root("tools.py"), ""))).Contains("tools"));
        Assert.True(Labels(Complete("import os, su|\n")).Contains("subprocess"), "after a comma");
        Assert.Equal(0, Complete("import os as j|\n").Count);
    }

    static (string Uri, string Text)[] Package() => new[]
    {
        (Root("pkg/__init__.py"), "version = 1\n\n\ndef helper(): ...\n"),
        (Root("pkg/alpha.py"), "class Alpha: ...\n\n\ndef make(): ...\n\n\n_hidden = 1\n"),
        (Root("pkg/beta.py"), "VALUE = 1\n"),
        (Root("pkg/sub/__init__.py"), ""),
        (Root("pkg/sub/deep.py"), "x = 1\n"),
    };

    static IReadOnlyList<CompletionItem> CompleteAt(string uri, string marked, params (string Uri, string Text)[] others) =>
        CompleteAtIn(uri, marked, others);

    static IReadOnlyList<CompletionItem> CompleteAtIn(string uri, string marked, (string Uri, string Text)[] others)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        foreach (var (u, t) in others) svc.Open(u, t, 1);
        svc.Open(uri, text, 1);
        return svc.Completion(uri, new LineMap(text).PositionOf(i));
    }

    [Fact]
    public void SubmodulesAfterADot()
    {
        var items = CompleteAt(Root("main.py"), "import pkg.a|\n", Package());
        Assert.Equal("alpha", Labels(items).Single());
        var all = Labels(CompleteAt(Root("main.py"), "import pkg.|\n", Package()));
        Assert.True(all.Contains("alpha") && all.Contains("beta") && all.Contains("sub"), string.Join(",", all));
        Assert.True(Labels(CompleteAt(Root("main.py"), "from pkg.sub import |\n", Package())).Contains("deep"));
        Assert.True(Labels(CompleteAt(Root("main.py"), "from pkg.|\n", Package())).Contains("beta"));
    }

    [Fact]
    public void NamesAfterFromImport()
    {
        var items = Labels(CompleteAt(Root("main.py"), "from pkg import |\n", Package()));
        Assert.True(items.Contains("alpha") && items.Contains("beta") && items.Contains("helper") && items.Contains("version"), string.Join(",", items));
        var members = Labels(CompleteAt(Root("main.py"), "from pkg.alpha import |\n", Package()));
        Assert.True(members.Contains("Alpha") && members.Contains("make") && !members.Contains("_hidden"), string.Join(",", members));
        Assert.Equal("Alpha", Labels(CompleteAt(Root("main.py"), "from pkg.alpha import Al|\n", Package())).Single());
    }

    [Fact]
    public void ImportedNamesAreNotOfferedAgainAndAliasesAreNew()
    {
        var items = Labels(CompleteAt(Root("main.py"), "from pkg.alpha import Alpha, |\n", Package()));
        Assert.True(!items.Contains("Alpha") && items.Contains("make"), string.Join(",", items));
        Assert.Equal(0, CompleteAt(Root("main.py"), "from pkg.alpha import Alpha as |\n", Package()).Count);
    }

    [Fact]
    public void ParenthesisedAndMultilineImportLists()
    {
        var items = Labels(CompleteAt(Root("main.py"), "from pkg.alpha import (\n    Alpha,\n    |\n)\n", Package()));
        Assert.True(items.Contains("make") && !items.Contains("Alpha"), string.Join(",", items));
        Assert.True(Labels(CompleteAt(Root("main.py"), "from pkg.alpha import (Al|\n", Package())).Contains("Alpha"));
    }

    [Fact]
    public void RelativeImports()
    {
        var items = Labels(CompleteAt(Root("pkg/alpha.py"), "from . import |\n", Package().Where(p => !p.Uri.EndsWith("alpha.py")).ToArray()));
        Assert.True(items.Contains("beta") && items.Contains("helper"), string.Join(",", items));
        var modules = Labels(CompleteAt(Root("pkg/alpha.py"), "from .|\n", Package().Where(p => !p.Uri.EndsWith("alpha.py")).ToArray()));
        Assert.True(modules.Contains("beta") && modules.Contains("sub"), string.Join(",", modules));
        Assert.True(Labels(CompleteAt(Root("pkg/alpha.py"), "from .sub import |\n", Package().Where(p => !p.Uri.EndsWith("alpha.py")).ToArray())).Contains("deep"));
    }

    [Fact]
    public void ImportLikeWordsInCodeAreNotImports()
    {
        var items = Labels(Complete("from_date = 1\nfrom_|\n"));
        Assert.True(items.Contains("from_date"));
    }

    // ------------------------------------------------------------ postfix templates

    [Fact]
    public void PostfixTemplatesWrapTheExpression()
    {
        string src = "names = ['a']\nnames.fo|\n";
        var item = Complete(src).Single(i => i.Label == "for");
        Assert.True(item.IsSnippet);
        Assert.True(item.Detail!.StartsWith("postfix"), item.Detail);
        Assert.Equal("for ${1:item} in names:\n\t$0", item.InsertText);
        // taking the expression away: "names." goes, the typed prefix is replaced by the snippet
        string after = Apply(src.Replace("fo|", "fo"), item.AdditionalTextEdits!);
        Assert.Equal("names = ['a']\nfo\n", after);
    }

    [Fact]
    public void PostfixKeysAndWrappers()
    {
        Assert.Equal("if flag:\n\t$0", Complete("flag = True\nflag.if|\n").Single(i => i.Label == "if").InsertText);
        Assert.Equal("print(flag)", Complete("flag = True\nflag.pri|\n").Single(i => i.Label == "print").InsertText);
        Assert.Equal("if flag is None:\n\t$0", Complete("flag = True\nflag.no|\n").Single(i => i.Label == "none").InsertText);
        Assert.Equal("${1:name} = flag", Complete("flag = True\nflag.va|\n").Single(i => i.Label == "var").InsertText);
    }

    [Fact]
    public void PostfixOnABracketedExpression()
    {
        string src = "a = 1\nb = 2\n(a + b).pri|\n";
        var item = Complete(src).Single(i => i.Label == "print");
        Assert.Equal("print(a + b)", item.InsertText!.Replace("print((a + b))", "print(a + b)"));
        string after = Apply(src.Replace("pri|", "pri"), item.AdditionalTextEdits!);
        Assert.Equal("a = 1\nb = 2\npri\n", after);
    }

    [Fact]
    public void PostfixTemplatesFitTheType()
    {
        Assert.True(!Complete("count = 1\ncount.fo|\n").Any(i => i.Label == "for"), "an int is not iterable");
        Assert.True(!Complete("count = 1\ncount.le|\n").Any(i => i.Label == "len"), "an int has no length");
        Assert.True(Complete("names = ['a']\nnames.le|\n").Any(i => i.Label == "len"));
        Assert.True(!Complete("names = ['a']\nnames.ite|\n").Any(i => i.Label == "items"), "a list has no items()");
        Assert.True(Complete("table = {'a': 1}\ntable.ite|\n").Any(i => i.Label == "items" && i.Kind == CompletionKind.Snippet));
        Assert.True(Complete("anything.fo|\n").Any(i => i.Label == "for"), "unknown types get every template");
    }

    [Fact]
    public void PostfixSpecialCharactersAreEscaped()
    {
        var item = Complete("price = '$5'\nprice.pri|\n").Single(i => i.Label == "print");
        Assert.Equal("print(price)", item.InsertText);
        var literal = Complete("x = '$1'\n'$1'.pri|\n").SingleOrDefault(i => i.Label == "print");
        if (literal != null) Assert.Contains("\\$1", literal.InsertText!);
    }

    [Fact]
    public void NoPostfixTemplatesWithoutATypedPrefix()
    {
        Assert.True(!Complete("names = ['a']\nnames.|\n").Any(i => i.Kind == CompletionKind.Snippet));
    }

    [Fact]
    public void SortOfFixturesAndParametersIsUntouched()
    {
        string conf = "import pytest\n\n\n@pytest.fixture\ndef db(): return 1\n";
        var items = CompleteAt(Root("test_a.py"), "def test_x(d|):\n    pass\n", (Root("conftest.py"), conf));
        Assert.True(items.Any(i => i.Label == "db"));
    }
}
