using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>A big project is searched for usages by several workers; they find exactly what one thread finds.</summary>
public class ParallelUsagesTests
{
    static string Project(int files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_pusage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        File.WriteAllText(Path.Combine(root, "pkg", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "pkg", "base.py"), "class Base:\n    def run(self, x):\n        return x\n\n    def other(self):\n        return 1\n");
        for (int i = 0; i < files; i++)
        {
            string text = "from pkg.base import Base\n\n\n" +
                $"class Child{i}(Base):\n    def go(self):\n        return self.run({i})\n\n\n" +
                $"def use{i}():\n    c = Child{i}()\n    c.run(1)\n    return c.other()\n" +
                (i % 3 == 0 ? $"\n\ndef unrelated{i}(thing):\n    return thing.run(2)\n" : "");   // the receiver is unknown: not a usage
            File.WriteAllText(Path.Combine(root, "pkg", $"mod{i}.py"), text);
        }
        return root;
    }

    static string Key(Location l) => $"{l.Uri}|{l.Range.Start.Line}|{l.Range.Start.Character}";

    [Fact]
    public void WorkersFindWhatOneThreadFinds()
    {
        string root = Project(200);
        try
        {
            string basePath = Path.Combine(root, "pkg", "base.py");
            string text = File.ReadAllText(basePath);
            var pos = new LineMap(text).PositionOf(text.IndexOf("run", StringComparison.Ordinal) + 1);

            List<Location> Find(int parallelFrom)
            {
                var svc = new LanguageService { ParallelUsagesFrom = parallelFrom };
                svc.SourceRoots.Add(root);
                string uri = LanguageService.UriOfPath(basePath);
                svc.Open(uri, text, 1);
                return svc.References(uri, pos, true).ToList();
            }

            var one = Find(int.MaxValue);
            var several = Find(1);
            Assert.True(one.Count >= 400, $"every file calls run twice: {one.Count}");
            Assert.Equal(string.Join("\n", one.Select(Key)), string.Join("\n", several.Select(Key)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RenameThroughWorkersChangesTheSameFiles()
    {
        string root = Project(160);
        try
        {
            string basePath = Path.Combine(root, "pkg", "base.py");
            string text = File.ReadAllText(basePath);
            var pos = new LineMap(text).PositionOf(text.IndexOf("run", StringComparison.Ordinal) + 1);
            var svc = new LanguageService { ParallelUsagesFrom = 1 };
            svc.SourceRoots.Add(root);
            string uri = LanguageService.UriOfPath(basePath);
            svc.Open(uri, text, 1);
            var edit = svc.Rename(uri, pos, "execute");
            Assert.Equal(161, edit.Changes.Count);                       // base.py and every module
            Assert.Equal(321, edit.Changes.Values.Sum(l => l.Count));   // the definition, and two calls in each module
        }
        finally { Directory.Delete(root, true); }
    }
}
