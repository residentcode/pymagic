using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Git;

/// <summary>One line of a hunk: ' ' (unchanged), '+' (added) or '-' (removed). <paramref name="NoNewline"/>: the file ends here without a line break.</summary>
public sealed record DiffLine(char Kind, string Text, bool NoNewline = false);

public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Section, List<DiffLine> Lines);

/// <summary>The change of one file against the last commit, in hunks of lines, as `git diff` writes it.</summary>
public sealed class FileDiff
{
    public required string Path { get; init; }
    /// <summary>The lines of the patch before the first hunk (diff --git, index, ---, +++), kept as they are.</summary>
    public List<string> Header { get; init; } = new();
    public bool IsNew { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsBinary { get; init; }
    public List<DiffHunk> Hunks { get; init; } = new();
    /// <summary>Changes with the text: tells a caller that the file is not what its selection was made on.</summary>
    public string Id { get; init; } = "";
}

public static class DiffParser
{
    static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$", RegexOptions.Compiled);

    public static string IdOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Reads the output of `git diff` for one file.</summary>
    public static FileDiff Parse(string path, string diff)
    {
        var header = new List<string>();
        var hunks = new List<DiffHunk>();
        bool isNew = false, isDeleted = false, isBinary = false;
        DiffHunk? current = null;
        foreach (var raw in diff.Split('\n'))
        {
            // the last element after the final newline is empty; a line is never cut at "\r" (a file with Windows line ends keeps them)
            if (current == null)
            {
                var h = HunkHeader.Match(raw);
                if (h.Success) { current = Begin(h, hunks); continue; }
                if (raw.Length == 0) continue;
                header.Add(raw);
                if (raw.StartsWith("new file mode")) isNew = true;
                else if (raw.StartsWith("deleted file mode")) isDeleted = true;
                else if (raw.StartsWith("Binary files") || raw.StartsWith("GIT binary patch")) isBinary = true;
                continue;
            }
            var next = HunkHeader.Match(raw);
            if (next.Success) { current = Begin(next, hunks); continue; }
            if (raw.Length == 0) continue;
            switch (raw[0])
            {
                case ' ': case '+': case '-': current.Lines.Add(new DiffLine(raw[0], raw[1..])); break;
                case '\\':
                    // "\ No newline at end of file" belongs to the line before it
                    if (current.Lines.Count > 0) current.Lines[^1] = current.Lines[^1] with { NoNewline = true };
                    break;
            }
        }
        return new FileDiff { Path = path, Header = header, IsNew = isNew, IsDeleted = isDeleted, IsBinary = isBinary, Hunks = hunks, Id = IdOf(diff) };
    }

    static DiffHunk Begin(Match h, List<DiffHunk> hunks)
    {
        int Num(int g, int missing) => h.Groups[g].Success ? int.Parse(h.Groups[g].Value) : missing;
        var hunk = new DiffHunk(Num(1, 0), Num(2, 1), Num(3, 0), Num(4, 1), h.Groups[5].Value, new List<DiffLine>());
        hunks.Add(hunk);
        return hunk;
    }

    /// <summary>The diff of a file git does not know yet: one hunk that adds every line.</summary>
    public static FileDiff ForNewFile(string path, string repoPath, string text)
    {
        bool binary = text.Contains('\0');
        var lines = new List<DiffLine>();
        if (!binary && text.Length > 0)
        {
            bool endsWithNewline = text.EndsWith('\n');
            var parts = text.Split('\n');
            int count = endsWithNewline ? parts.Length - 1 : parts.Length;
            for (int i = 0; i < count; i++) lines.Add(new DiffLine('+', parts[i], NoNewline: !endsWithNewline && i == count - 1));
        }
        var header = new List<string> { $"diff --git a/{repoPath} b/{repoPath}", "new file mode 100644", "--- /dev/null", $"+++ b/{repoPath}" };
        var hunks = new List<DiffHunk>();
        if (lines.Count > 0) hunks.Add(new DiffHunk(0, 0, 1, lines.Count, "", lines));
        return new FileDiff { Path = path, Header = header, IsNew = true, IsBinary = binary, Hunks = hunks, Id = IdOf(text) };
    }
}

/// <summary>Which changes of a file to take: for each hunk (by its place in the file's diff), the lines (by place in the hunk) of it.</summary>
public sealed class LineSelection
{
    public string Id { get; init; } = "";
    public Dictionary<int, HashSet<int>> Hunks { get; init; } = new();

    public bool IsEmpty => Hunks.Values.All(l => l.Count == 0);
}

public static class PatchBuilder
{
    /// <summary>
    /// A patch that makes only the chosen changes: a chosen line stays as it is, an added line that was not chosen is left out,
    /// and a removed line that was not chosen becomes an unchanged one. Returns null when nothing is chosen.
    /// </summary>
    public static string? Build(FileDiff diff, LineSelection selection)
    {
        if (diff.IsBinary) throw new GitException($"'{diff.Path}' is a binary file: it can only be committed whole");
        if (diff.IsDeleted) throw new GitException($"'{diff.Path}' was deleted: it can only be committed whole");

        var body = new StringBuilder();
        int delta = 0;     // lines the included hunks add so far, for the start of the next hunk in the new file
        bool any = false;
        for (int h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            selection.Hunks.TryGetValue(h, out var chosen);
            var lines = new List<DiffLine>();
            int added = 0, removed = 0;
            for (int i = 0; i < hunk.Lines.Count; i++)
            {
                var line = hunk.Lines[i];
                bool take = chosen != null && chosen.Contains(i);
                if (line.Kind == ' ') lines.Add(line);
                else if (line.Kind == '+') { if (take) { lines.Add(line); added++; } }
                else if (take) { lines.Add(line); removed++; }
                else lines.Add(line with { Kind = ' ' });
            }
            if (added == 0 && removed == 0) continue;
            any = true;
            int oldCount = lines.Count(l => l.Kind != '+'), newCount = lines.Count(l => l.Kind != '-');
            int oldStart = diff.IsNew ? 0 : hunk.OldStart;
            int newStart = diff.IsNew ? 1 : oldStart + delta + (oldCount == 0 ? 1 : 0);
            body.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@{(hunk.Section.Length > 0 ? " " + hunk.Section : "")}\n");
            foreach (var l in lines)
            {
                body.Append(l.Kind).Append(l.Text).Append('\n');
                if (l.NoNewline) body.Append("\\ No newline at end of file\n");
            }
            delta += added - removed;
        }
        if (!any) return null;
        return string.Join("\n", diff.Header) + "\n" + body;
    }
}
