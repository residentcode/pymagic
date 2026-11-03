using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// The inspections PyCharm has for other tools than Python itself (pandas, unittest.mock, Jupyter notebooks, PySpark), gathered here so
/// the list of the others stays as it was; each tool's checks are in their own file.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> ToolDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var d in PandasDiagnostics(m, ev)) yield return d;
        foreach (var d in MockDiagnostics(m, ev)) yield return d;
        foreach (var d in SparkDiagnostics(m, ev)) yield return d;
        string? uri = UriOfModule(m);
        foreach (var d in BddDiagnostics(m, uri)) yield return d;
        foreach (var d in NotebookDiagnostics(m, uri)) yield return d;
    }

    /// <summary>The quick fixes of those inspections; false when the code is not one of theirs.</summary>
    bool ToolFix(Snapshot snap, string code, ModuleInfo m, string uri, int start, int end, Action<string, bool, Edit[]> add)
    {
        switch (code)
        {
            case "pandas-series-to-list" or "pandas-truth-value":
                PandasFix(code, m, start, end, add);
                return true;
        }
        return false;
    }
}
