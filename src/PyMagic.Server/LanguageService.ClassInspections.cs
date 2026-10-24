using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Inspections that need the class hierarchy: a base class whose __init__ is never called, and abstract methods nobody implemented.</summary>
public sealed partial class LanguageService
{
    sealed record AbstractMember(string Name, FunctionDef Def, ClassType Owner);

    IEnumerable<Diag> ClassDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>())
        {
            if (ClassInfoOf(m, cd, ev) is not { } info) continue;

            if (SuperInitMissing(m, cd, info, ev) is { } init && cd.Body.OfType<FunctionDef>().First(f => f.Name == "__init__") is { } own)
            {
                var finding = new Finding(own.NameStart, own.Name.Length, $"Call to __init__ of super class '{init.Info.Name}' is missed", "missing-super-init", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }

            var missing = UnimplementedAbstract(cd, info, ev);
            if (missing.Count > 0)
            {
                string names = string.Join(", ", missing.Take(3).Select(a => a.Name)) + (missing.Count > 3 ? ", ..." : "");
                var finding = new Finding(cd.NameStart, cd.Name.Length, $"Class '{cd.Name}' must implement all abstract methods: {names}", "unimplemented-abstract", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        }
    }

    static Diag AsDiag(Finding f) => new(new Diagnostic(f.Start, f.Length, f.Message),
        f.Level switch { FindingLevel.Error => Severity.Error, FindingLevel.Warning => Severity.Warning, FindingLevel.Weak => Severity.Information, _ => Severity.Hint },
        f.Level == FindingLevel.Unused ? new[] { DiagnosticTag.Unnecessary } : null, f.Code);

    static ClassInfo? ClassInfoOf(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        var sym = m.Semantics.ScopeOf(cd)?.Parent?.Symbols.GetValueOrDefault(cd.Name);
        return sym != null && ev.TypeOfSymbol(sym) is ClassType { IsInstance: false } ct && ReferenceEquals(ct.Info.Node, cd) ? ct.Info : null;
    }

    /// <summary>The class's ancestors (itself first), or null when one of them cannot be resolved.</summary>
    static List<ClassType>? KnownMro(ClassInfo info, TypeEvaluator ev)
    {
        var list = new List<ClassType>();
        foreach (var t in ev.MroOf(info))
        {
            if (t is not ClassType ct) return null;
            list.Add(ct);
        }
        return list;
    }

    // ------------------------------------------------------------ __init__ of the base class

    static readonly Regex InitCall = new(@"\.\s*__init__", RegexOptions.Compiled);   // also `init = super().__init__` and calls made through a variable

    /// <summary>The nearest ancestor with an __init__ of its own, when this class defines an __init__ that never calls any.</summary>
    ClassType? SuperInitMissing(ModuleInfo m, ClassDef cd, ClassInfo info, TypeEvaluator ev)
    {
        var init = cd.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
        if (init == null || InitCall.IsMatch(m.Source[init.Start..init.End])) return null;
        if (ev.IsDataclass(info) || ev.IsProtocol(info)) return null;
        var mro = KnownMro(info, ev);
        if (mro == null || mro.Any(c => c.Info.Name is "Enum" or "IntEnum" or "Flag" or "NamedTuple" or "TypedDict")) return null;
        foreach (var b in mro.Skip(1))
        {
            if (b.Info.Name == "object" && b.Info.Module.Name == "builtins") continue;
            if (b.Info.Scope.Symbols.TryGetValue("__init__", out var s) && s.Declarations.Any(d => d.Kind == DeclKind.Function)) return b;
        }
        return null;
    }

    /// <summary>`super().__init__(a, b)` with the arguments the base takes that this __init__ has parameters of the same name for.</summary>
    static string SuperInitCall(ClassType baseClass, FunctionDef own)
    {
        var baseInit = baseClass.Info.Scope.Symbols["__init__"].Declarations.Select(d => d.Node).OfType<FunctionDef>().FirstOrDefault();
        var args = new List<string>();
        if (baseInit != null)
        {
            bool first = true, starSeen = false;
            foreach (var p in baseInit.Params)
            {
                if (p.Kind == ParamKind.KwOnlySeparator) { starSeen = true; continue; }
                if (p.Kind == ParamKind.Slash) continue;
                if (first) { first = false; continue; }   // self
                if (p.Kind == ParamKind.VarArgs) starSeen = true;
                var mine = own.Params.FirstOrDefault(x => x.Name == p.Name && x.Kind == p.Kind);
                if (mine == null) continue;
                args.Add(p.Kind switch
                {
                    ParamKind.VarArgs => "*" + p.Name,
                    ParamKind.KwArgs => "**" + p.Name,
                    _ => starSeen ? $"{p.Name}={p.Name}" : p.Name,   // after * or *args a parameter can only be passed by keyword
                });
            }
        }
        return $"super().__init__({string.Join(", ", args)})";
    }

    // ------------------------------------------------------------ abstract methods

    static bool IsAbstractDef(FunctionDef f) => f.Decorators.Any(d =>
        d is NameExpr { Name: "abstractmethod" or "abstractproperty" or "abstractclassmethod" or "abstractstaticmethod" }
        || d is AttributeExpr { Attr: "abstractmethod" or "abstractproperty" or "abstractclassmethod" or "abstractstaticmethod" });

    /// <summary>The abstract-method machinery only works with ABCMeta: through ABC, or a metaclass= somewhere up the line.</summary>
    static bool UsesAbc(ClassInfo info, TypeEvaluator ev) =>
        ev.MroOf(info).OfType<ClassType>().Any(c => c.Info.Name == "ABC" && c.Info.Module.Name == "abc"
            || c.Info.Node.Bases.Any(b => b.Name == "metaclass" && Regex.IsMatch(c.Info.Module.Source[b.Value.Start..b.Value.End], @"\bABCMeta\b")));

    List<AbstractMember> UnimplementedAbstract(ClassDef cd, ClassInfo info, TypeEvaluator ev)
    {
        var result = new List<AbstractMember>();
        if (ev.IsProtocol(info)) return result;
        var mro = KnownMro(info, ev);
        if (mro == null) return result;

        // a class that declares abstract methods itself, or says ABC / ABCMeta right in its header, is meant to stay abstract
        foreach (var f in cd.Body.OfType<FunctionDef>())
            if (IsAbstractDef(f)) return result;
        if (cd.Bases.Any(b => b.Name == "metaclass" || b.Name == null && Regex.IsMatch(info.Module.Source[b.Value.Start..b.Value.End], @"(^|\.)ABC$")))
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in mro)
        {
            foreach (var (name, sym) in c.Info.Scope.Symbols)
            {
                if (sym.Declarations.Count == 0 || !seen.Add(name)) continue;
                if (sym.Declarations[^1].Node is FunctionDef fd && IsAbstractDef(fd) && UsesAbc(c.Info, ev))
                    result.Add(new AbstractMember(name, fd, c));
            }
        }
        return result;
    }

    static string StubFor(AbstractMember a)
    {
        var f = a.Def;
        string src = a.Owner.Info.Module.Source;
        string parameters = f.Params.Count == 0 ? "" : Regex.Replace(src[f.Params[0].Start..f.Params[^1].End], @"\s*\n\s*", " ");
        string returns = f.Returns != null ? " -> " + src[f.Returns.Start..f.Returns.End] : "";
        var decorators = f.Decorators.Where(d => !(d is NameExpr { Name: "abstractmethod" } or AttributeExpr { Attr: "abstractmethod" }))
            .Select(d => "@" + src[d.Start..d.End]);
        return string.Join("\n", decorators.Append($"{(f.IsAsync ? "async " : "")}def {f.Name}({parameters}){returns}:").Append("    raise NotImplementedError"));
    }
}
