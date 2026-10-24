using PyMagic.Core.Syntax;

namespace PyMagic.Server;

/// <summary>A file, class, function or method that can be run. Ids are what the runner understands: <c>rel/path.py::Class::method</c>.</summary>
public sealed record TestNode(string Id, string Name, string Kind, string File, int Line, int EndLine, List<TestNode> Children);

/// <summary>
/// Finds tests without importing anything: parses the candidate files and applies pytest's and unittest's naming rules
/// (functions and methods starting with <c>test</c>, classes named <c>Test*</c> without <c>__init__</c>, and any class
/// that derives from a <c>TestCase</c>, including through base classes of the same file).
/// </summary>
public static class TestDiscovery
{
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "__pycache__", "node_modules", ".venv", "venv", "env", ".tox", ".mypy_cache", "bin", "obj", "site-packages", "dist", "build",
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
                    if (!SkippedDirectories.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.')) stack.Push(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var node = DiscoverFile(rel, text);
            if (node != null) result.Add(node);
        }
        return result;
    }

    public static TestNode? DiscoverFile(string rel, string text)
    {
        var parsed = Parser.Parse(text);
        var lines = new LineMap(text);
        int LineOf(int offset) => lines.PositionOf(offset).Line + 1;

        var classes = parsed.Module.Body.OfType<ClassDef>().ToDictionary(c => c.Name, c => c);
        var cache = new Dictionary<string, bool>();

        bool IsCase(ClassDef c, int depth = 0)
        {
            if (cache.TryGetValue(c.Name, out var known)) return known;
            if (depth > 8) return false;
            bool r = false;
            foreach (var b in c.Bases.Where(a => a.Name == null))
            {
                string? name = b.Value switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
                if (name == null) continue;
                if (name.EndsWith("TestCase", StringComparison.Ordinal)) { r = true; break; }
                if (classes.TryGetValue(name, out var local) && !ReferenceEquals(local, c) && IsCase(local, depth + 1)) { r = true; break; }
            }
            return cache[c.Name] = r;
        }

        IEnumerable<FunctionDef> MethodsOf(ClassDef c, int depth = 0)
        {
            var seen = new HashSet<string>();
            foreach (var m in c.Body.OfType<FunctionDef>())
                if (seen.Add(m.Name)) yield return m;
            if (depth > 8) yield break;
            foreach (var b in c.Bases.Where(a => a.Name == null))
                if (b.Value is NameExpr n && classes.TryGetValue(n.Name, out var baseClass) && !ReferenceEquals(baseClass, c))
                    foreach (var m in MethodsOf(baseClass, depth + 1))
                        if (seen.Add(m.Name)) yield return m;
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
                case ClassDef c when IsCase(c) || (c.Name.StartsWith("Test", StringComparison.Ordinal) && !HasInit(c)):
                {
                    var methods = MethodsOf(c).Where(m => m.Name.StartsWith("test", StringComparison.Ordinal))
                        .OrderBy(m => m.NameStart).ToList();
                    if (methods.Count == 0) break;
                    var kids = methods.Select(m => new TestNode($"{rel}::{c.Name}::{m.Name}", m.Name, "method", rel,
                        LineOf(m.NameStart), LineOf(m.End), new())).ToList();
                    children.Add(new TestNode($"{rel}::{c.Name}", c.Name, "class", rel, LineOf(c.NameStart), LineOf(c.End), kids));
                    break;
                }
            }
        }
        return children.Count == 0 ? null : new TestNode(rel, rel, "file", rel, 1, LineOf(text.Length), children);
    }
}
