using PyMagic.Core.Semantics;

namespace PyMagic.Core.Types;

/// <summary>
/// Parsed and bound modules that outlive a single <see cref="Workspace"/>. A workspace is cheap to create (the type
/// evaluator fills its caches lazily), but parsing and binding typeshed's builtins.pyi on every edit is not, so editors
/// keep one cache and hand it to each new workspace. Entries are reused only while they are still valid: files by
/// modification time and length, editor buffers by their exact text, and everything is dropped when the search paths or the
/// target Python version change. Modules are immutable once built, so sharing them between workspaces is safe; the cache
/// itself may be asked from several threads at once (the project's files are read in parallel, see <see cref="Preload"/>): every access to its tables is under one lock, while
/// parsing happens outside it, so two threads that want the same new file may both parse it and the later result is the one kept.
/// </summary>
public sealed class AnalysisCache
{
    readonly Dictionary<(string Name, string Path), (long Ticks, long Length, ModuleInfo Module)> _files = new();
    readonly Dictionary<string, (string Source, bool IsPackage, ModuleInfo Module)> _buffers = new();
    readonly Dictionary<string, ModuleInfo?> _bundled = new();   // null: known not to exist
    ModuleResolver? _resolver;
    WorkspaceOptions? _options;

    /// <summary>How many modules were taken from the cache and how many had to be parsed, since creation (for tests and tuning).</summary>
    int _hits, _misses;
    readonly object _gate = new();
    public int Hits => _hits;
    public int Misses => _misses;

    internal ModuleResolver ResolverFor(WorkspaceOptions options)
    {
        lock (_gate)
        {
            if (_resolver == null || !Same(_options!, options))
            {
                _files.Clear(); _buffers.Clear(); _bundled.Clear();
                _resolver = new ModuleResolver(options);
                _options = options;
            }
            return _resolver;
        }
    }

    static bool Same(WorkspaceOptions a, WorkspaceOptions b) =>
        a.TypeshedPath == b.TypeshedPath
        && a.Environment == b.Environment
        && a.SourceRoots.SequenceEqual(b.SourceRoots)
        && a.LibraryRoots.SequenceEqual(b.LibraryRoots);

    static (long Ticks, long Length)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>A module read from disk, parsed at most once while the file does not change.</summary>
    internal ModuleInfo File(string name, string path, Func<string, ModuleInfo> create)
    {
        var stamp = Stamp(path);   // taken before reading, so a write during the read makes the entry stale rather than wrong
        if (stamp is { } s && Lookup(name, path, s) is { } hit)
        {
            Interlocked.Increment(ref _hits);
            return hit;
        }
        Interlocked.Increment(ref _misses);
        var module = create(System.IO.File.ReadAllText(path));
        if (stamp is { } now) lock (_gate) _files[(name, path)] = (now.Ticks, now.Length, module);
        return module;
    }

    ModuleInfo? Lookup(string name, string path, (long Ticks, long Length) stamp)
    {
        lock (_gate)
            return _files.TryGetValue((name, path), out var hit) && hit.Ticks == stamp.Ticks && hit.Length == stamp.Length ? hit.Module : null;
    }

    /// <summary>
    /// Reads and parses these files on every core, so that the workspace that asks for them one by one finds them ready. Files that are
    /// still in the cache are not read again. Returns how many were parsed.
    /// </summary>
    internal int Preload(IReadOnlyList<(string Name, string Path, bool IsPackage)> files, PythonEnvironment environment)
    {
        int parsed = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6) }, f =>
        {
            var stamp = Stamp(f.Path);
            if (stamp is not { } s || Lookup(f.Name, f.Path, s) != null) return;
            try
            {
                var module = new ModuleInfo(f.Name, f.Path, System.IO.File.ReadAllText(f.Path), f.Path.EndsWith(".pyi"), f.IsPackage, ResolutionKind.UserCode, environment);
                lock (_gate) _files[(f.Name, f.Path)] = (s.Ticks, s.Length, module);
                Interlocked.Increment(ref parsed);
                Interlocked.Increment(ref _misses);
            }
            catch (IOException) { /* the workspace reports it when it asks for the file */ }
            catch (UnauthorizedAccessException) { }
        });
        return parsed;
    }

    /// <summary>An editor buffer: reused while its text is identical.</summary>
    internal ModuleInfo Buffer(string name, string source, bool isPackage, Func<ModuleInfo> create)
    {
        lock (_gate)
            if (_buffers.TryGetValue(name, out var hit) && hit.IsPackage == isPackage && hit.Source == source)
            {
                Interlocked.Increment(ref _hits);
                return hit.Module;
            }
        Interlocked.Increment(ref _misses);
        var module = create();
        lock (_gate) _buffers[name] = (source, isPackage, module);
        return module;
    }

    internal ModuleInfo? Bundled(string resource, Func<ModuleInfo?> create)
    {
        lock (_gate)
            if (_bundled.TryGetValue(resource, out var hit)) { Interlocked.Increment(ref _hits); return hit; }
        Interlocked.Increment(ref _misses);
        var module = create();
        lock (_gate) _bundled[resource] = module;
        return module;
    }
}
