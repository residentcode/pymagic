using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The typeshed shipped with PyMagic (item 144, the repo's typeshed/): every stub parses, and the standard library resolves from it.</summary>
public class BundledTypeshedTests
{
    static string? Bundled()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "typeshed");
            if (File.Exists(Path.Combine(candidate, "stdlib", "VERSIONS"))) return candidate;
        }
        return null;
    }

    [Fact]
    public void EveryStubParses()
    {
        if (Bundled() is not { } typeshed) return;
        var failed = new List<string>();
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(typeshed, "*.pyi", SearchOption.AllDirectories))
        {
            count++;
            var m = new Workspace().AddModule("stub" + count, File.ReadAllText(file), cached: false);
            if (m.SyntaxErrors.Count > 0) failed.Add(Path.GetRelativePath(typeshed, file) + ": " + m.SyntaxErrors[0]);
        }
        Assert.True(count > 4000, $"only {count} stubs");
        Assert.True(failed.Count == 0, string.Join("\n", failed.Take(10)));
    }

    [Fact]
    public void TheStandardLibraryResolvesFromIt()
    {
        if (Bundled() is not { } typeshed) return;
        var ws = new Workspace(new WorkspaceOptions { TypeshedPath = typeshed });
        foreach (var name in new[] { "os", "collections.abc", "asyncio", "json", "dataclasses", "typing_extensions" })
            Assert.True(ws.GetModule(name) is { IsStub: true }, $"{name} is not found in the bundled typeshed");
        // a third-party stub distribution
        Assert.True(ws.GetModule("yaml") is { IsStub: true }, "yaml's stubs are not found");
    }
}
