using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// Semantic highlighting (pymagic/semanticTokens): what each name is, from the code's meaning rather than its spelling - a module, a
/// class, an enum, a type parameter, a parameter (self and cls apart), a variable or a constant, a property, an enum member, a function,
/// a method, a decorator, a builtin - and the soft keywords match, case and type where they are keywords. The data is LSP's: five
/// numbers a token (line and column deltas, length, type, modifier bits), in the order of <see cref="SemanticTokenTypes"/>.
/// </summary>
public sealed partial class LanguageService
{
    public static readonly string[] SemanticTokenTypes =
        { "namespace", "class", "enum", "typeParameter", "parameter", "selfParameter", "variable", "property", "enumMember", "function", "method", "decorator", "softKeyword" };

    // readonly and defaultLibrary first: Monaco reads a token as its type and modifiers joined by dots in this order, and matches theme rules by prefix
    public static readonly string[] SemanticTokenModifiers = { "readonly", "defaultLibrary", "async", "static", "declaration" };

    enum Tok { Namespace, Class, Enum, TypeParameter, Parameter, SelfParameter, Variable, Property, EnumMember, Function, Method, Decorator, Keyword }

    [Flags] enum TokMod { None = 0, Readonly = 1, DefaultLibrary = 2, Async = 4, Static = 8, Declaration = 16 }

    static readonly Regex ConstantName = new(@"^_*[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    public int[] SemanticTokens(string uri)
    {
        var m = ModuleFor(uri);
        if (m == null) return Array.Empty<int>();
        var ev = Current().Workspace.Evaluator;
        var found = new Dictionary<int, (int Length, Tok Type, TokMod Mods)>();
        void Add(int start, int length, Tok type, TokMod mods = TokMod.None)
        {
            if (length > 0 && start >= 0 && !found.ContainsKey(start)) found[start] = (length, type, mods);
        }

        string text = m.Source;
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ClassDef cd:
                    foreach (var d in cd.Decorators) DecoratorTokens(d, Add);
                    var info = ClassInfoOf(m, cd, ev);
                    Add(cd.NameStart, cd.Name.Length, info != null && IsEnumClass(info, ev) ? Tok.Enum : Tok.Class, TokMod.Declaration);
                    foreach (var tp in cd.TypeParams) Add(tp.Start, tp.Name.Length, Tok.TypeParameter, TokMod.Declaration);
                    break;
                case FunctionDef fd:
                {
                    foreach (var d in fd.Decorators) DecoratorTokens(d, Add);
                    bool method = m.ParentOf(fd) is ClassDef;
                    bool isStatic = fd.Decorators.Any(d => DecoratorName(d) == "staticmethod");
                    var mods = TokMod.Declaration | (fd.IsAsync ? TokMod.Async : 0) | (isStatic ? TokMod.Static : 0);
                    Add(fd.NameStart, fd.Name.Length, method ? Tok.Method : Tok.Function, mods);
                    foreach (var tp in fd.TypeParams) Add(tp.Start, tp.Name.Length, Tok.TypeParameter, TokMod.Declaration);
                    for (int i = 0; i < fd.Params.Count; i++)
                    {
                        var p = fd.Params[i];
                        if (p.Name.Length == 0) continue;
                        int at = text.IndexOf(p.Name, p.Start, StringComparison.Ordinal);
                        bool receiver = i == 0 && method && !isStatic && p.Name is "self" or "cls";
                        if (at >= 0) Add(at, p.Name.Length, receiver ? Tok.SelfParameter : Tok.Parameter, TokMod.Declaration);
                    }
                    break;
                }
                case LambdaExpr lambda:
                    foreach (var p in lambda.Params.Where(p => p.Name.Length > 0))
                        if (text.IndexOf(p.Name, p.Start, StringComparison.Ordinal) is var at && at >= 0) Add(at, p.Name.Length, Tok.Parameter, TokMod.Declaration);
                    break;
                case TypeAliasStmt ta:
                    Add(ta.Start, 4, Tok.Keyword);
                    Add(ta.NameStart, ta.Name.Length, Tok.Class, TokMod.Declaration);
                    foreach (var tp in ta.TypeParams) Add(tp.Start, tp.Name.Length, Tok.TypeParameter, TokMod.Declaration);
                    break;
                case MatchStmt ms:
                    Add(ms.Start, 5, Tok.Keyword);
                    foreach (var c in ms.Cases)
                    {
                        // `case` is the word before the pattern on its line
                        int at = text.LastIndexOf("case", c.Pattern.Start, Math.Min(c.Pattern.Start + 1, 64), StringComparison.Ordinal);
                        if (at >= 0) Add(at, 4, Tok.Keyword);
                    }
                    break;
                case ImportStmt imp:
                    foreach (var a in imp.Names) ImportTokens(text, a, a.Name, Add);
                    break;
                case ImportFromStmt imp:
                {
                    if (imp.Module != null)
                    {
                        int at = text.IndexOf(imp.Module, imp.Start, StringComparison.Ordinal);
                        if (at >= 0) DottedTokens(at, imp.Module, Add);
                    }
                    foreach (var a in imp.Names)
                    {
                        var kind = ImportedKind(m, ev, imp, a);
                        int at = text.IndexOf(a.Name, a.Start, StringComparison.Ordinal);
                        if (at >= 0) Add(at, a.Name.Length, kind.Type, kind.Mods);
                        if (a.AsName != null && text.IndexOf(a.AsName, at + a.Name.Length, StringComparison.Ordinal) is var asAt && asAt >= 0 && asAt < a.End)
                            Add(asAt, a.AsName.Length, kind.Type, kind.Mods);
                    }
                    break;
                }
                case NameExpr n:
                    if (NameKind(m, ev, n) is { } nk) Add(n.Start, n.Name.Length, nk.Type, nk.Mods);
                    break;
                case AttributeExpr a:
                    if (AttributeKind(m, ev, a) is { } ak) Add(a.End - a.Attr.Length, a.Attr.Length, ak.Type, ak.Mods);
                    break;
            }
        }

        // LSP's encoding: sorted, each token relative to the one before
        var lines = Lines(m);
        var data = new List<int>(found.Count * 5);
        int lastLine = 0, lastChar = 0;
        foreach (var (start, (length, type, mods)) in found.OrderBy(kv => kv.Key))
        {
            var pos = lines.PositionOf(start);
            int line = pos.Line, ch = pos.Character;
            data.Add(line - lastLine);
            data.Add(line == lastLine ? ch - lastChar : ch);
            data.Add(length);
            data.Add((int)type);
            data.Add((int)mods);
            lastLine = line;
            lastChar = ch;
        }
        return data.ToArray();
    }

    static bool IsEnumClass(ClassInfo c, TypeEvaluator ev) =>
        ev.MroOf(c).Any(e => e is ClassType t && t.Info.Name is "Enum" or "IntEnum" or "StrEnum" or "Flag" or "IntFlag" && t.Info.Module.Name == "enum");

    /// <summary>`@app.route(...)`: the decorator's own name (and the names it is reached through) as decorator.</summary>
    static void DecoratorTokens(Expr d, Action<int, int, Tok, TokMod> add)
    {
        var head = d is CallExpr c ? c.Func : d;
        switch (head)
        {
            case NameExpr n: add(n.Start, n.Name.Length, Tok.Decorator, TokMod.None); break;
            case AttributeExpr a: add(a.End - a.Attr.Length, a.Attr.Length, Tok.Decorator, TokMod.None); break;
        }
    }

    /// <summary>Each part of a dotted module name as a namespace.</summary>
    static void DottedTokens(int at, string dotted, Action<int, int, Tok, TokMod> add)
    {
        foreach (var part in dotted.Split('.'))
        {
            add(at, part.Length, Tok.Namespace, TokMod.None);
            at += part.Length + 1;
        }
    }

    static void ImportTokens(string text, ImportAlias a, string module, Action<int, int, Tok, TokMod> add)
    {
        int at = text.IndexOf(module, a.Start, StringComparison.Ordinal);
        if (at < 0) return;
        DottedTokens(at, module, add);
        if (a.AsName != null && text.IndexOf(a.AsName, at + module.Length, StringComparison.Ordinal) is var asAt && asAt >= 0 && asAt < a.End)
            add(asAt, a.AsName.Length, Tok.Namespace, TokMod.None);
    }

    static (Tok Type, TokMod Mods) KindOfType(PyType t, string name, bool builtin) => t switch
    {
        ModuleType => (Tok.Namespace, TokMod.None),
        ClassType { IsInstance: false } => (Tok.Class, builtin ? TokMod.DefaultLibrary : TokMod.None),
        FunctionType or OverloadedType => (Tok.Function, builtin ? TokMod.DefaultLibrary : TokMod.None),
        ClassType { EnumMember: not null } => (Tok.EnumMember, TokMod.Readonly),
        _ => (Tok.Variable, ConstantName.IsMatch(name) ? TokMod.Readonly : TokMod.None),
    };

    (Tok Type, TokMod Mods) ImportedKind(ModuleInfo m, TypeEvaluator ev, ImportFromStmt imp, ImportAlias a)
    {
        string local = a.AsName ?? a.Name;
        var sym = m.Semantics.ModuleScope.Symbols.GetValueOrDefault(local);
        if (sym == null || ev.TypeOfSymbol(sym) is not { } t || t is UnknownType)
            return Current().Workspace.GetModule($"{Workspace.AbsoluteName(m, imp.Level, imp.Module)}.{a.Name}") != null ? (Tok.Namespace, TokMod.None) : (Tok.Variable, TokMod.None);
        return KindOfType(t, a.Name, false);
    }

    /// <summary>What a name stands for: by its declarations in the file, else by its type (imports, builtins); null to leave the grammar's colour.</summary>
    static (Tok Type, TokMod Mods)? NameKind(ModuleInfo m, TypeEvaluator ev, NameExpr n)
    {
        if (n.Name is "True" or "False" or "None" or "__debug__") return null;
        var sym = m.Semantics.Resolve(n);
        if (sym != null && sym.Declarations.Count > 0)
        {
            var decl = sym.Declarations[0];
            switch (decl.Kind)
            {
                case DeclKind.Class: return (ClassInfoOfDecl(m, ev, sym) is { } c && IsEnumClass(c, ev) ? Tok.Enum : Tok.Class, TokMod.None);
                case DeclKind.Function: return (decl.Node is FunctionDef fd && m.ParentOf(fd) is ClassDef ? Tok.Method : Tok.Function, decl.Node is FunctionDef { IsAsync: true } ? TokMod.Async : TokMod.None);
                case DeclKind.Parameter: return (n.Name is "self" or "cls" && IsReceiver(m, decl.Node) ? Tok.SelfParameter : Tok.Parameter, TokMod.None);
                case DeclKind.TypeParam: return (Tok.TypeParameter, TokMod.None);
                case DeclKind.TypeAlias: return (Tok.Class, TokMod.None);
                case DeclKind.Import:
                    return ev.TypeOf(m, n) is var it && it is not UnknownType ? KindOfType(it, n.Name, false) : (Tok.Namespace, TokMod.None);
                default:
                {
                    // a variable that holds a class or a module is shown as one (Model = get_user_model())
                    var t = ev.TypeOf(m, n);
                    if (t is ClassType { IsInstance: false } or ModuleType) return KindOfType(t, n.Name, false);
                    bool final = sym.Declarations.Any(d => d.Node is NameExpr target && m.ParentOf(target) is AnnAssignStmt { Annotation: var ann } && TypingName(ann is SubscriptExpr s ? s.Value : ann) == "Final");
                    var mods = (ConstantName.IsMatch(n.Name) || final ? TokMod.Readonly : TokMod.None) | (sym.Declarations.Any(d => ReferenceEquals(d.Node, n)) ? TokMod.Declaration : TokMod.None);
                    return (Tok.Variable, mods);
                }
            }
        }
        // nothing in the file binds it: a builtin
        var bt = ev.TypeOf(m, n);
        return bt is UnknownType ? null : KindOfType(bt, n.Name, true);
    }

    static ClassInfo? ClassInfoOfDecl(ModuleInfo m, TypeEvaluator ev, Symbol sym) =>
        ev.TypeOfSymbol(sym) is ClassType { IsInstance: false } c ? c.Info : null;

    /// <summary>The first parameter of a method that is not static: self, or cls.</summary>
    static bool IsReceiver(ModuleInfo m, Node paramNode)
    {
        for (var p = m.ParentOf(paramNode); p != null; p = m.ParentOf(p))
            if (p is FunctionDef fd)
                return fd.Params.Count > 0 && fd.Params[0].Start == paramNode.Start && m.ParentOf(fd) is ClassDef && !fd.Decorators.Any(d => DecoratorName(d) == "staticmethod");
        return false;
    }

    /// <summary>What `x.attr` is: a method, a class, a module, an enum member, a property (a constant when its name is upper case).</summary>
    static (Tok Type, TokMod Mods)? AttributeKind(ModuleInfo m, TypeEvaluator ev, AttributeExpr a)
    {
        var owner = ev.TypeOf(m, a.Value);
        var t = ev.TypeOf(m, a);
        if (t is ModuleType) return (Tok.Namespace, TokMod.None);
        if (t is ClassType { IsInstance: false }) return (Tok.Class, TokMod.None);
        if (t is ClassType { EnumMember: not null }) return (Tok.EnumMember, TokMod.Readonly);
        if (t is FunctionType or OverloadedType)
            return (owner is ModuleType ? Tok.Function : Tok.Method, TokMod.None);
        if (owner is ClassType { IsInstance: false } oc && IsEnumClass(oc.Info, ev) && t is ClassType { IsInstance: true }) return (Tok.EnumMember, TokMod.Readonly);
        if (owner is UnknownType && t is UnknownType) return null;
        return (Tok.Property, ConstantName.IsMatch(a.Attr) ? TokMod.Readonly : TokMod.None);
    }
}
