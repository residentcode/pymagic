using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Methods that override others: rename, change signature and the call hierarchy follow them; library classes get marks and hierarchy.</summary>
public class OverrideTests
{
    const string Shapes = "file:///c:/proj/shapes.py";
    const string Other = "file:///c:/proj/other.py";

    const string ShapesSource =
        "class Shape:\n    def area(self):\n        return 0\n\n\n" +                 // 0-4
        "class Circle(Shape):\n    def area(self):\n        return 3\n\n\n" +          // 5-9
        "class Big(Circle):\n    def area(self):\n        return 9\n\n\n" +            // 10-14
        "class Plain(Shape):\n    pass\n\n\n" +                                        // 15-18
        "class Unrelated:\n    def area(self):\n        return 1\n\n\n" +              // 19-23
        "def total(s: Shape):\n    return s.area()\n\n\n" +                            // 24-27
        "def round_one(c: Circle):\n    return c.area()\n";                            // 28-29

    static LanguageService Service(params (string Uri, string Text)[] files)
    {
        var svc = new LanguageService();
        foreach (var (u, t) in files) svc.Open(u, t, 1);
        return svc;
    }

    /// <summary>Applies a workspace edit to the texts it touches.</summary>
    static Dictionary<string, string> Apply(Dictionary<string, List<TextEdit>> changes, params (string Uri, string Text)[] files)
    {
        var texts = files.ToDictionary(f => f.Uri, f => f.Text);
        foreach (var (uri, edits) in changes)
        {
            var lm = new LineMap(texts[uri]);
            string t = texts[uri];
            foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
                t = t[..lm.OffsetOf(e.Range.Start)] + e.NewText + t[lm.OffsetOf(e.Range.End)..];
            texts[uri] = t;
        }
        return texts;
    }

    static string Line(string text, int line) => text.Split('\n')[line];

    static void Throws(Action a, string messagePart)
    {
        try { a(); }
        catch (ServiceException e) { Assert.Contains(messagePart, e.Message); return; }
        throw new AssertionException("expected a ServiceException containing: " + messagePart);
    }

    /// <summary>A folder of library modules (as site-packages would be) for the service to import from.</summary>
    static string LibraryFolder(params (string Name, string Text)[] modules)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_overrides_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in modules) File.WriteAllText(Path.Combine(dir, name + ".py"), text);
        return dir;
    }

    const string WorkersLibrary = "class Thread:\n    def run(self):\n        pass\n\n    def start(self):\n        self.run()\n";

    static LanguageService WithLibrary(string dir, params (string Uri, string Text)[] files)
    {
        var svc = new LanguageService();
        svc.LibraryRoots.Add(dir);
        foreach (var (u, t) in files) svc.Open(u, t, 1);
        return svc;
    }

    // ------------------------------------------------------------ rename

    [Fact]
    public void RenamingAMethodRenamesTheMethodsThatOverrideItAndTheirUses()
    {
        var svc = Service((Shapes, ShapesSource));
        var text = Apply(svc.Rename(Shapes, new Position(1, 9), "surface").Changes, (Shapes, ShapesSource))[Shapes];
        Assert.Equal("    def surface(self):", Line(text, 1));
        Assert.Equal("    def surface(self):", Line(text, 6));    // Circle
        Assert.Equal("    def surface(self):", Line(text, 11));   // Big, two levels down
        Assert.Equal("    def area(self):", Line(text, 20));      // an unrelated class keeps its own
        Assert.Equal("    return s.surface()", Line(text, 25));
        Assert.Equal("    return c.surface()", Line(text, 29));
    }

    [Fact]
    public void RenamingAnOverrideAsksWhetherTheBaseMethodGoesWithIt()
    {
        var svc = Service((Shapes, ShapesSource));
        var prepared = svc.PrepareRename(Shapes, new Position(6, 9))!;   // Circle.area
        Assert.Equal("Circle", prepared.MethodClass);
        Assert.Equal("Shape", prepared.BaseClass);
        Assert.Equal(null, prepared.LibraryBase);
        Assert.Equal(null, svc.PrepareRename(Shapes, new Position(1, 9))!.BaseClass);   // Shape.area overrides nothing

        // this one only: Circle.area and what overrides it, not Shape's
        var only = Apply(svc.Rename(Shapes, new Position(6, 9), "surface").Changes, (Shapes, ShapesSource))[Shapes];
        Assert.Equal("    def area(self):", Line(only, 1));
        Assert.Equal("    def surface(self):", Line(only, 6));
        Assert.Equal("    def surface(self):", Line(only, 11));
        Assert.Equal("    return s.area()", Line(only, 25));
        Assert.Equal("    return c.surface()", Line(only, 29));

        // the base method with its family
        var all = Apply(svc.Rename(Shapes, new Position(6, 9), "surface", renameBase: true).Changes, (Shapes, ShapesSource))[Shapes];
        Assert.Equal("    def surface(self):", Line(all, 1));
        Assert.Equal("    def surface(self):", Line(all, 11));
        Assert.Equal("    return s.surface()", Line(all, 25));
        Assert.Equal("    def area(self):", Line(all, 20));
    }

    [Fact]
    public void RenamingFromAUseFollowsTheOverridesAcrossFiles()
    {
        string user = "from shapes import Shape\n\n\nclass Square(Shape):\n    def area(self):\n        return 4\n\n\ndef show(s: Shape):\n    print(s.area())\n";
        var svc = Service((Shapes, ShapesSource), (Other, user));
        var texts = Apply(svc.Rename(Other, new Position(9, 14), "surface").Changes, (Shapes, ShapesSource), (Other, user));   // on `s.area()`
        Assert.Equal("    def surface(self):", Line(texts[Shapes], 1));
        Assert.Equal("    def surface(self):", Line(texts[Shapes], 6));
        Assert.Equal("    def surface(self):", Line(texts[Other], 4));
        Assert.Equal("    print(s.surface())", Line(texts[Other], 9));
    }

    [Fact]
    public void RenamingAMethodChecksForClashesInEveryClassItTouches()
    {
        string src = ShapesSource.Replace("class Big(Circle):\n", "class Big(Circle):\n    def surface(self):\n        return 0\n\n");
        var svc = Service((Shapes, src));
        Throws(() => svc.Rename(Shapes, new Position(1, 9), "surface"), "already defined in class Big");
        Throws(() => svc.Rename(Shapes, new Position(6, 9), "surface", renameBase: true), "already defined in class Big");
    }

    [Fact]
    public void RenamingAnOverrideOfALibraryMethodWarnsAndLeavesTheLibraryAlone()
    {
        string dir = LibraryFolder(("workers", WorkersLibrary));
        try
        {
            string src = "from workers import Thread\n\n\nclass Worker(Thread):\n    def run(self):\n        pass\n\n\ndef go(t: Thread, w: Worker):\n    t.run()\n    w.run()\n";
            var svc = WithLibrary(dir, (Other, src));
            var prepared = svc.PrepareRename(Other, new Position(4, 9))!;
            Assert.Equal("Thread (workers)", prepared.LibraryBase);
            Assert.Equal(null, prepared.BaseClass);
            var edit = svc.Rename(Other, new Position(4, 9), "work", renameBase: true);   // there is no project base: only this one
            Assert.Equal(1, edit.Changes.Count);
            var text = Apply(edit.Changes, (Other, src))[Other];
            Assert.Equal("    def work(self):", Line(text, 4));
            Assert.Equal("    t.run()", Line(text, 9));
            Assert.Equal("    w.work()", Line(text, 10));
            Throws(() => svc.Rename(Other, new Position(9, 7), "work"), "library");   // the library's own method
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------ call hierarchy

    [Fact]
    public void CallersOfAnOverrideIncludeTheCallsOfTheMethodItOverrides()
    {
        var svc = Service((Shapes, ShapesSource));
        var circle = svc.PrepareCallHierarchy(Shapes, new Position(6, 9)).Single();
        var calls = svc.IncomingCalls(circle);
        Assert.Equal("round_one,total", string.Join(",", calls.Select(c => c.From.Name).OrderBy(x => x)));
        Assert.Equal("shapes, through Shape.area", calls.Single(c => c.From.Name == "total").From.Detail);
        Assert.Equal("shapes", calls.Single(c => c.From.Name == "round_one").From.Detail);   // calls Circle.area itself
        Assert.Equal(25, calls.Single(c => c.From.Name == "total").FromRanges.Single().Start.Line);

        // two levels down: both bases' calls; the base's own callers are only its own
        var big = svc.PrepareCallHierarchy(Shapes, new Position(11, 9)).Single();
        Assert.Equal("round_one,total", string.Join(",", svc.IncomingCalls(big).Select(c => c.From.Name).OrderBy(x => x)));
        Assert.True(svc.IncomingCalls(big).All(c => c.From.Detail!.Contains("through")));
        var shape = svc.PrepareCallHierarchy(Shapes, new Position(1, 9)).Single();
        Assert.Equal("total", svc.IncomingCalls(shape).Single().From.Name);
    }

    [Fact]
    public void ASuperCallIsNotACallerOfTheOverride()
    {
        string src = "class A:\n    def run(self):\n        pass\n\n\nclass B(A):\n    def run(self):\n        super().run()\n\n\ndef go(a: A):\n    a.run()\n";
        var svc = Service((Shapes, src));
        var b = svc.PrepareCallHierarchy(Shapes, new Position(6, 9)).Single();
        Assert.Equal("go", svc.IncomingCalls(b).Single().From.Name);   // B.run's super().run() runs A's, not its own
        var a = svc.PrepareCallHierarchy(Shapes, new Position(1, 9)).Single();
        Assert.Equal("go,run", string.Join(",", svc.IncomingCalls(a).Select(c => c.From.Name).OrderBy(x => x)));
    }

    // ------------------------------------------------------------ library classes

    const string WorkerSource = "from workers import Thread\n\n\nclass Worker(Thread):\n    def __init__(self):\n        pass\n\n    def run(self):\n        pass\n";

    [Fact]
    public void AMethodThatOverridesALibraryMethodIsMarked()
    {
        string dir = LibraryFolder(("workers", WorkersLibrary));
        try
        {
            var svc = WithLibrary(dir, (Other, WorkerSource));
            var marks = svc.InheritanceMarkers(Other);
            var run = marks.Single(m => m.Line == 7);
            Assert.Equal("overrides", run.Kind);
            Assert.Equal("Overrides method in Thread (workers)", run.Label);
            Assert.True(run.Targets.Single().Uri.EndsWith("/workers.py"), run.Targets.Single().Uri);   // the library's file, on the method's name
            Assert.Equal(1, run.Targets.Single().Range.Start.Line);
            Assert.True(!marks.Any(m => m.Line == 4), "the constructor stays unmarked");

            // changing its signature says the library's method stays as it is
            var change = svc.ChangeSignature(Other, new Position(7, 9), null, new[] { P(null, "fast", "False") });
            Assert.True(change.Warnings.Any(w => w.Contains("overrides a method of Thread (workers)")), string.Join(" | ", change.Warnings));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MethodsThatOverrideObjectsAreMarkedAsInPyCharm()
    {
        string src = "class Point:\n    def __init__(self):\n        self.x = 0\n\n    def __eq__(self, other):\n        return True\n\n    def __str__(self):\n        return 'p'\n\n    def __post_init__(self):\n        pass\n\n    def move(self):\n        pass\n";
        var marks = Service((Shapes, src)).InheritanceMarkers(Shapes);
        Assert.Equal("Overrides method in object (builtins)", marks.Single(m => m.Line == 4).Label);
        Assert.Equal("Overrides method in object (builtins)", marks.Single(m => m.Line == 7).Label);
        Assert.True(!marks.Any(m => m.Line is 1 or 10 or 13), string.Join(" | ", marks.Select(m => m.Line + " " + m.Label)));
    }

    [Fact]
    public void LibraryClassesAreSupertypesLabelledWithTheirModule()
    {
        string dir = LibraryFolder(("workers", WorkersLibrary));
        try
        {
            var svc = WithLibrary(dir, (Other, WorkerSource));
            var worker = svc.PrepareTypeHierarchy(Other, new Position(3, 8)).Single();
            var thread = svc.Supertypes(worker).Single();
            Assert.Equal("Thread", thread.Name);
            Assert.Equal("workers", thread.Detail);
            Assert.True(thread.Uri.EndsWith("/workers.py"), thread.Uri);
            Assert.Equal("Worker", svc.Subtypes(thread).Single().Name);   // the item comes back to the library's class
            Assert.Equal(0, svc.Supertypes(thread).Count);                   // object is left out, as for the project's classes
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ThreadRunOfTheStandardLibraryWithTypeshed()
    {
        if (PyMagic.Core.Types.Workspace.DefaultTypeshedPath == null) return;   // the bundled stubs have no threading
        string src = "import threading\n\n\nclass Worker(threading.Thread):\n    def run(self) -> None:\n        pass\n";
        var svc = Service((Other, src));
        Assert.Equal("Overrides method in Thread (threading)", svc.InheritanceMarkers(Other).Single(m => m.Line == 4).Label);
        var thread = svc.Supertypes(svc.PrepareTypeHierarchy(Other, new Position(3, 8)).Single()).Single();
        Assert.Equal("threading", thread.Detail);
        Assert.True(thread.Uri.EndsWith("/threading.pyi"), thread.Uri);
    }

    [Fact]
    public void TheMarkOfATypeshedMethodGoesToItsLineInTheStub()
    {
        if (PyMagic.Core.Types.Workspace.DefaultTypeshedPath == null) return;   // the bundled stubs have no file
        string src = "import threading\n\n\nclass Worker(threading.Thread):\n    def run(self) -> None:\n        pass\n";
        var target = Service((Other, src)).InheritanceMarkers(Other).Single(m => m.Line == 4).Targets.Single();
        string path = Uri.UnescapeDataString(new Uri(target.Uri).LocalPath);
        Assert.True(File.Exists(path), path);
        string line = File.ReadAllLines(path)[target.Range.Start.Line];
        Assert.Contains("def run", line);
        Assert.Equal(line.IndexOf("run", StringComparison.Ordinal), target.Range.Start.Character);
    }

    // ------------------------------------------------------------ multiple inheritance

    const string Mixins =
        "class Base:\n    def save(self):\n        pass\n\n\n" +                         // 0-4
        "class Logged:\n    def save(self):\n        pass\n\n\n" +                       // 5-9
        "class Model(Base, Logged):\n    def save(self):\n        pass\n\n\n" +          // 10-14
        "class Child(Base):\n    def save(self):\n        pass\n\n\n" +                  // 15-19
        "class Deep(Child, Logged):\n    def save(self):\n        pass\n";               // 20-22

    [Fact]
    public void WithMultipleInheritanceEveryBaseThatDefinesTheMethodIsMarked()
    {
        var marks = Service((Shapes, Mixins)).InheritanceMarkers(Shapes);
        var model = marks.Where(m => m.Line == 11).ToList();
        Assert.Equal("Overrides Base.save|Overrides Logged.save", string.Join("|", model.Select(m => m.Label)));
        Assert.Equal("1|6", string.Join("|", model.Select(m => m.Targets.Single().Range.Start.Line)));
        // Base is above Child, so Deep overrides Child's and Logged's, not Base's as well
        Assert.Equal("Overrides Child.save|Overrides Logged.save", string.Join("|", marks.Where(m => m.Line == 21).Select(m => m.Label)));
        // and both bases are overridden by Model (and Deep)
        Assert.Equal("Overridden in 2 classes", marks.Single(m => m.Line == 6 && m.Kind == "overridden").Label);
    }

    [Fact]
    public void AMixinThatDefinesItOverObjectsCountsOnce()
    {
        // object's __eq__ is above both, so it is not a third mark
        string src = "class A:\n    def __eq__(self, o):\n        return True\n\n\nclass B:\n    def __eq__(self, o):\n        return True\n\n\n" +
                     "class C(A, B):\n    def __eq__(self, o):\n        return True\n";
        var marks = Service((Shapes, src)).InheritanceMarkers(Shapes);
        Assert.Equal("Overrides A.__eq__|Overrides B.__eq__", string.Join("|", marks.Where(m => m.Line == 11).Select(m => m.Label)));
        Assert.Equal("Overrides method in object (builtins)", marks.Single(m => m.Line == 1 && m.Kind == "overrides").Label);
    }

    // ------------------------------------------------------------ change signature

    const string Scaled =
        "class Shape:\n    def area(self, scale, unit='cm'):\n        return scale\n\n\n" +                          // 0-4
        "class Circle(Shape):\n    def area(self, factor, unit='cm'):\n        return factor * 3\n\n\n" +        // 5-9
        "class Big(Circle):\n    def area(self, scale, unit='m'):\n        return scale * 9\n\n\n" +             // 10-14
        "def use(s: Shape, c: Circle):\n    s.area(2)\n    c.area(factor=2)\n    Big().area(1, 'm')\n";        // 15-18

    static SignatureParam P(int? origin, string name, string? def = null, string? callValue = null) => new(origin, name, "normal", null, def, callValue);

    static (string Text, List<string> Warnings) ChangeAt(string src, Position pos, params SignatureParam[] ps)
    {
        var svc = Service((Shapes, src));
        var r = svc.ChangeSignature(Shapes, pos, null, ps);
        return (Apply(r.Changes, (Shapes, src))[Shapes], r.Warnings);
    }

    [Fact]
    public void ChangeSignatureGoesOnToTheOverridingMethodsAndTheirCalls()
    {
        // scale becomes size, unit goes, digits is new (existing calls pass 2)
        var (text, warnings) = ChangeAt(Scaled, new Position(1, 9), P(0, "size"), P(null, "digits", callValue: "2"));
        Assert.Equal("    def area(self, size, digits):", Line(text, 1));
        Assert.Equal("        return size", Line(text, 2));
        Assert.Equal("    def area(self, size, digits):", Line(text, 6));    // the override's own name for the renamed parameter follows the change
        Assert.Equal("        return size * 3", Line(text, 7));
        Assert.Equal("    def area(self, size, digits):", Line(text, 11));
        Assert.Equal("    s.area(2, 2)", Line(text, 16));
        Assert.Equal("    c.area(size=2, digits=2)", Line(text, 17));
        Assert.Equal("    Big().area(1, 2)", Line(text, 18));
        Assert.True(!warnings.Any(w => w.Contains("define a method with this name")), string.Join(" | ", warnings));
    }

    [Fact]
    public void AnOverrideKeepsWhatTheChangeLeavesAlone()
    {
        // only a new parameter at the end: each override keeps its own names and defaults
        var (text, _) = ChangeAt(Scaled, new Position(1, 9), P(0, "scale"), P(1, "unit", "'cm'"), P(null, "digits", "2"));
        Assert.Equal("    def area(self, scale, unit='cm', digits=2):", Line(text, 1));
        Assert.Equal("    def area(self, factor, unit='cm', digits=2):", Line(text, 6));
        Assert.Equal("    def area(self, scale, unit='m', digits=2):", Line(text, 11));
        Assert.Equal("    c.area(factor=2)", Line(text, 17));
    }

    [Fact]
    public void ARemovedParameterThatAnOverrideStillUsesIsRefused()
    {
        string src = Scaled.Replace("        return factor * 3", "        return unit");
        var svc = Service((Shapes, src));
        Throws(() => svc.ChangeSignature(Shapes, new Position(1, 9), null, new[] { P(0, "scale") }), "body of Circle.area");
    }

    [Fact]
    public void ChangeSignatureOffersTheBaseMethodOfAnOverride()
    {
        var svc = Service((Shapes, Scaled));
        var info = svc.GetSignature(Shapes, new Position(11, 9));   // Big.area
        Assert.Equal("Shape", info.Base!.Class);
        Assert.Equal(1, info.Base.Position.Line);
        Assert.Equal(0, info.Overriding);
        var top = svc.GetSignature(Shapes, info.Base.Position);
        Assert.Equal(null, top.Base);
        Assert.Equal(2, top.Overriding);

        // this one only: the base stays, and the warning says so
        var (text, warnings) = ChangeAt(Scaled, new Position(6, 9), P(0, "factor"));
        Assert.Equal("    def area(self, scale, unit='cm'):", Line(text, 1));
        Assert.Equal("    def area(self, factor):", Line(text, 6));
        Assert.Equal("    def area(self, scale):", Line(text, 11));
        Assert.True(warnings.Any(w => w.Contains("1 other class")), string.Join(" | ", warnings));
    }

    [Fact]
    public void AnOverrideThatPassesARenamedParameterToSuperGetsOneEditThere()
    {
        // the body's use of force and the argument list of the super() call it sits in are the same text: one edit, not two
        string src = "class Base:\n    def save(self, force):\n        return force\n\n\n" +
                     "class Child(Base):\n    def save(self, force):\n        super().save(force)\n        return super().save(force=force)\n";
        var (text, _) = ChangeAt(src, new Position(1, 9), P(0, "commit"));
        Assert.Equal("    def save(self, commit):", Line(text, 6));
        Assert.Equal("        super().save(commit)", Line(text, 7));
        Assert.Equal("        return super().save(commit=commit)", Line(text, 8));
    }

    [Fact]
    public void ARecursiveCallOfARenamedParameterGetsOneEdit()
    {
        string src = "def count(n):\n    return 0 if n == 0 else count(n - 1)\n";
        var svc = Service((Shapes, src));
        var r = svc.ChangeSignature(Shapes, new Position(0, 5), null, new[] { P(0, "k") });
        Assert.Equal("def count(k):\n    return 0 if k == 0 else count(k - 1)\n", Apply(r.Changes, (Shapes, src))[Shapes]);
        var edits = r.Changes[Shapes];
        for (int i = 1; i < edits.Count; i++)
            Assert.True(new LineMap(src).OffsetOf(edits[i].Range.Start) >= new LineMap(src).OffsetOf(edits[i - 1].Range.End), "overlapping edits");

        // a call inside another's arguments: both are rewritten, in one edit
        string nested = "def count(n):\n    return 0 if n == 0 else count(count(n - 1))\n";
        var r2 = Service((Shapes, nested)).ChangeSignature(Shapes, new Position(0, 5), null, new[] { P(0, "n"), P(null, "step", callValue: "1") });
        Assert.Equal("def count(n, step):\n    return 0 if n == 0 else count(count(n - 1, 1), 1)\n", Apply(r2.Changes, (Shapes, nested))[Shapes]);
    }

    [Fact]
    public void AnOverrideWithOtherParametersIsLeftAsItIsWithAWarning()
    {
        string src = Scaled.Replace("def area(self, factor, unit='cm'):", "def area(self, *args):").Replace("return factor * 3", "return 3");
        var (text, warnings) = ChangeAt(src, new Position(1, 9), P(0, "scale"));
        Assert.Equal("    def area(self, *args):", Line(text, 6));
        Assert.Equal("    def area(self, scale):", Line(text, 11));   // Big still lines up with the base
        Assert.True(warnings.Any(w => w.Contains("Circle.area overrides it with other parameters")), string.Join(" | ", warnings));
    }
}
