using System.Text.Json;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Snippets: live templates typed at the start of a statement, postfix templates typed after an expression, and the user's own.</summary>
public sealed partial class LanguageService
{
    enum TemplateContext { Statement, ClassBody }

    /// <param name="Body">Monaco snippet syntax: $1, ${2:name}, $0 for where the cursor ends; a tab is one level of indentation.</param>
    /// <param name="ImportModule">A module the template needs (with <paramref name="ImportName"/>: `from module import name`, else `import module`).</param>
    sealed record LiveTemplate(string Prefix, string Description, string Body, TemplateContext Context = TemplateContext.Statement,
        string? ImportModule = null, string? ImportName = null);

    sealed record PostfixTemplate(string Key, string Description, string Template, string Example);

    static readonly LiveTemplate[] LiveTemplates =
    {
        new("if", "if statement", "if ${1:condition}:\n\t${0:pass}"),
        new("ife", "if / else", "if ${1:condition}:\n\t${2:pass}\nelse:\n\t${0:pass}"),
        new("elif", "elif branch", "elif ${1:condition}:\n\t${0:pass}"),
        new("else", "else branch", "else:\n\t${0:pass}"),
        new("for", "for loop", "for ${1:item} in ${2:items}:\n\t${0:pass}"),
        new("fori", "for i in range(n)", "for ${1:i} in range(${2:n}):\n\t${0:pass}"),
        new("enum", "for i, item in enumerate(items)", "for ${1:i}, ${2:item} in enumerate(${3:items}):\n\t${0:pass}"),
        new("itemsfor", "for key, value in d.items()", "for ${1:key}, ${2:value} in ${3:mapping}.items():\n\t${0:pass}"),
        new("while", "while loop", "while ${1:condition}:\n\t${0:pass}"),
        new("try", "try / except", "try:\n\t${1:pass}\nexcept ${2:Exception} as ${3:e}:\n\t${0:raise}"),
        new("tryf", "try / finally", "try:\n\t${1:pass}\nfinally:\n\t${0:pass}"),
        new("with", "with statement", "with ${1:expression} as ${2:target}:\n\t${0:pass}"),
        new("wopen", "with open(...) as f", "with open(${1:path}, ${2:\"r\"}) as ${3:f}:\n\t${0:data = ${3:f}.read()}"),
        new("def", "function", "def ${1:name}(${2}):\n\t${0:pass}"),
        new("adef", "async function", "async def ${1:name}(${2}):\n\t${0:pass}"),
        new("class", "class", "class ${1:Name}:\n\t${0:pass}"),
        new("match", "match statement", "match ${1:subject}:\n\tcase ${2:pattern}:\n\t\t${0:pass}"),
        new("main", "if __name__ == \"__main__\"", "if __name__ == \"__main__\":\n\t${0:main()}"),
        new("ifmain", "if __name__ == \"__main__\"", "if __name__ == \"__main__\":\n\t${0:main()}"),
        new("init", "__init__ method", "def __init__(self${1}):\n\t${0:pass}", TemplateContext.ClassBody),
        new("defs", "method", "def ${1:name}(self${2}):\n\t${0:pass}", TemplateContext.ClassBody),
        new("prop", "property", "@property\ndef ${1:name}(self):\n\treturn self._${1:name}", TemplateContext.ClassBody),
        new("cm", "class method", "@classmethod\ndef ${1:name}(cls${2}):\n\t${0:pass}", TemplateContext.ClassBody),
        new("sm", "static method", "@staticmethod\ndef ${1:name}(${2}):\n\t${0:pass}", TemplateContext.ClassBody),
        new("dc", "dataclass", "@dataclass\nclass ${1:Name}:\n\t${0:field: int}", TemplateContext.Statement, "dataclasses", "dataclass"),
        new("log", "logger for this module", "logger = logging.getLogger(__name__)", TemplateContext.Statement, "logging"),
        new("argp", "argument parser", "parser = argparse.ArgumentParser(description=${1:\"\"})\nparser.add_argument(${2:\"--flag\"})\nargs = parser.parse_args()", TemplateContext.Statement, "argparse"),
        new("test", "test function", "def test_${1:name}():\n\t${0:pass}"),
        new("fixture", "pytest fixture", "@pytest.fixture\ndef ${1:name}():\n\t${0:pass}", TemplateContext.Statement, "pytest"),
        new("parametrize", "pytest parametrize", "@pytest.mark.parametrize(\"${1:arg}\", [${2}])", TemplateContext.Statement, "pytest"),
        new("lc", "list comprehension", "[${1:x} for ${1:x} in ${2:items}]"),
        new("bp", "breakpoint()", "breakpoint()"),
        new("todo", "TODO comment", "# TODO: ${0}"),
    };

    static readonly PostfixTemplate[] PostfixTemplates =
    {
        new("if", "if expr:", "if {e}:\n\t$0", "if x:"),
        new("ifnot", "if not expr:", "if not {e}:\n\t$0", "if not x:"),
        new("none", "if expr is None:", "if {e} is None:\n\t$0", "if x is None:"),
        new("notnone", "if expr is not None:", "if {e} is not None:\n\t$0", "if x is not None:"),
        new("not", "not expr", "not {e}", "not x"),
        new("while", "while expr:", "while {e}:\n\t$0", "while x:"),
        new("for", "for item in expr:", "for ${1:item} in {e}:\n\t$0", "for item in x:"),
        new("fori", "for i in range(len(expr)):", "for ${1:i} in range(len({e})):\n\t$0", "for i in range(len(x)):"),
        new("enum", "for i, item in enumerate(expr):", "for ${1:i}, ${2:item} in enumerate({e}):\n\t$0", "for i, item in enumerate(x):"),
        new("items", "for key, value in expr.items():", "for ${1:key}, ${2:value} in {e}.items():\n\t$0", "for key, value in x.items():"),
        new("with", "with expr as f:", "with {e} as ${1:f}:\n\t$0", "with x as f:"),
        new("print", "print(expr)", "print({e})", "print(x)"),
        new("len", "len(expr)", "len({e})", "len(x)"),
        new("str", "str(expr)", "str({e})", "str(x)"),
        new("int", "int(expr)", "int({e})", "int(x)"),
        new("float", "float(expr)", "float({e})", "float(x)"),
        new("bool", "bool(expr)", "bool({e})", "bool(x)"),
        new("list", "list(expr)", "list({e})", "list(x)"),
        new("set", "set(expr)", "set({e})", "set(x)"),
        new("tuple", "tuple(expr)", "tuple({e})", "tuple(x)"),
        new("sorted", "sorted(expr)", "sorted({e})", "sorted(x)"),
        new("reversed", "reversed(expr)", "reversed({e})", "reversed(x)"),
        new("sum", "sum(expr)", "sum({e})", "sum(x)"),
        new("max", "max(expr)", "max({e})", "max(x)"),
        new("min", "min(expr)", "min({e})", "min(x)"),
        new("any", "any(expr)", "any({e})", "any(x)"),
        new("all", "all(expr)", "all({e})", "all(x)"),
        new("repr", "repr(expr)", "repr({e})", "repr(x)"),
        new("type", "type(expr)", "type({e})", "type(x)"),
        new("isinstance", "isinstance(expr, Type)", "isinstance({e}, ${1:Type})", "isinstance(x, Type)"),
        new("return", "return expr", "return {e}", "return x"),
        new("yield", "yield expr", "yield {e}", "yield x"),
        new("await", "await expr", "await {e}", "await x"),
        new("assert", "assert expr", "assert {e}", "assert x"),
        new("raise", "raise expr", "raise {e}", "raise x"),
        new("del", "del expr", "del {e}", "del x"),
        new("var", "name = expr", "${1:name} = {e}", "name = x"),
        new("lc", "[x for x in expr]", "[${1:x} for ${1:x} in {e}]", "[x for x in expr]"),
        new("par", "(expr)", "({e})", "(x)"),
        new("f", "f\"{expr}\"", "f\"{{e}}\"", "f\"{x}\""),
    };

    /// <summary>Keywords that a live template of the same name stands in for at the start of a statement.</summary>
    static readonly HashSet<string> TemplatedKeywords = new(StringComparer.Ordinal) { "if", "elif", "else", "for", "while", "try", "with", "def", "class", "match" };

    static readonly string[] StatementKeywords =
    {
        "assert", "async", "await", "break", "continue", "del", "except", "finally", "from", "global", "import", "lambda", "nonlocal",
        "pass", "raise", "return", "yield", "not", "None", "True", "False",
    };

    static readonly string[] ExpressionKeywords = { "and", "await", "else", "for", "if", "in", "is", "lambda", "None", "not", "or", "True", "False", "yield" };

    /// <summary>Escapes text that goes into a snippet literally.</summary>
    static string SnippetEscape(string text) => text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");

    // ------------------------------------------------------------ the user's own snippets

    sealed record UserSnippet(string Prefix, string Description, string Body);

    (string Path, DateTime Stamp, List<UserSnippet> Snippets)? _userSnippets;

    /// <summary>VS Code style snippets from <c>.pymagic/snippets.json</c> in the workspace folder: {"name": {"prefix": "x", "body": ["line", "line"], "description": "..."}}.</summary>
    List<UserSnippet> UserSnippets()
    {
        foreach (var root in SourceRoots)
        {
            string path = System.IO.Path.Combine(root, ".pymagic", "snippets.json");
            if (!File.Exists(path)) continue;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_userSnippets is { } cached && cached.Path == path && cached.Stamp == stamp) return cached.Snippets;
            var list = new List<UserSnippet>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("body", out var body)) continue;
                    string text = body.ValueKind == JsonValueKind.Array ? string.Join("\n", body.EnumerateArray().Select(x => x.GetString() ?? "")) : body.GetString() ?? "";
                    var prefixes = entry.Value.TryGetProperty("prefix", out var p)
                        ? p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string> { p.GetString() ?? "" }
                        : new List<string> { entry.Name };
                    string description = entry.Value.TryGetProperty("description", out var d) ? d.GetString() ?? entry.Name : entry.Name;
                    foreach (var prefix in prefixes.Where(x => x.Length > 0)) list.Add(new UserSnippet(prefix, description, text));
                }
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { /* a broken file offers nothing */ }
            _userSnippets = (path, stamp, list);
            return list;
        }
        return new List<UserSnippet>();
    }

    // ------------------------------------------------------------ live templates

    /// <summary>Templates whose name starts with what is typed, at the start of a statement.</summary>
    IEnumerable<CompletionItem> LiveTemplateItems(ModuleInfo m, LineMap lines, string prefix, bool inClassBody)
    {
        foreach (var t in LiveTemplates)
        {
            if (!t.Prefix.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (t.Context == TemplateContext.ClassBody && !inClassBody) continue;
            List<TextEdit>? edits = null;
            if (t.ImportModule != null)
            {
                bool present = m.Semantics.ModuleScope.Symbols.TryGetValue(t.ImportName ?? t.ImportModule, out var existing) && existing.Declarations.Count > 0;
                if (!present) edits = new List<TextEdit> { ToTextEdit(lines, ImportEdit(m, t.ImportName == null ? null : t.ImportModule, t.ImportName ?? t.ImportModule)) };
            }
            yield return new CompletionItem(t.Prefix, CompletionKind.Snippet, t.Description, "4" + t.Prefix, t.Body, IsSnippet: true,
                AdditionalTextEdits: edits, Documentation: "```python\n" + Regex.Replace(t.Body, @"\$\{\d+:([^}]*)\}|\$\{?\d+\}?", "$1").Replace("\t", "    ") + "\n```");
        }
        foreach (var s in UserSnippets())
            if (s.Prefix.StartsWith(prefix, StringComparison.Ordinal))
                yield return new CompletionItem(s.Prefix, CompletionKind.Snippet, s.Description, "3" + s.Prefix, s.Body, IsSnippet: true,
                    Documentation: "```python\n" + s.Body.Replace("\t", "    ") + "\n```");
    }

    static TextEdit ToTextEdit(LineMap lines, Edit e) => new(lines.RangeOf(e.Start, e.End), e.Text);

    // ------------------------------------------------------------ postfix templates

    /// <summary>`expr.if`, `expr.for`, `expr.print`...: the expression before the dot is wrapped by the template.</summary>
    IEnumerable<CompletionItem> PostfixItems(LineMap lines, TypeEvaluator ev, string text, int exprStart, int dot, PyType recv, string prefix)
    {
        if (exprStart >= dot) yield break;
        string expression = text[exprStart..dot];
        if (expression.Contains('\n') || expression.Contains('\r') || expression.Trim().Length == 0) yield break;
        var removal = new List<TextEdit> { new(lines.RangeOf(exprStart, dot + 1), "") };

        bool iterable = recv is UnknownType || ev.IterElement(recv) is not UnknownType;
        bool mapping = recv is UnknownType || ev.AttributeType(recv, "items").Missing.Count == 0;
        bool sized = recv is UnknownType || ev.AttributeType(recv, "__len__").Missing.Count == 0;

        foreach (var t in PostfixTemplates)
        {
            if (!t.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (t.Key is "for" or "fori" or "enum" or "lc" or "sorted" or "reversed" or "sum" or "max" or "min" or "any" or "all" && !iterable) continue;
            if (t.Key == "items" && !mapping) continue;
            if (t.Key == "len" && !sized) continue;
            string body = t.Template.Replace("{e}", SnippetEscape(expression));
            yield return new CompletionItem(t.Key, CompletionKind.Snippet, "postfix · " + t.Description, "z9" + t.Key, body, IsSnippet: true,
                AdditionalTextEdits: removal, FilterText: t.Key, Documentation: $"Postfix template: `{t.Example}`");
        }
    }

    /// <summary>The start of the expression that ends at the dot: the attribute access's receiver, plus brackets that wrap it.</summary>
    static int ReceiverStart(string text, AttributeExpr access, int dot)
    {
        int s = access.Value.Start;
        int balance = 0;
        foreach (char c in text.AsSpan(s, Math.Max(0, dot - s)))
            balance += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
        // `(a + b).if`: the node starts inside the brackets
        while (balance < 0 && s > 0)
        {
            s--;
            if (text[s] is ')' or ']' or '}') balance--;
            else if (text[s] is '(' or '[' or '{') balance++;
        }
        return s;
    }
}
