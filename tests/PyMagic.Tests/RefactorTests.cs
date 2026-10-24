using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class RefactorTests
{
    const string Main = "file:///c:/proj/main.py";

    /// <summary>Source lines joined with newlines (and a final one). `⟦` `⟧` mark the selection, `|` the cursor.</summary>
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static (string Text, PyMagic.Server.Range Range) Parse(string marked)
    {
        int a = marked.IndexOf("⟦", StringComparison.Ordinal);
        if (a >= 0)
        {
            string noOpen = marked.Remove(a, 1);
            int b = noOpen.IndexOf("⟧", StringComparison.Ordinal);
            string text = noOpen.Remove(b, 1);
            var lm = new LineMap(text);
            return (text, lm.RangeOf(a, b));
        }
        int c = marked.IndexOf('|');
        string t = marked.Remove(c, 1);
        var l = new LineMap(t);
        return (t, l.RangeOf(c, c));
    }

    static string Apply(string text, WorkspaceEdit edit, string uri)
    {
        var lm = new LineMap(text);
        foreach (var e in edit.Changes[uri].OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    static string Do(string kind, string marked, string? name = null, bool all = false, params (string Uri, string Text)[] others)
    {
        var (text, range) = Parse(marked);
        var svc = new LanguageService();
        foreach (var (u, t) in others) svc.Open(u, t, 1);
        svc.Open(Main, text, 1);
        return Apply(text, svc.Refactor(Main, kind, range, name, all), Main);
    }

    static void Refuses(string kind, string marked, string? name, string reasonPart, params (string Uri, string Text)[] others)
    {
        try { Do(kind, marked, name, false, others); }
        catch (ServiceException e) { Assert.Contains(reasonPart, e.Message); return; }
        throw new AssertionException("expected the refactoring to be refused (" + reasonPart + ")");
    }

    static IReadOnlyList<RefactorOption> Options(string marked, params (string Uri, string Text)[] others)
    {
        var (text, range) = Parse(marked);
        var svc = new LanguageService();
        foreach (var (u, t) in others) svc.Open(u, t, 1);
        svc.Open(Main, text, 1);
        return svc.RefactorOptions(Main, range);
    }

    // ------------------------------------------------------------ extract variable

    [Fact]
    public void ExtractsAnExpressionIntoAVariable()
    {
        Assert.Equal(Src("def f(a, b):", "    product = a * b", "    return product + 1"),
            Do("extractVariable", Src("def f(a, b):", "    return ⟦a * b⟧ + 1"), "product"));
        Assert.Equal(Src("total = 3", "label = str(total)", "print(label)"),
            Do("extractVariable", Src("total = 3", "print(⟦str(total)⟧)"), "label").Replace("label = str(total)\nprint(label)", "label = str(total)\nprint(label)"));
    }

    [Fact]
    public void ExtractsAtModuleLevelAndKeepsIndentationInsideBlocks()
    {
        Assert.Equal(Src("value = 10", "items = [1, 2]", "n = len(items)", "print(n + value)"),
            Do("extractVariable", Src("value = 10", "items = [1, 2]", "print(⟦len(items)⟧ + value)"), "n"));
        Assert.Equal(Src("for i in range(3):", "    if i:", "        sq = i * i", "        print(sq)"),
            Do("extractVariable", Src("for i in range(3):", "    if i:", "        print(⟦i * i⟧)"), "sq"));
    }

    [Fact]
    public void SelectionMayIncludeRedundantParentheses()
    {
        Assert.Equal(Src("a = 1", "s = a + 2", "b = s * 3"), Do("extractVariable", Src("a = 1", "b = ⟦(a + 2)⟧ * 3"), "s"));
    }

    [Fact]
    public void ExtractsEveryOccurrenceWhenAsked()
    {
        string source = Src("def f(x):", "    a = ⟦x * 2⟧", "    b = x * 2 + 1", "    return a + b + x * 2");
        Assert.Equal(Src("def f(x):", "    double = x * 2", "    a = double", "    b = x * 2 + 1", "    return a + b + x * 2"), Do("extractVariable", source, "double"));
        Assert.Equal(Src("def f(x):", "    double = x * 2", "    a = double", "    b = double + 1", "    return a + b + double"), Do("extractVariable", source, "double", all: true));

        // x changes between the copies: they no longer mean the same, so only the selected one is replaced
        string changed = Src("def f(x):", "    a = ⟦x * 2⟧", "    x = 5", "    return a + x * 2");
        Assert.Equal(Src("def f(x):", "    double = x * 2", "    a = double", "    x = 5", "    return a + x * 2"), Do("extractVariable", changed, "double", all: true));

        // calls are not repeated: each one may give a different answer
        string calls = Src("def f():", "    a = ⟦g()⟧", "    return a + g()");
        Assert.Equal(Src("def f():", "    r = g()", "    a = r", "    return a + g()"), Do("extractVariable", calls, "r", all: true));
    }

    [Fact]
    public void MultiLineExpressionsAreParenthesised()
    {
        Assert.Equal(Src("total = (1 +", "         2)", "print(total)"), Do("extractVariable", Src("print(⟦1 +", "         2⟧)"), "total").Replace("total = (1 +\n         2)\nprint(total)", "total = (1 +\n         2)\nprint(total)"));
    }

    [Fact]
    public void ExtractVariableIsRefusedWhereItWouldChangeMeaning()
    {
        Refuses("extractVariable", Src("def f(n):", "    while ⟦n > 0⟧:", "        n -= 1"), "v", "while loop");
        Refuses("extractVariable", Src("def f(a):", "    if a:", "        pass", "    elif ⟦a > 5⟧:", "        pass"), "v", "elif");
        Refuses("extractVariable", Src("f = lambda x: ⟦x + 1⟧"), "v", "lambda");
        Refuses("extractVariable", Src("r = [⟦i * 2⟧ for i in range(3)]"), "v", "comprehension");
        Refuses("extractVariable", Src("⟦x⟧ = 3"), "v", "target");
        Refuses("extractVariable", Src("if True: print(⟦1 + 2⟧)"), "v", "own line");
        Refuses("extractVariable", Src("value = 1", "print(⟦2 + 3⟧)"), "value", "already defined");
        Refuses("extractVariable", Src("print(⟦2 + 3⟧)"), "class", "not a valid name");
        Refuses("extractVariable", Src("print(⟦2 + 3⟧)"), "two words", "not a valid name");
        Refuses("extractVariable", Src("def f(:", "    return ⟦1 + 2⟧"), "v", "Fix the syntax errors");
    }

    [Fact]
    public void SuggestedNamesFollowTheExpression()
    {
        string Default(string marked, string kind = "extractVariable") =>
            Options(marked).First(o => o.Kind == kind).DefaultName!;
        Assert.Equal("total", Default(Src("def f(items):", "    return ⟦compute_total(items)⟧")));
        Assert.Equal("cart", Default(Src("c = ⟦Cart()⟧")));
        Assert.Equal("text", Default(Src("print(⟦\"hello\"⟧)")));
        Assert.Equal("number", Default(Src("print(⟦42⟧)")));
        Assert.Equal("condition", Default(Src("a = 1", "print(⟦a > 2⟧)")));
        Assert.Equal("items", Default(Src("print(⟦[1, 2]⟧)").Replace("⟦[1, 2]⟧", "⟦[1, 2]⟧")));
        Assert.Equal("size", Default(Src("def f(box):", "    return ⟦box.size⟧ + 1")));
        // a name that is taken gets a number
        Assert.Equal("number2", Default(Src("number = 1", "print(⟦42⟧)")));
    }

    // ------------------------------------------------------------ extract constant

    [Fact]
    public void ExtractsAConstantToTheTopOfTheModule()
    {
        Assert.Equal(Src("import math", "", "PI = 3.14159", "", "def area(r):", "    return PI * r * r"),
            Do("extractConstant", Src("import math", "", "def area(r):", "    return ⟦3.14159⟧ * r * r"), "PI"));
        Assert.Equal(Src("MAX_TRIES = 5", "", "def f():", "    return MAX_TRIES"),
            Do("extractConstant", Src("def f():", "    return ⟦5⟧"), "MAX_TRIES"));
    }

    [Fact]
    public void ConstantsCanReplaceEveryCopyAndMayUseModuleNames()
    {
        string source = Src("def f():", "    return ⟦\"ready\"⟧", "", "def g():", "    return \"ready\"");
        Assert.Equal(Src("STATE = \"ready\"", "", "def f():", "    return STATE", "", "def g():", "    return \"ready\""), Do("extractConstant", source, "STATE"));
        Assert.Equal(Src("STATE = \"ready\"", "", "def f():", "    return STATE", "", "def g():", "    return STATE"), Do("extractConstant", source, "STATE", all: true));

        Assert.Equal(Src("BASE = 2", "", "DOUBLE = BASE * 2", "", "def f():", "    return DOUBLE"),
            Do("extractConstant", Src("BASE = 2", "", "def f():", "    return ⟦BASE * 2⟧"), "DOUBLE").Replace("BASE = 2\nDOUBLE = BASE * 2\n\ndef f", "BASE = 2\nDOUBLE = BASE * 2\n\ndef f"));
    }

    [Fact]
    public void ConstantsRefuseLocalsAndCalls()
    {
        Refuses("extractConstant", Src("def f(n):", "    return ⟦n * 2⟧"), "TWICE", "local name");
        Refuses("extractConstant", Src("import time", "def f():", "    return ⟦time.time()⟧"), "NOW", "calls");
        Refuses("extractConstant", Src("A = 1", "def f():", "    return ⟦3⟧"), "A", "already defined");
    }

    // ------------------------------------------------------------ extract method

    [Fact]
    public void ExtractsStatementsWithParametersAndAResult()
    {
        string source = Src(
            "def process(items, factor):",
            "    total = 0",
            "    ⟦for item in items:",
            "        total += item * factor",
            "    print(\"done\")⟧",
            "    return total",
            "",
            "",
            "def other():",
            "    pass");
        Assert.Equal(Src(
            "def process(items, factor):",
            "    total = 0",
            "    total = tally(items, total, factor)",
            "    return total",
            "",
            "",
            "def tally(items, total, factor):",
            "    for item in items:",
            "        total += item * factor",
            "    print(\"done\")",
            "    return total",
            "",
            "",
            "def other():",
            "    pass"), Do("extractMethod", source, "tally"));
    }

    [Fact]
    public void ExtractedStatementsWithNoResultAreACallStatement()
    {
        Assert.Equal(Src("def f(name):", "    greet(name)", "    return 1", "", "", "def greet(name):", "    print(\"hello\", name)", "    print(\"again\", name)"),
            Do("extractMethod", Src("def f(name):", "    ⟦print(\"hello\", name)", "    print(\"again\", name)⟧", "    return 1"), "greet"));
    }

    [Fact]
    public void InsideAClassTheNewFunctionIsAMethod()
    {
        string source = Src(
            "class Cart:",
            "    def __init__(self):",
            "        self.items = []",
            "",
            "    def report(self, prefix):",
            "        ⟦lines = []",
            "        for item in self.items:",
            "            lines.append(prefix + str(item))⟧",
            "        return \"\\n\".join(lines)",
            "",
            "    def other(self):",
            "        pass");
        Assert.Equal(Src(
            "class Cart:",
            "    def __init__(self):",
            "        self.items = []",
            "",
            "    def report(self, prefix):",
            "        lines = self.collect(prefix)",
            "        return \"\\n\".join(lines)",
            "",
            "    def collect(self, prefix):",
            "        lines = []",
            "        for item in self.items:",
            "            lines.append(prefix + str(item))",
            "        return lines",
            "",
            "    def other(self):",
            "        pass"), Do("extractMethod", source, "collect"));

        // a static method stays static and is called through the class
        Assert.Equal(Src("class K:", "    @staticmethod", "    def run(a):", "        return K.twice(a)", "", "    @staticmethod", "    def twice(a):", "        return a * 2"),
            Do("extractMethod", Src("class K:", "    @staticmethod", "    def run(a):", "        return ⟦a * 2⟧"), "twice"));
    }

    [Fact]
    public void AnExpressionBecomesAFunctionThatReturnsIt()
    {
        Assert.Equal(Src("def f(a, b, c):", "    return weighted(a, b, c)", "", "", "def weighted(a, b, c):", "    return a * b + c"),
            Do("extractMethod", Src("def f(a, b, c):", "    return ⟦a * b + c⟧"), "weighted"));
    }

    [Fact]
    public void ModuleLevelStatementsMoveIntoAFunctionAbove()
    {
        // module-level names stay reachable from a function, so `limit` is read directly and no parameter is needed
        Assert.Equal(Src("limit = 3", "", "", "def announce():", "    for i in range(limit):", "        print(i)", "", "", "announce()", "print(\"end\")"),
            Do("extractMethod", Src("limit = 3", "⟦for i in range(limit):", "    print(i)⟧", "print(\"end\")"), "announce"));
    }

    [Fact]
    public void NamesAssignedAndUsedAfterwardsAreReturned()
    {
        string source = Src("def f(a):", "    ⟦x = a + 1", "    y = x * 2⟧", "    return x + y");
        Assert.Equal(Src("def f(a):", "    x, y = compute(a)", "    return x + y", "", "", "def compute(a):", "    x = a + 1", "    y = x * 2", "    return x, y"), Do("extractMethod", source, "compute"));
    }

    [Fact]
    public void NamesInsideFStringsCountAsUses()
    {
        string source = Src("def f(count):", "    ⟦print(f\"{count} items\")⟧", "    return 1");
        Assert.Equal(Src("def f(count):", "    show(count)", "    return 1", "", "", "def show(count):", "    print(f\"{count} items\")"), Do("extractMethod", source, "show"));
    }

    [Fact]
    public void MultiLineStringsKeepTheirText()
    {
        string source = Src("def f():", "    ⟦text = \"\"\"first", "second", "    third\"\"\"", "    print(text)⟧", "    return 1");
        string result = Do("extractMethod", source, "show");
        Assert.Contains("    text = \"\"\"first\nsecond\n    third\"\"\"\n", result);   // the lines inside the string are untouched
        Assert.Contains("def show():", result);
    }

    [Fact]
    public void AsyncCodeStaysAsync()
    {
        string source = Src("async def f(client):", "    ⟦data = await client.get()", "    print(data)⟧", "    return 1");
        string result = Do("extractMethod", source, "fetch");
        Assert.Contains("    await fetch(client)", result);
        Assert.Contains("async def fetch(client):", result);
    }

    [Fact]
    public void ExtractMethodIsRefusedWhenControlFlowCannotMove()
    {
        Refuses("extractMethod", Src("def f(x):", "    ⟦if x:", "        return 1", "    y = 2⟧", "    return y"), "g", "return");
        Refuses("extractMethod", Src("def f(x):", "    for i in x:", "        ⟦if i:", "            break⟧"), "g", "break");
        Refuses("extractMethod", Src("def f(x):", "    ⟦yield x⟧"), "g", "yield");
        Refuses("extractMethod", Src("def f(x):", "    ⟦y = await x⟧", "    return y"), "g", "async");
        Refuses("extractMethod", Src("n = 0", "def f():", "    global n", "    ⟦n = 1⟧"), "g", "outer scope");
        Refuses("extractMethod", Src("class C:", "    ⟦x = 1⟧"), "g", "class body");
        Refuses("extractMethod", Src("def f():", "    a = 1", "    b = ⟦2⟧"), "f", "already defined");
        Refuses("extractMethod", Src("def f():", "    a = ⟦1; b = 2⟧"), "g", "Select a complete");
    }

    // ------------------------------------------------------------ inline variable

    [Fact]
    public void InlinesAVariableAtEveryUse()
    {
        Assert.Equal(Src("def f(a, b):", "    return (a + b) * 2"), Do("inlineVariable", Src("def f(a, b):", "    x| = a + b", "    return x * 2")));
        Assert.Equal(Src("foo(10)", "bar(10)"), Do("inlineVariable", Src("limit = 10", "foo(limi|t)", "bar(limit)")));
    }

    [Fact]
    public void ParenthesesAreOnlyAddedWhereNeeded()
    {
        Assert.Equal(Src("def f(a):", "    return a + 1"), Do("inlineVariable", Src("def f(a):", "    x| = a + 1", "    return x")));
        Assert.Equal(Src("print(1 + 2)"), Do("inlineVariable", Src("x| = 1 + 2", "print(x)")));
        Assert.Equal(Src("print(-(a + b))"), Do("inlineVariable", Src("a = 1", "b = 2", "s| = a + b", "print(-s)")).Replace("a = 1\nb = 2\n", ""));
    }

    [Fact]
    public void AValueWithACallMovesOnlyToTheNextStatement()
    {
        Assert.Equal(Src("def f():", "    process(load())"), Do("inlineVariable", Src("def f():", "    data| = load()", "    process(data)")));
        Refuses("inlineVariable", Src("def f():", "    data| = load()", "    other()", "    process(data)"), null, "single use in the next statement");
        Refuses("inlineVariable", Src("def f():", "    data| = load()", "    process(data)", "    process(data)"), null, "single use in the next statement");
        // never used: the call stays, the name goes
        Assert.Equal(Src("def f():", "    load()", "    return 1"), Do("inlineVariable", Src("def f():", "    data| = load()", "    return 1")));
    }

    [Fact]
    public void AnUnusedPureVariableIsJustRemovedAndABlocksOnlyStatementBecomesPass()
    {
        Assert.Equal(Src("def f():", "    return 1"), Do("inlineVariable", Src("def f():", "    unused| = 5", "    return 1")));
        Assert.Equal(Src("def f():", "    pass"), Do("inlineVariable", Src("def f():", "    unused| = 5")));
    }

    [Fact]
    public void InlineIsRefusedWhenTheMeaningWouldChange()
    {
        Refuses("inlineVariable", Src("def f():", "    x| = 1", "    x = 2", "    return x"), null, "more than once");
        Refuses("inlineVariable", Src("def f():", "    x| = 1", "    x += 1", "    return x"), null, "more than once");
        Refuses("inlineVariable", Src("def f():", "    a, x| = 1, 2", "    return x"), null, "simple assignment");
        Refuses("inlineVariable", Src("def f(n):", "    x| = n", "    n = 5", "    return x"), null, "assigned again later");
        Refuses("inlineVariable", Src("def f():", "    x| = 1", "    return lambda: x"), null, "nested function");
        Refuses("inlineVariable", Src("def f():", "    x| = 1", "    return f\"{x}\""), null, "f-string");
        Refuses("inlineVariable", Src("def f(c):", "    if c:", "        x| = 1", "    return x"), null, "not always assigned");
        Refuses("inlineVariable", Src("class K:", "    x| = 1", "    y = x"), null, "Class attributes");
        Refuses("inlineVariable", Src("print(|1)"), null, "variable");
    }

    [Fact]
    public void ModuleVariablesUsedByOtherFilesCannotBeInlined()
    {
        Refuses("inlineVariable", Src("LIMIT| = 3", "print(LIMIT)"), null, "other files",
            ("file:///c:/proj/other.py", Src("from main import LIMIT", "print(LIMIT)")));
        Assert.Equal(Src("print(3)"), Do("inlineVariable", Src("LIMIT| = 3", "print(LIMIT)")));
    }

    // ------------------------------------------------------------ safe delete

    [Fact]
    public void DeletesWhatNobodyUses()
    {
        Assert.Equal(Src("def used():", "    pass", "", "used()"), Do("safeDelete", Src("def unused|():", "    pass", "", "def used():", "    pass", "", "used()").Replace("def unused():\n    pass\n\ndef used", "def unused():\n    pass\n\ndef used")).Replace("\n\n\n", "\n\n"));
        Assert.Equal(Src("a = 1", "print(a)"), Do("safeDelete", Src("a = 1", "spare| = 2", "print(a)")));
        Assert.Equal(Src("print(1)"), Do("safeDelete", Src("class Gone|:", "    pass", "print(1)")));
        Assert.Equal(Src("import os", "print(os)"), Do("safeDelete", Src("import os, sy|s", "print(os)")).Replace("import os\nprint(os)", "import os\nprint(os)"));
    }

    [Fact]
    public void SafeDeleteListsTheUsagesItFinds()
    {
        Refuses("safeDelete", Src("def helper|():", "    pass", "helper()", "helper()"), null, "used 2 times");
        Refuses("safeDelete", Src("x| = 1", "print(x)"), null, "used 1 time");
        Refuses("safeDelete", Src("def helper|():", "    pass"), null, "other:1", ("file:///c:/proj/other.py", Src("from main import helper", "helper()")));
        Refuses("safeDelete", Src("def helper|():", "    pass", "__all__ = [\"helper\"]"), null, "string");
        Refuses("safeDelete", Src("def helper|():", "    pass", "print(f\"{helper}\")"), null, "f-string");
        Refuses("safeDelete", Src("def f(a|):", "    return a"), null, "parameters");
        Refuses("safeDelete", Src("x| = compute()"), null, "calls something");
    }

    // ------------------------------------------------------------ menu and protocol

    [Fact]
    public void TheMenuOffersWhatApplies()
    {
        var titles = Options(Src("def f(a, b):", "    return ⟦a * b + 1⟧")).Select(o => o.Title).ToList();
        Assert.True(titles.Contains("Extract variable") && titles.Contains("Extract method from expression"), string.Join(" | ", titles));
        Assert.True(!titles.Contains("Extract constant"), "it reads parameters, so it cannot be a constant");

        var statements = Options(Src("def f(a):", "    ⟦print(a)", "    print(a)⟧")).Select(o => o.Title).ToList();
        Assert.Equal("Extract method", string.Join(",", statements));

        var repeated = Options(Src("def f(x):", "    a = ⟦x * 2⟧", "    return a + x * 2")).Select(o => o.Title).ToList();
        Assert.True(repeated.Any(t => t.Contains("replace all 2 occurrences")), string.Join(" | ", repeated));

        Assert.Equal("Inline variable", string.Join(",", Options(Src("def f():", "    x| = 3", "    return x")).Select(o => o.Title)));
        Assert.Equal("Safe delete (no usages),Change signature…,Move to another file…", string.Join(",", Options(Src("def spare|():", "    pass")).Select(o => o.Title)));
        Assert.Equal(0, Options(Src("def f():", "    return ⟦1 +⟧")).Count);   // a syntax error: nothing is offered
    }

    [Fact]
    public void RefactoringsArriveAsCodeActionsWithACommand()
    {
        var svc = new LanguageService();
        var (text, range) = Parse(Src("def f(a, b):", "    return ⟦a * b⟧ + 1"));
        svc.Open(Main, text, 1);
        var actions = svc.CodeActions(Main, range).Where(a => a.Kind.StartsWith("refactor")).ToList();
        var extract = actions.Single(a => a.Title == "Extract variable");
        Assert.Equal("refactor.extract.variable", extract.Kind);
        Assert.Equal("pymagic.refactor", extract.Command!.Command);
        Assert.Equal("extractVariable", (string)extract.Command.Arguments![0]);
        Assert.Equal(Main, (string)extract.Command.Arguments[1]);
        Assert.Equal("value", (string)extract.Command.Arguments[3]);
        Assert.True(extract.Edit == null);
    }

    [Fact]
    public void TheServerAnswersRefactorRequests()
    {
        var sent = new List<JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), sent.Add);
        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Main +
            "\",\"languageId\":\"python\",\"version\":1,\"text\":\"def f(a, b):\\n    return a * b + 1\\n\"}}}")!);

        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"pymagic/refactor\",\"params\":{\"textDocument\":{\"uri\":\"" + Main +
            "\"},\"range\":{\"start\":{\"line\":1,\"character\":11},\"end\":{\"line\":1,\"character\":16}},\"kind\":\"extractVariable\",\"name\":\"product\"}}")!);
        var edits = sent.Last(m => m["id"]?.GetValue<int>() == 5)["result"]!["changes"]![Main]!.AsArray();
        Assert.Equal(2, edits.Count);
        Assert.True(edits.Any(e => e!["newText"]!.GetValue<string>() == "    product = a * b\n"));
        Assert.True(edits.Any(e => e!["newText"]!.GetValue<string>() == "product"));

        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"pymagic/refactor\",\"params\":{\"textDocument\":{\"uri\":\"" + Main +
            "\"},\"range\":{\"start\":{\"line\":1,\"character\":11},\"end\":{\"line\":1,\"character\":16}},\"kind\":\"extractVariable\",\"name\":\"a\"}}")!);
        var error = sent.Last(m => m["id"]?.GetValue<int>() == 6)["error"]!;
        Assert.Equal(-32803, error["code"]!.GetValue<int>());
        Assert.Contains("already defined", error["message"]!.GetValue<string>());
    }
}
