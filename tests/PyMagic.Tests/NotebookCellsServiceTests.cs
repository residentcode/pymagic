using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The notebook view sends all code cells as one Python file under .pymagic/notebooks (see notebook-model.js); this is what the
/// language service makes of such a file: names and types flow from cell to cell, and findings carry the right lines.
/// </summary>
public class NotebookCellsServiceTests
{
    // cell 1: lines 0-9, cell 2: lines 10-12 (its `%matplotlib inline` is `pass`), cell 3: lines 13-15
    const string Cells =
        "import math\nfrom dataclasses import dataclass\n\n@dataclass\nclass Point:\n    x: float\n    y: float\n\n    def norm(self) -> float:\n        return math.hypot(self.x, self.y)\n" +
        "pass\np = Point(3, 4)\np.norm()\n" +
        "print(undefined_name)\np.no_such_attribute\np.";

    static (LanguageService Svc, string Uri) Open(string text)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_nbcells_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var svc = new LanguageService();
        svc.SourceRoots.Add(root);
        string uri = LanguageService.UriOfPath(Path.Combine(root, ".pymagic", "notebooks", "cells_ipynb.py"));
        svc.Open(uri, text, 1);
        return (svc, uri);
    }

    [Fact]
    public void ANameFromAnEarlierCellIsKnownInALaterOne()
    {
        var (svc, uri) = Open(Cells);
        var found = svc.Diagnostics(uri).Where(d => d.Code is "unresolved-reference" or "missing-attribute").ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal(13, found[0].Range.Start.Line);                              // print(undefined_name), first line of cell 3
        Assert.True(found.Any(d => d.Message.Contains("'no_such_attribute' in 'Point'") && d.Range.Start.Line == 14));
        var unresolved = found.Where(d => d.Code == "unresolved-reference").Select(d => d.Message).ToList();
        Assert.Equal(1, unresolved.Count);                                        // only undefined_name: p, Point and math come from earlier cells
        Assert.Contains("undefined_name", unresolved[0]);
    }

    [Fact]
    public void MembersOfAClassDefinedInAnotherCellAreOffered()
    {
        var (svc, uri) = Open(Cells);
        var labels = svc.Completion(uri, new Position(15, 2)).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("norm") && labels.Contains("x") && labels.Contains("y"), string.Join(",", labels.Take(10)));
    }

    [Fact]
    public void HoverShowsTheTypeOfANameFromAnotherCell()
    {
        var (svc, uri) = Open(Cells);
        string text = svc.Hover(uri, new Position(14, 0))?.Contents.Value ?? "";
        Assert.Contains("Point", text);
    }

    [Fact]
    public void DefinitionPointsIntoTheEarlierCell()
    {
        var (svc, uri) = Open(Cells);
        var where = svc.Definition(uri, new Position(11, 4));      // `Point` in `p = Point(3, 4)`
        Assert.Equal(1, where.Count);
        Assert.Equal(uri, where[0].Uri);
        Assert.Equal(4, where[0].Range.Start.Line);                // class Point, in cell 1
    }

    [Fact]
    public void AMagicLineMaskedAsPassIsNotASyntaxError()
    {
        var (svc, uri) = Open("pass\nx = 1\nx\n");
        Assert.Equal(0, svc.Diagnostics(uri).Count(d => d.Code == "syntax-error"));
    }
}
