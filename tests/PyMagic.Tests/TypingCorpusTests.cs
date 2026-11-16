using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>False reports found by checking well-known working libraries (flask, click, httpx, bs4, jinja2, rich, attrs) as project files.</summary>
public class TypingCorpusTests
{
    [Fact]
    public void AClassWithoutInitTakesStarArgumentsThatMayBeEmpty()
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", "class View:\n    pass\n\n\ndef make(*args, **kwargs):\n    return View(*args, **kwargs)\n\n\ndef bad():\n    return View(1)\n");
        var found = ws.Evaluator.Inspect(m).Where(d => d.Message.EndsWith("takes no arguments")).ToList();
        Assert.Equal(1, found.Count);
        Assert.Equal(m.Source.IndexOf("View(1)") + 5, found[0].Start);
    }

    [Fact]
    public void AStandInClassForAMissingDependencyIsNotTheTypeWhenTheImportIsFound()
    {
        var ws = new Workspace();
        ws.AddModule("pkg.real", "class Pool:\n    def close(self) -> None: ...\n\n\nclass Proxy(Pool):\n    pass\n");
        ws.AddModule("pkg", "try:\n    from pkg.real import Proxy\nexcept ImportError:\n    class Proxy:\n        def __init__(self, *args, **kwargs):\n            raise RuntimeError('install socksio')\n");
        var m = ws.AddModule("main", "import pkg\n\np = pkg.Proxy()\np.close()\nV = p\n");
        Assert.Equal("Proxy", ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().Last().Value).Display());
        Assert.Equal(0, ws.Evaluator.Inspect(m).Count(d => d.Message.StartsWith("Cannot find reference")));
        // a missing import: the stand-in is all there is
        var ws2 = new Workspace();
        ws2.AddModule("pkg", "try:\n    from pkg.absent import Proxy\nexcept ImportError:\n    class Proxy:\n        def close(self) -> None: ...\n");
        var m2 = ws2.AddModule("main", "import pkg\n\nV = pkg.Proxy()\n");
        Assert.Equal("Proxy", ws2.Evaluator.TypeOf(m2, AstChildren.Descendants(m2.Ast).OfType<AssignStmt>().Last().Value).Display());
    }

    [Fact]
    public void AModuleAFunctionAndAPlainTypeGoWhereTheyFit()
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open("file:///c:/proj/helpers.py", "x = 1\n", 1);
        svc.Open("file:///c:/proj/main.py", "import types\nfrom typing import Hashable\n\nimport helpers\n\n\ndef takes_module(m: types.ModuleType) -> None: ...\ndef takes_key(k: Hashable) -> None: ...\ndef takes_class(c: type[Exception]) -> None: ...\ndef takes_str(s: str) -> None: ...\ndef f() -> None: ...\n\n\ndef run(t: type) -> None:\n    takes_module(helpers)\n    takes_key(f)\n    takes_class(t)\n    for cls in ValueError.__mro__:\n        takes_class(cls)\n    takes_str(1)\n", 1);
        var found = svc.Diagnostics("file:///c:/proj/main.py").Where(d => d.Code == "argument-type").Select(d => d.Message).ToList();
        Assert.Equal("Expected type 'str', got 'int' instead", string.Join(" | ", found));
    }

    [Fact]
    public void AClassmethodSubclassStaysACallableClassMethod()
    {
        // Django's classonlymethod (View.as_view): a classmethod whose __get__ refuses instances
        var ws = new Workspace();
        var m = ws.AddModule("main", "class classonlymethod(classmethod):\n    def __get__(self, instance, cls=None):\n        if instance is not None:\n            raise AttributeError('class only')\n        return super().__get__(instance, cls)\n\n\nclass View:\n    @classonlymethod\n    def as_view(cls, **initkwargs):\n        def view(request):\n            return cls()\n        return view\n\n\nclass ListView(View):\n    pass\n\n\nV = ListView.as_view(template_name='x.html')\n");
        Assert.Equal("", string.Join(" | ", ws.Evaluator.Inspect(m).Select(d => d.Message)));
        Assert.Equal("function view", ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().Last().Value).Display());
    }

    [Fact]
    public void TypeIgnoreOnADefSilencesItsOverrideAndMissingReturn()
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        const string uri = "file:///c:/proj/app.py";
        svc.Open(uri, "import sys\nfrom types import TracebackType\n\n\nclass B:\n    def run(self, a, b):\n        pass\n\n\nclass C(B):\n    def run(self, a=None):  # type: ignore\n        pass\n\n\nclass D(B):\n    def run(self, a=None):\n        pass\n\n\ndef tb(code) -> TracebackType:  # type: ignore\n    try:\n        exec(code)\n    except BaseException:\n        return sys.exc_info()[2]\n", 1);
        var d = svc.Diagnostics(uri);
        Assert.Equal("15", string.Join(",", d.Where(x => x.Code == "method-override").Select(x => x.Range.Start.Line)));
        Assert.Equal(0, d.Count(x => x.Code == "missing-return"));
    }
}
