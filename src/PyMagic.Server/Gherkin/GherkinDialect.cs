using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Gherkin's keywords in one of its languages, as Cucumber lists them in gherkin-languages.json (built into PyMagic; MIT License, its LICENSE.txt
/// is next to it and ships in the app's host folder). A .feature file is English unless a comment `# language: de` before its first keyword says otherwise.
/// </summary>
public sealed class GherkinDialect
{
    public string Code { get; }
    public string Name { get; }

    /// <summary>A line that opens a block: the keyword, what kind of block ("feature", "rule", "background", "scenario", "scenarioOutline", "examples") and its name.</summary>
    public Regex BlockLine { get; }

    /// <summary>A step line: the keyword and the step's text.</summary>
    public Regex StepLine { get; }

    /// <summary>The kind each block keyword stands for.</summary>
    public IReadOnlyDictionary<string, string> BlockKinds { get; }

    /// <summary>The step keywords (without their trailing space) and the kinds each is listed under: given, when, then, and, but.</summary>
    public IReadOnlyDictionary<string, HashSet<string>> StepKinds { get; }

    /// <summary>The step keywords of one kind as they are written before a step's text (with their trailing space where the language has one), "* " left out.</summary>
    public IReadOnlyDictionary<string, List<string>> StepKeywords { get; }

    GherkinDialect(string code, JsonElement json)
    {
        Code = code;
        Name = json.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "feature", "rule", "background", "scenarioOutline", "scenario", "examples" })
            foreach (var k in Words(json, kind))
                blocks.TryAdd(k.Trim(), kind);
        BlockKinds = blocks;
        var steps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var written = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var spaced = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kind in new[] { "given", "when", "then", "and", "but" })
        {
            written[kind] = new();
            foreach (var k in Words(json, kind))
            {
                string word = k.Trim();
                if (word.Length == 0) continue;
                if (!steps.TryGetValue(word, out var kinds)) steps[word] = kinds = new();
                kinds.Add(kind);
                // a keyword written with a space after it needs one before the text; Chinese and Japanese ones are written against it
                spaced[word] = spaced.GetValueOrDefault(word) || k.EndsWith(' ');
                if (word != "*") written[kind].Add(k);
            }
        }
        StepKinds = steps;
        StepKeywords = written;
        // the longest keyword first, so that "Gegeben seien" is not read as "Gegeben sei" and the rest
        string blockAlternatives = string.Join("|", blocks.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        BlockLine = new Regex($@"^(?<kw>{blockAlternatives})\s*:(?<name>.*)$", RegexOptions.Compiled);
        var stepAlternatives = steps.Keys.OrderByDescending(k => k.Length).Select(k => "(?<kw>" + Regex.Escape(k) + ")" + (spaced[k] ? @"\s+" : @"\s*"));
        StepLine = new Regex($@"^(?:{string.Join("|", stepAlternatives)})(?<text>.*?)\s*$", RegexOptions.Compiled);
    }

    static IEnumerable<string> Words(JsonElement json, string kind) =>
        json.TryGetProperty(kind, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(e => e.GetString() ?? "") : Array.Empty<string>();

    /// <summary>
    /// The kind a step keyword gives its step: given, when or then for a keyword listed only under one of them; null for And, But, * and a keyword
    /// listed under several kinds, whose step takes the kind of the step before.
    /// </summary>
    public string? KindOf(string keyword)
    {
        if (!StepKinds.TryGetValue(keyword, out var kinds) || kinds.Contains("and") || kinds.Contains("but")) return null;
        return kinds.Count == 1 ? kinds.First() : null;
    }

    static readonly Lazy<Dictionary<string, JsonElement>> Languages = new(() =>
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var stream = typeof(GherkinDialect).Assembly.GetManifestResourceStream("gherkin-languages.json");
        if (stream == null) return result;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        foreach (var lang in doc.RootElement.EnumerateObject()) result[lang.Name] = lang.Value.Clone();
        return result;
    });

    static readonly Dictionary<string, GherkinDialect> Made = new(StringComparer.Ordinal);

    /// <summary>The language codes Cucumber knows (af, de, en, zh-CN, ...).</summary>
    public static IEnumerable<string> Codes => Languages.Value.Keys;

    /// <summary>The dialect of a language code; null when Cucumber has no such language.</summary>
    public static GherkinDialect? Of(string code)
    {
        lock (Made)
        {
            if (Made.TryGetValue(code, out var d)) return d;
            if (!Languages.Value.TryGetValue(code, out var json)) return null;
            return Made[code] = new GherkinDialect(code, json);
        }
    }

    public static GherkinDialect English => Of("en") ?? throw new InvalidOperationException("gherkin-languages.json is not built into the server");

    static readonly Regex LanguageLine = new(@"^\s*#\s*language\s*:\s*(?<code>[a-zA-Z\-_]+)\s*$", RegexOptions.Compiled);

    /// <summary>The language a .feature file names in a `# language: xx` comment before its first keyword, and where the code is; (null, -1, -1) when it names none.</summary>
    public static (string? Code, int Start, int End) LanguageOf(string text)
    {
        int offset = 0;
        foreach (var raw in text.Split('\n'))
        {
            string body = raw.TrimEnd('\r').Trim();
            int at = offset;
            offset += raw.Length + 1;
            if (body.Length == 0) continue;
            if (!body.StartsWith('#')) break;
            var m = LanguageLine.Match(raw.TrimEnd('\r'));
            if (m.Success) return (m.Groups["code"].Value, at + m.Groups["code"].Index, at + m.Groups["code"].Index + m.Groups["code"].Length);
        }
        return (null, -1, -1);
    }

    /// <summary>The dialect a .feature file is written in: the one its `# language:` comment names, English otherwise (also for a language Cucumber does not have).</summary>
    public static GherkinDialect For(string text) => LanguageOf(text).Code is { } code && Of(code) is { } d ? d : English;
}
