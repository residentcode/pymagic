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
        Assert.Equal(13, Schemas.Urls.Count);
        foreach (var name in new[] { "package", "pyproject", "workflow", "compose", "gitlab", "precommit", "mkdocs", "dependabot", "codecov", "tox", "renovate", "openapi", "swagger" })
            Assert.True(Schemas.Urls.TryGetValue(name, out var url) && url.StartsWith("https://"), name);
    }

    [Fact]
    public void AKubernetesKindHasItsOwnSchemaAddressAndNothingElseIsMadeUp()
    {
        Assert.Equal("https://raw.githubusercontent.com/yannh/kubernetes-json-schema/master/master-standalone/deployment-apps-v1.json", Schemas.UrlOf("k8s-deployment-apps-v1"));
        Assert.True(Schemas.UrlOf("k8s-service-v1")!.EndsWith("/service-v1.json"));
        Assert.Equal("https://docs.renovatebot.com/renovate-schema.json", Schemas.UrlOf("renovate"));
        foreach (var bad in new[] { "k8s-", "k8s-../x", "k8s-a-b-c-d", "K8S-pod-v1", "../evil", "k8s-pod/v1" })
            Assert.True(Schemas.UrlOf(bad) == null && !Schemas.IsKnown(bad), bad);
    }

    [Fact]
    public void KeptKubernetesSchemasAreListedAndRead()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_schema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Schemas.FolderOf(root));
        try
        {
            File.WriteAllText(Schemas.PathOf(root, "k8s-pod-v1"), "{}");
            File.WriteAllText(Path.Combine(Schemas.FolderOf(root), "k8s-not a name.json"), "{}");
            File.WriteAllText(Schemas.PathOf(root, "renovate"), "{}");
            Assert.Equal("k8s-pod-v1,renovate", string.Join(",", Schemas.List(root).Keys.OrderBy(k => k)));
            Assert.Equal("{}", Schemas.Kept(root, "k8s-pod-v1"));
            Assert.True(Schemas.Kept(root, "k8s-service-v1") == null);
        }
        finally { Directory.Delete(root, true); }
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
