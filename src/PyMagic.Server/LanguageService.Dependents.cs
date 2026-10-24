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

    static string[] ComputeImports(ModuleInfo m)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void AddPrefixes(string full)
        {
            if (full.Length == 0) return;
            var parts = full.Split('.');
            for (int i = 1; i <= parts.Length; i++) set.Add(string.Join('.', parts.Take(i)));
        }

        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            switch (n)
            {
                case ImportStmt s:
                    foreach (var a in s.Names) AddPrefixes(a.Name);
                    break;
                case ImportFromStmt f:
                {
                    string? abs = Workspace.AbsoluteName(m, f.Level, f.Module);
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
}
