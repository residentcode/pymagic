using System.Diagnostics;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// The JSON Schemas of SchemaStore that files are checked against (package.json, pyproject.toml, GitHub workflows). They are
/// downloaded once, when the user has said yes, and kept in .pymagic/schemas; nothing is fetched while that folder has the file.
/// </summary>
public static class Schemas
{
    public static readonly IReadOnlyDictionary<string, string> Urls = new Dictionary<string, string>
    {
        ["package"] = "https://json.schemastore.org/package.json",
        ["pyproject"] = "https://json.schemastore.org/pyproject.json",
        ["workflow"] = "https://json.schemastore.org/github-workflow.json",
        // the entries of SchemaStore's catalog whose schema lives with the project that owns it
        ["compose"] = "https://raw.githubusercontent.com/compose-spec/compose-go/master/schema/compose-spec.json",
        ["gitlab"] = "https://gitlab.com/gitlab-org/gitlab-foss/-/raw/master/app/assets/javascripts/editor/schema/ci.json",
        ["precommit"] = "https://www.schemastore.org/pre-commit-config.json",
        ["mkdocs"] = "https://www.schemastore.org/mkdocs-1.6.json",
        ["dependabot"] = "https://www.schemastore.org/dependabot-2.0.json",
        ["codecov"] = "https://www.schemastore.org/codecov.json",
        ["tox"] = "https://raw.githubusercontent.com/tox-dev/tox/main/src/tox/tox.schema.json",
        ["renovate"] = "https://docs.renovatebot.com/renovate-schema.json",
        // OpenAPI 3.0 and Swagger 2.0 (3.1 needs $dynamicRef, which the validator does not follow: those files are not checked)
        ["openapi"] = "https://spec.openapis.org/oas/3.0/schema/2021-09-28",
        ["swagger"] = "https://raw.githubusercontent.com/OAI/OpenAPI-Specification/main/_archive_/schemas/v2.0/schema.json",
    };

    // Kubernetes has one schema per kind (k8s-deployment-apps-v1, k8s-service-v1), from yannh/kubernetes-json-schema's standalone files
    static readonly System.Text.RegularExpressions.Regex KubernetesName = new("^k8s-[a-z0-9]+(-[a-z0-9]+){0,2}$");

    /// <summary>Whether the name is one of the fixed schemas or the schema of a Kubernetes kind.</summary>
    public static bool IsKnown(string name) => Urls.ContainsKey(name) || KubernetesName.IsMatch(name);

    /// <summary>Where a known schema is downloaded from, or null for a name that is not one.</summary>
    public static string? UrlOf(string name) =>
        Urls.TryGetValue(name, out var url) ? url
        : KubernetesName.IsMatch(name) ? "https://raw.githubusercontent.com/yannh/kubernetes-json-schema/master/master-standalone/" + name[4..] + ".json"
        : null;

    public static string FolderOf(string root) => Path.Combine(root, ".pymagic", "schemas");

    public static string PathOf(string root, string name) => Path.Combine(FolderOf(root), name + ".json");

    /// <summary>The schema's text from the kept file, or null when it was not downloaded yet.</summary>
    public static string? Kept(string root, string name) => IsKnown(name) && File.Exists(PathOf(root, name)) ? File.ReadAllText(PathOf(root, name)) : null;

    /// <summary>Downloads one schema (the caller has the user's yes) and keeps it. Throws ServiceException when it cannot.</summary>
    public static async Task<string> Download(string root, string name, HttpClient http)
    {
        if (UrlOf(name) is not { } url) throw new ServiceException("not a schema PyMagic checks against");
        string text;
        try { text = await http.GetStringAsync(url); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new ServiceException("the schema is not reachable"); }
        try { System.Text.Json.Nodes.JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { throw new ServiceException("the schema server did not answer with a schema"); }
        Directory.CreateDirectory(FolderOf(root));
        File.WriteAllText(PathOf(root, name), text);
        return text;
    }

    /// <summary>Which schemas are kept: name to size in bytes.</summary>
    public static Dictionary<string, long> List(string root) =>
        Urls.Keys.Concat(Directory.Exists(FolderOf(root)) ? Directory.GetFiles(FolderOf(root), "k8s-*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => KubernetesName.IsMatch(n)) : [])
            .Where(n => File.Exists(PathOf(root, n))).Distinct().ToDictionary(n => n, n => new FileInfo(PathOf(root, n)).Length);

    /// <summary>A TOML text as JSON (Python's tomllib; dates become strings), or null when it does not parse or Python is older than 3.11.</summary>
    public static string? TomlToJson(string python, string toml)
    {
        const string script = "import sys, json\ntry:\n    import tomllib\nexcept ImportError:\n    sys.exit(3)\ntry:\n    print(json.dumps(tomllib.loads(sys.stdin.buffer.read().decode('utf-8')), default=str))\nexcept Exception:\n    sys.exit(2)\n";
        try
        {
            var psi = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardInput.BaseStream.Write(Encoding.UTF8.GetBytes(toml));
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
