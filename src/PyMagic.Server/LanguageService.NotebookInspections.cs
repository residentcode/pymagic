using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// The notebook's own check: the page hands the language server a notebook's code cells as one file (.pymagic/notebooks/...), top to bottom.
/// A name a cell reads that only a later cell (or a later line) defines works while the cells are run out of order, and stops with NameError
/// when the notebook is run from the top (Run All, a fresh kernel, nbconvert). Uses inside functions are left alone: they run when called.
/// </summary>
public sealed partial class LanguageService
{
    static bool IsNotebookFile(string? uri) => uri != null && uri.Contains("/.pymagic/notebooks/", StringComparison.OrdinalIgnoreCase);

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
        foreach (var r in m.Semantics.References)
        {
            if ((r.Access & Access.Read) == 0 || r.Symbol is not { } sym || !ReferenceEquals(sym.Scope, module) || !RunsAtOnce(r.UseScope)) continue;
            if (sym.Declarations.Count == 0 || global.Contains(sym.Name)) continue;
            int first = sym.Declarations.Min(d => d.Node.Start);
            // `x += 1` and `for x in ...` read after their own binding has begun: the binding counts from where the statement starts
            if (first <= r.Node.Start) continue;
            var f = new Finding(r.Node.Start, r.Node.End - r.Node.Start,
                $"'{r.Node.Name}' is defined only further down (a later cell): run from the top, the notebook stops here with NameError", "notebook-cell-order", FindingLevel.Warning);
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
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
