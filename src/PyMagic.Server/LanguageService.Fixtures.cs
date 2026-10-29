using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>pytest fixtures: which fixture a parameter asks for, and what that gives go to definition, usages, rename, hover, completion and inspections.</summary>
public sealed partial class LanguageService
{
    static readonly string[] ProjectMarkers = { "pytest.ini", "pyproject.toml", "tox.ini", "setup.cfg", ".git" };

    /// <summary>What one workspace needs to resolve fixtures: the resolver, and where each module came from so conftest.py files can be found.</summary>
    sealed class FixtureContext
    {
        public FixtureResolver Resolver { get; }
        readonly Dictionary<ModuleInfo, string> _uriOf = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<string, ModuleInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<ModuleInfo, IReadOnlyList<ModuleInfo>> _conftests = new(ReferenceEqualityComparer.Instance);
        readonly IReadOnlyList<string> _roots;
        readonly Func<IReadOnlyList<string>> _installedPlugins;
        readonly Dictionary<ModuleInfo, IReadOnlyList<ModuleInfo>> _plugins = new(ReferenceEqualityComparer.Instance);

        /// <param name="installedPlugins">The modules installed packages register with pytest (their `pytest11` entry points).</param>
        public FixtureContext(IEnumerable<string> sourceRoots, Func<IReadOnlyList<string>>? installedPlugins = null)
        {
            _roots = sourceRoots.Select(r => System.IO.Path.GetFullPath(r).TrimEnd('\\', '/')).ToList();
            _installedPlugins = installedPlugins ?? (() => Array.Empty<string>());
            Resolver = new FixtureResolver(ConftestsFor, PluginsFor);
        }

        /// <summary>
        /// The plugin modules pytest loads for a module: those `pytest_plugins` names in the module and its conftest.py files, then the installed plugins.
        /// A name the workspace cannot find is left out.
        /// </summary>
        IReadOnlyList<ModuleInfo> PluginsFor(Workspace ws, ModuleInfo m)
        {
            if (_plugins.TryGetValue(m, out var cached)) return cached;
            var list = new List<ModuleInfo>();
            _plugins[m] = list;
            var names = new List<string>();
            foreach (var source in new[] { m }.Concat(ConftestsFor(ws, m)))
                if (FixtureModel.PytestPluginsOf(source) is { } named) names.AddRange(named);
            names.AddRange(_installedPlugins());
            foreach (var name in names.Distinct(StringComparer.Ordinal))
                if (ws.GetModule(name) is { } module && !ReferenceEquals(module, m) && !list.Contains(module)) list.Add(module);
            return list;
        }

        public void Register(ModuleInfo m, string uri, string? path)
        {
            _uriOf[m] = uri;
            if (path != null) _byPath[System.IO.Path.GetFullPath(path)] = m;
        }

        public IReadOnlyList<ModuleInfo> ConftestsOf(Workspace ws, ModuleInfo m) => ConftestsFor(ws, m);

        string? PathOf(ModuleInfo m)
        {
            if (m.Path != null) return System.IO.Path.GetFullPath(m.Path);
            return _uriOf.TryGetValue(m, out var uri) ? PathOfUri(uri) is { } p ? System.IO.Path.GetFullPath(p) : null : null;
        }

        /// <summary>The conftest.py files from the module's folder upwards (to the project root), nearest first, without the module itself.</summary>
        IReadOnlyList<ModuleInfo> ConftestsFor(Workspace ws, ModuleInfo m)
        {
            if (_conftests.TryGetValue(m, out var cached)) return cached;
            var list = new List<ModuleInfo>();
            _conftests[m] = list;
            string? path = PathOf(m);
            string? dir = path == null ? null : System.IO.Path.GetDirectoryName(path);
            for (int level = 0; dir != null && level < 12; level++)
            {
                string conf = System.IO.Path.Combine(dir, "conftest.py");
                if (!string.Equals(conf, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (_byPath.TryGetValue(conf, out var known)) list.Add(known);
                    else if (File.Exists(conf))
                    {
                        try
                        {
                            var loaded = ws.AddDiskModule("__conftest_" + (uint)conf.GetHashCode(StringComparison.OrdinalIgnoreCase), conf);
                            Register(loaded, UriOfPath(conf), conf);
                            list.Add(loaded);
                        }
                        catch (IOException) { /* unreadable: skipped */ }
                    }
                }
                bool atRoot = _roots.Any(r => string.Equals(r, dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    || ProjectMarkers.Any(mk => File.Exists(System.IO.Path.Combine(dir, mk)) || Directory.Exists(System.IO.Path.Combine(dir, mk)));
                if (atRoot) break;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return list;
        }
    }

    // ------------------------------------------------------------ installed plugins

    readonly Dictionary<string, (DateTime Stamp, List<string> Modules)> _pluginEntryPoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugin modules an entry_points.txt registers with pytest.</summary>
    public static IEnumerable<string> PytestEntryPoints(string entryPointsText) => FixtureModel.PytestEntryPoints(entryPointsText);

    /// <summary>
    /// The modules installed packages register with pytest: the `[pytest11]` section of the entry_points.txt of every package in the library folders
    /// (`anyio = anyio.pytest_plugin`). pytest loads them, and so their fixtures, in every run. Read again when a library folder changes.
    /// </summary>
    IReadOnlyList<string> PytestPluginModules()
    {
        var all = new List<string>();
        foreach (var root in EffectiveLibraryRoots())
        {
            if (!Directory.Exists(root)) continue;
            DateTime stamp;
            try { stamp = Directory.GetLastWriteTimeUtc(root); } catch (IOException) { continue; }
            if (!_pluginEntryPoints.TryGetValue(root, out var cached) || cached.Stamp != stamp)
            {
                var found = new List<string>();
                try
                {
                    foreach (var dist in Directory.EnumerateDirectories(root, "*.dist-info").Concat(Directory.EnumerateDirectories(root, "*.egg-info")))
                    {
                        string file = System.IO.Path.Combine(dist, "entry_points.txt");
                        if (File.Exists(file)) found.AddRange(FixtureModel.PytestEntryPoints(File.ReadAllText(file)));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* an unreadable folder adds nothing */ }
                _pluginEntryPoints[root] = cached = (stamp, found);
            }
            all.AddRange(cached.Modules);
        }
        return all.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A workspace whose test and fixture parameters take the types of the fixtures they ask for.</summary>
    (Workspace Workspace, FixtureContext Fixtures) NewWorkspace()
    {
        var fixtures = new FixtureContext(SourceRoots, PytestPluginModules);
        var ws = new Workspace(WorkspaceOptionsNow((w, m, p) => p.Default == null ? fixtures.Resolver.TypeOfParameter(w, m, p) ?? AdminParameterType(w, m, p) : DependencyParameterType(w, m, p)), _cache);
        _fixtureContexts.Add(ws, fixtures);
        return (ws, fixtures);
    }
}
