using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>The inspections of PyCharm that need types: protected members, tuple items, functions that return nothing, exceptions and read-only properties.</summary>
    void InspectTyped(ModuleInfo m, Node node, List<Diagnostic> diags)
    {
        switch (node)
        {
            case AttributeExpr attr:
                ProtectedMember(m, attr, diags);
                break;
            case AssignStmt assign:
                foreach (var target in assign.Targets)
                {
                    if (target is SubscriptExpr sub && TypeOf(m, sub.Value) is ClassType { IsInstance: true, Info: { Name: "tuple", Module.Name: "builtins" } })
                        diags.Add(new Diagnostic(sub.Start, Math.Max(sub.End - sub.Start, 1), "'tuple' object does not support item assignment"));
                    if (target is AttributeExpr pa) { ReadOnlyProperty(m, pa, diags); FrozenAssignment(m, pa, diags); }
                    FinalReassignment(m, target, diags);
                }
                if (assign.Targets is [NameExpr] && assign.Value is CallExpr call) NoneFunction(m, call, diags);
                break;
            case AugAssignStmt aug:
                if (aug.Target is AttributeExpr augTarget) FrozenAssignment(m, augTarget, diags);
                FinalReassignment(m, aug.Target, diags);
                break;
            case FunctionDef fd:
                MethodOverride(m, fd, diags);
                break;
            case TryStmt t:
                ExceptOrder(m, t, diags);
                break;
            case RaiseStmt { Exc: { } exc }:
                NotAnException(m, exc, "Exceptions must derive from BaseException", diags);
                break;
        }
    }

    // ------------------------------------------------------------ protected members

    void ProtectedMember(ModuleInfo m, AttributeExpr attr, List<Diagnostic> diags)
    {
        string name = attr.Attr;
        if (name.Length < 2 || name[0] != '_' || name[1] == '_') return;
        if (attr.Value is NameExpr { Name: "self" or "cls" or "mcs" or "mcls" or "metacls" } || attr.Value is CallExpr { Func: NameExpr { Name: "super" } }) return;
        var recv = TypeOf(m, attr.Value);
        string kind;
        ClassInfo? owner = null;
        switch (recv)
        {
            case ClassType { Info.Module.IsStub: false } c: kind = "class"; owner = c.Info; break;
            case ModuleType { Module.IsStub: false } mt when !ReferenceEquals(mt.Module, m): kind = "module"; break;
            default: return;
        }
        // a class may use the protected members of its own bases
        if (owner != null)
        {
            for (var p = m.ParentOf(attr); p != null; p = m.ParentOf(p))
                if (p is ClassDef cd && m.Semantics.ScopeOf(cd)?.Parent?.Symbols.GetValueOrDefault(cd.Name) is { } sym && TypeOfSymbol(sym) is ClassType { IsInstance: false } enclosing
                    && (ReferenceEquals(enclosing.Info, owner) || IsSubclass(enclosing.Info, owner) || IsSubclass(owner, enclosing.Info)))
                    return;
            if (AttributeType(recv, name).Missing.Count > 0) return;      // not a member at all: reported as a missing one
        }
        diags.Add(new Diagnostic(attr.End - name.Length, name.Length, $"Access to a protected member {name} of a {kind}"));
    }

    // ------------------------------------------------------------ frozen dataclasses and attrs classes

    /// <summary>`p.x = 1` on an instance of a frozen dataclass or `@attrs.frozen` class raises at run time.</summary>
    void FrozenAssignment(ModuleInfo m, AttributeExpr target, List<Diagnostic> diags)
    {
        if (TypeOf(m, target.Value) is not ClassType { IsInstance: true, Info.Module.IsStub: false } recv) return;
        foreach (var entry in Mro(recv.Info))
        {
            if (entry is not ClassType e) return;
            if (DataclassOf(e.Info) is { Frozen: true })
            {
                diags.Add(new Diagnostic(target.End - target.Attr.Length, target.Attr.Length, $"Cannot assign to attribute '{target.Attr}': '{recv.Info.Name}' is frozen"));
                return;
            }
        }
    }

    // ------------------------------------------------------------ overriding methods

    static readonly HashSet<string> PlainDecorators = new() { "staticmethod", "classmethod", "abstractmethod" };

    static bool IsPlainMethod(FunctionDef f) =>
        !f.IsAsync && f.Decorators.All(d => d is NameExpr { Name: var n } && PlainDecorators.Contains(n) || d is AttributeExpr { Attr: "abstractmethod" });

    /// <summary>The parameters a caller can fill, without self/cls: positional ones with their defaults, and the shape of the rest.</summary>
    static (List<Parameter> Positional, List<Parameter> KeywordOnly, bool VarArgs, bool KwArgs) Shape(FunctionDef f, bool bound)
    {
        var pos = new List<Parameter>();
        var kw = new List<Parameter>();
        bool var = false, kwargs = false, afterStar = false;
        foreach (var p in f.Params)
        {
            switch (p.Kind)
            {
                case ParamKind.VarArgs: var = true; afterStar = true; break;
                case ParamKind.KwOnlySeparator: afterStar = true; break;
                case ParamKind.KwArgs: kwargs = true; break;
                case ParamKind.Slash: break;
                default: (afterStar ? kw : pos).Add(p); break;
            }
        }
        if (bound && pos.Count > 0) pos.RemoveAt(0);
        return (pos, kw, var, kwargs);
    }

    /// <summary>A method that overrides one of a user base class with a signature callers of the base would not fit.</summary>
    void MethodOverride(ModuleInfo m, FunctionDef f, List<Diagnostic> diags)
    {
        string name = f.Name;
        if (name.StartsWith("__")) return;                          // dunder methods and private names
        var info = FunctionOf(m, f);
        if (info.Owner == null) return;
        if (!IsPlainMethod(f)) return;
        foreach (var entry in Mro(info.Owner).Skip(1))
        {
            if (entry is not ClassType e) return;
            if (e.Info.Module.IsStub || !e.Info.Scope.Symbols.TryGetValue(name, out var sym)) continue;
            if (sym.Declarations.Count != 1 || sym.Declarations[0] is not { Kind: DeclKind.Function, Node: FunctionDef b } || !IsPlainMethod(b)) return;
            var baseInfo = FunctionOf(_ws.ModuleOf(sym) ?? m, b);
            if (baseInfo.IsStatic != info.IsStatic || baseInfo.IsClassMethod != info.IsClassMethod) return;
            var (dp, dk, dVar, dKw) = Shape(f, !info.IsStatic);
            var (bp, bk, bVar, bKw) = Shape(b, !baseInfo.IsStatic);
            bool bad = false;
            if (!dVar && dp.Count < bp.Count && !bVar) bad = true;
            if (!bad && !bVar && dp.Count(p => p.Default == null) > bp.Count) bad = true;
            if (!bad && bp.Count > 0) bad = dp.Zip(bp, (x, y) => x.Name != y.Name).Any(x => x) && !dVar;
            if (!bad && !dKw && bk.Any(k => !dk.Any(x => x.Name == k.Name) && !dp.Any(x => x.Name == k.Name))) bad = true;
            if (!bad && dk.Any(k => k.Default == null && !bk.Any(x => x.Name == k.Name)) && !bKw) bad = true;
            if (bad)
                diags.Add(new Diagnostic(f.NameStart, name.Length, $"Signature of method '{info.Owner.Name}.{name}()' does not match signature of the base method in class '{e.Info.Name}'"));
            return;
        }
    }

    // ------------------------------------------------------------ Final names

    static bool IsFinalAnnotation(Expr a) =>
        a is NameExpr { Name: "Final" } or AttributeExpr { Attr: "Final" }
        || a is SubscriptExpr { Value: NameExpr { Name: "Final" } or AttributeExpr { Attr: "Final" } };

    /// <summary>The declaration of `X: Final = value` among the declarations of a symbol, when it has a value.</summary>
    AnnAssignStmt? FinalDeclaration(ModuleInfo m, Symbol sym)
    {
        foreach (var d in sym.Declarations)
            if (d.Kind == DeclKind.Variable && m.ParentOf(d.Node) is AnnAssignStmt { Value: not null } a && ReferenceEquals(a.Target, d.Node) && IsFinalAnnotation(a.Annotation))
                return a;
        return null;
    }

    /// <summary>`MAX: Final = 3` followed by `MAX = 4`, or `Cls.MAX = 4` / `self.MAX = 4` for a Final class attribute.</summary>
    void FinalReassignment(ModuleInfo m, Expr target, List<Diagnostic> diags)
    {
        switch (target)
        {
            case NameExpr n:
            {
                var sym = m.Semantics.ReferenceOf(n)?.Symbol;
                if (sym == null || FinalDeclaration(m, sym) is not { } decl || ReferenceEquals(decl.Target, n)) return;
                diags.Add(new Diagnostic(n.Start, Math.Max(n.End - n.Start, 1), $"Cannot assign to final name '{n.Name}'"));
                return;
            }
            case AttributeExpr a:
            {
                if (TypeOf(m, a.Value) is not ClassType recv || recv.Info.Module.IsStub) return;
                foreach (var entry in Mro(recv.Info))
                {
                    if (entry is not ClassType e) return;
                    if (e.Info.Module.IsStub || !e.Info.Scope.Symbols.TryGetValue(a.Attr, out var sym)) continue;
                    if (FinalDeclaration(_ws.ModuleOf(sym) ?? m, sym) != null)
                        diags.Add(new Diagnostic(a.End - a.Attr.Length, a.Attr.Length, $"Cannot assign to final attribute '{a.Attr}'"));
                    return;
                }
                return;
            }
        }
    }

    // ------------------------------------------------------------ properties without a setter

    void ReadOnlyProperty(ModuleInfo m, AttributeExpr target, List<Diagnostic> diags)
    {
        if (TypeOf(m, target.Value) is not ClassType { IsInstance: true, Info.Module.IsStub: false } recv) return;
        if (target.Value is NameExpr { Name: "self" } && IsInitLike(m, target)) { }
        foreach (var entry in Mro(recv.Info))
        {
            if (entry is not ClassType e || e.Info.Module.IsStub) { if (entry is not ClassType) return; continue; }
            if (!e.Info.Scope.Symbols.TryGetValue(target.Attr, out var sym) || sym.Declarations.Count == 0) continue;
            var defs = sym.Declarations.Where(d => d.Kind == DeclKind.Function).Select(d => d.Node).OfType<FunctionDef>().ToList();
            if (defs.Count != sym.Declarations.Count) return;      // something else is also bound to the name
            bool property = defs.Any(f => f.Decorators.Any(d => d is NameExpr { Name: "property" } or AttributeExpr { Attr: "cached_property" or "property" }));
            bool setter = defs.Any(f => f.Decorators.Any(d => d is AttributeExpr { Attr: "setter" or "deleter" }));
            if (property && !setter && defs.Any(f => f.Decorators.Any(d => d is NameExpr { Name: "property" })))
                diags.Add(new Diagnostic(target.End - target.Attr.Length, target.Attr.Length, $"Property '{target.Attr}' cannot be set"));
            return;
        }
    }

    static bool IsInitLike(ModuleInfo m, Node n) => false;

    // ------------------------------------------------------------ functions that return nothing

    void NoneFunction(ModuleInfo m, CallExpr call, List<Diagnostic> diags)
    {
        if (TypeOf(m, call.Func) is not FunctionType { Info: { Node: FunctionDef def } info } f || info.Module.IsStub) return;
        if (def.IsAsync || def.Decorators.Count > 0 || (info.Flags & FuncFlags.Generator) != 0 || def.Returns != null) return;
        if (info.Owner != null && info.Owner.Node.Bases.Any(b => b.Name == null && b.Stars == 0 && !(b.Value is NameExpr { Name: "object" }))) return;   // a subclass may return something
        if (def.Body.All(s => s is PassStmt || s is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str or ConstKind.Ellipsis } } || s is RaiseStmt)) return;   // a stub or an abstract method
        var shallow = ShallowOf(def.Body).ToList();
        if (shallow.OfType<ReturnStmt>().Any(r => r.Value != null && !(r.Value is ConstExpr { Kind: ConstKind.None }))) return;
        if (shallow.Any(n => n is YieldExpr)) return;
        diags.Add(new Diagnostic(call.Start, Math.Max(call.End - call.Start, 1), $"Function '{def.Name}' doesn't return anything"));
    }

    static IEnumerable<Node> ShallowOf(IEnumerable<Stmt> body)
    {
        IEnumerable<Node> Walk(Node n)
        {
            yield return n;
            if (n is FunctionDef or ClassDef or LambdaExpr) yield break;
            foreach (var child in AstChildren.Of(n))
                foreach (var x in Walk(child)) yield return x;
        }
        foreach (var s in body)
            foreach (var n in Walk(s)) yield return n;
    }

    // ------------------------------------------------------------ exceptions

    /// <summary>`except Base:` before `except Derived:` - the second clause can never run.</summary>
    void ExceptOrder(ModuleInfo m, TryStmt t, List<Diagnostic> diags)
    {
        var caught = new List<ClassInfo>();
        foreach (var h in t.Handlers)
        {
            if (h.Type == null) continue;
            var exprs = h.Type is TupleExpr tup ? tup.Items : new[] { h.Type };
            var classes = new List<ClassInfo>();
            foreach (var e in exprs)
            {
                if (TypeOf(m, e) is ClassType { IsInstance: false } ct) classes.Add(ct.Info);
                NotAnException(m, e, "Exceptions must derive from BaseException", diags, classObject: true);
            }
            foreach (var cls in classes)
            {
                var earlier = caught.FirstOrDefault(prior => ReferenceEquals(prior, cls) || IsSubclass(cls, prior));
                if (earlier != null)
                    diags.Add(new Diagnostic(h.Type.Start, Math.Max(h.Type.End - h.Type.Start, 1), $"Exception class '{cls.Name}' is already handled by an earlier clause for '{earlier.Name}'"));
            }
            caught.AddRange(classes);
        }
    }

    /// <summary>`raise 5`, `raise NotAnError`, `except int:`: only what derives from BaseException can be raised or caught.</summary>
    void NotAnException(ModuleInfo m, Expr e, string message, List<Diagnostic> diags, bool classObject = false)
    {
        var t = TypeOf(m, e);
        if (t is not ClassType c) return;
        if (classObject && c.IsInstance) return;
        var mro = Mro(c.Info);
        if (mro.Any(x => x is not ClassType)) return;         // an unknown base may be an exception
        if (c.Info.Module.IsStub && c.Info.Module.Name != "builtins") return;
        if (mro.Any(x => x is ClassType xc && xc.Info.QualifiedName == "builtins.BaseException")) return;
        // a class the bundled minimal stubs do not describe fully is not judged
        if (mro.Any(x => x is ClassType xc && xc.Info.Module.IsBundledStub && xc.Info.Name == "object") && c.Info.Module.IsBundledStub) return;
        diags.Add(new Diagnostic(e.Start, Math.Max(e.End - e.Start, 1), message));
    }
}
