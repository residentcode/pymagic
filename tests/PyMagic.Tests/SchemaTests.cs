using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The schemas built into PyMagic (a project's own copy first) and the TOML-to-JSON helper used to check pyproject.toml against its schema.</summary>
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
    public void EverySchemaIsBuiltIn()
    {
        Assert.Equal(13, Schemas.Names.Count);
        foreach (var name in new[] { "package", "pyproject", "workflow", "compose", "gitlab", "precommit", "mkdocs", "dependabot", "codecov", "tox", "renovate", "openapi", "swagger" })
        {
            string text = Schemas.Builtin(name) ?? throw new AssertionException(name + " is not built in");
            Assert.True(System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject, name);
        }
        Assert.True(Schemas.Builtin("pyproject")!.Contains("\"project\""));
    }

    [Fact]
    public void AKubernetesKindIsARefIntoTheBuiltInDefinitionsAndNothingElseIsMadeUp()
    {
        static string RefOf(string name) => System.Text.Json.Nodes.JsonNode.Parse(Schemas.Builtin(name) ?? throw new AssertionException(name + " has no schema"))!["$ref"]!.GetValue<string>();
        Assert.Equal("#/definitions/io.k8s.api.apps.v1.Deployment", RefOf("k8s-deployment-apps-v1"));
        Assert.Equal("#/definitions/io.k8s.api.core.v1.Service", RefOf("k8s-service-v1"));
        Assert.Equal("#/definitions/io.k8s.api.networking.v1.Ingress", RefOf("k8s-ingress-networking-v1"));
        Assert.Equal("#/definitions/io.k8s.api.batch.v1.CronJob", RefOf("k8s-cronjob-batch-v1"));
        Assert.True(System.Text.Json.Nodes.JsonNode.Parse(Schemas.Builtin("k8s-pod-v1")!)!["definitions"]!["io.k8s.api.core.v1.Pod"] != null);
        Assert.True(Schemas.Builtin("k8s-nosuchkind-v1") == null);
        foreach (var bad in new[] { "k8s-", "k8s-../x", "k8s-a-b-c-d", "K8S-pod-v1", "../evil", "k8s-pod/v1", "k8s-definitions", "definitions" })
            Assert.True(Schemas.Builtin(bad) == null && Schemas.Of(Path.GetTempPath(), bad) == null, bad);
    }

    [Fact]
    public void AProjectsOwnCopyIsUsedInsteadOfTheBuiltInOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_schema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(Schemas.Builtin("package"), Schemas.Of(root, "package"));
            Directory.CreateDirectory(Schemas.FolderOf(root));
            File.WriteAllText(Schemas.PathOf(root, "package"), "{\"type\":\"object\"}");
            File.WriteAllText(Schemas.PathOf(root, "k8s-service-v1"), "{}");
            Assert.Equal("{\"type\":\"object\"}", Schemas.Of(root, "package"));
            Assert.Equal("{}", Schemas.Of(root, "k8s-service-v1"));
            Assert.Equal(Schemas.Builtin("pyproject"), Schemas.Of(root, "pyproject"));
            Assert.True(Schemas.Of(root, "../package") == null, "only the known names are read");
        }
        finally { Directory.Delete(root, true); }
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
