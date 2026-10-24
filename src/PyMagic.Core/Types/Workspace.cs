using Assembly = System.Reflection.Assembly;
using PyMagic.Core.Flow;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>Where a module was found; the order is the order resolution tries them.</summary>
public enum ResolutionKind
{
    InMemory,        // added with AddModule (editor buffers, tests)
    UserCode,        // a source root
    StubPackage,     // foo-stubs next to the library
    InlinePackage,   // a library marked with py.typed
    Typeshed,        // typeshed stdlib or third-party stubs
    LibrarySource,   // plain library source (site-packages, the interpreter's Lib directory)
    NamespacePackage,// a directory without __init__
    Bundled,         // the minimal stubs shipped inside PyMagic (used when no typeshed is configured)
}

/// <summary>How a workspace finds code. All paths are optional.</summary>
public sealed class WorkspaceOptions
{
    /// <summary>The user's own code; searched first, so it shadows library modules of the same name.</summary>
    public IReadOnlyList<string> SourceRoots { get; init; } = Array.Empty<string>();
    /// <summary>Installed libraries: site-packages and the interpreter's standard library sources.</summary>
    public IReadOnlyList<string> LibraryRoots { get; init; } = Array.Empty<string>();
    /// <summary>A typeshed checkout (the directory containing stdlib/ and stubs/). Without it the bundled minimal stubs are used.</summary>
    public string? TypeshedPath { get; init; }
    public PythonEnvironment Environment { get; init; } = PythonEnvironment.Default;
}

/// <summary>One parsed module with its semantic and flow models.</summary>
public sealed class ModuleInfo
{
    public string Name { get; }
    public string? Path { get; }
    public string Source { get; }
    public bool IsStub { get; }
    /// <summary>A stub shipped inside PyMagic (not read from disk).</summary>
    public bool IsBundledStub => IsStub && Path == null && Kind == ResolutionKind.Bundled;
    /// <summary>True for __init__ modules; relative imports resolve against the module itself instead of its parent.</summary>
    public bool IsPackage { get; }
    public ResolutionKind Kind { get; }
    public Module Ast { get; }
    public IReadOnlyList<Diagnostic> SyntaxErrors { get; }
    public SemanticModel Semantics { get; }
    public FlowModel Flow { get; }

    Dictionary<Node, Node>? _parents;

    internal ModuleInfo(string name, string? path, string source, bool isStub, bool isPackage, ResolutionKind kind, PythonEnvironment env)
    {
        Name = name; Path = path; Source = source; IsStub = isStub; IsPackage = isPackage; Kind = kind;
        var r = Parser.Parse(source);
        Ast = r.Module; SyntaxErrors = r.Diagnostics;
        Semantics = SemanticModel.Build(Ast, env);
        Flow = new FlowModel(Semantics);
    }

    /// <summary>Package that relative imports are relative to.</summary>
    public string Package => IsPackage ? Name : (Name.Contains('.') ? Name[..Name.LastIndexOf('.')] : "");

    public Node? ParentOf(Node n)
    {
        if (_parents == null)
        {
            _parents = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
            foreach (var p in AstChildren.Descendants(Ast))
                foreach (var c in AstChildren.Of(p)) _parents[c] = p;
        }
        return _parents.GetValueOrDefault(n);
    }

    public override string ToString() => Name;
}

/// <summary>
/// The set of modules known to the analysis: in-memory modules (tests, editor buffers), then whatever the resolver
/// finds in source roots, libraries and typeshed. Owns the <see cref="TypeEvaluator"/>.
/// </summary>
public sealed class Workspace
{
    /// <summary>
    /// Typeshed used by workspaces created without explicit options. Taken from the PYMAGIC_TYPESHED environment
    /// variable so whole test suites can be run against real typeshed.
    /// </summary>
    public static string? DefaultTypeshedPath { get; set; } = System.Environment.GetEnvironmentVariable("PYMAGIC_TYPESHED");

    readonly Dictionary<string, ModuleInfo?> _modules = new();
    readonly Dictionary<Scope, ModuleInfo> _byRootScope = new(ReferenceEqualityComparer.Instance);
    readonly ModuleResolver _resolver;
    readonly AnalysisCache _cache;

    public WorkspaceOptions Options { get; }
    public TypeEvaluator Evaluator { get; }
    public PythonEnvironment Environment => Options.Environment;

    public Workspace(params string[] sourceRoots)
        : this(new WorkspaceOptions { SourceRoots = sourceRoots, TypeshedPath = DefaultTypeshedPath }) { }

    /// <param name="cache">Parsed modules kept between workspaces (see <see cref="AnalysisCache"/>); a private one is used when omitted.</param>
    public Workspace(WorkspaceOptions options, AnalysisCache? cache = null)
    {
        Options = options;
        _cache = cache ?? new AnalysisCache();
        _resolver = _cache.ResolverFor(options);
        Evaluator = new TypeEvaluator(this);
    }

    public ModuleInfo Builtins => GetModule("builtins")!;
    public ModuleInfo Typing => GetModule("typing")!;

    /// <summary>Adds (or replaces) a module from source text.</summary>
    /// <param name="cached">False for throw-away variants of a buffer (they must not evict the real buffer's cache entry).</param>
    public ModuleInfo AddModule(string name, string source, bool isPackage = false, bool cached = true)
    {
        Func<ModuleInfo> create = () => new ModuleInfo(name, null, source, false, isPackage, ResolutionKind.InMemory, Environment);
        var m = cached ? _cache.Buffer(name, source, isPackage, create) : create();
        Register(name, m);
        return m;
    }

    /// <summary>A user file that is part of the project but not open in an editor; parsed once while the file stays unchanged.</summary>
    public ModuleInfo AddDiskModule(string name, string path, bool isPackage = false)
    {
        var m = _cache.File(name, path,
            text => new ModuleInfo(name, path, text, path.EndsWith(".pyi"), isPackage, ResolutionKind.UserCode, Environment));
        Register(name, m);
        return m;
    }

    /// <summary>Reads a file as a module with the given name (user code), replacing any module of that name.</summary>
    public ModuleInfo AddFile(string name, string path, bool isPackage = false)
    {
        var m = new ModuleInfo(name, path, File.ReadAllText(path), path.EndsWith(".pyi"), isPackage, ResolutionKind.UserCode, Environment);
        Register(name, m);
        return m;
    }

    void Register(string name, ModuleInfo m)
    {
        _modules[name] = m;
        _byRootScope[m.Semantics.ModuleScope] = m;
    }

    /// <summary>The module with this absolute dotted name, or null if it cannot be found.</summary>
    public ModuleInfo? GetModule(string name)
    {
        if (_modules.TryGetValue(name, out var m)) return m;
        m = Load(name);
        _modules[name] = m;
        if (m != null) _byRootScope[m.Semantics.ModuleScope] = m;
        return m;
    }

    ModuleInfo? Load(string name)
    {
        var found = _resolver.Resolve(name);
        if (found != null)
        {
            try
            {
                if (found.Kind == ResolutionKind.NamespacePackage)
                    return new ModuleInfo(name, found.Path, "", found.IsStub, found.IsPackage, found.Kind, Environment);
                return _cache.File(name, found.Path!,
                    text => new ModuleInfo(name, found.Path, text, found.IsStub, found.IsPackage, found.Kind, Environment));
            }
            catch (IOException) { /* fall through to the bundled stubs */ }
        }
        return Options.TypeshedPath == null ? LoadBundled(name) : null;
    }

    /// <summary>The module a symbol was declared in.</summary>
    public ModuleInfo? ModuleOf(Symbol symbol) => _byRootScope.GetValueOrDefault(symbol.Scope.ModuleScope);
    public ModuleInfo? ModuleOf(Scope scope) => _byRootScope.GetValueOrDefault(scope.ModuleScope);

    /// <summary>Resolves a (possibly relative) import to an absolute module name.</summary>
    public static string? AbsoluteName(ModuleInfo from, int level, string? module)
    {
        if (level == 0) return module;
        var parts = from.Package.Length == 0 ? new List<string>() : from.Package.Split('.').ToList();
        int up = level - 1;
        if (up > parts.Count) return null;
        parts.RemoveRange(parts.Count - up, up);
        if (!string.IsNullOrEmpty(module)) parts.Add(module);
        return string.Join(".", parts);
    }

    // ------------------------------------------------------------ bundled stubs

    static readonly Dictionary<string, string> BundledAliases = new()
    {
        ["typing_extensions"] = "typing",
        ["collections.abc"] = "typing",
        ["_typeshed"] = "typing",
    };

    ModuleInfo? LoadBundled(string name)
    {
        string resource = BundledAliases.GetValueOrDefault(name, name);
        if (resource != name && _modules.TryGetValue(resource, out var existing) && existing != null) return existing;
        var m = _cache.Bundled(resource, () =>
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Stubs/{resource}.pyi");
            if (s == null) return null;
            using var reader = new StreamReader(s);
            return new ModuleInfo(resource, null, reader.ReadToEnd(), true, false, ResolutionKind.Bundled, Environment);
        });
        if (m != null && resource != name) { _modules[resource] = m; _byRootScope[m.Semantics.ModuleScope] = m; }
        return m;
    }
}
