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
        public required FixtureContext Fixtures { get; init; }
        /// <summary>The project's classes by the class they derive from (built on first use, see SubclassIndex).</summary>
        public Dictionary<ClassDef, List<(ModuleInfo Module, ClassDef Class)>>? Subclasses { get; set; }
    }

    readonly Dictionary<string, Doc> _docs = new();
    readonly Dictionary<ModuleInfo, LineMap> _lineMaps = new(ReferenceEqualityComparer.Instance);
    Snapshot? _snapshot, _full;
    readonly AnalysisCache _cache;

    public LanguageService() : this(new AnalysisCache()) { }

    LanguageService(AnalysisCache cache) { _cache = cache; }

    /// <summary>Parsed-module cache shared by all analysis snapshots of this session (exposed for tests and diagnostics).</summary>
    public AnalysisCache Cache => _cache;

    public List<string> SourceRoots { get; } = new();
    public List<string> LibraryRoots { get; } = new();
    public string? TypeshedPath { get; set; } = Workspace.DefaultTypeshedPath;
    public PythonEnvironment Environment { get; set; } = PythonEnvironment.Default;

    /// <summary>The interpreter whose site-packages and version the analysis follows (null: just the configured roots and the default version).</summary>
    public PythonInterpreter? Interpreter { get; private set; }

    /// <summary>Folders searched for libraries: the explicitly configured ones, then the interpreter's.</summary>
    IEnumerable<string> EffectiveLibraryRoots() =>
        Interpreter == null ? LibraryRoots : LibraryRoots.Concat(Interpreter.LibraryRoots(includeStdlib: TypeshedPath != null)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Analyse for this interpreter: its Python version and platform decide which branches of `sys.version_info` and
    /// `sys.platform` checks are live, and its site-packages (with whatever .pth files add) become library roots.
    /// </summary>
    public void ConfigureInterpreter(PythonInterpreter? interpreter)
    {
        Interpreter = interpreter;
        Environment = interpreter == null ? PythonEnvironment.Default : new PythonEnvironment(interpreter.Major, interpreter.Minor, interpreter.Platform);
        Invalidate();
    }
    /// <summary>Report argument-type mismatches too (noisier than the default arity/keyword checks).</summary>
    public bool ReportTypeMismatches { get; set; }

    public IEnumerable<string> OpenUris => _docs.Keys;
    public int? VersionOf(string uri) => _docs.TryGetValue(uri, out var d) ? d.Version : _templateDocs.TryGetValue(uri, out var t) ? t.Version : null;

    // ------------------------------------------------------------ documents

    public void Open(string uri, string text, int version) => Set(uri, text, version);
    public void Change(string uri, string text, int version) => Set(uri, text, version);
    public void Close(string uri)
    {
        if (_templateDocs.Remove(uri)) return;
        if (_docs.Remove(uri)) Invalidate();
    }

    void Set(string uri, string text, int version)
    {
        // a template file is read by the template features and never reaches the Python analysis
        if (IsTemplateUri(uri)) { _templateDocs[uri] = (text, version); return; }
        string? path = PathOfUri(uri);
        var (name, isPackage) = ModuleNameOf(path);
        _docs[uri] = new Doc(uri, path, name, isPackage, text, version);
        Invalidate();
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
        foreach (var root in SourceRoots.Concat(EffectiveLibraryRoots()))
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
    Snapshot Full()
    {
        if (_needle == null) return _full ??= BuildSnapshot(true, null);
        if (_filtered != null && _filteredNeedle == _needle) return _filtered;
        _filteredNeedle = _needle;
        return _filtered = BuildSnapshot(true, _needle);
    }

    void Invalidate()
    {
        _snapshot = _full = _filtered = null;
        _filteredNeedle = null;
    }

    // Find usages and rename only need the project files that mention the name: the others are not read into the analysis
    // (a 2,000-file project took 6 s and 750 MB to answer one query before this).
    Snapshot? _filtered;
    string? _filteredNeedle, _needle;

    /// <summary>While alive, <see cref="Full"/> covers only the project files whose text contains the identifier under the cursor.</summary>
    IDisposable Searching(string uri, Position pos)
    {
        string? word = _docs.TryGetValue(uri, out var doc) ? WordAt(doc.Text, new LineMap(doc.Text).OffsetOf(pos)) : null;
        var before = _needle;
        _needle = word;
        return new Restore(() => _needle = before);
    }

    sealed class Restore : IDisposable
    {
        readonly Action _done;
        public Restore(Action done) { _done = done; }
        public void Dispose() => _done();
    }

    static string? WordAt(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;
        static bool Part(char c) => char.IsLetterOrDigit(c) || c == '_';
        int s = offset, e = offset;
        while (s > 0 && Part(text[s - 1])) s--;
        while (e < text.Length && Part(text[e])) e++;
        return e > s ? text[s..e] : null;
    }

    /// <summary>The file can hold a usage of the name: the name is written in it somewhere other than a comment or a docstring.</summary>
    static bool FileMentions(string path, string needle)
    {
        try
        {
            string text = File.ReadAllText(path);
            return text.Contains(needle, StringComparison.Ordinal) && MentionsOutsideProse(text, needle);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// False when every place the text holds the name is a comment or a string that stands alone as a statement (a docstring, as in the
    /// `model = X.from_pretrained(...)` examples most model files of a big library carry): such a file has no usage, and is not worth parsing.
    /// Any other string (an argument, an annotation, an f-string) counts, since names are looked for there too.
    /// </summary>
    public static bool MentionsOutsideProse(string text, string needle)
    {
        var tokens = Lexer.Tokenize(text);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Length == 0 || t.Kind == TokenKind.Comment || text.AsSpan(t.Start, t.Length).IndexOf(needle, StringComparison.Ordinal) < 0) continue;
            if (t.Kind == TokenKind.String && !IsFormatted(text, t) && StandsAlone(tokens, i)) continue;
            return true;
        }
        return false;
    }

    static bool IsFormatted(string text, Token t)
    {
        for (int i = t.Start; i < t.End && text[i] is not ('"' or '\''); i++)
            if (text[i] is 'f' or 'F' or 't' or 'T') return true;
        return false;
    }

    /// <summary>The string is a whole statement: the line before it ended (or the block began), and the line ends after it.</summary>
    static bool StandsAlone(List<Token> tokens, int at)
    {
        int before = at - 1;
        while (before >= 0 && tokens[before].Kind == TokenKind.Comment) before--;
        bool startsLine = before < 0 || tokens[before].Kind is TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent;
        int after = at + 1;
        while (after < tokens.Count && tokens[after].Kind == TokenKind.Comment) after++;
        bool endsLine = after >= tokens.Count || tokens[after].Kind is TokenKind.Newline or TokenKind.EndOfFile or TokenKind.Dedent;
        return startsLine && endsLine;
    }

    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "__pycache__", "node_modules", ".venv", "venv", "env", ".tox", ".mypy_cache", "bin", "obj", "site-packages",
    };

    List<string>? _projectFilesOverride;

    IEnumerable<string> ProjectFiles()
    {
        if (_projectFilesOverride != null) { foreach (var f in _projectFilesOverride) yield return f; yield break; }
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

    WorkspaceOptions WorkspaceOptionsNow(Func<Workspace, ModuleInfo, Parameter, PyType?>? parameterTypes = null) => new()
    {
        SourceRoots = SourceRoots.Concat(ImplicitSourceRoots()).ToArray(),
        LibraryRoots = EffectiveLibraryRoots().ToArray(),
        TypeshedPath = TypeshedPath,
        Environment = Environment,
        ParameterTypes = parameterTypes,
    };

    Snapshot BuildSnapshot(bool includeProject, string? needle = null)
    {
        var (ws, fixtures) = NewWorkspace();
        var byUri = new Dictionary<string, ModuleInfo>();
        var uriOf = new Dictionary<ModuleInfo, string>(ReferenceEqualityComparer.Instance);
        foreach (var d in _docs.Values)
        {
            var m = ws.AddModule(d.ModuleName, d.Text, d.IsPackage);
            byUri[d.Uri] = m;
            uriOf[m] = d.Uri;
            fixtures.Register(m, d.Uri, d.Path);
        }
        var project = new List<ModuleInfo>(byUri.Values);
        if (includeProject)
        {
            var openPaths = new HashSet<string>(_docs.Values.Where(d => d.Path != null).Select(d => System.IO.Path.GetFullPath(d.Path!)),
                StringComparer.OrdinalIgnoreCase);
            var candidates = ProjectFiles().Where(path => !openPaths.Contains(System.IO.Path.GetFullPath(path))).ToList();
            // the texts are read and parsed on every core at once; the workspace below then only registers what is ready
            if (needle != null) candidates = candidates.AsParallel().AsOrdered().Where(path => FileMentions(path, needle)).ToList();
            var named = candidates.Select(path => (Path: path, Name: ModuleNameOf(path))).ToList();
            ws.Preload(named.Select(n => (n.Name.Name, n.Path, n.Name.IsPackage)).ToList());
            foreach (var (path, (name, isPackage)) in named)
            {
                ModuleInfo m;
                try { m = ws.AddDiskModule(name, path, isPackage); } catch (IOException) { continue; }
                uriOf[m] = UriOfPath(path);
                fixtures.Register(m, uriOf[m], path);
                project.Add(m);
            }
        }
        return new Snapshot { Workspace = ws, ByUri = byUri, UriOf = uriOf, Project = project, Fixtures = fixtures };
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
        if (_filtered != null && _filtered.UriOf.TryGetValue(m, out u)) return u;
        return m.Path != null ? UriOfPath(m.Path) : null;
    }

    // ------------------------------------------------------------ diagnostics

    public IReadOnlyList<LspDiagnostic> Diagnostics(string uri)
    {
        if (_templateDocs.ContainsKey(uri)) return TemplateDiagnostics(uri);
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
        : message.StartsWith("Access to a protected member") ? "protected-member"
        : message.EndsWith("does not support item assignment") ? "tuple-item-assignment"
        : message.EndsWith("doesn't return anything") ? "none-function-assignment"
        : message.Contains("is already handled by an earlier clause") ? "except-clauses-order"
        : message.StartsWith("Cannot assign to attribute") && message.EndsWith("is frozen") ? "dataclass-frozen"
        : message.StartsWith("Signature of method") ? "method-override"
        : message.StartsWith("Cannot assign to final") ? "final-reassignment"
        : message.StartsWith("Exceptions must derive") ? "exception-inherit"
        : message.StartsWith("Property '") && message.EndsWith("cannot be set") ? "property-access"
        : message.StartsWith("Expected type '") ? "argument-type"
        : message.Contains("is not callable") ? "not-callable"
        : message.StartsWith("Unexpected argument") || message.StartsWith("Multiple values for parameter") || message.EndsWith(" unfilled")
          || message.StartsWith("No overload of") || message.EndsWith("takes no arguments") ? "call-arguments"
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
        foreach (var d in ev.Inspect(m, ReportTypeMismatches))
        {
            string? code = Classify(d.Message);
            r.Add(new Diag(d, DefaultSeverity(code), null, code));
        }
        foreach (var p in ImportProblems(m, ev))
            r.Add(new Diag(new Diagnostic(p.Start, Math.Max(p.End - p.Start, 1), p.Message), Severity.Warning, null, p.Code));
        foreach (var u in UnusedImports(m))
            r.Add(new Diag(new Diagnostic(u.Start, Math.Max(u.End - u.Start, 1), $"Unused import '{u.Name}'"),
                Severity.Hint, new[] { DiagnosticTag.Unnecessary }, "unused-import"));
        r.AddRange(InspectionDiagnostics(m, ev));
        return ApplyProfile(r);
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
        if (_templateDocs.ContainsKey(uri)) return TemplateHover(uri, pos);
        var m = ModuleFor(uri);
        if (m == null) return null;
        var lines = Lines(m);
        int offset = lines.OffsetOf(pos);
        var ev = Current().Workspace.Evaluator;

        if (FixtureHover(Current(), m, offset) is { } fixtureHover) return fixtureHover;
        if (DjangoHover(Current(), m, offset) is { } djangoHover) return djangoHover;

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
            // a decorated def whose parameters a ParamSpec kept (`@contextmanager def cm(x: int)`): those parameters, by name
            case SignatureType { Origin: not null, Params: { } ps } s: return FormatDef(ev, "def ", name, ps, s.Return);
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
        return FormatDef(ev, (f.Info.Flags & FuncFlags.Async) != 0 ? "async def " : "def ", name, ps, ev.ReturnTypeOf(f));
    }

    static string FormatDef(TypeEvaluator ev, string keyword, string name, IReadOnlyList<ParamInfo> ps, PyType ret)
    {
        var sb = new StringBuilder();
        sb.Append(keyword).Append(name).Append('(');
        sb.Append(string.Join(", ", ParamParts(ev, ps).Select(x => x.Text))).Append(')');
        if (ret is not UnknownType { Explicit: false }) sb.Append(" -> ").Append(FormatType(ev, ret));
        return sb.ToString();
    }

    /// <summary>The pieces of a parameter list as written: each parameter (`*args: int`, `x = ...`) and the bare `*` and `/` between them (those have no parameter).</summary>
    static List<(string Text, ParamInfo? Param)> ParamParts(TypeEvaluator ev, IReadOnlyList<ParamInfo> ps)
    {
        bool sawKwOnly = false, sawPosOnly = false;
        var parts = new List<(string, ParamInfo?)>();
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.Category == ParamCategory.KeywordOnly && !sawKwOnly && !ps.Any(x => x.Category == ParamCategory.VarPositional))
            { parts.Add(("*", null)); sawKwOnly = true; }
            if (p.Category == ParamCategory.PositionalOnly) sawPosOnly = true;
            else if (sawPosOnly) { parts.Add(("/", null)); sawPosOnly = false; }

            string prefix = p.Category switch { ParamCategory.VarPositional => "*", ParamCategory.VarKeyword => "**", _ => "" };
            string ann = p.Type is UnknownType { Explicit: false } ? "" : ": " + FormatType(ev, p.Type);
            parts.Add((prefix + p.Name + ann + (p.HasDefault ? " = ..." : ""), p));
        }
        if (sawPosOnly) parts.Add(("/", null));
        return parts;
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
            case ClassType { LiteralValue: { } literal }: return $"Literal[{ClassType.LiteralText(literal)}]";
            case ClassType c when c.TupleItems != null:
                return c.TupleItems.Count == 0 ? "tuple[()]"
                    : $"tuple[{string.Join(", ", c.TupleItems.Select(x => FormatType(ev, x, depth + 1)))}]";
            case ClassType c when c.Info.Name == "tuple" && c.Info.Module.Name == "builtins" && c.Args.Count == 1:
                return $"tuple[{FormatType(ev, c.Args[0], depth + 1)}, ...]";
            case ClassType c:
                return c.Args.Count == 0 ? c.Info.Name
                    : $"{c.Info.Name}[{string.Join(", ", c.Args.Select(x => FormatType(ev, x, depth + 1)))}]";
            case UnionType u:
            {
                // the literal members share one Literal[...], written where the first of them stands
                var parts = new List<string>();
                List<string>? literals = null;
                int literalAt = -1;
                foreach (var x in u.MemberList)
                {
                    if (x is ClassType { IsInstance: true, LiteralValue: { } lv })
                    {
                        if (literals == null) { literals = new List<string>(); literalAt = parts.Count; parts.Add(""); }
                        literals.Add(ClassType.LiteralText(lv));
                    }
                    else parts.Add(FormatType(ev, x, depth + 1));
                }
                if (literals != null) parts[literalAt] = "Literal[" + string.Join(", ", literals) + "]";
                return string.Join(" | ", parts);
            }
            case FunctionType f:
                return FormatFunction(ev, f.Info.Name, f);
            case SignatureType s:
                return $"({(s.Params == null ? "..." : string.Join(", ", s.Params.Select(p => FormatType(ev, p.Type, depth + 1)).Concat(s.Spec == null ? [] : ["**" + s.Spec.Name])))}) -> {FormatType(ev, s.Return, depth + 1)}";
            case ModuleType mt: return $"Module(\"{mt.Module.Name}\")";
            case UnknownType { Explicit: true }: return "Any";
            case UnknownType: return "Unknown";
            default: return t.Display();
        }
    }

    // ------------------------------------------------------------ definition

    public IReadOnlyList<Location> Definition(string uri, Position pos)
    {
        if (_templateDocs.ContainsKey(uri)) return TemplateDefinition(uri, pos);
        var m = ModuleFor(uri);
        if (m == null) return Array.Empty<Location>();
        var ws = Current().Workspace;
        var ev = ws.Evaluator;
        int offset = Lines(m).OffsetOf(pos);
        var found = new List<Location>();

        if (FixtureDefinition(uri, pos) is { Count: > 0 } fixtureLocations) return fixtureLocations;
        if (DjangoDefinition(uri, pos) is { Count: > 0 } djangoLocations) return djangoLocations;

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
                // a declaration found through a hook (a setting, say) can lie in any module of the project
                var candidates = CandidateModules(ev, m, recv).Concat(ws.UserModules).Distinct().ToList();
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

    static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
        return SymbolsOf(m.Ast.Body, lines, inClass: false);
    }

    /// <summary>The outline of a module from its syntax alone (the symbol index builds it for files nobody has open).</summary>
    internal static List<DocumentSymbol> SymbolsOf(IEnumerable<Stmt> body, LineMap lines, bool inClass)
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
                        null, SymbolsOf(f.Body, lines, false)));
                    break;
                case ClassDef c:
                    result.Add(new DocumentSymbol(c.Name, SymbolKind.Class,
                        lines.RangeOf(c.Start, c.End), lines.RangeOf(c.NameStart, c.NameStart + c.Name.Length),
                        null, SymbolsOf(c.Body, lines, true)));
                    break;
                case AssignStmt a:
                    foreach (var t in a.Targets.OfType<NameExpr>())
                        result.Add(Variable(t, a, lines, inClass));
                    break;
                case AnnAssignStmt aa when aa.Target is NameExpr tn:
                    result.Add(Variable(tn, aa, lines, inClass));
                    break;
                case IfStmt i:
                    result.AddRange(SymbolsOf(i.Body, lines, inClass));
                    result.AddRange(SymbolsOf(i.Orelse, lines, inClass));
                    break;
                case TryStmt t:
                    result.AddRange(SymbolsOf(t.Body, lines, inClass));
                    foreach (var h in t.Handlers) result.AddRange(SymbolsOf(h.Body, lines, inClass));
                    result.AddRange(SymbolsOf(t.Orelse, lines, inClass));
                    break;
            }
        }
        return result;
    }

    static DocumentSymbol Variable(NameExpr name, Stmt stmt, LineMap lines, bool inClass)
    {
        int kind = name.Name.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_') ? SymbolKind.Constant
            : inClass ? SymbolKind.Field : SymbolKind.Variable;
        return new DocumentSymbol(name.Name, kind, lines.RangeOf(stmt.Start, stmt.End), lines.RangeOf(name.Start, name.End), null, new());
    }
}
