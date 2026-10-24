using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Formatting;

/// <summary>What the .editorconfig files above a file say about it (a section such as <c>[*.py]</c> or <c>[tests/**.py]</c> that matches it).</summary>
public static class EditorConfig
{
    /// <summary>The properties for a file, nearest file and last matching section winning; keys are lower case.</summary>
    public static Dictionary<string, string> For(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string full;
        try { full = Path.GetFullPath(filePath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return result; }

        // files from the file's folder up to the first one that says root = true; the farthest is applied first
        var files = new List<string>();
        for (string? dir = Path.GetDirectoryName(full); dir != null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, ".editorconfig");
            if (!File.Exists(candidate)) continue;
            files.Add(candidate);
            if (IsRoot(candidate)) break;
        }
        files.Reverse();
        foreach (var file in files)
            foreach (var (key, value) in Read(file, full))
                result[key] = value;
        return result;
    }

    static bool IsRoot(string path)
    {
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) return false;
                if (line.Replace(" ", "").Equals("root=true", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>The properties of every section of one file that matches the target path, in file order.</summary>
    public static List<(string Key, string Value)> Read(string file, string target)
    {
        var found = new List<(string, string)>();
        string baseDir = Path.GetDirectoryName(file)!;
        string relative = Path.GetRelativePath(baseDir, target).Replace('\\', '/');
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { return found; }
        catch (UnauthorizedAccessException) { return found; }
        return Parse(lines, relative);
    }

    /// <summary>The same for the text of a file; <paramref name="relative"/> is the path of the target below the file's folder, with forward slashes.</summary>
    public static List<(string Key, string Value)> Parse(IEnumerable<string> lines, string relative)
    {
        var found = new List<(string, string)>();
        bool inMatching = false, seenSection = false;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                seenSection = true;
                inMatching = Matches(line[1..^1], relative);
                continue;
            }
            if (!seenSection || !inMatching) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();
            int comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment].TrimEnd();
            found.Add((key, value.ToLowerInvariant()));
        }
        return found;
    }

    /// <summary>EditorConfig globs: <c>*</c> (not across folders), <c>**</c>, <c>?</c>, <c>[abc]</c>, <c>{a,b}</c> and <c>{1..3}</c>; a pattern with no slash matches in any folder.</summary>
    public static bool Matches(string glob, string relative)
    {
        string pattern = glob.StartsWith('/') ? glob[1..] : glob.Contains('/') ? glob : "**/" + glob;
        try { return Regex.IsMatch(relative, "^" + ToRegex(pattern) + "$", RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return false; }
    }

    static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        int braces = 0;
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        // "**/" also matches no folder at all
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { sb.Append("(?:.*/)?"); i++; }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                {
                    int close = glob.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append("\\["); break; }
                    string set = glob[(i + 1)..close];
                    sb.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                    i = close;
                    break;
                }
                case '{':
                {
                    int close = glob.IndexOf('}', i + 1);
                    if (close < 0) { sb.Append("\\{"); break; }
                    string body = glob[(i + 1)..close];
                    var range = Regex.Match(body, @"^(-?\d+)\.\.(-?\d+)$");
                    if (range.Success)
                    {
                        int lo = int.Parse(range.Groups[1].Value), hi = int.Parse(range.Groups[2].Value);
                        sb.Append("(?:").Append(string.Join("|", Enumerable.Range(Math.Min(lo, hi), Math.Abs(hi - lo) + 1))).Append(')');
                    }
                    else sb.Append("(?:").Append(string.Join("|", body.Split(',').Select(ToRegex))).Append(')');
                    i = close;
                    braces++;
                    break;
                }
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }
}
