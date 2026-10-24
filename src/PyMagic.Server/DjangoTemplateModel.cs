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
        ["ifchanged"] = "endifchanged", ["localize"] = "endlocalize",
    };

    static readonly HashSet<string> Opaque = new(StringComparer.Ordinal) { "comment", "verbatim" };

    public static ParsedTemplate Parse(string text)
    {
        var result = new ParsedTemplate();
        var stack = new List<TemplateTag>();
        var blocks = new Dictionary<string, TemplateTag>(StringComparer.Ordinal);
        bool nodeBeforeExtends = false;
        bool sawExtends = false;
        int at = 0;
        while (at < text.Length)
        {
            var m = Token.Match(text, at);
            if (!m.Success) break;
            at = m.Index + m.Length;
            if (m.Groups["var"].Success)
            {
                var g = m.Groups["var"];
                string expr = g.Value.Trim();
                int lead = g.Value.Length - g.Value.TrimStart().Length;
                result.Variables.Add(new TemplateVariable(expr, g.Index + lead, m.Index, m.Index + m.Length));
                nodeBeforeExtends = true;
                continue;
            }
            if (!m.Groups["tag"].Success) continue;   // a comment

            var tg = m.Groups["tag"];
            string body = tg.Value;
            int bodyLead = body.Length - body.TrimStart().Length;
            string trimmed = body.Trim();
            var nameMatch = Regex.Match(trimmed, @"^[\w]*");
            string name = nameMatch.Value;
            int afterName = tg.Index + bodyLead + name.Length;
            string args = trimmed[name.Length..].TrimStart();
            int argsStart = afterName + (trimmed.Length - name.Length - args.Length > 0 ? trimmed.Length - name.Length - args.Length : 0);
            var tag = new TemplateTag(name, args.TrimEnd(), m.Index, m.Index + m.Length, argsStart);
            result.Tags.Add(tag);

            if (Opaque.Contains(name))
            {
                // the text up to the matching end tag is not template syntax
                var end = Regex.Match(text[at..], @"\{%\s*end" + name + @"\b[^%]*%\}");
                if (end.Success) { at += end.Index + end.Length; result.Tags.Add(new TemplateTag("end" + name, "", at - end.Length, at, at - 2)); }
                else result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"Unclosed tag '{name}': add {{% end{name} %}}", "template-structure", true));
                nodeBeforeExtends = true;
                continue;
            }

            if (name == "extends")
            {
                if (nodeBeforeExtends) result.Problems.Add(new TemplateProblem(tag.Start, tag.End, "{% extends %} must be the first tag in the template", "template-structure", true));
                sawExtends = true;
            }
            else if (name.Length > 0) nodeBeforeExtends = true;

            if (name == "block")
            {
                string blockName = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (blockName.Length > 0 && !blocks.TryAdd(blockName, tag))
                    result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"Block '{blockName}' is defined twice", "template-structure", true));
            }

            if (Closers.ContainsKey(name)) { stack.Add(tag); continue; }
            if (name.StartsWith("end", StringComparison.Ordinal) && Closers.ContainsValue(name))
            {
                string opener = Closers.First(c => c.Value == name).Key;
                int depth = stack.FindLastIndex(t => t.Name == opener);
                if (depth < 0) { result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"'{name}' has no matching {{% {opener} %}}", "template-structure", true)); continue; }
                for (int i = stack.Count - 1; i > depth; i--)
                    result.Problems.Add(new TemplateProblem(stack[i].Start, stack[i].End, $"Unclosed tag '{stack[i].Name}': add {{% {Closers[stack[i].Name]} %}}", "template-structure", true));
                stack.RemoveRange(depth, stack.Count - depth);
                continue;
            }
            if (name is "elif" or "else" or "empty")
            {
                string? top = stack.Count > 0 ? stack[^1].Name : null;
                bool fine = name switch { "elif" => top == "if", "empty" => top == "for", _ => top is "if" or "for" or "ifchanged" };
                if (!fine) result.Problems.Add(new TemplateProblem(tag.Start, tag.End, $"'{name}' is not inside a tag that takes it", "template-structure", true));
            }
        }
        foreach (var open in stack)
            result.Problems.Add(new TemplateProblem(open.Start, open.End, $"Unclosed tag '{open.Name}': add {{% {Closers[open.Name]} %}}", "template-structure", true));
        _ = sawExtends;
        return result;
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
