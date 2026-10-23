namespace PyMagic.Core.Types;

/// <summary>Where a module name resolved to on disk.</summary>
internal sealed record ResolvedFile(string? Path, bool IsPackage, bool IsStub, ResolutionKind Kind);

/// <summary>
/// Finds the file behind an absolute module name. Search order (first hit wins):
/// 1. source roots (user code; a .pyi next to a .py wins),
/// 2. stub packages in library roots (<c>foo-stubs</c>),
/// 3. inline typed packages in library roots (a <c>py.typed</c> marker),
/// 4. typeshed: the standard library (gated by VERSIONS for the configured Python version), then third-party stubs,
/// 5. library sources,
/// 6. namespace packages (directories without __init__).
/// Partial stub packages (<c>py.typed</c> containing "partial", METADATA.toml <c>partial_stub = true</c>) fall through
/// to the next source for modules they do not contain.
/// </summary>
internal sealed class ModuleResolver
{
    readonly WorkspaceOptions _options;
    Dictionary<string, (int[] Min, int[]? Max)>? _versions;
    Dictionary<string, List<(string Dir, bool Partial)>>? _thirdPartyStubs;

    public ModuleResolver(WorkspaceOptions options) { _options = options; }

    public ResolvedFile? Resolve(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var parts = name.Split('.');

        // 1. user code
        foreach (var root in _options.SourceRoots)
            if (FindIn(root, parts, stubsOnly: false, ResolutionKind.UserCode) is { } user) return user;

        // 2. stub packages
        foreach (var lib in _options.LibraryRoots)
        {
            var stubDir = System.IO.Path.Combine(lib, parts[0] + "-stubs");
            if (!Directory.Exists(stubDir)) continue;
            if (FindIn(stubDir, parts.Skip(1).ToArray(), stubsOnly: true, ResolutionKind.StubPackage, rootIsPackage: parts.Length == 1) is { } stub)
                return stub;
            // not found in a stub package (partial or not): keep looking in other sources
        }

        // 3. inline typed packages
        foreach (var lib in _options.LibraryRoots)
        {
            var pkgDir = System.IO.Path.Combine(lib, parts[0]);
            if (!File.Exists(System.IO.Path.Combine(pkgDir, "py.typed"))) continue;
            if (FindIn(lib, parts, stubsOnly: false, ResolutionKind.InlinePackage) is { } inline) return inline;
        }

        // 4. typeshed
        if (_options.TypeshedPath != null)
        {
            if (StdlibAvailable(name) && FindIn(System.IO.Path.Combine(_options.TypeshedPath, "stdlib"), parts, stubsOnly: true, ResolutionKind.Typeshed) is { } std)
                return std;
            foreach (var (dir, _) in ThirdPartyStubDirs(parts[0]))
                if (FindIn(dir, parts, stubsOnly: true, ResolutionKind.Typeshed) is { } third) return third;
        }

        // 5. library sources
        foreach (var lib in _options.LibraryRoots)
            if (FindIn(lib, parts, stubsOnly: false, ResolutionKind.LibrarySource) is { } source) return source;

        // 6. namespace packages
        foreach (var root in _options.SourceRoots.Concat(_options.LibraryRoots))
            if (Directory.Exists(System.IO.Path.Combine(new[] { root }.Concat(parts).ToArray())))
                return new ResolvedFile(null, true, false, ResolutionKind.NamespacePackage);

        return null;
    }

    static ResolvedFile? FindIn(string root, string[] parts, bool stubsOnly, ResolutionKind kind, bool rootIsPackage = false)
    {
        if (!Directory.Exists(root)) return null;
        if (parts.Length == 0)
        {
            // the stub package itself: foo-stubs/__init__.pyi
            var init = System.IO.Path.Combine(root, "__init__.pyi");
            return rootIsPackage && File.Exists(init) ? new ResolvedFile(init, true, true, kind) : null;
        }
        var basePath = System.IO.Path.Combine(new[] { root }.Concat(parts).ToArray());
        foreach (var ext in stubsOnly ? new[] { ".pyi" } : new[] { ".pyi", ".py" })
        {
            if (File.Exists(basePath + ext)) return new ResolvedFile(basePath + ext, false, ext == ".pyi", kind);
            var init = System.IO.Path.Combine(basePath, "__init__" + ext);
            if (File.Exists(init)) return new ResolvedFile(init, true, ext == ".pyi", kind);
        }
        return null;
    }

    // ------------------------------------------------------------ typeshed: VERSIONS

    bool StdlibAvailable(string name)
    {
        _versions ??= LoadVersions();
        var parts = name.Split('.');
        for (int n = parts.Length; n >= 1; n--)
        {
            if (!_versions.TryGetValue(string.Join(".", parts.Take(n)), out var range)) continue;
            var v = new[] { _options.Environment.Major, _options.Environment.Minor };
            return Compare(v, range.Min) >= 0 && (range.Max == null || Compare(v, range.Max) <= 0);
        }
        return true;   // not listed: available in every version
    }

    static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    Dictionary<string, (int[] Min, int[]? Max)> LoadVersions()
    {
        var result = new Dictionary<string, (int[], int[]?)>();
        var file = System.IO.Path.Combine(_options.TypeshedPath!, "stdlib", "VERSIONS");
        if (!File.Exists(file)) return result;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Contains('#') ? raw[..raw.IndexOf('#')] : raw;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var module = line[..colon].Trim();
            var range = line[(colon + 1)..].Trim();
            int dash = range.IndexOf('-');
            if (dash < 0) continue;
            var min = ParseVersion(range[..dash]);
            var maxText = range[(dash + 1)..].Trim();
            if (min == null) continue;
            result[module] = (min, maxText.Length == 0 ? null : ParseVersion(maxText));
        }
        return result;
    }

    static int[]? ParseVersion(string text)
    {
        var bits = text.Trim().Split('.');
        var nums = new List<int>();
        foreach (var b in bits)
        {
            if (!int.TryParse(b, out int n)) return null;
            nums.Add(n);
        }
        return nums.ToArray();
    }

    // ------------------------------------------------------------ typeshed: third-party stubs

    /// <summary>Stub distributions (stubs/&lt;dist&gt;) that provide the given top-level module, with their partial flag.</summary>
    IEnumerable<(string Dir, bool Partial)> ThirdPartyStubDirs(string topLevel)
    {
        if (_thirdPartyStubs == null)
        {
            _thirdPartyStubs = new Dictionary<string, List<(string, bool)>>();
            var stubsRoot = System.IO.Path.Combine(_options.TypeshedPath!, "stubs");
            if (Directory.Exists(stubsRoot))
                foreach (var dist in Directory.EnumerateDirectories(stubsRoot))
                {
                    bool partial = IsPartialDistribution(dist);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dist))
                    {
                        string n = System.IO.Path.GetFileName(entry);
                        if (n.StartsWith('@') || n == "METADATA.toml") continue;
                        string top = n.EndsWith(".pyi") ? n[..^4] : n;
                        if (!_thirdPartyStubs.TryGetValue(top, out var list)) _thirdPartyStubs[top] = list = new();
                        list.Add((dist, partial));
                    }
                }
        }
        return _thirdPartyStubs.TryGetValue(topLevel, out var dirs) ? dirs : Enumerable.Empty<(string, bool)>();
    }

    static bool IsPartialDistribution(string dist)
    {
        var meta = System.IO.Path.Combine(dist, "METADATA.toml");
        if (!File.Exists(meta)) return false;
        foreach (var line in File.ReadLines(meta))
        {
            var l = line.Replace(" ", "");
            if (l.StartsWith("partial_stub=true", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
