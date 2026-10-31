using System.Text.Json.Serialization;

namespace PyMagic.Server.Formatting;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuoteStyle { Double, Single, Preserve }

/// <summary>How lines end in the formatted text: as the file has them (Auto), or always LF or CRLF.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LineEnding { Auto, Lf, Crlf }

/// <summary>What the built-in formatter can be told: the same knobs black and ruff read from pyproject.toml, and what .editorconfig says.</summary>
/// <param name="LineLength">The width lines are wrapped at (black's and ruff's <c>line-length</c>).</param>
/// <param name="Quotes">Double (the default), Single, or Preserve (black's <c>skip-string-normalization</c>, ruff's <c>quote-style = "preserve"</c>).</param>
/// <param name="MagicTrailingComma">False when a trailing comma does not keep a collection exploded (<c>skip-magic-trailing-comma</c>).</param>
/// <param name="IndentWidth">Spaces per level (ruff's <c>indent-width</c>, editorconfig's <c>indent_size</c>); with tabs, the width a tab counts for.</param>
/// <param name="Source">The configuration file the options were read from, if any.</param>
/// <param name="UseTabs">One tab per level (ruff's <c>indent-style = "tab"</c>, editorconfig's <c>indent_style = tab</c>).</param>
/// <param name="Endings">ruff's <c>line-ending</c>, editorconfig's <c>end_of_line</c>.</param>
/// <param name="Imports">isort's and ruff's import settings (<c>known-first-party</c>, <c>force-sort-within-sections</c>, ...).</param>
/// <param name="ParenthesizedWith">Several context managers may be put in parentheses when a with is split: the Pythons the code is for
/// are 3.9 and later (black's and ruff's <c>target-version</c>, or syntax of the file that needs 3.9).</param>
/// <param name="CommaAfterStar">A split call or def gets a trailing comma after `*args` or `**kwargs` too: the code is for Python 3.6 and
/// later (<c>target-version</c>, or syntax of the file that needs 3.6, as black infers it).</param>
public sealed record FormatOptions(int LineLength = 88, QuoteStyle Quotes = QuoteStyle.Double, bool MagicTrailingComma = true, int IndentWidth = 4, string? Source = null,
    bool UseTabs = false, LineEnding Endings = LineEnding.Auto, ImportOptions? Imports = null, bool ParenthesizedWith = false, bool CommaAfterStar = false)
{
    public static readonly FormatOptions Default = new();
}

/// <summary>The settings a configuration file states, each null when it says nothing (so another source can fill it in).</summary>
public sealed record FormatSettings(int? LineLength = null, QuoteStyle? Quotes = null, bool? MagicTrailingComma = null, int? IndentWidth = null, bool? UseTabs = null, LineEnding? Endings = null, ImportOptions? Imports = null, bool? ParenthesizedWith = null, bool? CommaAfterStar = null)
{
    /// <summary>The options these settings give, the defaults filling what is left.</summary>
    public FormatOptions ToOptions(string? source = null, FormatOptions? baseline = null)
    {
        var d = baseline ?? FormatOptions.Default;
        return new FormatOptions(LineLength ?? d.LineLength, Quotes ?? d.Quotes, MagicTrailingComma ?? d.MagicTrailingComma, IndentWidth ?? d.IndentWidth, source ?? d.Source, UseTabs ?? d.UseTabs, Endings ?? d.Endings, Imports ?? d.Imports, ParenthesizedWith ?? d.ParenthesizedWith, CommaAfterStar ?? d.CommaAfterStar);
    }
}

/// <summary>Finds and reads the formatter settings of a project: <c>[tool.black]</c> and <c>[tool.ruff]</c> in pyproject.toml, <c>ruff.toml</c> and <c>.ruff.toml</c>, then <c>.editorconfig</c> for what they leave open.</summary>
public static class FormatConfig
{
    static readonly string[] ProjectMarkers = { "pyproject.toml", "ruff.toml", ".ruff.toml", ".git", ".hg" };

    /// <summary>
    /// The options for a file: from the nearest folder upwards that holds a configuration file (the project root, as black
    /// and ruff find it). A project root without a formatter section means the defaults. What those files do not say
    /// is taken from the .editorconfig files that apply to the file.
    /// </summary>
    public static FormatOptions For(string filePath)
    {
        var fromEditor = FromEditorConfig(EditorConfig.For(filePath));
        var fromProject = ProjectSettings(filePath, out string? source);
        var merged = new FormatSettings(
            fromProject.LineLength ?? fromEditor.LineLength, fromProject.Quotes ?? fromEditor.Quotes, fromProject.MagicTrailingComma ?? fromEditor.MagicTrailingComma,
            fromProject.IndentWidth ?? fromEditor.IndentWidth, fromProject.UseTabs ?? fromEditor.UseTabs, fromProject.Endings ?? fromEditor.Endings, fromProject.Imports, fromProject.ParenthesizedWith, fromProject.CommaAfterStar);
        return merged.ToOptions(source);
    }

    static FormatSettings ProjectSettings(string filePath, out string? source)
    {
        source = null;
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new FormatSettings(); }

        for (; dir != null; dir = Path.GetDirectoryName(dir))
        {
            var found = ReadFolder(dir, out source);
            if (found != null) return found;
            if (ProjectMarkers.Any(m => File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)))) return new FormatSettings();
        }
        return new FormatSettings();
    }

    /// <summary>The settings a folder's configuration files give, or null when they have no formatter section.</summary>
    static FormatSettings? ReadFolder(string dir, out string? source)
    {
        source = null;
        foreach (var name in new[] { "pyproject.toml", "ruff.toml", ".ruff.toml" })
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var settings = SettingsFromToml(text, isRuffFile: name != "pyproject.toml");
            if (settings != null) { source = path; return settings; }
        }
        return null;
    }

    /// <summary>What an .editorconfig says: <c>indent_style</c>, <c>indent_size</c> (or <c>tab_width</c>), <c>end_of_line</c> and <c>max_line_length</c>.</summary>
    public static FormatSettings FromEditorConfig(IReadOnlyDictionary<string, string> p)
    {
        bool? tabs = p.TryGetValue("indent_style", out var style) ? style switch { "tab" => true, "space" => false, _ => null } : null;
        int? width = null;
        if (p.TryGetValue("indent_size", out var size) && size != "tab" && int.TryParse(size, out int n) && n is >= 1 and <= 100) width = n;
        else if (p.TryGetValue("tab_width", out var tw) && int.TryParse(tw, out int t) && t is >= 1 and <= 100 && (size == "tab" || tabs == true)) width = t;
        LineEnding? ending = p.TryGetValue("end_of_line", out var eol) ? eol switch { "lf" => LineEnding.Lf, "crlf" => LineEnding.Crlf, _ => null } : null;
        int? length = p.TryGetValue("max_line_length", out var ml) && int.TryParse(ml, out int len) && len is >= 1 and <= 1000 ? len : null;
        return new FormatSettings(LineLength: length, IndentWidth: width, UseTabs: tabs, Endings: ending);
    }

    /// <summary>`[tool.ruff.lint.isort]` (`[lint.isort]` in ruff.toml) and `[tool.isort]`: the packages that are first or third party and how the lines are laid out.</summary>
    static ImportOptions? ImportsFromToml(Dictionary<string, object?> root, bool isRuffFile)
    {
        var tables = new List<Dictionary<string, object?>>();
        foreach (var path in isRuffFile ? new[] { "lint.isort", "isort" } : new[] { "tool.ruff.lint.isort", "tool.ruff.isort", "tool.isort" })
            if (Toml.At(root, path) is Dictionary<string, object?> t) tables.Add(t);
        if (tables.Count == 0) return null;

        object? Find(string key)
        {
            foreach (var t in tables)
                foreach (var spelled in new[] { key, key.Replace('-', '_') })
                    if (t.TryGetValue(spelled, out var v)) return v;
            return null;
        }
        bool Flag(string key, bool fallback) => Find(key) is bool b ? b : fallback;
        List<string>? Names(string key) => Find(key) is List<object?> list ? list.OfType<string>().ToList() : null;
        return new ImportOptions(Flag("force-sort-within-sections", false), Flag("combine-as-imports", false), Flag("force-single-line", false), Flag("order-by-type", true),
            Names("known-first-party"), Names("known-third-party"));
    }

    /// <summary>The options a configuration text gives; null when it has no black or ruff section (or is not valid TOML).</summary>
    public static FormatOptions? FromToml(string text, bool isRuffFile = false, string? source = null) =>
        SettingsFromToml(text, isRuffFile)?.ToOptions(source);

    static FormatSettings? SettingsFromToml(string text, bool isRuffFile)
    {
        var root = Toml.Parse(text);
        if (root == null) return null;
        var black = isRuffFile ? null : Toml.At(root, "tool.black") as Dictionary<string, object?>;
        var ruff = (isRuffFile ? root : Toml.At(root, "tool.ruff") as Dictionary<string, object?>);
        if (black == null && ruff == null) return null;
        var ruffFormat = (isRuffFile ? Toml.At(root, "format") : Toml.At(root, "tool.ruff.format")) as Dictionary<string, object?>;

        int? Int(Dictionary<string, object?>? t, string key) => t != null && t.TryGetValue(key, out var v) && v is long n && n is >= 1 and <= 1000 ? (int)n : null;
        bool? Bool(Dictionary<string, object?>? t, string key) => t != null && t.TryGetValue(key, out var v) && v is bool b ? b : null;
        string? Str(Dictionary<string, object?>? t, string key) => t != null && t.TryGetValue(key, out var v) && v is string s ? s : null;

        int? lineLength = Int(black, "line-length") ?? Int(ruff, "line-length");
        int? indent = Int(ruff, "indent-width");

        QuoteStyle? quotes = null;
        if (Bool(black, "skip-string-normalization") == true) quotes = QuoteStyle.Preserve;
        else if (black == null || !black.ContainsKey("skip-string-normalization"))
            quotes = Str(ruffFormat, "quote-style") switch { "single" => QuoteStyle.Single, "preserve" => QuoteStyle.Preserve, "double" => QuoteStyle.Double, _ => null };

        bool? magic = Bool(black, "skip-magic-trailing-comma") == true || Bool(ruffFormat, "skip-magic-trailing-comma") == true ? false : null;
        bool? tabs = Str(ruffFormat, "indent-style") switch { "tab" => true, "space" => false, _ => null };
        LineEnding? ending = Str(ruffFormat, "line-ending") switch { "lf" => LineEnding.Lf, "cr-lf" or "crlf" => LineEnding.Crlf, "auto" => LineEnding.Auto, _ => null };
        // target-version: black's list (every one 3.9 or later), ruff's single version
        int? Minor(string v) => System.Text.RegularExpressions.Regex.Match(v, @"^py3(\d+)$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
        bool? parenthesizedWith = null, commaAfterStar = null;
        if (black != null && black.TryGetValue("target-version", out var tv) && tv is List<object?> versions && versions.Count > 0)
        {
            parenthesizedWith = versions.All(v => v is string s && Minor(s) >= 9);
            commaAfterStar = versions.All(v => v is string s && Minor(s) >= 6);
        }
        else if (Str(ruff, "target-version") is { } rv && Minor(rv) is { } rm)
        {
            parenthesizedWith = rm >= 9;
            commaAfterStar = rm >= 6;
        }
        return new FormatSettings(lineLength, quotes, magic, indent, tabs, ending, ImportsFromToml(root, isRuffFile), parenthesizedWith, commaAfterStar);
    }
}
