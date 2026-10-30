using System.Collections.Concurrent;
using System.Text.Json;
using PyMagic.Core.Syntax;

namespace PyMagic.Server;

/// <summary>One name of the project: where it is and what it is (a function, class, method or variable).</summary>
public readonly record struct IndexedSymbol(string Name, int Kind, string Container, int Line, int Character, string Path);

/// <summary>
/// The names defined in the files of the project, kept without analysing anything: each file is only parsed (not bound or evaluated),
/// the result is remembered with the file's modification time and length, and only the files that changed are read again. It is built
/// in the background by every core at once, kept up to date from the file system watcher, and saved in <c>.pymagic/index.json</c> so that
/// the next session starts with it. A query answers from what has been indexed so far.
/// </summary>
public sealed class SymbolIndex : IDisposable
{
    sealed record FileEntry(long Ticks, long Length, IndexedSymbol[] Symbols, string ModuleName, string[] Imports);

    readonly object _gate = new();
    readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<IEnumerable<string>> _projectFiles;
    readonly Func<string, string> _moduleName;
    readonly string? _storePath;
    readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> _watchers = new();
    Timer? _settle;
    bool _needsSave, _reconcileAll, _disposed;
    int _building;

    public SymbolIndex(Func<IEnumerable<string>> projectFiles, Func<string, string> moduleName, string? storePath)
    {
        _projectFiles = projectFiles; _moduleName = moduleName; _storePath = storePath;
    }

    /// <summary>True once every file of the project has been looked at (or taken from the saved index).</summary>
    public bool Ready { get; private set; }

    /// <summary>How many files are indexed.</summary>
    public int Files { get { lock (_gate) return _files.Count; } }

    /// <summary>How many files were parsed since the index was created: those that changed since the saved index, or all of them the first time.</summary>
    public int Parsed { get; private set; }

    /// <summary>Raised on a background thread when files were added, changed or removed.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------ building

    /// <summary>Loads the saved index, then reads what changed since, in the background. <paramref name="roots"/> are watched for further changes.</summary>
    public Task Start(IEnumerable<string> roots)
    {
        foreach (var root in roots) Watch(root);
        return Task.Run(() => { Load(); Reconcile(); });
    }

    /// <summary>Looks at every file of the project: new and changed ones are parsed (in parallel), vanished ones are forgotten.</summary>
    public void Reconcile()
    {
        if (Interlocked.Exchange(ref _building, 1) == 1) { _reconcileAll = true; return; }
        try
        {
            do
            {
                _reconcileAll = false;
                var present = _projectFiles().Select(Path.GetFullPath).ToList();
                var known = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
                lock (_gate) foreach (var (k, v) in _files) known[k] = v;

                var stale = new List<string>();
                foreach (var path in present)
                    if (!known.TryGetValue(path, out var e) || Stamp(path) is not { } s || s.Ticks != e.Ticks || s.Length != e.Length) stale.Add(path);
                var presentSet = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
                var gone = known.Keys.Where(k => !presentSet.Contains(k)).ToList();

                var parsed = new ConcurrentBag<(string Path, FileEntry? Entry)>();
                Parallel.ForEach(stale, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6) }, path => parsed.Add((path, Read(path))));

                lock (_gate)
                {
                    foreach (var g in gone) _files.Remove(g);
                    foreach (var (path, entry) in parsed)
                        if (entry != null) _files[path] = entry; else _files.Remove(path);
                }
                Parsed += stale.Count;
                if (stale.Count > 0 || gone.Count > 0) { _needsSave = true; _importers = null; Changed?.Invoke(); }
            } while (_reconcileAll);
            Ready = true;
        }
        finally { Interlocked.Exchange(ref _building, 0); }
        Save();
    }

    static (long Ticks, long Length)? Stamp(string path)
    {
        try { var i = new FileInfo(path); return i.Exists ? (i.LastWriteTimeUtc.Ticks, i.Length) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    FileEntry? Read(string path)
    {
        const long TooBig = 8 * 1024 * 1024;
        try
        {
            if (Stamp(path) is not { } stamp || stamp.Length > TooBig) return null;
            string text = File.ReadAllText(path);
            var module = Parser.Parse(text).Module;
            var lines = new LineMap(text);
            string name = _moduleName(path);
            var list = new List<IndexedSymbol>();
            void Visit(IEnumerable<DocumentSymbol> symbols, string container)
            {
                foreach (var s in symbols)
                {
                    list.Add(new IndexedSymbol(s.Name, s.Kind, container, s.SelectionRange.Start.Line, s.SelectionRange.Start.Character, path));
                    if (s.Kind == SymbolKind.Class) Visit(s.Children, container + "." + s.Name);
                }
            }
            Visit(LanguageService.SymbolsOf(module.Body, lines, inClass: false), name);
            bool isPackage = Path.GetFileNameWithoutExtension(path) == "__init__";
            string package = isPackage ? name : name.Contains('.') ? name[..name.LastIndexOf('.')] : "";
            return new FileEntry(stamp.Ticks, stamp.Length, list.ToArray(), name, LanguageService.ComputeImports(module, package));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------ keeping up

    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".venv", "venv", "node_modules", "__pycache__", ".pymagic", ".idea", ".tox", ".mypy_cache", ".pytest_cache",
    };

    void Watch(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
            w.Changed += (_, e) => Touched(e.FullPath);
            w.Created += (_, e) => Touched(e.FullPath);
            w.Deleted += (_, e) => Touched(e.FullPath);
            w.Renamed += (_, e) => { Touched(e.OldFullPath); Touched(e.FullPath); };
            w.Error += (_, _) => { _reconcileAll = true; Settle(); };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException) { /* no watching: queries still check the files they use */ }
    }

    void Touched(string path)
    {
        if (_disposed) return;
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(Ignored.Contains) || ProjectFolders.IsExcluded(Path.GetDirectoryName(path) ?? path)) return;
        string ext = Path.GetExtension(path);
        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase)) _dirty[Path.GetFullPath(path)] = 0;
        else if (ext.Length == 0) _reconcileAll = true;   // a folder moved or deleted: every file under it
        else return;
        Settle();
    }

    /// <summary>Waits for the editing to pause, then brings the index up to date.</summary>
    void Settle()
    {
        lock (_gate)
        {
            _settle?.Dispose();
            _settle = new Timer(_ => Catch(), null, 400, Timeout.Infinite);
        }
    }

    void Catch()
    {
        if (_disposed) return;
        if (_reconcileAll) { _dirty.Clear(); Reconcile(); return; }
        bool changed = false;
        foreach (var path in _dirty.Keys.ToList())
        {
            _dirty.TryRemove(path, out _);
            var entry = File.Exists(path) ? Read(path) : null;
            lock (_gate)
            {
                if (entry != null) { _files[path] = entry; changed = true; }
                else changed |= _files.Remove(path);
            }
            Parsed++;
        }
        if (changed) { _needsSave = true; _importers = null; Changed?.Invoke(); Save(); }
    }

    /// <summary>Brings one file up to date now (a file that was saved a moment ago and whose event has not arrived yet).</summary>
    public void Refresh(string path)
    {
        path = Path.GetFullPath(path);
        var entry = File.Exists(path) ? Read(path) : null;
        lock (_gate)
        {
            if (entry != null) _files[path] = entry; else _files.Remove(path);
        }
        _needsSave = true;
        _importers = null;
    }

    // ------------------------------------------------------------ queries

    /// <summary>The symbols whose name matches the query (every letter of it, in order), best first, skipping the files in <paramref name="except"/> (open documents answer for themselves).</summary>
    public List<(int Score, IndexedSymbol Symbol)> Query(string query, ISet<string>? except = null, int limit = 400)
    {
        var scored = new List<(int Score, IndexedSymbol Symbol)>();
        FileEntry[] snapshot;
        lock (_gate) snapshot = _files.Where(kv => except == null || !except.Contains(kv.Key)).Select(kv => kv.Value).ToArray();
        foreach (var f in snapshot)
            foreach (var s in f.Symbols)
            {
                int score = LanguageService.Score(s.Name, query);
                if (score >= 0) scored.Add((score, s));
            }
        return scored.OrderBy(x => x.Score).ThenBy(x => x.Symbol.Name.Length).ThenBy(x => x.Symbol.Name, StringComparer.Ordinal).ThenBy(x => x.Symbol.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToList();
    }

    volatile Dictionary<string, List<(string Path, string ModuleName)>>? _importers;

    /// <summary>The files (and the modules they are) that import a module by this dotted name, or a package it is in: the answer comes from the names every file imports, read once per file.</summary>
    public IReadOnlyList<(string Path, string ModuleName)> ImportersOf(string module)
    {
        var map = _importers;
        if (map == null)
        {
            map = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            lock (_gate)
                foreach (var (path, e) in _files)
                    foreach (var name in e.Imports)
                    {
                        if (!map.TryGetValue(name, out var list)) map[name] = list = new();
                        list.Add((path, e.ModuleName));
                    }
            _importers = map;
        }
        return map.TryGetValue(module, out var found) ? found : Array.Empty<(string, string)>();
    }

    /// <summary>Every indexed file with the number of names in it (for tests).</summary>
    public Dictionary<string, int> Overview()
    {
        lock (_gate) return _files.ToDictionary(kv => kv.Key, kv => kv.Value.Symbols.Length, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ saving

    void Load()
    {
        if (_storePath == null || !File.Exists(_storePath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_storePath));
            if (doc.RootElement.GetProperty("v").GetInt32() != 2) return;
            var loaded = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                string path = f.GetProperty("p").GetString()!;
                var symbols = new List<IndexedSymbol>();
                foreach (var s in f.GetProperty("s").EnumerateArray())
                    symbols.Add(new IndexedSymbol(s[0].GetString()!, s[1].GetInt32(), s[2].GetString()!, s[3].GetInt32(), s[4].GetInt32(), path));
                loaded[path] = new FileEntry(f.GetProperty("t").GetInt64(), f.GetProperty("l").GetInt64(), symbols.ToArray(), f.GetProperty("m").GetString()!,
                    f.GetProperty("i").EnumerateArray().Select(x => x.GetString()!).ToArray());
            }
            lock (_gate) foreach (var (k, v) in loaded) _files[k] = v;
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException) { /* a damaged file is rebuilt */ }
    }

    void Save()
    {
        if (_storePath == null || !_needsSave) return;
        _needsSave = false;
        try
        {
            string folder = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(folder);
            PymagicFolder.IgnoreInGit(folder, "index.json", "index.json.tmp");   // the index is a cache: the other files of the folder (the inspection profile) are the project's, this one is not
            string temp = _storePath + ".tmp";
            KeyValuePair<string, FileEntry>[] copy;
            lock (_gate) copy = _files.ToArray();
            using (var stream = File.Create(temp))
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteNumber("v", 2);
                w.WriteStartArray("files");
                foreach (var (path, e) in copy)
                {
                    w.WriteStartObject();
                    w.WriteString("p", path); w.WriteNumber("t", e.Ticks); w.WriteNumber("l", e.Length); w.WriteString("m", e.ModuleName);
                    w.WriteStartArray("i");
                    foreach (var name in e.Imports) w.WriteStringValue(name);
                    w.WriteEndArray();
                    w.WriteStartArray("s");
                    foreach (var s in e.Symbols)
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(s.Name); w.WriteNumberValue(s.Kind); w.WriteStringValue(s.Container); w.WriteNumberValue(s.Line); w.WriteNumberValue(s.Character);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.Move(temp, _storePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _needsSave = true; }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _settle?.Dispose();
    }
}
