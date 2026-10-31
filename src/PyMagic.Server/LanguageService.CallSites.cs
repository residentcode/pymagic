using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>One parameter as the editor shows and edits it. Origin is the index in the current signature, null for a new parameter.</summary>
/// <param name="Category">positionalOnly, normal, keywordOnly, varArgs or kwArgs.</param>
/// <param name="CallValue">For a new parameter: what existing calls pass for it.</param>
public sealed record SignatureParam(int? Origin, string Name, string Category, string? Annotation, string? Default, string? CallValue = null);

/// <summary>The signature of the function under the cursor and how widely it is used.</summary>
/// <param name="Kind">function, method or constructor.</param>
/// <param name="ImplicitFirst">self or cls: part of the definition but not editable.</param>
/// <param name="Overriding">How many methods that override it take the change too.</param>
/// <param name="Base">The project method at the top of the chain it overrides, which the dialog offers to change instead.</param>
public sealed record SignatureInfo(string Name, string Kind, string? ImplicitFirst, IReadOnlyList<SignatureParam> Parameters,
    int Calls, int Files, int OtherReferences, int Overriding = 0, SignatureBase? Base = null);

/// <summary>Where the method an override overrides is: the class, and a position on its name for asking about it.</summary>
public sealed record SignatureBase(string Class, string Uri, Position Position);

/// <summary>Edits plus the things the user should look at afterwards.</summary>
public sealed record RefactorResult(Dictionary<string, List<TextEdit>> Changes, List<string> Warnings);

public sealed partial class LanguageService
{
    sealed record OldParam(int Index, Parameter Node, string Name, string Category, string? Annotation, string? Default);

    sealed class FunctionTarget
    {
        public required ModuleInfo Module { get; init; }
        public required FunctionDef Def { get; init; }
        public ClassDef? Class { get; init; }
        public bool IsStatic { get; init; }
        public bool IsClassMethod { get; init; }
        public bool IsConstructor => Class != null && Def.Name == "__init__";
        public Parameter? Implicit { get; init; }
        public required List<OldParam> Params { get; init; }
        public string Kind => IsConstructor ? "constructor" : Class != null ? "method" : "function";
    }

    sealed record CallSite(ModuleInfo Module, CallExpr Call, bool ExplicitSelf);

    // ------------------------------------------------------------ the function under the cursor

    FunctionTarget ResolveFunction(Snapshot snap, ModuleInfo m, int offset)
    {
        FunctionDef? def = null;

        // inside a def's header (its name, parameters and annotations)
        foreach (var fd in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            int headerEnd = fd.Body.Count > 0 ? fd.Body[0].Start : fd.End;
            if (offset >= fd.NameStart && offset < headerEnd && (def == null || fd.Start > def.Start)) def = fd;
        }

        if (def == null)
        {
            var target = FindTarget(snap, m, offset) ?? throw new ServiceException("Put the cursor on a function, or on a call of one.");
            var nodes = new List<Node>(target.Nodes);
            // an import (possibly under another name) stands for what it imports
            foreach (var sym in target.Symbols.ToList())
            {
                var cur = sym; var curModule = snap.Workspace.ModuleOf(sym) ?? m;
                for (int i = 0; i < 12; i++)
                {
                    var hop = ImportHop(snap, curModule, cur);
                    if (hop == null || hop.Value.OriginModule == null) break;
                    cur = hop.Value.Origin; curModule = hop.Value.OriginModule;
                    nodes.AddRange(cur.Declarations.Select(d => d.Node));
                }
            }
            var cls = nodes.OfType<ClassDef>().FirstOrDefault();
            if (cls != null)
            {
                def = cls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__")
                    ?? throw new ServiceException($"'{cls.Name}' has no __init__ of its own: there is no signature to change.");
            }
            else
            {
                var defs = nodes.OfType<FunctionDef>().Distinct().ToList();
                if (defs.Count == 0) throw new ServiceException("Put the cursor on a function, or on a call of one.");
                if (defs.Count > 1) throw new ServiceException("This name stands for more than one function (overloads or redefinitions).");
                def = defs[0];
            }
        }

        var module = OwnerModule(def, snap.Project) ?? throw new ServiceException($"'{def.Name}' is defined in a library and cannot be changed.");
        return Describe(module, def);
    }

    static FunctionTarget Describe(ModuleInfo m, FunctionDef def)
    {
        var cls = m.ParentOf(def) as ClassDef;
        bool isStatic = def.Decorators.Any(d => d is NameExpr { Name: "staticmethod" });
        bool isClassMethod = def.Decorators.Any(d => d is NameExpr { Name: "classmethod" });
        // typing's @override only marks an override: it changes nothing callers see
        if (def.Decorators.Any(d => !(d is NameExpr { Name: "staticmethod" or "classmethod" or "override" } or AttributeExpr { Attr: "override" })))
            throw new ServiceException($"'{def.Name}' is decorated: a decorator may change what its callers see.");

        int slash = def.Params.ToList().FindIndex(p => p.Kind == ParamKind.Slash);
        bool afterStar = false;
        var all = new List<OldParam>();
        for (int i = 0; i < def.Params.Count; i++)
        {
            var p = def.Params[i];
            string? category = null;
            switch (p.Kind)
            {
                case ParamKind.Slash: continue;
                case ParamKind.KwOnlySeparator: afterStar = true; continue;
                case ParamKind.VarArgs: category = "varArgs"; afterStar = true; break;
                case ParamKind.KwArgs: category = "kwArgs"; break;
                default: category = slash >= 0 && i < slash ? "positionalOnly" : afterStar ? "keywordOnly" : "normal"; break;
            }
            all.Add(new OldParam(all.Count, p, p.Name, category,
                p.Annotation != null ? m.Source[p.Annotation.Start..p.Annotation.End] : null,
                p.Default != null ? m.Source[p.Default.Start..p.Default.End] : null));
        }

        Parameter? implicitFirst = null;
        if (cls != null && !isStatic && all.Count > 0 && all[0].Category is "normal" or "positionalOnly")
        {
            implicitFirst = all[0].Node;
            all.RemoveAt(0);
            all = all.Select((p, i) => p with { Index = i }).ToList();
        }
        return new FunctionTarget { Module = m, Def = def, Class = cls, IsStatic = isStatic, IsClassMethod = isClassMethod, Implicit = implicitFirst, Params = all };
    }

    // ------------------------------------------------------------ calls

    /// <summary>Calls of the function everywhere in the project, plus how many other references (callbacks, decorators) there are.</summary>
    (List<CallSite> Calls, int Other) FindCalls(Snapshot snap, FunctionTarget fn)
    {
        var ev = snap.Workspace.Evaluator;
        var calls = new List<CallSite>();
        var seen = new HashSet<(ModuleInfo, int)>();
        int other = 0;
        var indexes = new Dictionary<ModuleInfo, Dictionary<int, Expr>>(ReferenceEqualityComparer.Instance);

        Expr? ExprAt(ModuleInfo module, int start)
        {
            if (!indexes.TryGetValue(module, out var index))
            {
                index = new Dictionary<int, Expr>();
                foreach (var n in AstChildren.Descendants(module.Ast))
                {
                    if (n is NameExpr ne) index[ne.Start] = ne;
                    else if (n is AttributeExpr ae) index[ae.End - ae.Attr.Length] = ae;
                }
                indexes[module] = index;
            }
            return index.GetValueOrDefault(start);
        }

        void Collect(Target target, bool countOther)
        {
            foreach (var site in Usages(snap, target, forRename: false))
            {
                if (site.IsDeclaration || !seen.Add((site.Module, site.Start))) continue;
                var expr = ExprAt(site.Module, site.Start);
                if (expr == null) continue;                                     // an import's name token
                if (site.Module.ParentOf(expr) is CallExpr call && ReferenceEquals(call.Func, expr))
                {
                    bool explicitSelf = false;
                    if (expr is AttributeExpr ae && fn.Class != null && !fn.IsStatic && !fn.IsClassMethod)
                        explicitSelf = ev.TypeOf(site.Module, ae.Value) is ClassType { IsInstance: false };
                    calls.Add(new CallSite(site.Module, call, explicitSelf));
                }
                else if (countOther) other++;
            }
        }

        var self = FindTarget(snap, fn.Module, fn.Def.NameStart);
        if (self != null) Collect(self, countOther: true);
        if (fn.IsConstructor && fn.Class != null && FindTarget(snap, fn.Module, fn.Class.NameStart) is { } classTarget) Collect(classTarget, countOther: false);
        return (calls, other);
    }

    /// <summary>The arguments of one call, assigned to the parameters of the current signature.</summary>
    sealed class ArgMap
    {
        public string? Unsupported { get; set; }
        public string? SelfArgument { get; set; }
        public Dictionary<int, (string Text, bool Keyword)> ByParam { get; } = new();
        public List<string> ExtraPositional { get; } = new();
        public List<(string Name, string Text)> ExtraKeywords { get; } = new();
    }

    /// <param name="text">The text of an argument, when it differs from the source (edits already made inside it).</param>
    ArgMap MapArguments(FunctionTarget fn, CallSite site, Func<Node, string>? text = null)
    {
        var map = new ArgMap();
        var m = site.Module;
        string Text(Node n) => text?.Invoke(n) ?? m.Source[n.Start..n.End];
        var positional = fn.Params.Where(p => p.Category is "positionalOnly" or "normal").ToList();
        bool hasVarArgs = fn.Params.Any(p => p.Category == "varArgs");
        bool hasKwArgs = fn.Params.Any(p => p.Category == "kwArgs");
        int next = 0;
        bool selfPending = site.ExplicitSelf;

        foreach (var arg in site.Call.Args)
        {
            if (arg.Stars != 0) { map.Unsupported = "uses * or ** to pass arguments"; return map; }
            if (arg.Name == null)
            {
                if (selfPending) { map.SelfArgument = Text(arg.Value); selfPending = false; continue; }
                if (next < positional.Count) map.ByParam[positional[next++].Index] = (Text(arg.Value), false);
                else if (hasVarArgs) map.ExtraPositional.Add(Text(arg.Value));
                else { map.Unsupported = "passes more arguments than the function takes"; return map; }
            }
            else
            {
                var p = fn.Params.FirstOrDefault(x => x.Name == arg.Name && x.Category is "normal" or "keywordOnly");
                if (p != null && !map.ByParam.ContainsKey(p.Index)) map.ByParam[p.Index] = (Text(arg.Value), true);
                else if (hasKwArgs) map.ExtraKeywords.Add((arg.Name, Text(arg.Value)));
                else { map.Unsupported = $"passes an argument '{arg.Name}' that the function does not take"; return map; }
            }
        }
        if (selfPending) map.Unsupported = "does not pass the instance";
        return map;
    }

    // ------------------------------------------------------------ text helpers

    /// <summary>Index of the ')' that closes the '(' at <paramref name="open"/>, skipping nested brackets, strings and comments.</summary>
    static int FindClosingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}':
                    if (--depth == 0) return i;
                    break;
                case '#':
                    while (i < text.Length && text[i] != '\n') i++;
                    break;
                case '\'' or '"':
                {
                    bool triple = i + 2 < text.Length && text[i + 1] == c && text[i + 2] == c;
                    int j = i + (triple ? 3 : 1);
                    for (; j < text.Length; j++)
                    {
                        if (text[j] == '\\') { j++; continue; }
                        if (triple ? (text[j] == c && j + 2 < text.Length + 0 && text.AsSpan(j).StartsWith(new string(c, 3))) : text[j] == c) break;
                        if (!triple && text[j] == '\n') break;
                    }
                    i = j + (triple ? 2 : 0);
                    break;
                }
            }
        }
        return -1;
    }

    /// <summary>The text between the parentheses of a def's parameter list.</summary>
    static (int Start, int End) ParameterRegion(ModuleInfo m, FunctionDef def)
    {
        string text = m.Source;
        int i = def.NameStart + def.Name.Length;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i < text.Length && text[i] == '[') { i = FindClosingParen(text, i); i = i < 0 ? i : i + 1; }   // type parameters
        while (i >= 0 && i < text.Length && text[i] != '(') i++;
        if (i < 0 || i >= text.Length) throw new ServiceException("Cannot find the parameter list of this function.");
        int close = FindClosingParen(text, i);
        if (close < 0) throw new ServiceException("Cannot find the end of the parameter list.");
        return (i + 1, close);
    }


    bool CanChangeSignature(Snapshot snap, ModuleInfo m, int offset)
    {
        try { ResolveFunction(snap, m, offset); return true; }
        catch (ServiceException) { return false; }
    }

    /// <summary>The cursor is in a def's header and the function's body has a shape that can be inlined (the calls are checked when it runs).</summary>
    bool CanInlineAll(Snapshot snap, ModuleInfo m, int offset)
    {
        foreach (var fd in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            int headerEnd = fd.Body.Count > 0 ? fd.Body[0].Start : fd.End;
            if (offset < fd.NameStart || offset >= headerEnd) continue;
            try
            {
                var fn = ResolveFunction(snap, m, offset);
                if (ShapeOf(fn, out _) == null) return false;
                var (calls, other) = FindCalls(snap, fn);   // only worth offering when something calls it
                return calls.Count > 0 && other == 0;
            }
            catch (ServiceException) { return false; }
        }
        return false;
    }
    // ------------------------------------------------------------ public queries

    public SignatureInfo GetSignature(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        if (m.SyntaxErrors.Count > 0) throw new ServiceException("Fix the syntax errors in this file first.");
        var fn = ResolveFunction(snap, m, Lines(m).OffsetOf(pos));
        var (calls, other) = FindCalls(snap, fn);
        int overriding = 0;
        SignatureBase? baseMethod = null;
        if (fn.Class != null && !fn.IsConstructor && Overridable(fn.Def.Name))
        {
            overriding = OverridingMethods(snap, fn.Class, fn.Def.Name).Count;
            if (DeepestProjectSuper(snap, fn.Module, fn.Class, fn.Def.Name) is { } top && UriOfModule(top.Module) is { } baseUri)
                baseMethod = new SignatureBase(top.Class.Name, baseUri, Lines(top.Module).PositionOf(top.Method.NameStart));
        }
        return new SignatureInfo(fn.Def.Name, fn.Kind, fn.Implicit?.Name, fn.Params.Select(p => new SignatureParam(p.Index, p.Name, p.Category, p.Annotation, p.Default)).ToList(),
            calls.Count, calls.Select(c => c.Module).Distinct().Count(), other, overriding, baseMethod);
    }
}
