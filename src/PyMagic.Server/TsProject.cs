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
        void Walk(string dir, int depth)
        {
            if (depth > 20 || cut) return;
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal).ToList(); dirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var f in files)
            {
                string name = System.IO.Path.GetFileName(f);
                if (!CodeExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || name.Contains(".min.", StringComparison.OrdinalIgnoreCase)) continue;
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
                Walk(d, depth + 1);
            }
        }
        Walk(root, 0);
        return new Files(list, cut);
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
        if (file == null) return null;
        var options = new JsonObject();
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
                    options[k] = value;
                }
                if (own["paths"] != null && own["baseUrl"] == null && options["baseUrl"] == null) options["pathsBase"] = Rel(root, dir);
            }
        }
        Read(file, 0);
        var result = new JsonObject { ["path"] = Rel(root, file), ["compilerOptions"] = options };
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
