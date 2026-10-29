using System.Text.Json;

namespace PyMagic.Tests;

/// <summary>What else follows a module that moves (item 143): a package's `__all__`, the imports of notebooks, and `{% load %}` of a tag library.</summary>
public class MoveModuleMoreTests
{
    static (Dictionary<string, string> After, PyMagic.Server.ModuleMovePlan Plan) Move((string, string)[] files, string from, string to) => MoveModuleTests.Move(files, from, to);

    static readonly (string, string)[] Package =
    {
        ("pkg/__init__.py", "__all__ = [\"a\", \"b\"]\n"),
        ("pkg/a.py", "A = 1\n"),
        ("pkg/b.py", "B = 2\n"),
        ("other/__init__.py", ""),
    };

    [Fact]
    public void AllFollowsASubmoduleRenamedInItsPackage()
    {
        var (after, plan) = Move(Package, "pkg/b.py", "pkg/c.py");
        Assert.Equal("__all__ = [\"a\", \"c\"]\n", after["pkg/__init__.py"]);
        Assert.Equal(0, plan.Warnings.Count);
    }

    [Fact]
    public void ASubmoduleThatLeavesItsPackageLeavesAllAndThatIsSaid()
    {
        var (after, plan) = Move(Package, "pkg/b.py", "other/b.py");
        Assert.Equal("__all__ = [\"a\"]\n", after["pkg/__init__.py"]);
        Assert.True(plan.Warnings.Any(w => w.Contains("__all__") && w.Contains("other.b")));
        // the first of several, written as a tuple over lines
        var (first, _) = Move(new[] { ("pkg/__init__.py", "__all__ = (\n    'b',\n    'a',\n)\n"), Package[1], Package[2], Package[3] }, "pkg/b.py", "other/b.py");
        Assert.Equal("__all__ = (\n    'a',\n)\n", first["pkg/__init__.py"]);
    }

    [Fact]
    public void ANameThePackageImportsItselfStaysInAll()
    {
        var (after, _) = Move(new[] { ("pkg/__init__.py", "from . import b\n\n__all__ = [\"a\", \"b\"]\n"), Package[1], Package[2], Package[3] }, "pkg/b.py", "pkg/c.py");
        // the import keeps the name b, so __all__ stays right as it is
        Assert.Equal("from . import c as b\n\n__all__ = [\"a\", \"b\"]\n", after["pkg/__init__.py"]);
    }

    /// <summary>A notebook as Jupyter writes it: one JSON string per source line, indented by one space.</summary>
    static string Notebook(params (string Kind, string[] Lines)[] cells)
    {
        string Cell((string Kind, string[] Lines) c) =>
            " {\n  \"cell_type\": \"" + c.Kind + "\",\n  \"metadata\": {},\n" + (c.Kind == "code" ? "  \"execution_count\": null,\n  \"outputs\": [],\n" : "") +
            "  \"source\": [\n" + string.Join(",\n", c.Lines.Select(l => "   " + JsonSerializer.Serialize(l, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))) + "\n  ]\n }";
        return "{\n \"cells\": [\n" + string.Join(",\n", cells.Select(Cell)) + "\n ],\n \"metadata\": {},\n \"nbformat\": 4,\n \"nbformat_minor\": 5\n}\n";
    }

    static List<(string Kind, string Source)> CellsOf(string notebook)
    {
        using var doc = JsonDocument.Parse(notebook);
        return doc.RootElement.GetProperty("cells").EnumerateArray()
            .Select(c => (c.GetProperty("cell_type").GetString()!, string.Concat(c.GetProperty("source").EnumerateArray().Select(s => s.GetString())))).ToList();
    }

    static readonly (string, string)[] WithB =
    {
        ("pkg/__init__.py", ""),
        ("pkg/b.py", "VALUE = 1\n"),
        ("other/__init__.py", ""),
    };

    [Fact]
    public void ANotebooksImportsFollowAndTheRestOfItStays()
    {
        string nb = Notebook(
            ("markdown", new[] { "We use `import pkg.b` here." }),
            ("code", new[] { "import pkg.b\n", "from pkg.b import VALUE" }),
            ("code", new[] { "%matplotlib inline\n", "print(pkg.b.VALUE, VALUE)\n", "pkg.b?" }),
            ("code", new[] { "%%bash\n", "echo import pkg.b" }));
        var (after, _) = Move(WithB.Append(("analysis.ipynb", nb)).ToArray(), "pkg/b.py", "other/b.py");
        var cells = CellsOf(after["analysis.ipynb"]);
        Assert.Equal("We use `import pkg.b` here.", cells[0].Source);
        Assert.Equal("import other.b\nfrom other.b import VALUE", cells[1].Source);
        Assert.Equal("%matplotlib inline\nprint(other.b.VALUE, VALUE)\npkg.b?", cells[2].Source);
        Assert.Equal("%%bash\necho import pkg.b", cells[3].Source);
        // only the strings of the changed lines are written again: the file keeps Jupyter's layout
        Assert.Equal(nb.Replace("\"import pkg.b\\n\"", "\"import other.b\\n\"").Replace("\"from pkg.b import VALUE\"", "\"from other.b import VALUE\"")
            .Replace("\"print(pkg.b.VALUE, VALUE)\\n\"", "\"print(other.b.VALUE, VALUE)\\n\""), after["analysis.ipynb"]);
    }

    [Fact]
    public void ANotebookWithEscapedTextAndAModuleTakenFromItsPackage()
    {
        // written with every non-ASCII character escaped (json.dump's default), and a module imported by name from its package
        string nb = "{\"cells\": [{\"cell_type\": \"code\", \"metadata\": {}, \"outputs\": [], \"execution_count\": 1, \"source\": [\"# caf\\u00e9\\n\", \"from pkg import b\\n\", \"b.VALUE\"]}], \"metadata\": {}, \"nbformat\": 4, \"nbformat_minor\": 5}";
        var (after, _) = Move(WithB.Append(("n.ipynb", nb)).ToArray(), "pkg/b.py", "other/b.py");
        Assert.Contains("\"# caf\\u00e9\\n\"", after["n.ipynb"]);
        Assert.Equal("# café\nfrom other import b\nb.VALUE", CellsOf(after["n.ipynb"])[0].Source);
    }

    [Fact]
    public void ANotebookThatDoesNotImportTheModuleIsLeftAlone()
    {
        string nb = Notebook(("code", new[] { "import os\n", "print(os.sep)" }));
        var (after, plan) = Move(WithB.Append(("n.ipynb", nb)).ToArray(), "pkg/b.py", "other/b.py");
        Assert.Equal(nb, after["n.ipynb"]);
        Assert.True(!plan.Changes.Keys.Any(k => k.EndsWith(".ipynb", StringComparison.Ordinal)));
    }

    static readonly (string, string)[] TagProject =
    {
        ("app/__init__.py", ""),
        ("app/templatetags/__init__.py", ""),
        ("app/templatetags/shop_tags.py", "from django import template\n\nregister = template.Library()\n\n\n@register.filter\ndef price(v):\n    return v\n"),
        ("app/templates/page.html", "{% load static shop_tags %}\n{% load price from shop_tags %}\n{{ 3|price }}\n"),
    };

    [Fact]
    public void TemplatesLoadARenamedTagLibraryByItsNewName()
    {
        var (after, _) = Move(TagProject, "app/templatetags/shop_tags.py", "app/templatetags/shop_extras.py");
        Assert.Equal("{% load static shop_extras %}\n{% load price from shop_extras %}\n{{ 3|price }}\n", after["app/templates/page.html"]);
    }

    [Fact]
    public void ATagLibraryMovedOutOfTemplatetagsIsSaid()
    {
        var (after, plan) = Move(TagProject, "app/templatetags/shop_tags.py", "app/shop_tags.py");
        Assert.Equal(TagProject[3].Item2, after["app/templates/page.html"]);
        Assert.True(plan.Warnings.Any(w => w.Contains("shop_tags") && w.Contains("templatetags")));
    }
}
