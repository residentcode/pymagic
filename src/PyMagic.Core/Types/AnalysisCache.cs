using PyMagic.Core.Semantics;

namespace PyMagic.Core.Types;

/// <summary>
/// Parsed and bound modules that outlive a single <see cref="Workspace"/>. A workspace is cheap to create (the type
/// evaluator fills its caches lazily), but parsing and binding typeshed's builtins.pyi on every edit is not, so editors
/// keep one cache and hand it to each new workspace. Entries are reused only while they are still valid: files by
/// modification time and length, editor buffers by their exact text, and everything is dropped when the search paths or the
/// target Python version change. Modules are immutable once built, so sharing them between workspaces is safe; the cache
/// itself is not thread-safe (use one per language-server session).
/// </summary>
public sealed class AnalysisCache
{
    readonly Dictionary<(string Name, string Path), (long Ticks, long Length, ModuleInfo Module)> _files = new();
    readonly Dictionary<string, (string Source, bool IsPackage, ModuleInfo Module)> _buffers = new();
    readonly Dictionary<string, ModuleInfo?> _bundled = new();   // null: known not to exist
    ModuleResolver? _resolver;
    WorkspaceOptions? _options;

    /// <summary>How many modules were taken from the cache and how many had to be parsed, since creation (for tests and tuning).</summary>
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    internal ModuleResolver ResolverFor(WorkspaceOptions options)
    {
        if (_resolver == null || !Same(_options!, options))
        {
            _files.Clear(); _buffers.Clear(); _bundled.Clear();
            _resolver = new ModuleResolver(options);
            _options = options;
        }
        return _resolver;
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
        if (stamp is { } s && _files.TryGetValue((name, path), out var hit) && hit.Ticks == s.Ticks && hit.Length == s.Length)
        {
            Hits++;
            return hit.Module;
        }
        Misses++;
        var module = create(System.IO.File.ReadAllText(path));
        if (stamp is { } now) _files[(name, path)] = (now.Ticks, now.Length, module);
        return module;
    }

    /// <summary>An editor buffer: reused while its text is identical.</summary>
    internal ModuleInfo Buffer(string name, string source, bool isPackage, Func<ModuleInfo> create)
    {
        if (_buffers.TryGetValue(name, out var hit) && hit.IsPackage == isPackage && hit.Source == source)
        {
            Hits++;
            return hit.Module;
        }
        Misses++;
        var module = create();
        _buffers[name] = (source, isPackage, module);
        return module;
    }

    internal ModuleInfo? Bundled(string resource, Func<ModuleInfo?> create)
    {
        if (_bundled.TryGetValue(resource, out var hit)) { Hits++; return hit; }
        Misses++;
        var module = create();
        _bundled[resource] = module;
        return module;
    }
}
