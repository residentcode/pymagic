using PyMagic.Core.Syntax;

namespace PyMagic.Server;

/// <summary>A file, class, function or method that can be run. Ids are what the runner understands: <c>rel/path.py::Class::method</c>.</summary>
public sealed record TestNode(string Id, string Name, string Kind, string File, int Line, int EndLine, List<TestNode> Children);

/// <summary>
/// Finds tests without importing anything: parses the candidate files and applies pytest's and unittest's naming rules
/// (functions and methods starting with <c>test</c>, classes named <c>Test*</c> without <c>__init__</c>, and any class
/// that derives from a <c>TestCase</c>, including through base classes of the same file or imported from other project files).
/// </summary>
public static class TestDiscovery
{
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "__pycache__", "node_modules", ".venv", "venv", ".conda", "env", ".tox", ".mypy_cache", "bin", "obj", "site-packages", "dist", "build",
    };

    public static bool IsTestFile(string fileName)
    {
        if (!fileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileNameWithoutExtension(fileName);
        return name.StartsWith("test", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Test files under the root (sorted), each with its tests; files without tests are left out.</summary>
    public static List<TestNode> Discover(string root)
    {
        var result = new List<TestNode>();
        var stack = new Stack<string>();
        stack.Push(root);
        var files = new List<string>();
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.py").Where(f => IsTestFile(Path.GetFileName(f))));
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (!SkippedDirectories.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.') && !ProjectFolders.IsExcluded(d)) stack.Push(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Read(string rel)
        {
            if (texts.TryGetValue(rel, out var known)) return known;
            try { return texts[rel] = File.ReadAllText(Path.Combine(root, rel)); }
            catch (IOException) { return texts[rel] = null; }
            catch (UnauthorizedAccessException) { return texts[rel] = null; }
        }
        var modules = new Dictionary<string, FileClasses?>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Read(rel) is not { } text) continue;
            var node = DiscoverFile(rel, text, Read, modules);
            if (node != null) result.Add(node);
        }
        return result;
    }

    /// <summary>A parsed file: its classes by name and the names its <c>from ... import</c> statements and <c>import</c>s bind.</summary>
    sealed class FileClasses
    {
        public required string Rel;
        public required Dictionary<string, ClassDef> Classes;
        public required Dictionary<string, (string? Module, int Level, string Name)> FromImports;   // local name -> where it comes from
        public required Dictionary<string, string> ModuleImports;                                  // local name -> dotted module
    }

    static FileClasses ModuleOf(string rel, Module parsed)
    {
        var from = new Dictionary<string, (string?, int, string)>(StringComparer.Ordinal);
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var st in parsed.Body)
        {
            if (st is ImportFromStmt f && !f.IsStar)
                foreach (var a in f.Names) from[a.AsName ?? a.Name] = (f.Module, f.Level, a.Name);
            else if (st is ImportStmt i)
                foreach (var a in i.Names) imports[a.AsName ?? a.Name] = a.Name;
        }
        return new FileClasses
        {
            Rel = rel, FromImports = from, ModuleImports = imports,
            Classes = parsed.Body.OfType<ClassDef>().GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Last()),
        };
    }

    /// <summary>The project file of a module an import names: relative to the importing file's folder for <c>from .base</c>,
    /// else from the root or from that folder (pytest puts a test file's folder on the path).</summary>
    static IEnumerable<string> FilesOf(string importer, string? module, int level)
    {
        string folder = Path.GetDirectoryName(importer)?.Replace('\\', '/') ?? "";
        for (int i = 1; i < level && folder.Length > 0; i++) folder = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "";
        string path = (module ?? "").Replace('.', '/');
        var bases = level > 0 ? new[] { folder } : new[] { "", folder };
        foreach (var b in bases.Distinct())
        {
            string stem = string.Join("/", new[] { b, path }.Where(p => p.Length > 0));
            if (stem.Length == 0) continue;
            yield return stem + ".py";
            yield return stem + "/__init__.py";
        }
    }

    public static TestNode? DiscoverFile(string rel, string text) => DiscoverFile(rel, text, null, new());

    /// <param name="read">The text of another project file by its path from the root, for base classes imported from it.</param>
    static TestNode? DiscoverFile(string rel, string text, Func<string, string?>? read, Dictionary<string, FileClasses?> modules)
    {
        var parsed = Parser.Parse(text);
        var lines = new LineMap(text);
        int LineOf(int offset) => lines.PositionOf(offset).Line + 1;
        var own = ModuleOf(rel, parsed.Module);
        modules[rel] = own;

        FileClasses? Load(string file)
        {
            if (modules.TryGetValue(file, out var known)) return known;
            modules[file] = null;   // while it is read: an import cycle finds nothing
            return read?.Invoke(file) is { } t ? modules[file] = ModuleOf(file, Parser.Parse(t).Module) : null;
        }

        // the class a base expression names: one of the same file, one imported by name, or `module.Class` of an imported module
        (FileClasses, ClassDef)? Resolve(FileClasses m, Expr e, int depth)
        {
            if (depth > 8) return null;
            if (e is NameExpr n)
            {
                if (m.Classes.TryGetValue(n.Name, out var local)) return (m, local);
                if (m.FromImports.TryGetValue(n.Name, out var from))
                    foreach (var file in FilesOf(m.Rel, from.Module, from.Level))
                        if (Load(file) is { } other)
                        {
                            if (other.Classes.TryGetValue(from.Name, out var cls)) return (other, cls);
                            // re-exported by that module (a package's __init__.py importing it from a module of its own)
                            if (other.FromImports.ContainsKey(from.Name)) return Resolve(other, new NameExpr(from.Name), depth + 1);
                        }
                return null;
            }
            if (e is AttributeExpr { Value: NameExpr owner } a && m.ModuleImports.TryGetValue(owner.Name, out var dotted))
                foreach (var file in FilesOf(m.Rel, dotted, 0))
                    if (Load(file) is { } other && other.Classes.TryGetValue(a.Attr, out var cls)) return (other, cls);
            return null;
        }

        var cache = new Dictionary<ClassDef, bool>(ReferenceEqualityComparer.Instance);

        bool IsCase(FileClasses m, ClassDef c, int depth = 0)
        {
            if (cache.TryGetValue(c, out var known)) return known;
            if (depth > 8) return false;
            cache[c] = false;
            bool r = false;
            foreach (var b in c.Bases.Where(a => a.Name == null))
            {
                string? name = b.Value switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
                if (name == null) continue;
                if (name.EndsWith("TestCase", StringComparison.Ordinal)) { r = true; break; }
                if (Resolve(m, b.Value, depth) is var (bm, bc) && !ReferenceEquals(bc, c) && IsCase(bm, bc, depth + 1)) { r = true; break; }
            }
            return cache[c] = r;
        }

        // the methods of a class and of its bases, the class's own first; with the file each is written in
        IEnumerable<(FunctionDef Method, FileClasses Where)> MethodsOf(FileClasses m, ClassDef c, HashSet<string>? seen = null, int depth = 0)
        {
            seen ??= new HashSet<string>();
            foreach (var f in c.Body.OfType<FunctionDef>())
                if (seen.Add(f.Name)) yield return (f, m);
            if (depth > 8) yield break;
            foreach (var b in c.Bases.Where(a => a.Name == null))
                if (Resolve(m, b.Value, depth) is var (bm, bc) && !ReferenceEquals(bc, c))
                    foreach (var found in MethodsOf(bm, bc, seen, depth + 1))
                        yield return found;
        }

        bool HasInit(ClassDef c) => c.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__");

        var children = new List<TestNode>();
        foreach (var st in parsed.Module.Body)
        {
            switch (st)
            {
                case FunctionDef f when f.Name.StartsWith("test", StringComparison.Ordinal):
                    children.Add(new TestNode($"{rel}::{f.Name}", f.Name, "function", rel, LineOf(f.NameStart), LineOf(f.End), new()));
                    break;
                case ClassDef c when IsCase(own, c) || (c.Name.StartsWith("Test", StringComparison.Ordinal) && !HasInit(c)):
                {
                    // a method written in another file (an imported base class) is placed on the class's own line, after those of this file
                    var methods = MethodsOf(own, c).Where(m => m.Method.Name.StartsWith("test", StringComparison.Ordinal))
                        .OrderBy(m => m.Where == own ? 0 : 1).ThenBy(m => m.Where == own ? m.Method.NameStart : 0).ToList();
                    if (methods.Count == 0) break;
                    var kids = methods.Select(m => m.Where == own
                        ? new TestNode($"{rel}::{c.Name}::{m.Method.Name}", m.Method.Name, "method", rel, LineOf(m.Method.NameStart), LineOf(m.Method.End), new())
                        : new TestNode($"{rel}::{c.Name}::{m.Method.Name}", m.Method.Name, "method", rel, LineOf(c.NameStart), LineOf(c.NameStart), new())).ToList();
                    children.Add(new TestNode($"{rel}::{c.Name}", c.Name, "class", rel, LineOf(c.NameStart), LineOf(c.End), kids));
                    break;
                }
            }
        }
        return children.Count == 0 ? null : new TestNode(rel, rel, "file", rel, 1, LineOf(text.Length), children);
    }
}
