using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyModule = PyMagic.Core.Syntax.Module;

namespace PyMagic.Core.Types;

/// <summary>
/// More of PyCharm's inspections that need no type information: too broad exceptions, names that hide a name of an outer scope,
/// the first parameter of a method, <c>__slots__</c>, <c>from __future__</c> out of place, mixed indentation, non-ASCII bytes,
/// properties whose accessors have the wrong shape, <c>__new__</c> and <c>__init__</c> that do not fit, and docstrings that name
/// parameters the function does not have.
/// </summary>
public static class MoreInspections
{
    public static void Run(ModuleInfo m, List<Finding> found)
    {
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ExceptHandler h: BroadException(h, found); break;
                case ConstExpr { Kind: ConstKind.Bytes } c: NonAsciiBytes(m, c, found); break;
                case ClassDef c: Slots(m, c, found); InitNew(m, c, found); break;
                case FunctionDef f:
                    MethodParameters(m, f, found);
                    PropertyAccessors(m, f, found);
                    DocstringParameters(m, f, found);
                    break;
            }
        }
        FutureImports(m, found);
        Indentation(m, found);
        foreach (var scope in Walk(m.Semantics.ModuleScope))
            ShadowedNames(m, scope, found);
    }

    static IEnumerable<Scope> Walk(Scope s)
    {
        yield return s;
        foreach (var c in s.Children)
            foreach (var x in Walk(c)) yield return x;
    }

    static int NameStartOf(ModuleInfo m, Parameter p)
    {
        int i = m.Source.IndexOf(p.Name, p.Start, StringComparison.Ordinal);
        return i >= 0 && i < p.End ? i : p.Start;
    }

    static ClassDef? OwnerOf(ModuleInfo m, FunctionDef f) =>
        m.Semantics.ScopeOf(f)?.Parent is { Kind: ScopeKind.Class, Node: ClassDef c } ? c : null;

    static string DecoratorName(Expr d) => d switch
    {
        NameExpr n => n.Name,
        AttributeExpr a => a.Attr,
        CallExpr { Func: var f } => DecoratorName(f),
        _ => "",
    };

    // ------------------------------------------------------------ too broad exceptions

    /// <summary>`except Exception:` and `except BaseException:` that swallow what they catch (a clause that raises again is a cleanup).</summary>
    static void BroadException(ExceptHandler h, List<Finding> found)
    {
        if (h.Type == null) return;   // a bare clause has its own inspection
        bool broad = h.Type switch
        {
            NameExpr { Name: "Exception" or "BaseException" } => true,
            TupleExpr t => t.Items.Any(i => i is NameExpr { Name: "Exception" or "BaseException" }),
            _ => false,
        };
        if (!broad) return;
        if (h.Body.SelectMany(s => AstChildren.Descendants(s)).Any(n => n is RaiseStmt)) return;
        found.Add(new Finding(h.Type.Start, Math.Max(h.Type.End - h.Type.Start, 1), "Too broad exception clause", "broad-exception", FindingLevel.Weak));
    }

    // ------------------------------------------------------------ names of outer scopes

    /// <summary>A parameter or local variable of a function with the name of a variable, function, class or import of the module or of an enclosing function.</summary>
    static void ShadowedNames(ModuleInfo m, Scope scope, List<Finding> found)
    {
        if (scope.Kind != ScopeKind.Function || scope.Node is not FunctionDef) return;
        foreach (var sym in scope.Symbols.Values)
        {
            string name = sym.Name;
            if (sym.Redirect != null || sym.Declarations.Count == 0 || name == "_" || (name.StartsWith("__") && name.EndsWith("__"))) continue;
            if (Builtins.Names.Contains(name)) continue;   // shadowed-builtin reports those
            var decl = sym.Declarations[0];
            if (decl.Kind is not (DeclKind.Parameter or DeclKind.Variable)) continue;
            if (decl.Kind == DeclKind.Variable && !CodeInspections.IsPlainAssignment(m, decl.Node)) continue;
            if (name is "self" or "cls") continue;

            Scope? outer = null;
            for (var p = scope.Parent; p != null; p = p.Parent)
            {
                if (p.Kind == ScopeKind.Class) continue;   // class attributes are reached through the class
                if (p.Kind is not (ScopeKind.Module or ScopeKind.Function)) continue;
                if (p.Symbols.TryGetValue(name, out var o) && o.Redirect == null && o.Declarations.Count > 0) { outer = p; break; }
            }
            if (outer == null) continue;

            int at = decl.Kind == DeclKind.Parameter && decl.Node is Parameter dp ? NameStartOf(m, dp) : decl.Node.Start;
            found.Add(new Finding(at, name.Length, $"Shadows name '{name}' from outer scope", "shadowing-names", FindingLevel.Weak));
        }
    }

    // ------------------------------------------------------------ first parameter of a method

    static readonly HashSet<string> PlainDecorators = new(StringComparer.Ordinal)
    {
        "property", "setter", "getter", "deleter", "abstractmethod", "classmethod", "staticmethod", "override", "final", "cached_property", "overload",
    };

    static bool ExtendsType(ClassDef c) => c.Bases.Any(b => b.Name == null && b.Value is NameExpr { Name: "type" or "ABCMeta" or "EnumMeta" or "EnumType" });

    /// <summary>`def f(this):` in a class: an instance method takes `self`, a class method `cls` (a metaclass's methods `cls`, `mcs` or `metacls`).</summary>
    static void MethodParameters(ModuleInfo m, FunctionDef f, List<Finding> found)
    {
        if (OwnerOf(m, f) is not { } owner) return;
        var decorators = f.Decorators.Select(DecoratorName).ToList();
        if (decorators.Any(d => !PlainDecorators.Contains(d))) return;   // another decorator decides what is passed
        if (decorators.Contains("staticmethod")) return;
        var first = f.Params.FirstOrDefault();
        if (first == null || first.Kind != ParamKind.Normal) return;
        if (UsedInClassBody(owner, f)) return;   // a helper of the class body (`__add__, __radd__ = _fallbacks(_add)`), not a method
        string name = first.Name;
        bool classLevel = decorators.Contains("classmethod") || f.Name is "__new__" or "__init_subclass__" or "__class_getitem__";
        bool meta = ExtendsType(owner);
        string expected = classLevel ? "cls" : "self";
        bool fine = name.StartsWith('_') || (classLevel ? name is "cls" or "mcs" or "metacls" or "mcls" or "klass" : name == "self" || (meta && name is "cls" or "mcs" or "metacls" or "mcls"));
        if (fine) return;
        found.Add(new Finding(NameStartOf(m, first), name.Length,
            classLevel ? $"Usually first parameter of a class method should be named 'cls'" : $"Usually first parameter of a method is named 'self'", "method-parameters", FindingLevel.Warning));
        _ = expected;
    }

    /// <summary>The name of the function is read by statements of the class body itself, outside the functions defined there.</summary>
    static bool UsedInClassBody(ClassDef owner, FunctionDef f)
    {
        var stack = new Stack<Node>(owner.Body.Where(s => s is not (FunctionDef or ClassDef)).Cast<Node>());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is NameExpr ne && ne.Name == f.Name) return true;
            if (n is FunctionDef or ClassDef or LambdaExpr) continue;
            foreach (var c in AstChildren.Of(n)) stack.Push(c);
        }
        return false;
    }

    // ------------------------------------------------------------ __slots__

    static IReadOnlyList<ConstExpr>? SlotNames(ClassDef c, out Stmt? statement)
    {
        statement = null;
        foreach (var s in c.Body)
        {
            if (s is not AssignStmt { Targets: [NameExpr { Name: "__slots__" }] } a) continue;
            var items = a.Value switch
            {
                TupleExpr t => t.Items,
                ListExpr l => l.Items,
                ConstExpr { Kind: ConstKind.Str } one => new Expr[] { one },
                _ => null,
            };
            if (items == null || !items.All(i => i is ConstExpr { Kind: ConstKind.Str, IsFString: false })) return null;
            statement = s;
            return items.Cast<ConstExpr>().ToList();
        }
        return null;
    }

    static string Unquote(string raw)
    {
        int q = 0;
        while (q < raw.Length && raw[q] != '"' && raw[q] != '\'') q++;
        return raw.Length >= q + 2 ? raw[(q + 1)..^1] : raw;
    }

    /// <summary>A slot that is also a class variable (an error at class creation), and `self.x = ...` for an x that is not a slot of a class with no bases.</summary>
    static void Slots(ModuleInfo m, ClassDef c, List<Finding> found)
    {
        if (SlotNames(c, out _) is not { } slots) return;
        var names = slots.Select(s => Unquote(s.Text)).ToList();

        var classVariables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in c.Body)
        {
            switch (s)
            {
                case AssignStmt a when !(a.Targets is [NameExpr { Name: "__slots__" }]):
                    foreach (var t in a.Targets.OfType<NameExpr>()) classVariables.Add(t.Name);
                    break;
                case AnnAssignStmt { Target: NameExpr n, Value: not null }: classVariables.Add(n.Name); break;
            }
        }
        foreach (var slot in slots)
        {
            string name = Unquote(slot.Text);
            if (classVariables.Contains(name))
                found.Add(new Finding(slot.Start, Math.Max(slot.End - slot.Start, 1), $"'{name}' in __slots__ conflicts with a class variable", "dunder-slots", FindingLevel.Error));
        }

        // attributes assigned to self that no slot provides: only certain when nothing else can supply a __dict__
        bool noBases = c.Bases.All(b => b.Name == null && b.Value is NameExpr { Name: "object" });
        if (!noBases || names.Contains("__dict__")) return;
        var declared = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var s in c.Body)
        {
            if (s is FunctionDef or ClassDef) declared.Add(s is FunctionDef fd ? fd.Name : ((ClassDef)s).Name);
        }
        foreach (var f in c.Body.OfType<FunctionDef>())
        {
            if (f.Params.Count == 0 || f.Decorators.Count > 0) continue;
            string self = f.Params[0].Name;
            foreach (var n in AstChildren.Descendants(f))
            {
                if (n is not AttributeExpr { Value: NameExpr v } attr || v.Name != self) continue;
                if (m.ParentOf(attr) is not (AssignStmt or AugAssignStmt or AnnAssignStmt)) continue;
                bool isTarget = m.ParentOf(attr) switch
                {
                    AssignStmt a => a.Targets.Any(t => ReferenceEquals(t, attr)),
                    AugAssignStmt a => ReferenceEquals(a.Target, attr),
                    AnnAssignStmt a => ReferenceEquals(a.Target, attr),
                    _ => false,
                };
                if (!isTarget || declared.Contains(attr.Attr) || classVariables.Contains(attr.Attr)) continue;
                int at = m.Source.LastIndexOf(attr.Attr, attr.End - 1, attr.Attr.Length, StringComparison.Ordinal);
                found.Add(new Finding(at < 0 ? attr.Start : at, attr.Attr.Length, $"'{c.Name}' object has no attribute '{attr.Attr}' and __slots__ does not list it", "dunder-slots", FindingLevel.Warning));
            }
        }
    }

    // ------------------------------------------------------------ from __future__

    static bool IsDocstringStmt(Stmt s) => s is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } };

    /// <summary>`from __future__ import x` anywhere but the top of the file (after the docstring and other future imports) is a syntax error.</summary>
    static void FutureImports(ModuleInfo m, List<Finding> found)
    {
        bool onlyHeader = true;
        for (int i = 0; i < m.Ast.Body.Count; i++)
        {
            var s = m.Ast.Body[i];
            if (s is ImportFromStmt { Level: 0, Module: "__future__" })
            {
                if (!onlyHeader)
                    found.Add(new Finding(s.Start, Math.Max(s.End - s.Start, 1), "from __future__ imports must occur at the beginning of the file", "from-future", FindingLevel.Error));
                continue;
            }
            if (i == 0 && IsDocstringStmt(s)) continue;
            onlyHeader = false;
        }
    }

    // ------------------------------------------------------------ mixed indentation

    /// <summary>Statements of one block that are indented some with tabs and some with spaces, or a line that mixes both.</summary>
    static void Indentation(ModuleInfo m, List<Finding> found)
    {
        if (!m.Source.Contains('\t')) return;
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            IReadOnlyList<Stmt>? body = node switch
            {
                PyModule mod => mod.Body,
                FunctionDef f => f.Body,
                ClassDef c => c.Body,
                IfStmt i => i.Body,
                ForStmt f => f.Body,
                WhileStmt w => w.Body,
                WithStmt w => w.Body,
                TryStmt t => t.Body,
                _ => null,
            };
            if (body == null) continue;
            string? firstKind = null;
            foreach (var s in body)
            {
                if (LeadingWhitespace(m.Source, s.Start) is not { } ws || ws.Length == 0) continue;
                string kind = ws.Contains('\t') && ws.Contains(' ') ? "mixed" : ws.Contains('\t') ? "tab" : "space";
                firstKind ??= kind;
                if (kind == "mixed" || kind != firstKind)
                {
                    int ls = s.Start - ws.Length;
                    found.Add(new Finding(ls, ws.Length, "Inconsistent indentation: mix of tabs and spaces", "inconsistent-indentation", FindingLevel.Warning));
                }
            }
        }
    }

    /// <summary>The whitespace before the statement on its line, or null when something else comes first (a `;` or `if x: y` follows another statement).</summary>
    static string? LeadingWhitespace(string text, int start)
    {
        int i = start;
        while (i > 0 && text[i - 1] is ' ' or '\t') i--;
        if (i > 0 && text[i - 1] is not ('\n' or '\r')) return null;
        return text[i..start];
    }

    // ------------------------------------------------------------ bytes

    static void NonAsciiBytes(ModuleInfo m, ConstExpr c, List<Finding> found)
    {
        string raw = m.Source[c.Start..Math.Min(c.End, m.Source.Length)];
        if (raw.Any(ch => ch > 127))
            found.Add(new Finding(c.Start, Math.Max(c.End - c.Start, 1), "Only ASCII characters are allowed in bytes literals", "byte-literal", FindingLevel.Error));
    }

    // ------------------------------------------------------------ properties

    static IEnumerable<string> Accessor(FunctionDef f)
    {
        foreach (var d in f.Decorators)
        {
            if (d is NameExpr { Name: "property" }) yield return "getter";
            else if (d is AttributeExpr { Attr: "setter" or "getter" or "deleter" } a) yield return a.Attr;
        }
    }

    /// <summary>A `return value` or a `yield` of the function itself (not of the functions defined inside it).</summary>
    static bool ReturnsOrYields(FunctionDef f)
    {
        var stack = new Stack<Node>(f.Body.Cast<Node>());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is ReturnStmt { Value: not null } or YieldExpr) return true;
            if (n is FunctionDef or ClassDef or LambdaExpr) continue;
            foreach (var c in AstChildren.Of(n)) stack.Push(c);
        }
        return false;
    }

    static bool IsStub(FunctionDef f) => f.Body.All(s => s is PassStmt or ExprStmt { Value: ConstExpr { Kind: ConstKind.Str or ConstKind.Ellipsis } } or RaiseStmt);

    /// <summary>A getter takes only `self` and returns something, a setter takes `self` and the value and returns nothing, a deleter takes only `self`.</summary>
    static void PropertyAccessors(ModuleInfo m, FunctionDef f, List<Finding> found)
    {
        if (OwnerOf(m, f) == null) return;
        var kinds = Accessor(f).ToList();
        if (kinds.Count != 1) return;
        if (f.Decorators.Any(d => !PlainDecorators.Contains(DecoratorName(d)))) return;
        var positional = f.Params.Where(p => p.Kind == ParamKind.Normal).ToList();
        if (f.Params.Any(p => p.Kind == ParamKind.VarArgs)) return;
        string kind = kinds[0];
        bool returnsValue = ReturnsOrYields(f);
        int required = kind == "setter" ? 2 : 1;
        int takes = positional.Count(p => p.Default == null);
        if (takes > required || positional.Count < required)
        {
            string signature = kind == "setter" ? "(self, value)" : "(self)";
            found.Add(new Finding(f.NameStart, f.Name.Length, $"{char.ToUpperInvariant(kind[0])}{kind[1..]} signature should be {signature}", "property-definition", FindingLevel.Warning));
            return;
        }
        bool abstractOrStub = f.Decorators.Any(d => DecoratorName(d) == "abstractmethod") || IsStub(f) || f.Body.Any(s => s is RaiseStmt);
        if (kind == "getter" && !returnsValue && !abstractOrStub)
            found.Add(new Finding(f.NameStart, f.Name.Length, "Getter should return or yield something", "property-definition", FindingLevel.Warning));
        else if (kind == "setter" && returnsValue)
            found.Add(new Finding(f.NameStart, f.Name.Length, "Setter should not return a value", "property-definition", FindingLevel.Warning));
        else if (kind == "deleter" && returnsValue)
            found.Add(new Finding(f.NameStart, f.Name.Length, "Deleter should not return anything", "property-definition", FindingLevel.Warning));
    }

    // ------------------------------------------------------------ __new__ and __init__

    static string Shape(FunctionDef f) => string.Join(",", f.Params.Skip(1).Select(p => p.Kind switch
    {
        ParamKind.VarArgs => "*",
        ParamKind.KwArgs => "**",
        ParamKind.Slash => "/",
        ParamKind.KwOnlySeparator => "*",
        _ => p.Name + (p.Default != null ? "=" : ""),
    }));

    /// <summary>A class that defines both: Python calls `__new__` and then `__init__` with the same arguments, so each must accept what the other does.</summary>
    static void InitNew(ModuleInfo m, ClassDef c, List<Finding> found)
    {
        var init = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
        var @new = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__new__");
        if (init == null || @new == null) return;
        bool Open(FunctionDef f) => f.Params.Any(p => p.Kind == ParamKind.VarArgs) && f.Params.Any(p => p.Kind == ParamKind.KwArgs);
        if (Open(init) || Open(@new)) return;
        if (Shape(init) == Shape(@new)) return;
        found.Add(new Finding(init.NameStart, init.Name.Length, "Signature is not compatible to __new__", "init-new-signature", FindingLevel.Warning));
    }

    // ------------------------------------------------------------ docstrings

    static readonly Regex SphinxParam = new(@":(?:param|parameter|arg|argument|key|keyword)\s+(?:[^:\n]*?\s+)?\**(?<n>[A-Za-z_]\w*)\s*:", RegexOptions.Compiled);
    static readonly Regex GoogleHeader = new(@"^(?<i>[ \t]*)(?:Args|Arguments|Parameters|Params)\s*:\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex GoogleEntry = new(@"^(?<i>[ \t]+)\**(?<n>[A-Za-z_]\w*)\s*(?:\([^)\n]*\))?\s*:", RegexOptions.Compiled);

    /// <summary>The parameter names a docstring documents in the Sphinx (`:param x:`) and Google (`Args:`) styles, each with the offset of its name in the text.</summary>
    public static List<(string Name, int Offset)> DocumentedParameters(string doc)
    {
        var result = new List<(string, int)>();
        foreach (Match mt in SphinxParam.Matches(doc))
            result.Add((mt.Groups["n"].Value, mt.Groups["n"].Index));

        var header = GoogleHeader.Match(doc);
        if (header.Success)
        {
            int headerIndent = header.Groups["i"].Length;
            int? entryIndent = null;
            int offset = header.Index + header.Length;
            var lines = doc[offset..].Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineStart = offset;
                offset += line.Length + 1;
                if (i == 0 || line.Trim().Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;
                if (indent <= headerIndent) break;
                entryIndent ??= indent;
                if (indent != entryIndent) continue;
                var e = GoogleEntry.Match(line);
                if (e.Success) result.Add((e.Groups["n"].Value, lineStart + e.Groups["n"].Index));
            }
        }
        return result;
    }

    /// <summary>A parameter the docstring documents and the signature does not have, and (when it documents any) one it leaves out.</summary>
    static void DocstringParameters(ModuleInfo m, FunctionDef f, List<Finding> found)
    {
        if (f.Body.Count == 0 || f.Body[0] is not ExprStmt { Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } doc }) return;
        string raw = m.Source[doc.Start..Math.Min(doc.End, m.Source.Length)];
        var documented = DocumentedParameters(raw);
        if (documented.Count == 0) return;

        bool method = OwnerOf(m, f) != null && !f.Decorators.Any(d => DecoratorName(d) == "staticmethod");
        var actual = f.Params.Where(p => p.Kind is ParamKind.Normal or ParamKind.VarArgs or ParamKind.KwArgs).Select(p => p.Name).ToList();
        if (method && actual.Count > 0) actual.RemoveAt(0);
        var names = new HashSet<string>(actual, StringComparer.Ordinal);

        bool takesKeywords = f.Params.Any(p => p.Kind == ParamKind.KwArgs);   // a documented keyword may be read from **kwargs
        foreach (var (name, offset) in documented)
            if (!names.Contains(name) && !takesKeywords)
                found.Add(new Finding(doc.Start + offset, name.Length, $"Unexpected parameter '{name}' in docstring", "docstring-parameter", FindingLevel.Warning));

        var seen = new HashSet<string>(documented.Select(d => d.Name), StringComparer.Ordinal);
        foreach (var p in f.Params.Where(p => p.Kind == ParamKind.Normal).Skip(method ? 1 : 0))
            if (!seen.Contains(p.Name) && !p.Name.StartsWith('_'))
                found.Add(new Finding(NameStartOf(m, p), p.Name.Length, $"Missing parameter '{p.Name}' in docstring", "docstring-missing-parameter", FindingLevel.Weak));
    }
}
