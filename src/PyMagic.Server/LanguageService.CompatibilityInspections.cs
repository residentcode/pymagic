using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// PyCharm's inspections of versions and documentation: code that needs a newer Python than the project supports (requires-python in
/// pyproject.toml, else the interpreter) or a module later Pythons removed, uses of what is deprecated (`@deprecated` in the project or
/// the stubs, deprecated standard modules), and a docstring type that is not the annotation's.
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>The oldest Python the project supports, and where that was read; null when neither pyproject.toml nor an interpreter says.</summary>
    (int Major, int Minor, string Source)? SupportedPython()
    {
        if (SourceRoots.Count > 0 && System.IO.Path.Combine(SourceRoots[0], "pyproject.toml") is var path && File.Exists(path))
        {
            string text;
            try { text = File.ReadAllText(path); } catch (IOException) { text = ""; }
            var m = Regex.Match(text, @"^\s*requires-python\s*=\s*[""'](?<spec>\s*(?:>=|~=|==|\^)?\s*(?<maj>\d+)\.(?<min>\d+)[^""']*)", RegexOptions.Multiline);
            string label = "requires-python";
            // Poetry's python = "^3.9" under [tool.poetry.dependencies]
            if (!m.Success) { m = Regex.Match(text, @"^\s*python\s*=\s*[""'](?<spec>\s*(?:>=|~=|==|\^)?\s*(?<maj>\d+)\.(?<min>\d+)[^""']*)", RegexOptions.Multiline); label = "Poetry's python"; }
            if (m.Success) return (int.Parse(m.Groups["maj"].Value), int.Parse(m.Groups["min"].Value), $"{label} {m.Groups["spec"].Value.Trim()}");
        }
        return Interpreter is { } i ? (i.Major, i.Minor, $"the interpreter, Python {i.Major}.{i.Minor}") : null;
    }

    /// <summary>Standard names added after 3.8, with the version that added them: what an import of them needs.</summary>
    static readonly Dictionary<string, (int Major, int Minor)> AddedNames = new(StringComparer.Ordinal)
    {
        ["tomllib"] = (3, 11), ["zoneinfo"] = (3, 9), ["graphlib"] = (3, 9),
        ["typing.Self"] = (3, 11), ["typing.Never"] = (3, 11), ["typing.LiteralString"] = (3, 11), ["typing.assert_never"] = (3, 11), ["typing.reveal_type"] = (3, 11),
        ["typing.Required"] = (3, 11), ["typing.NotRequired"] = (3, 11), ["typing.Unpack"] = (3, 11), ["typing.TypeVarTuple"] = (3, 11), ["typing.dataclass_transform"] = (3, 11),
        ["typing.assert_type"] = (3, 11), ["typing.TypeAlias"] = (3, 10), ["typing.ParamSpec"] = (3, 10), ["typing.Concatenate"] = (3, 10), ["typing.TypeGuard"] = (3, 10),
        ["typing.is_typeddict"] = (3, 10), ["typing.override"] = (3, 12), ["typing.TypeAliasType"] = (3, 12), ["typing.TypeIs"] = (3, 13), ["typing.ReadOnly"] = (3, 13),
        ["enum.StrEnum"] = (3, 11), ["enum.verify"] = (3, 11), ["warnings.deprecated"] = (3, 13), ["itertools.batched"] = (3, 12), ["itertools.pairwise"] = (3, 10),
        ["functools.cache"] = (3, 9), ["math.lcm"] = (3, 9), ["asyncio.TaskGroup"] = (3, 11), ["asyncio.timeout"] = (3, 11), ["asyncio.to_thread"] = (3, 9),
        ["contextlib.chdir"] = (3, 11), ["contextlib.aclosing"] = (3, 10), ["datetime.UTC"] = (3, 11), ["types.NoneType"] = (3, 10), ["types.EllipsisType"] = (3, 10),
        ["types.GenericAlias"] = (3, 9), ["operator.call"] = (3, 11), ["hashlib.file_digest"] = (3, 11), ["copy.replace"] = (3, 13),
    };

    /// <summary>Standard modules a later Python removed (PEP 594 and PEP 632), with the version that removed them.</summary>
    static readonly Dictionary<string, (int Major, int Minor)> RemovedModules = new(StringComparer.Ordinal)
    {
        ["distutils"] = (3, 12), ["imp"] = (3, 12), ["asynchat"] = (3, 12), ["asyncore"] = (3, 12), ["smtpd"] = (3, 12),
        ["aifc"] = (3, 13), ["audioop"] = (3, 13), ["cgi"] = (3, 13), ["cgitb"] = (3, 13), ["chunk"] = (3, 13), ["crypt"] = (3, 13), ["imghdr"] = (3, 13),
        ["mailcap"] = (3, 13), ["msilib"] = (3, 13), ["nis"] = (3, 13), ["nntplib"] = (3, 13), ["ossaudiodev"] = (3, 13), ["pipes"] = (3, 13),
        ["sndhdr"] = (3, 13), ["spwd"] = (3, 13), ["sunau"] = (3, 13), ["telnetlib"] = (3, 13), ["uu"] = (3, 13), ["xdrlib"] = (3, 13), ["lib2to3"] = (3, 13),
    };

    /// <summary>When those modules were marked deprecated: an import of one is reported as deprecated from there on, as removed past its end.</summary>
    static (int Major, int Minor) DeprecatedSince(string module) => module is "distutils" ? (3, 10) : module is "imp" ? (3, 4) : module is "asynchat" or "asyncore" or "smtpd" ? (3, 6) : (3, 11);

    IEnumerable<Diag> CompatibilityDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        var findings = new List<(Finding F, bool Deprecated)>();
        if (SupportedPython() is { } supported)
            foreach (var f in CompatibilityFindings(m, supported)) findings.Add((f, false));
        foreach (var f in DeprecationFindings(m, ev)) findings.Add((f, true));
        foreach (var f in DocstringTypeFindings(m, ev)) findings.Add((f, false));
        foreach (var (f, deprecated) in findings)
        {
            if (Suppressed(m.Source, f)) continue;
            var d = AsDiag(f);
            yield return deprecated ? d with { Tags = new[] { DiagnosticTag.Deprecated } } : d;
        }
    }

    IEnumerable<Finding> CompatibilityFindings(ModuleInfo m, (int Major, int Minor, string Source) supported)
    {
        bool Older((int Major, int Minor) v) => supported.Major < v.Major || supported.Major == v.Major && supported.Minor < v.Minor;
        string Needs(string what, (int Major, int Minor) v) => $"{what} needs Python {v.Major}.{v.Minor}, and the project supports {supported.Major}.{supported.Minor} ({supported.Source})";
        bool futureAnnotations = m.Ast.Body.OfType<ImportFromStmt>().Any(i => i.Module == "__future__" && i.Names.Any(n => n.Name == "annotations"));

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case MatchStmt ms when Older((3, 10)):
                    yield return new Finding(ms.Start, 5, Needs("The match statement", (3, 10)), "python-version", FindingLevel.Error);
                    break;
                case TryStmt { IsStar: true } ts when Older((3, 11)):
                    yield return new Finding(ts.Start, 3, Needs("except*", (3, 11)), "python-version", FindingLevel.Error);
                    break;
                case TypeAliasStmt ta when Older((3, 12)):
                    yield return new Finding(ta.Start, 4, Needs("The type statement", (3, 12)), "python-version", FindingLevel.Error);
                    break;
                case ClassDef { TypeParams.Count: > 0 } cd when Older((3, 12)):
                    yield return new Finding(cd.NameStart, cd.Name.Length, Needs("Type parameter syntax (class C[T])", (3, 12)), "python-version", FindingLevel.Error);
                    break;
                case FunctionDef fd:
                    if (fd.TypeParams.Count > 0 && Older((3, 12)))
                        yield return new Finding(fd.NameStart, fd.Name.Length, Needs("Type parameter syntax (def f[T])", (3, 12)), "python-version", FindingLevel.Error);
                    if (fd.Params.Any(p => p.Kind == ParamKind.Slash) && Older((3, 8)))
                        yield return new Finding(fd.NameStart, fd.Name.Length, Needs("A positional-only marker (/)", (3, 8)), "python-version", FindingLevel.Error);
                    // annotations are evaluated when the def runs, unless `from __future__ import annotations` postpones them
                    if (!futureAnnotations)
                        foreach (var a in fd.Params.Select(p => p.Annotation).Append(fd.Returns).OfType<Expr>())
                            foreach (var f in AnnotationSyntax(a)) yield return f;
                    break;
                case NamedExpr ne when Older((3, 8)):
                    yield return new Finding(ne.Start, ne.End - ne.Start, Needs("An assignment expression (:=)", (3, 8)), "python-version", FindingLevel.Error);
                    break;
                case ImportStmt imp:
                    foreach (var a in imp.Names)
                    {
                        if (AddedNames.TryGetValue(a.Name, out var v) && Older(v)) yield return new Finding(a.Start, a.End - a.Start, Needs($"'{a.Name}'", v), "python-version", FindingLevel.Error);
                    }
                    break;
                case ImportFromStmt { Level: 0, Module: { } module } imp:
                {
                    if (AddedNames.TryGetValue(module, out var mv) && Older(mv))
                        yield return new Finding(imp.Start, imp.End - imp.Start, Needs($"'{module}'", mv), "python-version", FindingLevel.Error);
                    foreach (var a in imp.Names)
                        if (AddedNames.TryGetValue($"{module}.{a.Name}", out var v) && Older(v))
                            yield return new Finding(a.Start, a.End - a.Start, Needs($"'{module}.{a.Name}'", v), "python-version", FindingLevel.Error);
                    break;
                }
            }
        }

        IEnumerable<Finding> AnnotationSyntax(Expr a)
        {
            foreach (var e in AstChildren.Descendants(a))
            {
                // "Shape" | None is a string: that one fails differently, and is left to the type checks
                if (e is BinaryExpr { Op: "|" } b && Older((3, 10)) && b.Left is not ConstExpr { Kind: ConstKind.Str } && b.Right is not ConstExpr { Kind: ConstKind.Str })
                {
                    yield return new Finding(b.Start, b.End - b.Start, Needs("X | Y in an annotation evaluated at run time", (3, 10)) + "; add `from __future__ import annotations`", "python-version", FindingLevel.Error);
                    yield break;
                }
                if (e is SubscriptExpr { Value: NameExpr { Name: "list" or "dict" or "tuple" or "set" or "frozenset" or "type" } n } && Older((3, 9)))
                {
                    yield return new Finding(n.Start, n.End - n.Start, Needs($"{n.Name}[...] in an annotation evaluated at run time", (3, 9)) + "; add `from __future__ import annotations`", "python-version", FindingLevel.Error);
                    yield break;
                }
            }
        }
    }

    // ------------------------------------------------------------ deprecation

    /// <summary>The message of `@deprecated("...")` on a function, method or class, or null when it has none.</summary>
    static string? DeprecationOf(Node? node)
    {
        var decorators = node switch { FunctionDef f => f.Decorators, ClassDef c => c.Decorators, _ => null };
        if (decorators == null) return null;
        foreach (var d in decorators)
            if (d is CallExpr { Func: var fn } call && TypingName(fn) == "deprecated")
                return Pytest.FixtureModel.StringValue(call.Args.FirstOrDefault()?.Value) ?? "";
        return null;
    }

    IEnumerable<Finding> DeprecationFindings(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ImportStmt imp:
                    foreach (var a in imp.Names)
                        if (DeprecatedModule(a.Name) is { } why) yield return new Finding(a.Start, a.End - a.Start, why, "deprecated", FindingLevel.Weak);
                    break;
                case ImportFromStmt { Level: 0, Module: { } module } imp when DeprecatedModule(module) is { } why:
                    yield return new Finding(imp.Start, imp.End - imp.Start, why, "deprecated", FindingLevel.Weak);
                    break;
                case CallExpr call:
                {
                    var t = ev.TypeOf(m, call.Func);
                    var target = t switch { FunctionType f => f.Info.Node, ClassType { IsInstance: false } c => c.Info.Node, _ => null };
                    // an overload marked deprecated: only when every overload is (the one picked is not known here)
                    string? message = DeprecationOf(target) ?? (t is OverloadedType o && o.Overloads.All(x => DeprecationOf(x.Info.Node) != null) ? DeprecationOf(o.Overloads[0].Info.Node) : null);
                    if (message == null || target is FunctionDef own && ReferenceEquals(own, AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(x => x.Start <= call.Start && call.End <= x.End))) break;
                    string name = m.Source[call.Func.Start..call.Func.End];
                    int at = call.Func is AttributeExpr a ? a.End - a.Attr.Length : call.Func.Start;
                    yield return new Finding(at, call.Func.End - at, $"'{name}' is deprecated{(message.Length > 0 ? ": " + message : "")}", "deprecated", FindingLevel.Weak);
                    break;
                }
            }
        }
    }

    string? DeprecatedModule(string module)
    {
        string top = module.Split('.')[0];
        if (!RemovedModules.TryGetValue(top, out var gone)) return null;
        var since = DeprecatedSince(top);
        if (Interpreter is { } i && (i.Major > gone.Major || i.Major == gone.Major && i.Minor >= gone.Minor)) return null;   // gone already: the version check says so
        return $"'{top}' is deprecated since Python {since.Major}.{since.Minor} and removed in {gone.Major}.{gone.Minor}";
    }

    // ------------------------------------------------------------ docstring types

    static readonly Regex RestType = new(@"^\s*:type\s+(?<name>\w+)\s*:\s*(?<type>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex RestReturn = new(@"^\s*:rtype\s*:\s*(?<type>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex GoogleParam = new(@"^\s+(?<name>\w+)\s*\((?<type>[^)]+)\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);

    static readonly HashSet<string> PlainTypes = new(StringComparer.Ordinal) { "int", "float", "str", "bytes", "bool", "list", "dict", "tuple", "set", "None", "complex", "bytearray", "frozenset" };

    /// <summary>The plain class names a type written in a docstring or an annotation is made of (`str or None`, `list[int] | None`); null when it names anything else.</summary>
    static HashSet<string>? PlainNames(string text)
    {
        var parts = Regex.Split(text.Replace("Optional[", "").Replace("]", ""), @"\s*(?:\bor\b|\||,)\s*")
            .Select(p => Regex.Replace(p.Trim(), @"\[.*$", "")).Select(p => p == "NoneType" ? "None" : p).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0 || parts.Any(p => !PlainTypes.Contains(p))) return null;
        return parts.ToHashSet(StringComparer.Ordinal);
    }

    IEnumerable<Finding> DocstringTypeFindings(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var fd in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            if (fd.Body.Count == 0 || fd.Body[0] is not ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } docNode } || Pytest.FixtureModel.StringValue(docNode) is not { } doc) continue;
            var written = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match x in RestType.Matches(doc)) written.TryAdd(x.Groups["name"].Value, x.Groups["type"].Value);
            if (Regex.IsMatch(doc, @"^\s*(Args|Arguments|Parameters):\s*$", RegexOptions.Multiline))
                foreach (Match x in GoogleParam.Matches(doc)) written.TryAdd(x.Groups["name"].Value, x.Groups["type"].Value.Split(',')[0]);
            foreach (var p in fd.Params.Where(p => p.Annotation != null && written.ContainsKey(p.Name)))
            {
                string annotation = m.Source[p.Annotation!.Start..p.Annotation.End];
                if (PlainNames(written[p.Name]) is { } said && PlainNames(annotation) is { } declared && !said.SetEquals(declared))
                    yield return new Finding(docNode.Start, docNode.End - docNode.Start, $"The docstring says '{p.Name}' is {written[p.Name]}, and its annotation says {annotation}", "docstring-type", FindingLevel.Weak);
            }
            if (fd.Returns != null && RestReturn.Match(doc) is { Success: true } r && PlainNames(r.Groups["type"].Value) is { } saidReturn
                && PlainNames(m.Source[fd.Returns.Start..fd.Returns.End]) is { } declaredReturn && !saidReturn.SetEquals(declaredReturn))
                yield return new Finding(docNode.Start, docNode.End - docNode.Start, $"The docstring says '{fd.Name}' returns {r.Groups["type"].Value}, and its annotation says {m.Source[fd.Returns.Start..fd.Returns.End]}", "docstring-type", FindingLevel.Weak);
        }
    }
}
