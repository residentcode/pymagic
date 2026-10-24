using System.Text.Json.Serialization;

namespace PyMagic.Server.Formatting;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuoteStyle { Double, Single, Preserve }

/// <summary>What the built-in formatter can be told: the same knobs black and ruff read from pyproject.toml.</summary>
/// <param name="LineLength">The width lines are wrapped at (black's and ruff's <c>line-length</c>).</param>
/// <param name="Quotes">Double (the default), Single, or Preserve (black's <c>skip-string-normalization</c>, ruff's <c>quote-style = "preserve"</c>).</param>
/// <param name="MagicTrailingComma">False when a trailing comma does not keep a collection exploded (<c>skip-magic-trailing-comma</c>).</param>
/// <param name="IndentWidth">Spaces per level (ruff's <c>indent-width</c>).</param>
/// <param name="Source">The configuration file the options were read from, if any.</param>
public sealed record FormatOptions(int LineLength = 88, QuoteStyle Quotes = QuoteStyle.Double, bool MagicTrailingComma = true, int IndentWidth = 4, string? Source = null)
{
    public static readonly FormatOptions Default = new();
}

/// <summary>Finds and reads the formatter settings of a project: <c>[tool.black]</c> and <c>[tool.ruff]</c> in pyproject.toml, <c>ruff.toml</c> and <c>.ruff.toml</c>.</summary>
public static class FormatConfig
{
    static readonly string[] ProjectMarkers = { "pyproject.toml", "ruff.toml", ".ruff.toml", ".git", ".hg" };

    /// <summary>
    /// The options for a file: from the nearest folder upwards that holds a configuration file (the project root, as black
    /// and ruff find it). A project root without a formatter section means the defaults.
    /// </summary>
    public static FormatOptions For(string filePath)
    {
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return FormatOptions.Default; }

        for (; dir != null; dir = Path.GetDirectoryName(dir))
        {
            var found = ReadFolder(dir);
            if (found != null) return found;
            if (ProjectMarkers.Any(m => File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)))) return FormatOptions.Default;
        }
        return FormatOptions.Default;
    }

    /// <summary>The options a folder's configuration files give, or null when they have no formatter section.</summary>
    static FormatOptions? ReadFolder(string dir)
    {
        foreach (var name in new[] { "pyproject.toml", "ruff.toml", ".ruff.toml" })
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var options = FromToml(text, isRuffFile: name != "pyproject.toml", path);
            if (options != null) return options;
        }
        return null;
    }

    /// <summary>The options a configuration text gives; null when it has no black or ruff section (or is not valid TOML).</summary>
    public static FormatOptions? FromToml(string text, bool isRuffFile = false, string? source = null)
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

        int lineLength = Int(black, "line-length") ?? Int(ruff, "line-length") ?? FormatOptions.Default.LineLength;
        int indent = Int(ruff, "indent-width") ?? FormatOptions.Default.IndentWidth;

        var quotes = QuoteStyle.Double;
        if (Bool(black, "skip-string-normalization") == true) quotes = QuoteStyle.Preserve;
        else if (black == null || !black.ContainsKey("skip-string-normalization"))
            quotes = Str(ruffFormat, "quote-style") switch { "single" => QuoteStyle.Single, "preserve" => QuoteStyle.Preserve, _ => QuoteStyle.Double };

        bool magic = !(Bool(black, "skip-magic-trailing-comma") == true || Bool(ruffFormat, "skip-magic-trailing-comma") == true);
        return new FormatOptions(lineLength, quotes, magic, indent, source);
    }
}
