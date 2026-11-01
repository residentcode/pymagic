using System.Security.Cryptography;
using System.Text;

namespace PyMagic.Server;

public sealed record HistoryEntry(string Id, DateTime Time, int Bytes);

/// <summary>A moment of the whole project that was marked, by hand or by PyMagic before something big (a refactoring, a test run, a Git update).</summary>
public sealed record HistoryLabel(string Id, DateTime Time, string Name, bool Auto);

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
        PymagicFolder.IgnoreInGit(Path.Combine(root, ".pymagic"), "history/");   // written now, so a later label does not change a committed .gitignore under a Git operation
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
        if (r.StartsWith(".pymagic/", StringComparison.OrdinalIgnoreCase) || r.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) || r.StartsWith(".scratches/", StringComparison.OrdinalIgnoreCase)) return false;
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

    /// <summary>The versions of every file under a folder ('' for the project), deleted files too, the newest first: (file, version).</summary>
    public IReadOnlyList<(string Path, HistoryEntry Entry)> ListFolder(string dirRel, int max = 500)
    {
        string dir = dirRel.Replace('\\', '/').Trim('/');
        string store = Path.Combine(_root, ".pymagic", "history");
        var found = new List<(string, HistoryEntry)>();
        if (!Directory.Exists(store)) return found;
        foreach (var folder in Directory.GetDirectories(store))
        {
            string marker = Path.Combine(folder, "path.txt");
            string rel;
            try { if (!File.Exists(marker)) continue; rel = File.ReadAllText(marker).Trim().Replace('\\', '/'); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (dir.Length > 0 && !rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in List(rel)) found.Add((rel, entry));
        }
        return found.OrderByDescending(x => x.Item2.Id, StringComparer.Ordinal).Take(max).ToList();
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

    // ------------------------------------------------------------ labels: moments of the whole project

    /// <summary>The most labels kept; the oldest made by PyMagic go first.</summary>
    public const int MaxLabels = 120;

    string LabelsFile => Path.Combine(_root, ".pymagic", "history", "labels.json");

    public IReadOnlyList<HistoryLabel> Labels()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(LabelsFile)) return Array.Empty<HistoryLabel>();
                var list = System.Text.Json.JsonSerializer.Deserialize<List<HistoryLabel>>(File.ReadAllText(LabelsFile)) ?? new();
                return list.OrderByDescending(l => l.Id, StringComparer.Ordinal).ToList();
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return Array.Empty<HistoryLabel>(); }
        }
    }

    void SaveLabels(List<HistoryLabel> labels)
    {
        while (labels.Count > MaxLabels)
        {
            var drop = labels.Where(l => l.Auto).OrderBy(l => l.Id, StringComparer.Ordinal).FirstOrDefault() ?? labels.OrderBy(l => l.Id, StringComparer.Ordinal).First();
            labels.Remove(drop);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(LabelsFile)!);
        File.WriteAllText(LabelsFile, System.Text.Json.JsonSerializer.Serialize(labels.OrderBy(l => l.Id, StringComparer.Ordinal).ToList()));
    }

    /// <summary>
    /// Marks the project as it is now: every file given is kept as a version first (when it differs from its newest), so the state at the
    /// label is known for each of them, then the label is made.
    /// </summary>
    public HistoryLabel PutLabel(string name, bool auto, IEnumerable<(string Rel, string Text)> files)
    {
        foreach (var (rel, text) in files) Record(rel, text);
        lock (_gate)
        {
            var labels = Labels().ToList();
            long ticks = DateTime.UtcNow.Ticks;
            if (labels.Count > 0 && long.TryParse(labels[0].Id, out var last) && ticks <= last) ticks = last + 1;
            var made = new HistoryLabel(ticks.ToString("D19"), new DateTime(ticks, DateTimeKind.Utc), name.Trim().Length == 0 ? "Label" : name.Trim(), auto);
            labels.Add(made);
            PymagicFolder.IgnoreInGit(Path.Combine(_root, ".pymagic"), "history/");
            SaveLabels(labels);
            return made;
        }
    }

    public bool RemoveLabel(string id)
    {
        lock (_gate)
        {
            var labels = Labels().ToList();
            bool removed = labels.RemoveAll(l => l.Id == id) > 0;
            if (removed) SaveLabels(labels);
            return removed;
        }
    }

    /// <summary>The text of the version just older than this one, or null when it is the first.</summary>
    public string? Before(string rel, string id)
    {
        var older = List(rel).Where(e => string.CompareOrdinal(e.Id, id) < 0).FirstOrDefault();   // List is newest first
        return older == null ? null : Read(rel, older.Id);
    }

    /// <summary>What every file with a history held at a label (the newest version not after it): path and text, null when it had no version yet.</summary>
    public IReadOnlyList<(string Path, string? Text)> StateAt(string labelId)
    {
        string store = Path.Combine(_root, ".pymagic", "history");
        var state = new List<(string, string?)>();
        if (!Directory.Exists(store)) return state;
        foreach (var folder in Directory.GetDirectories(store))
        {
            string marker = Path.Combine(folder, "path.txt");
            string rel;
            try { if (!File.Exists(marker)) continue; rel = File.ReadAllText(marker).Trim().Replace('\\', '/'); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var at = List(rel).FirstOrDefault(e => string.CompareOrdinal(e.Id, labelId) <= 0);
            state.Add((rel, at == null ? null : Read(rel, at.Id)));
        }
        return state.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
