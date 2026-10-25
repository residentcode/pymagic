using System.Runtime.CompilerServices;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Which open documents are affected when another one changes (the importers, transitively).</summary>
public sealed partial class LanguageService
{
    readonly ConditionalWeakTable<ModuleInfo, string[]> _imports = new();

    /// <summary>
    /// Every module name a module may depend on: for <c>import a.b.c</c> the names a, a.b and a.b.c; for
    /// <c>from x import y</c> the module x and the possible submodule x.y. Over-approximate on purpose.
    /// </summary>
    string[] ImportedNames(ModuleInfo m) => _imports.GetValue(m, ComputeImports);

    static string[] ComputeImports(ModuleInfo m) => ComputeImports(m.Ast, m.Package);

    /// <summary>The names a module may depend on, from its syntax tree and the package its relative imports start from.</summary>
    internal static string[] ComputeImports(Module ast, string package)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void AddPrefixes(string full)
        {
            if (full.Length == 0) return;
            var parts = full.Split('.');
            for (int i = 1; i <= parts.Length; i++) set.Add(string.Join('.', parts.Take(i)));
        }

        foreach (var n in AstChildren.Descendants(ast))
        {
            switch (n)
            {
                case ImportStmt s:
                    foreach (var a in s.Names) AddPrefixes(a.Name);
                    break;
                case ImportFromStmt f:
                {
                    string? abs = Workspace.AbsoluteName(package, f.Level, f.Module);
                    if (abs == null) break;
                    AddPrefixes(abs);
                    foreach (var a in f.Names) AddPrefixes(abs.Length == 0 ? a.Name : abs + "." + a.Name);
                    break;
                }
            }
        }
        return set.ToArray();
    }

    /// <summary>
    /// The open documents (other than the given ones) whose diagnostics can change because one of the given documents
    /// changed. Importers are followed through files that are not open as well, since a chain a -> b -> c still lets an
    /// edit of c reach a.
    /// </summary>
    public IReadOnlyList<string> DependentsOf(IEnumerable<string> uris)
    {
        var changed = uris.Where(u => _docs.ContainsKey(u)).ToList();
        if (changed.Count == 0 || _docs.Count < 2) return Array.Empty<string>();
        if (_index is { Ready: true }) return DependentsFromIndex(changed);

        var snap = Full();
        var importers = new Dictionary<string, List<ModuleInfo>>(StringComparer.Ordinal);
        foreach (var m in snap.Project)
            foreach (var name in ImportedNames(m))
            {
                if (!importers.TryGetValue(name, out var list)) importers[name] = list = new List<ModuleInfo>();
                list.Add(m);
            }

        var visited = new HashSet<ModuleInfo>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<ModuleInfo>();
        foreach (var u in changed)
            if (snap.ByUri.TryGetValue(u, out var m) && visited.Add(m)) queue.Enqueue(m);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!importers.TryGetValue(current.Name, out var list)) continue;
            foreach (var importer in list)
                if (visited.Add(importer)) queue.Enqueue(importer);
        }

        var result = new List<string>();
        foreach (var m in visited)
            if (snap.UriOf.TryGetValue(m, out var uri) && _docs.ContainsKey(uri) && !changed.Contains(uri)) result.Add(uri);
        return result;
    }

    /// <summary>
    /// The same answer without reading the project: the importers of files nobody has open come from the symbol index (which keeps the names
    /// every file imports, updated as files change), the open documents are asked directly.
    /// </summary>
    IReadOnlyList<string> DependentsFromIndex(List<string> changed)
    {
        var snap = Current();
        var openByName = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
        var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _docs.Values)
        {
            if (d.Path != null) openPaths.Add(System.IO.Path.GetFullPath(d.Path));
            if (snap.ByUri.TryGetValue(d.Uri, out var om)) openByName[d.Uri] = om;
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var u in changed)
            if (snap.ByUri.TryGetValue(u, out var m) && seenNames.Add(m.Name)) queue.Enqueue(m.Name);

        var reached = new HashSet<string>(StringComparer.Ordinal);   // uris of open documents
        while (queue.Count > 0)
        {
            string name = queue.Dequeue();
            // open documents import it?
            foreach (var (uri, m) in openByName)
                if (ImportedNames(m).Contains(name) && reached.Add(uri) && seenNames.Add(m.Name)) queue.Enqueue(m.Name);
            // files that are not open
            foreach (var (path, moduleName) in _index!.ImportersOf(name))
                if (!openPaths.Contains(path) && seenNames.Add(moduleName)) queue.Enqueue(moduleName);
        }
        foreach (var u in changed) reached.Remove(u);
        return reached.ToList();
    }
}
