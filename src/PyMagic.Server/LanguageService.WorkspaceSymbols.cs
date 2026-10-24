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
        return scored.OrderBy(x => x.Score).ThenBy(x => x.Info.Name.Length).ThenBy(x => x.Info.Name, StringComparer.Ordinal)
            .Take(limit).Select(x => x.Info).ToList();
    }

    /// <summary>Lower is better; -1 when the letters of the query do not appear in order.</summary>
    static int Score(string name, string query)
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
