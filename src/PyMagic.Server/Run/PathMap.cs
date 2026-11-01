namespace PyMagic.Server;

/// <summary>
/// Path mappings of a debug server: which folder of this computer a folder of the machine the program runs on is
/// (<c>/app/src</c> there is <c>C:\proj\src</c> here). The longest matching folder wins; a path outside every mapping is
/// returned unchanged.
/// </summary>
public sealed class PathMap
{
    public sealed record Entry(string Local, string Remote);

    readonly List<Entry> _entries;
    public IReadOnlyList<Entry> Entries => _entries;

    public PathMap(IEnumerable<Entry> entries) => _entries = entries.ToList();

    static string Slashes(string p) => p.Replace('\\', '/');
    static string Trim(string p) { string s = Slashes(p); return s.Length > 1 ? s.TrimEnd('/') : s; }

    // a Windows path (C:\x or \\server\share) is compared without regard to case, a Unix path exactly
    static bool Windows(string p) => p.Length >= 2 && (p[1] == ':' || p.StartsWith("\\\\") || p.StartsWith("//"));

    /// <summary>The path <paramref name="path"/> has below <paramref name="folder"/> ("" for the folder itself), or null when it is not below it.</summary>
    static string? Below(string path, string folder)
    {
        string p = Trim(path), f = Trim(folder);
        if (f == "/") f = "";
        var cmp = Windows(folder) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (p.Equals(f, cmp)) return "";
        return p.StartsWith(f + "/", cmp) ? p[f.Length..] : null;
    }

    static string Join(string folder, string rest)
    {
        char sep = Windows(folder) ? '\\' : '/';
        string head = folder.Length > 1 ? folder.TrimEnd('/', '\\') : folder;
        if (rest.Length == 0) return folder;
        if (head == "/") head = "";
        return head + rest.Replace('/', sep);
    }

    string Map(string path, bool toRemote)
    {
        Entry? best = null;
        string? bestRest = null;
        foreach (var e in _entries)
        {
            string from = toRemote ? e.Local : e.Remote;
            if (Below(path, from) is not { } rest) continue;
            if (best == null || Trim(from).Length > Trim(toRemote ? best.Local : best.Remote).Length) { best = e; bestRest = rest; }
        }
        return best == null ? path : Join(toRemote ? best.Remote : best.Local, bestRest!);
    }

    /// <summary>A file of this computer as the program's machine names it.</summary>
    public string ToRemote(string local) => Map(local, true);

    /// <summary>A file of the program's machine as it is named here.</summary>
    public string ToLocal(string remote) => Map(remote, false);

    /// <summary>
    /// Makes the map of (local, remote) folder pairs: a relative or empty local folder is the project's; a pair without a remote folder is skipped.
    /// </summary>
    public static PathMap Parse(string root, IEnumerable<(string Local, string Remote)> pairs)
    {
        var list = new List<Entry>();
        foreach (var (local, remote) in pairs)
        {
            string l = local.Trim(), r = remote.Trim();
            if (r.Length == 0) continue;
            if (l.Length == 0 || l == ".") l = root;
            else if (!Path.IsPathRooted(l)) l = Path.GetFullPath(Path.Combine(root, l));
            list.Add(new Entry(l, r));
        }
        return new PathMap(list);
    }
}
