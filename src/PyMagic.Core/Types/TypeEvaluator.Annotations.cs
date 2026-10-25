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
                        "DefaultDict" => CollectionsInst("defaultdict"),
                        "OrderedDict" => CollectionsInst("OrderedDict"),
                        "Counter" => CollectionsInst("Counter"),
                        "Deque" => CollectionsInst("deque"),
                        "ChainMap" => CollectionsInst("ChainMap"),
                        "TypedDict" => Inst("dict", Inst("str"), PyType.Any),
                        "Callable" => new SignatureType(null, PyType.Unknown),
                        _ => PyType.Unknown,
                    };
                case ClassType { IsInstance: true } inst when IsNone(inst): return inst;
                default: return PyType.Unknown;
            }
        });
    }

    readonly Dictionary<string, ClassType?> _collectionsClasses = new();

    PyType CollectionsInst(string name, params PyType[] args)
    {
        var module = _ws.GetModule("collections");
        var c = module == null ? null : ModuleClass(_collectionsClasses, module, name);
        return c == null ? PyType.Unknown : new ClassType(c.Info, args, true);
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
        TypeVarType tv when !tv.Name.StartsWith("type[", StringComparison.Ordinal) => new TypeVarType($"type[{tv.Name}]"),   // type[T]: solved from the class an argument is
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
            case "DefaultDict": return CollectionsInst("defaultdict", A(0), A(1));
            case "OrderedDict": return CollectionsInst("OrderedDict", A(0), A(1));
            case "Counter": return CollectionsInst("Counter", A(0));
            case "Deque": return CollectionsInst("deque", A(0));
            case "ChainMap": return CollectionsInst("ChainMap", A(0), A(1));
            case "Literal": return PyType.Union(args.Select(a => LiteralType(m, a, lookup)));
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

    /// <summary>One argument of Literal[...]: a value of str, int, bytes or bool, None, an enum member, or another Literal.</summary>
    PyType LiteralType(ModuleInfo m, Expr e, Scope? lookup)
    {
        PyType WithValue(string cls, object? value) =>
            Inst(cls) is ClassType c && value != null ? c with { LiteralValue = value } : Inst(cls);
        switch (e)
        {
            case ConstExpr { Kind: ConstKind.Int } i: return WithValue("int", LiteralInt(i.Text, negative: false));
            case ConstExpr { Kind: ConstKind.Str, IsFString: false } s: return WithValue("str", StringValue(s));
            case ConstExpr { Kind: ConstKind.Bytes } b: return WithValue("bytes", LiteralBytesValue(b));
            case ConstExpr { Kind: ConstKind.True }: return WithValue("bool", true);
            case ConstExpr { Kind: ConstKind.False }: return WithValue("bool", false);
            case ConstExpr { Kind: ConstKind.None }: return NoneInstance;
            case UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } ni }: return WithValue("int", LiteralInt(ni.Text, negative: true));
            case NameExpr or AttributeExpr:
                // Literal[Color.RED]
                return TypeOf(m, e) is ClassType { IsInstance: true, EnumMember: not null } member ? member : PyType.Unknown;
            case SubscriptExpr:
                return Annotation(m, e, lookup);      // Literal[Literal["a"], "b"]
            default: return PyType.Unknown;
        }
    }

    static object? LiteralInt(string text, bool negative)
    {
        text = text.Replace("_", "");
        long value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { if (!long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value)) return null; }
        else if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { try { value = Convert.ToInt64(text[2..], 8); } catch (Exception) { return null; } }
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { try { value = Convert.ToInt64(text[2..], 2); } catch (Exception) { return null; } }
        else if (!long.TryParse(text, out value)) return null;
        return negative ? -value : value;
    }

    /// <summary>The text between the quotes of a bytes literal.</summary>
    static LiteralBytes? LiteralBytesValue(ConstExpr c)
    {
        string text = c.Text;
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return null;
        int n = text.Length - q >= 6 && text[q + 1] == text[q] && text[q + 2] == text[q] ? 3 : 1;
        return text.Length < q + 2 * n ? null : new LiteralBytes(text.Substring(q + n, text.Length - q - 2 * n));
    }
}
