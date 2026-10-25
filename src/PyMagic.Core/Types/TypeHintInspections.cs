using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>PyCharm's "Invalid type hints definitions and usages": the typing constructs that are written wrongly, found from the syntax alone.</summary>
public static class TypeHintInspections
{
    const string Code = "type-hints";

    static readonly HashSet<string> BuiltinTypes = new()
    {
        "int", "str", "float", "bool", "bytes", "list", "dict", "set", "frozenset", "tuple", "object", "complex", "bytearray",
    };

    /// <summary>The generic classes with a fixed number of type arguments, builtin and typing spellings.</summary>
    static readonly Dictionary<string, int> Arity = new()
    {
        ["list"] = 1, ["set"] = 1, ["frozenset"] = 1, ["dict"] = 2, ["type"] = 1,
        ["List"] = 1, ["Set"] = 1, ["FrozenSet"] = 1, ["Dict"] = 2, ["Type"] = 1, ["Optional"] = 1,
        ["Sequence"] = 1, ["Iterable"] = 1, ["Iterator"] = 1, ["Mapping"] = 2, ["MutableMapping"] = 2,
    };

    static string? NameOf(Expr e) => e switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };

    static void Add(List<Finding> found, Node at, string message) =>
        found.Add(new Finding(at.Start, Math.Max(at.End - at.Start, 1), message, Code, FindingLevel.Warning));

    public static void Run(ModuleInfo m, List<Finding> found)
    {
        if (m.IsStub) return;
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case AssignStmt { Targets: [NameExpr target], Value: CallExpr { Func: var f, Args: { Count: > 0 } args } }
                    when NameOf(f) is "TypeVar" or "ParamSpec" or "TypeVarTuple"
                         && args[0] is { Name: null, Stars: 0, Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } label }
                         && Unquote(label.Text) is { } text && text != target.Name:
                    Add(found, label, $"The argument to {NameOf(f)}() must be a string equal to the variable name to which it is assigned");
                    break;

                case SubscriptExpr { Value: var head, Index: var index } sub when NameOf(head) is { } name:
                    Subscript(m, sub, name, index, found);
                    break;
            }
        }
    }

    static string? Unquote(string literal)
    {
        string t = literal.Trim();
        int q = 0;
        while (q < t.Length && char.IsLetter(t[q])) q++;
        if (q >= t.Length || t[q] is not ('"' or '\'')) return null;
        char quote = t[q];
        int run = t.Skip(q).TakeWhile(c => c == quote).Count();
        int qlen = run >= 6 ? 3 : 1;
        return t.Length >= q + 2 * qlen ? t.Substring(q + qlen, t.Length - q - 2 * qlen) : null;
    }

    static IReadOnlyList<Expr> Items(Expr index) => index is TupleExpr { Items: var items } ? items : new[] { index };

    static void Subscript(ModuleInfo m, SubscriptExpr sub, string name, Expr index, List<Finding> found)
    {
        var items = Items(index);
        // a name the file defines itself (its own `class Dict`) is not the generic of that name
        if (sub.Value is NameExpr n && m.Semantics.ReferenceOf(n) is { Symbol: { } sym })
        {
            foreach (var d in sym.Declarations)
            {
                if (d.Kind != Semantics.DeclKind.Import) return;
                if (m.ParentOf(d.Node) is not ImportFromStmt { Module: "typing" or "typing_extensions" or "collections.abc" }) return;
            }
        }

        if (name == "Optional" && items.Count != 1)
        {
            Add(found, index, "Optional[] must have exactly one type argument");
            return;
        }
        if (name == "Callable" && items.Count == 2 && IsPlainType(items[0]))
        {
            Add(found, items[0], "'Callable' must be used as 'Callable[[arg, ...], result]'");
            return;
        }
        if (name == "Literal")
        {
            foreach (var item in items)
                if (IsPlainType(item) || item is ListExpr or CallExpr or ConstExpr { Kind: ConstKind.Float or ConstKind.Imaginary })
                    Add(found, item, "'Literal' may be parameterized with literal ints, byte and unicode strings, bools, Enum values, None, other literal types, or type aliases to other literal types");
            return;
        }
        if (name == "Generic")
        {
            foreach (var item in items)
                if (IsPlainType(item) || item is ConstExpr)
                    Add(found, item, "Parameters to Generic[...] must all be type variables");
            return;
        }
        if (Arity.TryGetValue(name, out int expected) && name != "Optional" && items.All(i => i is not StarExpr))
        {
            // a builtin spelled without the typing module only when it really is the builtin
            if (sub.Value is NameExpr bn && char.IsLower(name[0]) && m.Semantics.ReferenceOf(bn) is { IsBuiltin: false, Symbol: not null }) return;
            if (items.Count > expected)
                Add(found, index, $"Too many type arguments: expected {expected}, got {items.Count}");
            else if (items.Count < expected && index is not TupleExpr)
                Add(found, index, $"Too few type arguments: expected {expected}, got {items.Count}");
        }
    }

    static bool IsPlainType(Expr e) => e is NameExpr { Name: var n } && BuiltinTypes.Contains(n);
}
