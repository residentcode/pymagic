using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <param name="Include">Comma-separated globs a file path (relative, with `/`) must match; empty means every searched file.</param>
/// <param name="Exclude">Comma-separated globs of paths to skip.</param>
/// <param name="Replace">When set, every match also carries the text that would replace it.</param>
public sealed record SearchOptions(string Query, bool Regex = false, bool CaseSensitive = false, bool WholeWord = false,
    string? Include = null, string? Exclude = null, string? Replace = null, int MaxResults = 2000);

/// <summary>One match; Line and Column are zero-based, Text is the whole line (cut at 400 characters).</summary>
public sealed record SearchMatch(int Line, int Column, int Length, string Text, string? Replacement);
public sealed record SearchFile(string Path, List<SearchMatch> Matches);
public sealed record SearchResult(List<SearchFile> Files, int Total, bool Truncated, int FilesSearched);

/// <summary>Text search over the files of a workspace folder, with the editor's unsaved texts taking the place of the files on disk.</summary>
public static class WorkspaceSearch
{
    static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".hg", ".svn", "__pycache__", "node_modules", ".venv", "venv", "env", ".tox", ".mypy_cache", ".pytest_cache", ".ruff_cache", "bin", "obj", "dist", "build", ".idea", ".vs", ".pymagic" };

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".py", ".pyi", ".pyw", ".txt", ".md", ".rst", ".toml", ".cfg", ".ini", ".json", ".yaml", ".yml", ".csv", ".html", ".css", ".js", ".ts", ".sh", ".bat", ".ps1", ".sql", ".xml", ".lock", ".in" };

    const long MaxFileBytes = 2 * 1024 * 1024;

    /// <exception cref="ServiceException">The query is empty or not a valid regular expression.</exception>
    public static SearchResult Run(string root, SearchOptions options, IReadOnlyDictionary<string, string>? unsaved = null)
    {
        if (string.IsNullOrEmpty(options.Query)) throw new ServiceException("Type something to search for.");
        var pattern = BuildPattern(options);
        var include = Globs(options.Include);
        var exclude = Globs(options.Exclude);

        var files = new List<SearchFile>();
        int total = 0, searched = 0;
        bool truncated = false;
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var rel in Candidates(root, unsaved))
        {
            if (include.Count > 0 && !include.Any(g => g.IsMatch(rel))) continue;
            if (exclude.Any(g => g.IsMatch(rel))) continue;
            string? text = unsaved != null && unsaved.TryGetValue(rel, out var open) ? open : Read(Path.Combine(root, rel));
            if (text == null) continue;
            searched++;

            var matches = new List<SearchMatch>();
            int lineNo = 0;
            foreach (var line in Lines(text))
            {
                foreach (Match m in pattern.Matches(line))
                {
                    if (m.Length == 0) continue;
                    if (total >= options.MaxResults) { truncated = true; break; }
                    string? replacement = options.Replace == null ? null : options.Regex ? m.Result(options.Replace) : options.Replace;
                    string shown = line.Length > 400 ? line[..400] : line;
                    matches.Add(new SearchMatch(lineNo, m.Index, m.Length, shown, replacement));
                    total++;
                }
                lineNo++;
                if (truncated) break;
            }
            if (matches.Count > 0) files.Add(new SearchFile(rel, matches));
            if (truncated) break;
        }
        return new SearchResult(files, total, truncated, searched);
    }

    static Regex BuildPattern(SearchOptions o)
    {
        string body = o.Regex ? o.Query : Regex.Escape(o.Query);
        if (o.WholeWord) body = @"(?<![\p{L}\p{N}_])(?:" + body + @")(?![\p{L}\p{N}_])";
        try
        {
            return new Regex(body, (o.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException e)
        {
            throw new ServiceException("Not a valid regular expression: " + e.Message);
        }
    }

    /// <summary>Lines split on \n, \r\n or \r, without the terminators.</summary>
    static IEnumerable<string> Lines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r') continue;
            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        yield return text[start..];
    }

    static IEnumerable<string> Candidates(string root, IReadOnlyDictionary<string, string>? unsaved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        void Walk(string dir)
        {
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var f in files)
                if (TextExtensions.Contains(Path.GetExtension(f)) || Path.GetFileName(f).StartsWith("requirements", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (seen.Add(rel)) list.Add(rel);
                }
            foreach (var d in dirs)
                if (!SkipDirectories.Contains(Path.GetFileName(d)) && !ProjectFolders.IsExcluded(d)) Walk(d);
        }
        if (Directory.Exists(root)) Walk(root);
        if (unsaved != null) foreach (var rel in unsaved.Keys) if (seen.Add(rel)) list.Add(rel);   // a new file that was not saved yet
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>The text of a file, or null for files that are too large, unreadable or not text.</summary>
    static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) >= 0) return null;
            return new UTF8Encoding(false, false).GetString(bytes).TrimStart('﻿');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------ globs

    static List<Regex> Globs(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Glob).ToList();

    /// <summary>`*` stays inside a folder, `**` crosses folders, `?` is one character; a pattern without `/` matches the file name anywhere, and a folder name matches everything below it.</summary>
    static Regex Glob(string pattern)
    {
        pattern = pattern.Replace('\\', '/').TrimStart('/');
        bool anywhere = !pattern.Contains('/');
        bool folder = pattern.EndsWith('/');
        pattern = pattern.TrimEnd('/');
        var sb = new StringBuilder("^");
        if (anywhere) sb.Append("(?:.*/)?");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        // `src` or `src/` also covers what is inside the folder
        sb.Append(folder || !pattern.Contains('*') ? "(?:/.*)?$" : "$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
