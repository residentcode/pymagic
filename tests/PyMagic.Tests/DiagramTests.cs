using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Diagrams of classes, modules and Django models (LanguageService.Diagrams.cs).</summary>
public class DiagramTests
{
    const string Root = "file:///c:/proj/";

    static LanguageService Service(params (string Path, string Text)[] files)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        foreach (var (p, t) in files) svc.Open(Root + p, t, 1);
        return svc;
    }

    static string Edges(Diagram d) => string.Join(" ", d.Edges.Select(e => $"{d.Nodes.First(n => n.Id == e.From).Label}-{e.Kind}{(e.Label != null ? ":" + e.Label : "")}->{d.Nodes.First(n => n.Id == e.To).Label}").OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public void AFilesClassesWithTheirBasesAndWhatDerivesFromThem()
    {
        var svc = Service(
            ("base.py", "class Shape:\n    def area(self):\n        return 0\n"),
            ("shapes.py", "from base import Shape\n\n\nclass Square(Shape):\n    side: int\n\n    def __init__(self, side):\n        self.side = side\n        self.name = 'sq'\n\n    def area(self):\n        return self.side ** 2\n"),
            ("more.py", "from shapes import Square\n\n\nclass Cube(Square):\n    pass\n\n\nclass Other:\n    pass\n"));
        var d = svc.Diagram("classes", Root + "shapes.py");
        Assert.Equal("Cube,Shape,Square", string.Join(",", d.Nodes.Select(n => n.Label).OrderBy(x => x)));
        Assert.Equal("Cube-extends->Square Square-extends->Shape", Edges(d));
        var square = d.Nodes.Single(n => n.Label == "Square");
        Assert.Equal("side: int,name,__init__(),area()", string.Join(",", square.Members));
        Assert.True(!square.Outside && d.Nodes.Single(n => n.Label == "Shape").Outside, "classes of other files are marked as outside");
        Assert.Equal(3, square.Line);
        Assert.Equal(4, svc.Diagram("classes", null).Nodes.Count);   // the project: Other too
    }

    [Fact]
    public void ModulesAndTheirImportsInTheProject()
    {
        var svc = Service(
            ("app/__init__.py", ""),
            ("app/models.py", "import json\n\n\nclass Item:\n    pass\n"),
            ("app/views.py", "from .models import Item\nfrom app import models\n\n\ndef show():\n    return Item()\n"),
            ("main.py", "import app.views\n"));
        var d = svc.Diagram("modules", null);
        Assert.Equal("app app.models app.views main", string.Join(" ", d.Nodes.Select(n => n.Id)));
        Assert.Equal("app.views-imports->app app.views-imports->app.models main-imports->app.views", Edges(d));
        Assert.Equal("show()", string.Join(",", d.Nodes.Single(n => n.Id == "app.views").Members));
    }

    [Fact]
    public void DjangoModelsWithTheirFieldsAndRelations()
    {
        var svc = DjangoFixture.Service().With("shop/models.py", DjangoFixture.Models).Service;
        var d = svc.Diagram("models", null);
        Assert.Equal("Author,Book,Profile,Tag", string.Join(",", d.Nodes.Select(n => n.Label).OrderBy(x => x)));
        Assert.Equal("Book-foreignKey:author->Author Book-foreignKey:editor->Author Book-manyToMany:tags->Tag Profile-oneToOne:owner->Author", Edges(d));
        Assert.True(d.Nodes.Single(n => n.Label == "Book").Members.Contains("title: CharField"));
    }
}
