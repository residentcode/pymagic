using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// BDD with behave and pytest-bdd, as PyCharm Professional's Gherkin, Behave and pytest-bdd support checks it: in a .feature file, a step no
/// step definition of the project matches (with the fix "Create step definition"), a Scenario Outline without Examples, a table whose rows
/// have different numbers of cells, and an outline's &lt;parameter&gt; with no Examples column (or a column no step uses); in Python, a step
/// definition no step of any .feature file uses. Step texts are matched the way the frameworks do: behave's default "parse" patterns
/// (`{name}`, `{n:d}`) and `use_step_matcher("re")`, pytest-bdd's plain strings, `parsers.parse`, `parsers.cfparse` and `parsers.re`.
/// The BDD plugin (Settings > Plugins) switches all of it off.
/// </summary>
public sealed partial class LanguageService
{
    readonly Dictionary<string, (string Text, int Version)> _featureDocs = new();

    static bool IsFeatureUri(string uri) => uri.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ Gherkin

    public sealed record GherkinStep(string Keyword, string Kind, string Text, int Line, int Start, int End);
    public sealed record GherkinTable(List<(int Line, List<(string Text, int Start, int End)> Cells)> Rows);
    public sealed record GherkinExamples(int Line, int Start, int End, GherkinTable? Table);
    public sealed record GherkinScenario(string Name, bool Outline, bool Background, int Line, int Start, int End, List<GherkinStep> Steps, List<GherkinExamples> Examples);
    public sealed record GherkinFeature(List<GherkinScenario> Scenarios, List<GherkinTable> Tables);

    static readonly Regex StepLine = new(@"^(?<kw>Given|When|Then|And|But|\*)\s+(?<text>.*?)\s*$", RegexOptions.Compiled);
    static readonly Regex BlockLine = new(@"^(?<kw>Feature|Rule|Background|Scenario Outline|Scenario Template|Scenario|Example|Examples|Scenarios)\s*:(?<name>.*)$", RegexOptions.Compiled);

    /// <summary>Reads a .feature file (English keywords): its scenarios with their steps (And / But / * take the kind of the step before), Examples and tables.</summary>
    public static GherkinFeature ParseGherkin(string text)
    {
        var scenarios = new List<GherkinScenario>();
        var tables = new List<GherkinTable>();
        GherkinScenario? current = null;
        GherkinTable? table = null;
        GherkinExamples? examples = null;
        string kind = "given";
        string? docFence = null;
        int offset = 0, line = 0;
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            int indent = l.Length - l.TrimStart().Length;
            string body = l.Trim();
            int start = offset + indent;
            offset += raw.Length + 1;
            int lineNo = line++;
            if (docFence != null) { if (body.StartsWith(docFence, StringComparison.Ordinal)) docFence = null; continue; }
            if (body.StartsWith("\"\"\"", StringComparison.Ordinal) || body.StartsWith("```", StringComparison.Ordinal)) { docFence = body[..3]; table = null; continue; }
            if (body.StartsWith('|'))
            {
                if (table == null)
                {
                    table = new GherkinTable(new());
                    tables.Add(table);
                    if (examples is { Table: null } ex && current != null) { examples = ex with { Table = table }; current.Examples[^1] = examples; }
                }
                var cells = new List<(string, int, int)>();
                int i = 1;
                while (i < body.Length)
                {
                    int j = i;
                    var cell = new StringBuilder();
                    while (j < body.Length && body[j] != '|') { if (body[j] == '\\' && j + 1 < body.Length) { cell.Append(body[j + 1]); j += 2; continue; } cell.Append(body[j]); j++; }
                    if (j >= body.Length) break;   // text after the last bar is not a cell
                    string s = cell.ToString();
                    int lead = s.Length - s.TrimStart().Length;
                    cells.Add((s.Trim(), start + i + lead, start + i + lead + s.Trim().Length));
                    i = j + 1;
                }
                table.Rows.Add((lineNo, cells));
                continue;
            }
            table = null;
            if (body.Length == 0 || body.StartsWith('#') || body.StartsWith('@')) continue;
            var block = BlockLine.Match(body);
            if (block.Success)
            {
                string kw = block.Groups["kw"].Value;
                if (kw is "Examples" or "Scenarios")
                {
                    if (current == null) continue;
                    examples = new GherkinExamples(lineNo, start, start + body.Length, null);
                    current.Examples.Add(examples);
                    continue;
                }
                examples = null;
                if (kw is "Feature" or "Rule") { current = null; continue; }
                current = new GherkinScenario(block.Groups["name"].Value.Trim(), kw is "Scenario Outline" or "Scenario Template", kw == "Background", lineNo, start, start + body.Length, new(), new());
                scenarios.Add(current);
                kind = "given";
                continue;
            }
            var step = StepLine.Match(body);
            if (step.Success && current != null && examples == null)
            {
                string kw = step.Groups["kw"].Value;
                if (kw is "Given" or "When" or "Then") kind = kw.ToLowerInvariant();
                int textStart = start + step.Groups["text"].Index;
                current.Steps.Add(new GherkinStep(kw, kind, step.Groups["text"].Value, lineNo, textStart, textStart + step.Groups["text"].Length));
            }
        }
        return new GherkinFeature(scenarios, tables);
    }

    // ------------------------------------------------------------ step definitions

    /// <summary>
    /// One step definition: its kind (given, when, then or step for any), how its pattern is read ("parse", "re" or "exact"; null when the pattern is not a
    /// literal, which then matches every step), the framework, and where it is.
    /// </summary>
    public sealed record StepDefinition(string Kind, string? Pattern, string Matcher, string Framework, string Uri, int Start, int End, string FunctionName)
    {
        Regex? _regex;
        bool _broken;

        public bool Matches(string kind, string text)
        {
            if (Kind != "step" && Kind != kind) return false;
            if (Pattern == null || _broken) return true;
            try { _regex ??= new Regex(ToRegex(Pattern, Matcher), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { _broken = true; return true; }
            try { return _regex.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return true; }
        }
    }

    /// <summary>A pattern as a .NET regular expression that must match the whole step text.</summary>
    public static string ToRegex(string pattern, string matcher)
    {
        if (matcher == "exact") return "^" + Regex.Escape(pattern) + "$";
        if (matcher == "re")
        {
            // Python's named groups and anchors; the rest of re's syntax reads the same here
            string re = Regex.Replace(pattern, @"\(\?P<(\w+)>", "(?<$1>");
            re = Regex.Replace(re, @"\(\?P=(\w+)\)", @"\k<$1>");
            if (re.EndsWith(@"\Z", StringComparison.Ordinal)) re = re[..^2] + "$";
            return "^(?:" + re.TrimStart('^').TrimEnd('$') + ")$";
        }
        // parse / cfparse: {name}, {name:d}, {:w}; {{ and }} are braces
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '{' && i + 1 < pattern.Length && pattern[i + 1] == '{') { sb.Append(@"\{"); i++; continue; }
            if (c == '}' && i + 1 < pattern.Length && pattern[i + 1] == '}') { sb.Append(@"\}"); i++; continue; }
            if (c == '{')
            {
                int close = pattern.IndexOf('}', i);
                if (close < 0) { sb.Append(Regex.Escape(pattern[i..])); break; }
                string field = pattern[(i + 1)..close];
                int colon = field.IndexOf(':');
                string format = colon < 0 ? "" : field[(colon + 1)..];
                sb.Append(format.TrimEnd('?', '+', '*') switch
                {
                    "d" or "n" => @"[-+]?\d+",
                    "f" or "F" or "g" or "e" => @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
                    "w" => @"\w+",
                    "W" => @"\W+",
                    "S" => @"\S+",
                    _ => ".+?",
                });
                i = close;
                continue;
            }
            sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.Append('$').ToString();
    }

    static readonly Regex StepDecorator = new(@"(^|\.)(given|when|then|step)$", RegexOptions.Compiled);

    /// <summary>The text of a string literal for a pattern: raw strings as written, others with their escapes read; null for anything else.</summary>
    static string? PatternLiteral(Expr? e)
    {
        if (e is not ConstExpr { Kind: ConstKind.Str, IsFString: false } c) return null;
        var m = Regex.Match(c.Text, "^(?<p>[rRuU]?)(?<q>\"\"\"|'''|\"|')(?<b>.*)\\k<q>$", RegexOptions.Singleline);
        if (!m.Success) return FixtureModel.StringValue(c);
        string b = m.Groups["b"].Value;
        if (m.Groups["p"].Value is "r" or "R") return b;
        if (!b.Contains('\\')) return b;
        try { return Regex.Unescape(b.Replace("\\'", "'").Replace("\\\"", "\"")); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The step definitions of one Python module (one importing behave or pytest_bdd), in the order they are written.</summary>
    public static List<StepDefinition> StepDefinitionsIn(Module ast, string source, string uri)
    {
        var result = new List<StepDefinition>();
        string? framework = source.Contains("pytest_bdd", StringComparison.Ordinal) ? "pytest-bdd" : source.Contains("behave", StringComparison.Ordinal) ? "behave" : null;
        if (framework == null) return result;
        string matcher = framework == "behave" ? "parse" : "exact";
        foreach (var stmt in AstChildren.Descendants(ast))
        {
            // behave: use_step_matcher("re") changes how the definitions after it are read
            if (stmt is ExprStmt { Value: CallExpr use } && source[use.Func.Start..use.Func.End].EndsWith("use_step_matcher", StringComparison.Ordinal)
                && FixtureModel.StringValue(use.Args.FirstOrDefault()?.Value) is { } chosen)
                matcher = chosen switch { "re" => "re", "cfparse" or "parse" => "parse", _ => "unknown" };
            if (stmt is not FunctionDef f) continue;
            foreach (var d in f.Decorators)
            {
                if (d is not CallExpr call || !StepDecorator.IsMatch(source[call.Func.Start..call.Func.End]) || call.Args.Count == 0) continue;
                string kind = StepDecorator.Match(source[call.Func.Start..call.Func.End]).Groups[2].Value;
                var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name is "name" or "step_name")?.Value;
                if (arg == null) continue;
                string? pattern = null;
                string how = matcher;
                if (arg is CallExpr p && source[p.Func.Start..p.Func.End] is var pf && Regex.IsMatch(pf, @"(^|\.)(parse|cfparse|re)$"))
                {
                    how = pf.EndsWith("re", StringComparison.Ordinal) && !pf.EndsWith("cfparse", StringComparison.Ordinal) && !pf.EndsWith("parse", StringComparison.Ordinal) ? "re" : "parse";
                    pattern = PatternLiteral(p.Args.FirstOrDefault()?.Value);
                }
                else if (arg is ConstExpr) pattern = PatternLiteral(arg);
                // a matcher of behave's that is not known, or a pattern that is not a literal: it may match anything
                if (how == "unknown") pattern = null;
                result.Add(new StepDefinition(kind, pattern, how, framework, uri, arg.Start, arg.End, f.Name));
            }
        }
        return result;
    }

    readonly Dictionary<string, (DateTime Stamp, List<StepDefinition> Steps)> _stepFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (DateTime Stamp, string Text)> _featureFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every step definition of the project: the open buffers, and the files on disk that mention behave or pytest_bdd.</summary>
    List<StepDefinition> ProjectStepDefinitions()
    {
        var result = new List<StepDefinition>();
        var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _docs.Values)
        {
            if (d.Path != null) openPaths.Add(System.IO.Path.GetFullPath(d.Path));
            if (d.Text.Contains("behave", StringComparison.Ordinal) || d.Text.Contains("pytest_bdd", StringComparison.Ordinal))
                result.AddRange(StepDefinitionsIn(Parser.Parse(d.Text).Module, d.Text, d.Uri));
        }
        foreach (var path in ProjectFiles())
        {
            string full = System.IO.Path.GetFullPath(path);
            if (openPaths.Contains(full)) continue;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(full); } catch (IOException) { continue; }
            if (!_stepFiles.TryGetValue(full, out var cached) || cached.Stamp != stamp)
            {
                string text;
                try { text = File.ReadAllText(full); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var steps = text.Contains("behave", StringComparison.Ordinal) || text.Contains("pytest_bdd", StringComparison.Ordinal)
                    ? StepDefinitionsIn(Parser.Parse(text).Module, text, UriOfPath(full)) : new List<StepDefinition>();
                _stepFiles[full] = cached = (stamp, steps);
            }
            result.AddRange(cached.Steps);
        }
        return result;
    }

    /// <summary>Every .feature file of the project: the open ones and those on disk, by URI.</summary>
    Dictionary<string, string> ProjectFeatures()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uri, doc) in _featureDocs) result[uri] = doc.Text;
        var open = new HashSet<string>(_featureDocs.Keys.Select(PathOfUri).Where(p => p != null).Select(p => System.IO.Path.GetFullPath(p!)), StringComparer.OrdinalIgnoreCase);
        foreach (var path in ProjectFilesWithExtension(".feature"))
        {
            string full = System.IO.Path.GetFullPath(path);
            if (open.Contains(full)) continue;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(full); } catch (IOException) { continue; }
            if (!_featureFiles.TryGetValue(full, out var cached) || cached.Stamp != stamp)
            {
                try { _featureFiles[full] = cached = (stamp, File.ReadAllText(full)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            }
            result[UriOfPath(full)] = cached.Text;
        }
        return result;
    }

    /// <summary>The texts a step stands for: an outline's step once for each row of its Examples (none when they have no rows).</summary>
    static IEnumerable<string> StepTexts(GherkinScenario s, GherkinStep step)
    {
        if (!s.Outline || !step.Text.Contains('<')) { yield return step.Text; yield break; }
        foreach (var ex in s.Examples)
        {
            if (ex.Table is not { Rows.Count: > 1 } t) continue;
            var header = t.Rows[0].Cells.Select(c => c.Text).ToList();
            foreach (var row in t.Rows.Skip(1))
            {
                string text = step.Text;
                for (int i = 0; i < header.Count && i < row.Cells.Count; i++) text = text.Replace("<" + header[i] + ">", row.Cells[i].Text);
                yield return text;
            }
        }
    }

    // ------------------------------------------------------------ the checks

    static readonly Regex OutlineParameter = new(@"<([^<>\r\n]+)>", RegexOptions.Compiled);

    IReadOnlyList<LspDiagnostic> FeatureDiagnostics(string uri)
    {
        if (!Plugins.On("bdd") || !_featureDocs.TryGetValue(uri, out var doc)) return Array.Empty<LspDiagnostic>();
        var found = new List<(int Start, int End, string Message, string Code, int Severity)>();
        var feature = ParseGherkin(doc.Text);
        var definitions = ProjectStepDefinitions();
        foreach (var s in feature.Scenarios)
        {
            if (definitions.Count > 0)
                foreach (var step in s.Steps)
                {
                    var texts = StepTexts(s, step).ToList();
                    if (texts.Count > 0 && !texts.Any(t => definitions.Any(d => d.Matches(step.Kind, t))))
                        found.Add((step.Start, step.End, $"Undefined step reference: '{step.Text}'", "bdd-undefined-step", Severity.Warning));
                }
            if (!s.Outline) continue;
            if (s.Examples.Count == 0 || s.Examples.All(e => e.Table is not { Rows.Count: > 1 }))
                found.Add((s.Start, s.End, s.Examples.Count == 0 ? "Scenario Outline has no Examples" : "The Examples of this Scenario Outline have no rows", "bdd-outline-examples", Severity.Warning));
            var used = new HashSet<string>(s.Steps.SelectMany(st => OutlineParameter.Matches(st.Text).Select(x => x.Groups[1].Value)), StringComparer.Ordinal);
            var columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ex in s.Examples)
            {
                if (ex.Table is not { Rows.Count: > 0 } t) continue;
                foreach (var cell in t.Rows[0].Cells)
                {
                    columns.Add(cell.Text);
                    if (!used.Contains(cell.Text))
                        found.Add((cell.Start, cell.End, $"Column '{cell.Text}' is not used in the steps of the outline", "bdd-examples-column", Severity.Information));
                }
            }
            if (s.Examples.Any(e => e.Table is { Rows.Count: > 0 }))
                foreach (var step in s.Steps)
                    foreach (Match p in OutlineParameter.Matches(step.Text))
                        if (!columns.Contains(p.Groups[1].Value))
                            found.Add((step.Start + p.Index, step.Start + p.Index + p.Length, $"No Examples column for '<{p.Groups[1].Value}>'", "bdd-examples-column", Severity.Warning));
        }
        foreach (var t in feature.Tables)
        {
            int width = t.Rows[0].Cells.Count;
            foreach (var row in t.Rows.Skip(1))
                if (row.Cells.Count != width && row.Cells.Count > 0)
                    found.Add((row.Cells[0].Start, row.Cells[^1].End, $"This row has {row.Cells.Count} cells, the table's first row {width}", "bdd-table", Severity.Error));
        }
        var lines = new LineMap(doc.Text);
        var profile = InspectionProfile();
        var result = new List<LspDiagnostic>();
        foreach (var f in found)
        {
            string level = profile.GetValueOrDefault(f.Code) ?? (OffByDefault.Contains(f.Code) ? "off" : "");
            if (level == "off") continue;
            result.Add(new LspDiagnostic(lines.RangeOf(f.Start, Math.Max(f.End, f.Start + 1)), level.Length > 0 ? SeverityOf(level) : f.Severity, f.Message, Code: f.Code));
        }
        return result;
    }

    /// <summary>Step definitions of a Python module that no step of any .feature file of the project uses.</summary>
    IEnumerable<Diag> BddDiagnostics(ModuleInfo m, string? uri)
    {
        if (!Plugins.On("bdd") || m.IsStub || uri == null || !m.Source.Contains("behave", StringComparison.Ordinal) && !m.Source.Contains("pytest_bdd", StringComparison.Ordinal)) yield break;
        var mine = StepDefinitionsIn(m.Ast, m.Source, uri).Where(d => d.Pattern != null).ToList();
        if (mine.Count == 0) yield break;
        var features = ProjectFeatures();
        if (features.Count == 0) yield break;
        var steps = new List<(string Kind, string Text)>();
        foreach (var text in features.Values)
            foreach (var s in ParseGherkin(text).Scenarios)
                foreach (var step in s.Steps)
                {
                    var texts = StepTexts(s, step).ToList();
                    // an outline step without rows stands for no text: its parameters may be anything
                    if (texts.Count == 0) texts.AddRange(new[] { OutlineParameter.Replace(step.Text, "1"), OutlineParameter.Replace(step.Text, "x") });
                    steps.AddRange(texts.Select(t => (step.Kind, t)));
                }
        foreach (var d in mine)
        {
            if (steps.Any(s => d.Matches(s.Kind, s.Text))) continue;
            var f = new Finding(d.Start, d.End - d.Start, $"Step definition '{d.FunctionName}' is not used by any step of the project's .feature files", "bdd-unused-step", FindingLevel.Weak);
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }
    }

    // ------------------------------------------------------------ Create step definition

    /// <summary>The fix for an undefined step: a definition added at the end of the project's step file of the framework the project uses.</summary>
    List<CodeAction> FeatureActions(string uri, Range range)
    {
        var actions = new List<CodeAction>();
        if (!Plugins.On("bdd") || !_featureDocs.TryGetValue(uri, out var doc)) return actions;
        var lines = new LineMap(doc.Text);
        int at = lines.OffsetOf(range.Start);
        var feature = ParseGherkin(doc.Text);
        var step = feature.Scenarios.SelectMany(s => s.Steps).FirstOrDefault(s => s.Start <= at && at <= s.End);
        if (step == null) return actions;
        var definitions = ProjectStepDefinitions();
        if (definitions.Any(d => d.Matches(step.Kind, step.Text) && d.Pattern != null)) return actions;
        // the file to add to: the one that already holds the most definitions, behave's steps folder first
        var target = definitions.GroupBy(d => d.Uri).OrderByDescending(g => g.Key.Contains("/steps/", StringComparison.OrdinalIgnoreCase)).ThenByDescending(g => g.Count()).FirstOrDefault();
        if (target == null) return actions;
        string targetUri = target.Key;
        string? text = _docs.TryGetValue(targetUri, out var open) ? open.Text : PathOfUri(targetUri) is { } p && File.Exists(p) ? File.ReadAllText(p) : null;
        if (text == null) return actions;
        string framework = target.First().Framework;
        string decorator = step.Kind;
        string literal = step.Text.Contains('\'') && !step.Text.Contains('"') ? $"\"{Escape(step.Text, framework)}\"" : $"'{Escape(step.Text, framework).Replace("'", "\\'")}'";
        string name = framework == "behave" ? "step_impl" : StepFunctionName(step.Text);
        string parameters = framework == "behave" ? "context" : "";
        string message = $"{char.ToUpperInvariant(decorator[0])}{decorator[1..]} {step.Text}".Replace("\\", "\\\\").Replace("'", "\\'");
        var code = new StringBuilder();
        string trimmed = text.TrimEnd();
        code.Append(trimmed.Length > 0 ? "\n\n\n" : "");
        code.Append($"@{DecoratorPrefix(text, decorator, framework)}{decorator}({literal})\ndef {name}({parameters}):\n    raise NotImplementedError('STEP: {message}')\n");
        var tl = new LineMap(text);
        var edits = new List<TextEdit> { new(tl.RangeOf(trimmed.Length, text.Length), code.ToString()) };
        // the decorator imported when the file neither imports it nor star-imports the framework
        if (DecoratorPrefix(text, decorator, framework).Length == 0 && !Regex.IsMatch(text, $@"^from\s+{(framework == "behave" ? "behave" : "pytest_bdd")}\s+import\s+(\*|[^\n]*\b{decorator}\b)", RegexOptions.Multiline))
            edits.Insert(0, new TextEdit(tl.RangeOf(0, 0), $"from {(framework == "behave" ? "behave" : "pytest_bdd")} import {decorator}\n"));
        string file = System.IO.Path.GetFileName(PathOfUri(targetUri) ?? targetUri);
        actions.Add(new CodeAction($"Create step definition in {file}", QuickFixKind, new WorkspaceEdit(new Dictionary<string, List<TextEdit>> { [targetUri] = edits }), true));
        return actions;

        static string Escape(string s, string framework) => (framework == "behave" ? s.Replace("{", "{{").Replace("}", "}}") : s).Replace("\\", "\\\\");
    }

    /// <summary>"behave." or "pytest_bdd." when the file imports the module itself and not the decorator; empty otherwise.</summary>
    static string DecoratorPrefix(string text, string decorator, string framework)
    {
        string module = framework == "behave" ? "behave" : "pytest_bdd";
        bool importsName = Regex.IsMatch(text, $@"^from\s+{module}\s+import\s+(\*|[^\n]*\b{decorator}\b)", RegexOptions.Multiline);
        return !importsName && Regex.IsMatch(text, $@"^import\s+{module}\b", RegexOptions.Multiline) ? module + "." : "";
    }

    /// <summary>A function name made of a step's words: "the user logs in" is the_user_logs_in.</summary>
    static string StepFunctionName(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+").Select(x => x.Value).ToList();
        string name = string.Join('_', words);
        if (name.Length == 0) return "step";
        if (char.IsDigit(name[0])) name = "_" + name;
        return name.Length > 60 ? name[..60].TrimEnd('_') : name;
    }

    /// <summary>The open documents whose BDD findings depend on these edited ones: .feature files on a change of step definitions, and back.</summary>
    public IReadOnlyList<string> BddDependentsOf(IEnumerable<string> uris)
    {
        if (!Plugins.On("bdd")) return Array.Empty<string>();
        var list = uris.ToList();
        var result = new List<string>();
        if (list.Any(u => _docs.TryGetValue(u, out var d) && (d.Text.Contains("behave", StringComparison.Ordinal) || d.Text.Contains("pytest_bdd", StringComparison.Ordinal))))
            result.AddRange(_featureDocs.Keys.Where(k => !list.Contains(k)));
        if (list.Any(IsFeatureUri))
            result.AddRange(_docs.Where(d => !list.Contains(d.Key) && (d.Value.Text.Contains("behave", StringComparison.Ordinal) || d.Value.Text.Contains("pytest_bdd", StringComparison.Ordinal))).Select(d => d.Key));
        return result;
    }
}
