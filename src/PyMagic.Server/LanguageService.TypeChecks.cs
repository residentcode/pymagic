using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// The checks a type checker makes beside calls: a returned or assigned value that is not what the annotation says, a function that can end without
/// returning what it declares, a loop over something that cannot be iterated, a subscript on something that has none, and an operator between builtin
/// types that do not support it. They report only what is certain: a value whose type is not known completely, an annotation with type variables and
/// a class with unknown bases are left alone.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> TypeCheckDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            IEnumerable<Finding> findings = node switch
            {
                FunctionDef f => MissingReturn(m, f, ev),
                ReturnStmt r => ReturnMismatch(m, r, ev),
                AnnAssignStmt a => AnnotatedMismatch(m, a, ev),
                ForStmt f => NotIterable(m, f.Iter, ev),
                Comprehension c => NotIterable(m, c.Iter, ev),
                SubscriptExpr s => NotSubscriptable(m, s, ev),
                BinaryExpr b => UnsupportedOperand(m, b, ev),
                _ => Array.Empty<Finding>(),
            };
            foreach (var f in findings)
                if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }
    }

    // ------------------------------------------------------------ what is known well enough

    /// <summary>Nothing in the type is unknown, a type variable or a type of another kind that cannot be compared (a parameter specification, a callable's unknown parameters).</summary>
    static bool Certain(PyType t) => t switch
    {
        UnknownType or TypeVarType or ParamSpecType or TypeVarTupleType => false,
        UnionType u => u.MemberList.All(Certain),
        ClassType c => c.Args.All(Certain) && (c.TupleItems?.All(Certain) ?? true),
        _ => true,
    };

    static bool IsNone(PyType t) => t is ClassType { Info.Name: "NoneType" };

    static bool IsFresh(Expr e) => e is ConstExpr or ListExpr or DictExpr or SetExpr or TupleExpr or CompExpr or UnaryExpr { Operand: ConstExpr };

    static FunctionDef? EnclosingFunction(ModuleInfo m, Node n)
    {
        for (var p = m.ParentOf(n); p != null; p = m.ParentOf(p))
            if (p is FunctionDef f) return f;
            else if (p is LambdaExpr or ClassDef) return null;
        return null;
    }

    static bool HasYield(IEnumerable<Stmt> body)
    {
        bool Walk(Node n)
        {
            if (n is YieldExpr) return true;
            if (n is FunctionDef or LambdaExpr or ClassDef) return false;
            return AstChildren.Of(n).Any(Walk);
        }
        return body.Any(Walk);
    }

    static bool Decorated(FunctionDef f, params string[] names) =>
        f.Decorators.Any(d => (d is CallExpr c ? c.Func : d) is NameExpr { Name: var n } && names.Contains(n) || (d is CallExpr c2 ? c2.Func : d) is AttributeExpr { Attr: var a } && names.Contains(a));

    // ------------------------------------------------------------ which values are judged

    static bool SimpleAnnotation(Expr ann) => ann switch
    {
        NameExpr or AttributeExpr => true,
        SubscriptExpr { Value: NameExpr { Name: "list" or "dict" or "set" or "tuple" or "frozenset" or "List" or "Dict" or "Set" or "Tuple" or "FrozenSet" } } => true,
        _ => false,
    };

    /// <summary>
    /// The type of an expression when it can be trusted as much as a reader would: a literal, or a name that is a parameter with a plain annotation or is
    /// assigned once, from a literal. The type engine's answer for a call, a union or a longer expression is not used: an operator or a decorator it
    /// understands only in part would turn into a report here.
    /// </summary>
    static PyType? Reliable(ModuleInfo m, Expr e, TypeEvaluator ev, int depth = 0)
    {
        switch (e)
        {
            case ConstExpr or ListExpr or TupleExpr or SetExpr or DictExpr or UnaryExpr { Operand: ConstExpr }:
                return ev.TypeOf(m, e) is { } t && Certain(t) ? t : null;
            case NameExpr n when depth == 0:
            {
                if (m.Semantics.Resolve(n) is not { } sym) return null;
                var decls = (sym.Redirect ?? sym).Declarations;
                if (decls.Count != 1) return null;
                switch (decls[0].Node)
                {
                    case Parameter { Kind: ParamKind.Normal, Annotation: { } ann } p when SimpleAnnotation(ann) && p.Default is not ConstExpr { Kind: ConstKind.None }:
                        return ev.AnnotatedType(m, ann) is ClassType { IsInstance: true } declared && Certain(declared) ? declared : null;
                    case NameExpr target when m.ParentOf(target) is AssignStmt { Targets.Count: 1 } a && ReferenceEquals(a.Targets[0], target):
                        return Reliable(m, a.Value, ev, 1);
                    case NameExpr target2 when m.ParentOf(target2) is AnnAssignStmt aa && ReferenceEquals(aa.Target, target2) && SimpleAnnotation(aa.Annotation):
                        return ev.AnnotatedType(m, aa.Annotation) is ClassType { IsInstance: true } declared2 && Certain(declared2) ? declared2 : null;
                    default: return null;
                }
            }
            default: return null;
        }
    }

    // ------------------------------------------------------------ return and assignment

    IEnumerable<Finding> ReturnMismatch(ModuleInfo m, ReturnStmt r, TypeEvaluator ev)
    {
        if (r.Value == null || EnclosingFunction(m, r) is not { Returns: { } ann } f) yield break;
        if (HasYield(f.Body) || Decorated(f, "overload")) yield break;
        var declared = ev.AnnotatedType(m, ann);
        if (!Certain(declared) || declared.Members.Any(x => x is ClassType { Info: { Name: "object", Module.Name: "builtins" } })) yield break;
        if (Reliable(m, r.Value, ev) is not { } actual) yield break;
        if (ev.IsAssignable(declared, actual, IsFresh(r.Value))) yield break;
        yield return new Finding(r.Value.Start, Math.Max(r.Value.End - r.Value.Start, 1), $"Expected type '{declared}', got '{actual}' instead", "return-type", FindingLevel.Warning);
    }

    IEnumerable<Finding> AnnotatedMismatch(ModuleInfo m, AnnAssignStmt a, TypeEvaluator ev)
    {
        if (a.Value == null || !IsFresh(a.Value)) yield break;
        string? head = (a.Annotation is SubscriptExpr s ? s.Value : a.Annotation) switch { NameExpr n => n.Name, AttributeExpr at => at.Attr, _ => null };
        if (head is "ClassVar" or "Final" or "Annotated" or "InitVar" or "TypeAlias" or "Required" or "NotRequired" or "ReadOnly" or "Unpack") yield break;
        var declared = ev.AnnotatedType(m, a.Annotation);
        if (!Certain(declared) || declared.Members.Any(x => x is ClassType { Info: { Name: "object", Module.Name: "builtins" } })) yield break;
        if (Reliable(m, a.Value, ev) is not { } actual || ev.IsAssignable(declared, actual, true)) yield break;
        // `x: Thing = None` is a habit of dataclasses and of models: noted, not warned
        yield return new Finding(a.Value.Start, Math.Max(a.Value.End - a.Value.Start, 1), $"Expected type '{declared}', got '{actual}' instead", "assignment-type", IsNone(actual) ? FindingLevel.Weak : FindingLevel.Warning);
    }

    // ------------------------------------------------------------ a function that can end without returning

    IEnumerable<Finding> MissingReturn(ModuleInfo m, FunctionDef f, TypeEvaluator ev)
    {
        if (f.Returns == null || f.Body.Count == 0 || HasYield(f.Body) || Decorated(f, "overload", "abstractmethod", "abstractproperty", "abstractclassmethod", "abstractstaticmethod")) yield break;
        if (IsStubBody(f.Body)) yield break;
        var declared = ev.AnnotatedType(m, f.Returns);
        if (!Certain(declared) || declared is NeverType || declared.Members.Any(x => IsNone(x) || x is NeverType || x is ClassType { Info: { Name: "object", Module.Name: "builtins" } })) yield break;
        // a Protocol's methods are signatures
        if (m.ParentOf(f) is ClassDef cd && ClassInfoOf(m, cd, ev) is { } info && ev.IsProtocol(info)) yield break;
        if (AlwaysExits(m, f.Body, ev)) yield break;
        yield return new Finding(f.NameStart, f.Name.Length, $"Missing return statement: '{f.Name}' is declared to return '{declared}' but can end without returning", "missing-return", FindingLevel.Warning);
    }

    /// <summary>A body that only says what the function is: a docstring, `...`, `pass`, or `raise NotImplementedError`.</summary>
    static bool IsStubBody(IReadOnlyList<Stmt> body) => body.All(s =>
        s is PassStmt
        || s is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str or ConstKind.Ellipsis } }
        || s is RaiseStmt { Exc: NameExpr { Name: "NotImplementedError" } or CallExpr { Func: NameExpr { Name: "NotImplementedError" } } });

    static bool AlwaysExits(ModuleInfo m, IReadOnlyList<Stmt> body, TypeEvaluator ev) => body.Any(s => Exits(m, s, ev));

    static bool Exits(ModuleInfo m, Stmt s, TypeEvaluator ev)
    {
        switch (s)
        {
            case ReturnStmt or RaiseStmt: return true;
            case AssertStmt { Test: ConstExpr { Kind: ConstKind.False } }: return true;
            case AssertStmt { Test: ConstExpr { Kind: ConstKind.Int, Text: "0" } }: return true;
            case IfStmt i: return i.Orelse.Count > 0 && AlwaysExits(m, i.Body, ev) && AlwaysExits(m, i.Orelse, ev);
            case WhileStmt { Test: ConstExpr { Kind: ConstKind.True } or ConstExpr { Kind: ConstKind.Int, Text: not "0" } } w: return !ContainsBreak(w.Body);
            case WithStmt w: return AlwaysExits(m, w.Body, ev);
            case TryStmt t:
                if (t.Finally.Count > 0 && AlwaysExits(m, t.Finally, ev)) return true;
                return (AlwaysExits(m, t.Body, ev) || (t.Orelse.Count > 0 && AlwaysExits(m, t.Orelse, ev))) && t.Handlers.All(h => AlwaysExits(m, h.Body, ev));
            case MatchStmt ms:
                // exhaustive only through a last case that takes everything
                return ms.Cases.Count > 0 && ms.Cases[^1] is { Guard: null, Pattern: MatchCapture } && ms.Cases.All(c => AlwaysExits(m, c.Body, ev));
            case ExprStmt { Value: CallExpr call }:
                return ev.TypeOf(m, call) is NeverType || call.Func is NameExpr { Name: "exit" or "quit" or "abort" } || call.Func is AttributeExpr { Attr: "exit" or "_exit" or "abort", Value: NameExpr { Name: "sys" or "os" or "flask" } };
            default: return false;
        }
    }

    static bool ContainsBreak(IEnumerable<Stmt> body)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case BreakStmt: return true;
                case IfStmt i when ContainsBreak(i.Body) || ContainsBreak(i.Orelse): return true;
                case TryStmt t when ContainsBreak(t.Body) || t.Handlers.Any(h => ContainsBreak(h.Body)) || ContainsBreak(t.Orelse) || ContainsBreak(t.Finally): return true;
                case WithStmt w when ContainsBreak(w.Body): return true;
                case MatchStmt ms when ms.Cases.Any(c => ContainsBreak(c.Body)): return true;
                // a break of an inner loop belongs to it (its else clause belongs to this one)
                case WhileStmt w when ContainsBreak(w.Orelse): return true;
                case ForStmt f when ContainsBreak(f.Orelse): return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------ iteration, subscripts, operators

    /// <summary>The type when it is one instance of one class whose methods are all known.</summary>
    static ClassType? PlainInstance(PyType? t) => t is ClassType { IsInstance: true } c && Certain(c) ? c : null;

    static string NameOf(ClassType c) => c.Info.Name;

    IEnumerable<Finding> NotIterable(ModuleInfo m, Expr iter, TypeEvaluator ev)
    {
        if (PlainInstance(Reliable(m, iter, ev)) is not { } c) yield break;
        if (ev.AttributeType(c, "__iter__").Missing.Count == 0 || ev.AttributeType(c, "__getitem__").Missing.Count == 0) yield break;
        if (ev.AttributeType(c, "__iter__").Declarations.Count > 0) yield break;
        yield return new Finding(iter.Start, Math.Max(iter.End - iter.Start, 1), $"'{NameOf(c)}' object is not iterable", "not-iterable", FindingLevel.Warning);
    }

    IEnumerable<Finding> NotSubscriptable(ModuleInfo m, SubscriptExpr s, TypeEvaluator ev)
    {
        // only a read: the parent that stores to it, deletes it or iterates into it is another question
        var parent = m.ParentOf(s);
        if (parent is AssignStmt a && a.Targets.Any(t => ReferenceEquals(t, s)) || parent is AugAssignStmt or DelStmt || parent is AnnAssignStmt) yield break;
        if (PlainInstance(Reliable(m, s.Value, ev)) is not { } c) yield break;
        if (ev.AttributeType(c, "__getitem__").Missing.Count == 0) yield break;
        yield return new Finding(s.Value.Start, Math.Max(s.Value.End - s.Value.Start, 1), $"'{NameOf(c)}' object is not subscriptable", "not-subscriptable", FindingLevel.Warning);
    }

    static readonly HashSet<string> Numbers = new() { "int", "bool", "float", "complex" };
    static readonly HashSet<string> Whole = new() { "int", "bool" };
    static readonly HashSet<string> Known = new() { "int", "bool", "float", "complex", "str", "bytes", "list", "tuple", "dict", "set", "frozenset", "NoneType" };

    static bool Supported(string op, string l, string r)
    {
        if (Numbers.Contains(l) && Numbers.Contains(r))
            return op switch
            {
                "+" or "-" or "*" or "/" or "**" => true,
                "//" or "%" => l != "complex" && r != "complex",
                "&" or "|" or "^" or "<<" or ">>" => Whole.Contains(l) && Whole.Contains(r),
                _ => false,
            };
        if (op == "%" && l is "str" or "bytes") return true;                                  // formatting takes anything
        if (op == "+") return l == r && l is "str" or "bytes" or "list" or "tuple";
        if (op == "*") return (l is "str" or "bytes" or "list" or "tuple" && Whole.Contains(r)) || (Whole.Contains(l) && r is "str" or "bytes" or "list" or "tuple");
        if (op == "|") return (l == "dict" && r == "dict") || (l is "set" or "frozenset" && r is "set" or "frozenset");
        if (op is "&" or "^" or "-") return l is "set" or "frozenset" && r is "set" or "frozenset";
        return false;
    }

    IEnumerable<Finding> UnsupportedOperand(ModuleInfo m, BinaryExpr b, TypeEvaluator ev)
    {
        if (b.Op is not ("+" or "-" or "*" or "/" or "//" or "%" or "**" or "&" or "|" or "^" or "<<" or ">>")) yield break;
        if (PlainInstance(Reliable(m, b.Left, ev)) is not { } l || PlainInstance(Reliable(m, b.Right, ev)) is not { } r) yield break;
        // only the builtin types themselves (a subclass may define its own operators)
        bool Builtin(ClassType c) => Known.Contains(c.Info.Name) && (c.Info.Name == "NoneType" || c.Info.Module.Name == "builtins");
        if (!Builtin(l) || !Builtin(r) || Supported(b.Op, NameOf(l), NameOf(r))) yield break;
        yield return new Finding(b.Start, Math.Max(b.End - b.Start, 1), $"Unsupported operand type(s) for {b.Op}: '{NameOf(l)}' and '{NameOf(r)}'", "unsupported-operand", FindingLevel.Warning);
    }
}
