using System.Text;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// The editor-facing features, independent of the transport: it holds the open documents and answers positional
/// queries with LSP-shaped results. A <see cref="Workspace"/> is rebuilt lazily after edits (modules are immutable,
/// and the evaluator caches by node), so a query always sees a consistent snapshot of all open buffers.
/// </summary>
public sealed partial class LanguageService
{
    sealed record Doc(string Uri, string? Path, string ModuleName, bool IsPackage, string Text, int Version);

    sealed class Snapshot
    {
        public required Workspace Workspace { get; init; }
        public required Dictionary<string, ModuleInfo> ByUri { get; init; }
        public required Dictionary<ModuleInfo, string> UriOf { get; init; }
        public required List<ModuleInfo> Project { get; init; }
    }

    readonly Dictionary<string, Doc> _docs = new();
    readonly Dictionary<ModuleInfo, LineMap> _lineMaps = new(ReferenceEqualityComparer.Instance);
    Snapshot? _snapshot, _full;

    public List<string> SourceRoots { get; } = new();
    public List<string> LibraryRoots { get; } = new();
    public string? TypeshedPath { get; set; } = Workspace.DefaultTypeshedPath;
    public PythonEnvironment Environment { get; set; } = PythonEnvironment.Default;
    /// <summary>Report argument-type mismatches too (noisier than the default arity/keyword checks).</summary>
    public bool ReportTypeMismatches { get; set; }

    public IEnumerable<string> OpenUris => _docs.Keys;
    public int? VersionOf(string uri) => _docs.TryGetValue(uri, out var d) ? d.Version : null;

    // ------------------------------------------------------------ documents

    public void Open(string uri, string text, int version) => Set(uri, text, version);
    public void Change(string uri, string text, int version) => Set(uri, text, version);
    public void Close(string uri)
    {
        if (_docs.Remove(uri)) _snapshot = _full = null;
    }

    void Set(string uri, string text, int version)
    {
        string? path = PathOfUri(uri);
        var (name, isPackage) = ModuleNameOf(path);
        _docs[uri] = new Doc(uri, path, name, isPackage, text, version);
        _snapshot = _full = null;
    }

    static string? PathOfUri(string uri)
    {
        try { return uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uri).LocalPath : null; }
        catch (UriFormatException) { return null; }
    }

    public static string UriOfPath(string path) => new Uri(path).AbsoluteUri;

    (string Name, bool IsPackage) ModuleNameOf(string? path)
    {
        if (path == null) return ("__untitled__", false);
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        bool isPackage = stem == "__init__";
        string full = System.IO.Path.GetFullPath(path);
        foreach (var root in SourceRoots.Concat(LibraryRoots))
        {
            string r = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = full[r.Length..].Split('\\', '/');
            parts[^1] = stem;
            if (isPackage) parts = parts[..^1];
            if (parts.Length > 0 && parts.All(p => p.Length > 0)) return (string.Join('.', parts), isPackage);
        }
        return (stem, isPackage);
    }

    Snapshot Current() => _snapshot ??= BuildSnapshot(false);

    /// <summary>Open buffers plus every .py file under the source roots (for project-wide queries such as find usages and rename).</summary>
    Snapshot Full() => _full ??= BuildSnapshot(true);

    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "__pycache__", "node_modules", ".venv", "venv", "env", ".tox", ".mypy_cache", "bin", "obj", "site-packages",
    };

    IEnumerable<string> ProjectFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SourceRoots)
        {
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                IEnumerable<string> files, subdirs;
                try { files = Directory.EnumerateFiles(dir, "*.py").ToList(); subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var f in files) if (seen.Add(System.IO.Path.GetFullPath(f))) yield return f;
                foreach (var d in subdirs) if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(d))) stack.Push(d);
            }
        }
    }

    Snapshot BuildSnapshot(bool includeProject)
    {
        var ws = new Workspace(new WorkspaceOptions
        {
            SourceRoots = SourceRoots.ToArray(),
            LibraryRoots = LibraryRoots.ToArray(),
            TypeshedPath = TypeshedPath,
            Environment = Environment,
        });
        var byUri = new Dictionary<string, ModuleInfo>();
        var uriOf = new Dictionary<ModuleInfo, string>(ReferenceEqualityComparer.Instance);
        foreach (var d in _docs.Values)
        {
            var m = ws.AddModule(d.ModuleName, d.Text, d.IsPackage);
            byUri[d.Uri] = m;
            uriOf[m] = d.Uri;
        }
        var project = new List<ModuleInfo>(byUri.Values);
        if (includeProject)
        {
            var openPaths = new HashSet<string>(_docs.Values.Where(d => d.Path != null).Select(d => System.IO.Path.GetFullPath(d.Path!)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in ProjectFiles())
            {
                if (openPaths.Contains(System.IO.Path.GetFullPath(path))) continue;
                string text;
                try { text = File.ReadAllText(path); } catch (IOException) { continue; }
                var (name, isPackage) = ModuleNameOf(path);
                var m = ws.AddModule(name, text, isPackage);
                uriOf[m] = UriOfPath(path);
                project.Add(m);
            }
        }
        return new Snapshot { Workspace = ws, ByUri = byUri, UriOf = uriOf, Project = project };
    }

    LineMap Lines(ModuleInfo m)
    {
        if (!_lineMaps.TryGetValue(m, out var lm)) _lineMaps[m] = lm = new LineMap(m.Source);
        return lm;
    }

    ModuleInfo? ModuleFor(string uri) => _docs.ContainsKey(uri) ? Current().ByUri[uri] : null;

    string? UriOfModule(ModuleInfo m)
    {
        if (Current().UriOf.TryGetValue(m, out var u)) return u;
        if (_full != null && _full.UriOf.TryGetValue(m, out u)) return u;
        return m.Path != null ? UriOfPath(m.Path) : null;
    }

    // ------------------------------------------------------------ diagnostics

    public IReadOnlyList<LspDiagnostic> Diagnostics(string uri)
    {
        var m = ModuleFor(uri);
        if (m == null) return Array.Empty<LspDiagnostic>();
        var lines = Lines(m);
        return Collect(m, Current().Workspace.Evaluator)
            .Select(x => new LspDiagnostic(lines.RangeOf(x.D.Start, x.D.Start + Math.Max(x.D.Length, 1)), x.Severity, x.D.Message,
                Tags: x.Tags, Code: x.Code))
            .ToList();
    }

    sealed record Diag(Diagnostic D, int Severity, int[]? Tags, string? Code);

    static string? Classify(string message) =>
        message.StartsWith("Unresolved reference") ? "unresolved-reference"
        : message.StartsWith("Cannot find reference") ? "missing-attribute"
        : message == "Unreachable code" ? "unreachable"
        : message.StartsWith("Local variable") ? "unbound-local"
        : null;

    /// <summary>Every finding for a module with its severity and a stable code the quick fixes key on.</summary>
    List<Diag> Collect(ModuleInfo m, TypeEvaluator ev)
    {
        var r = new List<Diag>();
        foreach (var d in m.SyntaxErrors) r.Add(new Diag(d, Severity.Error, null, "syntax-error"));
        foreach (var d in m.Semantics.Diagnostics) r.Add(new Diag(d, Severity.Warning, null, Classify(d.Message)));
        foreach (var d in m.Flow.Analyse())
            r.Add(d.Message == "Unreachable code"
                ? new Diag(d, Severity.Hint, new[] { DiagnosticTag.Unnecessary }, "unreachable")
                : new Diag(d, Severity.Warning, null, Classify(d.Message)));
        foreach (var d in ev.Inspect(m, ReportTypeMismatches)) r.Add(new Diag(d, Severity.Warning, null, Classify(d.Message)));
        foreach (var u in UnusedImports(m))
            r.Add(new Diag(new Diagnostic(u.Start, Math.Max(u.End - u.Start, 1), $"Unused import '{u.Name}'"),
                Severity.Hint, new[] { DiagnosticTag.Unnecessary }, "unused-import"));
        return r;
    }

    // ------------------------------------------------------------ node lookup

    /// <summary>The innermost node of the module containing the offset (end-inclusive, so a cursor right after a name still finds it).</summary>
    static Node? NodeAt(ModuleInfo m, int offset, Func<Node, bool>? filter = null)
    {
        Node? best = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n.Start > offset || offset > n.End) continue;
            if (filter != null && !filter(n)) continue;
            if (best == null || (n.End - n.Start) <= (best.End - best.Start)) best = n;
        }
        return best;
    }

    static (int Start, int End) AttrSpan(AttributeExpr a) => (a.End - a.Attr.Length, a.End);

    /// <summary>Name or attribute expression under the cursor (attributes only when the cursor is on the attribute name).</summary>
    static Expr? NameLikeAt(ModuleInfo m, int offset)
    {
        Expr? best = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n.Start > offset || offset > n.End) continue;
            switch (n)
            {
                case NameExpr:
                    best = (Expr)n;
                    break;
                case AttributeExpr a:
                {
                    var (s, e) = AttrSpan(a);
                    if (offset >= s && offset <= e) best = a;
                    break;
                }
            }
        }
        return best;
    }

    // ------------------------------------------------------------ hover

    public Hover? Hover(string uri, Position pos)
    {
        var m = ModuleFor(uri);
        if (m == null) return null;
        var lines = Lines(m);
        int offset = lines.OffsetOf(pos);
        var ev = Current().Workspace.Evaluator;

        // definition names: `def f`, `class C`
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            (string name, int nameStart)? def = n switch
            {
                FunctionDef f => (f.Name, f.NameStart),
                ClassDef c => (c.Name, c.NameStart),
                _ => null,
            };
            if (def is not { } d || offset < d.nameStart || offset > d.nameStart + d.name.Length) continue;
            var scope = m.Semantics.ScopeOf(n)?.Parent;
            var sym = scope?.Symbols.GetValueOrDefault(d.name);
            if (sym == null) continue;
            return MakeHover(Describe(ev, d.name, ev.TypeOfSymbol(sym)), lines.RangeOf(d.nameStart, d.nameStart + d.name.Length));
        }

        var target = NameLikeAt(m, offset);
        if (target is NameExpr ne)
        {
            var type = ev.TypeOf(m, ne);
            var sym = m.Semantics.Resolve(ne);
            string text = Describe(ev, ne.Name, type);
            if (sym != null && sym.Declarations.Count > 0 && sym.Declarations[0].Kind == DeclKind.Parameter)
                text = "(parameter) " + text;
            return MakeHover(text, lines.RangeOf(ne.Start, ne.End));
        }
        if (target is AttributeExpr ae)
        {
            var (s, e) = AttrSpan(ae);
            return MakeHover(Describe(ev, ae.Attr, ev.TypeOf(m, ae)), lines.RangeOf(s, e));
        }

        // any other expression: show its type
        var expr = NodeAt(m, offset, n => n is Expr and not ErrorExpr) as Expr;
        if (expr == null) return null;
        var t = ev.TypeOf(m, expr);
        if (t is UnknownType) return null;
        return MakeHover($"{FormatType(ev, t)}", lines.RangeOf(expr.Start, expr.End));
    }

    static Hover MakeHover(string code, Range range) =>
        new(new MarkupContent("markdown", "```python\n" + code + "\n```"), range);

    /// <summary>One-line description of a named value: signature for functions, header for classes, annotation-style for the rest.</summary>
    internal static string Describe(TypeEvaluator ev, string name, PyType type)
    {
        switch (type)
        {
            case FunctionType f: return FormatFunction(ev, name, f);
            case OverloadedType o:
                return FormatFunction(ev, name, o.Overloads[0]) + (o.Overloads.Count > 1 ? $"  # +{o.Overloads.Count - 1} overloads" : "");
            case ClassType { IsInstance: false } c: return FormatClassHeader(ev, c);
            case ModuleType mt: return $"(module) {mt.Module.Name}";
            default: return $"{name}: {FormatType(ev, type)}";
        }
    }

    static string FormatClassHeader(TypeEvaluator ev, ClassType c)
    {
        var bases = c.Info.Node.Bases.Where(a => a.Name == null && a.Stars == 0)
            .Select(a => SourceText(c.Info.Module, a.Value)).ToList();
        return $"class {c.Info.Name}" + (bases.Count > 0 ? $"({string.Join(", ", bases)})" : "");
    }

    static string SourceText(ModuleInfo m, Node n) => m.Source.Substring(n.Start, Math.Max(0, Math.Min(n.End, m.Source.Length) - n.Start));

    static string FormatFunction(TypeEvaluator ev, string name, FunctionType f)
    {
        var ps = ev.ParametersOf(f.Info).ToList();
        if (f.BoundFirst && !f.Info.IsStatic && ps.Count > 0 && ps[0].Category is ParamCategory.PositionalOnly or ParamCategory.Normal)
            ps.RemoveAt(0);
        var sb = new StringBuilder();
        sb.Append((f.Info.Flags & FuncFlags.Async) != 0 ? "async def " : "def ").Append(name).Append('(');
        bool sawKwOnly = false, sawPosOnly = false;
        var parts = new List<string>();
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.Category == ParamCategory.KeywordOnly && !sawKwOnly && !ps.Any(x => x.Category == ParamCategory.VarPositional))
            { parts.Add("*"); sawKwOnly = true; }
            if (p.Category == ParamCategory.PositionalOnly) sawPosOnly = true;
            else if (sawPosOnly) { parts.Add("/"); sawPosOnly = false; }

            string prefix = p.Category switch { ParamCategory.VarPositional => "*", ParamCategory.VarKeyword => "**", _ => "" };
            string ann = p.Type is UnknownType { Explicit: false } ? "" : ": " + FormatType(ev, p.Type);
            parts.Add(prefix + p.Name + ann + (p.HasDefault ? " = ..." : ""));
        }
        if (sawPosOnly) parts.Add("/");
        sb.Append(string.Join(", ", parts)).Append(')');
        var ret = ev.ReturnTypeOf(f);
        if (ret is not UnknownType { Explicit: false }) sb.Append(" -> ").Append(FormatType(ev, ret));
        return sb.ToString();
    }

    /// <summary>Type-checker style display (`int | None`, `type[C]`, `list[str]`, `(int) -> str`).</summary>
    internal static string FormatType(TypeEvaluator ev, PyType t, int depth = 0)
    {
        if (depth > 6) return "...";
        switch (t)
        {
            case ClassType { IsInstance: true, Info.Name: "NoneType" }: return "None";
            case ClassType { IsInstance: false } c: return $"type[{FormatType(ev, c.AsInstance(), depth + 1)}]";
            case ClassType c when c.EnumMember != null: return $"{c.Info.Name}.{c.EnumMember}";
            case ClassType c when c.TupleItems != null:
                return c.TupleItems.Count == 0 ? "tuple[()]"
                    : $"tuple[{string.Join(", ", c.TupleItems.Select(x => FormatType(ev, x, depth + 1)))}]";
            case ClassType c when c.Info.Name == "tuple" && c.Info.Module.Name == "builtins" && c.Args.Count == 1:
                return $"tuple[{FormatType(ev, c.Args[0], depth + 1)}, ...]";
            case ClassType c:
                return c.Args.Count == 0 ? c.Info.Name
                    : $"{c.Info.Name}[{string.Join(", ", c.Args.Select(x => FormatType(ev, x, depth + 1)))}]";
            case UnionType u:
                return string.Join(" | ", u.MemberList.Select(x => FormatType(ev, x, depth + 1)));
            case FunctionType f:
                return FormatFunction(ev, f.Info.Name, f);
            case SignatureType s:
                return $"({(s.Params == null ? "..." : string.Join(", ", s.Params.Select(p => FormatType(ev, p.Type, depth + 1))))}) -> {FormatType(ev, s.Return, depth + 1)}";
            case ModuleType mt: return $"Module(\"{mt.Module.Name}\")";
            case UnknownType { Explicit: true }: return "Any";
            case UnknownType: return "Unknown";
            default: return t.Display();
        }
    }

    // ------------------------------------------------------------ definition

    public IReadOnlyList<Location> Definition(string uri, Position pos)
    {
        var m = ModuleFor(uri);
        if (m == null) return Array.Empty<Location>();
        var ws = Current().Workspace;
        var ev = ws.Evaluator;
        int offset = Lines(m).OffsetOf(pos);
        var found = new List<Location>();

        void AddNode(ModuleInfo? module, Node node)
        {
            if (module == null) return;
            var u = UriOfModule(module);
            if (u == null) return;
            var (s, e) = NameSpan(module, node);
            found.Add(new Location(u, Lines(module).RangeOf(s, e)));
        }

        void AddModuleStart(ModuleInfo? module)
        {
            if (module == null) return;
            var u = UriOfModule(module);
            if (u != null) found.Add(new Location(u, new Range(new Position(0, 0), new Position(0, 0))));
        }

        void AddDeclarations(IEnumerable<Declaration> decls, IEnumerable<ModuleInfo> candidates)
        {
            foreach (var d in decls) AddNode(OwnerModule(d.Node, candidates), d.Node);
        }

        // import statements: the module itself, or the imported member
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not ImportAlias alias || offset < alias.Start || offset > alias.End) continue;
            var stmt = m.ParentOf(alias);
            if (stmt is ImportStmt)
            {
                AddModuleStart(ws.GetModule(alias.Name));
                return found;
            }
            if (stmt is ImportFromStmt from)
            {
                var target = ws.GetModule(Workspace.AbsoluteName(m, from.Level, from.Module) ?? "");
                if (target == null) return found;
                var sym = ev.FindModuleSymbol(target, alias.Name);
                if (sym != null) AddDeclarations(sym.Declarations, new[] { target, ws.ModuleOf(sym) ?? target });
                else AddModuleStart(ws.GetModule(target.Name + "." + alias.Name));
                return found;
            }
        }

        var target2 = NameLikeAt(m, offset);
        switch (target2)
        {
            case NameExpr ne:
            {
                var sym = m.Semantics.Resolve(ne);
                if (sym != null)
                {
                    var decls = sym.Declarations;
                    if (decls.Count > 0 && decls.All(d => d.Kind == DeclKind.Import))
                    {
                        // follow the import to where the thing is defined
                        if (!AddTypeDefinition(ev.TypeOf(m, ne), AddNode, AddModuleStart)) AddDeclarations(decls, new[] { m });
                    }
                    else AddDeclarations(decls, new[] { m });
                }
                else AddTypeDefinition(ev.TypeOf(m, ne), AddNode, AddModuleStart);
                break;
            }
            case AttributeExpr ae:
            {
                var recv = ev.TypeOf(m, ae.Value);
                var res = ev.AttributeType(recv, ae.Attr);
                var candidates = CandidateModules(ev, m, recv);
                if (res.Declarations.Count > 0) AddDeclarations(res.Declarations, candidates);
                else foreach (var site in res.InstanceSites) AddNode(OwnerModule(site, candidates), site);
                if (found.Count == 0) AddTypeDefinition(res.Type, AddNode, AddModuleStart);
                break;
            }
        }
        return found;
    }

    static bool AddTypeDefinition(PyType type, Action<ModuleInfo?, Node> addNode, Action<ModuleInfo?> addModule)
    {
        bool any = false;
        foreach (var t in type.Members)
        {
            switch (t)
            {
                case ClassType c: addNode(c.Info.Module, c.Info.Node); any = true; break;
                case FunctionType f: addNode(f.Info.Module, f.Info.Node); any = true; break;
                case OverloadedType o:
                    foreach (var f in o.Overloads) { addNode(f.Info.Module, f.Info.Node); any = true; }
                    break;
                case ModuleType mt: addModule(mt.Module); any = true; break;
            }
        }
        return any;
    }

    /// <summary>The range to highlight for a declaration node: a def/class name, otherwise the node itself.</summary>
    static (int Start, int End) NameSpan(ModuleInfo m, Node node) => node switch
    {
        FunctionDef f => (f.NameStart, f.NameStart + f.Name.Length),
        ClassDef c => (c.NameStart, c.NameStart + c.Name.Length),
        TypeAliasStmt t => (t.NameStart, t.NameStart + t.Name.Length),
        _ => (node.Start, Math.Max(node.End, node.Start)),
    };

    static IEnumerable<ModuleInfo> CandidateModules(TypeEvaluator ev, ModuleInfo current, PyType receiver)
    {
        var result = new List<ModuleInfo> { current };
        foreach (var t in receiver.Members)
        {
            switch (t)
            {
                case ClassType c:
                    foreach (var anc in ev.MroOf(c.Info))
                        if (anc is ClassType ac && !result.Contains(ac.Info.Module)) result.Add(ac.Info.Module);
                    break;
                case ModuleType mt when !result.Contains(mt.Module):
                    result.Add(mt.Module);
                    break;
            }
        }
        return result;
    }

    /// <summary>Which of the candidate modules contains this node (by reference), for declarations that carry no module.</summary>
    static ModuleInfo? OwnerModule(Node node, IEnumerable<ModuleInfo> candidates)
    {
        foreach (var m in candidates)
        {
            Node? cur = node;
            while (cur != null)
            {
                if (ReferenceEquals(cur, m.Ast)) return m;
                cur = m.ParentOf(cur);
            }
        }
        return null;
    }

    // ------------------------------------------------------------ completion

    static readonly string[] SoftKeywords = { "match", "case", "type", "_" };

    public IReadOnlyList<CompletionItem> Completion(string uri, Position pos)
    {
        if (!_docs.TryGetValue(uri, out var doc)) return Array.Empty<CompletionItem>();
        var lines = new LineMap(doc.Text);
        int offset = lines.OffsetOf(pos);

        // the identifier fragment being typed, and whether a dot precedes it
        int start = offset;
        while (start > 0 && IsIdentChar(doc.Text[start - 1])) start--;
        string prefix = doc.Text[start..offset];
        bool afterDot = start > 0 && doc.Text[start - 1] == '.' && !(start > 1 && doc.Text[start - 2] == '.');

        if (afterDot) return MemberCompletion(doc, start, offset, prefix);
        return NameCompletion(doc, start, offset, prefix);
    }

    static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    IReadOnlyList<CompletionItem> MemberCompletion(Doc doc, int start, int end, string prefix)
    {
        // `recv.pre|` is usually a syntax error while typing, so analyse a copy with a complete attribute access
        const string Placeholder = "__pymagic_cursor__";
        string patched = doc.Text[..start] + Placeholder + doc.Text[end..];
        var ws = new Workspace(new WorkspaceOptions
        {
            SourceRoots = SourceRoots.ToArray(), LibraryRoots = LibraryRoots.ToArray(),
            TypeshedPath = TypeshedPath, Environment = Environment,
        });
        foreach (var d in _docs.Values)
            if (d.Uri != doc.Uri) ws.AddModule(d.ModuleName, d.Text, d.IsPackage);
        var m = ws.AddModule(doc.ModuleName, patched, doc.IsPackage);

        AttributeExpr? access = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
            if (n is AttributeExpr a && a.Attr == Placeholder) { access = a; break; }
        if (access == null) return Array.Empty<CompletionItem>();

        var ev = ws.Evaluator;
        var recv = ev.TypeOf(m, access.Value);
        var items = new List<CompletionItem>();
        foreach (var member in ev.MemberNames(recv))
        {
            if (!member.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (member.Name.StartsWith("__") && member.Name.EndsWith("__") && !prefix.StartsWith('_')) continue;
            if (member.Name.StartsWith('_') && !prefix.StartsWith('_')) continue;
            var attr = ev.AttributeType(recv, member.Name);
            items.Add(new CompletionItem(member.Name, KindOf(attr.Type, member.IsInstanceAttribute),
                FormatType(ev, attr.Type) is var t && t != "Unknown" ? t : null,
                SortText: (member.Owner == "object" ? "z" : "a") + member.Name));
        }
        return items;
    }

    static int KindOf(PyType t, bool instanceAttr)
    {
        if (instanceAttr) return CompletionKind.Field;
        return t switch
        {
            FunctionType or OverloadedType => CompletionKind.Method,
            ClassType { IsInstance: false } => CompletionKind.Class,
            ModuleType => CompletionKind.Module,
            _ => CompletionKind.Property,
        };
    }

    IReadOnlyList<CompletionItem> NameCompletion(Doc doc, int start, int end, string prefix)
    {
        var m = Current().ByUri[doc.Uri];
        var ev = Current().Workspace.Evaluator;
        var items = new Dictionary<string, CompletionItem>();

        void Add(string name, int kind, string? detail)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (name.StartsWith('_') && !prefix.StartsWith('_')) return;
            items.TryAdd(name, new CompletionItem(name, kind, detail));
        }

        // enclosing scopes, innermost first; class scopes are only visible from their own body
        var scope = ScopeAtCursor(m, doc.Text, start);
        bool innermost = true;
        for (var s = scope; s != null; s = s.Parent)
        {
            if (s.Kind == ScopeKind.Class && !innermost) continue;
            foreach (var sym in s.Symbols.Values)
            {
                if (sym.Declarations.Count == 0) continue;
                var type = ev.TypeOfSymbol(sym);
                Add(sym.Name, sym.Declarations[0].Kind switch
                {
                    DeclKind.Function => s.Kind == ScopeKind.Class ? CompletionKind.Method : CompletionKind.Function,
                    DeclKind.Class => CompletionKind.Class,
                    DeclKind.Import => type is ModuleType ? CompletionKind.Module : KindOf(type, false),
                    DeclKind.TypeParam => CompletionKind.TypeParameter,
                    _ => CompletionKind.Variable,
                }, type is UnknownType ? null : FormatType(ev, type));
            }
            innermost = false;
        }

        foreach (var kw in Lexer.Keywords) Add(kw, CompletionKind.Keyword, null);
        foreach (var kw in SoftKeywords) if (kw != "_") Add(kw, CompletionKind.Keyword, null);

        foreach (var name in Builtins.Names)
            Add(name, char.IsUpper(name[0]) ? CompletionKind.Class : CompletionKind.Function, null);

        return items.Values.ToList();
    }

    /// <summary>
    /// The scope the cursor is typing in. A new, still empty line after a function body lies outside the parsed node,
    /// so indentation decides which preceding def/class the line continues.
    /// </summary>
    static Scope ScopeAtCursor(ModuleInfo m, string text, int offset)
    {
        var scope = m.Semantics.InnermostScopeAt(offset);
        int lineStart = offset;
        while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r') lineStart--;
        if (text[lineStart..offset].Any(c => c != ' ' && c != '\t')) return scope;   // inside a statement: the tree is right
        int cursorIndent = offset - lineStart;

        while (true)
        {
            Scope? next = null;
            foreach (var c in scope.Children)
            {
                if (c.Kind is not (ScopeKind.Function or ScopeKind.Class) || c.Node.End > lineStart) continue;
                if (next == null || c.Node.End >= next.Node.End) next = c;
            }
            if (next == null) return scope;

            int defLineStart = next.Node.Start;
            while (defLineStart > 0 && text[defLineStart - 1] != '\n' && text[defLineStart - 1] != '\r') defLineStart--;
            int defIndent = 0;
            while (defLineStart + defIndent < text.Length && text[defLineStart + defIndent] is ' ' or '\t') defIndent++;
            if (cursorIndent <= defIndent) return scope;

            // only blank lines and comments may lie between the end of the def and the cursor line
            foreach (var line in text[Math.Min(next.Node.End, lineStart)..lineStart].Split('\n', '\r'))
                if (line.Trim().Length > 0 && !line.TrimStart().StartsWith('#')) return scope;
            scope = next;
        }
    }

    // ------------------------------------------------------------ document symbols

    public IReadOnlyList<DocumentSymbol> DocumentSymbols(string uri)
    {
        var m = ModuleFor(uri);
        if (m == null) return Array.Empty<DocumentSymbol>();
        var lines = Lines(m);
        var ev = Current().Workspace.Evaluator;
        return SymbolsOf(m.Ast.Body, m, lines, ev, inClass: false);
    }

    List<DocumentSymbol> SymbolsOf(IEnumerable<Stmt> body, ModuleInfo m, LineMap lines, TypeEvaluator ev, bool inClass)
    {
        var result = new List<DocumentSymbol>();
        foreach (var st in body)
        {
            switch (st)
            {
                case FunctionDef f:
                    result.Add(new DocumentSymbol(f.Name,
                        inClass ? (f.Name == "__init__" ? SymbolKind.Constructor : SymbolKind.Method) : SymbolKind.Function,
                        lines.RangeOf(f.Start, f.End), lines.RangeOf(f.NameStart, f.NameStart + f.Name.Length),
                        null, SymbolsOf(f.Body, m, lines, ev, false)));
                    break;
                case ClassDef c:
                    result.Add(new DocumentSymbol(c.Name, SymbolKind.Class,
                        lines.RangeOf(c.Start, c.End), lines.RangeOf(c.NameStart, c.NameStart + c.Name.Length),
                        null, SymbolsOf(c.Body, m, lines, ev, true)));
                    break;
                case AssignStmt a:
                    foreach (var t in a.Targets.OfType<NameExpr>())
                        result.Add(Variable(t, a, m, lines, inClass));
                    break;
                case AnnAssignStmt aa when aa.Target is NameExpr tn:
                    result.Add(Variable(tn, aa, m, lines, inClass));
                    break;
                case IfStmt i:
                    result.AddRange(SymbolsOf(i.Body, m, lines, ev, inClass));
                    result.AddRange(SymbolsOf(i.Orelse, m, lines, ev, inClass));
                    break;
                case TryStmt t:
                    result.AddRange(SymbolsOf(t.Body, m, lines, ev, inClass));
                    foreach (var h in t.Handlers) result.AddRange(SymbolsOf(h.Body, m, lines, ev, inClass));
                    result.AddRange(SymbolsOf(t.Orelse, m, lines, ev, inClass));
                    break;
            }
        }
        return result;
    }

    static DocumentSymbol Variable(NameExpr name, Stmt stmt, ModuleInfo m, LineMap lines, bool inClass)
    {
        int kind = name.Name.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_') ? SymbolKind.Constant
            : inClass ? SymbolKind.Field : SymbolKind.Variable;
        return new DocumentSymbol(name.Name, kind, lines.RangeOf(stmt.Start, stmt.End), lines.RangeOf(name.Start, name.End), null, new());
    }
}
