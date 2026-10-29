using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>Calls of Jinja macros checked against their parameters, as Jinja itself checks them when the template renders (item 145).</summary>
public sealed partial class LanguageService
{
    /// <summary>A macro: its parameters (with whether each has a default), and whether its body reads `varargs`, `kwargs` and `caller`.</summary>
    sealed record JinjaMacroDef(string Name, List<(string Name, bool HasDefault)> Params, bool Varargs, bool Kwargs, bool Caller);

    /// <summary>The macros a template's text defines, by name.</summary>
    static Dictionary<string, JinjaMacroDef> MacrosIn(string text)
    {
        var macros = new Dictionary<string, JinjaMacroDef>(StringComparer.Ordinal);
        var tags = DjangoTemplateParser.Parse(text, true).Tags;
        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i].Name != "macro" || JinjaMacro.Match(tags[i].Args) is not { Success: true } m) continue;
            // the body runs to the endmacro of this macro (macros can hold macros)
            int depth = 0, bodyEnd = text.Length;
            for (int k = i + 1; k < tags.Count; k++)
            {
                if (tags[k].Name == "macro") depth++;
                else if (tags[k].Name == "endmacro" && depth-- == 0) { bodyEnd = tags[k].Start; break; }
            }
            string body = text[tags[i].End..Math.Max(tags[i].End, bodyEnd)];
            var parameters = SplitTopLevel(m.Groups["params"].Value, ',').Select(Trimmed).Where(p => p.Text.Length > 0)
                .Select(p => (Regex.Match(p.Text, @"^[A-Za-z_]\w*").Value, SplitTopLevel(p.Text, '=').Count > 1)).Where(p => p.Item1.Length > 0).ToList();
            bool Reads(string name) => Regex.IsMatch(body, $@"(?<![\w.]){name}\b");
            macros.TryAdd(m.Groups["name"].Value, new JinjaMacroDef(m.Groups["name"].Value, parameters, Reads("varargs"), Reads("kwargs"), Reads("caller")));
        }
        return macros;
    }

    static readonly Regex MacroCallStart = new(@"(?<![\w.])(?<chain>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Each call of a macro in the template's expressions and `{% call %}` tags, checked as Jinja checks it: more positional arguments than parameters (unless
    /// the macro reads `varargs`), a keyword it has no parameter for or that repeats a positional one (unless it reads `kwargs`), `{% call %}` of a macro that
    /// never calls `caller()`, and a parameter without a default that is not given (Jinja passes it undefined, which prints as nothing).
    /// </summary>
    void CheckMacroCalls(TemplateAnalysis a, Action<int, int, string, int, string> add)
    {
        var local = new Lazy<Dictionary<string, JinjaMacroDef>>(() => MacrosIn(a.Text));
        var imported = new Dictionary<string, Dictionary<string, JinjaMacroDef>?>(StringComparer.Ordinal);
        Dictionary<string, JinjaMacroDef>? MacrosOfTemplate(string name)
        {
            if (imported.TryGetValue(name, out var known)) return known;
            var file = Templates().FirstOrDefault(t => t.Name == name);
            return imported[name] = file.Path != null && TemplateTextOf(file.Path) is { } text ? MacrosIn(text) : null;
        }
        // the template an import or from tag names
        string? TemplateOf(TemplateVar v)
        {
            var owner = a.Parsed.Tags.FirstOrDefault(t => t.Name == v.Origin && t.Start <= v.Start && v.Start < t.End);
            var first = owner == null ? default : SplitTopLevel(owner.Args, ' ').Select(Trimmed).FirstOrDefault(p => p.Text.Length > 0);
            return first.Text == null ? null : DjangoTemplateParser.Unquote(first.Text);
        }
        JinjaMacroDef? Resolve(string chain, int offset)
        {
            var parts = chain.Split('.');
            if (JinjaScopeAt(a, offset).Find(parts[0]) is not { } v) return null;
            switch (v.Origin)
            {
                case "macro" when parts.Length == 1:
                    return local.Value.GetValueOrDefault(parts[0]);
                case "from" when parts.Length == 1:
                {
                    string original = NamesDefinedBy(a.Parsed.Tags.First(x => x.Name == "from" && x.Start <= v.Start && v.Start < x.End)).FirstOrDefault(d => d.Name == v.Name).Value ?? v.Name;
                    return TemplateOf(v) is { } fromFile ? MacrosOfTemplate(fromFile)?.GetValueOrDefault(original) : null;
                }
                case "import" when parts.Length == 2:
                    return TemplateOf(v) is { } importFile ? MacrosOfTemplate(importFile)?.GetValueOrDefault(parts[1]) : null;
            }
            return null;
        }

        var callTagExprs = a.Parsed.Tags.Where(t => t.Name == "call").SelectMany(ExpressionsOf).Select(e => e.Offset).ToHashSet();
        foreach (var (expr, at) in AllExpressions(a))
        {
            string masked = MaskStrings(expr);
            foreach (Match m in MacroCallStart.Matches(masked))
            {
                // a method of something else (loop.cycle, x.items) has a dot before the chain; filters (|f(...)) are not macros
                int before = m.Index - 1;
                while (before >= 0 && masked[before] == ' ') before--;
                if (before >= 0 && masked[before] == '|') continue;
                string chain = m.Groups["chain"].Value;
                if (Resolve(chain, at + m.Index) is not { } macro) continue;
                int open = m.Index + m.Length - 1, close = -1, depth = 0;
                for (int i = open; i < masked.Length; i++)
                {
                    if (masked[i] == '(') depth++;
                    else if (masked[i] == ')' && --depth == 0) { close = i; break; }
                }
                if (close < 0) continue;
                int nameStart = at + m.Index, nameEnd = nameStart + chain.Length;
                var args = SplitTopLevel(expr[(open + 1)..close], ',').Select(Trimmed).Where(p => p.Text.Length > 0).ToList();
                if (args.Any(p => p.Text.StartsWith('*'))) continue;   // *list or **dict: the arguments are not known
                var keywords = args.Select(p => (Part: p, Kw: Regex.Match(p.Text, @"^(?<n>[A-Za-z_]\w*)\s*=(?!=)"))).Where(x => x.Kw.Success).ToList();
                int positional = args.Count - keywords.Count;
                var names = macro.Params.Select(p => p.Name).ToList();
                if (positional > names.Count && !macro.Varargs)
                    add(nameStart, nameEnd, $"macro '{macro.Name}' takes not more than {names.Count} argument(s), and {positional} are given", Severity.Error, "jinja-macro-call");
                foreach (var (part, kw) in keywords)
                {
                    string name = kw.Groups["n"].Value;
                    int s = at + open + 1 + part.Offset;
                    int index = names.IndexOf(name);
                    if (index >= 0 && index < positional && !macro.Kwargs)
                        add(s, s + name.Length, $"'{name}' is already given as the {Ordinal(index + 1)} argument: Jinja stops with \"macro '{macro.Name}' takes no keyword argument '{name}'\"", Severity.Error, "jinja-macro-call");
                    else if (index < 0 && !macro.Kwargs && name != "caller")
                        add(s, s + name.Length, $"macro '{macro.Name}' has no parameter '{name}'{(names.Count > 0 ? $" (it takes {string.Join(", ", names)})" : "")}", Severity.Error, "jinja-macro-call");
                }
                // in {% call %}, the macro is the one called first and takes the body as caller
                if (callTagExprs.Contains(at) && m.Index == expr.Length - expr.TrimStart().Length && !macro.Caller)
                    add(nameStart, nameEnd, $"macro '{macro.Name}' never calls caller(), so {{% call %}} cannot give it a body: Jinja stops with \"invoked with two values for the special caller argument\"", Severity.Error, "jinja-macro-call");
                var given = new HashSet<string>(names.Take(positional).Concat(keywords.Select(k => k.Kw.Groups["n"].Value)), StringComparer.Ordinal);
                var missing = macro.Params.Where(p => !p.HasDefault && !given.Contains(p.Name)).Select(p => p.Name).ToList();
                if (missing.Count > 0)
                    add(nameStart, nameEnd, $"{string.Join(", ", missing.Select(n => $"'{n}'"))} of macro '{macro.Name}' {(missing.Count == 1 ? "is" : "are")} not given: Jinja passes {(missing.Count == 1 ? "it" : "them")} undefined, which prints as nothing", Severity.Warning, "jinja-macro-call");
            }
        }
    }

    static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => $"{n}th" };
}
