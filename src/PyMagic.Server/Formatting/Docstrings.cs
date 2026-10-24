using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Formatting;

/// <summary>Docstrings the way black writes them: re-indented, trimmed, with the closing quotes after the text or on their own line.</summary>
internal static class Docstrings
{
    const int LineLength = 88;

    /// <summary>The docstring literal <paramref name="token"/> of a statement that is indented by <paramref name="indentWidth"/>; continuation lines carry their indentation.</summary>
    public static string Format(string token, int indentWidth, string nl)
    {
        int q = 0;
        while (q < token.Length && token[q] != '"' && token[q] != '\'') q++;
        if (q >= token.Length) return token;
        string prefix = token[..q];
        if (prefix.IndexOfAny(new[] { 'b', 'B', 'f', 'F', 't', 'T' }) >= 0) return PythonFormatter.NormaliseString(token);

        string normalised = PythonFormatter.NormaliseString(token);
        string rest = normalised[q..];
        char quote = rest[0];
        int quoteLength = rest.Length >= 6 && rest[1] == quote && rest[2] == quote ? 3 : 1;
        if (rest.Length < 2 * quoteLength) return normalised;
        string body = rest[quoteLength..^quoteLength].Replace("\r\n", "\n");

        // a line that ends in a backslash continues the string: leave such docstrings alone
        if (Regex.IsMatch(body, @"\\\s*\n")) return normalised;

        string indent = new string(' ', indentWidth);
        bool startedEmpty = body.Length == 0;
        string doc = body.Contains('\n') ? Fix(body, indent) : body.Trim();

        if (doc.Length > 0)
        {
            if (doc[0] == quote) doc = " " + doc;
            if (doc[^1] == quote) doc += " ";
            if (doc[^1] == '\\')
            {
                int backslashes = doc.Length - doc.TrimEnd('\\').Length;
                if (backslashes % 2 == 1) doc += " ";
            }
        }
        else if (!startedEmpty) doc = " ";

        string quotes = new string(quote, quoteLength);
        string result;
        var lines = doc.Split('\n');
        if (quoteLength == 3 && lines.Length > 1 && lines[^1].Length + quoteLength > LineLength && lines[^1].Trim().Length > 0)
            result = prefix + quotes + doc + "\n" + indent + quotes;
        else
            result = prefix + quotes + doc + quotes;
        return nl == "\n" ? result : result.Replace("\n", nl);
    }

    /// <summary>Black's fix_docstring: the common indentation of the continuation lines is replaced by the indentation of the statement.</summary>
    static string Fix(string body, string indent)
    {
        var lines = ExpandLeadingTabs(body.Split('\n'));
        int common = int.MaxValue;
        for (int i = 1; i < lines.Count; i++)
        {
            string stripped = lines[i].TrimStart();
            if (stripped.Length > 0) common = Math.Min(common, lines[i].Length - stripped.Length);
        }

        var trimmed = new List<string> { lines[0].Trim() };
        if (common < int.MaxValue)
        {
            int last = lines.Count - 2;
            for (int i = 1; i < lines.Count; i++)
            {
                string line = lines[i];
                string stripped = (line.Length >= common ? line[common..] : "").TrimEnd();
                trimmed.Add(stripped.Length > 0 || i - 1 == last ? indent + stripped : "");
            }
        }
        return string.Join("\n", trimmed);
    }

    static List<string> ExpandLeadingTabs(string[] lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            string stripped = line.TrimStart();
            if (stripped.Length == 0 || stripped.Length == line.Length) { result.Add(line); continue; }
            string lead = line[..(line.Length - stripped.Length)];
            var sb = new StringBuilder();
            foreach (char c in lead)
            {
                if (c == '\t') sb.Append(' ', 8 - sb.Length % 8);
                else sb.Append(c);
            }
            result.Add(sb + stripped);
        }
        return result;
    }

    /// <summary>The text of a docstring as the compiler's cleandoc sees it, for comparing two spellings of one docstring.</summary>
    public static string Clean(string body)
    {
        var lines = ExpandLeadingTabs(body.Replace("\r\n", "\n").Split('\n'));
        int common = int.MaxValue;
        for (int i = 1; i < lines.Count; i++)
        {
            string stripped = lines[i].TrimStart();
            if (stripped.Length > 0) common = Math.Min(common, lines[i].Length - stripped.Length);
        }
        var cleaned = new List<string> { lines[0].Trim() };
        for (int i = 1; i < lines.Count; i++)
            cleaned.Add(common == int.MaxValue ? "" : (lines[i].Length >= common ? lines[i][common..] : "").TrimEnd());
        while (cleaned.Count > 0 && cleaned[^1].Trim().Length == 0) cleaned.RemoveAt(cleaned.Count - 1);
        while (cleaned.Count > 0 && cleaned[0].Trim().Length == 0) cleaned.RemoveAt(0);
        return string.Join("\n", cleaned);
    }
}
