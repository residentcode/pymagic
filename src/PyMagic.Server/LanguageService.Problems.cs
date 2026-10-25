using System.Text.RegularExpressions;

namespace PyMagic.Server;

public sealed record ProblemEntry(string Uri, int Line, int Column, int Severity, string Message, string? Code);

public sealed record TodoEntry(string Uri, int Line, string Tag, string Text);

/// <summary>The problems and the TODO comments of the whole project, for the two panels that list them.</summary>
public sealed partial class LanguageService
{
    /// <summary>Every finding of every file of the project that is at least as severe as <paramref name="leastSevere"/> (1 error, 2 warning, 3 weak warning, 4 hint), worst first.</summary>
    public IReadOnlyList<ProblemEntry> Problems(int leastSevere = 3, int limit = 3000)
    {
        var snap = Full();
        var ev = snap.Workspace.Evaluator;
        var result = new List<ProblemEntry>();
        foreach (var m in snap.Project.ToList())
        {
            if (m.IsStub || UriOfModule(m) is not { } uri) continue;
            var lines = Lines(m);
            foreach (var d in Collect(m, ev))
            {
                if (d.Severity > leastSevere || d.Tags?.Contains(DiagnosticTag.Unnecessary) == true && d.Severity >= Severity.Hint) continue;
                var pos = lines.PositionOf(Math.Min(d.D.Start, m.Source.Length));
                result.Add(new ProblemEntry(uri, pos.Line, pos.Character, d.Severity, d.D.Message, d.Code));
            }
        }
        return result.OrderBy(p => p.Severity).ThenBy(p => p.Uri, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Line).Take(limit).ToList();
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
