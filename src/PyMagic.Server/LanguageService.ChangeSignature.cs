using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

public sealed partial class LanguageService
{
    static int CategoryRank(string c) => c switch { "positionalOnly" => 0, "normal" => 1, "varArgs" => 2, "keywordOnly" => 3, "kwArgs" => 4, _ => -1 };

    static bool IsPositional(string category) => category is "positionalOnly" or "normal";

    static string FormatParameter(SignatureParam p)
    {
        string annotation = string.IsNullOrWhiteSpace(p.Annotation) ? "" : ": " + p.Annotation!.Trim();
        return p.Category switch
        {
            "varArgs" => "*" + p.Name + annotation,
            "kwArgs" => "**" + p.Name + annotation,
            _ => p.Name + annotation + (string.IsNullOrWhiteSpace(p.Default) ? "" : (annotation.Length > 0 ? " = " : "=") + p.Default!.Trim()),
        };
    }

    /// <summary>The parameter list text for the new signature, with the / and * separators the categories need.</summary>
    static string RenderParameters(string? implicitText, IReadOnlyList<SignatureParam> ps)
    {
        var parts = new List<string>();
        if (implicitText != null) parts.Add(implicitText);
        bool hasVarArgs = ps.Any(p => p.Category == "varArgs");
        int lastPositionalOnly = ps.ToList().FindLastIndex(p => p.Category == "positionalOnly");
        bool star = false;
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.Category == "keywordOnly" && !hasVarArgs && !star) { parts.Add("*"); star = true; }
            parts.Add(FormatParameter(p));
            if (i == lastPositionalOnly) parts.Add("/");
        }
        return string.Join(", ", parts);
    }

    /// <exception cref="ServiceException">The new signature is not valid, or something in the way cannot be changed safely.</exception>
    public RefactorResult ChangeSignature(string uri, Position pos, string? newName, IReadOnlyList<SignatureParam> requested)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        if (snap.Project.Any(p => p.SyntaxErrors.Count > 0 && ReferenceEquals(p, m))) throw new ServiceException("Fix the syntax errors in this file first.");
        var fn = ResolveFunction(snap, m, Lines(m).OffsetOf(pos));
        var def = fn.Def;
        var defModule = fn.Module;
        var warnings = new List<string>();

        // ---- is the requested signature valid?
        var seenNames = new HashSet<string>();
        int lastRank = 0, varArgs = 0, kwArgs = 0;
        bool defaultSeen = false;
        foreach (var p in requested)
        {
            if (!Regex.IsMatch(p.Name ?? "", @"^[\p{L}_][\p{L}\p{N}_]*$") || Lexer.Keywords.Contains(p.Name!)) throw new ServiceException($"'{p.Name}' is not a valid parameter name.");
            if (!seenNames.Add(p.Name!)) throw new ServiceException($"The parameter '{p.Name}' appears twice.");
            int rank = CategoryRank(p.Category);
            if (rank < 0) throw new ServiceException($"Unknown kind of parameter '{p.Category}'.");
            if (rank < lastRank) throw new ServiceException("The order of parameter kinds must be: positional-only, normal, *args, keyword-only, **kwargs.");
            lastRank = rank;
            if (p.Category == "varArgs") varArgs++;
            if (p.Category == "kwArgs") kwArgs++;
            if (IsPositional(p.Category))
            {
                bool hasDefault = !string.IsNullOrWhiteSpace(p.Default);
                if (defaultSeen && !hasDefault) throw new ServiceException($"'{p.Name}' has no default but follows a parameter that has one.");
                defaultSeen |= hasDefault;
            }
            if (p.Category is "varArgs" or "kwArgs" && !string.IsNullOrWhiteSpace(p.Default)) throw new ServiceException($"'{p.Name}' cannot have a default.");
            if (p.Origin is { } o)
            {
                if (o < 0 || o >= fn.Params.Count) throw new ServiceException("The signature in the editor is out of date; open the dialog again.");
                var old = fn.Params[o];
                if (old.Category is "varArgs" or "kwArgs" && old.Category != p.Category) throw new ServiceException($"'{old.Name}' must stay a {(old.Category == "varArgs" ? "*args" : "**kwargs")} parameter.");
            }
        }
        if (varArgs > 1 || kwArgs > 1) throw new ServiceException("A function can have only one *args and one **kwargs parameter.");
        if (requested.Where(p => p.Origin != null).Select(p => p.Origin).Distinct().Count() != requested.Count(p => p.Origin != null))
            throw new ServiceException("A parameter was used twice.");
        if (newName != null && newName != def.Name)
        {
            if (fn.IsConstructor) throw new ServiceException("A constructor is always called __init__.");
            if (!Regex.IsMatch(newName, @"^[\p{L}_][\p{L}\p{N}_]*$") || Lexer.Keywords.Contains(newName)) throw new ServiceException($"'{newName}' is not a valid function name.");
        }

        // ---- the body: removed parameters must be unused, renamed ones are updated, new names must be free
        var edits = new Dictionary<ModuleInfo, List<Edit>>(ReferenceEqualityComparer.Instance);
        void Add(ModuleInfo module, Edit e)
        {
            if (!edits.TryGetValue(module, out var list)) edits[module] = list = new List<Edit>();
            list.Add(e);
        }

        var scope = defModule.Semantics.ScopeOf(def) ?? throw new ServiceException("Cannot analyse this function.");
        var bodyNodes = def.Body.Cast<Node>().ToList();
        bool Mentioned(string name) => FStrings(bodyNodes).Any(c => Regex.IsMatch(c.Text, @"(?<![\w.])" + Regex.Escape(name) + @"(?!\w)"));

        foreach (var old in fn.Params)
        {
            var kept = requested.FirstOrDefault(p => p.Origin == old.Index);
            scope.Symbols.TryGetValue(old.Name, out var sym);
            var refs = sym?.References.Where(r => !ReferenceEquals(r.Node, old.Node)).ToList() ?? new();
            if (kept == null)
            {
                if (refs.Count > 0 || Mentioned(old.Name)) throw new ServiceException($"The parameter '{old.Name}' is used in the function body, so it cannot be removed.");
            }
            else if (kept.Name != old.Name)
            {
                if (Mentioned(old.Name)) throw new ServiceException($"'{old.Name}' appears inside an f-string in the body, which cannot be updated yet.");
                foreach (var r in refs) Add(defModule, new Edit(r.Node.Start, r.Node.End, kept.Name));
            }
        }
        // a name the body already uses for something else would be captured by a parameter of that name
        var bodyNames = bodyNodes.SelectMany(AstChildren.Descendants).OfType<NameExpr>().Select(n => n.Name).ToHashSet();
        foreach (var p in requested)
        {
            bool unchanged = p.Origin is { } o && fn.Params[o].Name == p.Name;
            if (unchanged) continue;
            var currentOwner = fn.Params.FirstOrDefault(o => o.Name == p.Name);
            if (currentOwner != null)
            {
                // taking the name of another parameter is fine when that parameter is renamed or removed
                bool freed = !requested.Any(r => r.Origin == currentOwner.Index && r.Name == p.Name);
                if (freed) continue;
                throw new ServiceException($"The name '{p.Name}' is already used by another parameter.");
            }
            if (bodyNames.Contains(p.Name) || scope.Symbols.TryGetValue(p.Name, out var local) && local.Declarations.Count > 0)
                throw new ServiceException($"The body already uses the name '{p.Name}'.");
        }

        // ---- the header
        var (regionStart, regionEnd) = ParameterRegion(defModule, def);
        string text = defModule.Source;
        string region = text[regionStart..regionEnd];
        string? implicitText = fn.Implicit != null ? text[fn.Implicit.Start..fn.Implicit.End] : null;
        string rendered = RenderParameters(implicitText, requested);
        if (region.Contains('\n') && requested.Count + (implicitText != null ? 1 : 0) > 1)
        {
            string defIndent = LineIndent(text, def.Start);
            string indent = def.Params.Count > 0 ? LineIndent(text, def.Params[0].Start) : defIndent + "    ";
            string nl = NewLine(defModule);
            rendered = nl + indent + string.Join("," + nl + indent, SplitTopLevel(rendered)) + "," + nl + defIndent;
        }
        if (region.Contains('#')) warnings.Add("Comments inside the old parameter list were removed.");
        Add(defModule, new Edit(regionStart, regionEnd, rendered));
        if (newName != null && newName != def.Name)
        {
            var target = FindTarget(snap, defModule, def.NameStart)!;
            EnsureRenameable(snap, target);
            foreach (var s in target.Symbols)
                if (s.Scope.Symbols.TryGetValue(newName, out var clash) && (clash.Redirect ?? clash).Declarations.Count > 0)
                    throw new ServiceException($"'{newName}' is already defined in this scope.");
            foreach (var site in Usages(snap, target, forRename: true)) Add(site.Module, new Edit(site.Start, site.End, newName));
        }

        // ---- the calls
        var (calls, other) = FindCalls(snap, fn);
        var unsupported = new List<string>();
        foreach (var site in calls)
        {
            var map = MapArguments(fn, site);
            string? why = map.Unsupported;
            string? replacement = why == null ? RewriteCall(fn, map, requested, out why) : null;
            if (replacement == null)
            {
                int line = Lines(site.Module).PositionOf(site.Call.Start).Line + 1;
                unsupported.Add($"{System.IO.Path.GetFileName(site.Module.Path ?? site.Module.Name)}:{line} {why}");
                continue;
            }

            var args = site.Call.Args;
            if (args.Count > 0)
            {
                int start = args[0].Start, end = args[^1].End;
                if (replacement.Length == 0)
                {
                    int e = end;
                    while (e < site.Module.Source.Length && char.IsWhiteSpace(site.Module.Source[e])) e++;
                    if (e < site.Module.Source.Length && site.Module.Source[e] == ',') end = e + 1;
                }
                Add(site.Module, new Edit(start, end, replacement));
            }
            else if (replacement.Length > 0)
            {
                int close = site.Call.End - 1;
                Add(site.Module, new Edit(close, close, replacement));
            }
        }
        foreach (var u in unsupported.Take(5)) warnings.Add($"Not updated: {u}.");
        if (unsupported.Count > 5) warnings.Add($"…and {unsupported.Count - 5} more calls were not updated.");
        if (other > 0) warnings.Add($"{other} reference{(other == 1 ? "" : "s")} that {(other == 1 ? "is" : "are")} not a call (callbacks, decorators) {(other == 1 ? "was" : "were")} not changed.");
        if (fn.Class != null && !fn.IsConstructor)
        {
            int overriding = snap.Project.SelectMany(p => AstChildren.Descendants(p.Ast).OfType<ClassDef>())
                .Count(c => !ReferenceEquals(c, fn.Class) && c.Body.OfType<FunctionDef>().Any(f => f.Name == def.Name));
            if (overriding > 0) warnings.Add($"{overriding} other class{(overriding == 1 ? "" : "es")} define a method with this name; {(overriding == 1 ? "it was" : "they were")} not changed.");
        }

        var changes = new Dictionary<string, List<TextEdit>>();
        foreach (var (module, list) in edits)
        {
            string target = UriOfModule(module) ?? throw new ServiceException("A file that has to change cannot be edited.");
            var ml = Lines(module);
            changes[target] = list.OrderBy(e => e.Start).ThenBy(e => e.End).Select(e => new TextEdit(ml.RangeOf(e.Start, e.End), e.Text)).ToList();
        }
        return new RefactorResult(changes, warnings);
    }

    /// <summary>Splits "a, b=(1, 2), *c" at the commas that are not inside brackets or strings.</summary>
    static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') quote = c;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(text[start..i].Trim()); start = i + 1; }
        }
        if (text[start..].Trim().Length > 0) parts.Add(text[start..].Trim());
        return parts;
    }

    /// <summary>The argument list text for one call under the new signature, or null (with the reason) when it cannot be rewritten.</summary>
    static string? RewriteCall(FunctionTarget fn, ArgMap map, IReadOnlyList<SignatureParam> ps, out string? why)
    {
        why = null;
        var positional = new List<string>();
        var keywords = new List<string>();
        if (map.SelfArgument != null) positional.Add(map.SelfArgument);
        bool gap = false;

        (string Text, bool Keyword)? Provided(SignatureParam p) =>
            p.Origin is { } o ? (map.ByParam.TryGetValue(o, out var v) ? v : null)
            : string.IsNullOrWhiteSpace(p.CallValue) ? null : (p.CallValue!.Trim(), false);

        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            var v = Provided(p);
            switch (p.Category)
            {
                case "positionalOnly" or "normal":
                    if (v == null)
                    {
                        bool laterProvided = ps.Skip(i + 1).Any(q => IsPositional(q.Category) && Provided(q) != null);
                        if (p.Category == "positionalOnly" && laterProvided)
                        {
                            if (string.IsNullOrWhiteSpace(p.Default)) { why = $"leaves out '{p.Name}', which cannot be skipped"; return null; }
                            positional.Add(p.Default!.Trim());
                        }
                        else gap = true;
                        break;
                    }
                    if (!gap && (!v.Value.Keyword || p.Category == "positionalOnly")) positional.Add(v.Value.Text);
                    else if (p.Category == "positionalOnly") { why = $"cannot pass '{p.Name}' by position after skipping an argument"; return null; }
                    else { gap = true; keywords.Add(p.Name + "=" + v.Value.Text); }
                    break;
                case "varArgs":
                    if (map.ExtraPositional.Count > 0)
                    {
                        if (gap) { why = "has extra positional arguments that cannot follow a skipped one"; return null; }
                        positional.AddRange(map.ExtraPositional);
                    }
                    break;
                case "keywordOnly":
                    if (v != null) keywords.Add(p.Name + "=" + v.Value.Text);
                    break;
                case "kwArgs":
                    break;
            }
        }
        if (map.ExtraPositional.Count > 0 && !ps.Any(p => p.Category == "varArgs")) { why = "passes extra positional arguments that the new signature does not accept"; return null; }
        if (map.ExtraKeywords.Count > 0 && !ps.Any(p => p.Category == "kwArgs")) { why = "passes extra keyword arguments that the new signature does not accept"; return null; }
        foreach (var (name, value) in map.ExtraKeywords) keywords.Add(name + "=" + value);

        // an old argument whose parameter was removed is dropped, but one that is not a plain value may have effects
        var kept = ps.Where(p => p.Origin != null).Select(p => p.Origin!.Value).ToHashSet();
        foreach (var (index, arg) in map.ByParam)
            if (!kept.Contains(index) && Regex.IsMatch(arg.Text, @"\(")) { why = $"passes a call as the removed argument '{fn.Params[index].Name}': dropping it would drop the call"; return null; }

        return string.Join(", ", positional.Concat(keywords));
    }
}
