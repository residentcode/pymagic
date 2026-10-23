using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>A field of a dataclass, named tuple or TypedDict.</summary>
public sealed record FieldInfo(string Name, PyType Type, bool HasDefault, bool InInit = true, bool KwOnly = false);

/// <summary>
/// Library features that give classes meaning beyond their source: enums, dataclasses, named tuples, TypedDicts.
/// Each is recognised from the class's ancestry or decorators and contributes members, constructor signatures
/// and tuple/dict-like behaviour to the ordinary member and call machinery.
/// </summary>
public sealed partial class TypeEvaluator
{
    sealed record DataclassOptions(bool Init, bool KwOnly, bool Frozen);

    readonly Dictionary<ClassInfo, DataclassOptions?> _dataclassOptions = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, List<FieldInfo>> _ownFields = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, List<FieldInfo>> _syntheticTupleFields = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<ClassInfo> _syntheticUnknownTuples = new(ReferenceEqualityComparer.Instance);   // namedtuple(...) whose field names are computed
    readonly Dictionary<Node, ClassInfo> _syntheticByNode = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Declaration, PyType> _declOverride = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, bool> _isEnum = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, List<(string Name, Expr? Value)>> _enumMembers = new(ReferenceEqualityComparer.Instance);

    bool InheritsFrom(ClassInfo c, string qualifiedName) =>
        Mro(c).Any(e => e is ClassType ct && ct.Info.QualifiedName == qualifiedName);

    static bool HasExplicitMember(ClassInfo c, string name) =>
        c.Scope.Symbols.TryGetValue(name, out var s) && (s.Redirect ?? s).Declarations.Count > 0;

    // ------------------------------------------------------------ synthetic classes

    /// <summary>A class that exists only because of a call such as namedtuple("P", "x y").</summary>
    ClassInfo MakeSyntheticClass(ModuleInfo m, string name, Node at, ClassType? baseClass, IEnumerable<(string Name, PyType Type)> members)
    {
        if (_syntheticByNode.TryGetValue(at, out var existing)) return existing;
        var def = new ClassDef(name, Array.Empty<TypeParam>(), Array.Empty<Argument>(), Array.Empty<Stmt>(), Array.Empty<Expr>())
        { Start = at.Start, End = at.End, NameStart = at.Start };
        var scope = new Scope(ScopeKind.Class, def, null);
        var info = new ClassInfo(name, m, def, scope);

        var data = new ClassData();
        var b = baseClass ?? BuiltinClass("object");
        if (b != null) data.Bases.Add(b);
        _classData[info] = data;

        foreach (var (n, t) in members)
        {
            var sym = scope.GetOrAdd(n);
            var decl = new Declaration(n, DeclKind.Variable, new NameExpr(n));
            sym.Declarations.Add(decl);
            _declOverride[decl] = t;
        }
        _syntheticByNode[at] = info;
        return info;
    }

    static string? StringValue(Expr? e)
    {
        if (e is not ConstExpr { Kind: ConstKind.Str, IsFString: false } c) return null;
        string text = c.Text;
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return null;
        int qlen = text.Length - q >= 6 && text[q + 1] == text[q] && text[q + 2] == text[q] ? 3 : 1;
        if (text.Length < q + 2 * qlen) return null;
        return text.Substring(q + qlen, text.Length - q - 2 * qlen);
    }

    // ------------------------------------------------------------ enum

    public bool IsEnumClass(ClassInfo c)
    {
        if (_isEnum.TryGetValue(c, out var r)) return r;
        _isEnum[c] = false; // guards against inheritance cycles
        r = c.QualifiedName != "enum.Enum" && InheritsFrom(c, "enum.Enum");
        return _isEnum[c] = r;
    }

    List<(string Name, Expr? Value)> EnumMembersOf(ClassInfo c)
    {
        if (_enumMembers.TryGetValue(c, out var list)) return list;
        list = new List<(string, Expr?)>();
        _enumMembers[c] = list;
        void Scan(IEnumerable<Stmt> body)
        {
            foreach (var st in body)
            {
                switch (st)
                {
                    case AssignStmt { Targets.Count: 1, Targets: [NameExpr n] } a when IsMemberName(n.Name) && !IsDescriptorValue(a.Value):
                        list.Add((n.Name, a.Value));
                        break;
                    case AnnAssignStmt { Target: NameExpr n, Value: not null } an when IsMemberName(n.Name) && !IsDescriptorValue(an.Value):
                        list.Add((n.Name, an.Value));
                        break;
                    case IfStmt or TryStmt:
                        Scan(AstChildren.Of(st).OfType<Stmt>());
                        break;
                }
            }
        }
        Scan(c.Node.Body);
        return list;
    }

    static bool IsMemberName(string n) => !n.StartsWith('_');

    static bool IsDescriptorValue(Expr? v) => v is LambdaExpr
        || v is CallExpr { Func: NameExpr { Name: "property" or "classmethod" or "staticmethod" or "nonmember" } };

    PyType EnumValueType(ClassInfo c, Expr? value)
    {
        if (value == null) return PyType.Unknown;
        if (value is CallExpr { Func: NameExpr { Name: "auto" } } or CallExpr { Func: AttributeExpr { Attr: "auto" } })
            return InheritsFrom(c, "builtins.str") ? Inst("str") : Inst("int");
        return TypeOf(c.Module, value);
    }

    /// <summary>Members, <c>name</c> and <c>value</c> of enum classes; false if the name is not special.</summary>
    bool TryEnumAttribute(ClassType c, string name, List<PyType> types)
    {
        if (!IsEnumClass(c.Info)) return false;
        var members = EnumMembersOf(c.Info);
        if (members.Any(x => x.Name == name))
        {
            types.Add(new ClassType(c.Info, c.Args, true, null, name));
            return true;
        }
        if (!c.IsInstance) return false;

        if (name is "value" or "_value_")
        {
            if (c.EnumMember != null)
            {
                var m = members.FirstOrDefault(x => x.Name == c.EnumMember);
                if (m.Name != null)
                {
                    types.Add(EnumValueType(c.Info, m.Value));
                    return true;
                }
            }
            if (members.Count > 0)
            {
                types.Add(PyType.Union(members.Select(x => EnumValueType(c.Info, x.Value))));
                return true;
            }
            return false; // no members known: fall back to the stub's declaration
        }
        if (name is "name" or "_name_")
        {
            types.Add(Inst("str"));
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------ field extraction (dataclass, NamedTuple, TypedDict)

    static string? AnnotationHead(Expr ann) => ann switch
    {
        NameExpr n => n.Name,
        AttributeExpr a => a.Attr,
        SubscriptExpr s => AnnotationHead(s.Value),
        _ => null,
    };

    static Expr? KeywordArg(CallExpr call, string name) =>
        call.Args.FirstOrDefault(a => a.Name == name)?.Value;

    static bool? BoolConst(Expr? e) => e switch
    {
        ConstExpr { Kind: ConstKind.True } => true,
        ConstExpr { Kind: ConstKind.False } => false,
        _ => null,
    };

    IEnumerable<AnnAssignStmt> AnnotatedFields(ClassInfo c)
    {
        IEnumerable<AnnAssignStmt> Scan(IEnumerable<Stmt> body)
        {
            foreach (var st in body)
            {
                if (st is AnnAssignStmt { Target: NameExpr } a) yield return a;
                else if (st is IfStmt or TryStmt)
                    foreach (var inner in Scan(AstChildren.Of(st).OfType<Stmt>())) yield return inner;
            }
        }
        return Scan(c.Node.Body);
    }

    // ---- dataclass

    DataclassOptions? DataclassOf(ClassInfo c)
    {
        if (_dataclassOptions.TryGetValue(c, out var o)) return o;
        o = null;
        foreach (var d in c.Node.Decorators)
        {
            var call = d as CallExpr;
            var head = call != null ? call.Func : d;
            string? name = head switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
            if (name != "dataclass") continue;
            o = new DataclassOptions(
                Init: call == null || BoolConst(KeywordArg(call, "init")) != false,
                KwOnly: call != null && BoolConst(KeywordArg(call, "kw_only")) == true,
                Frozen: call != null && BoolConst(KeywordArg(call, "frozen")) == true);
            break;
        }
        return _dataclassOptions[c] = o;
    }

    public bool IsDataclass(ClassInfo c) => Mro(c).Any(e => e is ClassType ec && DataclassOf(ec.Info) != null);

    List<FieldInfo> OwnDataclassFields(ClassInfo c, bool classKwOnly)
    {
        if (_ownFields.TryGetValue(c, out var cached)) return cached;
        var list = new List<FieldInfo>();
        bool kwOnly = classKwOnly;
        foreach (var a in AnnotatedFields(c))
        {
            var name = ((NameExpr)a.Target).Name;
            var head = AnnotationHead(a.Annotation);
            if (head == "ClassVar") continue;
            if (head == "KW_ONLY") { kwOnly = true; continue; }

            Expr ann = a.Annotation;
            if (head == "InitVar" && ann is SubscriptExpr isub) ann = isub.Index;
            var type = AnnotationType(c.Module, ann);

            bool hasDefault = a.Value != null, inInit = true, fieldKw = kwOnly;
            if (a.Value is CallExpr { Func: NameExpr { Name: "field" } or AttributeExpr { Attr: "field" } } fc)
            {
                hasDefault = KeywordArg(fc, "default") != null || KeywordArg(fc, "default_factory") != null;
                if (BoolConst(KeywordArg(fc, "init")) == false) inInit = false;
                if (BoolConst(KeywordArg(fc, "kw_only")) is bool k) fieldKw = k;
            }
            list.Add(new FieldInfo(name, type, hasDefault, inInit, fieldKw));
        }
        return _ownFields[c] = list;
    }

    /// <summary>All dataclass fields in init order: base classes first, redefinitions keep their original position.</summary>
    IReadOnlyList<FieldInfo> DataclassFields(ClassType receiver)
    {
        var map = TypeParamMap(receiver.Info, receiver.Args);
        var result = new List<FieldInfo>();
        foreach (var e in Mro(receiver.Info).AsEnumerable().Reverse())
        {
            if (e is not ClassType ec) continue;
            var opts = DataclassOf(ec.Info);
            if (opts == null) continue;
            var entryMap = TypeParamMap(ec.Info, ((ClassType)Substitute(ec, map)).Args);
            foreach (var f in OwnDataclassFields(ec.Info, opts.KwOnly))
            {
                var field = f with { Type = Substitute(f.Type, entryMap) };
                int at = result.FindIndex(x => x.Name == f.Name);
                if (at >= 0) result[at] = field; else result.Add(field);
            }
        }
        return result;
    }

    // ---- named tuples

    /// <summary>Fields of the named tuple this class is or extends; null if it is not a named tuple.</summary>
    public IReadOnlyList<FieldInfo>? NamedTupleFieldsOf(ClassInfo c)
    {
        foreach (var e in Mro(c))
        {
            if (e is not ClassType ec) return null;
            if (_syntheticTupleFields.TryGetValue(ec.Info, out var synth)) return synth;
            if (ClassDataOf(ec.Info).Bases.OfType<ClassType>().Any(b => b.Info.QualifiedName == "typing.NamedTuple"))
            {
                if (!_ownFields.TryGetValue(ec.Info, out var list))
                {
                    list = new List<FieldInfo>();
                    foreach (var a in AnnotatedFields(ec.Info))
                        list.Add(new FieldInfo(((NameExpr)a.Target).Name, AnnotationType(ec.Info.Module, a.Annotation), a.Value != null));
                    _ownFields[ec.Info] = list;
                }
                return list;
            }
        }
        return null;
    }

    bool DefinesNamedTuple(ClassInfo c) =>
        _syntheticTupleFields.ContainsKey(c) || _syntheticUnknownTuples.Contains(c) || ClassDataOf(c).Bases.OfType<ClassType>().Any(b => b.Info.QualifiedName == "typing.NamedTuple");

    /// <summary>The items an instance unpacks/iterates/indexes as: a tuple's recorded items, or a named tuple's fields.</summary>
    IReadOnlyList<PyType>? TupleItemsOf(ClassType c)
    {
        if (c.TupleItems != null) return c.TupleItems;
        if (!c.IsInstance) return null;
        return NamedTupleFieldsOf(c.Info)?.Select(f => f.Type).ToList();
    }

    /// <summary>namedtuple("P", "x y") / namedtuple("P", ["x", "y"], defaults=[0]) -> a class object.</summary>
    PyType NamedTupleFromCall(ModuleInfo m, CallExpr call)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).Select(a => a.Value).ToList();
        string name = StringValue(positional.Count > 0 ? positional[0] : KeywordArg(call, "typename")) ?? "namedtuple";
        var namesExpr = positional.Count > 1 ? positional[1] : KeywordArg(call, "field_names");

        List<string>? names = namesExpr switch
        {
            ConstExpr => StringValue(namesExpr)?.Split(new[] { ' ', ',', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
            ListExpr l => l.Items.Select(StringValue).ToList() is var xs && xs.All(x => x != null) ? xs.Select(x => x!).ToList() : null,
            TupleExpr t => t.Items.Select(StringValue).ToList() is var ys && ys.All(x => x != null) ? ys.Select(x => x!).ToList() : null,
            _ => null,
        };
        int defaults = KeywordArg(call, "defaults") switch { ListExpr l => l.Items.Count, TupleExpr t => t.Items.Count, _ => 0 };

        var tupleBase = TypingClass("NamedTuple");
        var fields = (names ?? new List<string>()).Select((n, i) => new FieldInfo(n, PyType.Any, i >= names!.Count - defaults)).ToList();
        var info = MakeSyntheticClass(m, name, call, tupleBase, fields.Select(f => (f.Name, f.Type)));
        if (names != null) _syntheticTupleFields[info] = fields;
        else
        {
            // field names not known statically: do not complain about members or construction
            ClassDataOf(info).HasUnknownBase = true;
            _syntheticUnknownTuples.Add(info);
        }
        return new ClassType(info, false);
    }

    /// <summary>NamedTuple("P", [("x", int), ("y", str)]) -> a class object.</summary>
    PyType NamedTupleFromTypingCall(ModuleInfo m, CallExpr call)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).Select(a => a.Value).ToList();
        string name = StringValue(positional.Count > 0 ? positional[0] : null) ?? "NamedTuple";
        var fields = new List<FieldInfo>();
        bool known = positional.Count > 1 && positional[1] is ListExpr or TupleExpr;
        if (known)
        {
            var items = positional[1] switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => (IReadOnlyList<Expr>)Array.Empty<Expr>() };
            foreach (var it in items)
            {
                if (it is TupleExpr { Items.Count: 2 } pair && StringValue(pair.Items[0]) is string fname)
                    fields.Add(new FieldInfo(fname, AnnotationType(m, pair.Items[1]), false));
                else { known = false; break; }
            }
        }
        var tupleBase = TypingClass("NamedTuple");
        var info = MakeSyntheticClass(m, name, call, tupleBase, fields.Select(f => (f.Name, f.Type)));
        if (known) _syntheticTupleFields[info] = fields;
        else ClassDataOf(info).HasUnknownBase = true;
        return new ClassType(info, false);
    }

    // ---- TypedDict

    /// <summary>A user TypedDict: it, or one of its ancestors, lists the TypedDict special form as a base (library classes do not count).</summary>
    bool IsTypedDictClass(ClassInfo c) =>
        c.Module.Name is not ("typing" or "typing_extensions")
        && Mro(c).Any(e => e is ClassType ec && ClassDataOf(ec.Info).IsTypedDict);

    /// <summary>Fields of a TypedDict class (with inherited ones); null if the class is not a TypedDict.</summary>
    public IReadOnlyList<(FieldInfo Field, bool Required)>? TypedDictFieldsOf(ClassInfo c)
    {
        if (!IsTypedDictClass(c)) return null;
        var result = new List<(FieldInfo, bool)>();
        foreach (var e in Mro(c).AsEnumerable().Reverse())
        {
            if (e is not ClassType ec || !IsTypedDictClass(ec.Info)) continue;
            bool total = ec.Info.Node.Bases.FirstOrDefault(b => b.Name == "total") is not { } t || BoolConst(t.Value) != false;
            foreach (var a in AnnotatedFields(ec.Info))
            {
                var head = AnnotationHead(a.Annotation);
                bool required = head == "Required" || (head != "NotRequired" && total);
                var field = new FieldInfo(((NameExpr)a.Target).Name, AnnotationType(ec.Info.Module, a.Annotation), !required);
                int at = result.FindIndex(x => x.Item1.Name == field.Name);
                if (at >= 0) result[at] = (field, required); else result.Add((field, required));
            }
        }
        return result;
    }

    // ------------------------------------------------------------ synthesized constructors

    /// <summary>The parameters (without self/cls) a library feature gives the class <paramref name="entry"/> for construction, if any.</summary>
    List<ParamInfo>? SynthesizedParams(ClassType receiver, ClassType entry, out bool isNamedTuple)
    {
        isNamedTuple = false;
        var dc = DataclassOf(entry.Info);
        if (dc != null)
        {
            if (!dc.Init) return null;
            var fields = DataclassFields(receiver).Where(f => f.InInit).ToList();
            return fields.Where(f => !f.KwOnly).Select(f => new ParamInfo(f.Name, f.Type, ParamCategory.Normal, f.HasDefault))
                .Concat(fields.Where(f => f.KwOnly).Select(f => new ParamInfo(f.Name, f.Type, ParamCategory.KeywordOnly, f.HasDefault))).ToList();
        }
        if (DefinesNamedTuple(entry.Info))
        {
            isNamedTuple = true;
            if (_syntheticUnknownTuples.Contains(entry.Info))
                return new List<ParamInfo>
                {
                    new("args", PyType.Any, ParamCategory.VarPositional, false),
                    new("kwargs", PyType.Any, ParamCategory.VarKeyword, false),
                };
            var fields = NamedTupleFieldsOf(entry.Info) ?? Array.Empty<FieldInfo>();
            return fields.Select(f => new ParamInfo(f.Name, f.Type, ParamCategory.Normal, f.HasDefault)).ToList();
        }
        if (IsTypedDictClass(entry.Info))
        {
            var fields = TypedDictFieldsOf(receiver.Info) ?? Array.Empty<(FieldInfo, bool)>();
            var ps = new List<ParamInfo> { new("__map", PyType.Any, ParamCategory.PositionalOnly, true) };
            ps.AddRange(fields.Select(f => new ParamInfo(f.Field.Name, f.Field.Type, ParamCategory.KeywordOnly, !f.Required)));
            return ps;
        }
        return null;
    }

    /// <summary>The constructor signature the library feature provides for this class, or null to use the ordinary lookup.</summary>
    SignatureType? SynthesizedInit(ClassType receiver)
    {
        foreach (var e in Mro(receiver.Info))
        {
            if (e is not ClassType ec) return null;
            // anything the class wrote itself takes precedence over what a base class would synthesize
            if (HasExplicitMember(ec.Info, "__init__") || HasExplicitMember(ec.Info, "__new__")) return null;
            var ps = SynthesizedParams(receiver, ec, out _);
            if (ps != null) return new SignatureType(ps, receiver);
        }
        return null;
    }

    /// <summary>
    /// <c>__init__</c> / <c>__new__</c> as a member (for explicit calls such as super().__new__(cls, ...)) of a class whose
    /// constructor comes from a library feature. For super() the search starts after <paramref name="startAfter"/>.
    /// </summary>
    PyType? SynthesizedMember(ClassType receiver, string name, ClassInfo? startAfter)
    {
        bool skipping = startAfter != null;
        foreach (var e in Mro(receiver.Info))
        {
            if (e is not ClassType ec) return null;
            if (skipping) { if (ReferenceEquals(ec.Info, startAfter)) skipping = false; continue; }
            if (HasExplicitMember(ec.Info, name)) return null;
            var ps = SynthesizedParams(receiver, ec, out bool isNamedTuple);
            if (ps == null) continue;
            if (name == "__init__" && !isNamedTuple) return new SignatureType(ps, NoneInstance);
            if (name == "__new__" && isNamedTuple)
            {
                var all = new List<ParamInfo> { new("cls", PyType.Unknown, ParamCategory.PositionalOnly, false) };
                all.AddRange(ps);
                return new SignatureType(all, receiver.AsInstance());
            }
        }
        return null;
    }
}