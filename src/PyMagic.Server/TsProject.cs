using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// What the editor's TypeScript service needs of a JavaScript or TypeScript project beyond the files open in it (it sees only those
/// otherwise): the project's own .ts/.tsx/.js/.jsx/.mjs/.cjs files, its tsconfig.json (or jsconfig.json) with what it extends, and the
/// packages' types: each package of node_modules' package.json, the .d.ts files of the packages the project depends on (and of the packages
/// those depend on), and every @types package. The page gives them to Monaco's TypeScript service as files of their own, under the same
/// addresses the editor's models have (pymagic:///path), so imports, Go to Definition and type errors reach across them.
/// </summary>
public static class TsProject
{
    static readonly string[] SkipDirs = { "node_modules", ".git", "vendor", ".venv", "venv", "env", "__pycache__", "bin", "obj", ".pymagic", "dist", "build", "out",
        ".next", ".nuxt", ".svelte-kit", ".output", "coverage", ".turbo", ".cache", "storage", "public" };
    static readonly string[] CodeExtensions = { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs" };
    static readonly JsonDocumentOptions Loose = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public sealed record File(string Path, string Text);
    /// <summary>The files and whether some were left out (<paramref name="Truncated"/>: the limits were reached).</summary>
    public sealed record Files(List<File> List, bool Truncated);

    /// <summary>Whether the project has JavaScript or TypeScript to speak of: a package.json, a tsconfig.json or a jsconfig.json at its root.</summary>
    public static bool IsJsProject(string root) =>
        new[] { "package.json", "tsconfig.json", "jsconfig.json" }.Any(f => System.IO.File.Exists(System.IO.Path.Combine(root, f)));

    /// <summary>The project's own code files (not node_modules', builds', environments'), at most <paramref name="maxFiles"/> and <paramref name="maxBytes"/>.</summary>
    public static Files Code(string root, int maxFiles = 3000, long maxBytes = 16 << 20)
    {
        var list = new List<File>();
        long bytes = 0;
        bool cut = false;
        // folder by folder, the nearest to the root first: a project too large for the limits keeps its own top (src, its entry files) and
        // loses the deepest folders, whose files the page reads when a file that imports them is opened (/api/ts/package, /api/file)
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        void Walk(string dir, int depth)
        {
            if (depth > 20 || cut) return;
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal).ToList(); dirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var f in files)
            {
                string name = System.IO.Path.GetFileName(f);
                // a package.json of the project's own too: its "type" says whether its .ts and .js files are ES modules (module nodenext reads it)
                bool manifest = name.Equals("package.json", StringComparison.OrdinalIgnoreCase);
                if (!manifest && (!CodeExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || name.Contains(".min.", StringComparison.OrdinalIgnoreCase))) continue;
                long size;
                try { size = new FileInfo(f).Length; } catch (IOException) { continue; }
                if (size > 1 << 20) continue;   // a bundle, not code of the project's own
                if (list.Count >= maxFiles || bytes + size > maxBytes) { cut = true; return; }
                try { list.Add(new File(Rel(root, f), System.IO.File.ReadAllText(f))); bytes += size; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            foreach (var d in dirs)
            {
                string name = System.IO.Path.GetFileName(d);
                if (name.StartsWith('.') && name != ".storybook" || SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                queue.Enqueue((d, depth + 1));
            }
        }
        while (queue.Count > 0 && !cut) { var (dir, depth) = queue.Dequeue(); Walk(dir, depth); }

        // a project's built declarations (its config's outDir or declarationDir, which a dist or build folder usually is, left out above):
        // what another project imports by that path, or by a package whose "types" names it (a referenced project, a workspace's package)
        var seen = new HashSet<string>(list.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var config in cut ? new List<JsonObject>() : Configs(root))
        {
            var options = config["compilerOptions"] as JsonObject;
            // its outDir and declarationDir (the config's folder's), and its rootDirs (the project's, see ConfigOf): generated declarations
            // there (SvelteKit's ./$types in .svelte-kit/types) are what its imports find
            var places = new List<string>();
            foreach (var key in new[] { "declarationDir", "outDir" })
                if (options?[key] is JsonValue v && v.TryGetValue<string>(out var outDir) && !string.IsNullOrWhiteSpace(outDir))
                    places.Add(System.IO.Path.Combine(config["dir"]?.GetValue<string>() ?? "", outDir));
            if (options?["rootDirs"] is JsonArray rootDirs)
                foreach (var d in rootDirs)
                    if (d is JsonValue dv && dv.TryGetValue<string>(out var ds) && ds != ".") places.Add(ds);
            foreach (var place in places)
            {
                string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, place));
                if (!IsUnder(root, dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> found;
                try { found = Directory.EnumerateFiles(dir, "*.d.ts", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 12, IgnoreInaccessible = true }).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in found)
                {
                    string rel = Rel(root, f);
                    if (rel.Contains("/node_modules/", StringComparison.Ordinal) || !seen.Add(rel)) continue;
                    long size;
                    try { size = new FileInfo(f).Length; } catch (IOException) { continue; }
                    if (size > 1 << 20) continue;
                    if (list.Count >= maxFiles || bytes + size > maxBytes) { cut = true; break; }
                    try { list.Add(new File(rel, System.IO.File.ReadAllText(f))); bytes += size; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return new Files(list, cut);
    }

    static bool IsUnder(string root, string path)
    {
        string r = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One package's types, for a project whose packages' types did not all fit: the .d.ts files of node_modules/<paramref name="name"/> and of
    /// its @types package, at most <paramref name="maxBytes"/>. A name that is no package's (a path, "..") gives none.
    /// </summary>
    public static Files Package(string root, string name, long maxBytes = 12 << 20)
    {
        var list = new List<File>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^(@[\w.-]+/)?[\w.-]+$") || name.Contains("..")) return new Files(list, false);
        long bytes = 0;
        bool cut = false;
        string types = "@types/" + (name.StartsWith('@') ? name[1..].Replace('/', '_') : name);
        foreach (var n in new[] { name, types })
        {
            string dir = System.IO.Path.Combine(root, "node_modules", n.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            foreach (var f in DeclarationFiles(dir))
            {
                long size;
                try { size = new FileInfo(f).Length; } catch (IOException) { continue; }
                if (size > 4 << 20) continue;
                if (bytes + size > maxBytes) { cut = true; break; }
                try { list.Add(new File(Rel(root, f), System.IO.File.ReadAllText(f))); bytes += size; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return new Files(list, cut);
    }

    /// <summary>
    /// Every tsconfig and jsconfig of the project (tsconfig.json, tsconfig.app.json, a package's own in a monorepo), each as
    /// <see cref="ConfigOf"/> gives it: the page uses the one that covers the file in the editor.
    /// </summary>
    public static List<JsonObject> Configs(string root, int max = 64)
    {
        var found = new List<JsonObject>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && found.Count < max)
        {
            var (dir, depth) = queue.Dequeue();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir, "*.json"); dirs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
                if (found.Count < max && System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(f), @"^(tsconfig|jsconfig)(\.[\w.-]+)?\.json$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    found.Add(ConfigOf(root, f));
            if (depth >= 6) continue;
            foreach (var d in dirs.OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileName(d);
                if (name.StartsWith('.') || SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                queue.Enqueue((d, depth + 1));
            }
        }
        return found;
    }

    /// <summary>
    /// The packages' types: the package.json of every package of the root's node_modules (small; module resolution reads their "types",
    /// "exports" and "typesVersions"), and the .d.ts files of the packages depended on, of every @types package, and of what they depend on.
    /// </summary>
    public static Files Packages(string root, long maxBytes = 48 << 20)
    {
        string modules = System.IO.Path.Combine(root, "node_modules");
        var list = new List<File>();
        if (!Directory.Exists(modules)) return new Files(list, false);
        long bytes = 0;
        bool cut = false;
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);   // package name -> its folder
        try
        {
            foreach (var d in Directory.EnumerateDirectories(modules))
            {
                string name = System.IO.Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                if (name.StartsWith('@')) foreach (var s in Directory.EnumerateDirectories(d)) dirs[name + "/" + System.IO.Path.GetFileName(s)] = s;
                else dirs[name] = d;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Files(list, false); }

        var manifests = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, dir) in dirs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string pj = System.IO.Path.Combine(dir, "package.json");
            try
            {
                if (!System.IO.File.Exists(pj) || new FileInfo(pj).Length > 512 << 10) continue;
                string text = System.IO.File.ReadAllText(pj);
                list.Add(new File(Rel(root, pj), text));
                bytes += text.Length;
                try { manifests[name] = JsonNode.Parse(text, documentOptions: Loose); } catch (JsonException) { manifests[name] = null; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // whose .d.ts files: the project's dependencies, every @types package, then what those depend on (their types import them)
        var wanted = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Want(string name) { if (dirs.ContainsKey(name) && seen.Add(name)) wanted.Enqueue(name); }
        static IEnumerable<string> DepsOf(JsonNode? manifest) =>
            new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" }.SelectMany(k => (manifest?[k] as JsonObject)?.Select(kv => kv.Key) ?? Enumerable.Empty<string>());
        JsonNode? own = null;
        try { if (System.IO.File.Exists(System.IO.Path.Combine(root, "package.json"))) own = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "package.json")), documentOptions: Loose); }
        catch (Exception e) when (e is IOException or JsonException) { }
        foreach (var d in DepsOf(own)) { Want(d); Want("@types/" + (d.StartsWith('@') ? d[1..].Replace('/', '_') : d)); }
        foreach (var t in dirs.Keys.Where(k => k.StartsWith("@types/", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal)) Want(t);
        while (wanted.Count > 0 && !cut)
        {
            string name = wanted.Dequeue();
            foreach (var f in DeclarationFiles(dirs[name]))
            {
                long size;
                try { size = new FileInfo(f).Length; } catch (IOException) { continue; }
                if (size > 4 << 20) continue;
                if (bytes + size > maxBytes) { cut = true; break; }
                try { list.Add(new File(Rel(root, f), System.IO.File.ReadAllText(f))); bytes += size; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            foreach (var d in DepsOf(manifests.GetValueOrDefault(name))) Want(d);
        }
        return new Files(list, cut);
    }

    /// <summary>A package's .d.ts (and .d.mts, .d.cts) files, not those of a node_modules inside it or of its tests.</summary>
    static IEnumerable<string> DeclarationFiles(string dir)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((dir, 0));
        while (stack.Count > 0)
        {
            var (d, depth) = stack.Pop();
            string[] files, subs;
            try { files = Directory.GetFiles(d); subs = Directory.GetDirectories(d); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
                if (f.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            if (depth >= 12) continue;
            foreach (var s in subs.OrderByDescending(x => x, StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileName(s);
                // (ts4.8/, ts5.6/: a package's types for older TypeScripts, which would declare its globals a second time)
                if (name is "node_modules" or "test" or "tests" or "__tests__" or ".git" || name.StartsWith('.') || System.Text.RegularExpressions.Regex.IsMatch(name, @"^ts\d+\.\d+$")) continue;
                stack.Push((s, depth + 1));
            }
        }
    }

    /// <summary>
    /// The project's tsconfig.json (else jsconfig.json): { path, compilerOptions } with what it extends merged under it (a relative file or a
    /// package's, as "@tsconfig/node20/tsconfig.json"; its own options win), or null when there is none. Comments and trailing commas are taken.
    /// </summary>
    public static JsonObject? Config(string root)
    {
        string? file = new[] { "tsconfig.json", "jsconfig.json" }.Select(f => System.IO.Path.Combine(root, f)).FirstOrDefault(System.IO.File.Exists);
        return file == null ? null : ConfigOf(root, file);
    }

    /// <summary>
    /// One config file: { path, dir, compilerOptions, include, exclude, files, references } with what it extends merged under it. include,
    /// exclude and files (its own, else the ones of what it extends) are written from the project's folder; references are the configs it
    /// names (a solution's tsconfig.json: "files": [] and references to tsconfig.app.json and tsconfig.node.json).
    /// </summary>
    public static JsonObject ConfigOf(string root, string file)
    {
        var options = new JsonObject();
        var lists = new Dictionary<string, JsonArray>();
        JsonArray Under(string dir, JsonArray patterns)
        {
            string at = Rel(root, dir);
            var result = new JsonArray();
            foreach (var p in patterns)
                if (p is JsonValue pv && pv.TryGetValue<string>(out var text))
                    result.Add(Rel(root, System.IO.Path.GetFullPath(System.IO.Path.Combine(root, at == "." ? "" : at, text.Replace("${configDir}", ".")))));
            return result;
        }
        string? error = null;
        var chain = new List<string>();
        void Read(string path, int depth)
        {
            if (depth > 8 || chain.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            chain.Add(path);
            JsonNode? doc;
            try { doc = JsonNode.Parse(System.IO.File.ReadAllText(path), documentOptions: Loose); }
            catch (Exception e) when (e is IOException or JsonException) { error ??= $"{Rel(root, path)}: {e.Message}"; return; }
            // what it extends first (one or several), then its own options over them
            var bases = doc?["extends"] switch { JsonArray a => a.Select(x => x?.GetValue<string>()).OfType<string>().ToList(), JsonValue v when v.TryGetValue<string>(out var s) => new List<string> { s }, _ => new List<string>() };
            foreach (var b in bases)
                if (Resolve(System.IO.Path.GetDirectoryName(path)!, b) is { } basePath) Read(basePath, depth + 1);
                else error ??= $"{Rel(root, path)} extends {b}, which is not there";
            if (doc?["compilerOptions"] is JsonObject own)
            {
                string dir = System.IO.Path.GetDirectoryName(path)!;
                foreach (var (k, v) in own)
                {
                    // the paths of a base config are its own folder's: written relative to the project
                    var value = v?.DeepClone();
                    if (k is "baseUrl" && value is JsonValue bv && bv.TryGetValue<string>(out var bu)) value = Rel(root, System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, bu)));
                    // rootDirs (SvelteKit's: the project and .svelte-kit/types, from node_modules/$app/tsconfig.json): each one the project's path
                    if (k is "rootDirs" && value is JsonArray dirs)
                        value = new JsonArray(dirs.Select(d => d is JsonValue dv && dv.TryGetValue<string>(out var ds) ? (JsonNode)JsonValue.Create(Rel(root, System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, ds)))) : null).Where(d => d != null).ToArray());
                    options[k] = value;
                }
                if (own["paths"] != null && own["baseUrl"] == null && options["baseUrl"] == null) options["pathsBase"] = Rel(root, dir);
            }
            // which files: its own lists, over those of what it extends
            foreach (var key in new[] { "include", "exclude", "files" })
                if (doc?[key] is JsonArray patterns) lists[key] = Under(System.IO.Path.GetDirectoryName(path)!, patterns);
        }
        Read(file, 0);
        string folder = Rel(root, System.IO.Path.GetDirectoryName(file)!);
        var result = new JsonObject { ["path"] = Rel(root, file), ["dir"] = folder == "." ? "" : folder, ["compilerOptions"] = options };
        foreach (var (key, patterns) in lists) result[key] = patterns;
        // the configs it refers to: a folder's tsconfig.json, or the file named
        try
        {
            if (JsonNode.Parse(System.IO.File.ReadAllText(file), documentOptions: Loose)?["references"] is JsonArray refs)
            {
                var named = new JsonArray();
                foreach (var r in refs)
                {
                    if (r?["path"] is not JsonValue rv || !rv.TryGetValue<string>(out var to)) continue;
                    string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, to));
                    if (Directory.Exists(full)) full = System.IO.Path.Combine(full, "tsconfig.json");
                    else if (!System.IO.File.Exists(full) && System.IO.File.Exists(full + ".json")) full += ".json";
                    named.Add(Rel(root, full));
                }
                result["references"] = named;
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { /* told above */ }
        if (error != null) result["error"] = error;
        return result;
    }

    static string? Resolve(string dir, string spec)
    {
        if (spec.StartsWith('.') || System.IO.Path.IsPathRooted(spec))
        {
            string p = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, spec));
            return System.IO.File.Exists(p) ? p : System.IO.File.Exists(p + ".json") ? p + ".json" : null;
        }
        // a package's: node_modules/<spec>, or its tsconfig.json
        for (string? d = dir; d != null; d = System.IO.Path.GetDirectoryName(d))
        {
            string p = System.IO.Path.Combine(d, "node_modules", spec.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(p)) return p;
            if (System.IO.File.Exists(p + ".json")) return p + ".json";
            if (System.IO.File.Exists(System.IO.Path.Combine(p, "tsconfig.json"))) return System.IO.Path.Combine(p, "tsconfig.json");
        }
        return null;
    }

    static string Rel(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
}
