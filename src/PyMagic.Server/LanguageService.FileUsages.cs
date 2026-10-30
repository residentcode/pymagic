namespace PyMagic.Server;

public sealed partial class LanguageService
{
    /// <summary>
    /// Find Usages of a file or folder (the project tree's menu, and Safe Delete): the places that import the module or package or name it in a string,
    /// which are the places a rename of it would change. They are found as such a rename: the plan of a move to a name beside it, each edit a usage.
    /// </summary>
    public List<Location> FileUsages(string uri)
    {
        string? path = PathOfUri(uri);
        if (path == null) return new();
        string full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        string? dir = System.IO.Path.GetDirectoryName(full);
        if (dir == null) return new();
        string probe = System.IO.Path.Combine(dir, Directory.Exists(full) ? "pymagic_usages_probe" : "pymagic_usages_probe" + System.IO.Path.GetExtension(full));
        var plan = PlanModuleMove(uri, UriOfPath(probe));
        return plan.Changes
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => kv.Value.Select(e => new Location(kv.Key, e.Range)))
            .ToList();
    }
}
