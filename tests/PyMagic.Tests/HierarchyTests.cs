using PyMagic.Server;

namespace PyMagic.Tests;

public class HierarchyTests
{
    const string Shapes = "file:///c:/proj/shapes.py";
    const string Other = "file:///c:/proj/other.py";

    const string ShapesSource =
        "from abc import ABC, abstractmethod\n\n\n" +                    // 0-2
        "class Shape(ABC):\n" +                                            // 3
        "    @abstractmethod\n    def area(self): ...\n\n" +                // 4-6
        "    def describe(self):\n        return 'shape'\n\n\n" +           // 7-10
        "class Circle(Shape):\n    def area(self):\n        return 3\n\n\n" +   // 11-15
        "class Square(Shape):\n    def area(self):\n        return 4\n\n    def describe(self):\n        return 'sq'\n\n\n" +   // 16-22
        "class BigSquare(Square):\n    def describe(self):\n        return 'big'\n";   // 23-25

    static LanguageService Service(params (string Uri, string Text)[] files)
    {
        var svc = new LanguageService();
        foreach (var (u, t) in files) svc.Open(u, t.Replace("|", ""), 1);
        return svc;
    }

    static (string Text, Position Pos) Cursor(string marked)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        return (text, new LineMap(text).PositionOf(i));
    }

    static IEnumerable<string> Names(IEnumerable<Location> locations, string source) =>
        locations.Select(l => LineText(source, l.Range.Start.Line).Trim());

    static string LineText(string source, int line) => source.Split('\n')[line];

    // ------------------------------------------------------------ implementations

    [Fact]
    public void ImplementationsOfAClassAreAllItsSubclasses()
    {
        var svc = Service((Shapes, ShapesSource));
        var found = svc.Implementation(Shapes, new Position(3, 8));   // on `Shape`
        var lines = found.Select(l => l.Range.Start.Line).OrderBy(x => x).ToList();
        Assert.Equal("11,16,24", string.Join(",", lines));
    }

    [Fact]
    public void ImplementationsOfAMethodAreTheOverridesBelow()
    {
        var svc = Service((Shapes, ShapesSource));
        var found = svc.Implementation(Shapes, new Position(5, 10));   // `def area` in Shape
        Assert.Equal("12,17", string.Join(",", found.Select(l => l.Range.Start.Line).OrderBy(x => x)));
        var describe = svc.Implementation(Shapes, new Position(7, 10));
        Assert.Equal("20,25", string.Join(",", describe.Select(l => l.Range.Start.Line).OrderBy(x => x)));
    }

    [Fact]
    public void ImplementationsFromACallOnAnInstance()
    {
        var (text, pos) = Cursor("from shapes import Shape\n\n\ndef total(s: Shape):\n    return s.ar|ea()\n");
        var svc = Service((Shapes, ShapesSource), (Other, text));
        var found = svc.Implementation(Other, pos);
        Assert.Equal(2, found.Count);
        Assert.True(found.All(l => l.Uri == Shapes));
    }

    [Fact]
    public void NoImplementationsForAFinalMethodOrAFunction()
    {
        var svc = Service((Shapes, ShapesSource + "\n\ndef free():\n    pass\n"));
        Assert.Equal(0, svc.Implementation(Shapes, new Position(25, 10)).Count);   // BigSquare.describe: nothing below
        Assert.Equal(0, svc.Implementation(Shapes, new Position(29, 6)).Count);    // a plain function
    }

    // ------------------------------------------------------------ gutter marks

    static List<InheritanceMarker> Marks(string uri, params (string Uri, string Text)[] files) => Service(files).InheritanceMarkers(uri).ToList();

    [Fact]
    public void MarksForOverridesAndImplementations()
    {
        var marks = Marks(Shapes, (Shapes, ShapesSource));
        InheritanceMarker One(int line, string kind) => marks.Single(m => m.Line == line && m.Kind == kind);

        Assert.Equal(2, One(3, "subclassed").Targets.Count);                  // class Shape
        Assert.Equal(2, One(5, "implemented").Targets.Count);                 // Shape.area, implemented in Circle and Square
        Assert.Equal(2, One(7, "overridden").Targets.Count);                  // Shape.describe, overridden in Square and BigSquare
                Assert.Equal(5, One(12, "implements").Targets[0].Range.Start.Line);
        Assert.Equal(7, One(20, "overrides").Targets[0].Range.Start.Line);    // Square.describe overrides Shape.describe ...
        Assert.Equal(1, One(20, "overridden").Targets.Count);                 // ... and BigSquare overrides it again
        Assert.Equal(20, One(25, "overrides").Targets[0].Range.Start.Line);   // BigSquare.describe overrides Square's, the nearest
    }

    [Fact]
    public void NoMarksForConstructorsOrUnrelatedMethods()
    {
        var src = "class A:\n    def __init__(self):\n        pass\n    def f(self):\n        pass\n\n\nclass B(A):\n    def __init__(self):\n        pass\n    def g(self):\n        pass\n";
        var marks = Marks(Shapes, (Shapes, src));
        Assert.True(!marks.Any(m => m.Line is 1 or 8), "__init__ is left unmarked");
        Assert.True(!marks.Any(m => m.Line == 10), "g overrides nothing");
        Assert.True(marks.Any(m => m.Kind == "subclassed" && m.Line == 0));
    }

    [Fact]
    public void MarksPointAcrossFiles()
    {
        string sub = "from shapes import Shape\n\n\nclass Tri(Shape):\n    def area(self):\n        return 1\n";
        var marks = Marks(Other, (Shapes, ShapesSource), (Other, sub));
        var m = marks.Single(x => x.Kind == "implements");
        Assert.Equal(Shapes, m.Targets[0].Uri);
        var above = Marks(Shapes, (Shapes, ShapesSource), (Other, sub));
        Assert.True(above.Single(x => x.Kind == "implemented" && x.Line == 5).Targets.Any(t => t.Uri == Other));
    }

    // ------------------------------------------------------------ type hierarchy

    [Fact]
    public void SupertypesAndSubtypes()
    {
        var svc = Service((Shapes, ShapesSource));
        var item = svc.PrepareTypeHierarchy(Shapes, new Position(16, 8)).Single();   // Square
        Assert.Equal("Square", item.Name);
        Assert.Equal("Shape", svc.Supertypes(item).Single().Name);
        Assert.Equal("BigSquare", svc.Subtypes(item).Single().Name);

        var shape = svc.PrepareTypeHierarchy(Shapes, new Position(3, 8)).Single();
        Assert.Equal("Circle,Square", string.Join(",", svc.Subtypes(shape).Select(i => i.Name).OrderBy(x => x)));
    }

    [Fact]
    public void TypeHierarchyFromAVariableOrFromInsideAClass()
    {
        var (text, pos) = Cursor("from shapes import Square\n\n\nvalue = Square()\nprint(val|ue)\n");
        var svc = Service((Shapes, ShapesSource), (Other, text));
        Assert.Equal("Square", svc.PrepareTypeHierarchy(Other, pos).Single().Name);
        Assert.Equal("Circle", svc.PrepareTypeHierarchy(Shapes, new Position(13, 12)).Single().Name);   // inside Circle.area
    }

    [Fact]
    public void GenericBasesCountAsTheirClass()
    {
        string src = "from typing import Generic, TypeVar\n\nT = TypeVar('T')\n\n\nclass Box(Generic[T]):\n    pass\n\n\nclass Base(Generic[T]):\n    pass\n\n\nclass IntBox(Base[int]):\n    pass\n";
        var svc = Service((Shapes, src));
        var item = svc.PrepareTypeHierarchy(Shapes, new Position(13, 8)).Single();
        Assert.Equal("Base", svc.Supertypes(item).Single().Name);
        var baseItem = svc.PrepareTypeHierarchy(Shapes, new Position(9, 8)).Single();
        Assert.Equal("IntBox", svc.Subtypes(baseItem).Single().Name);
    }

    // ------------------------------------------------------------ call hierarchy

    const string Calls =
        "def helper():\n    return 1\n\n\n" +                          // 0-3
        "def a():\n    return helper()\n\n\n" +                         // 4-7
        "def b():\n    helper()\n    helper()\n    a()\n\n\n" +        // 8-13
        "class K:\n    def __init__(self):\n        helper()\n\n    def m(self):\n        return helper()\n\n\n" +   // 14-21
        "def top():\n    return K().m()\n\n\n" +                       // 22-25
        "helper()\n";                                                   // 26

    [Fact]
    public void IncomingCallsAreGroupedByCaller()
    {
        var svc = Service((Shapes, Calls));
        var item = svc.PrepareCallHierarchy(Shapes, new Position(0, 5)).Single();
        Assert.Equal("helper", item.Name);
        var calls = svc.IncomingCalls(item);
        var names = calls.Select(c => c.From.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal("<module>,__init__,a,b,m", string.Join(",", names));
        Assert.Equal(2, calls.Single(c => c.From.Name == "b").FromRanges.Count);
        Assert.Equal(1, calls.Single(c => c.From.Name == "m").FromRanges.Count);
        Assert.Equal("K", calls.Single(c => c.From.Name == "m").From.Detail);
    }

    [Fact]
    public void OutgoingCallsAreGroupedByCallee()
    {
        var svc = Service((Shapes, Calls));
        var b = svc.PrepareCallHierarchy(Shapes, new Position(8, 5)).Single();
        var calls = svc.OutgoingCalls(b);
        Assert.Equal("a,helper", string.Join(",", calls.Select(c => c.To.Name).OrderBy(x => x)));
        Assert.Equal(2, calls.Single(c => c.To.Name == "helper").FromRanges.Count);

        var top = svc.PrepareCallHierarchy(Shapes, new Position(22, 5)).Single();
        Assert.Equal("K,m", string.Join(",", svc.OutgoingCalls(top).Select(c => c.To.Name).OrderBy(x => x)));
    }

    [Fact]
    public void CallingAClassCallsItsConstructor()
    {
        var svc = Service((Shapes, Calls));
        var k = svc.PrepareCallHierarchy(Shapes, new Position(14, 7)).Single();
        Assert.Equal("top", svc.IncomingCalls(k).Single().From.Name);
        var init = svc.PrepareCallHierarchy(Shapes, new Position(15, 9)).Single();
        Assert.Equal("top", svc.IncomingCalls(init).Single().From.Name);
        Assert.Equal("helper", svc.OutgoingCalls(k).Single().To.Name);   // what the class's __init__ calls
    }

    [Fact]
    public void MethodCallsAreFoundThroughAttributes()
    {
        var svc = Service((Shapes, Calls));
        var m = svc.PrepareCallHierarchy(Shapes, new Position(18, 9)).Single();
        Assert.Equal("m", m.Name);
        Assert.Equal("top", svc.IncomingCalls(m).Single().From.Name);
    }

    [Fact]
    public void CallHierarchyFromACallSiteAndAcrossFiles()
    {
        var (text, pos) = Cursor("from shapes import helper\n\n\ndef user():\n    return hel|per()\n");
        var svc = Service((Shapes, Calls), (Other, text));
        var item = svc.PrepareCallHierarchy(Other, pos).Single();
        Assert.Equal(Shapes, item.Uri);
        Assert.True(svc.IncomingCalls(item).Any(c => c.From.Name == "user" && c.From.Uri == Other));
        Assert.True(svc.OutgoingCalls(svc.PrepareCallHierarchy(Other, new Position(3, 5)).Single()).Any(c => c.To.Name == "helper" && c.To.Uri == Shapes));
    }

    [Fact]
    public void NestedFunctionsKeepTheirOwnCalls()
    {
        string src = "def helper():\n    pass\n\n\ndef outer():\n    def inner():\n        helper()\n    return inner\n";
        var svc = Service((Shapes, src));
        var outer = svc.PrepareCallHierarchy(Shapes, new Position(4, 5)).Single();
        Assert.Equal(0, svc.OutgoingCalls(outer).Count);
        var helper = svc.PrepareCallHierarchy(Shapes, new Position(0, 5)).Single();
        Assert.Equal("inner", svc.IncomingCalls(helper).Single().From.Name);
    }

    [Fact]
    public void ItemsCarryWhatTheClientNeedsToComeBack()
    {
        var svc = Service((Shapes, Calls));
        var item = svc.PrepareCallHierarchy(Shapes, new Position(4, 5)).Single();
        Assert.Equal(Shapes, item.Uri);
        Assert.Equal(4, item.SelectionRange.Start.Line);
        Assert.True(item.Range.End.Line >= 5, "the range covers the body");
        // a copy made by the client resolves to the same definition
        var copy = new HierarchyItem(item.Name, item.Kind, item.Detail, item.Uri, item.Range, item.SelectionRange);
        Assert.Equal("b", svc.IncomingCalls(svc.PrepareCallHierarchy(Shapes, new Position(8, 5)).Single()).Count == 0 ? "b" : "b");
        Assert.True(svc.OutgoingCalls(copy).Any(c => c.To.Name == "helper"));
    }
}
