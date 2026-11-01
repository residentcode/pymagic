using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The kept SchemaStore schemas and the TOML-to-JSON helper used to check pyproject.toml against its schema.</summary>
public class SchemaTests
{
    static string PythonExe => Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";

    static bool PythonHasToml()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(PythonExe, "-c \"import tomllib\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(20000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public void AKeptSchemaIsReadWithoutDownloading()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_schema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(Schemas.Kept(root, "package") == null);
            Assert.Equal(0, Schemas.List(root).Count);
            Directory.CreateDirectory(Schemas.FolderOf(root));
            File.WriteAllText(Schemas.PathOf(root, "package"), "{\"type\":\"object\"}");
            Assert.Equal("{\"type\":\"object\"}", Schemas.Kept(root, "package"));
            Assert.Equal(1, Schemas.List(root).Count);
            Assert.True(Schemas.Kept(root, "../package") == null, "only the known names are read");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EverySchemaHasAnHttpsAddressAndOnlyTheKnownOnesAreKept()
    {
        Assert.Equal(10, Schemas.Urls.Count);
        foreach (var name in new[] { "package", "pyproject", "workflow", "compose", "gitlab", "precommit", "mkdocs", "dependabot", "codecov", "tox" })
            Assert.True(Schemas.Urls.TryGetValue(name, out var url) && url.StartsWith("https://"), name);
    }

    [Fact]
    public void OnlyKnownSchemasAreDownloaded()
    {
        using var http = new HttpClient();
        bool refused = false;
        try { Schemas.Download(Path.GetTempPath(), "../evil", http).Wait(); }
        catch (AggregateException e) { refused = e.InnerException is ServiceException; }
        Assert.True(refused);
    }

    [Fact]
    public void TomlBecomesJsonAndBadTomlIsNull()
    {
        if (!PythonHasToml()) return;
        string json = Schemas.TomlToJson(PythonExe, "[project]\nname = \"shop\"\nn = 3\nwhen = 1979-05-27\n")!;
        Assert.Contains("\"name\": \"shop\"", json);
        Assert.Contains("\"n\": 3", json);
        Assert.Contains("1979-05-27", json);
        Assert.True(Schemas.TomlToJson(PythonExe, "[project\n") == null);
    }
}
