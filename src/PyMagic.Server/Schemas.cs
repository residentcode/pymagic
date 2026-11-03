using System.Diagnostics;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// The JSON Schemas files are checked against (package.json, pyproject.toml, GitHub workflows, docker-compose, Kubernetes, ...). They are built into
/// PyMagic (Schemas/, where each comes from is in its README.md): nothing is downloaded and nothing is asked. A project can keep its own copy of one in
/// .pymagic/schemas, which is used instead.
/// </summary>
public static class Schemas
{
    /// <summary>The fixed schemas, by the name the page asks for.</summary>
    public static readonly IReadOnlyList<string> Names = ["package", "pyproject", "workflow", "compose", "gitlab", "precommit", "mkdocs", "dependabot", "codecov", "tox", "renovate",
        // OpenAPI 3.1 (its schema uses $dynamicRef), 3.0 and Swagger 2.0; Kustomize's kustomization.yaml
        "openapi31", "openapi", "swagger", "kustomization"];

    // Kubernetes has one schema per kind (k8s-deployment-apps-v1, k8s-service-v1): a $ref into the definitions of every built-in kind
    static readonly System.Text.RegularExpressions.Regex KubernetesName = new("^k8s-[a-z0-9]+(-[a-z0-9]+){0,2}$");

    /// <summary>Whether the name is one of the fixed schemas or the schema of a Kubernetes kind.</summary>
    public static bool IsKnown(string name) => Names.Contains(name) || KubernetesName.IsMatch(name);

    static string? Resource(string file)
    {
        using var stream = typeof(Schemas).Assembly.GetManifestResourceStream("schema-" + file + ".json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static readonly Lazy<string?> KubernetesText = new(() => Resource("k8s-definitions")?.Trim());

    // a kind's name (k8s-deployment-apps-v1: the kind, the first part of its API group, the version) to its definition, read from x-kubernetes-group-version-kind
    static readonly Lazy<Dictionary<string, string>> KubernetesKinds = new(() =>
    {
        var kinds = new Dictionary<string, string>();
        if (KubernetesText.Value is not { } text) return kinds;
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        foreach (var def in doc.RootElement.GetProperty("definitions").EnumerateObject())
        {
            if (!def.Value.TryGetProperty("x-kubernetes-group-version-kind", out var gvks) || gvks.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (var gvk in gvks.EnumerateArray())
            {
                string group = gvk.GetProperty("group").GetString() ?? "", kind = gvk.GetProperty("kind").GetString() ?? "", version = gvk.GetProperty("version").GetString() ?? "";
                string name = string.Join("-", new[] { "k8s", kind.ToLowerInvariant(), group.Split('.')[0], version }.Where(p => p.Length > 0));
                kinds.TryAdd(name, def.Name);
            }
        }
        return kinds;
    });

    /// <summary>The schema built into PyMagic, or null for a name it has none of (a kind Kubernetes does not have).</summary>
    public static string? Builtin(string name)
    {
        if (Names.Contains(name)) return Resource(name);
        if (!KubernetesName.IsMatch(name) || !KubernetesKinds.Value.TryGetValue(name, out var definition) || KubernetesText.Value is not { } text) return null;
        // the kind's definition as the root, its definitions beside it (the text after the opening brace, not parsed again)
        return "{\"$ref\":" + System.Text.Json.JsonSerializer.Serialize("#/definitions/" + definition) + "," + text[1..];
    }

    public static string FolderOf(string root) => Path.Combine(root, ".pymagic", "schemas");

    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", ".venv", "venv", "env", "__pycache__", ".pymagic", ".idea", ".tox", "dist", "build" };

    /// <summary>
    /// The project's YAML files that declare a CustomResourceDefinition (their custom resources are checked against them): {path from the
    /// root, text}. At most 5,000 files are looked at, each under 2 MB, leaving out environments, node_modules and build output.
    /// </summary>
    public static List<(string Path, string Text)> CustomResourceDefinitions(string root)
    {
        var found = new List<(string, string)>();
        var pending = new Stack<string>();
        pending.Push(root);
        int seen = 0;
        while (pending.Count > 0 && seen < 5000)
        {
            string dir = pending.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string f in files)
            {
                if (++seen > 5000) break;
                if (!f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (new FileInfo(f).Length > 2 * 1024 * 1024) continue;
                    string text = File.ReadAllText(f);
                    if (text.Contains("CustomResourceDefinition") && text.Contains("apiextensions.k8s.io"))
                        found.Add((Path.GetRelativePath(root, f).Replace('\\', '/'), text));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            foreach (string d in dirs) if (!Skipped.Contains(Path.GetFileName(d))) pending.Push(d);
        }
        return found;
    }

    public static string PathOf(string root, string name) => Path.Combine(FolderOf(root), name + ".json");

    /// <summary>The schema a file is checked against: the project's own copy when it keeps one, else the built-in one; null when there is neither.</summary>
    public static string? Of(string root, string name)
    {
        if (!IsKnown(name)) return null;
        try { if (File.Exists(PathOf(root, name))) return File.ReadAllText(PathOf(root, name)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Builtin(name);
    }

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
