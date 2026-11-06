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
    /// <summary>The fixed schemas the status bar's picker lists, by the name the page asks for, with the title PyCharm gives them where it has them.</summary>
    public static readonly IReadOnlyList<(string Name, string Title)> Registered =
    [
        ("asyncapi2", "AsyncAPI 2"), ("asyncapi3", "AsyncAPI 3"), ("babelrc", "Babelrc"), ("codecov", "Codecov"), ("compose", "Compose Specification"),
        ("dependabot", "Dependabot"), ("devcontainer", "Dev Containers"), ("eslintrc", "ESLint"), ("github-action", "GitHub Action JSON Schema"),
        ("workflow", "GitHub Workflow JSON Schema"), ("gitlab", "GitLab CI"), ("http-client-env", "HTTP Client Environment File"),
        ("http-client-private-env", "HTTP Client Private Environment File"), ("jest", "Jest"), ("jsconfig", "JSConfig"),
        ("jsonschema-2019-09", "JSON Schema 2019-09"), ("jsonschema-2020-12", "JSON Schema 2020-12"), ("jsonschema-4", "JSON Schema 4"),
        ("jsonschema-6", "JSON Schema 6"), ("jsonschema-7", "JSON Schema 7"), ("kustomization", "Kustomization"),
        ("markdown-frontmatter", "Markdown Front Matter Generic Schema"), ("mermaid", "Mermaid Config"), ("mkdocs", "MkDocs"),
        ("openapi", "OpenAPI 3.0"), ("openapi31", "OpenAPI 3.1"), ("package", "package"), ("precommit", "pre-commit"), ("prettierrc", "Prettier"),
        ("pyproject", "pyproject.toml"), ("stylelintrc", "StyleLint"), ("swagger", "Swagger 2.0"), ("tox", "tox"),
        ("tsconfig", "TSConfig"), ("tsd", "TSD"), ("tslint", "TSLint"), ("typings", "Typings"),
    ];

    /// <summary>The fixed schemas, by the name the page asks for: the registered ones and the vocabularies the JSON Schema meta-schemas $ref.</summary>
    public static readonly IReadOnlyList<string> Names = Registered.Select(r => r.Name).Concat(new[]
    {
        "jsonschema-2019-09-core", "jsonschema-2019-09-applicator", "jsonschema-2019-09-validation", "jsonschema-2019-09-meta-data", "jsonschema-2019-09-format",
        "jsonschema-2019-09-content", "jsonschema-2020-12-core", "jsonschema-2020-12-applicator", "jsonschema-2020-12-unevaluated", "jsonschema-2020-12-validation",
        "jsonschema-2020-12-meta-data", "jsonschema-2020-12-format-annotation", "jsonschema-2020-12-content",
    }).ToList();

    // Kubernetes has one schema per kind (k8s-deployment-apps-v1, k8s-service-v1): a $ref into the definitions of every built-in kind
    static readonly System.Text.RegularExpressions.Regex KubernetesName = new("^k8s-[a-z0-9]+(-[a-z0-9]+){0,2}$");

    /// <summary>Whether the name is one of the fixed schemas or the schema of a Kubernetes kind.</summary>
    public static bool IsKnown(string name) => Names.Contains(name) || ProjectOnly.Contains(name) || KubernetesName.IsMatch(name) || StoreStem(name) is { } stem && Store.Value.Files.Contains(stem);

    /// <summary>Schemas the page knows a file by but PyMagic does not carry: only a project's own copy in .pymagic/schemas is used. Renovate's
    /// project is under the GNU AGPL 3.0, whose terms do not fit PyMagic's license.</summary>
    public static readonly IReadOnlyList<string> ProjectOnly = new[] { "renovate" };

    // ------------------------------------------------------------ SchemaStore.org

    /// <summary>A SchemaStore schema's name as the page asks for it: "store:" and its file's name without .json (store:babelrc).</summary>
    public const string StorePrefix = "store:";

    static string? StoreStem(string name) => name.StartsWith(StorePrefix, StringComparison.Ordinal) ? name[StorePrefix.Length..] : null;

    /// <summary>A catalog entry PyMagic has the schema of: its name (store:...), the title the catalog gives it, and the files it is for.</summary>
    public sealed record StoreEntry(string Name, string Title, IReadOnlyList<string> FileMatch);

    /// <summary>
    /// SchemaStore's catalog and schemas, built into PyMagic (SchemaStore/: downloaded with the user's yes, every schema of its repository up to 500 KB,
    /// Apache License 2.0, its LICENSE.txt ships beside the app): the index says which files each catalog entry is for and each schema's $id; the
    /// schemas are one zip, read when a file needs one.
    /// </summary>
    static readonly Lazy<(IReadOnlyList<StoreEntry> Catalog, Dictionary<string, string> Ids, HashSet<string> Files, byte[] Zip)> Store = new(() =>
    {
        var catalog = new List<StoreEntry>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        byte[] zip = [];
        using (var z = typeof(Schemas).Assembly.GetManifestResourceStream("schemastore.zip"))
        {
            if (z == null) return (catalog, ids, files, zip);
            using var copy = new MemoryStream();
            z.CopyTo(copy);
            zip = copy.ToArray();
        }
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read))
            foreach (var e in archive.Entries) if (e.Name.EndsWith(".json", StringComparison.Ordinal)) files.Add(e.Name[..^5]);
        using var index = typeof(Schemas).Assembly.GetManifestResourceStream("schemastore-index.json");
        if (index == null) return (catalog, ids, files, zip);
        using var doc = System.Text.Json.JsonDocument.Parse(index);
        foreach (var e in doc.RootElement.GetProperty("catalog").EnumerateArray())
        {
            string stem = e.GetProperty("name").GetString() ?? "";
            if (!files.Contains(stem)) continue;
            catalog.Add(new StoreEntry(StorePrefix + stem, e.GetProperty("title").GetString() ?? stem, e.GetProperty("fileMatch").EnumerateArray().Select(f => f.GetString() ?? "").Where(f => f.Length > 0).ToList()));
        }
        foreach (var p in doc.RootElement.GetProperty("ids").EnumerateObject())
            if (p.Value.GetString() is { } stem && files.Contains(stem)) ids.TryAdd(NormalId(p.Name), StorePrefix + stem);
        return (catalog, ids, files, zip);
    });

    /// <summary>SchemaStore's catalog entries whose schema PyMagic has (the status bar's picker lists them; the page picks one by a file's name).</summary>
    public static IReadOnlyList<StoreEntry> StoreCatalog => Store.Value.Catalog;

    static readonly object StoreLock = new();

    /// <summary>The text of a SchemaStore schema, from the zip; null when PyMagic has not got it.</summary>
    static string? StoreText(string stem)
    {
        if (!Store.Value.Files.Contains(stem)) return null;
        lock (StoreLock)
        {
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(Store.Value.Zip), System.IO.Compression.ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry(stem + ".json")!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Each fixed schema's $id (or draft-04's id) to its name, without the fragment and with http read as https: what a file's "$schema" names, and
    /// what one schema $refs of another (AsyncAPI's of JSON Schema 7, the 2020-12 meta-schema's of its vocabularies), are found by it.
    /// </summary>
    public static readonly Lazy<Dictionary<string, string>> Ids = new(() =>
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            if (Resource(name) is not { } text) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                foreach (var key in new[] { "$id", "id" })
                    if (doc.RootElement.TryGetProperty(key, out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() is { Length: > 0 } s)
                    {
                        ids.TryAdd(NormalId(s), name);
                        break;
                    }
            }
            catch (System.Text.Json.JsonException) { }
        }
        // SchemaStore's after PyMagic's own: an address both have is PyMagic's
        foreach (var (id, name) in Store.Value.Ids) ids.TryAdd(id, name);
        return ids;
    });

    /// <summary>A schema address as the ids are kept: no fragment, no trailing slash, http as https.</summary>
    public static string NormalId(string id)
    {
        int hash = id.IndexOf('#');
        if (hash >= 0) id = id[..hash];
        if (id.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) id = "https://" + id[7..];
        // SchemaStore's one schema has several addresses: json.schemastore.org, www.schemastore.org or schemastore.org, with .json or without
        var store = StoreAddress.Match(id);
        if (store.Success) id = "https://json.schemastore.org/" + store.Groups["path"].Value;
        return id.TrimEnd('/');
    }

    static readonly System.Text.RegularExpressions.Regex StoreAddress = new(@"^https://(www\.|json\.)?schemastore\.org/(?<path>.+?)(\.json)?/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static string? Resource(string file)
    {
        using var stream = typeof(Schemas).Assembly.GetManifestResourceStream("schema-" + file + ".json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static readonly Lazy<string?> KubernetesText = new(() => Resource("k8s-definitions")?.Trim());

    /// <summary>
    /// Mermaid's schema describes the whole configuration after its defaults are filled in, so it requires every field (fontFamily, logLevel, every
    /// diagram's section); a config file sets only what it changes. It is served without its `required` lists, so a real file is not reported.
    /// </summary>
    static readonly Lazy<string?> MermaidText = new(() =>
    {
        if (Resource("mermaid") is not { } text) return null;
        var node = System.Text.Json.Nodes.JsonNode.Parse(text);
        void Strip(System.Text.Json.Nodes.JsonNode? n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
            {
                if (o["required"] is System.Text.Json.Nodes.JsonArray) o.Remove("required");
                foreach (var (_, v) in o.ToList()) Strip(v);
            }
            else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var v in a) Strip(v);
        }
        Strip(node);
        return node?.ToJsonString();
    });

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
        if (name == "mermaid") return MermaidText.Value;
        if (StoreStem(name) is { } stem) return StoreText(stem);
        if (Names.Contains(name)) return Resource(name);
        if (!KubernetesName.IsMatch(name) || !KubernetesKinds.Value.TryGetValue(name, out var definition) || KubernetesText.Value is not { } text) return null;
        // the kind's definition as the root, its definitions beside it (the text after the opening brace, not parsed again)
        return "{\"$ref\":" + System.Text.Json.JsonSerializer.Serialize("#/definitions/" + definition) + "," + text[1..];
    }

    public static string FolderOf(string root) => Path.Combine(root, ".pymagic", "schemas");

    /// <summary>
    /// The project's own choices, as PyCharm keeps its JSON Schema mappings: <c>{"mappings": [{"name", "schema", "patterns": [...]}], "ignored": [...]}</c>
    /// in .pymagic/schema-mappings.json. A mapping's schema is a built-in name or <c>file:</c> and a path in the project; its patterns are paths from
    /// the project's root, with * (in a name) and ** (any folders). Ignored files are checked against no schema.
    /// </summary>
    public static string MappingsPath(string root) => Path.Combine(root, ".pymagic", "schema-mappings.json");

    public static string ReadMappings(string root)
    {
        try { if (File.Exists(MappingsPath(root))) return CheckedMappings(File.ReadAllText(MappingsPath(root))) ?? Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Empty;
    }

    const string Empty = "{\"mappings\":[],\"ignored\":[]}";

    /// <summary>Writes the project's mappings; false when the text is not of their shape (nothing is written then).</summary>
    public static bool WriteMappings(string root, string json)
    {
        if (CheckedMappings(json) is not { } text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(MappingsPath(root))!);
        File.WriteAllText(MappingsPath(root), text + "\n");
        return true;
    }

    /// <summary>The mappings as they are kept (indented), or null when the text is not of their shape: a schema that is neither built in nor a project file.</summary>
    static string? CheckedMappings(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return null;
            var mappings = node["mappings"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            var ignored = node["ignored"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            foreach (var m in mappings)
            {
                if (m is not System.Text.Json.Nodes.JsonObject o || o["schema"]?.GetValue<string>() is not { Length: > 0 } schema) return null;
                if (!schema.StartsWith("file:", StringComparison.Ordinal) && !IsKnown(schema)) return null;
                if (o["patterns"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 } patterns || patterns.Any(p => p?.GetValue<string>() is not { Length: > 0 })) return null;
                if (o["name"] is not null && o["name"]!.GetValue<string>() is null) return null;
            }
            if (ignored.Any(p => p?.GetValue<string>() is not { Length: > 0 })) return null;
            var clean = new System.Text.Json.Nodes.JsonObject { ["mappings"] = mappings.DeepClone(), ["ignored"] = ignored.DeepClone() };
            return clean.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

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
