using System.Text;

namespace PyMagic.Server.Git;

/// <summary>
/// One change of a file: a block of adjacent added and removed lines, as the Git tab's line chooser sees it. <c>Key</c> is made of its text, so it stays the same
/// when other parts of the file move. <c>List</c> is the list of the change; <c>Lines</c> its lines, each with the list it is in (some may be in another one).
/// </summary>
public sealed record ChangePart(string Key, int OldLine, int NewLine, int Added, int Removed, string Preview, string List, List<ChangeLine> Lines);

/// <summary>One line of a change: '+' or '-', its text, and the changelist it is in.</summary>
public sealed record ChangeLine(char Kind, string Text, string List);

public sealed partial class GitRepository
{
    // ------------------------------------------------------------ changes of a file in changelists
    //
    // A changelist holds files; some of the changes of a file may be moved to another list. What is moved is kept in changelists.json as
    // "parts": { path: { key: list } }, where a key is a hash of the change's own lines (and its number among equal ones). A change that no longer exists
    // (committed, reverted, edited) drops out of the file at the next reading of the lists. A single line of a change can be in a list of its own:
    // its entry is "key@n" (n: the line's place in the change, from 0), and it wins over the change's entry, which wins over the file's list.

    sealed record RawChange(string Key, int Hunk, int First, int Count, int OldLine, int NewLine, string Preview, int Added, int Removed, List<(char Kind, string Text)> Lines);

    /// <summary>The list a change is in (its own entry, else its file's).</summary>
    static string ListOfChange(Dictionary<string, string>? moved, string owner, string key) =>
        moved != null && moved.TryGetValue(key, out var l) ? l : owner;

    /// <summary>The list line <paramref name="n"/> of a change is in.</summary>
    static string ListOfLine(Dictionary<string, string>? moved, string owner, string key, int n) =>
        moved != null && moved.TryGetValue(key + "@" + n, out var l) ? l : ListOfChange(moved, owner, key);

    /// <summary>
    /// Tidies the entries of one file: those of changes or lines that are gone are dropped, an entry that says what would be so without it is dropped,
    /// and a change whose lines all went to one list becomes that list's as a whole.
    /// </summary>
    static void Tidy(Dictionary<string, string> moved, List<RawChange> raw, string owner)
    {
        var count = raw.ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        foreach (var k in moved.Keys.ToList())
        {
            int at = k.LastIndexOf('@');
            bool known = at < 0 ? count.ContainsKey(k)
                : count.TryGetValue(k[..at], out int n) && int.TryParse(k[(at + 1)..], out int i) && i >= 0 && i < n && k[(at + 1)..] == i.ToString();
            if (!known) moved.Remove(k);
        }
        foreach (var c in raw)
        {
            if (moved.TryGetValue(c.Key, out var own) && own == owner) moved.Remove(c.Key);
            var lines = Enumerable.Range(0, c.Count).Select(i => ListOfLine(moved, owner, c.Key, i)).ToList();
            if (lines.Count > 0 && lines.Distinct().Count() == 1)
            {
                // every line in one list: the change is that list's, with no entries for its lines
                for (int i = 0; i < c.Count; i++) moved.Remove(c.Key + "@" + i);
                if (lines[0] == owner) moved.Remove(c.Key); else moved[c.Key] = lines[0];
                continue;
            }
            string whole = ListOfChange(moved, owner, c.Key);
            for (int i = 0; i < c.Count; i++) if (moved.TryGetValue(c.Key + "@" + i, out var l) && l == whole) moved.Remove(c.Key + "@" + i);
        }
    }

    /// <summary>The changes of a file's diff: every run of added and removed lines between unchanged ones.</summary>
    static List<RawChange> RawChanges(FileDiff diff)
    {
        var result = new List<RawChange>();
        if (diff.IsBinary || diff.IsDeleted) return result;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            int oldLine = hunk.OldStart, newLine = hunk.NewStart, i = 0;
            while (i < hunk.Lines.Count)
            {
                if (hunk.Lines[i].Kind == ' ') { oldLine++; newLine++; i++; continue; }
                int first = i, o = oldLine, n = newLine;
                var text = new StringBuilder();
                var lines = new List<(char, string)>();
                string added = "", removed = "";
                int addedCount = 0, removedCount = 0;
                while (i < hunk.Lines.Count && hunk.Lines[i].Kind != ' ')
                {
                    var line = hunk.Lines[i];
                    text.Append(line.Kind).Append(line.Text).Append('\n');
                    lines.Add((line.Kind, line.Text));
                    if (line.Text.Trim().Length > 0) { if (line.Kind == '+' && added.Length == 0) added = line.Text.Trim(); else if (line.Kind == '-' && removed.Length == 0) removed = line.Text.Trim(); }
                    if (line.Kind == '+') { newLine++; addedCount++; } else { oldLine++; removedCount++; }
                    i++;
                }
                string preview = added.Length > 0 ? added : removed.Length > 0 ? "- " + removed : "";   // what was written, or what was taken away
                string key = DiffParser.IdOf(text.ToString());
                seen.TryGetValue(key, out int times);
                seen[key] = times + 1;
                result.Add(new RawChange(times == 0 ? key : key + "#" + times, h, first, i - first, o, n, preview.Length > 70 ? preview[..70] : preview, addedCount, removedCount, lines));
            }
        }
        return result;
    }

    string OwnerOf(ChangelistState state, string path) =>
        state.Lists.FirstOrDefault(l => l.Files.Contains(path))?.Name ?? DefaultChangelist;

    /// <summary>The changes of a file with the list each one is in.</summary>
    public List<ChangePart> ChangeParts(string path)
    {
        string p = CheckPath(path);
        var state = Changelists();
        string owner = OwnerOf(state, p);
        _parts.TryGetValue(p, out var moved);
        return RawChanges(DiffOf(p)).Select(c => new ChangePart(c.Key, c.OldLine, c.NewLine, c.Added, c.Removed, c.Preview, ListOfChange(moved, owner, c.Key),
            c.Lines.Select((l, i) => new ChangeLine(l.Kind, l.Text, ListOfLine(moved, owner, c.Key, i))).ToList())).ToList();
    }

    /// <summary>Puts changes of a file into a list: the ones named by <paramref name="keys"/> (a change that is in the list of its file stays with it).</summary>
    public ChangelistState MoveChanges(string path, IReadOnlyList<string> keys, string list)
    {
        string p = CheckPath(path);
        var state = Changelists();
        lock (_clLock)
        {
            if (state.Lists.All(l => l.Name != list)) throw new GitException($"there is no changelist '{list}'");
            if (IsAppFile(p) || !state.Lists.Any(l => l.Files.Contains(p))) throw new GitException($"'{p}' is not a changed file");
            var raw = RawChanges(DiffOf(p));
            if (raw.Count == 0) throw new GitException($"'{p}' has no changes that can be moved one by one (a binary, deleted or unchanged file moves whole)");
            var known = raw.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            string owner = OwnerOf(state, p);
            if (!_parts.TryGetValue(p, out var moved)) _parts[p] = moved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in keys.Distinct())
            {
                if (!known.Contains(key)) throw new GitException("a change of the file is gone or changed since the list was read: open it again");
                // the whole change: its lines go with it
                foreach (var k in moved.Keys.Where(k => k.StartsWith(key + "@", StringComparison.Ordinal)).ToList()) moved.Remove(k);
                if (list == owner) moved.Remove(key); else moved[key] = list;
            }
            if (moved.Count == 0) _parts.Remove(p);
            SaveLists(state);
        }
        return Changelists();
    }

    /// <summary>
    /// Puts single lines of one change of a file into a list (<paramref name="lines"/>: their places in the change, from 0); the other lines of the change stay where
    /// they are. A commit of that list takes just those lines (an added line is written, a removed one taken out).
    /// </summary>
    public ChangelistState MoveLines(string path, string key, IReadOnlyList<int> lines, string list)
    {
        string p = CheckPath(path);
        var state = Changelists();
        lock (_clLock)
        {
            if (state.Lists.All(l => l.Name != list)) throw new GitException($"there is no changelist '{list}'");
            if (IsAppFile(p) || !state.Lists.Any(l => l.Files.Contains(p))) throw new GitException($"'{p}' is not a changed file");
            var raw = RawChanges(DiffOf(p));
            var change = raw.FirstOrDefault(c => c.Key == key) ?? throw new GitException("a change of the file is gone or changed since the list was read: open it again");
            if (lines.Count == 0) throw new GitException("no line is chosen");
            if (lines.Any(i => i < 0 || i >= change.Count)) throw new GitException("a chosen line is not in that change");
            string owner = OwnerOf(state, p);
            if (!_parts.TryGetValue(p, out var moved)) _parts[p] = moved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var i in lines.Distinct()) moved[key + "@" + i] = list;
            Tidy(moved, raw, owner);
            if (moved.Count == 0) _parts.Remove(p);
            SaveLists(state);
        }
        return Changelists();
    }

    /// <summary>
    /// The lines of a file's diff that belong to <paramref name="list"/>, as a selection (null when the list has none of them), and whether that is every line of the file's changes.
    /// </summary>
    (LineSelection? Selection, bool All) SelectionFor(string path, string list)
    {
        var diff = DiffOf(path);
        var raw = RawChanges(diff);
        var state = Changelists();
        string owner = OwnerOf(state, path);
        _parts.TryGetValue(path, out var moved);
        var sel = new LineSelection { Id = diff.Id };
        int mine = 0;
        int lines = 0;
        foreach (var c in raw)
        {
            lines += c.Count;
            for (int i = 0; i < c.Count; i++)
            {
                if (ListOfLine(moved, owner, c.Key, i) != list) continue;
                mine++;
                if (!sel.Hunks.TryGetValue(c.Hunk, out var set)) sel.Hunks[c.Hunk] = set = new HashSet<int>();
                set.Add(c.First + i);
            }
        }
        return (mine == 0 ? null : sel, mine == lines);
    }

    /// <summary>
    /// What committing a changelist takes: the files whole, and for a file of which only some changes are in the list, just those. <paramref name="partial"/> holds the
    /// choices made by hand in the line chooser; they are kept as they are.
    /// </summary>
    public (List<string> Whole, Dictionary<string, LineSelection> Partial) ResolveForList(string list, IReadOnlyList<string> paths, IReadOnlyDictionary<string, LineSelection> partial)
    {
        Changelists();   // brings _parts up to date
        var whole = new List<string>();
        var result = new Dictionary<string, LineSelection>(partial);
        foreach (var raw in paths)
        {
            string p = CheckPath(raw);
            if (partial.ContainsKey(p)) continue;
            if (!_parts.ContainsKey(p)) { whole.Add(p); continue; }
            var (selection, all) = SelectionFor(p, list);
            if (selection == null) continue;
            if (all) whole.Add(p); else result[p] = selection;
        }
        return (whole, result);
    }

    /// <summary>Puts chosen changes of one file away, leaving the rest of the file as it is: the patch of just those changes is kept on the shelf and taken out of the working tree.</summary>
    public ShelvedSet ShelveChanges(string path, IReadOnlyList<string> keys, string? name, string? changelist)
    {
        string p = CheckPath(path);
        if (IsAppFile(p)) throw new GitException("PyMagic's own files are not shelved");
        var status = Status();
        var file = status.Files.FirstOrDefault(f => f.Path == p) ?? throw new GitException($"'{p}' has no changes");
        if (file.IsConflict) throw new GitException("a file with a conflict cannot be shelved: resolve it first");
        var diff = DiffOf(p);
        if (diff.IsNew || diff.IsBinary || diff.IsDeleted) throw new GitException($"'{p}' is new, deleted or binary: shelve it whole");
        var raw = RawChanges(diff);
        var wanted = keys.Distinct().ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) throw new GitException("no change is chosen");
        if (wanted.Any(k => raw.All(c => c.Key != k))) throw new GitException("a change of the file is gone or changed since the list was read: open it again");
        var sel = new LineSelection { Id = diff.Id };
        foreach (var c in raw.Where(c => wanted.Contains(c.Key)))
        {
            if (!sel.Hunks.TryGetValue(c.Hunk, out var set)) sel.Hunks[c.Hunk] = set = new HashSet<int>();
            for (int i = 0; i < c.Count; i++) set.Add(c.First + i);
        }
        string patch = PatchBuilder.Build(diff, sel) ?? throw new GitException("no change is chosen");
        TakeOut(new[] { patch });
        return KeepOnShelf(patch, new List<ShelfFile> { new ShelfFile(p, "M") }, string.IsNullOrWhiteSpace(name) ? $"{p} (part)" : name.Trim(), changelist, status);
    }

    /// <summary>Takes patches out of the working tree (a reverse apply). Everything is checked before anything is changed.</summary>
    void TakeOut(IReadOnlyList<string> patches)
    {
        foreach (var patch in patches)
        {
            var check = Execute(_git, _dir, new[] { "apply", "-R", "--check", "--recount", "--whitespace=nowarn", "-" }, patch, 60_000, null);
            if (check.Code != 0) throw new GitException("these changes could not be taken out of the working tree, so nothing was shelved: " + Message(check));
        }
        foreach (var patch in patches)
        {
            var r = Execute(_git, _dir, new[] { "apply", "-R", "--recount", "--whitespace=nowarn", "-" }, patch, 60_000, null);
            if (r.Code != 0) throw new GitException("these changes could not be taken out of the working tree: " + Message(r));
        }
    }

    ShelvedSet KeepOnShelf(string patch, List<ShelfFile> files, string title, string? changelist, GitStatus status)
    {
        var now = DateTime.Now;
        string id = now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..6].ToLowerInvariant();
        var set = new ShelvedSet(id, title, now.ToString("yyyy-MM-ddTHH:mm:ss"), changelist, status.Detached ? null : status.Branch, files);
        Directory.CreateDirectory(ShelfDir);
        File.WriteAllText(PatchPath(id), patch, new UTF8Encoding(false));
        File.WriteAllText(MetaPath(id), System.Text.Json.JsonSerializer.Serialize(set, Pretty) + "\n", new UTF8Encoding(false));
        return set;
    }
}
