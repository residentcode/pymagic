using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// PyCharm's inspections that need types: assert_type that does not hold, a cast between unrelated types or to the type the value
/// already has, NewType, ClassVar and Final where they cannot be, @final classes and methods overridden, a type alias declared twice,
/// and str() of an object whose class has no __str__ or __repr__.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> PyCharmTypeDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var f in TypeFindings(m, ev))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    static PyType Widened(PyType t) => t is ClassType { LiteralValue: not null } c ? c with { LiteralValue = null } : t;

    static bool KnownType(PyType t) => t is not UnknownType && !(t is ClassType { Info.Name: "Any" });

    static string? TypingName(Expr e) => e switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };

    IEnumerable<Finding> TypeFindings(ModuleInfo m, TypeEvaluator ev)
    {
        Finding At(Node n, string message, string code, FindingLevel level = FindingLevel.Warning) => new(n.Start, Math.Max(n.End - n.Start, 1), message, code, level);

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case CallExpr call when TypingName(call.Func) == "assert_type" && call.Args.Count == 2 && call.Args.All(a => a.Name == null):
                {
                    var actual = ev.TypeOf(m, call.Args[0].Value);
                    var expected = ev.AnnotationType(m, call.Args[1].Value);
                    // x = 1 is an int to assert_type as the type checkers see it, though the engine knows it is the literal 1
                    if (KnownType(actual) && KnownType(expected) && !SameType(ev, actual, expected) && !(expected is ClassType { LiteralValue: null } && SameType(ev, Widened(actual), expected)))
                        yield return At(call.Args[0].Value, $"Expected type '{expected.Display()}', got '{actual.Display()}' instead", "assert-type");
                    break;
                }
                case CallExpr call when TypingName(call.Func) == "cast" && call.Args.Count == 2 && call.Args.All(a => a.Name == null) && ev.TypeOf(m, call.Func) is FunctionType or OverloadedType:
                {
                    var target = ev.AnnotationType(m, call.Args[0].Value);
                    var value = ev.TypeOf(m, call.Args[1].Value);
                    if (!KnownType(target) || !KnownType(value)) break;
                    if (SameType(ev, Widened(value), target))
                        yield return At(call, $"Unnecessary cast: the value is already '{target.Display()}'", "unnecessary-cast", FindingLevel.Weak);
                    else if (!ev.IsAssignable(target, value) && !ev.IsAssignable(Widened(value), target) && Unrelated(target, Widened(value)))
                        yield return At(call, $"Cast of '{Widened(value).Display()}' to '{target.Display()}': the types are unrelated", "invalid-cast");
                    break;
                }
                case AssignStmt { Targets: [NameExpr name], Value: CallExpr nt } when TypingName(nt.Func) == "NewType" && nt.Args.Count == 2:
                {
                    if (Pytest.FixtureModel.StringValue(nt.Args[0].Value) is { } given && given != name.Name)
                        yield return At(nt.Args[0].Value, $"The name given to NewType ('{given}') is not the variable's ('{name.Name}')", "newtype");
                    var baseType = ev.AnnotationType(m, nt.Args[1].Value);
                    string? why = baseType switch
                    {
                        UnionType => "a union",
                        ClassType { LiteralValue: not null } => "a Literal",
                        ClassType { Info.Name: "Any" } => "Any",
                        ClassType c when ev.IsProtocol(c.Info) => "a protocol",
                        _ when TypingName(nt.Args[1].Value is SubscriptExpr s ? s.Value : nt.Args[1].Value) is "Literal" or "Union" or "Optional" or "Any" => "this type",
                        _ => null,
                    };
                    if (why != null) yield return At(nt.Args[1].Value, $"NewType cannot be based on {why}: it needs a class", "newtype");
                    break;
                }
                case ClassDef cd:
                    foreach (var b in cd.Bases.Where(b => b.Name == null))
                    {
                        if (b.Value is NameExpr bn && IsNewTypeName(m, bn))
                            yield return At(b.Value, $"'{bn.Name}' is a NewType: it cannot be subclassed", "newtype");
                        else if (ev.TypeOf(m, b.Value) is ClassType { IsInstance: false } bc && bc.Info.Node is ClassDef bd && bd.Decorators.Any(d => DecoratorName(d) == "final"))
                            yield return At(b.Value, $"'{bc.Info.Name}' is marked @final: it cannot be subclassed", "final-override");
                    }
                    foreach (var f in FinalOverrides(m, cd, ev)) yield return f;
                    break;
                case FunctionDef fd:
                    foreach (var p in fd.Params.Where(p => p.Annotation != null))
                        if (QualifierIn(p.Annotation!) is { } q) yield return At(p.Annotation!, $"{q} is not allowed in a parameter's annotation", q == "Final" ? "final-misuse" : "classvar");
                    if (fd.Returns != null && QualifierIn(fd.Returns) is { } rq)
                        yield return At(fd.Returns, $"{rq} is not allowed in a return annotation", rq == "Final" ? "final-misuse" : "classvar");
                    break;
                case AnnAssignStmt ann:
                {
                    bool inClass = m.ParentOf(ann) is ClassDef;
                    string? head = TypingName(ann.Annotation is SubscriptExpr hs ? hs.Value : ann.Annotation);
                    if (head == "ClassVar" && !inClass)
                        yield return At(ann.Annotation, "ClassVar is allowed only in a class body", "classvar");
                    else if (head == "ClassVar" && ann.Target is not NameExpr)
                        yield return At(ann.Annotation, "ClassVar is allowed only on a name declared in the class body, not on an attribute", "classvar");
                    if (head == "Final" && ann.Value == null && !inClass)
                        yield return At(ann.Target, $"'{(ann.Target as NameExpr)?.Name ?? "this"}' is Final: it needs a value where it is declared", "final-misuse");
                    // ClassVar[...] or Final[...] inside another type (list[ClassVar[int]])
                    if (head is not ("ClassVar" or "Final" or "Annotated") && QualifierIn(ann.Annotation) is { } nested)
                        yield return At(ann.Annotation, $"{nested} cannot be inside another type", nested == "Final" ? "final-misuse" : "classvar");
                    break;
                }
                case AssignStmt { Targets: [AttributeExpr { Value: NameExpr { Name: "self" } } sa] } when ClassVarOf(m, sa, ev) is { } owner:
                    yield return new Finding(sa.End - sa.Attr.Length, sa.Attr.Length, $"Cannot assign to class variable '{sa.Attr}' of {owner} through an instance", "classvar", FindingLevel.Warning);
                    break;
                case CallExpr call when call.Func is NameExpr { Name: "str" } && call.Args.Count == 1 && call.Args[0].Name == null && ev.TypeOf(m, call.Func) is ClassType { Info.Module.Name: "builtins" }:
                {
                    if (ev.TypeOf(m, call.Args[0].Value) is ClassType { IsInstance: true } c && !c.Info.Module.IsStub && WithoutDunderStr(c.Info, ev))
                        yield return At(call, $"str() of a '{c.Info.Name}' gives its default text (<{c.Info.Name} object at 0x...>): the class has no __str__ or __repr__", "str-without-dunder", FindingLevel.Weak);
                    break;
                }
            }
        }

        // a type alias declared twice in one scope
        foreach (var body in new[] { m.Ast.Body }.Concat(AstChildren.Descendants(m.Ast).OfType<ClassDef>().Select(c => c.Body)))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stmt in body)
            {
                (string Name, Node At)? alias = stmt switch
                {
                    TypeAliasStmt t => (t.Name, t),
                    AnnAssignStmt { Target: NameExpr n, Annotation: var a } when TypingName(a) == "TypeAlias" => (n.Name, n),
                    _ => null,
                };
                if (alias is { } x && !seen.Add(x.Name))
                    yield return x.At is TypeAliasStmt ts ? new Finding(ts.NameStart, ts.Name.Length, $"Type alias '{x.Name}' is declared again", "type-alias-redeclared", FindingLevel.Warning)
                        : At(x.At, $"Type alias '{x.Name}' is declared again", "type-alias-redeclared");
            }
        }
    }

    /// <summary>Equal types, or each assignable to the other (int | str and str | int).</summary>
    static bool SameType(TypeEvaluator ev, PyType a, PyType b) =>
        a.Equals(b) || a.Display() == b.Display() || ev.IsAssignable(a, b) && ev.IsAssignable(b, a) && a is not ClassType { Info.Name: "object" } && b is not ClassType { Info.Name: "object" };

    /// <summary>Two classes neither of which derives from the other, where a value of one cannot be the other (protocols and unions are not judged).</summary>
    static bool Unrelated(PyType a, PyType b) =>
        a is ClassType { IsInstance: true } ca && b is ClassType { IsInstance: true } cb && ca.Info.Name != "object" && cb.Info.Name != "object";

    /// <summary>The first ClassVar or Final anywhere in an annotation, or null.</summary>
    static string? QualifierIn(Expr annotation) =>
        new[] { annotation }.Concat(AstChildren.Descendants(annotation).OfType<Expr>())
            .Select(e => e is SubscriptExpr s ? TypingName(s.Value) : TypingName(e)).FirstOrDefault(n => n is "ClassVar" or "Final");

    static bool IsNewTypeName(ModuleInfo m, NameExpr name) =>
        m.Semantics.Resolve(name) is { } sym && sym.Declarations.Any(d => d.Node is NameExpr t && m.ParentOf(t) is AssignStmt { Value: CallExpr c } && TypingName(c.Func) == "NewType");

    /// <summary>`self.x = ...` in a method where x is declared `x: ClassVar[...]` by the class or a class it derives from: the class's name.</summary>
    static string? ClassVarOf(ModuleInfo m, AttributeExpr target, TypeEvaluator ev)
    {
        if (ev.TypeOf(m, target.Value) is not ClassType { IsInstance: true } self) return null;
        foreach (var entry in ev.MroOf(self.Info))
        {
            if (entry is not ClassType e || e.Info.Node is not ClassDef cd) continue;
            var decl = cd.Body.OfType<AnnAssignStmt>().FirstOrDefault(a => a.Target is NameExpr n && n.Name == target.Attr);
            if (decl == null) continue;
            return TypingName(decl.Annotation is SubscriptExpr s ? s.Value : decl.Annotation) == "ClassVar" ? e.Info.Name : null;
        }
        return null;
    }

    /// <summary>A method of this class that overrides an @final one, or a name that redeclares a Final attribute of a base class.</summary>
    IEnumerable<Finding> FinalOverrides(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        if (ClassInfoOf(m, cd, ev) is not { } info) yield break;
        var bases = ev.MroOf(info).Skip(1).OfType<ClassType>().Where(b => b.Info.Node is ClassDef).ToList();
        foreach (var stmt in cd.Body)
        {
            (string Name, int Start)? own = stmt switch
            {
                FunctionDef f => (f.Name, f.NameStart),
                AnnAssignStmt { Target: NameExpr n } => (n.Name, n.Start),
                AssignStmt { Targets: [NameExpr n] } => (n.Name, n.Start),
                _ => null,
            };
            if (own is not { } o) continue;
            foreach (var b in bases)
            {
                var bd = (ClassDef)b.Info.Node;
                if (bd.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == o.Name) is { } method)
                {
                    if (method.Decorators.Any(d => DecoratorName(d) == "final"))
                        yield return new Finding(o.Start, o.Name.Length, $"'{b.Info.Name}.{o.Name}' is marked @final: it cannot be overridden", "final-override", FindingLevel.Warning);
                    break;
                }
                if (bd.Body.OfType<AnnAssignStmt>().FirstOrDefault(a => a.Target is NameExpr n && n.Name == o.Name) is { } attr)
                {
                    if (TypingName(attr.Annotation is SubscriptExpr s ? s.Value : attr.Annotation) == "Final")
                        yield return new Finding(o.Start, o.Name.Length, $"'{b.Info.Name}.{o.Name}' is Final: it cannot be overridden", "final-override", FindingLevel.Warning);
                    break;
                }
            }
        }
    }

    /// <summary>A class of the project none of whose classes but object defines __str__ or __repr__.</summary>
    static bool WithoutDunderStr(ClassInfo c, TypeEvaluator ev)
    {
        foreach (var entry in ev.MroOf(c))
        {
            if (entry is not ClassType e) return false;
            if (e.Info.Name == "object" && e.Info.Module.Name == "builtins") continue;
            if (e.Info.Module.IsStub) return false;   // a library class: its text is its own business
            if (e.Info.Scope.Symbols.ContainsKey("__str__") || e.Info.Scope.Symbols.ContainsKey("__repr__")) return false;
            if (e.Info.Node is ClassDef d && d.Decorators.Count > 0) return false;   // dataclasses and attrs write __repr__
        }
        return true;
    }

    void PyCharmTypeFix(ModuleInfo m, string code, int start, int end, Action<string, bool, Edit[]> add)
    {
        if (code != "unnecessary-cast") return;
        var call = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => c.Start == start && c.Args.Count == 2);
        if (call == null) return;
        var value = call.Args[1].Value;
        add("Remove the cast", true, new[] { new Edit(call.Start, call.End, m.Source[value.Start..value.End]) });
    }
}
