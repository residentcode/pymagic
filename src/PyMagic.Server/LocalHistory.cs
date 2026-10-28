using System.Security.Cryptography;
using System.Text;

namespace PyMagic.Server;

public sealed record HistoryEntry(string Id, DateTime Time, int Bytes);

/// <summary>Lines of the project's <c>.pymagic/.gitignore</c>: the caches PyMagic keeps there stay out of version control, the files that are the project's (inspection profile, run configurations, bookmarks) do not.</summary>
public static class PymagicFolder
{
    static readonly object Gate = new();

    public static void IgnoreInGit(string folder, params string[] names)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, ".gitignore");
                var have = File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
                var missing = names.Where(n => !have.Contains(n)).ToList();
                if (missing.Count > 0) File.AppendAllText(file, string.Concat(missing.Select(n => n + "\n")));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* only the ignore file is missing */ }
        }
    }
}

/// <summary>
/// The local history of a file: what it held at each save, newest first, kept in <c>.pymagic/history</c> (not in version control) so that an
/// earlier version, or the one before the first save made here, can be looked at and brought back. A snapshot identical to the latest is not made again.
/// </summary>
public sealed class LocalHistory
{
    /// <summary>The largest file whose versions are kept.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    readonly string _root;
    readonly int _perFile;
    readonly object _gate = new();

    public LocalHistory(string root, int perFile = 60)
    {
        _root = root;
        _perFile = perFile;
    }

    string FolderOf(string rel)
    {
        string key = rel.Replace('\\', '/').Trim('/').ToLowerInvariant();
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(_root, ".pymagic", "history", name);
    }

    static bool Keeps(string rel, string text)
    {
        string r = rel.Replace('\\', '/').TrimStart('/');
        if (r.StartsWith(".pymagic/", StringComparison.OrdinalIgnoreCase) || r.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Length <= MaxFileBytes && !text.Contains('\0');
    }

    /// <summary>Keeps this text as the newest version of the file, unless it is what the newest already is. Returns whether a version was made.</summary>
    public bool Record(string rel, string text, DateTime? at = null)
    {
        if (!Keeps(rel, text)) return false;
        lock (_gate)
        {
            try
            {
                string folder = FolderOf(rel);
                var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.txt").Where(f => Path.GetFileName(f) != "path.txt").OrderBy(f => f, StringComparer.Ordinal).ToList() : new List<string>();
                if (files.Count > 0 && File.ReadAllText(files[^1]) == text) return false;
                Directory.CreateDirectory(folder);
                PymagicFolder.IgnoreInGit(Path.Combine(_root, ".pymagic"), "history/");
                long ticks = (at ?? DateTime.UtcNow).Ticks;
                // two versions in the same instant keep their order
                if (files.Count > 0 && long.TryParse(Path.GetFileNameWithoutExtension(files[^1]), out var last) && ticks <= last) ticks = last + 1;
                string made = Path.Combine(folder, ticks.ToString("D19") + ".txt");
                File.WriteAllText(made, text, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "path.txt"), rel);
                files.Add(made);
                foreach (var old in files.Take(Math.Max(0, files.Count - _perFile))) File.Delete(old);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>The version the file has before the first save made here: kept once, so that the first change can be undone too.</summary>
    public void Baseline(string rel, string text)
    {
        lock (_gate)
        {
            if (List(rel).Count > 0) return;
        }
        Record(rel, text);
    }

    public IReadOnlyList<HistoryEntry> List(string rel)
    {
        lock (_gate)
        {
            string folder = FolderOf(rel);
            if (!Directory.Exists(folder)) return Array.Empty<HistoryEntry>();
            var list = new List<HistoryEntry>();
            foreach (var f in Directory.GetFiles(folder, "*.txt"))
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(f), out var ticks)) continue;
                list.Add(new HistoryEntry(Path.GetFileNameWithoutExtension(f), new DateTime(ticks, DateTimeKind.Utc), (int)new FileInfo(f).Length));
            }
            return list.OrderByDescending(e => e.Id, StringComparer.Ordinal).ToList();
        }
    }

    public string? Read(string rel, string id)
    {
        if (id.Length != 19 || !id.All(char.IsDigit)) return null;
        lock (_gate)
        {
            string file = Path.Combine(FolderOf(rel), id + ".txt");
            try { return File.Exists(file) ? File.ReadAllText(file) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
