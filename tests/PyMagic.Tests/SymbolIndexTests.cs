using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The index of the names of the project: built in the background, remembered, and kept up to date.</summary>
public class SymbolIndexTests
{
    static string Project(Dictionary<string, string> files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_idx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (rel, text) in files) Write(root, rel, text);
        return root;
    }

    static void Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    static LanguageService Service(string root)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add(root);
        return svc;
    }

    static readonly Dictionary<string, string> Sample = new()
    {
        ["shop/__init__.py"] = "",
        ["shop/models.py"] = "MAX_ITEMS = 10\n\nclass Book:\n    title = ''\n    def price(self):\n        return 1\n\ndef make_book():\n    pass\n",
        ["shop/views.py"] = "from .models import Book\n\ndef index(request):\n    return Book()\n",
        ["main.py"] = "def run():\n    pass\n",
    };

    [Fact]
    public void EveryFileOfTheProjectIsIndexedWithoutOpeningIt()
    {
        string root = Project(Sample);
        var svc = Service(root);
        svc.StartIndexing()!.Wait();
        var found = svc.WorkspaceSymbols("book");
        Assert.True(found.Any(s => s.Name == "Book" && s.Kind == SymbolKind.Class && s.ContainerName == "shop.models"), "the class");
        Assert.True(found.Any(s => s.Name == "make_book" && s.Kind == SymbolKind.Function), "the function");
        Assert.True(svc.WorkspaceSymbols("price").Any(s => s.Name == "price" && s.Kind == SymbolKind.Method && s.ContainerName == "shop.models.Book"), "the method");
        Assert.True(svc.WorkspaceSymbols("MAX").Any(s => s.Name == "MAX_ITEMS" && s.Kind == SymbolKind.Constant), "the constant");
        var hit = svc.WorkspaceSymbols("make_book").First(s => s.Name == "make_book");
        Assert.True(hit.Location.Uri.EndsWith("shop/models.py"), "the location is the file");
        Assert.Equal(7, hit.Location.Range.Start.Line);
        Assert.Equal(4, hit.Location.Range.Start.Character);
    }

    [Fact]
    public void AnOpenDocumentAnswersForItselfAndTheIndexSkipsItsFile()
    {
        string root = Project(Sample);
        var svc = Service(root);
        svc.StartIndexing()!.Wait();
        string uri = LanguageService.UriOfPath(Path.Combine(root, "main.py"));
        svc.Open(uri, "def run():\n    pass\n\ndef run_fast():\n    pass\n", 1);
        var found = svc.WorkspaceSymbols("run");
        Assert.Equal(1, found.Count(s => s.Name == "run"));      // not once from the buffer and once from the disk
        Assert.True(found.Any(s => s.Name == "run_fast"), "the unsaved name");
    }

    [Fact]
    public void FoldersThatHoldNoProjectCodeAreLeftOut()
    {
        string root = Project(new() { ["app.py"] = "def real(): pass\n", [".venv/lib/x.py"] = "def hidden_venv(): pass\n", ["node_modules/y.py"] = "def hidden_node(): pass\n" });
        var svc = Service(root);
        svc.StartIndexing()!.Wait();
        Assert.True(svc.WorkspaceSymbols("real").Any(), "the project's own");
        Assert.Equal(0, svc.WorkspaceSymbols("hidden").Count);
    }

    [Fact]
    public void OnlyTheFilesThatChangedAreReadAgain()
    {
        string root = Project(Sample);
        using var index = new SymbolIndex(() => Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories), p => Path.GetFileNameWithoutExtension(p), null);
        index.Reconcile();
        Assert.Equal(4, index.Parsed);
        index.Reconcile();
        Assert.Equal(4, index.Parsed);   // nothing changed: nothing read

        Write(root, "main.py", "def run():\n    pass\n\ndef walk():\n    pass\n");
        File.SetLastWriteTimeUtc(Path.Combine(root, "main.py"), DateTime.UtcNow.AddMinutes(1));
        Write(root, "shop/new.py", "class Fresh: pass\n");
        File.Delete(Path.Combine(root, "shop", "views.py"));
        index.Reconcile();
        Assert.Equal(6, index.Parsed);   // the changed file and the new one
        var names = index.Query("").Select(x => x.Symbol.Name).ToHashSet();
        Assert.True(names.Contains("walk") && names.Contains("Fresh"), "the new names");
        Assert.True(!names.Contains("index"), "the deleted file's names are gone");
        Assert.Equal(4, index.Files);
    }

    [Fact]
    public void TheIndexIsSavedAndTheNextSessionReadsNothingThatDidNotChange()
    {
        string root = Project(Sample);
        string store = Path.Combine(root, ".pymagic", "index.json");
        Func<IEnumerable<string>> files = () => Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories).Where(f => !f.Contains(".pymagic"));
        using (var first = new SymbolIndex(files, p => Path.GetFileNameWithoutExtension(p), store))
        {
            first.Start(Array.Empty<string>()).Wait();
            Assert.Equal(4, first.Parsed);
        }
        Assert.True(File.Exists(store), "saved");
        Assert.True(File.ReadAllText(Path.Combine(root, ".pymagic", ".gitignore")).Contains("index.json"), "the cache stays out of version control");

        using (var second = new SymbolIndex(files, p => Path.GetFileNameWithoutExtension(p), store))
        {
            second.Start(Array.Empty<string>()).Wait();
            Assert.Equal(0, second.Parsed);
            Assert.True(second.Query("make_book").Any(), "the names came from the saved index");
        }

        Write(root, "main.py", "def run_again():\n    pass\n");
        File.SetLastWriteTimeUtc(Path.Combine(root, "main.py"), DateTime.UtcNow.AddMinutes(2));
        using (var third = new SymbolIndex(files, p => Path.GetFileNameWithoutExtension(p), store))
        {
            third.Start(Array.Empty<string>()).Wait();
            Assert.Equal(1, third.Parsed);   // only the file that changed between the sessions
            Assert.True(third.Query("run_again").Any() && !third.Query("run").Any(x => x.Symbol.Name == "run"), "the new text");
        }
    }

    [Fact]
    public void ADamagedIndexFileIsBuiltAgain()
    {
        string root = Project(Sample);
        string store = Path.Combine(root, ".pymagic", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "{ this is not json");
        using var index = new SymbolIndex(() => Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories), p => Path.GetFileNameWithoutExtension(p), store);
        index.Start(Array.Empty<string>()).Wait();
        Assert.Equal(4, index.Parsed);
        Assert.True(index.Query("Book").Any(), "built again");
    }

    [Fact]
    public void TheFileWatcherKeepsTheIndexCurrent()
    {
        string root = Project(Sample);
        using var index = new SymbolIndex(() => Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories), p => Path.GetFileNameWithoutExtension(p), null);
        index.Start(new[] { root }).Wait();

        Write(root, "shop/extra.py", "def appears_by_itself():\n    pass\n");
        Assert.True(Eventually(() => index.Query("appears_by_itself").Any()), "a new file is indexed");

        Write(root, "shop/extra.py", "def renamed_by_itself():\n    pass\n");
        Assert.True(Eventually(() => index.Query("renamed_by_itself").Any() && !index.Query("appears_by_itself").Any()), "a saved file is indexed again");

        File.Delete(Path.Combine(root, "shop", "extra.py"));
        Assert.True(Eventually(() => !index.Query("renamed_by_itself").Any()), "a deleted file is forgotten");

        Directory.Delete(Path.Combine(root, "shop"), true);
        Assert.True(Eventually(() => !index.Query("make_book").Any()), "a deleted folder takes its files with it");
    }

    static bool Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }
}
