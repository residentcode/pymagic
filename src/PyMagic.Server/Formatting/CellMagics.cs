using System.Text.RegularExpressions;

namespace PyMagic.Server.Formatting;

/// <summary>
/// The IPython lines of a notebook cell, which are not Python: `%magic`, `!command`, `x = !command`, `x = %magic`, and a first-line `%%time`.
/// Each is set aside as a placeholder line the formatter keeps as it is, and put back afterwards, so the rest of the cell can be laid out.
/// </summary>
public static class CellMagics
{
    /// <summary>Cell magics whose body is Python (`%%time` and the like); a cell under any other (`%%bash`, `%%html`) is not Python at all.</summary>
    static readonly HashSet<string> PythonCellMagics = new(StringComparer.Ordinal) { "time", "timeit", "capture", "prun", "debug", "memit", "mprun", "px" };

    static readonly Regex MagicLine = new(@"^(?<indent>[ \t]*)(?:[A-Za-z_][\w.]*(?:\s*,\s*[A-Za-z_][\w.]*)*\s*=\s*)?[!%]", RegexOptions.Compiled);
    static readonly Regex Placeholder = new(@"^(?<indent>[ \t]*)__pymagic_magic_(?<n>\d+)__$", RegexOptions.Compiled);

    /// <summary>The cell's text with its IPython lines replaced, line for line, by placeholders; null when it has none.</summary>
    /// <exception cref="ServiceException">The cell is under a cell magic whose body is not Python (`%%bash`).</exception>
    public static (string Text, List<string> Lines)? Mask(string text)
    {
        var lines = text.Split('\n');
        var set = new List<string>();
        bool continued = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            bool magic = continued || MagicLine.IsMatch(line) && !InsideBrackets(lines, i);
            if (i == 0 && line.TrimStart().StartsWith("%%", StringComparison.Ordinal))
            {
                string name = Regex.Match(line.TrimStart()[2..], @"^\w+").Value;
                if (!PythonCellMagics.Contains(name)) throw new ServiceException($"A %%{name} cell is not Python, so it is left as it is.");
            }
            if (!magic) continue;
            string indent = continued ? "" : MagicLine.Match(line).Groups["indent"].Value;
            continued = line.EndsWith('\\');
            lines[i] = indent + "__pymagic_magic_" + set.Count + "__" + (lines[i].EndsWith('\r') ? "\r" : "");
            set.Add(line);
        }
        return set.Count == 0 ? null : (string.Join('\n', lines), set);
    }

    /// <summary>The formatted text with the IPython lines put back where their placeholders ended up, at the indentation the formatter gave them.</summary>
    public static string Unmask(string formatted, List<string> set)
    {
        var lines = formatted.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Placeholder.Match(lines[i].TrimEnd('\r'));
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, out int n) || n >= set.Count) continue;
            string original = set[n];
            // a line that went on from the one before keeps its own indentation; others take the new one
            bool continuation = n > 0 && set[n - 1].EndsWith('\\');
            lines[i] = (continuation ? original : m.Groups["indent"].Value + original.TrimStart()) + (lines[i].EndsWith('\r') ? "\r" : "");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Whether line <paramref name="i"/> is inside brackets opened before it (`f(\n  %x)` is code, not a magic), counting brackets roughly.</summary>
    static bool InsideBrackets(string[] lines, int i)
    {
        int depth = 0;
        for (int k = 0; k < i; k++)
            foreach (char c in lines[k])
            {
                if (c == '#') break;
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            }
        return depth > 0;
    }
}
