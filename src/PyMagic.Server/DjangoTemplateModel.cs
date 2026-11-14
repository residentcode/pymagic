using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>`{% name args %}` with the span of the whole tag and where its arguments start.</summary>
internal sealed record TemplateTag(string Name, string Args, int Start, int End, int ArgsStart);

/// <summary>`{{ expression|filter }}` with the span of the expression text between the braces.</summary>
internal sealed record TemplateVariable(string Expression, int ExprStart, int Start, int End);

internal sealed record TemplateProblem(int Start, int End, string Message, string Code, bool IsError);

/// <summary>A Django (or Jinja-like) template read as a flat list of tags and variables, with the structure problems found on the way.</summary>
internal sealed class ParsedTemplate
{
    public List<TemplateTag> Tags { get; } = new();
    public List<TemplateVariable> Variables { get; } = new();
    public List<TemplateProblem> Problems { get; } = new();
}

internal static class DjangoTemplateParser
{
    static readonly Regex Token = new(@"\{%(?<tag>.*?)%\}|\{\{(?<var>.*?)\}\}|\{#[^\r\n]*?#\}", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Tags that open a block and the tag that closes it.</summary>
    public static readonly Dictionary<string, string> Closers = new(StringComparer.Ordinal)
    {
        ["if"] = "endif", ["for"] = "endfor", ["block"] = "endblock", ["with"] = "endwith", ["autoescape"] = "endautoescape",
        ["spaceless"] = "endspaceless", ["filter"] = "endfilter", ["blocktrans"] = "endblocktrans", ["blocktranslate"] = "endblocktranslate",
        ["language"] = "endlanguage", ["localtime"] = "endlocaltime", ["timezone"] = "endtimezone", ["cache"] = "endcache",
        ["ifchanged"] = "endifchanged", ["localize"] = "endlocalize", ["partialdef"] = "endpartialdef",
    };

    static readonly HashSet<string> Opaque = new(StringComparer.Ordinal) { "comment", "verbatim" };

    /// <summary>Jinja: comments may span lines, and `{%-` / `-%}` (or `+`) control the whitespace around a tag.</summary>
    /// <remarks>
    /// As Jinja's lexer, a string inside a tag or a variable is read whole: `{{ "}}" }}` ends at the second `}}`; a string that is never closed falls
    /// back to the first `}}`, so that the open string is what is reported.
    /// </remarks>
    static readonly Regex JinjaToken = new(
        @"\{%(?<tag>(?:'(?:\\.|[^'\\])*'|""(?:\\.|[^""\\])*""|[^'""%]|%(?!\}))*?)%\}|\{\{(?<var>(?:'(?:\\.|[^'\\])*'|""(?:\\.|[^""\\])*""|[^'""}]|\}(?!\}))*?)\}\}" +
        @"|\{%(?<tag>.*?)%\}|\{\{(?<var>.*?)\}\}|\{#.*?#\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Jinja's tags that open a block and the tag that closes it (`set` only in its block form, `{% set x %}...{% endset %}`).</summary>
    public static readonly Dictionary<string, string> JinjaClosers = new(StringComparer.Ordinal)
    {
        ["if"] = "endif", ["for"] = "endfor", ["block"] = "endblock", ["macro"] = "endmacro", ["call"] = "endcall", ["filter"] = "endfilter",
        ["set"] = "endset", ["with"] = "endwith", ["autoescape"] = "endautoescape", ["trans"] = "endtrans",
    };

    static readonly HashSet<string> JinjaOpaque = new(StringComparer.Ordinal) { "raw" };

    public static Dictionary<string, string> ClosersOf(bool jinja) => jinja ? JinjaClosers : Closers;

    /// <summary>The body of a Jinja tag or variable without its whitespace control marks: `- if x -` gives ` if x `.</summary>
    static (string Body, int Offset) WithoutMarks(string body, int offset, bool jinja)
    {
        if (!jinja) return (body, offset);
        if (body.Length > 0 && body[0] is '-' or '+') { body = body[1..]; offset++; }
        if (body.Length > 0 && body[^1] is '-' or '+') body = body[..^1];
        return (body, offset);
    }

    /// <summary>
    /// A Jinja template's tokens when the environment sets `line_statement_prefix` (a line `# for x in y:` is a tag) and `line_comment_prefix`
    /// (`## ...` up to the end of the line is a comment).
    /// </summary>
    static Regex JinjaTokenWith(string? lineStatement, string? lineComment)
    {
        if (lineStatement == null && lineComment == null) return JinjaToken;
        var parts = new List<string>();
        if (lineComment != null) parts.Add(Regex.Escape(lineComment) + @"[^\n]*");
        if (lineStatement != null) parts.Add(@"(?<=^|\n)[ \t]*" + Regex.Escape(lineStatement) + @"(?<line>[^\n]*)");
        parts.Add(JinjaToken.ToString());
        return new Regex(string.Join("|", parts), RegexOptions.Singleline);
    }

    public static ParsedTemplate Parse(string text, bool jinja = false, string? lineStatement = null, string? lineComment = null)
    {
        var token = jinja ? JinjaTokenWith(lineStatement, lineComment) : Token;
        var result = new ParsedTemplate();
        var stack = new List<TemplateTag>();
        var blocks = new Dictionary<string, TemplateTag>(StringComparer.Ordinal);
        var closers = ClosersOf(jinja);
        bool nodeBeforeExtends = false;
        bool sawExtends = false;
        int at = 0;
        while (at < text.Length)
        {
            var m = token.Match(text, at);
            if (!m.Success) break;
            at = m.Index + m.Length;
            if (m.Groups["var"].Success)
            {
                var g = m.Groups["var"];
                var (raw, rawAt) = WithoutMarks(g.Value, g.Index, jinja);
                string expr = raw.Trim();
                int lead = raw.Length - raw.TrimStart().Length;
                result.Variables.Add(new TemplateVariable(expr, rawAt + lead, m.Index, m.Index + m.Length));
                nodeBeforeExtends = true;
                continue;
            }
            bool lineTag = m.Groups["line"].Success;
            if (!m.Groups["tag"].Success && !lineTag) continue;   // a comment

            var tg = lineTag ? m.Groups["line"] : m.Groups["tag"];
            var (body, bodyAt) = lineTag ? (tg.Value, tg.Index) : WithoutMarks(tg.Value, tg.Index, jinja);
            // a line statement may end with a colon (`# for x in y:`), and a line comment after it is not part of it
            if (lineTag)
            {
                if (lineComment != null && body.IndexOf(lineComment, StringComparison.Ordinal) is int cut and >= 0) body = body[..cut];
                body = body.TrimEnd();
                if (body.EndsWith(':')) body = body[..^1];
            }
            int bodyLead = body.Length - body.TrimStart().Length;
            string trimmed = body.Trim();
            var nameMatch = Regex.Match(trimmed, @"^[\w]*");
            string name = nameMatch.Value;
            int afterName = bodyAt + bodyLead + name.Length;
            string args = trimmed[name.Length..].TrimStart();
            int argsStart = afterName + (trimmed.Length - name.Length - args.Length > 0 ? trimmed.Length - name.Length - args.Length : 0);
            var tag = new TemplateTag(name, args.TrimEnd(), m.Index, m.Index + m.Length, argsStart);
            result.Tags.Add(tag);

            if ((jinja ? JinjaOpaque : Opaque).Contains(name))
            {
                // the text up to the matching end tag is not template syntax
                var end = Regex.Match(text[at..], @"\{%[-+]?\s*end" + name + @"\b[^%]*%\}");
                if (end.Success) { at += end.Index + end.Length; result.Tags.Add(new TemplateTag("end" + name, "", at - end.Length, at, at - 2)); }
                else result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"Unclosed tag '{name}': add {{% end{name} %}}", "template-structure", true));
                nodeBeforeExtends = true;
                continue;
            }

            // Jinja prints what comes before {% extends %}, it does not refuse it
            if (name == "extends")
            {
                if (nodeBeforeExtends && !jinja) result.Problems.Add(new TemplateProblem(tag.Start, tag.End, "{% extends %} must be the first tag in the template", "template-structure", true));
                sawExtends = true;
            }
            else if (name.Length > 0) nodeBeforeExtends = true;

            if (name == "block")
            {
                string blockName = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (blockName.Length > 0 && !blocks.TryAdd(blockName, tag))
                    result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"Block '{blockName}' is defined twice", "template-structure", true));
            }

            // {% set x = 1 %} is a statement; only {% set x %}...{% endset %} is a block
            bool opens = closers.ContainsKey(name) && !(jinja && name == "set" && args.Contains('='));
            if (opens) { stack.Add(tag); continue; }
            if (name.StartsWith("end", StringComparison.Ordinal) && closers.ContainsValue(name))
            {
                string opener = closers.First(c => c.Value == name).Key;
                int depth = stack.FindLastIndex(t => t.Name == opener);
                if (depth < 0) { result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"'{name}' has no matching {{% {opener} %}}", "template-structure", true)); continue; }
                for (int i = stack.Count - 1; i > depth; i--)
                    result.Problems.Add(new TemplateProblem(stack[i].Start, stack[i].End, $"Unclosed tag '{stack[i].Name}': add {{% {closers[stack[i].Name]} %}}", "template-structure", true));
                // {% endblock content %} and {% endpartialdef card %} may repeat the name, which must be the opener's
                if (name is "endblock" or "endpartialdef" && args.Length > 0)
                {
                    string opened = stack[depth].Args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    string closed = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (closed != opened) result.Problems.Add(new TemplateProblem(argsStart, argsStart + closed.Length, $"{{% {name} {closed} %}} closes the {opener} '{opened}': the names must match", "template-structure", true));
                }
                stack.RemoveRange(depth, stack.Count - depth);
                continue;
            }
            if (name is "elif" or "else" || name == "empty" && !jinja)
            {
                string? top = stack.Count > 0 ? stack[^1].Name : null;
                bool fine = name switch { "elif" => top == "if", "empty" => top == "for", _ => top is "if" or "for" || !jinja && top == "ifchanged" };
                if (!fine) result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"'{name}' is not inside a tag that takes it", "template-structure", true));
            }
        }
        foreach (var open in stack)
            result.Problems.Add(new TemplateProblem(open.Start, open.End, $"Unclosed tag '{open.Name}': add {{% {closers[open.Name]} %}}", "template-structure", true));
        UnclosedDelimiters(text, jinja, result);
        _ = sawExtends;
        return result;
    }

    /// <summary>
    /// A {{, {% or {# that is never closed (`{{ name }</p>`): Django reads a tag only within its line, so its closer must be on the same line
    /// (else Django prints the text as it is); Jinja's may be on a later line, and one missing is its "unexpected end of template". The text
    /// of {% verbatim %}, {% comment %} and Jinja's {% raw %} is not looked at.
    /// </summary>
    static void UnclosedDelimiters(string text, bool jinja, ParsedTemplate result)
    {
        var opaque = new List<(int Start, int End)>();
        var names = jinja ? JinjaOpaque : Opaque;
        for (int t = 0; t < result.Tags.Count; t++)
            if (names.Contains(result.Tags[t].Name) && t + 1 < result.Tags.Count && result.Tags[t + 1].Name == "end" + result.Tags[t].Name)
                opaque.Add((result.Tags[t].End, result.Tags[t + 1].Start));
        int i = 0;
        while (i < text.Length - 1)
        {
            int at = text.IndexOf('{', i);
            if (at < 0 || at + 1 >= text.Length) break;
            char kind = text[at + 1];
            if (kind is not ('{' or '%' or '#') || opaque.Any(o => at >= o.Start && at < o.End)) { i = at + 1; continue; }
            string closer = kind switch { '{' => "}}", '%' => "%}", _ => "#}" };
            int limit = jinja ? text.Length : (text.IndexOf('\n', at) is int nl and >= 0 ? nl : text.Length);
            int close = text.IndexOf(closer, at + 2, limit - at - 2, StringComparison.Ordinal);
            if (close >= 0) { i = close + 2; continue; }
            string what = kind switch { '{' => "variable", '%' => "tag", _ => "comment" };
            string where = jinja ? "" : " on the same line";
            result.Problems.Add(new TemplateProblem(at, at + 2, $"Unclosed {what} '{{{kind}': add {closer}{where}", "template-structure", true));
            i = at + 2;
        }
    }

    /// <summary>The arguments of a tag split like Django does: whitespace apart from quoted strings.</summary>
    public static List<(string Text, int Offset)> SplitArguments(string args, int baseOffset)
    {
        var list = new List<(string, int)>();
        foreach (Match m in Regex.Matches(args, @"(?:[^\s""']+=)?(?:""[^""]*""|'[^']*')|\S+"))
            list.Add((m.Value, baseOffset + m.Index));
        return list;
    }

    /// <summary>The text of a quoted argument without its quotes, or null when it is not a plain string.</summary>
    public static string? Unquote(string arg) =>
        arg.Length >= 2 && (arg[0] == '"' && arg[^1] == '"' || arg[0] == '\'' && arg[^1] == '\'') ? arg[1..^1] : null;
}
