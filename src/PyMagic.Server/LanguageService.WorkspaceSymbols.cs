namespace PyMagic.Server;

public sealed record SymbolInformation(string Name, int Kind, Location Location, string? ContainerName);

public sealed partial class LanguageService
{
    /// <summary>Functions, classes, methods and variables of the open documents whose name matches the query (every letter of it, in order).</summary>
    public IReadOnlyList<SymbolInformation> WorkspaceSymbols(string query, int limit = 200)
    {
        query = (query ?? "").Trim();
        var scored = new List<(int Score, SymbolInformation Info)>();
        foreach (var uri in _docs.Keys.OrderBy(u => u, StringComparer.Ordinal))
        {
            var m = ModuleFor(uri);
            if (m == null) continue;
            void Visit(IEnumerable<DocumentSymbol> list, string container)
            {
                foreach (var s in list)
                {
                    int score = Score(s.Name, query);
                    if (score >= 0) scored.Add((score, new SymbolInformation(s.Name, s.Kind, new Location(uri, s.SelectionRange), container)));
                    if (s.Kind == SymbolKind.Class) Visit(s.Children, container + "." + s.Name);
                }
            }
            Visit(DocumentSymbols(uri), m.Name);
        }
        // the files nobody has open come from the index (built in the background, kept up to date by the file watcher)
        if (_index != null)
        {
            var open = new HashSet<string>(_docs.Values.Where(d => d.Path != null).Select(d => System.IO.Path.GetFullPath(d.Path!)), StringComparer.OrdinalIgnoreCase);
            foreach (var (score, sym) in _index.Query(query, open))
            {
                var at = new Position(sym.Line, sym.Character);
                scored.Add((score, new SymbolInformation(sym.Name, sym.Kind, new Location(UriOfPath(sym.Path), new Range(at, new Position(sym.Line, sym.Character + sym.Name.Length))), sym.Container)));
            }
        }
        return scored.OrderBy(x => x.Score).ThenBy(x => x.Info.Name.Length).ThenBy(x => x.Info.Name, StringComparer.Ordinal)
            .Take(limit).Select(x => x.Info).ToList();
    }

    SymbolIndex? _index;

    /// <summary>The symbol index of the project, or null before <see cref="StartIndexing"/>.</summary>
    public SymbolIndex? Index => _index;

    /// <summary>Indexes the names of every file of the project in the background and keeps the index up to date (go to symbol finds files nobody has opened). The index is saved in <c>.pymagic/index.json</c>.</summary>
    public Task? StartIndexing()
    {
        if (_index != null || SourceRoots.Count == 0) return null;
        var roots = SourceRoots.Select(System.IO.Path.GetFullPath).ToArray();
        string ModuleOf(string path)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            string full = System.IO.Path.GetFullPath(path);
            foreach (var root in roots)
            {
                string r = root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = full[r.Length..].Split('\\', '/');
                parts[^1] = stem;
                if (stem == "__init__") parts = parts[..^1];
                if (parts.Length > 0 && parts.All(p => p.Length > 0)) return string.Join('.', parts);
            }
            return stem;
        }
        _index = new SymbolIndex(ProjectFiles, ModuleOf, System.IO.Path.Combine(roots[0], ".pymagic", "index.json"));
        return _index.Start(roots);
    }

    /// <summary>Lower is better; -1 when the letters of the query do not appear in order.</summary>
    internal static int Score(string name, string query)
    {
        if (query.Length == 0) return 100;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        int at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return 2 + (at > 0 ? 1 : 0);
        int i = 0;
        foreach (char c in name)
            if (i < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[i])) i++;
        return i == query.Length ? 10 : -1;
    }
}
