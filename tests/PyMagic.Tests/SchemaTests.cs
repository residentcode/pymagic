using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The schemas built into PyMagic (a project's own copy first) and the TOML-to-JSON helper used to check pyproject.toml against its schema.</summary>
public class SchemaTests
{
    static string PythonExe => Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";

    [Fact]
    public void EachSchemaIsFoundByItsId()
    {
        // what a file's "$schema" and one schema's $ref of another name: http read as https, without the fragment
        var ids = Schemas.Ids.Value;
        Assert.Equal("jsonschema-7", ids[Schemas.NormalId("http://json-schema.org/draft-07/schema#")]);
        Assert.Equal("jsonschema-4", ids[Schemas.NormalId("http://json-schema.org/draft-04/schema#")]);
        Assert.Equal("jsonschema-2020-12-core", ids["https://json-schema.org/draft/2020-12/meta/core"]);
        Assert.Equal("tsconfig", ids["https://json.schemastore.org/tsconfig"]);
        Assert.Equal("asyncapi3", ids["https://asyncapi.com/definitions/3.0.0/asyncapi.json"]);
    }

    [Fact]
    public void SchemaStoresSchemasAreBuiltIn()
    {
        // its catalog's entries whose schema is in the zip, with the files they are for; each schema read when asked for
        Assert.True(Schemas.StoreCatalog.Count > 600, $"{Schemas.StoreCatalog.Count} entries");
        var nodemon = Schemas.StoreCatalog.First(e => e.Name == "store:nodemon");
        Assert.Equal("nodemon.json", nodemon.FileMatch[0]);
        Assert.True(Schemas.IsKnown("store:nodemon") && !Schemas.IsKnown("store:no-such-schema"), "known");
        Assert.True(System.Text.Json.Nodes.JsonNode.Parse(Schemas.Builtin("store:ruff")!) is System.Text.Json.Nodes.JsonObject, "ruff's schema from the zip");
        // a schema both have is PyMagic's own; one only SchemaStore has is found by its address in every form
        Assert.Equal("tsconfig", Schemas.Ids.Value[Schemas.NormalId("https://json.schemastore.org/tsconfig.json")]);
        Assert.Equal("store:nodemon", Schemas.Ids.Value[Schemas.NormalId("https://www.schemastore.org/nodemon.json")]);
        Assert.Equal("https://json.schemastore.org/partial-eslint-plugins", Schemas.NormalId("http://json.schemastore.org/partial-eslint-plugins.json#"));
        Assert.True(Schemas.Ids.Value.ContainsKey("https://json.schemastore.org/partial-eslint-plugins"), "ESLint's plugin part, which eslintrc $refs");
    }

    [Fact]
    public void MermaidsSchemaRequiresNothing()
    {
        // it describes the whole configuration with its defaults filled in; a config file sets only what it changes
        var text = Schemas.Builtin("mermaid")!;
        Assert.True(!text.Contains("\"required\":["), "no required lists");
        Assert.True(text.Contains("\"flowchart\""), "the schema itself is kept");
    }

    [Fact]
    public void TheProjectsMappingsAreKeptWhenTheyAreOfTheirShape()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm-mappings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("{\"mappings\":[],\"ignored\":[]}", Schemas.ReadMappings(root));
            Assert.True(Schemas.WriteMappings(root, "{\"mappings\":[{\"name\":\"TS\",\"schema\":\"tsconfig\",\"patterns\":[\"base.json\"]},{\"name\":\"Mine\",\"schema\":\"file:schemas/a.json\",\"patterns\":[\"config/*.json\"]}],\"ignored\":[\"x.json\"]}"));
            var kept = System.Text.Json.Nodes.JsonNode.Parse(Schemas.ReadMappings(root))!;
            Assert.Equal(2, kept["mappings"]!.AsArray().Count);
            Assert.Equal("x.json", kept["ignored"]![0]!.GetValue<string>());
            // a schema PyMagic has not got, no files, or not JSON: refused, and the file stays as it was
            Assert.True(!Schemas.WriteMappings(root, "{\"mappings\":[{\"name\":\"X\",\"schema\":\"nope\",\"patterns\":[\"a.json\"]}]}"), "an unknown schema");
            Assert.True(!Schemas.WriteMappings(root, "{\"mappings\":[{\"name\":\"X\",\"schema\":\"tsconfig\",\"patterns\":[]}]}"), "no files");
            Assert.True(!Schemas.WriteMappings(root, "{\"mappings\":[{\"name\":\"X\",\"schema\":\"tsconfig\"}]}"), "no patterns");
            Assert.True(!Schemas.WriteMappings(root, "not json"), "not JSON");
            Assert.Equal(2, System.Text.Json.Nodes.JsonNode.Parse(Schemas.ReadMappings(root))!["mappings"]!.AsArray().Count);
        }
        finally { Directory.Delete(root, true); }
    }

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
        // the registered ones (the status bar's list, PyCharm's and the earlier ones) and the vocabularies of the 2019-09 and 2020-12 meta-schemas
        Assert.Equal(38, Schemas.Registered.Count);
        Assert.Equal(38 + 13, Schemas.Names.Count);
        foreach (var name in Schemas.Names)
        {
            string text = Schemas.Builtin(name) ?? throw new AssertionException(name + " is not built in");
            Assert.True(System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject, name);
        }
        foreach (var title in new[] { "AsyncAPI 2", "AsyncAPI 3", "Babelrc", "Dev Containers", "ESLint", "GitHub Action JSON Schema", "GitHub Workflow JSON Schema", "HTTP Client Environment File",
            "HTTP Client Private Environment File", "Jest", "JSConfig", "JSON Schema 2019-09", "JSON Schema 2020-12", "JSON Schema 4", "JSON Schema 6", "JSON Schema 7",
            "Markdown Front Matter Generic Schema", "OpenAPI 3.0", "OpenAPI 3.1", "package", "Prettier", "StyleLint", "Swagger 2.0", "TSConfig", "TSD", "TSLint", "Typings" })
            Assert.True(Schemas.Registered.Any(r => r.Title == title), "PyCharm's " + title);
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

    [Fact]
    public void TheProjectsCustomResourceDefinitionsAreFound()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_crd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "deploy", "crds"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "x"));
        try
        {
            string crd = "apiVersion: apiextensions.k8s.io/v1\nkind: CustomResourceDefinition\nspec:\n  group: shop.example.com\n";
            File.WriteAllText(Path.Combine(root, "deploy", "crds", "shelf.yaml"), crd);
            File.WriteAllText(Path.Combine(root, "deploy", "app.yml"), "apiVersion: apps/v1\nkind: Deployment\n");
            File.WriteAllText(Path.Combine(root, "node_modules", "x", "crd.yaml"), crd);   // a library's copy is not the project's
            var found = Schemas.CustomResourceDefinitions(root);
            Assert.Equal("deploy/crds/shelf.yaml", string.Join(",", found.Select(f => f.Path)));
            Assert.Contains("shop.example.com", found[0].Text);
        }
        finally { Directory.Delete(root, true); }
    }
}
