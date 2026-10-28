using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;

namespace PyMagic.Server;

/// <summary>Reading a docstring: its text without the quotes and the indentation, its first paragraph, and what it says about each parameter (Sphinx, Google and NumPy styles).</summary>
public static class Docstrings
{
    static readonly Regex Literal = new("^[rRuU]?(\"\"\"|'''|\"|')(?<b>.*)\\1$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The docstring of a def or class body (its first statement, when that is a plain string), cleaned like `inspect.cleandoc`; null when there is none.</summary>
    public static string? Of(IReadOnlyList<Stmt> body)
    {
        if (body.Count == 0 || body[0] is not ExprStmt { Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } c }) return null;
        var m = Literal.Match(c.Text);
        return m.Success ? Clean(m.Groups["b"].Value) : null;
    }

    /// <summary>The text without the indentation the source gave it, and without blank lines around it.</summary>
    public static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        int indent = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        for (int i = 1; i < lines.Count; i++) lines[i] = lines[i].Length >= indent ? lines[i][indent..] : lines[i].TrimStart();
        lines[0] = lines[0].TrimStart();
        return string.Join("\n", lines).Trim('\n', ' ', '\t');
    }

    /// <summary>The first paragraph on one line, shortened when it is long; null when the docstring is empty.</summary>
    public static string? Summary(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return null;
        string first = Regex.Replace(Regex.Split(doc.Trim(), @"\n\s*\n")[0], @"\s*\n\s*", " ").Trim();
        return first.Length > 300 ? first[..300] + "..." : first;
    }

    static readonly Regex Sphinx = new(@"^\s*:param\s+(?:.*\s)?\*{0,2}(?<n>\w+)\s*:\s*(?<d>.*)$", RegexOptions.Compiled);
    static readonly Regex GoogleHeader = new(@"^(?<i>\s*)(Args|Arguments|Parameters|Params|Keyword Args|Keyword Arguments|Other Parameters):\s*$", RegexOptions.Compiled);
    static readonly Regex GoogleEntry = new(@"^\*{0,2}(?<n>\w+)\s*(\([^)]*\))?\s*:\s*(?<d>.*)$", RegexOptions.Compiled);
    static readonly Regex NumpyEntry = new(@"^(?<n>\*{0,2}\w+(?:\s*,\s*\*{0,2}\w+)*)(\s*:.*)?$", RegexOptions.Compiled);

    /// <summary>What a docstring says about each parameter, by name (without any `*`): from `:param x:` lines, an `Args:` block or a NumPy `Parameters` section.</summary>
    public static IReadOnlyDictionary<string, string> Parameters(string? doc)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(doc)) return found;
        var lines = doc.Split('\n');
        static int Indent(string l) => l.Length - l.TrimStart().Length;

        void Put(string name, List<string> parts)
        {
            string text = string.Join(" ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));
            if (text.Length > 0) found.TryAdd(name, text.Length > 300 ? text[..300] + "..." : text);
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // :param type name: description, going on over the lines that are indented more
            if (Sphinx.Match(line) is { Success: true } sp)
            {
                var parts = new List<string> { sp.Groups["d"].Value };
                int j = i + 1;
                while (j < lines.Length && lines[j].Trim().Length > 0 && !lines[j].TrimStart().StartsWith(':') && Indent(lines[j]) > Indent(line)) parts.Add(lines[j++]);
                Put(sp.Groups["n"].Value, parts);
                continue;
            }

            // Args:
            //     name (type): description
            //         going on
            if (GoogleHeader.Match(line) is { Success: true } gh)
            {
                int headerIndent = gh.Groups["i"].Length;
                int j = i + 1;
                int entryIndent = -1;
                while (j < lines.Length)
                {
                    string l = lines[j];
                    if (l.Trim().Length == 0) { j++; continue; }
                    int ind = Indent(l);
                    if (ind <= headerIndent) break;
                    if (entryIndent < 0) entryIndent = ind;
                    if (ind == entryIndent && GoogleEntry.Match(l.Trim()) is { Success: true } ge)
                    {
                        var parts = new List<string> { ge.Groups["d"].Value };
                        int k = j + 1;
                        while (k < lines.Length && lines[k].Trim().Length > 0 && Indent(lines[k]) > entryIndent) parts.Add(lines[k++]);
                        Put(ge.Groups["n"].Value, parts);
                        j = k;
                        continue;
                    }
                    j++;
                }
                i = j - 1;
                continue;
            }

            // Parameters
            // ----------
            // name : type
            //     description
            if (i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*-{3,}\s*$") && Regex.IsMatch(line, @"^\s*(Other )?Parameters\s*$"))
            {
                int baseIndent = Indent(line);
                int j = i + 2;
                while (j < lines.Length)
                {
                    string l = lines[j];
                    // the next section starts with a title and a line of dashes
                    if (j + 1 < lines.Length && Regex.IsMatch(lines[j + 1], @"^\s*-{3,}\s*$") && l.Trim().Length > 0) break;
                    if (l.Trim().Length == 0 || Indent(l) > baseIndent) { j++; continue; }
                    if (NumpyEntry.Match(l.Trim()) is { Success: true } ne)
                    {
                        var parts = new List<string>();
                        int k = j + 1;
                        while (k < lines.Length && (lines[k].Trim().Length == 0 || Indent(lines[k]) > baseIndent))
                        {
                            if (lines[k].Trim().Length == 0) { if (parts.Count > 0) break; k++; continue; }
                            parts.Add(lines[k++]);
                        }
                        foreach (var n in ne.Groups["n"].Value.Split(',')) Put(n.Trim().TrimStart('*'), parts);
                        j = k;
                        continue;
                    }
                    j++;
                }
                i = j - 1;
            }
        }
        return found;
    }
}
