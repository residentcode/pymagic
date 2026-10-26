using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>The functional forms Enum("Color", "RED GREEN") and TypedDict("Movie", {...}), and PEP 681 dataclass_transform (attrs, pydantic, your own).</summary>
public sealed partial class TypeEvaluator
{
    readonly Dictionary<ClassInfo, List<(FieldInfo Field, bool Required)>> _syntheticTypedDictFields = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, DataclassOptions?> _transformOptions = new(ReferenceEqualityComparer.Instance);

    // ------------------------------------------------------------ functional Enum

    /// <summary>`Enum("Color", "RED GREEN")`, `IntEnum("N", ["A", "B"])`, `Enum("E", [("A", 1)])`, `Enum("E", {"A": 1})`.</summary>
    bool IsEnumFunctionalCall(ClassType callee, CallExpr call)
    {
        if (callee.IsInstance) return false;
        bool baseEnum = callee.Info.QualifiedName == "enum.Enum" || IsEnumClass(callee.Info);
        if (!baseEnum || EnumMembersOf(callee.Info).Count > 0) return false;
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
        var first = positional.Count > 0 ? positional[0].Value : KeywordArg(call, "value");
        bool hasNames = positional.Count > 1 || KeywordArg(call, "names") != null;
        return StringValue(first) != null && hasNames && call.Args.All(a => a.Stars == 0);
    }

    PyType EnumFromCall(ModuleInfo m, CallExpr call, ClassType callee)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).Select(a => a.Value).ToList();
        string name = StringValue(positional.Count > 0 ? positional[0] : KeywordArg(call, "value")) ?? "Enum";
        var namesExpr = positional.Count > 1 ? positional[1] : KeywordArg(call, "names");

        var members = new List<(string Name, Expr? Value)>();
        bool known = true;
        int counter = 0;
        Expr Auto() => new ConstExpr(ConstKind.Int, (++counter).ToString()) { Start = call.Start, End = call.End };

        switch (namesExpr)
        {
            case ConstExpr:
            {
                var words = StringValue(namesExpr)?.Split(new[] { ' ', ',', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (words == null) known = false; else foreach (var w in words) members.Add((w, Auto()));
                break;
            }
            case ListExpr or TupleExpr:
            {
                var items = namesExpr is ListExpr l ? l.Items : ((TupleExpr)namesExpr).Items;
                foreach (var it in items)
                {
                    if (StringValue(it) is { } plain) members.Add((plain, Auto()));
                    else if (it is TupleExpr { Items.Count: 2 } pair && StringValue(pair.Items[0]) is { } named) members.Add((named, pair.Items[1]));
                    else { known = false; break; }
                }
                break;
            }
            case DictExpr d:
                for (int i = 0; i < d.Keys.Count; i++)
                {
                    if (d.Keys[i] != null && StringValue(d.Keys[i]) is { } key) members.Add((key, d.Values[i]));
                    else { known = false; break; }
                }
                break;
            default:
                known = false;
                break;
        }

        var info = MakeSyntheticClass(m, name, call, callee.AsClassObject(), Array.Empty<(string, PyType)>());
        if (known) _enumMembers[info] = members;
        else
        {
            // member names computed at run time: do not complain about members
            _enumMembers[info] = new List<(string, Expr?)>();
            ClassDataOf(info).HasUnknownBase = true;
        }
        return new ClassType(info, false);
    }

    // ------------------------------------------------------------ functional TypedDict

    bool IsTypedDictFunctionalCall(PyType callee, CallExpr call)
    {
        bool isForm = callee is SpecialFormType { Name: "TypedDict" }
            || callee is ClassType { IsInstance: false } c && c.Info.QualifiedName is "typing.TypedDict" or "typing_extensions.TypedDict";
        if (!isForm) return false;
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
        return positional.Count >= 1 && StringValue(positional[0].Value) != null && call.Args.All(a => a.Stars == 0);
    }

    /// <summary>`TypedDict("Movie", {"name": str, "year": int}, total=False)` and `TypedDict("Movie", name=str)`.</summary>
    PyType TypedDictFromCall(ModuleInfo m, CallExpr call)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).Select(a => a.Value).ToList();
        string name = StringValue(positional[0]) ?? "TypedDict";
        bool total = BoolConst(KeywordArg(call, "total")) != false;

        var fields = new List<(FieldInfo, bool)>();
        bool known = true;
        if (positional.Count > 1)
        {
            if (positional[1] is DictExpr d)
            {
                for (int i = 0; i < d.Keys.Count; i++)
                {
                    if (d.Keys[i] == null || StringValue(d.Keys[i]) is not { } key) { known = false; break; }
                    var head = AnnotationHead(d.Values[i]);
                    bool required = head == "Required" || (head != "NotRequired" && total);
                    fields.Add((new FieldInfo(key, Annotation(m, d.Values[i], null), !required), required));
                }
            }
            else known = false;
        }
        else
        {
            foreach (var a in call.Args.Where(a => a.Name != null && a.Name is not ("total" or "typename")))
            {
                var head = AnnotationHead(a.Value);
                bool required = head == "Required" || (head != "NotRequired" && total);
                fields.Add((new FieldInfo(a.Name!, Annotation(m, a.Value, null), !required), required));
            }
        }

        var info = MakeSyntheticClass(m, name, call, BuiltinClass("dict"), Array.Empty<(string, PyType)>());
        ClassDataOf(info).IsTypedDict = true;
        if (known) _syntheticTypedDictFields[info] = fields;
        else
        {
            _syntheticTypedDictFields[info] = new List<(FieldInfo, bool)>();
            ClassDataOf(info).HasUnknownBase = true;
        }
        return new ClassType(info, false);
    }

    // ------------------------------------------------------------ dataclass_transform

    static bool IsTransformMarker(Expr d) =>
        (d is CallExpr call ? call.Func : d) is NameExpr { Name: "dataclass_transform" } or AttributeExpr { Attr: "dataclass_transform" };

    /// <summary>The defaults a `@dataclass_transform(...)` decorator on this node declares, or null if there is none.</summary>
    static DataclassOptions? TransformMarker(IEnumerable<Expr> decorators)
    {
        foreach (var d in decorators)
        {
            if (!IsTransformMarker(d)) continue;
            var call = d as CallExpr;
            return new DataclassOptions(Init: true,
                KwOnly: call != null && BoolConst(KeywordArg(call, "kw_only_default")) == true,
                Frozen: call != null && BoolConst(KeywordArg(call, "frozen_default")) == true);
        }
        return null;
    }

    /// <summary>The transform behind a decorator expression: a function (or overloads) marked with dataclass_transform.</summary>
    DataclassOptions? TransformOfDecorator(ModuleInfo m, Expr head)
    {
        var t = TypeOf(m, head);
        foreach (var member in t.Members)
        {
            switch (member)
            {
                case FunctionType { Info.Node: FunctionDef f }:
                    if (TransformMarker(f.Decorators) is { } o) return o;
                    break;
                case OverloadedType ov:
                    foreach (var fn in ov.Overloads)
                        if (fn.Info.Node is FunctionDef od && TransformMarker(od.Decorators) is { } oo) return oo;
                    break;
                case ClassType { IsInstance: false } c:
                    if (TransformMarker(c.Info.Node.Decorators) is { } co) return co;
                    break;
            }
        }
        return null;
    }

    /// <summary>The transform an ancestor class provides: it is decorated, or its metaclass is.</summary>
    DataclassOptions? TransformOfAncestors(ClassInfo c)
    {
        foreach (var e in Mro(c).Skip(1))
        {
            if (e is not ClassType ec) continue;
            if (_transformOptions.TryGetValue(ec.Info, out var cached)) { if (cached != null) return cached; continue; }
            _transformOptions[ec.Info] = null;
            var own = TransformMarker(ec.Info.Node.Decorators);
            if (own == null)
            {
                var meta = ec.Info.Node.Bases.FirstOrDefault(b => b.Name == "metaclass");
                if (meta != null && TypeOf(ec.Info.Module, meta.Value) is ClassType { IsInstance: false } mc)
                    foreach (var me in Mro(mc.Info))
                        if (me is ClassType mec && TransformMarker(mec.Info.Node.Decorators) is { } mo) { own = mo; break; }
            }
            _transformOptions[ec.Info] = own;
            if (own != null) return own;
        }
        return null;
    }

    /// <summary>A class decorated with a transform function, or deriving from a class whose metaclass is one: the options it is built with.</summary>
    DataclassOptions? TransformedDataclassOf(ClassInfo c)
    {
        foreach (var d in c.Node.Decorators)
        {
            var call = d as CallExpr;
            var head = call != null ? call.Func : d;
            if (IsTransformMarker(d)) continue;
            if (TransformOfDecorator(c.Module, head) is not { } defaults) continue;
            return new DataclassOptions(
                Init: call == null || BoolConst(KeywordArg(call, "init")) != false,
                KwOnly: call != null && BoolConst(KeywordArg(call, "kw_only")) is bool k ? k : defaults.KwOnly,
                Frozen: call != null && BoolConst(KeywordArg(call, "frozen")) is bool f ? f : defaults.Frozen);
        }
        if (TransformOfAncestors(c) is { } inherited)
        {
            var kw = c.Node.Bases.FirstOrDefault(b => b.Name == "kw_only");
            var frozen = c.Node.Bases.FirstOrDefault(b => b.Name == "frozen");
            return new DataclassOptions(Init: true,
                KwOnly: kw != null && BoolConst(kw.Value) is bool k2 ? k2 : inherited.KwOnly,
                Frozen: frozen != null && BoolConst(frozen.Value) is bool f2 ? f2 : inherited.Frozen);
        }
        return null;
    }

    /// <summary>Calls such as `field(default=1)`, `Field(...)`, `attr.ib(factory=list)` that describe one field.</summary>
    static bool IsFieldSpecifier(Expr? value, out CallExpr call)
    {
        call = null!;
        if (value is not CallExpr c) return false;
        string? name = c.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
        if (name is not ("field" or "Field" or "ib" or "attrib")) return false;
        call = c;
        return true;
    }

    static bool FieldHasDefault(CallExpr call)
    {
        if (KeywordArg(call, "default") != null || KeywordArg(call, "default_factory") != null || KeywordArg(call, "factory") != null) return true;
        // pydantic: Field(...) is "required", Field(5) has the default 5
        var first = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0);
        return first != null && first.Value is not ConstExpr { Kind: ConstKind.Ellipsis };
    }
}
