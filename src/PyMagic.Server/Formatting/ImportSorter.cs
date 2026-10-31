using System.Text;
using PyMagic.Core.Syntax;

namespace PyMagic.Server.Formatting;

public enum ImportSection { Future, Stdlib, ThirdParty, FirstParty, Local }

/// <summary>What isort and ruff's `lint.isort` let a project decide: which packages belong where, and how the lines are laid out.</summary>
/// <param name="ForceSortWithinSections">`import x` and `from x import y` sorted together by module name.</param>
/// <param name="CombineAsImports">`from a import b as c, d` in one statement instead of a line for each alias.</param>
/// <param name="ForceSingleLine">One imported name per line.</param>
/// <param name="OrderByType">Constants, classes, then the rest (the default); off sorts names case-insensitively.</param>
/// <param name="KnownFirstParty">Top-level packages that are the project's own.</param>
/// <param name="KnownThirdParty">Top-level packages that are installed ones.</param>
/// <param name="LinesAfterImports">Blank lines between the imports and the code after them; -1 leaves them as the formatter has them.</param>
/// <param name="SectionOrder">The order of the sections (`section-order`, isort's `sections`); null for future, standard library, third party, first party, local.</param>
/// <param name="ForceWrapAliases">A `from` import of several names with an `as` among them is written one name to a line (with <paramref name="CombineAsImports"/>).</param>
/// <param name="FromFirst">`from x import y` lines before `import x` lines in a section.</param>
/// <param name="ClosestRelativeFirst">`from . import` before `from .. import` (ruff's `closest-to-furthest`); the default is the furthest first.</param>
/// <param name="SplitOnTrailingComma">A `from` import written with a trailing comma inside its parentheses stays one name to a line (ruff's default).</param>
public sealed record ImportOptions(bool ForceSortWithinSections = false, bool CombineAsImports = false, bool ForceSingleLine = false, bool OrderByType = true,
    IReadOnlyList<string>? KnownFirstParty = null, IReadOnlyList<string>? KnownThirdParty = null, int LinesAfterImports = -1, IReadOnlyList<ImportSection>? SectionOrder = null,
    bool ForceWrapAliases = false, bool FromFirst = false, bool ClosestRelativeFirst = false, bool SplitOnTrailingComma = true)
{
    public static readonly ImportOptions Default = new();

    static readonly ImportSection[] Sections = { ImportSection.Future, ImportSection.Stdlib, ImportSection.ThirdParty, ImportSection.FirstParty, ImportSection.Local };

    /// <summary>The sections in the order they are written: those named first, then any left out, in the usual order.</summary>
    public IEnumerable<ImportSection> OrderOfSections() => (SectionOrder ?? Array.Empty<ImportSection>()).Concat(Sections).Distinct();

    public ImportSection? Override(string top) =>
        KnownFirstParty?.Contains(top) == true ? ImportSection.FirstParty : KnownThirdParty?.Contains(top) == true ? ImportSection.ThirdParty : null;
}

/// <summary>
/// Sorts the blocks of top-level imports the way isort does with its black profile: sections (future, standard
/// library, third party, first party, relative), `import x` lines before `from x import y` lines inside a section,
/// names ordered constants, classes, then functions, duplicates merged, long lines wrapped with a trailing comma. Comments
/// above an import and after it stay with it; a block that has comments inside a statement, several statements on a line
/// or an `isort: skip` marker is left alone.
/// </summary>
public static class ImportSorter
{
    sealed class Entry
    {
        public bool IsFrom;
        public int Level;
        public string Module = "";
        public string? Name;              // for `from`: the imported name ("*" for a star import); for `import`: null
        public string? Alias;
        public List<string> Leading = new();
        public string? Trailing;
        public ImportSection Section;
        public bool TrailingComma;        // the `from` statement it came from ends its names with a comma: `(a, b,)`
        public bool HasComment => Leading.Count > 0 || Trailing != null;
        public string Key => $"{(IsFrom ? "from" : "import")}|{Level}|{Module}|{Name}|{Alias}";
    }

    /// <summary>The sorted text, or null when the text cannot be handled (syntax errors, `isort: skip_file`).</summary>
    public static string? Sort(string source, Func<string, ImportSection> classify, int lineLength = 88, ImportOptions? options = null)
    {
        options ??= ImportOptions.Default;
        if (source.Contains("isort: skip_file") || source.Contains("isort:skip_file")) return null;
        var parsed = Parser.Parse(source);
        if (parsed.Diagnostics.Count > 0) return null;
        var tokens = Lexer.Tokenize(source);
        string nl = source.Contains("\r\n") ? "\r\n" : "\n";

        // runs of consecutive import statements at the top level
        var body = parsed.Module.Body;
        var runs = new List<List<Stmt>>();
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is not (ImportStmt or ImportFromStmt)) continue;
            var run = new List<Stmt>();
            while (i < body.Count && body[i] is ImportStmt or ImportFromStmt) run.Add(body[i++]);
            runs.Add(run);
            i--;
        }

        string text = source;
        for (int r = runs.Count - 1; r >= 0; r--)
        {
            var replaced = SortRun(text, source, runs[r], tokens, classify, lineLength, nl, options);
            if (replaced == null) continue;
            text = text[..replaced.Value.Start] + replaced.Value.Text + text[replaced.Value.End..];
            if (options.LinesAfterImports >= 0) text = BlankLinesAfter(text, replaced.Value.Start + replaced.Value.Text.Length, options.LinesAfterImports, nl);
        }
        return text;
    }

    /// <summary>Exactly <paramref name="count"/> blank lines between the end of an import block and what follows it, when something follows.</summary>
    static string BlankLinesAfter(string text, int end, int count, string nl)
    {
        int p = end;
        while (p < text.Length && text[p] is '\r' or '\n' or ' ' or '\t') p++;
        if (p >= text.Length) return text;
        // back to the start of the line that has the code (its indentation is kept)
        int lineStart = p;
        while (lineStart > end && text[lineStart - 1] is ' ' or '\t') lineStart--;
        return text[..end] + string.Concat(Enumerable.Repeat(nl, count + 1)) + text[lineStart..];
    }

    // `text` is the evolving result, `original` the text the offsets of the syntax tree refer to; runs are processed from
    // the end, so the offsets of earlier runs are still valid in `text`.
    static (int Start, int End, string Text)? SortRun(string text, string original, List<Stmt> run, List<Token> tokens,
        Func<string, ImportSection> classify, int lineLength, string nl, ImportOptions options)
    {
        string s = original;
        var entries = new List<Entry>();
        int regionStart = -1, regionEnd = -1;

        for (int n = 0; n < run.Count; n++)
        {
            var stmt = run[n];
            int end = stmt.End;
            while (end > stmt.Start && (s[end - 1] == '\n' || s[end - 1] == '\r')) end--;
            int lineEnd = end;
            while (lineEnd < s.Length && s[lineEnd] != '\n' && s[lineEnd] != '\r') lineEnd++;
            string tail = s[end..lineEnd];
            string? trailing = null;
            if (tail.Trim().Length > 0)
            {
                if (!tail.TrimStart().StartsWith('#')) return null;     // `import a; import b`
                trailing = tail.Trim();
                if (trailing.Contains("isort:skip") || trailing.Contains("isort: skip")) return null;
            }
            // a statement that starts on the line of the previous one
            int lineStart = LineStart(s, stmt.Start);
            if (s[lineStart..stmt.Start].Trim().Length > 0) return null;
            // comments inside the statement cannot be moved safely
            if (tokens.Any(t => t.Kind == TokenKind.Comment && t.Start > stmt.Start && t.End <= end)) return null;

            // own-line comments above: for the first statement only the ones touching it, for the others all between
            var leading = new List<string>();
            int gapStart = n == 0 ? lineStart : regionEnd, commentStart = lineStart;
            if (n == 0)
            {
                int p = lineStart;
                while (p > 0)
                {
                    int prevStart = LineStart(s, p - 1 > 0 && s[p - 1] == '\n' && p - 2 >= 0 && s[p - 2] == '\r' ? p - 2 : p - 1);
                    string prevLine = s[prevStart..p].Trim();
                    if (!prevLine.StartsWith('#')) break;
                    leading.Insert(0, prevLine);
                    commentStart = prevStart;
                    p = prevStart;
                }
                regionStart = commentStart;
            }
            else
            {
                foreach (var line in s[gapStart..lineStart].Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0) continue;
                    if (!t.StartsWith('#')) return null;
                    leading.Add(t);
                }
            }
            regionEnd = lineEnd;

            var made = new List<Entry>();
            switch (stmt)
            {
                case ImportStmt imp:
                    foreach (var a in imp.Names) made.Add(new Entry { IsFrom = false, Module = a.Name, Alias = a.AsName });
                    break;
                case ImportFromStmt f:
                {
                    bool comma = System.Text.RegularExpressions.Regex.IsMatch(s[f.Start..end], @",\s*\)\s*$");
                    if (f.IsStar) made.Add(new Entry { IsFrom = true, Level = f.Level, Module = f.Module ?? "", Name = "*" });
                    else foreach (var a in f.Names) made.Add(new Entry { IsFrom = true, Level = f.Level, Module = f.Module ?? "", Name = a.Name, Alias = a.AsName, TrailingComma = comma });
                    break;
                }
            }
            if (made.Count == 0) return null;
            made[0].Leading = leading;
            made[0].Trailing = trailing;
            entries.AddRange(made);
        }

        foreach (var e in entries)
        {
            string top = e.Module.Split('.')[0];
            e.Section = e.Level > 0 ? ImportSection.Local : e.Module == "__future__" ? ImportSection.Future : options.Override(top) ?? classify(top);
        }

        // duplicates go (the first copy keeps its comments); the others are merged by the rendering
        var unique = new List<Entry>();
        var seen = new Dictionary<string, Entry>();
        foreach (var e in entries)
        {
            if (seen.TryGetValue(e.Key, out var first))
            {
                first.Leading.AddRange(e.Leading.Where(c => !first.Leading.Contains(c)));
                first.Trailing ??= e.Trailing;
                continue;
            }
            seen[e.Key] = e;
            unique.Add(e);
        }

        string rendered = Render(unique, lineLength, nl, options);
        // the region is given back even when it does not change, so that the blank lines after it can be set
        return (regionStart, regionEnd, rendered);
    }

    // ---------------------------------------------------------- output

    static string Render(List<Entry> entries, int lineLength, string nl, ImportOptions options)
    {
        var sb = new StringBuilder();
        bool firstSection = true;
        foreach (var section in options.OrderOfSections())
        {
            var inSection = entries.Where(e => e.Section == section).ToList();
            if (inSection.Count == 0) continue;
            if (!firstSection) sb.Append(nl);
            firstSection = false;

            var plain = inSection.Where(e => !e.IsFrom).OrderBy(e => e.Module.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(e => e.Module, StringComparer.Ordinal).ThenBy(e => e.Alias ?? "", StringComparer.Ordinal).ToList();
            var froms = inSection.Where(e => e.IsFrom).ToList();
            var ordered = froms.OrderBy(e => options.ClosestRelativeFirst ? e.Level : -e.Level).ThenBy(e => e.Module.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(e => e.Module, StringComparer.Ordinal)
                .ThenBy(e => Mergeable(e, options.CombineAsImports) ? 0 : 1).ThenBy(e => TypeRank(e.Name!, options)).ThenBy(e => e.Name!.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(e => e.Alias ?? "", StringComparer.Ordinal).ToList();

            // each statement to write: a plain import, or one from statement made of one or more entries
            var statements = new List<(string SortKey, int Level, Action Write)>();
            var fromStatements = new List<(string SortKey, int Level, Action Write)>();
            foreach (var e in plain)
            {
                var entry = e;
                statements.Add((e.Module.ToLowerInvariant(), 0, () => AppendEntry(sb, entry, $"import {entry.Module}{(entry.Alias != null ? " as " + entry.Alias : "")}", nl)));
            }
            var done = new HashSet<Entry>();
            foreach (var e in ordered)
            {
                if (done.Contains(e)) continue;
                var group = ordered.Where(x => x.Level == e.Level && x.Module == e.Module && !done.Contains(x) && Mergeable(x, options.CombineAsImports) && Mergeable(e, options.CombineAsImports)).ToList();
                if (group.Count == 0 || !Mergeable(e, options.CombineAsImports)) group = new List<Entry> { e };
                foreach (var g in group) done.Add(g);
                var captured = group;
                fromStatements.Add((e.Module.ToLowerInvariant(), e.Level, () => AppendFrom(sb, captured, lineLength, nl, options)));
            }
            // `from` lines after the plain ones, or before them with from-first
            statements = options.FromFirst ? fromStatements.Concat(statements).ToList() : statements.Concat(fromStatements).ToList();
            if (options.ForceSortWithinSections)
            {
                // one run ordered by module name, relative imports keeping their own order (farthest first, or closest with closest-to-furthest)
                foreach (var s in statements.Select((s, i) => (s, i)).OrderBy(x => options.ClosestRelativeFirst ? x.s.Level : -x.s.Level).ThenBy(x => x.s.SortKey, StringComparer.Ordinal).ThenBy(x => x.i).Select(x => x.s)) s.Write();
            }
            else foreach (var s in statements) s.Write();
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    static bool Mergeable(Entry e, bool combineAs = false) => (combineAs || e.Alias == null) && !e.HasComment && e.Name != "*";

    static void AppendEntry(StringBuilder sb, Entry e, string statement, string nl)
    {
        foreach (var c in e.Leading) sb.Append(c).Append(nl);
        sb.Append(statement);
        if (e.Trailing != null) sb.Append("  ").Append(e.Trailing);
        sb.Append(nl);
    }

    static void AppendFrom(StringBuilder sb, List<Entry> group, int lineLength, string nl, ImportOptions options)
    {
        var first = group[0];
        string module = new string('.', first.Level) + first.Module;
        var names = group.Select(g => (Name: g.Name!, g.Alias)).Distinct().OrderBy(x => TypeRank(x.Name, options)).ThenBy(x => x.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        string Written((string Name, string? Alias) n) => n.Alias != null ? $"{n.Name} as {n.Alias}" : n.Name;
        foreach (var c in first.Leading) sb.Append(c).Append(nl);
        if (options.ForceSingleLine)
        {
            foreach (var n in names) sb.Append($"from {module} import {Written(n)}").Append(nl);
            return;
        }
        string line = $"from {module} import {string.Join(", ", names.Select(Written))}";
        // kept one to a line: the statement was written so with a trailing comma, or it has an alias among several names (force-wrap-aliases)
        bool wrap = options.SplitOnTrailingComma && group.Any(g => g.TrailingComma) && first.Name != "*"
            || options.ForceWrapAliases && options.CombineAsImports && names.Count > 1 && names.Any(n => n.Alias != null);
        if (!wrap && (line.Length <= lineLength || names.Count == 1 && first.Name == "*"))
        {
            sb.Append(line);
            if (first.Trailing != null) sb.Append("  ").Append(first.Trailing);
            sb.Append(nl);
            return;
        }
        sb.Append($"from {module} import (");
        if (first.Trailing != null) sb.Append("  ").Append(first.Trailing);
        sb.Append(nl);
        foreach (var n in names) sb.Append("    ").Append(Written(n)).Append(',').Append(nl);
        sb.Append(')').Append(nl);
    }

    /// <summary>CONSTANTS first, then Classes, then everything else (isort's order_by_type).</summary>
    static int TypeRank(string name, ImportOptions options)
    {
        if (!options.OrderByType) return 0;
        if (name == "*") return 0;
        if (name.Length > 1 && name.ToUpperInvariant() == name && name.Any(char.IsLetter)) return 0;
        if (char.IsUpper(name[0])) return 1;
        return 2;
    }

    static int LineStart(string s, int pos)
    {
        while (pos > 0 && s[pos - 1] != '\n' && s[pos - 1] != '\r') pos--;
        return pos;
    }

    static int EndOfLine(string s, int pos)
    {
        while (pos < s.Length && s[pos] != '\n' && s[pos] != '\r') pos++;
        return pos;
    }
}
