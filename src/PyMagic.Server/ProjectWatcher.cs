using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PyMagic.Server;

/// <summary>
/// The changes made to the project's files from outside the page (a script in the terminal, another editor, git on the command line), for the page to
/// follow: one watcher on the project folder, each change handed to every page that listens as its path relative to the folder. What is not the
/// user's work is left out: `.git`, `.pymagic`, caches, virtual environments and `node_modules`.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    readonly string _root;
    FileSystemWatcher? _watcher;
    readonly ConcurrentDictionary<Channel<string>, byte> _listeners = new();
    readonly object _gate = new();

    public ProjectWatcher(string root) { _root = Path.GetFullPath(root).TrimEnd('\\', '/'); }

    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".pymagic", "__pycache__", "node_modules", ".venv", "venv", ".conda", "env", ".tox", ".nox", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".idea", ".vs", "site-packages",
    };

    /// <summary>True for a path (relative, with / or \) the page has no use for: inside one of the skipped folders, or an editor's temporary file.</summary>
    public static bool Ignored(string rel)
    {
        var parts = rel.Split('/', '\\');
        if (parts.Take(parts.Length - 1).Any(SkippedFolders.Contains) || SkippedFolders.Contains(parts[^1])) return true;
        string name = parts[^1];
        return name.EndsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".#", StringComparison.Ordinal);
    }

    /// <summary>A new listener: what changes from now on, as `kind|path` (changed, created, deleted; `all|*` when there were too many to tell), until it is given back with <see cref="Unsubscribe"/>.</summary>
    public ChannelReader<string> Subscribe(out Channel<string> handle)
    {
        handle = Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });
        _listeners[handle] = 0;
        Start();
        return handle.Reader;
    }

    public void Unsubscribe(Channel<string> handle)
    {
        _listeners.TryRemove(handle, out _);
        handle.Writer.TryComplete();
    }

    void Start()
    {
        lock (_gate)
        {
            if (_watcher != null || !Directory.Exists(_root)) return;
            try
            {
                var w = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Changed += (_, e) => Tell("changed", e.FullPath);
                w.Created += (_, e) => Tell("created", e.FullPath);
                w.Deleted += (_, e) => Tell("deleted", e.FullPath);
                w.Renamed += (_, e) => { Tell("deleted", e.OldFullPath); Tell("created", e.FullPath); };
                // too many changes at once (a checkout of another branch): the page reads everything again
                w.Error += (_, _) => TellAll("all|*");
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException) { /* no watching: the page refreshes when it gets the focus */ }
        }
    }

    /// <summary>A virtual environment was made in the project folder: the name of its folder (.venv, venv, ...), when its pyvenv.cfg is written.</summary>
    public event Action<string>? EnvironmentMade;

    static readonly string[] EnvironmentNames = { ".venv", "venv", "env", "virtualenv" };

    /// <summary>The environment folder a change is the pyvenv.cfg of (`.venv/pyvenv.cfg`), or null.</summary>
    public static string? EnvironmentOf(string rel)
    {
        var parts = rel.Split('/', '\\');
        return parts.Length == 2 && parts[1].Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase) && EnvironmentNames.Contains(parts[0], StringComparer.OrdinalIgnoreCase) ? parts[0] : null;
    }

    readonly ConcurrentDictionary<string, DateTime> _environmentsTold = new(StringComparer.OrdinalIgnoreCase);

    void Tell(string kind, string full)
    {
        string rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (kind != "deleted" && EnvironmentOf(rel) is { } env)
        {
            // created, then written: told once
            var now = DateTime.UtcNow;
            if (!_environmentsTold.TryGetValue(env, out var last) || now - last > TimeSpan.FromSeconds(30))
            {
                _environmentsTold[env] = now;
                EnvironmentMade?.Invoke(env);
            }
        }
        if (NodeModulesOf(rel) is { } modules) NodePackagesTouched(modules);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Ignored(rel)) return;
        TellAll(kind + "|" + rel);
    }

    /// <summary>How long after the last change in a node_modules its install is taken as done (npm writes thousands of files).</summary>
    public TimeSpan NodeSettle { get; set; } = TimeSpan.FromSeconds(2);

    readonly ConcurrentDictionary<string, Timer> _nodeTimers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The node_modules folder (relative, with /) a change says a package came or went in: the folder itself, a package at its top
    /// (`node_modules/express`, `node_modules/@types/node`), or npm's record of the install (`node_modules/.package-lock.json`); null for anything
    /// deeper (the files inside a package) or outside one.
    /// </summary>
    public static string? NodeModulesOf(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/');
        // node_modules inside a package is that package's own: the project's is the first one on the path
        int at = Array.FindIndex(parts, p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (at < 0) return null;
        int depth = parts.Length - at - 1;
        bool top = depth == 0 || depth == 1 || depth == 2 && parts[at + 1].StartsWith('@');
        return top ? string.Join('/', parts.Take(at + 1)) : null;
    }

    void NodePackagesTouched(string modules)
    {
        // one notice when the install settles: `node|path/of/node_modules`
        _nodeTimers.AddOrUpdate(modules,
            m => new Timer(_ => { if (_nodeTimers.TryRemove(m, out var t)) t.Dispose(); TellAll("node|" + m); }, null, NodeSettle, Timeout.InfiniteTimeSpan),
            (_, existing) => { existing.Change(NodeSettle, Timeout.InfiniteTimeSpan); return existing; });
    }

    /// <summary>Hands something to every page that listens, as `kind|text` (the interpreter the host chose: `interpreter|label`).</summary>
    public void Announce(string kind, string text) => TellAll(kind + "|" + text);

    void TellAll(string rel)
    {
        foreach (var listener in _listeners.Keys) listener.Writer.TryWrite(rel);
    }

    public void Dispose()
    {
        lock (_gate) { _watcher?.Dispose(); _watcher = null; }
        foreach (var l in _listeners.Keys) l.Writer.TryComplete();
        _listeners.Clear();
    }
}
