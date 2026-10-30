using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Introduce Parameter / Attribute, Extract Superclass / Delegate, Pull Up, Push Down, Encapsulate Field, Invert Boolean,
/// Make Function Top-Level and Move Static Members (LanguageService.ClassRefactorings.cs).</summary>
public class ClassRefactoringTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Lib = "file:///c:/proj/lib.py";

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>`⟦` `⟧` mark the selection, `|` the cursor.</summary>
    static (string Text, PyMagic.Server.Range Range) Parse(string marked)
    {
        int a = marked.IndexOf('⟦');
        if (a >= 0)
        {
            string noOpen = marked.Remove(a, 1);
            int b = noOpen.IndexOf('⟧');
            string text = noOpen.Remove(b, 1);
            return (text, new LineMap(text).RangeOf(a, b));
        }
        int c = marked.IndexOf('|');
        string t = marked.Remove(c, 1);
        return (t, new LineMap(t).RangeOf(c, c));
    }

    static string Apply(string text, WorkspaceEdit edit, string uri)
    {
        if (!edit.Changes.TryGetValue(uri, out var list)) return text;
        var lm = new LineMap(text);
        foreach (var e in list.OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    /// <summary>The main file after the refactoring, and the other file (lib.py) when one is given.</summary>
    static (string Main, string Lib) Do(string kind, string marked, string? name = null, string[]? members = null, string? lib = null)
    {
        var (text, range) = Parse(marked);
        var svc = new LanguageService();
        if (lib != null) svc.Open(Lib, lib, 1);
        svc.Open(Main, text, 1);
        var edit = svc.Refactor(Main, kind, range, name, members: members);
        return (Apply(text, edit, Main), lib == null ? "" : Apply(lib, edit, Lib));
    }

    static string Fails(string kind, string marked, string? name = null, string? lib = null)
    {
        try { Do(kind, marked, name, null, lib); }
        catch (ServiceException e) { return e.Message; }
        throw new AssertionException($"{kind} did not fail");
    }

    static List<string> Offered(string marked, string? lib = null)
    {
        var (text, range) = Parse(marked);
        var svc = new LanguageService();
        if (lib != null) svc.Open(Lib, lib, 1);
        svc.Open(Main, text, 1);
        return svc.RefactorOptions(Main, range).Select(o => o.Kind).ToList();
    }

    [Fact]
    public void IntroducesAParameterWithTheExpressionAsItsDefault()
    {
        Assert.Equal(Src("def area(r, pi=3.14159, *, unit='m'):", "    return pi * r * r"),
            Do("introduceParameter", Src("def area(r, *, unit='m'):", "    return ⟦3.14159⟧ * r * r"), "pi").Main);
        Assert.Equal(Src("def f(limit=MAX):", "    return limit"), Do("introduceParameter", Src("def f():", "    return ⟦MAX⟧"), "limit").Main);
        Assert.True(Fails("introduceParameter", Src("def f(a):", "    return ⟦a + 1⟧"), "b").Contains("'a'"));
        Assert.True(Offered(Src("def f():", "    return ⟦2 + 3⟧")).Contains("introduceParameter"));
    }

    [Fact]
    public void IntroducesAnAttributeInInitOrBeforeTheStatement()
    {
        Assert.Equal(Src("class C:", "    def __init__(self):", "        self.a = 1", "        self.limit = 10", "", "    def f(self):", "        return self.limit"),
            Do("introduceField", Src("class C:", "    def __init__(self):", "        self.a = 1", "", "    def f(self):", "        return ⟦10⟧"), "limit").Main);
        // a local name in it: set just before the statement
        Assert.Equal(Src("class C:", "    def f(self, x):", "        self.twice = x * 2", "        return self.twice"),
            Do("introduceField", Src("class C:", "    def f(self, x):", "        return ⟦x * 2⟧"), "twice").Main);
        Assert.True(!Offered(Src("def f():", "    return ⟦2 + 3⟧")).Contains("introduceField"));
    }

    [Fact]
    public void ExtractsASuperclassWithTheChosenMembers()
    {
        string src = Src("class Square(Shape):", "    sides = 4", "", "    def area(self):", "        return self.s ** 2", "", "    def name(self):", "        return 'square'");
        Assert.Equal(Src("class Polygon(Shape):", "    sides = 4", "", "    def area(self):", "        return self.s ** 2", "", "", "class Square(Polygon):",
                "    def name(self):", "        return 'square'"),
            Do("extractSuperclass", src.Replace("class Square", "class |Square"), "Polygon", new[] { "sides", "area" }).Main);
        // no bases, and every member taken: the class keeps a pass
        Assert.Equal(Src("class Base:", "    def f(self):", "        pass", "", "", "class C(Base):", "    pass"),
            Do("extractSuperclass", Src("class |C:", "    def f(self):", "        pass"), "Base").Main);
        Assert.True(Fails("extractSuperclass", Src("class |C(metaclass=M):", "    pass"), "B").Contains("metaclass"));
    }

    [Fact]
    public void PullsMembersUpIntoTheBaseInAnotherFile()
    {
        string lib = Src("class Shape:", "    pass");
        var (main, after) = Do("pullMembersUp", Src("from lib import Shape", "", "", "class Circle(Shape):", "    def |area(self):", "        return 3", "",
            "    def name(self):", "        return 'c'"), lib: lib);
        Assert.Equal(Src("class Shape:", "    def area(self):", "        return 3"), after);
        Assert.Equal(Src("from lib import Shape", "", "", "class Circle(Shape):", "    def name(self):", "        return 'c'"), main);
        Assert.True(Fails("pullMembersUp", Src("class C:", "    def |f(self):", "        pass")).Contains("no base class"));
    }

    [Fact]
    public void PushesMembersDownIntoEachSubclass()
    {
        string src = Src("class Shape:", "    def |area(self):", "        return 0", "", "    def name(self):", "        return '?'", "", "",
            "class Square(Shape):", "    pass", "", "", "class Circle(Shape):", "    def area(self):", "        return 3");
        Assert.Equal(Src("class Shape:", "    def name(self):", "        return '?'", "", "",
                "class Square(Shape):", "    def area(self):", "        return 0", "", "", "class Circle(Shape):", "    def area(self):", "        return 3"),
            Do("pushMembersDown", src).Main);
        Assert.True(!Offered(Src("class C:", "    def |f(self):", "        pass")).Contains("pushMembersDown"));
    }

    [Fact]
    public void EncapsulatesAFieldBehindAProperty()
    {
        string src = Src("class Account:", "    def __init__(self, balance):", "        self.|balance = balance", "", "    def show(self):", "        return self.balance");
        Assert.Equal(Src("class Account:", "    def __init__(self, balance):", "        self._balance = balance", "", "    def show(self):", "        return self._balance", "",
                "    @property", "    def balance(self):", "        return self._balance", "", "    @balance.setter", "    def balance(self, value):", "        self._balance = value"),
            Do("encapsulateField", src).Main);
        Assert.True(Fails("encapsulateField", Src("class C:", "    def f(self):", "        self.|_x = 1")).Contains("private"));
    }

    [Fact]
    public void InvertsABooleanAndEveryUse()
    {
        string src = Src("|done = False", "if done:", "    print(1)", "if not done:", "    print(2)", "x = done == 1", "done = a > b");
        Assert.Equal(Src("pending = True", "if not pending:", "    print(1)", "if pending:", "    print(2)", "x = (not pending) == 1", "done = a > b".Replace("done = a > b", "pending = not a > b")),
            Do("invertBoolean", src, "pending").Main);
        Assert.Equal(Src("ready = x is not None", "print(not ready)"), Do("invertBoolean", Src("|busy = x is None", "print(busy)"), "ready").Main);
        Assert.True(Fails("invertBoolean", Src("|n = True", "n += 1"), "m").Contains("cannot be inverted"));
    }

    [Fact]
    public void MakesAMethodTopLevelAndRewritesItsCalls()
    {
        string src = Src("class Report:", "    def __init__(self):", "        self.rows = []", "", "    def |total(self, scale):", "        return sum(self.rows) * scale", "",
            "    def show(self):", "        print(self.total(2))", "", "", "r = Report()", "print(r.total(3))");
        Assert.Equal(Src("class Report:", "    def __init__(self):", "        self.rows = []", "", "    def show(self):", "        print(total(self, 2))", "", "",
                "def total(self, scale):", "    return sum(self.rows) * scale", "", "", "r = Report()", "print(total(r, 3))"),
            Do("makeTopLevel", src).Main);
        // a static method loses its decorator and the class in its calls
        Assert.Equal(Src("class M:", "    pass", "", "", "def twice(x):", "    return 2 * x", "", "", "print(twice(4))"),
            Do("makeTopLevel", Src("class M:", "    @staticmethod", "    def |twice(x):", "        return 2 * x", "", "", "print(M.twice(4))")).Main);
    }

    [Fact]
    public void MakesANestedFunctionTopLevelWhenItUsesNothingAroundIt()
    {
        Assert.Equal(Src("def helper(x):", "    return x + 1", "", "", "def outer():", "    return helper(2)"),
            Do("makeTopLevel", Src("def outer():", "    def |helper(x):", "        return x + 1", "", "    return helper(2)")).Main);
        Assert.True(Fails("makeTopLevel", Src("def outer(n):", "    def |helper(x):", "        return x + n", "    return helper(2)")).Contains("'n'"));
    }

    [Fact]
    public void MovesAStaticMemberToAnotherClass()
    {
        string lib = Src("class Units:", "    pass");
        var (main, after) = Do("moveStaticMember", Src("class Shape:", "    |FACTOR = 2", "", "    def size(self):", "        return Shape.FACTOR * 3", "", "", "print(Shape.FACTOR)"),
            "Units", lib: lib);
        Assert.Equal(Src("class Units:", "    FACTOR = 2"), after);
        Assert.Equal(Src("from lib import Units", "", "class Shape:", "    def size(self):", "        return Units.FACTOR * 3", "", "", "print(Units.FACTOR)"), main);
        Assert.True(Fails("moveStaticMember", Src("class A:", "    def |f(self):", "        pass", "class B:", "    pass"), "B").Contains("not a static method"));
    }

    [Fact]
    public void ExtractsADelegateThatTheClassHandsCallsTo()
    {
        string src = Src("class |Order:", "    def __init__(self):", "        self.items = []", "", "    def tax(self, amount, *, rate=0.2):", "        return amount * rate");
        Assert.Equal(Src("class TaxRules:", "    def tax(self, amount, *, rate=0.2):", "        return amount * rate", "", "",
                "class Order:", "    def __init__(self):", "        self.tax_rules = TaxRules()", "        self.items = []", "",
                "    def tax(self, amount, *, rate=0.2):", "        return self.tax_rules.tax(amount, rate=rate)"),
            Do("extractDelegate", src, "TaxRules", new[] { "tax" }).Main);
        string uses = Src("class |C:", "    def f(self):", "        return self.items");
        try { Do("extractDelegate", uses, "D", new[] { "f" }); throw new AssertionException("should fail"); }
        catch (ServiceException e) { Assert.True(e.Message.Contains("self.items")); }
    }
}
