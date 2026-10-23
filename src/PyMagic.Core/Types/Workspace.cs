using Assembly = System.Reflection.Assembly;
using PyMagic.Core.Flow;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>One parsed module with its semantic and flow models.</summary>
public sealed class ModuleInfo
{
    public string Name { get; }
    public string? Path { get; }
    public string Source { get; }
    public bool IsStub { get; }
    /// <summary>A stub shipped inside PyMagic (not read from disk).</summary>
    public bool IsBundledStub => IsStub && Path == null;
    /// <summary>True for __init__ modules; relative imports resolve against the module itself instead of its parent.</summary>
    public bool IsPackage { get; }
    public Module Ast { get; }
    public IReadOnlyList<Diagnostic> SyntaxErrors { get; }
    public SemanticModel Semantics { get; }
    public FlowModel Flow { get; }

    Dictionary<Node, Node>? _parents;

    internal ModuleInfo(string name, string? path, string source, bool isStub, bool isPackage)
    {
        Name = name; Path = path; Source = source; IsStub = isStub; IsPackage = isPackage;
        var r = Parser.Parse(source);
        Ast = r.Module; SyntaxErrors = r.Diagnostics;
        Semantics = SemanticModel.Build(Ast);
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
/// The set of modules known to the analysis: in-memory modules (tests, editor buffers), bundled stubs and
/// modules found under the search roots. Owns the <see cref="TypeEvaluator"/>.
/// </summary>
public sealed class Workspace
{
    readonly Dictionary<string, ModuleInfo?> _modules = new();
    readonly Dictionary<Scope, ModuleInfo> _byRootScope = new(ReferenceEqualityComparer.Instance);
    readonly List<string> _roots = new();

    public TypeEvaluator Evaluator { get; }
    public IReadOnlyList<string> Roots => _roots;

    public Workspace(params string[] searchRoots)
    {
        _roots.AddRange(searchRoots);
        Evaluator = new TypeEvaluator(this);
    }

    public ModuleInfo Builtins => GetModule("builtins")!;
    public ModuleInfo Typing => GetModule("typing")!;

    /// <summary>Adds (or replaces) a module from source text.</summary>
    public ModuleInfo AddModule(string name, string source, bool isPackage = false)
    {
        var m = new ModuleInfo(name, null, source, false, isPackage);
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
        m = LoadBundled(name) ?? LoadFromRoots(name);
        _modules[name] = m;
        if (m != null) _byRootScope[m.Semantics.ModuleScope] = m;
        return m;
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

    // ------------------------------------------------------------ loading

    static readonly Dictionary<string, string> BundledAliases = new()
    {
        ["typing_extensions"] = "typing",
        ["collections.abc"] = "typing",
        ["_typeshed"] = "typing",
    };

    ModuleInfo? LoadBundled(string name)
    {
        string resource = BundledAliases.GetValueOrDefault(name, name);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Stubs/{resource}.pyi");
        if (s == null) return null;
        if (resource != name && _modules.TryGetValue(resource, out var existing) && existing != null) return existing;
        using var reader = new StreamReader(s);
        var m = new ModuleInfo(resource, null, reader.ReadToEnd(), true, false);
        if (resource != name) { _modules[resource] = m; _byRootScope[m.Semantics.ModuleScope] = m; }
        return m;
    }

    ModuleInfo? LoadFromRoots(string name)
    {
        var rel = name.Replace('.', System.IO.Path.DirectorySeparatorChar);
        foreach (var root in _roots)
        {
            foreach (var ext in new[] { ".pyi", ".py" })
            {
                var file = System.IO.Path.Combine(root, rel + ext);
                if (File.Exists(file)) return Load(name, file, false);
                var init = System.IO.Path.Combine(root, rel, "__init__" + ext);
                if (File.Exists(init)) return Load(name, init, true);
            }
        }
        return null;
    }

    static ModuleInfo? Load(string name, string file, bool isPackage)
    {
        try { return new ModuleInfo(name, file, File.ReadAllText(file), file.EndsWith(".pyi"), isPackage); }
        catch (IOException) { return null; }
    }
}
