using System.Text;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Attributes whose types depend on each other must not be worked out again and again (yaml/emitter.py hung the editor).</summary>
public class MutualAttributeTests
{
    [Fact]
    public void ManyAttributesThatReadEachOtherAreTypedQuickly()
    {
        const int n = 24;
        var sb = new StringBuilder("class E:\n    def __init__(self):\n");
        for (int i = 0; i < n; i++) sb.Append($"        self.a{i} = None\n");
        for (int i = 0; i < n; i++)
            sb.Append($"\n    def step{i}(self):\n        self.a{i} = self.a{(i + 1) % n} or self.a{(i + 2) % n} or self.a{(i + 3) % n}\n        self.a{(i + 5) % n} += 1\n");
        string src = sb.ToString();

        var task = Task.Run(() =>
        {
            var ws = new Workspace();
            var m = ws.AddModule("main", src);
            ws.Evaluator.Inspect(m, true);
        });
        Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "typing mutually dependent attributes did not finish");
    }
}
