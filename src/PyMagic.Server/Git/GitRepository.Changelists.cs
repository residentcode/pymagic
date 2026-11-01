using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Git;

/// <summary>A named group of changed files. Which files belong to which group is kept in .pymagic/changelists.json.</summary>
public sealed record Changelist(string Name, List<string> Files);

/// <param name="Active">The list that new changes go into.</param>
public sealed record ChangelistState(string Active, List<Changelist> Lists);

public sealed record ShelfFile(string Path, string Status);

/// <summary>A set of changes put away as a patch in .pymagic/shelf/.</summary>
public sealed record ShelvedSet(string Id, string Name, string Time, string? Changelist, string? Branch, List<ShelfFile> Files);

public sealed record UnshelveResult(bool Conflicts, List<string> ConflictFiles, List<string> Files, string Output);

/// <summary>
/// Changelists (grouping the changed files before a commit) and the shelf (putting changes away as a patch, apart from the stash).
/// Both are project files under .pymagic/ so they travel with the project.
/// </summary>
public sealed partial class GitRepository
{
    public const string DefaultChangelist = "Changes";

    readonly object _clLock = new();
    string ListsFile => Path.Combine(_dir, ".pymagic", "changelists.json");
    string ShelfDir => Path.Combine(_dir, ".pymagic", "shelf");

    /// <summary>Files PyMagic writes itself: they are never in a changelist (the page shows them in the default one) and are never shelved.</summary>
    public static bool IsAppFile(string path)
    {
        string p = path.Replace('\\', '/');
        return p == ".pymagic/changelists.json" || p.StartsWith(".pymagic/shelf/", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ changelists

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    ChangelistState LoadLists(out bool existed)
    {
        existed = File.Exists(ListsFile);
        var state = new ChangelistState(DefaultChangelist, new List<Changelist>());
        if (existed)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ListsFile, Encoding.UTF8));
                string active = doc.RootElement.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? DefaultChangelist : DefaultChangelist;
                var lists = new List<Changelist>();
                if (doc.RootElement.TryGetProperty("lists", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                    {
                        if (!l.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                        var files = new List<string>();
                        if (l.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
                            foreach (var f in fs.EnumerateArray()) if (f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } s) files.Add(s);
                        lists.Add(new Changelist(n.GetString()!, files));
                    }
                state = new ChangelistState(active, lists);
            }
            catch (JsonException) { /* a damaged file starts over: everything is in the default list */ }
        }
        // the default list is always there, first; a name is used once; a file is in one list
        var clean = new List<Changelist>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var def = state.Lists.FirstOrDefault(l => l.Name == DefaultChangelist) ?? new Changelist(DefaultChangelist, new List<string>());
        foreach (var l in new[] { def }.Concat(state.Lists.Where(l => l != def)))
        {
            if (!seen.Add(l.Name)) continue;
            clean.Add(new Changelist(l.Name, l.Files.Where(f => !IsAppFile(f) && owned.Add(f)).ToList()));
        }
        string act = clean.Any(l => l.Name == state.Active) ? state.Active : DefaultChangelist;
        return new ChangelistState(act, clean);
    }

    static string Serialise(ChangelistState s) => JsonSerializer.Serialize(new { active = s.Active, lists = s.Lists }, Pretty);

    void SaveLists(ChangelistState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ListsFile)!);
        string temp = ListsFile + ".tmp";
        File.WriteAllText(temp, Serialise(s) + "\n", new UTF8Encoding(false));
        File.Move(temp, ListsFile, true);
    }

    /// <summary>The lists as they are now: files that are no longer changes dropped, new changes put into the active list.</summary>
    public ChangelistState Changelists()
    {
        lock (_clLock)
        {
            var state = LoadLists(out bool existed);
            var changed = Status().Files.Select(f => f.Path).Where(p => !IsAppFile(p)).ToList();
            var changedSet = changed.ToHashSet();
            var before = Serialise(state);
            foreach (var l in state.Lists) l.Files.RemoveAll(f => !changedSet.Contains(f));
            var owned = state.Lists.SelectMany(l => l.Files).ToHashSet();
            var target = state.Lists.First(l => l.Name == state.Active);
            foreach (var p in changed) if (owned.Add(p)) target.Files.Add(p);
            // nothing to remember until someone makes a list of their own: no file for a project that never used the feature
            bool plain = state.Active == DefaultChangelist && state.Lists.Count == 1;
            if (Serialise(state) != before && (existed || !plain)) SaveLists(state);
            return state;
        }
    }

    static string CheckListName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0 || n.Length > 80 || n.Any(char.IsControl)) throw new GitException("a changelist needs a name of up to 80 characters");
        return n;
    }

    public ChangelistState CreateChangelist(string? name, bool makeActive)
    {
        string n = CheckListName(name);
        var state = Changelists();
        lock (_clLock)
        {
            if (state.Lists.Any(l => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))) throw new GitException($"there is a changelist '{n}' already");
            state.Lists.Add(new Changelist(n, new List<string>()));
            var next = new ChangelistState(makeActive ? n : state.Active, state.Lists);
            SaveLists(next);
            return next;
        }
    }

    public ChangelistState RenameChangelist(string from, string? to)
    {
        string n = CheckListName(to);
        var state = Changelists();
        lock (_clLock)
        {
            if (from == DefaultChangelist) throw new GitException($"'{DefaultChangelist}' is the default changelist: it keeps its name");
            if (state.Lists.All(l => l.Name != from)) throw new GitException($"there is no changelist '{from}'");
            if (state.Lists.Any(l => l.Name != from && string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))) throw new GitException($"there is a changelist '{n}' already");
            var lists = state.Lists.Select(l => l.Name == from ? new Changelist(n, l.Files) : l).ToList();
            var next = new ChangelistState(state.Active == from ? n : state.Active, lists);
            SaveLists(next);
            return next;
        }
    }

    /// <summary>Removes a list; its files go to the default one.</summary>
    public ChangelistState DeleteChangelist(string name)
    {
        var state = Changelists();
        lock (_clLock)
        {
            if (name == DefaultChangelist) throw new GitException($"'{DefaultChangelist}' is the default changelist: it cannot be deleted");
            var gone = state.Lists.FirstOrDefault(l => l.Name == name) ?? throw new GitException($"there is no changelist '{name}'");
            state.Lists.First(l => l.Name == DefaultChangelist).Files.AddRange(gone.Files);
            state.Lists.Remove(gone);
            var next = new ChangelistState(state.Active == name ? DefaultChangelist : state.Active, state.Lists);
            SaveLists(next);
            return next;
        }
    }

    public ChangelistState SetActiveChangelist(string name)
    {
        var state = Changelists();
        lock (_clLock)
        {
            if (state.Lists.All(l => l.Name != name)) throw new GitException($"there is no changelist '{name}'");
            var next = new ChangelistState(name, state.Lists);
            SaveLists(next);
            return next;
        }
    }

    /// <summary>Moves files into a list (out of the one they were in).</summary>
    public ChangelistState MoveToChangelist(IReadOnlyList<string> paths, string name)
    {
        var state = Changelists();
        lock (_clLock)
        {
            var target = state.Lists.FirstOrDefault(l => l.Name == name) ?? throw new GitException($"there is no changelist '{name}'");
            var moving = paths.Select(CheckPath).Distinct().Where(p => !IsAppFile(p)).ToList();
            foreach (var l in state.Lists) l.Files.RemoveAll(moving.Contains);
            target.Files.AddRange(moving);
            SaveLists(state);
            return state;
        }
    }

    /// <summary>Throws unless every path is in the list (a file no list holds, such as PyMagic's own, counts as the default list's).</summary>
    public void RequireInChangelist(string name, IEnumerable<string> paths)
    {
        var state = Changelists();
        var list = state.Lists.FirstOrDefault(l => l.Name == name) ?? throw new GitException($"there is no changelist '{name}'");
        foreach (var raw in paths)
        {
            string p = CheckPath(raw);
            bool inList = list.Files.Contains(p) || name == DefaultChangelist && IsAppFile(p);
            if (!inList) throw new GitException($"'{p}' is not in the changelist '{name}'");
        }
    }

    // ------------------------------------------------------------ the shelf

    static readonly Regex ShelfId = new(@"^[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$", RegexOptions.Compiled);

    static string CheckShelfId(string id) => ShelfId.IsMatch(id ?? "") ? id! : throw new GitException("there is no such shelved set");

    string MetaPath(string id) => Path.Combine(ShelfDir, CheckShelfId(id) + ".json");
    string PatchPath(string id) => Path.Combine(ShelfDir, CheckShelfId(id) + ".patch");

    string EmptyTree()
    {
        var r = Run("hash-object", "-t", "tree", "--stdin");
        return r.Code == 0 && r.Out.Trim().Length >= 40 ? r.Out.Trim() : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    }

    (int Code, string Out, string Err) InIndex(string index, string? stdin, params string[] args) =>
        Execute(_git, _dir, args, stdin, 60_000, new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index });

    /// <summary>
    /// Puts the changes of these files away: the patch (last commit against the working tree, new files and removals included, binary files
    /// too) is written to .pymagic/shelf/ and the files are brought back to the last commit. Unlike a stash it takes only the files given.
    /// </summary>
    public ShelvedSet Shelve(IReadOnlyList<string> paths, string? name, string? changelist)
    {
        var wanted = paths.Select(CheckPath).Distinct().Where(p => !IsAppFile(p)).ToHashSet();
        var status = Status();
        var files = status.Files.Where(f => wanted.Contains(f.Path)).ToList();
        if (files.Count == 0) throw new GitException("there is nothing to shelve in these files");
        if (files.Any(f => f.IsConflict)) throw new GitException("a file with a conflict cannot be shelved: resolve it first");

        var pathspec = files.Select(f => f.Path).Concat(files.Where(f => f.OriginalPath != null).Select(f => CheckPath(f.OriginalPath!))).Distinct().ToList();
        string temp = Path.Combine(Path.GetTempPath(), "pymagic_shelf_" + Guid.NewGuid().ToString("N"));
        string patch;
        try
        {
            string Must((int Code, string Out, string Err) r) => r.Code == 0 ? r.Out : throw new GitException(Message(r));
            Must(InIndex(temp, null, status.HasCommits ? new[] { "read-tree", "HEAD" } : new[] { "read-tree", "--empty" }));
            var add = new List<string> { "add", "-A", "--" };
            add.AddRange(pathspec);
            Must(InIndex(temp, null, add.ToArray()));
            patch = Must(InIndex(temp, null, "diff", "--cached", "--binary", "--no-color", "--no-ext-diff", "--no-renames", status.HasCommits ? "HEAD" : EmptyTree()));
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            try { File.Delete(temp + ".lock"); } catch (IOException) { }
        }
        if (patch.Trim().Length == 0) throw new GitException("these files have no changes to shelve");

        var now = DateTime.Now;
        string id = now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..6].ToLowerInvariant();
        string title = string.IsNullOrWhiteSpace(name) ? (changelist ?? "Shelved changes") : name.Trim();
        var shelved = files.Select(f => new ShelfFile(f.Path, f.IsUntracked || f.Index == 'A' ? "A" : f.Worktree == 'D' || f.Index == 'D' ? "D" : "M")).ToList();
        var set = new ShelvedSet(id, title, now.ToString("yyyy-MM-ddTHH:mm:ss"), changelist, status.Detached ? null : status.Branch, shelved);
        Directory.CreateDirectory(ShelfDir);
        File.WriteAllText(PatchPath(id), patch, new UTF8Encoding(false));
        File.WriteAllText(MetaPath(id), JsonSerializer.Serialize(set, Pretty) + "\n", new UTF8Encoding(false));
        try { Discard(files.Select(f => f.Path).ToList()); }
        catch (GitException e) { throw new GitException($"the changes were shelved as '{title}' but could not all be taken out of the working tree: {e.Message}"); }
        return set;
    }

    ShelvedSet ReadMeta(string id)
    {
        string file = MetaPath(id);
        if (!File.Exists(file)) throw new GitException("there is no such shelved set");
        try { return JsonSerializer.Deserialize<ShelvedSet>(File.ReadAllText(file, Encoding.UTF8), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new GitException("the shelved set is damaged"); }
        catch (JsonException) { throw new GitException("the shelved set is damaged"); }
    }

    /// <summary>The shelved sets, newest first.</summary>
    public List<ShelvedSet> ShelvedSets()
    {
        var list = new List<ShelvedSet>();
        if (!Directory.Exists(ShelfDir)) return list;
        foreach (var f in Directory.EnumerateFiles(ShelfDir, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(f);
            if (!ShelfId.IsMatch(id) || !File.Exists(PatchPath(id))) continue;
            try { list.Add(ReadMeta(id)); } catch (GitException) { }
        }
        return list.OrderByDescending(s => s.Id).ToList();
    }

    /// <summary>The patch of a shelved set, as text (the diff to show).</summary>
    public string ShelfPatch(string id)
    {
        ReadMeta(id);
        return File.ReadAllText(PatchPath(id), new UTF8Encoding(false));
    }

    public void DeleteShelf(string id)
    {
        ReadMeta(id);
        File.Delete(PatchPath(id));
        File.Delete(MetaPath(id));
    }

    /// <summary>
    /// Brings a shelved set back into the working tree. The patch is applied as it is when the files are as they were; otherwise it is merged
    /// three ways (the patch knows the blobs it was made on), and a clash leaves the usual markers in the file and is reported. Nothing is
    /// staged. A set is removed afterwards only when <paramref name="delete"/> is true and nothing clashed.
    /// </summary>
    public UnshelveResult Unshelve(string id, string? changelist, bool delete)
    {
        var meta = ReadMeta(id);
        string patch = PatchPath(id);
        var paths = meta.Files.Select(f => CheckPath(f.Path)).ToList();
        var conflicts = new List<string>();
        string output;

        var check = Run("apply", "--check", "--whitespace=nowarn", patch);
        if (check.Code == 0)
        {
            output = Combined(RunOr("apply", "--whitespace=nowarn", patch));
        }
        else
        {
            // a private index that matches the working tree for these files, so that the merge may use the working tree as it is
            string temp = Path.Combine(Path.GetTempPath(), "pymagic_unshelve_" + Guid.NewGuid().ToString("N"));
            try
            {
                bool hasCommits = Status().HasCommits;
                var rt = InIndex(temp, null, hasCommits ? new[] { "read-tree", "HEAD" } : new[] { "read-tree", "--empty" });
                if (rt.Code != 0) throw new GitException(Message(rt));
                var present = paths.Where(p => File.Exists(Path.Combine(_dir, p))).ToList();
                var absent = paths.Except(present).ToList();
                if (present.Count > 0)
                {
                    var add = new List<string> { "add", "-A", "--" };
                    add.AddRange(present);
                    var a = InIndex(temp, null, add.ToArray());
                    if (a.Code != 0) throw new GitException(Message(a));
                }
                foreach (var p in absent) InIndex(temp, null, "update-index", "--force-remove", "--", p);
                var r = InIndex(temp, null, "apply", "--3way", "--whitespace=nowarn", patch);
                output = Combined(r);
                if (r.Code != 0)
                {
                    var unmerged = InIndex(temp, null, "diff", "--name-only", "--diff-filter=U");
                    conflicts = unmerged.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).Select(x => RelativeToWorkspace(x) ?? x).ToList();
                    if (conflicts.Count == 0)
                        throw new GitException("the shelved changes do not fit the files as they are now: " + Message(r));
                }
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
                try { File.Delete(temp + ".lock"); } catch (IOException) { }
            }
        }

        // the files come back as changes in the list they left (or the one asked for)
        var state = Changelists();
        string target = changelist is { Length: > 0 } && state.Lists.Any(l => l.Name == changelist) ? changelist
            : meta.Changelist is { Length: > 0 } && state.Lists.Any(l => l.Name == meta.Changelist) ? meta.Changelist : state.Active;
        var nowChanged = Status().Files.Select(f => f.Path).ToHashSet();
        var back = paths.Where(nowChanged.Contains).ToList();
        if (back.Count > 0) MoveToChangelist(back, target);
        if (delete && conflicts.Count == 0) DeleteShelf(id);
        return new UnshelveResult(conflicts.Count > 0, conflicts, paths, output);
    }
}
