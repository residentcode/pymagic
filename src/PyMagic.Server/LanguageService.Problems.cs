using PyMagic.Core.Types;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

public sealed record ProblemEntry(string Uri, int Line, int Column, int Severity, string Message, string? Code);

public sealed record TodoEntry(string Uri, int Line, string Tag, string Text);

/// <summary>The problems and the TODO comments of the whole project, for the two panels that list them.</summary>
public sealed partial class LanguageService
{
    /// <summary>Every finding of every file of the project that is at least as severe as <paramref name="leastSevere"/> (1 error, 2 warning, 3 weak warning, 4 hint), worst first.</summary>
    /// <param name="workers">How many analyses run side by side; by default as many as the size of the project asks for (1 below 150 files).</param>
    public IReadOnlyList<ProblemEntry> Problems(int leastSevere = 3, int limit = 3000, int? workers = null)
    {
        var snap = Full();
        var modules = snap.Project.Where(m => !m.IsStub && UriOfModule(m) != null).ToList();
        int n = workers ?? (modules.Count < 150 ? 1 : Math.Clamp(System.Environment.ProcessorCount / 4, 2, 4));
        List<ProblemEntry> result;
        if (n <= 1) result = ProblemsOf(snap, modules, leastSevere);
        else
        {
            // Each worker is a copy of this service with an evaluator of its own, analysing its share of the files; the parsed modules are shared.
            var files = snap.Project.Select(m => m.Path).Where(p => p != null).Select(p => p!).ToList();
            var chunks = Enumerable.Range(0, n).Select(i => modules.Skip(i * modules.Count / n).Take((i + 1) * modules.Count / n - i * modules.Count / n).ToList()).ToList();
            var tasks = chunks.Select(chunk => Task.Run(() =>
            {
                var worker = Fork(files);
                var wsnap = worker.Full();
                var mine = new HashSet<string>(chunk.Select(m => UriOfModule(m)!), StringComparer.Ordinal);
                return worker.ProblemsOf(wsnap, wsnap.Project.Where(m => !m.IsStub && worker.UriOfModule(m) is { } u && mine.Contains(u)).ToList(), leastSevere);
            })).ToArray();
            try { Task.WaitAll(tasks); }
            catch (AggregateException) { return Problems(leastSevere, limit, 1); }   // a worker failed: do it the plain way
            result = tasks.SelectMany(t => t.Result).ToList();
        }
        return result.OrderBy(p => p.Severity).ThenBy(p => p.Uri, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Line).Take(limit).ToList();
    }

    List<ProblemEntry> ProblemsOf(Snapshot snap, List<ModuleInfo> modules, int leastSevere)
    {
        var ev = snap.Workspace.Evaluator;
        var result = new List<ProblemEntry>();
        foreach (var m in modules)
        {
            if (UriOfModule(m) is not { } uri) continue;
            var lines = Lines(m);
            foreach (var d in Collect(m, ev))
            {
                if (d.Severity > leastSevere || d.Tags?.Contains(DiagnosticTag.Unnecessary) == true && d.Severity >= Severity.Hint) continue;
                var pos = lines.PositionOf(Math.Min(d.D.Start, m.Source.Length));
                result.Add(new ProblemEntry(uri, pos.Line, pos.Character, d.Severity, d.D.Message, d.Code));
            }
        }
        return result;
    }

    /// <summary>
    /// A service of its own with the same settings, the same open documents and the same parsed modules, for work that runs beside this one: it has
    /// its own snapshots and its own type evaluator, so nothing it computes can disturb what this service is doing.
    /// </summary>
    /// <summary>A copy that can be asked for slow things from another thread while this service keeps answering the editor.</summary>
    public LanguageService ForkForAnalysis() => Fork(null);

    LanguageService Fork(List<string>? projectFiles)
    {
        var f = new LanguageService(_cache) { TypeshedPath = TypeshedPath, ReportTypeMismatches = ReportTypeMismatches, ReportUnresolvedImports = ReportUnresolvedImports };
        f.SourceRoots.AddRange(SourceRoots);
        f.LibraryRoots.AddRange(LibraryRoots);
        f.ConfigureInterpreter(Interpreter);
        f.Environment = Environment;
        foreach (var d in _docs.Values) f.Open(d.Uri, d.Text, d.Version);
        f._projectFilesOverride = projectFiles;
        return f;
    }

    static readonly Regex TodoComment = new(@"#\s*(?<tag>TODO|FIXME|XXX|HACK|BUG)\b[:\s\-]*(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>The `# TODO`, `# FIXME`, `# XXX`, `# HACK` and `# BUG` comments of the project (found in comments only, not in strings).</summary>
    public IReadOnlyList<TodoEntry> Todos(int limit = 3000)
    {
        var snap = Full();
        var result = new List<TodoEntry>();
        foreach (var m in snap.Project.ToList())
        {
            if (m.IsStub || m.Source.IndexOf("TODO", StringComparison.Ordinal) < 0 && m.Source.IndexOf("FIXME", StringComparison.Ordinal) < 0
                && m.Source.IndexOf("XXX", StringComparison.Ordinal) < 0 && m.Source.IndexOf("HACK", StringComparison.Ordinal) < 0 && m.Source.IndexOf("BUG", StringComparison.Ordinal) < 0) continue;
            if (UriOfModule(m) is not { } uri) continue;
            string src = m.Source;
            foreach (var t in PyMagic.Core.Syntax.Lexer.Tokenize(src).Where(t => t.Kind == PyMagic.Core.Syntax.TokenKind.Comment))
            {
                var match = TodoComment.Match(t.Text(src));
                if (!match.Success) continue;
                int line = Lines(m).PositionOf(t.Start).Line;
                result.Add(new TodoEntry(uri, line, match.Groups["tag"].Value, match.Groups["text"].Value.Trim()));
                if (result.Count >= limit) return result;
            }
        }
        return result.OrderBy(t => t.Uri, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Line).ToList();
    }
}
