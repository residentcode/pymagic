using PyMagic.Core.Syntax;
using PyMagic.Server;

namespace PyMagic.Tests;

public class FStringTests
{
    const string Main = "file:///c:/proj/main.py";

    static List<string> Names(string src)
    {
        var module = Parser.Parse(src).Module;
        return AstChildren.Descendants(module).OfType<NameExpr>().Select(n => n.Name).ToList();
    }

    static List<LspDiagnostic> Diagnostics(string src)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc.Diagnostics(Main).ToList();
    }

    [Fact]
    public void NamesInReplacementFieldsAreParsed()
    {
        Assert.Equal("f,a,b", string.Join(",", Names("f = f'{a} and {b}'")));
        Assert.Equal("s,x,y", string.Join(",", Names("s = f'''{x}\n{y}'''")));
    }

    [Fact]
    public void ExpressionsInFieldsAreFullExpressions()
    {
        Assert.Equal("s,obj,items,i", string.Join(",", Names("s = f'{obj.name} {items[i]}'")).Replace("s,obj,items,i", "s,obj,items,i"));
        Assert.True(Names("s = f'{a + b * c!r}'").SequenceEqual(new[] { "s", "a", "b", "c" }));
        Assert.True(Names("s = f'{f(x, key=y)}'").SequenceEqual(new[] { "s", "f", "x", "y" }));
        Assert.True(Names("s = f'{[k for k in d]}'").Contains("d"));
        Assert.True(Names("s = f'{a if b else c}'").SequenceEqual(new[] { "s", "a", "b", "c" }));
    }

    [Fact]
    public void ConversionsFormatSpecsAndSelfDocumentation()
    {
        Assert.True(Names("s = f'{a!r}'").SequenceEqual(new[] { "s", "a" }));
        Assert.True(Names("s = f'{a:>10}'").SequenceEqual(new[] { "s", "a" }));
        Assert.True(Names("s = f'{a=}'").SequenceEqual(new[] { "s", "a" }));
        Assert.True(Names("s = f'{a = !r:>8}'").SequenceEqual(new[] { "s", "a" }));
        Assert.True(Names("s = f'{a:{width}.{prec}f}'").SequenceEqual(new[] { "s", "a", "width", "prec" }));
        Assert.True(Names("s = f'{a == b} {c != d} {e <= g}'").SequenceEqual(new[] { "s", "a", "b", "c", "d", "e", "g" }));
    }

    [Fact]
    public void LiteralTextIsNotAnExpression()
    {
        Assert.True(Names("s = f'{{a}} {{{b}}}'").SequenceEqual(new[] { "s", "b" }));
        Assert.True(Names("s = f'plain text with name'").SequenceEqual(new[] { "s" }));
        Assert.True(Names("s = f'\\N{DASH} {a}'").SequenceEqual(new[] { "s", "a" }));
        Assert.True(Names("s = rf'\\d{a}'").SequenceEqual(new[] { "s", "a" }));
        Assert.True(Names("s = 'not {a} an f-string'").SequenceEqual(new[] { "s" }));
    }

    [Fact]
    public void NestedStringsAndFStringsInFields()
    {
        Assert.True(Names("s = f'{d[\"key\"]} {x}'").SequenceEqual(new[] { "s", "d", "x" }));
        Assert.True(Names("s = f\"{', '.join(items)}\"").SequenceEqual(new[] { "s", "items" }));
        Assert.True(Names("s = f'{f\"{inner}\"}'").SequenceEqual(new[] { "s", "inner" }));
        Assert.True(Names("s = f'{a:%Y-%m-%d}'").SequenceEqual(new[] { "s", "a" }));
    }

    [Fact]
    public void ConcatenatedStringsKeepEveryField()
    {
        Assert.True(Names("s = 'a' f'{x}' 'b' f'{y}'").SequenceEqual(new[] { "s", "x", "y" }));
    }

    [Fact]
    public void AnUnparsableFieldIsLeftOpaque()
    {
        Assert.True(Names("s = f'{a +}'").SequenceEqual(new[] { "s" }));
        Assert.True(Names("s = f'{}'").SequenceEqual(new[] { "s" }));
    }

    [Fact]
    public void PositionsPointIntoTheSource()
    {
        string src = "name = 1\ns = f'hello {name}'\n";
        var module = Parser.Parse(src).Module;
        var n = AstChildren.Descendants(module).OfType<NameExpr>().Last();
        Assert.Equal("name", src[n.Start..n.End]);
    }

    // ------------------------------------------------------------ what the rest of the engine now sees

    [Fact]
    public void UnresolvedNameInAnFString()
    {
        var d = Diagnostics("print(f'{missing}')\n");
        Assert.True(d.Any(x => x.Message.Contains("Unresolved reference 'missing'")), string.Join("; ", d.Select(x => x.Message)));
    }

    [Fact]
    public void NamesUsedOnlyInAnFStringAreUsed()
    {
        Assert.True(!Diagnostics("def f():\n    x = 1\n    return f'{x}'\n").Any(d => d.Code == "unused-variable"));
        Assert.True(!Diagnostics("import os\nprint(f'{os.sep}')\n").Any(d => d.Code == "unused-import"));
    }

    [Fact]
    public void RenameReachesIntoAnFString()
    {
        var svc = new LanguageService();
        string src = "value = 1\nprint(f'{value}')\n";
        svc.Open(Main, src, 1);
        var edit = svc.Rename(Main, new Position(0, 1), "total");
        Assert.True(edit != null, "rename works");
        Assert.Equal(2, edit!.Changes[Main].Count);
    }

    [Fact]
    public void HoverInsideAnFField()
    {
        var svc = new LanguageService();
        string src = "value = 1\nprint(f'{value}')\n";
        svc.Open(Main, src, 1);
        var hover = svc.Hover(Main, new Position(1, 10));
        Assert.True(hover != null, "hover finds the name");
        Assert.Contains("int", hover!.Contents.Value);
    }
}
