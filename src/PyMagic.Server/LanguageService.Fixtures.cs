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

        public FixtureContext(IEnumerable<string> sourceRoots)
        {
            _roots = sourceRoots.Select(r => System.IO.Path.GetFullPath(r).TrimEnd('\\', '/')).ToList();
            Resolver = new FixtureResolver(ConftestsFor);
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

    /// <summary>A workspace whose test and fixture parameters take the types of the fixtures they ask for.</summary>
    (Workspace Workspace, FixtureContext Fixtures) NewWorkspace()
    {
        var fixtures = new FixtureContext(SourceRoots);
        var ws = new Workspace(WorkspaceOptionsNow(fixtures.Resolver.TypeOfParameter), _cache);
        _fixtureContexts.Add(ws, fixtures);
        return (ws, fixtures);
    }
}
