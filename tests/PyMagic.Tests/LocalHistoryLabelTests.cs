using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Labels of Local History (item 237): moments of the whole project, and what the project held at each.</summary>
public class LocalHistoryLabelTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "pm_histl_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ALabelKeepsEveryFileAsItIsAndTheStateAtItIsKnownLater()
    {
        var h = new LocalHistory(Root());
        h.Record("a.py", "a1\n");
        var label = h.PutLabel("before the refactoring", false, new[] { ("a.py", "a2\n"), ("b.py", "b1\n") });
        Assert.Equal("before the refactoring", label.Name);
        Assert.True(!label.Auto);
        h.Record("a.py", "a3\n");
        h.Record("c.py", "c1\n");                                    // made after the label
        var state = h.StateAt(label.Id).ToDictionary(x => x.Path, x => x.Text);
        Assert.Equal("a2\n", state["a.py"]);
        Assert.Equal("b1\n", state["b.py"]);
        Assert.True(state["c.py"] == null, "a file with no version by the label");
        Assert.Equal(1, h.Labels().Count);
        Assert.Equal("a2\n", h.Before("a.py", h.List("a.py")[0].Id));
        Assert.True(h.Before("b.py", h.List("b.py")[0].Id) == null, "the first version has none before it");
    }

    [Fact]
    public void ALabelKeepsTheUnsavedTextOfOpenFiles()
    {
        var h = new LocalHistory(Root());
        var disk = new[] { ("a.py", "a on disk\n"), ("b.py", "b on disk\n") };
        var unsaved = new Dictionary<string, string> { ["A.py"] = "a typed, not saved\n", ["new.py"] = "never saved\n" };
        var files = LocalHistory.WithUnsaved(disk, unsaved);
        Assert.Equal("a.py,b.py,new.py", string.Join(",", files.Select(f => f.Rel)));
        var label = h.PutLabel("with my edits", false, files);
        var state = h.StateAt(label.Id).ToDictionary(x => x.Path, x => x.Text);
        Assert.Equal("a typed, not saved\n", state["a.py"]);
        Assert.Equal("b on disk\n", state["b.py"]);
        Assert.Equal("never saved\n", state["new.py"]);
        Assert.Equal(2, LocalHistory.WithUnsaved(disk, new Dictionary<string, string>()).Count);
    }

    [Fact]
    public void LabelsAreOrderedRemovedAndLimited()
    {
        var h = new LocalHistory(Root());
        var first = h.PutLabel("first", false, Array.Empty<(string, string)>());
        var second = h.PutLabel("second", true, Array.Empty<(string, string)>());
        Assert.Equal("second", h.Labels()[0].Name);                    // newest first
        Assert.True(string.CompareOrdinal(second.Id, first.Id) > 0);
        Assert.True(h.RemoveLabel(first.Id));
        Assert.True(!h.RemoveLabel(first.Id));
        Assert.Equal(1, h.Labels().Count);
        var mine = h.PutLabel("mine", false, Array.Empty<(string, string)>());
        for (int i = 0; i < LocalHistory.MaxLabels + 10; i++) h.PutLabel("auto " + i, true, Array.Empty<(string, string)>());
        Assert.Equal(LocalHistory.MaxLabels, h.Labels().Count);
        Assert.Equal("auto " + (LocalHistory.MaxLabels + 9), h.Labels()[0].Name);
        Assert.True(h.Labels().Any(l => l.Id == mine.Id), "a label made by hand outlives the automatic ones");
    }
}
