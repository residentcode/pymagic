using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// Inspections PyCharm has that need the shape of the code more than its types: a class without __init__ anywhere in its hierarchy,
/// @classmethod / @staticmethod outside a class or applied before another decorator, a relative import with no package around it,
/// open() in text mode without an encoding, docstrings that are not triple double-quoted, missing or empty docstrings and missing
/// type hints (the last two off unless the profile turns them on, as in PyCharm).
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> PyCharmShapeDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var f in ShapeFindings(m, ev))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    IEnumerable<Finding> ShapeFindings(ModuleInfo m, TypeEvaluator ev)
    {
        string text = m.Source;
        Finding At(int start, int length, string message, string code, FindingLevel level = FindingLevel.Weak) => new(start, Math.Max(length, 1), message, code, level);

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ClassDef cd:
                    if (ClassWithoutInit(m, cd, ev)) yield return At(cd.NameStart, cd.Name.Length, $"Class '{cd.Name}' has no __init__ method, nor has any class it derives from", "class-no-init");
                    break;
                case FunctionDef fd:
                {
                    bool inClass = m.ParentOf(fd) is ClassDef;
                    for (int i = 0; i < fd.Decorators.Count; i++)
                    {
                        string? name = DecoratorName(fd.Decorators[i]);
                        if (name is not ("classmethod" or "staticmethod")) continue;
                        var d = fd.Decorators[i];
                        if (!inClass)
                            yield return At(d.Start, d.End - d.Start, $"@{name} is used outside a class: it only makes a method of a function defined in a class body", "decorator-outside-class", FindingLevel.Warning);
                        else if (i > 0 && DecoratorName(fd.Decorators[i - 1]) is { } outer && outer is not ("abstractmethod" or "override" or "overload"))
                            yield return At(d.Start, d.End - d.Start, $"@{name} is applied before @{outer}: @{outer} receives a {name} object, not the function; put @{name} on top", "decorator-nesting", FindingLevel.Warning);
                    }
                    break;
                }
                case ImportFromStmt { Level: > 0 } imp when RelativeImportProblem(m) is { } why:
                    yield return At(imp.Start, imp.End - imp.Start, why, "relative-import", FindingLevel.Warning);
                    break;
                case CallExpr call when IsTextOpenWithoutEncoding(m, ev, call):
                    yield return At(call.Func.Start, call.Func.End - call.Func.Start, "open() in text mode without an encoding: the file is read or written in the system's encoding (cp1252 on Windows); give encoding=\"utf-8\"", "open-encoding");
                    break;
            }
        }

        // docstrings: triple double quotes, not missing (off by default), not empty
        foreach (var (owner, body, nameStart, nameLength, kind) in DocstringOwners(m))
        {
            var doc = body.Count > 0 && body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } c } ? c : null;
            if (doc != null)
            {
                string raw = text[doc.Start..doc.End];
                string quotes = raw.TrimStart('r', 'R', 'u', 'U');
                if (!quotes.StartsWith("\"\"\"", StringComparison.Ordinal))
                    yield return At(doc.Start, doc.End - doc.Start, "The docstring is not in triple double quotes (\"\"\"...\"\"\")", "docstring-quotes");
                if (Pytest.FixtureModel.StringValue(doc) is { } value && value.Trim().Length == 0)
                    yield return At(doc.Start, doc.End - doc.Start, $"The docstring of this {kind} is empty", "missing-docstring");
            }
            else if (owner != null && !(owner is FunctionDef f && f.Decorators.Any(d => DecoratorName(d) is "overload" or "override")))
                yield return At(nameStart, nameLength, $"Missing docstring in this {kind}", "missing-docstring");
        }

        // type hints (off by default): every parameter but self / cls, and the return
        foreach (var fd in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            bool method = m.ParentOf(fd) is ClassDef && !fd.Decorators.Any(d => DecoratorName(d) == "staticmethod");
            var missing = fd.Params.Where((p, i) => p.Kind is ParamKind.Normal or ParamKind.VarArgs or ParamKind.KwArgs && p.Annotation == null && !(method && i == 0))
                .Select(p => p.Name).ToList();
            bool noReturn = fd.Returns == null && fd.Name != "__init__";
            if (missing.Count == 0 && !noReturn) continue;
            string what = string.Join(", ", missing.Select(n => $"'{n}'")) + (noReturn ? (missing.Count > 0 ? " and the return type" : "the return type") : "");
            yield return At(fd.NameStart, fd.Name.Length, $"Missing type hints: {what}", "missing-type-hint");
        }
    }

    /// <summary>The module, its public classes and functions (methods included): what a docstring belongs to.</summary>
    static IEnumerable<(Node? Owner, IReadOnlyList<Stmt> Body, int NameStart, int NameLength, string Kind)> DocstringOwners(ModuleInfo m)
    {
        if (m.Ast.Body.Count > 0 && m.Ast.Body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) yield return (null, m.Ast.Body, 0, 0, "module");
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            if (node is ClassDef cd && !cd.Name.StartsWith('_')) yield return (cd, cd.Body, cd.NameStart, cd.Name.Length, "class");
            else if (node is FunctionDef fd && !fd.Name.StartsWith('_') && m.ParentOf(fd) is Module or ClassDef)
                yield return (fd, fd.Body, fd.NameStart, fd.Name.Length, m.ParentOf(fd) is ClassDef ? "method" : "function");
        }
    }

    /// <summary>True for a class none of whose classes (object left out) defines __init__ or __new__, and that nothing else gives one (dataclasses, enums, named tuples, protocols).</summary>
    static bool ClassWithoutInit(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        if (cd.Decorators.Count > 0 || ClassInfoOf(m, cd, ev) is not { } info || KnownMro(info, ev) is not { } mro) return false;
        if (ev.IsDataclass(info) || ev.IsProtocol(info)) return false;
        foreach (var c in mro)
        {
            if (c.Info.Name == "object" && c.Info.Module.Name == "builtins") continue;
            if (c.Info.Name is "Enum" or "NamedTuple" or "TypedDict" or "tuple" or "type" or "Exception" or "BaseException" or "Protocol" or "Generic") return false;
            if (c.Info.Scope.Symbols.ContainsKey("__init__") || c.Info.Scope.Symbols.ContainsKey("__new__")) return false;
        }
        // a metaclass or __init_subclass__ machinery is beyond what this can see
        return !cd.Bases.Any(b => b.Name is "metaclass");
    }

    /// <summary>Why a relative import has no package to be relative to, or null when it has one.</summary>
    string? RelativeImportProblem(ModuleInfo m)
    {
        if (m.Package.Length == 0)
            return "A relative import in a module that is not in a package: Python stops with \"attempted relative import with no known parent package\"";
        if (Current().Workspace.GetModule(m.Package) is { Kind: ResolutionKind.NamespacePackage })
            return $"A relative import in '{m.Package}', a folder without __init__.py: it works only as a namespace package; add __init__.py";
        return null;
    }

    /// <summary>open(path) or open(path, "r" / "w" / ...) of the builtins, with no encoding, no "b" in the mode and no **kwargs.</summary>
    static bool IsTextOpenWithoutEncoding(ModuleInfo m, TypeEvaluator ev, CallExpr call)
    {
        if (call.Func is not NameExpr { Name: "open" } || call.Args.Any(a => a.Name == "encoding" || a.Stars > 0)) return false;
        // the builtin, overloaded in the stubs; a function of the project named open is another thing
        var callee = ev.TypeOf(m, call.Func) switch { FunctionType f => f, OverloadedType o => o.Overloads[0], _ => null };
        if (callee?.Info.Module.Name is not ("builtins" or "io")) return false;
        // encoding is open()'s fourth parameter
        if (call.Args.Count(a => a.Name == null) >= 4) return false;
        var mode = call.Args.FirstOrDefault(a => a.Name == "mode")?.Value ?? (call.Args.Count > 1 && call.Args[1].Name == null ? call.Args[1].Value : null);
        return mode == null || Pytest.FixtureModel.StringValue(mode) is { } text && !text.Contains('b');
    }

    /// <summary>The quick fixes of these inspections: encoding="utf-8" for open(), triple double quotes for a docstring.</summary>
    void PyCharmShapeFix(ModuleInfo m, string code, int start, int end, Action<string, bool, Edit[]> add)
    {
        string text = m.Source;
        switch (code)
        {
            case "open-encoding":
            {
                var call = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => c.Func.Start == start);
                if (call == null) break;
                int close = call.End - 1;
                if (close < 0 || text[close] != ')') break;
                string before = text[..close].TrimEnd();
                string sep = before.EndsWith('(') ? "" : before.EndsWith(',') ? " " : ", ";
                add("Add encoding=\"utf-8\"", true, new[] { new Edit(close, close, sep + "encoding=\"utf-8\"") });
                break;
            }
            case "docstring-quotes":
            {
                string raw = text[start..end];
                int prefix = raw.Length - raw.TrimStart('r', 'R', 'u', 'U').Length;
                string body = raw[prefix..];
                int q = body.StartsWith("'''", StringComparison.Ordinal) || body.StartsWith("\"\"\"", StringComparison.Ordinal) ? 3 : 1;
                string inner = body[q..^q];
                if (inner.Contains("\"\"\"") || inner.EndsWith('"')) break;
                add("Use triple double quotes", true, new[] { new Edit(start, end, raw[..prefix] + "\"\"\"" + inner + "\"\"\"") });
                break;
            }
        }
    }
}
