using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>Converts an annotation expression to the type it denotes (class objects become instances, etc.).</summary>
    public PyType AnnotationType(ModuleInfo m, Expr ann) => Annotation(m, ann, null);

    /// <param name="lookup">
    /// Scope for resolving bare names. Used for string annotations, whose parsed fragment has no semantic references;
    /// null for ordinary expressions that the binder already resolved.
    /// </param>
    PyType Annotation(ModuleInfo m, Expr e, Scope? lookup)
    {
        switch (e)
        {
            case ConstExpr { Kind: ConstKind.None }: return NoneInstance;
            case ConstExpr { Kind: ConstKind.Str, IsFString: false } s:
                return ForwardReference(m, s);
            case NameExpr or AttributeExpr:
                return ToAnnotation(ValueOf(m, e, lookup));
            case SubscriptExpr sub:
                return AnnotationSubscript(m, sub, lookup);
            case BinaryExpr { Op: "|" } b:
                return PyType.Union(Annotation(m, b.Left, lookup), Annotation(m, b.Right, lookup));
            default:
                return PyType.Unknown;
        }
    }

    /// <summary>Evaluates a name/attribute chain used inside an annotation as a value.</summary>
    PyType ValueOf(ModuleInfo m, Expr e, Scope? lookup)
    {
        switch (e)
        {
            case NameExpr n when lookup == null: return TypeOf(m, n);
            case NameExpr n:
            {
                var sym = lookup!.Lookup(n.Name);
                return sym != null ? TypeOfSymbol(sym) : BuiltinNameType(n.Name);
            }
            case AttributeExpr a: return AttributeType(ValueOf(m, a.Value, lookup), a.Attr).Type;
            default: return PyType.Unknown;
        }
    }

    PyType ForwardReference(ModuleInfo m, ConstExpr s)
    {
        string text = s.Text.Trim();
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return PyType.Unknown;
        char quote = text[q];
        int run = text.Skip(q).TakeWhile(c => c == quote).Count();
        int qlen = run >= 6 ? 3 : 1;
        if (text.Length < q + 2 * qlen) return PyType.Unknown;
        string inner = text.Substring(q + qlen, text.Length - q - 2 * qlen).Trim();
        if (inner.Length == 0) return PyType.Unknown;

        var parsed = Parser.Parse(inner);
        if (parsed.Diagnostics.Count > 0 || parsed.Module.Body.Count != 1 || parsed.Module.Body[0] is not ExprStmt es)
            return PyType.Unknown;
        var scope = m.Semantics.InnermostScopeAt(s.Start);
        return Annotation(m, es.Value, scope);
    }

    /// <summary>A value found in an annotation position -> the type of things it describes.</summary>
    PyType ToAnnotation(PyType v)
    {
        return MapMembers(v, t =>
        {
            switch (t)
            {
                case ClassType { IsInstance: false } c: return c.AsInstance();
                case TypeVarType: return t;
                case SpecialFormType f:
                    return f.Name switch
                    {
                        "Any" => PyType.Any,
                        "NoReturn" or "Never" => PyType.Never,
                        "Self" => new TypeVarType("Self"),
                        "LiteralString" => Inst("str"),
                        "List" => Inst("list"),
                        "Dict" => Inst("dict"),
                        "Set" => Inst("set"),
                        "FrozenSet" => Inst("frozenset"),
                        "Tuple" => Inst("tuple"),
                        "Type" => Inst("type"),
                        "Callable" => new SignatureType(null, PyType.Unknown),
                        _ => PyType.Unknown,
                    };
                case ClassType { IsInstance: true } inst when IsNone(inst): return inst;
                default: return PyType.Unknown;
            }
        });
    }

    /// <summary>The annotation form back to a value: instances of classes become the class object (for `Alias = list[int]`).</summary>
    PyType AnnotationToValue(PyType ann) => MapMembers(ann, t => t is ClassType { IsInstance: true } c ? c.AsClassObject() : t);

    PyType AnnotationSubscript(ModuleInfo m, SubscriptExpr sub, Scope? lookup)
    {
        var baseValue = ValueOf(m, sub.Value, lookup);
        IReadOnlyList<Expr> args = sub.Index is TupleExpr { Parenthesized: false } t ? t.Items : new[] { sub.Index };

        return MapMembers(baseValue, b =>
        {
            switch (b)
            {
                case SpecialFormType f: return SpecialFormSubscript(m, f.Name, args, lookup);
                case ClassType { IsInstance: false } c:
                    if (c.Info.Name == "tuple" && c.Info.Module.Name == "builtins") return TupleAnnotation(m, args, lookup);
                    if (c.Info.Name == "type" && c.Info.Module.Name == "builtins" && args.Count == 1)
                        return TypeOfClassObject(Annotation(m, args[0], lookup));
                    return new ClassType(c.Info, args.Select(a => Annotation(m, a, lookup)).ToList(), true);
                default: return PyType.Unknown;
            }
        });
    }

    PyType TypeOfClassObject(PyType instance) => MapMembers(instance, t => t switch
    {
        ClassType { IsInstance: true } c => c.AsClassObject(),
        TypeVarType { Name: "Self" } => new TypeVarType("type[Self]"),
        UnknownType => Inst("type"),
        _ => PyType.Unknown,
    });

    PyType TupleAnnotation(ModuleInfo m, IReadOnlyList<Expr> args, Scope? lookup)
    {
        var tuple = BuiltinClass("tuple");
        if (tuple == null) return PyType.Unknown;
        // tuple[T, ...] is homogeneous; tuple[()] is empty
        if (args.Count == 2 && args[1] is ConstExpr { Kind: ConstKind.Ellipsis })
            return new ClassType(tuple.Info, new[] { Annotation(m, args[0], lookup) }, true);
        if (args.Count == 1 && args[0] is TupleExpr { Items.Count: 0 })
            return new ClassType(tuple.Info, new[] { PyType.Unknown }, true, Array.Empty<PyType>());
        var items = args.Select(a => Annotation(m, a, lookup)).ToList();
        return new ClassType(tuple.Info, new[] { PyType.Union(items) }, true, items);
    }

    PyType SpecialFormSubscript(ModuleInfo m, string form, IReadOnlyList<Expr> args, Scope? lookup)
    {
        PyType A(int i) => i < args.Count ? Annotation(m, args[i], lookup) : PyType.Unknown;
        switch (form)
        {
            case "Optional": return PyType.Union(A(0), NoneInstance);
            case "Union": return PyType.Union(args.Select(a => Annotation(m, a, lookup)));
            case "ClassVar": case "Final": case "Annotated": case "Required": case "NotRequired":
            case "ReadOnly": case "Unpack":
                return A(0);
            case "TypeGuard": case "TypeIs": return Inst("bool");
            case "Type": return TypeOfClassObject(A(0));
            case "Tuple": return TupleAnnotation(m, args, lookup);
            case "List": return Inst("list", A(0));
            case "Set": return Inst("set", A(0));
            case "FrozenSet": return Inst("frozenset", A(0));
            case "Dict": return Inst("dict", A(0), A(1));
            case "Literal": return PyType.Union(args.Select(a => LiteralType(a)));
            case "Callable":
            {
                if (args.Count < 2) return new SignatureType(null, PyType.Unknown);
                PyType ret = A(1);
                if (args[0] is ListExpr l)
                {
                    var ps = l.Items.Select((x, i) =>
                        new ParamInfo($"_{i}", Annotation(m, x, lookup), ParamCategory.PositionalOnly, false)).ToList();
                    return new SignatureType(ps, ret);
                }
                return new SignatureType(null, ret); // Callable[..., R] / ParamSpec
            }
            default: return PyType.Unknown;
        }
    }

    PyType LiteralType(Expr e) => e switch
    {
        ConstExpr { Kind: ConstKind.Int } => Inst("int"),
        ConstExpr { Kind: ConstKind.Str } => Inst("str"),
        ConstExpr { Kind: ConstKind.Bytes } => Inst("bytes"),
        ConstExpr { Kind: ConstKind.True or ConstKind.False } => Inst("bool"),
        ConstExpr { Kind: ConstKind.None } => NoneInstance,
        UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } } => Inst("int"),
        _ => PyType.Unknown,
    };
}
