using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;

namespace PyMagic.Server;

/// <summary>
/// Full line completion without the model: what the model is shown around the caret, and the checks a line it wrote must pass before it is
/// suggested (as JetBrains' Full Line plugin does, a line that is likely wrong is dropped rather than shown): it must be on one line, its brackets
/// must not close what it did not open, it must not repeat the line below, and in Python every name it uses must be known. The caret must be at the
/// end of the code of its line (only closing brackets and quotes after it), and outside a comment and a string.
/// </summary>
public static class FullLineChecks
{
    /// <summary>The text before and after the caret the model is shown: whole lines, the nearest ones kept when the file is long.</summary>
    public static (string Prefix, string Suffix) Window(string text, int offset, int maxPrefix = 6000, int maxSuffix = 1500)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        string prefix = text[..offset], suffix = text[offset..];
        if (prefix.Length > maxPrefix)
        {
            int cut = prefix.IndexOf('\n', prefix.Length - maxPrefix);
            prefix = cut < 0 ? prefix[^maxPrefix..] : prefix[(cut + 1)..];
        }
        if (suffix.Length > maxSuffix)
        {
            int cut = suffix.LastIndexOf('\n', maxSuffix);
            suffix = cut < 0 ? suffix[..maxSuffix] : suffix[..cut];
        }
        return (prefix, suffix);
    }

    static readonly HashSet<char> Closers = new() { ')', ']', '}', '"', '\'', '`', ';', ',', ':', ' ', '\t', '>' };

    /// <summary>Whether a line may be completed at the caret: something typed on it, only closers after it, and not in a comment or a string.</summary>
    public static bool CanComplete(string text, int offset, string language)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (offset > 0 && text[offset - 1] == '\n') start = offset;
        int end = text.IndexOf('\n', offset);
        if (end < 0) end = text.Length;
        string before = text[start..offset], after = text[offset..end].TrimEnd('\r');
        if (before.Trim().Length == 0) return false;
        if (after.Any(c => !Closers.Contains(c))) return false;
        return !InCommentOrString(before, language) && !InDocstring(text[..start], language);
    }

    /// <summary>Whether the end of a line's text is in a comment or an open string (# in Python and shell, // in the C-like languages).</summary>
    public static bool InCommentOrString(string line, string language)
    {
        string comment = Hash(language) ? "#" : CLike(language) ? "//" : "";
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'' || (c == '`' && CLike(language))) quote = c;
            else if (comment.Length > 0 && string.CompareOrdinal(line, i, comment, 0, comment.Length) == 0) return true;
        }
        return quote != '\0';
    }

    /// <summary>Whether the text before a line leaves a Python triple-quoted string open (the line is in a docstring).</summary>
    static bool InDocstring(string before, string language)
    {
        if (language != "python") return false;
        return Regex.Matches(before, "\"\"\"|'''").Count % 2 == 1;
    }

    static bool Hash(string language) => language is "python" or "shell" or "toml" or "yaml" or "dockerfile" or "ini" or "r" or "perl" or "ruby";
    static bool CLike(string language) => language is "javascript" or "typescript" or "javascriptreact" or "typescriptreact" or "java" or "csharp" or "cpp" or "c" or "go" or "rust" or "php" or "kotlin" or "swift" or "scss" or "less";

    /// <summary>
    /// The suggestion made of what the model wrote, or null when it must not be shown: the first line only, without trailing spaces; nothing (or only
    /// spaces) is not a suggestion; brackets it closes must be ones it opened (or ones opened before it on its line, or on earlier lines when it starts
    /// with the closer); one it opens must be closed, unless it is the line's last character (a block or a call that goes on below); it must not be the
    /// line that follows; in Python, a name it uses must be known.
    /// </summary>
    public static string? Clean(string generated, string prefix, string suffix, string language)
    {
        int nl = generated.IndexOfAny(new[] { '\n', '\r' });
        string line = (nl < 0 ? generated : generated[..nl]).TrimEnd();
        if (line.Trim().Length == 0) return null;
        // what is after the caret on its line (closers) is already there: the suggestion stops before it
        int lineEnd = suffix.IndexOf('\n');
        string rest = (lineEnd < 0 ? suffix : suffix[..lineEnd]).TrimEnd('\r').Trim();
        if (rest.Length > 0 && line.EndsWith(rest)) line = line[..^rest.Length].TrimEnd();
        if (line.Length == 0) return null;
        int lineStart = prefix.LastIndexOf('\n') + 1;
        string before = prefix[lineStart..];
        if (!BracketsFit(prefix, line, language)) return null;
        string next = NextCodeLine(lineEnd < 0 ? "" : suffix[(lineEnd + 1)..]);
        if (next.Length > 0 && (before + line).Trim() == next) return null;
        if (language == "python" && UnknownName(before, line, prefix + "\n" + suffix) is not null) return null;
        return line;
    }

    static string NextCodeLine(string text)
    {
        foreach (string l in text.Split('\n'))
            if (l.Trim().Length > 0) return l.Trim();
        return "";
    }

    /// <summary>
    /// Whether the brackets of a suggestion fit the line it ends (strings and comments skipped): a closer closes the innermost bracket open on the
    /// line, or one left open on an earlier line (the last argument of a call written over several lines); a bracket the suggestion opens is closed
    /// in it, unless it is its last character.
    /// </summary>
    public static bool BracketsFit(string prefix, string line, string language)
    {
        int lineStart = prefix.LastIndexOf('\n') + 1;
        var outer = OpenBrackets(prefix[..lineStart], language);
        string before = prefix[lineStart..], all = before + line;
        var open = new Stack<(char C, bool Mine)>();
        char quote = '\0';
        for (int i = 0; i < all.Length; i++)
        {
            char c = all[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'' || (c == '`' && CLike(language))) { quote = c; continue; }
            if (c == '#' && Hash(language)) break;
            if (c is '(' or '[' or '{') { open.Push((c, i >= before.Length)); continue; }
            if (c is not (')' or ']' or '}')) continue;
            char want = c == ')' ? '(' : c == ']' ? '[' : '{';
            if (open.Count > 0)
            {
                if (open.Peek().C != want) return false;
                open.Pop();
            }
            else if (outer.Count > 0 && outer.Peek() == want) outer.Pop();
            else if (i >= before.Length) return false;
        }
        int mine = open.Count(o => o.Mine);
        if (mine == 0) return true;
        return mine == 1 && open.Peek().Mine && line.TrimEnd()[^1] is '(' or '[' or '{';
    }

    /// <summary>The brackets left open at the end of a text, innermost on top (strings, which end at their line, and comments skipped).</summary>
    static Stack<char> OpenBrackets(string text, string language)
    {
        var open = new Stack<char>();
        char quote = '\0';
        bool comment = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') { quote = '\0'; comment = false; continue; }
            if (comment) continue;
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'' || (c == '`' && CLike(language))) quote = c;
            else if (c == '#' && Hash(language)) comment = true;
            else if (c is '(' or '[' or '{') open.Push(c);
            else if (c is ')' or ']' or '}' && open.Count > 0) open.Pop();
        }
        return open;
    }

    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
        "while", "with", "yield", "match", "case", "type", "self", "cls", "_",
    };

    static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z_0-9]*", RegexOptions.Compiled);

    /// <summary>
    /// The first name in a Python suggestion that is not known, or null: a name is known when it is a keyword or a builtin, is in the file's text,
    /// is an attribute (after a dot), is being bound (assigned, a keyword argument, a parameter, a loop or with target, imported, defined), or is in a
    /// string or a comment. A name the model made up would be an unresolved reference.
    /// </summary>
    public static string? UnknownName(string before, string line, string fileText)
    {
        string all = before + line;
        string code = BlankStrings(all);
        int hash = code.IndexOf('#');
        if (hash >= 0) code = code[..hash];
        var words = new HashSet<string>(Identifier.Matches(fileText).Select(m => m.Value), StringComparer.Ordinal);
        string head = code.TrimStart();
        bool binding = Regex.IsMatch(head, @"^(def|class|import|from|for|with|async\s+(def|for|with)|global|nonlocal|except)\b");
        foreach (Match m in Identifier.Matches(code))
        {
            if (m.Index + m.Length <= before.Length) continue;   // typed already, not the model's
            string name = m.Value;
            if (Keywords.Contains(name) || Builtins.Names.Contains(name)) continue;
            // a word cut by the caret (open|ed) is the person's, finished by the model: only the whole word counts
            int s = m.Index;
            if (s > 0 && code[s - 1] == '.') continue;
            if (char.IsDigit(name[0])) continue;
            string after = code[(m.Index + m.Length)..].TrimStart();
            if (after.StartsWith('=') && !after.StartsWith("==")) continue;                    // assigned, or a keyword argument
            if (Regex.IsMatch(after, @"^(,\s*[A-Za-z_][\w.]*\s*)*(=[^=]|\+=|-=|\*=|/=|:\s*[A-Za-z_][\w.\[\], ]*\s*=)")) continue;   // a, b = ...; x: int = ...
            string lead = code[..s];
            if (Regex.IsMatch(lead, @"\b(as|for|lambda|def|class|import|global|nonlocal)\s+([\w]+\s*,\s*)*$")) continue;
            if (Regex.IsMatch(lead, @"\blambda\b[^:]*$")) continue;                          // lambda x, y:
            if (Regex.Matches(code, @"\blambda\b([^:]*):").Any(l => Regex.IsMatch(l.Groups[1].Value, @"\b" + name + @"\b"))) continue;   // ...and in its body
            if (binding && Regex.IsMatch(lead, @"^\s*(async\s+)?def\s+\w+\s*\(")) continue;   // a parameter
            if (Regex.IsMatch(code, @"\bfor\s+[\w\s,()]*\b" + name + @"\b[\w\s,()]*\bin\b")) continue;   // for i, (a, b) in; [x for x in xs]
            if (Regex.IsMatch(lead, @"\bimport\s+[\w.\s,]*$")) continue;
            if (words.Contains(name)) continue;
            return name;
        }
        return null;
    }

    /// <summary>A line with the text of its strings replaced by spaces (the quotes kept), so that no word in a string is read as a name.</summary>
    static string BlankStrings(string line)
    {
        var sb = new StringBuilder(line.Length);
        char quote = '\0';
        bool fString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote == '\0')
            {
                if (c is '"' or '\'')
                {
                    quote = c;
                    fString = i > 0 && line[i - 1] is 'f' or 'F';
                }
                sb.Append(c);
                continue;
            }
            if (c == '\\' && i + 1 < line.Length) { sb.Append("  "); i++; continue; }
            if (c == quote) { quote = '\0'; sb.Append(c); continue; }
            // an f-string's {expressions} are code: their names are checked
            if (fString && c == '{')
            {
                int close = line.IndexOf('}', i);
                if (close > i) { sb.Append(line, i, close - i + 1); i = close; continue; }
            }
            sb.Append(' ');
        }
        return sb.ToString();
    }
}
