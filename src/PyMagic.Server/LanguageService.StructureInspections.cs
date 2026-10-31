using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Inspections of how classes and functions are put together, for the mistakes that fail when the program starts or when the class is used: dataclass and named tuple fields in the wrong order, a mutable dataclass default, extending an enumeration that has members, instantiating an abstract class or a protocol, TypedDict keys that do not exist, and overloads that are alone or have no implementation.</summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> StructureDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            IEnumerable<Finding> findings = node switch
            {
                ClassDef cd => ClassStructure(m, cd, ev).Concat(OverloadStructure(cd.Body, cd.Bases.Any(b => b.Name == null && m.Source[b.Value.Start..b.Value.End].EndsWith("Protocol")) || IsAbstractClassDef(cd))),
                CallExpr { Func: AttributeExpr { Attr: "get" or "pop" or "setdefault" } get, Args: [{ Name: null, Stars: 0, Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } key }, ..] } call
                    => CallStructure(m, call, ev).Concat(CleanedDataGet(m, get.Value, key, ev)),
                CallExpr call => CallStructure(m, call, ev),
                SubscriptExpr sub => TypedDictSubscript(m, sub, ev),
                _ => Array.Empty<Finding>(),
            };
            foreach (var f in findings)
                if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }
        // the module's own functions
        foreach (var f in OverloadStructure(m.Ast.Body, false))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    static bool IsAbstractClassDef(ClassDef cd) => cd.Body.OfType<FunctionDef>().Any(IsAbstractDef);

    // ------------------------------------------------------------ dataclass and named tuple fields

    static string? DecoratorName(Expr d)
    {
        var head = d is CallExpr c ? c.Func : d;
        return head switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
    }

    static bool IsStdDataclass(ClassDef cd, out CallExpr? call)
    {
        call = null;
        foreach (var d in cd.Decorators)
        {
            if (DecoratorName(d) != "dataclass") continue;
            var head = d is CallExpr c ? c.Func : d;
            if (head is AttributeExpr { Value: not NameExpr { Name: "dataclasses" } }) continue;
            call = d as CallExpr;
            return true;
        }
        return false;
    }

    static bool IsTrue(Expr? e) => e is ConstExpr { Kind: ConstKind.True };
    static bool IsFalse(Expr? e) => e is ConstExpr { Kind: ConstKind.False };

    sealed record OwnField(string Name, AnnAssignStmt Node, bool HasDefault, bool InInit, bool KwOnly, Expr? Value);

    /// <summary>The fields a dataclass body declares, in order; ClassVar and the KW_ONLY marker are read the way dataclasses reads them.</summary>
    static List<OwnField> DataclassBodyFields(ClassDef cd, bool classKwOnly)
    {
        var list = new List<OwnField>();
        bool kwOnly = classKwOnly;
        foreach (var st in cd.Body)
        {
            if (st is not AnnAssignStmt { Target: NameExpr name } a) continue;
            Expr ann = a.Annotation is SubscriptExpr s ? s.Value : a.Annotation;
            string? head = ann switch { NameExpr n => n.Name, AttributeExpr at => at.Attr, _ => null };
            if (head == "ClassVar") continue;
            if (head == "KW_ONLY") { kwOnly = true; continue; }
            bool hasDefault = a.Value != null, inInit = true, fieldKw = kwOnly;
            if (a.Value is CallExpr fc && DecoratorName(fc) == "field")
            {
                hasDefault = fc.Args.Any(x => x.Name is "default" or "default_factory");
                if (fc.Args.FirstOrDefault(x => x.Name == "init") is { } initArg && IsFalse(initArg.Value)) inInit = false;
                if (fc.Args.FirstOrDefault(x => x.Name == "kw_only") is { } kwArg) fieldKw = IsTrue(kwArg.Value);
            }
            list.Add(new OwnField(name.Name, a, hasDefault, inInit, fieldKw, a.Value));
        }
        return list;
    }

    static bool IsMutableDisplay(Expr? e) =>
        e is ListExpr or DictExpr or SetExpr
        || e is CallExpr { Args.Count: 0, Func: NameExpr { Name: "list" or "dict" or "set" } };

    IEnumerable<Finding> ClassStructure(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        if (ClassInfoOf(m, cd, ev) is not { } info) yield break;

        // dataclass: a field without a default after one with a default; a list, dict or set as a default
        if (IsStdDataclass(cd, out var dcCall))
        {
            bool noInitMade = dcCall != null && dcCall.Args.FirstOrDefault(a => a.Name == "init") is { } noInit && IsFalse(noInit.Value);   // no __init__ is made, so no order is checked
            bool allKwOnly = dcCall != null && dcCall.Args.FirstOrDefault(a => a.Name == "kw_only") is { } kwo && IsTrue(kwo.Value);
            var order = new List<(string Name, bool HasDefault)>();
            // the fields the base dataclasses bring come first
            if (KnownMro(info, ev) is { } mro)
                foreach (var b in Enumerable.Reverse(mro.Skip(1).ToList()))
                {
                    if (!IsStdDataclass(b.Info.Node, out var bc)) continue;
                    bool bKw = bc != null && bc.Args.FirstOrDefault(a => a.Name == "kw_only") is { } bk && IsTrue(bk.Value);
                    foreach (var f in DataclassBodyFields(b.Info.Node, bKw).Where(f => f.InInit && !f.KwOnly))
                    {
                        int at = order.FindIndex(x => x.Name == f.Name);
                        if (at >= 0) order[at] = (f.Name, f.HasDefault); else order.Add((f.Name, f.HasDefault));
                    }
                }
            foreach (var f in DataclassBodyFields(cd, allKwOnly))
            {
                if (IsMutableDisplay(f.Value) || f.Value is CallExpr fc && DecoratorName(fc) == "field" && fc.Args.FirstOrDefault(a => a.Name == "default") is { } d && IsMutableDisplay(d.Value))
                {
                    var bad = f.Value is CallExpr { } wrapped && DecoratorName(wrapped) == "field" ? wrapped.Args.First(a => a.Name == "default").Value : f.Value!;
                    string kind = bad switch { ListExpr or CallExpr { Func: NameExpr { Name: "list" } } => "list", DictExpr or CallExpr { Func: NameExpr { Name: "dict" } } => "dict", _ => "set" };
                    yield return new Finding(bad.Start, Math.Max(bad.End - bad.Start, 1), $"Mutable default <class '{kind}'> for field '{f.Name}' is not allowed: use default_factory", "dataclass-fields", FindingLevel.Error);
                }
                if (noInitMade || !f.InInit || f.KwOnly) continue;
                int existing = order.FindIndex(x => x.Name == f.Name);
                if (existing >= 0) { order[existing] = (f.Name, f.HasDefault); continue; }
                if (!f.HasDefault && order.Any(x => x.HasDefault))
                    yield return new Finding(f.Node.Target.Start, f.Name.Length, $"Non-default argument '{f.Name}' follows default argument '{order.First(x => x.HasDefault).Name}'", "dataclass-fields", FindingLevel.Error);
                order.Add((f.Name, f.HasDefault));
            }
        }

        // a named tuple: the same rule
        if (cd.Bases.Any(b => b.Name == null && b.Value is NameExpr { Name: "NamedTuple" } or AttributeExpr { Attr: "NamedTuple" }))
        {
            string? firstDefault = null;
            foreach (var st in cd.Body)
            {
                if (st is not AnnAssignStmt { Target: NameExpr name } a) continue;
                if (a.Value != null) { firstDefault ??= name.Name; continue; }
                if (firstDefault != null)
                    yield return new Finding(name.Start, name.Name.Length, $"Non-default namedtuple field '{name.Name}' cannot follow default field '{firstDefault}'", "namedtuple-fields", FindingLevel.Error);
            }
        }

        // an enumeration that has members cannot be extended
        foreach (var b in cd.Bases.Where(b => b.Name == null))
        {
            if (ev.TypeOf(m, b.Value) is not ClassType { IsInstance: false } bt) continue;
            if (bt.Info.Module.IsStub || !ev.IsEnumClass(bt.Info) || !EnumHasMembers(bt.Info.Node)) continue;
            yield return new Finding(b.Value.Start, Math.Max(b.Value.End - b.Value.Start, 1), $"Cannot extend enumeration '{bt.Info.Name}': it defines members", "enum-extend", FindingLevel.Error);
        }
    }

    static bool EnumHasMembers(ClassDef cd)
    {
        bool IsMember(string n, Expr? value) =>
            !n.StartsWith('_') && value != null
            && value is not LambdaExpr
            && !(value is CallExpr c && DecoratorName(c) is "nonmember" or "property" or "staticmethod" or "classmethod" or "cached_property");
        foreach (var st in cd.Body)
        {
            if (st is AssignStmt { Targets: [NameExpr n], Value: var v } && IsMember(n.Name, v)) return true;
            if (st is AnnAssignStmt { Target: NameExpr an, Value: var av } && IsMember(an.Name, av)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ calls: abstract classes, protocols, TypedDict

    IEnumerable<Finding> CallStructure(ModuleInfo m, CallExpr call, TypeEvaluator ev)
    {
        string? written = call.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
        if (written == null || written is "cls" or "self" or "klass") yield break;
        if (ev.TypeOf(m, call.Func) is not ClassType { IsInstance: false } ct || ct.Info.Name != written) yield break;
        if (!NamesAClassDirectly(m, call.Func, ev)) yield break;   // `Console: type[Base]` followed by imports of subclasses says nothing about this class
        var info = ct.Info;
        if (info.Module.IsStub || info.Module.Name is "typing" or "typing_extensions" or "abc") yield break;

        if (ev.IsProtocol(info))
        {
            yield return new Finding(call.Func.Start, Math.Max(call.Func.End - call.Func.Start, 1), $"Cannot instantiate the protocol class '{info.Name}'", "abstract-instantiate", FindingLevel.Error);
            yield break;
        }

        var open = AbstractLeft(info, ev);
        if (open.Count > 0)
        {
            string names = string.Join(", ", open.Take(3)) + (open.Count > 3 ? ", ..." : "");
            yield return new Finding(call.Func.Start, Math.Max(call.Func.End - call.Func.Start, 1), $"Cannot instantiate the abstract class '{info.Name}': {names} not implemented", "abstract-instantiate", FindingLevel.Error);
            yield break;
        }

        // a TypedDict built with keywords: only keys it has, and all that are required
        if (ev.TypedDictFieldsOf(info) is { } fields && call.Args.All(a => a.Name != null && a.Stars == 0) && !info.Module.IsStub)
        {
            foreach (var a in call.Args)
                if (!fields.Any(f => f.Field.Name == a.Name))
                    yield return new Finding(a.Start, Math.Max(a.Name!.Length, 1), $"TypedDict '{info.Name}' has no key '{a.Name}'", "typeddict-keys", FindingLevel.Warning);
            var missing = fields.Where(f => f.Required && call.Args.All(a => a.Name != f.Field.Name)).Select(f => f.Field.Name).ToList();
            if (missing.Count > 0 && call.Args.Count > 0)
                yield return new Finding(call.Func.Start, Math.Max(call.Func.End - call.Func.Start, 1), $"Missing required key{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing.Select(k => "'" + k + "'"))} for TypedDict '{info.Name}'", "typeddict-keys", FindingLevel.Warning);
        }
    }

    /// <summary>The expression is the name of a class (a definition or an import), or a class taken from a module: not a variable that merely holds some class.</summary>
    static bool NamesAClassDirectly(ModuleInfo m, Expr func, TypeEvaluator ev) => func switch
    {
        NameExpr n => m.Semantics.Resolve(n) is { } sym && sym.Declarations.Count > 0 && sym.Declarations.All(d => d.Kind is DeclKind.Class or DeclKind.Import),
        AttributeExpr a => ev.TypeOf(m, a.Value) is ModuleType,
        _ => false,
    };

    /// <summary>The abstract methods an instance of the class would still lack (empty when the class is not built on ABCMeta).</summary>
    static List<string> AbstractLeft(ClassInfo info, TypeEvaluator ev)
    {
        var result = new List<string>();
        if (KnownMro(info, ev) is not { } mro) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in mro)
            foreach (var (name, sym) in c.Info.Scope.Symbols)
            {
                if (sym.Declarations.Count == 0 || !seen.Add(name)) continue;
                if (sym.Declarations[^1].Node is FunctionDef fd && IsAbstractDef(fd) && UsesAbc(c.Info, ev)) result.Add(name);
            }
        return result;
    }

    /// <summary>`form.cleaned_data.get("titel")`: a Django form's cleaned_data read with a key no field gives (a TypedDict's own get may take any key).</summary>
    IEnumerable<Finding> CleanedDataGet(ModuleInfo m, Expr receiver, ConstExpr key, TypeEvaluator ev)
    {
        if (ev.TypeOf(m, receiver) is not ClassType { IsInstance: true } ct || !(ct.Info.Name.StartsWith("cleaned_data of ", StringComparison.Ordinal) || ct.Info.Name.StartsWith("validated_data of ", StringComparison.Ordinal))) return Array.Empty<Finding>();
        return TypedDictKey(m, ct, key, ev);
    }

    IEnumerable<Finding> TypedDictSubscript(ModuleInfo m, SubscriptExpr sub, TypeEvaluator ev)
    {
        if (sub.Index is not ConstExpr { Kind: ConstKind.Str, IsFString: false } key) return Array.Empty<Finding>();
        if (ev.TypeOf(m, sub.Value) is not ClassType { IsInstance: true } ct || ct.Info.Module.IsStub) return Array.Empty<Finding>();
        return TypedDictKey(m, ct, key, ev);
    }

    IEnumerable<Finding> TypedDictKey(ModuleInfo m, ClassType ct, ConstExpr key, TypeEvaluator ev)
    {
        if (ev.TypedDictFieldsOf(ct.Info) is not { } fields) yield break;
        string text = key.Text;
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length || text.Length - q < 2) yield break;
        string name = text[(q + 1)..^1];
        if (fields.Any(f => f.Field.Name == name)) yield break;
        // a Django form's cleaned_data has a key for each of the form's fields
        string message = ct.Info.Name.StartsWith("cleaned_data of ", StringComparison.Ordinal)
            ? $"{ct.Info.Name["cleaned_data of ".Length..]} has no field '{name}', so cleaned_data has no such key. Its fields are: {string.Join(", ", fields.Select(f => f.Field.Name).Take(12))}"
            // a REST framework serializer's validated_data holds the fields it writes
            : ct.Info.Name.StartsWith("validated_data of ", StringComparison.Ordinal)
            ? $"{ct.Info.Name["validated_data of ".Length..]} writes no field '{name}', so validated_data has no such key. The fields it writes are: {string.Join(", ", fields.Select(f => f.Field.Name).Take(12))}"
            : $"TypedDict '{ct.Info.Name}' has no key '{name}'";
        yield return new Finding(key.Start, Math.Max(key.End - key.Start, 1), message, "typeddict-keys", FindingLevel.Warning);
    }

    // ------------------------------------------------------------ overloads

    static bool IsOverloadDef(FunctionDef f) => f.Decorators.Any(d => d is NameExpr { Name: "overload" } or AttributeExpr { Attr: "overload" });

    /// <summary>`@overload` needs a second overload and an implementation after them (unless the body is a protocol or an abstract class, where the overloads may stand alone).</summary>
    static IEnumerable<Finding> OverloadStructure(IReadOnlyList<Stmt> body, bool mayLackImplementation)
    {
        foreach (var group in body.OfType<FunctionDef>().GroupBy(f => f.Name))
        {
            var overloads = group.Where(IsOverloadDef).ToList();
            if (overloads.Count == 0) continue;
            var first = overloads[0];
            if (overloads.Count == 1)
                yield return new Finding(first.NameStart, first.Name.Length, $"'{first.Name}' has only one @overload; it needs at least two", "overload-structure", FindingLevel.Warning);
            bool implemented = group.Any(f => !IsOverloadDef(f));
            if (!implemented && !mayLackImplementation && !group.Any(IsAbstractDef))
                yield return new Finding(overloads[^1].NameStart, overloads[^1].Name.Length, $"The @overload '{first.Name}' has no implementation after it", "overload-structure", FindingLevel.Warning);
        }
    }

    // ------------------------------------------------------------ fixes

    void StructureFix(string code, ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        switch (code)
        {
            case "dataclass-fields" or "namedtuple-fields":
                if (AstChildren.Descendants(m.Ast).OfType<AnnAssignStmt>().FirstOrDefault(a => a.Target.Start == start) is { } field)
                    MoveFieldFix(m, field, add);
                else
                    MutableDefaultFieldFix(m, start, add);
                break;
            case "typeddict-keys":
            {
                var call = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => c.Args.Any(a => a.Start == start && a.Name != null));
                if (call == null) break;
                int i = call.Args.ToList().FindIndex(a => a.Start == start);
                var arg = call.Args[i];
                Edit edit = call.Args.Count == 1 ? new Edit(arg.Start, arg.End, "")
                    : i + 1 < call.Args.Count ? new Edit(arg.Start, call.Args[i + 1].Start, "")
                    : new Edit(call.Args[i - 1].End, arg.End, "");
                add($"Remove the key '{arg.Name}'", true, new[] { edit });
                break;
            }
            case "overload-structure":
            {
                var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(d => d.NameStart == start);
                var dec = f?.Decorators.FirstOrDefault(d => d is NameExpr { Name: "overload" } or AttributeExpr { Attr: "overload" });
                if (f == null || dec == null || SiblingOverloads(m, f) != 1) break;   // only a lone overload has this fix
                string text = m.Source;
                int ls = LineStart(text, dec.Start), le = LineEndInclusive(text, dec.End);
                int at = text.LastIndexOf('@', dec.Start, dec.Start - ls + 1);   // the decorator node starts after the @
                bool whole = at >= ls && text[ls..at].All(c => c is ' ' or '\t') && text[dec.End..le].All(c => c is ' ' or '\t' or '\r' or '\n');
                if (whole) add("Remove the @overload decorator", true, new[] { new Edit(ls, le, "") });
                break;
            }
        }
    }

    /// <summary>How many @overload definitions of this name stand in the body of the function (itself included).</summary>
    static int SiblingOverloads(ModuleInfo m, FunctionDef f)
    {
        var parent = m.ParentOf(f);
        var body = parent == null ? null : BodyContaining(parent, f);
        return body == null ? 0 : body.OfType<FunctionDef>().Count(x => x.Name == f.Name && IsOverloadDef(x));
    }

    /// <summary>A field without a default moves above the first field that has one (its trailing comment goes along).</summary>
    void MoveFieldFix(ModuleInfo m, AnnAssignStmt field, Action<string, bool, Edit[]> add)
    {
        if (m.ParentOf(field) is not ClassDef cd || field.Target is not NameExpr name) return;
        var firstDefault = cd.Body.TakeWhile(s => !ReferenceEquals(s, field)).OfType<AnnAssignStmt>().FirstOrDefault(a => a.Value != null && a.Target is NameExpr);
        if (firstDefault == null) return;   // the default comes from a base class
        string text = m.Source;
        int ls = LineStart(text, field.Start), le = LineEndInclusive(text, field.End);
        if (!text[ls..field.Start].All(c => c is ' ' or '\t')) return;
        string tail = text[field.End..le].Trim();
        if (tail.Length > 0 && !tail.StartsWith('#')) return;
        string line = text[ls..le];
        if (!line.EndsWith('\n')) line += "\n";
        int to = LineStart(text, firstDefault.Start);
        add($"Move '{name.Name}' above '{((NameExpr)firstDefault.Target).Name}'", true, new[] { new Edit(to, to, line), new Edit(ls, le, "") });
    }

    /// <summary>`items: list = []` becomes `items: list = field(default_factory=list)` and `field(default=[])` becomes `field(default_factory=list)`.</summary>
    void MutableDefaultFieldFix(ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        var value = AstChildren.Descendants(m.Ast).OfType<Expr>().FirstOrDefault(e => e.Start == start && IsMutableDisplay(e));
        if (value == null) return;
        string text = m.Source, original = text[value.Start..value.End];
        string factory = value switch
        {
            ListExpr { Items.Count: 0 } or CallExpr { Func: NameExpr { Name: "list" } } => "list",
            DictExpr { Values.Count: 0 } or CallExpr { Func: NameExpr { Name: "dict" } } => "dict",
            CallExpr { Func: NameExpr { Name: "set" } } => "set",
            _ => "lambda: " + original,
        };
        var edits = new List<Edit>();
        var wrapper = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => DecoratorName(c) == "field" && c.Args.Any(a => ReferenceEquals(a.Value, value)));
        if (wrapper != null)
        {
            var arg = wrapper.Args.First(a => ReferenceEquals(a.Value, value));
            if (arg.Name != "default") return;
            edits.Add(new Edit(arg.Start, arg.End, "default_factory=" + factory));
        }
        else edits.Add(new Edit(value.Start, value.End, $"{FieldReference(m, edits)}(default_factory={factory})"));
        add("Use default_factory", true, edits.ToArray());
    }

    /// <summary>How `field` is written in this module; adds the import that makes it available when there is none.</summary>
    static string FieldReference(ModuleInfo m, List<Edit> edits)
    {
        var imports = m.Ast.Body.OfType<ImportFromStmt>().Where(s => s.Level == 0 && s.Module == "dataclasses").ToList();
        if (imports.Any(s => s.IsStar || s.Names.Any(a => a.Name == "field" && a.AsName == null))) return "field";
        if (imports.FirstOrDefault(s => s.Names.Count > 0) is { } from)
        {
            edits.Add(new Edit(from.Names[^1].End, from.Names[^1].End, ", field"));
            return "field";
        }
        if (m.Ast.Body.OfType<ImportStmt>().Any(s => s.Names.Any(a => a.Name == "dataclasses" && a.AsName == null))) return "dataclasses.field";
        // the dataclass decorator is written as `dataclasses.dataclass` or imported from dataclasses, so one of the cases above applies
        return "dataclasses.field";
    }
}
