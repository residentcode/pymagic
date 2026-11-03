using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyModule = PyMagic.Core.Syntax.Module;

namespace PyMagic.Core.Types;

public enum FindingLevel { Error, Warning, Weak, Unused }

/// <summary>One finding of <see cref="CodeInspections"/>: a span, a message and the stable code the quick fixes key on.</summary>
public sealed record Finding(int Start, int Length, string Message, string Code, FindingLevel Level);

/// <summary>
/// The syntax-level inspections of PyCharm that need no type information: statements that are wrong where they stand,
/// comparisons and defaults that almost always are mistakes, and code that has no effect.
/// </summary>
public static class CodeInspections
{
    /// <param name="ev">When given, inspections that ask the flow graph take what types decide about it (see StyleInspections.Run).</param>
    public static List<Finding> Run(ModuleInfo m, TypeEvaluator? ev = null)
    {
        var found = new List<Finding>();
        if (m.IsStub) return found;

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ReturnStmt r: Outside(m, r, "return", found); break;
                case BreakStmt b: Outside(m, b, "break", found); break;
                case ContinueStmt c: Outside(m, c, "continue", found); break;
                case YieldExpr y: Outside(m, y, "yield", found); break;
                case CompareExpr c: Compare(m, c, found); break;
                case DictExpr d: DuplicateKeys(d, found); break;
                case ExceptHandler { Type: null } h:
                    found.Add(new Finding(h.Start, "except".Length, "Too broad exception clause", "bare-except", FindingLevel.Warning));
                    break;
                case FunctionDef f:
                    MutableDefaults(f.Params, found);
                    MissingSelf(m, f, found);
                    break;
                case LambdaExpr l: MutableDefaults(l.Params, found); break;
                case AssertStmt { Test: TupleExpr { Items.Count: > 0 } t }:
                    found.Add(new Finding(t.Start, Math.Max(t.End - t.Start, 1), "Assertion is always true, perhaps remove parentheses?",
                        "assert-tuple", FindingLevel.Warning));
                    break;
                case AssignStmt { Targets.Count: 1 } a when IsNoOpAssignment(m, a):
                    found.Add(new Finding(a.Start, Math.Max(a.End - a.Start, 1), "Assignment of a value to itself has no effect", "self-assignment", FindingLevel.Warning));
                    break;
            }
        }

        foreach (var scope in Scopes(m.Semantics.ModuleScope))
        {
            UnusedVariables(m, scope, found);
            UnusedParameters(m, scope, found);
            Redeclared(m, scope, found);
            Shadowing(m, scope, found);
            Naming(m, scope, found);
        }
        StyleInspections.Run(m, found, ev);
        TypeHintInspections.Run(m, found);
        MoreInspections.Run(m, found);
        return found;
    }

    /// <summary>A parameter node may start at its `*`; the name itself is what findings and renames point at.</summary>
    static int NameStartOf(ModuleInfo m, Parameter p)
    {
        int i = m.Source.IndexOf(p.Name, p.Start, StringComparison.Ordinal);
        return i >= 0 && i < p.End ? i : p.Start;
    }

    // ------------------------------------------------------------ unused parameters

    /// <summary>The class whose body defines this function, also when the def sits under an `if` or `try` inside it.</summary>
    static ClassDef? EnclosingClass(ModuleInfo m, FunctionDef f) =>
        m.Semantics.ScopeOf(f)?.Parent is { Kind: ScopeKind.Class, Node: ClassDef c } ? c : null;

    static readonly Regex DeliberatelyUnused = new(@"^(_|unused|dummy|ignored?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool HasBases(ClassDef c) => c.Bases.Any(b => b.Name == null && b.Stars == 0 && !(b.Value is NameExpr { Name: "object" }));

    /// <summary>A parameter nobody reads. Quiet where it is not the author's choice: overrides (a method of a class with bases),
    /// bodies that only stand in for an implementation, decorated functions (the decorator decides what is passed) and dunder methods.</summary>
    static void UnusedParameters(ModuleInfo m, Scope scope, List<Finding> found)
    {
        if (scope.Kind != ScopeKind.Function || scope.Node is not FunctionDef def) return;
        if (def.Name.StartsWith("__") && def.Name.EndsWith("__")) return;
        if (def.Name.StartsWith("test")) return;   // a test asks for fixtures it needs only for their effect
        if (def.Decorators.Any(d => d is not NameExpr { Name: "staticmethod" or "classmethod" })) return;
        var owner = EnclosingClass(m, def);
        if (owner != null && HasBases(owner)) return;
        if (IsPlaceholderBody(def.Body)) return;
        if (Regex.IsMatch(m.Source[def.Start..def.End], @"\b(locals|vars|eval|exec)\s*\(")) return;

        bool first = true;
        foreach (var p in def.Params)
        {
            if (p.Kind is ParamKind.Slash or ParamKind.KwOnlySeparator) continue;
            bool implicitFirst = first && owner != null && !def.Decorators.Any(d => d is NameExpr { Name: "staticmethod" });
            first = false;
            if (implicitFirst || DeliberatelyUnused.IsMatch(p.Name)) continue;
            if (!scope.Symbols.TryGetValue(p.Name, out var sym) || sym.Redirect != null) continue;
            if (sym.References.Any(r => (r.Access & Access.Read) != 0) || ChangedByNestedScope(scope, p.Name)) continue;
            if (sym.Declarations.Count == 0 || sym.Declarations[0].Kind != DeclKind.Parameter) continue;
            found.Add(new Finding(NameStartOf(m, p), p.Name.Length, $"Parameter '{p.Name}' value is not used", "unused-argument", FindingLevel.Unused));
        }
    }

    /// <summary>Only a docstring, `pass`, `...` or `raise NotImplementedError`: the function is a stub, its parameters describe a contract.</summary>
    static bool IsPlaceholderBody(IReadOnlyList<Stmt> body) => body.All(s => s switch
    {
        PassStmt => true,
        ExprStmt { Value: ConstExpr { Kind: ConstKind.Str or ConstKind.Ellipsis } } => true,
        RaiseStmt { Exc: NameExpr { Name: "NotImplementedError" } } => true,
        RaiseStmt { Exc: CallExpr { Func: NameExpr { Name: "NotImplementedError" } } } => true,
        _ => false,
    });

    // ------------------------------------------------------------ shadowed built-ins

    static readonly HashSet<string> TolerableBuiltins = new(StringComparer.Ordinal)
    {
        "copyright", "credits", "license", "exit", "quit", "help", "_",
    };

    /// <summary>A module-level or local name, parameter, function or class that hides a built-in (class bodies are left alone: attributes are reached through the class).</summary>
    static void Shadowing(ModuleInfo m, Scope scope, List<Finding> found)
    {
        if (scope.Kind is not (ScopeKind.Module or ScopeKind.Function)) return;
        foreach (var sym in scope.Symbols.Values)
        {
            string name = sym.Name;
            if (sym.Redirect != null || sym.Declarations.Count == 0 || TolerableBuiltins.Contains(name)) continue;
            if (name.StartsWith("__") && name.EndsWith("__")) continue;
            if (!Builtins.Names.Contains(name)) continue;
            var decl = sym.Declarations[0];
            (int Start, int Length)? at = decl.Kind switch
            {
                DeclKind.Parameter when decl.Node is Parameter dp => (NameStartOf(m, dp), name.Length),
                DeclKind.Function when decl.Node is FunctionDef f => (f.NameStart, name.Length),
                DeclKind.Class when decl.Node is ClassDef c => (c.NameStart, name.Length),
                DeclKind.Variable when IsPlainAssignment(m, decl.Node) && !IsRebindOfItself(m, decl.Node) => (decl.Node.Start, name.Length),
                _ => null,
            };
            if (at is { } a)
                found.Add(new Finding(a.Start, a.Length, $"Shadows built-in name '{name}'", "shadowed-builtin", FindingLevel.Weak));
        }
    }

    /// <summary>`BlockingIOError = BlockingIOError`, `input = raw_input`-style compatibility bindings read the built-in on purpose.</summary>
    static bool IsRebindOfItself(ModuleInfo m, Node target) =>
        m.ParentOf(target) is AssignStmt { Value: NameExpr v } && target is NameExpr n && v.Name == n.Name;

    // ------------------------------------------------------------ PEP 8 naming

    static readonly Regex LowerName = new(@"^_*[a-z0-9][a-z0-9_]*$|^_+$", RegexOptions.Compiled);
    static readonly Regex CapWords = new(@"^_*[A-Z][A-Za-z0-9]*_?$", RegexOptions.Compiled);

    public static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool afterLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool endsRun = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if ((afterLowerOrDigit || endsRun) && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string ToCapWords(string name)
    {
        string trimmed = name.TrimStart('_');
        string lead = name[..(name.Length - trimmed.Length)];
        var parts = trimmed.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return lead + string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    static void Naming(ModuleInfo m, Scope scope, List<Finding> found)
    {
        switch (scope.Node)
        {
            case ClassDef c when scope.Kind == ScopeKind.Class:
                if (!CapWords.IsMatch(c.Name) && !(c.Name.StartsWith("__") && c.Name.EndsWith("__")))
                    found.Add(new Finding(c.NameStart, c.Name.Length, "Class names should use CamelCase convention", "naming-class", FindingLevel.Weak));
                break;
            case FunctionDef f when scope.Kind == ScopeKind.Function:
            {
                var owner = EnclosingClass(m, f);
                bool override_ = owner != null && HasBases(owner);
                bool dunder = f.Name.StartsWith("__") && f.Name.EndsWith("__");
                if (!dunder && !override_ && !LowerName.IsMatch(f.Name))
                    found.Add(new Finding(f.NameStart, f.Name.Length, "Function name should be lowercase", "naming-function", FindingLevel.Weak));
                foreach (var p in f.Params)
                    if (p.Kind is not (ParamKind.Slash or ParamKind.KwOnlySeparator) && !LowerName.IsMatch(p.Name))
                        found.Add(new Finding(NameStartOf(m, p), p.Name.Length, "Argument name should be lowercase", "naming-argument", FindingLevel.Weak));
                foreach (var sym in scope.Symbols.Values)
                {
                    if (sym.Redirect != null || LowerName.IsMatch(sym.Name) || sym.Declarations.Count == 0) continue;
                    if (!sym.Declarations.All(d => d.Kind == DeclKind.Variable && IsPlainAssignment(m, d.Node) && !IsAlias(m, d.Node))) continue;
                    var first = sym.Declarations[0].Node;
                    found.Add(new Finding(first.Start, sym.Name.Length, "Variable in function should be lowercase", "naming-variable", FindingLevel.Weak));
                }
                break;
            }
        }
    }

    /// <summary>`Point = namedtuple(...)`, `Alias = SomeClass`: a name for a class or type may be CapWords anywhere.</summary>
    static bool IsAlias(ModuleInfo m, Node target) => m.ParentOf(target) switch
    {
        AssignStmt { Value: NameExpr or AttributeExpr or SubscriptExpr or CallExpr } => true,
        AnnAssignStmt => true,
        _ => false,
    };

    static IEnumerable<Scope> Scopes(Scope s)
    {
        yield return s;
        foreach (var c in s.Children)
            foreach (var x in Scopes(c)) yield return x;
    }

    // ------------------------------------------------------------ statements out of place

    static void Outside(ModuleInfo m, Node node, string keyword, List<Finding> found)
    {
        Node child = node;
        for (var p = m.ParentOf(node); p != null; child = p, p = m.ParentOf(p))
        {
            switch (p)
            {
                case FunctionDef or LambdaExpr when keyword is "return" or "yield":
                    return;
                case CompExpr when keyword == "yield": return;   // reported by the interpreter, with other wording
                case ForStmt f when keyword is "break" or "continue" && f.Body.Any(s => ReferenceEquals(s, child)): return;
                case WhileStmt w when keyword is "break" or "continue" && w.Body.Any(s => ReferenceEquals(s, child)): return;
                case FunctionDef or ClassDef or LambdaExpr: goto report;
            }
        }
    report:
        string where = keyword is "return" or "yield" ? "function" : "loop";
        found.Add(new Finding(node.Start, keyword.Length, $"'{keyword}' outside {where}", $"{keyword}-outside", FindingLevel.Error));
    }

    // ------------------------------------------------------------ comparisons

    static readonly Regex EqualityOperator = new(@"==|!=", RegexOptions.Compiled);

    static void Compare(ModuleInfo m, CompareExpr c, List<Finding> found)
    {
        for (int i = 0; i < c.Ops.Count; i++)
        {
            var left = i == 0 ? c.Left : c.Comparators[i - 1];
            var right = c.Comparators[i];
            string op = c.Ops[i];
            if (op is "==" or "!=" && (IsNone(left) || IsNone(right)))
                found.Add(new Finding(c.Start, Math.Max(c.End - c.Start, 1), "Comparison with None performed with equality operators", "none-comparison", FindingLevel.Warning));
            else if (op is "is" or "is not" && (IsLiteral(left) || IsLiteral(right)))
                found.Add(new Finding(c.Start, Math.Max(c.End - c.Start, 1), "Using 'is' with a literal: the result depends on how the value was created", "is-literal", FindingLevel.Warning));
        }
    }

    static bool IsNone(Expr e) => e is ConstExpr { Kind: ConstKind.None };

    static bool IsLiteral(Expr e) => e is ConstExpr { Kind: ConstKind.Int or ConstKind.Float or ConstKind.Imaginary or ConstKind.Str or ConstKind.Bytes, IsFString: false };

    /// <summary>The offset range of operator number <paramref name="index"/> of a comparison, found between its operands.</summary>
    public static (int Start, int End)? OperatorSpan(ModuleInfo m, CompareExpr c, int index)
    {
        var left = index == 0 ? c.Left : c.Comparators[index - 1];
        var right = c.Comparators[index];
        string op = c.Ops[index];
        string between = m.Source[left.End..Math.Max(left.End, right.Start)];
        var match = op is "==" or "!=" ? EqualityOperator.Match(between) : Regex.Match(between, op == "is" ? @"\bis\b(?!\s+not)" : @"\bis\s+not\b");
        return match.Success ? (left.End + match.Index, left.End + match.Index + match.Length) : null;
    }

    // ------------------------------------------------------------ dict literals

    static void DuplicateKeys(DictExpr d, List<Finding> found)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in d.Keys)
        {
            if (key is not ConstExpr { Kind: ConstKind.Int or ConstKind.Str or ConstKind.Bytes, IsFString: false } c) continue;
            string id = KeyId(c);
            if (id.Length == 0) continue;
            if (!seen.Add(id))
                found.Add(new Finding(c.Start, Math.Max(c.End - c.Start, 1), $"Duplicate key {c.Text} in the dict literal", "duplicate-key", FindingLevel.Warning));
        }
    }

    static string KeyId(ConstExpr c)
    {
        if (c.Kind == ConstKind.Int) return "i" + c.Text.Replace("_", "");
        var m = Regex.Match(c.Text, "^([bB]?)([\"'])(.*)\\2$", RegexOptions.Singleline);
        // anything with escapes, prefixes or implicit concatenation is not compared
        return m.Success && !m.Groups[3].Value.Contains('\\') && !m.Groups[3].Value.Contains(m.Groups[2].Value)
            ? "s" + m.Groups[1].Value.ToLowerInvariant() + m.Groups[3].Value : "";
    }

    // ------------------------------------------------------------ parameters

    static void MutableDefaults(IReadOnlyList<Parameter> parameters, List<Finding> found)
    {
        foreach (var p in parameters)
            if (p.Default != null && IsMutableValue(p.Default))
                found.Add(new Finding(p.Default.Start, Math.Max(p.Default.End - p.Default.Start, 1),
                    "Default argument value is mutable", "mutable-default", FindingLevel.Warning));
    }

    public static bool IsMutableValue(Expr e) => e switch
    {
        ListExpr or DictExpr or SetExpr => true,
        CompExpr { Kind: not CompKind.Generator } => true,
        CallExpr { Func: NameExpr { Name: "list" or "dict" or "set" or "bytearray" }, Args.Count: 0 } => true,
        _ => false,
    };

    static void MissingSelf(ModuleInfo m, FunctionDef f, List<Finding> found)
    {
        if (EnclosingClass(m, f) == null) return;
        if (f.Decorators.Count > 0 || f.Name is "__new__" or "__init_subclass__" or "__class_getitem__") return;
        if (f.Params.Count > 0 && f.Params[0].Kind != ParamKind.Slash && f.Params[0].Kind != ParamKind.KwOnlySeparator) return;
        found.Add(new Finding(f.NameStart, f.Name.Length, "Method must have a first parameter, usually called 'self'", "missing-self", FindingLevel.Warning));
    }

    // ------------------------------------------------------------ no effect

    /// <summary>`self.a = self.a`, or `x = x` where x already has a value. In a class body `x = x` copies an outer name in, and
    /// when this is the only binding of the name (`BlockingIOError = BlockingIOError`) it rebinds a builtin or imported name: neither is a no-op.</summary>
    static bool IsNoOpAssignment(ModuleInfo m, AssignStmt a)
    {
        if (!SameReference(a.Targets[0], a.Value)) return false;
        if (a.Targets[0] is not NameExpr name) return true;
        var scope = m.Semantics.InnermostScopeAt(a.Start);
        return scope.Kind != ScopeKind.Class && scope.Symbols.TryGetValue(name.Name, out var sym) && sym.Declarations.Count > 1;
    }

    static bool SameReference(Expr a, Expr b) => (a, b) switch
    {
        (NameExpr x, NameExpr y) => x.Name == y.Name,
        (AttributeExpr x, AttributeExpr y) => x.Attr == y.Attr && SameReference(x.Value, y.Value),
        _ => false,
    };

    // ------------------------------------------------------------ names

    /// <summary>`x = value` in a function where x is never read.</summary>
    static void UnusedVariables(ModuleInfo m, Scope scope, List<Finding> found)
    {
        if (scope.Kind != ScopeKind.Function) return;
        var def = scope.Node as FunctionDef;
        if (def != null && Regex.IsMatch(m.Source[def.Start..def.End], @"\b(locals|vars|eval|exec)\s*\(")) return;

        foreach (var sym in scope.Symbols.Values)
        {
            if (sym.Redirect != null || DeliberatelyUnused.IsMatch(sym.Name) || sym.Declarations.Count == 0) continue;
            if (sym.References.Any(r => (r.Access & Access.Read) != 0)) continue;
            if (ChangedByNestedScope(scope, sym.Name)) continue;
            if (!sym.Declarations.All(d => d.Kind == DeclKind.Variable && IsPlainAssignment(m, d.Node))) continue;
            foreach (var d in sym.Declarations)
                found.Add(new Finding(d.Node.Start, Math.Max(d.Node.End - d.Node.Start, 1), $"Local variable '{sym.Name}' value is not used", "unused-variable", FindingLevel.Unused));
        }
    }

    /// <summary>A nested function says `nonlocal name`: the variable is shared with it, so a value nobody reads here may still matter.</summary>
    static bool ChangedByNestedScope(Scope scope, string name) =>
        scope.Children.Any(c => c.NonlocalNames.Contains(name) || ChangedByNestedScope(c, name));

    /// <summary>The name is the whole target of `x = ...` or `x: T = ...`, not a tuple element, loop variable or augmented target.</summary>
    public static bool IsPlainAssignment(ModuleInfo m, Node target) => target is NameExpr && m.ParentOf(target) switch
    {
        AssignStmt a => a.Targets.Any(t => ReferenceEquals(t, target)),
        AnnAssignStmt a => ReferenceEquals(a.Target, target) && a.Value != null,
        _ => false,
    };

    /// <summary>`def f` twice in a row of the same block with no use of f between them.</summary>
    static void Redeclared(ModuleInfo m, Scope scope, List<Finding> found)
    {
        foreach (var sym in scope.Symbols.Values)
        {
            if (sym.Redirect != null || sym.Declarations.Count < 2) continue;
            for (int i = 1; i < sym.Declarations.Count; i++)
            {
                var a = sym.Declarations[i - 1].Node;
                var b = sym.Declarations[i].Node;
                if (!SameKindOfDefinition(a, b) || Decorators(a).Count > 0 || Decorators(b).Count > 0) continue;
                var parent = m.ParentOf(a);
                if (parent == null || !ReferenceEquals(parent, m.ParentOf(b))) continue;
                var listA = Block(parent, (Stmt)a);
                if (listA == null || !ReferenceEquals(listA, Block(parent, (Stmt)b))) continue;
                if (sym.References.Any(r => r.Node.Start >= a.Start && r.Node.Start < b.End)) continue;
                var (start, length) = b is FunctionDef fb ? (fb.NameStart, fb.Name.Length) : (((ClassDef)b).NameStart, ((ClassDef)b).Name.Length);
                found.Add(new Finding(start, length, $"Redeclared '{sym.Name}' defined above without usage", "redeclared", FindingLevel.Warning));
            }
        }
    }

    static bool SameKindOfDefinition(Node a, Node b) => a is FunctionDef && b is FunctionDef || a is ClassDef && b is ClassDef;

    static IReadOnlyList<Expr> Decorators(Node n) => n switch { FunctionDef f => f.Decorators, ClassDef c => c.Decorators, _ => Array.Empty<Expr>() };

    static IReadOnlyList<Stmt>? Block(Node parent, Stmt s)
    {
        bool In(IReadOnlyList<Stmt> l) => l.Any(x => ReferenceEquals(x, s));
        IReadOnlyList<Stmt>? Pick(params IReadOnlyList<Stmt>[] lists) => lists.FirstOrDefault(In);
        return parent switch
        {
            PyModule x => x.Body,
            IfStmt x => Pick(x.Body, x.Orelse),
            WhileStmt x => Pick(x.Body, x.Orelse),
            ForStmt x => Pick(x.Body, x.Orelse),
            TryStmt x => Pick(x.Body, x.Orelse, x.Finally),
            ExceptHandler x => x.Body,
            WithStmt x => x.Body,
            FunctionDef x => x.Body,
            ClassDef x => x.Body,
            MatchCase x => x.Body,
            _ => null,
        };
    }
}
