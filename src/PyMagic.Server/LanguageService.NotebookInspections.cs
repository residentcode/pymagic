using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// The notebook's own check: the page hands the language server a notebook's code cells as one file (.pymagic/notebooks/...), top to bottom.
/// A name a cell reads that only a later cell (or a later line) defines works while the cells are run out of order, and stops with NameError
/// when the notebook is run from the top (Run All, a fresh kernel, nbconvert). Uses inside functions are left alone: they run when called.
/// When the page also sends the cells' run counts (Jupyter's execution_count), the cells are taken in the order they ran, as the kernel saw
/// them: a name defined in a cell that ran earlier is defined, even when that cell sits lower. Cells never run keep their place in the file.
/// </summary>
public sealed partial class LanguageService
{
    static bool IsNotebookFile(string? uri) => uri != null && uri.Contains("/.pymagic/notebooks/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The code cells of each notebook file the page sent them for: the first line in the file, the lines, and the run count (null: never run).</summary>
    readonly ConcurrentDictionary<string, (int Line, int Lines, int? Count)[]> _notebookCells = new(StringComparer.Ordinal);

    /// <summary>
    /// The cells of a notebook file as the page sends them with its text (didOpen / didChange's `notebookCells`: [{start, lines, count}], in
    /// the order of the file); nothing (an older page, an ordinary file) forgets them, and the cells are then read from the top.
    /// </summary>
    public void SetNotebookCells(string uri, JsonNode? cells)
    {
        if (cells is not JsonArray list || !IsNotebookFile(uri)) { _notebookCells.TryRemove(uri, out _); return; }
        var parsed = new List<(int, int, int?)>();
        foreach (var c in list)
        {
            if (c?["start"] is not JsonValue start || c["lines"] is not JsonValue lines) continue;
            int? count = c["count"] is JsonValue v && v.TryGetValue(out int n) ? n : null;
            parsed.Add((start.GetValue<int>(), lines.GetValue<int>(), count));
        }
        _notebookCells[uri] = parsed.ToArray();
    }

    /// <summary>
    /// The place of each cell in the run order (by the cells' order in the file): the cells that ran take the places of the cells that ran,
    /// by their run counts (equal counts in the order of the file); a cell never run keeps its own place. Null when no cell has a count.
    /// </summary>
    public static int[]? RunOrder(IReadOnlyList<int?> counts)
    {
        if (counts.All(c => c == null)) return null;
        var ran = Enumerable.Range(0, counts.Count).Where(i => counts[i] != null).ToList();
        var byCount = ran.OrderBy(i => counts[i]).ToList();   // OrderBy is stable: ties stay in the order of the file
        var place = Enumerable.Range(0, counts.Count).ToArray();
        // the k-th cell to run sits in the k-th slot of the ones that ran
        for (int k = 0; k < ran.Count; k++) place[byCount[k]] = ran[k];
        return place;
    }

    IEnumerable<Diag> NotebookDiagnostics(ModuleInfo m, string? uri)
    {
        if (!IsNotebookFile(uri) || !Plugins.On("notebooks")) yield break;
        var module = m.Semantics.ModuleScope;
        if (module.HasStarImport) yield break;
        // a name a function makes global may be bound by calling it, before its first binding at the top
        var global = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new Stack<Scope>(module.Children);
        while (scopes.Count > 0)
        {
            var s = scopes.Pop();
            foreach (var x in s.Symbols.Values) if (x.Redirect != null) global.Add(x.Name);
            foreach (var c in s.Children) scopes.Push(c);
        }
        // by the run counts: the cell of each line, and the place of each cell in the run order
        int[]? place = null;
        Func<int, int> cellAt = _ => 0;
        if (uri != null && _notebookCells.TryGetValue(uri, out var cells) && RunOrder(cells.Select(c => c.Count).ToList()) is { } order)
        {
            var lines = new LineMap(m.Source);
            var cellOf = new int[lines.LineCount];
            for (int line = 0, c = 0; line < cellOf.Length; line++)
            {
                while (c + 1 < cells.Length && cells[c + 1].Line <= line) c++;
                cellOf[line] = c;
            }
            place = order;
            cellAt = offset => cellOf[Math.Min(lines.PositionOf(offset).Line, cellOf.Length - 1)];
        }
        foreach (var r in m.Semantics.References)
        {
            if ((r.Access & Access.Read) == 0 || r.Symbol is not { } sym || !ReferenceEquals(sym.Scope, module) || !RunsAtOnce(r.UseScope)) continue;
            if (sym.Declarations.Count == 0 || global.Contains(sym.Name)) continue;
            string message;
            if (place == null)
            {
                int first = sym.Declarations.Min(d => d.Node.Start);
                // `x += 1` and `for x in ...` read after their own binding has begun: the binding counts from where the statement starts
                if (first <= r.Node.Start) continue;
                message = $"'{r.Node.Name}' is defined only further down (a later cell): run from the top, the notebook stops here with NameError";
            }
            else
            {
                // a binding in a cell that ran before, or further up in the same cell
                int at = cellAt(r.Node.Start);
                bool sameCell = false, before = false;
                foreach (var d in sym.Declarations)
                {
                    int dc = cellAt(d.Node.Start);
                    if (dc == at) { sameCell = true; if (d.Node.Start <= r.Node.Start) before = true; }
                    else if (place[dc] < place[at]) before = true;
                }
                if (before) continue;
                message = sameCell
                    ? $"'{r.Node.Name}' is defined only further down in this cell: run, it stops here with NameError"
                    : $"'{r.Node.Name}' is defined only in a cell that runs after this one (by the cells' run counts): run in that order, the notebook stops here with NameError";
            }
            var f = new Finding(r.Node.Start, r.Node.End - r.Node.Start, message, "notebook-cell-order", FindingLevel.Warning);
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }
        foreach (var d in NotebookRedeclarations(m, uri, global)) yield return d;
    }

    /// <summary>Loop counters PyCharm leaves alone when another cell binds them again.</summary>
    static readonly HashSet<string> NotebookLoopNames = new(StringComparer.Ordinal) { "i", "j", "k", "_" };

    /// <summary>
    /// PyCharm's *Global variable redeclaration in notebook*: a global assigned in one cell and assigned again in another. Which value it holds
    /// then depends on the order the cells were run in. Each assignment in a cell after the first one's is marked; functions, classes and
    /// imports bound again are left alone, as are the loop counters. Needs the page's cells (a file without them has no cells to tell apart).
    /// </summary>
    IEnumerable<Diag> NotebookRedeclarations(ModuleInfo m, string? uri, HashSet<string> global)
    {
        if (uri == null || !_notebookCells.TryGetValue(uri, out var cells) || cells.Length < 2) yield break;
        var lines = new LineMap(m.Source);
        int CellOf(int offset)
        {
            int line = lines.PositionOf(offset).Line, c = 0;
            while (c + 1 < cells.Length && cells[c + 1].Line <= line) c++;
            return c;
        }
        foreach (var sym in m.Semantics.ModuleScope.Symbols.Values)
        {
            if (NotebookLoopNames.Contains(sym.Name) || global.Contains(sym.Name) || sym.Redirect != null) continue;
            var assigned = sym.Declarations.Where(d => d.Kind == DeclKind.Variable).OrderBy(d => d.Node.Start).ToList();
            if (assigned.Count < 2) continue;
            int firstCell = CellOf(assigned[0].Node.Start);
            foreach (var d in assigned.Skip(1))
            {
                if (CellOf(d.Node.Start) == firstCell) continue;
                var f = new Finding(d.Node.Start, d.Node.End - d.Node.Start,
                    $"Global variable '{sym.Name}' is redeclared: another cell assigns it too, so its value depends on the order the cells run in",
                    "notebook-redeclaration", FindingLevel.Weak);
                if (!Suppressed(m.Source, f)) yield return AsDiag(f);
            }
        }
    }

    /// <summary>The use runs when its cell runs: at the top level, in a class body or in a comprehension there, not inside a function or lambda.</summary>
    static bool RunsAtOnce(Scope? s)
    {
        for (; s != null; s = s.Parent)
            if (s.Kind is ScopeKind.Function or ScopeKind.Lambda) return false;
        return true;
    }
}
