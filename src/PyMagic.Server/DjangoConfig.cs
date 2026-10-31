using System.Text.Json;

namespace PyMagic.Server;

/// <summary>
/// The Django page of the settings, kept in the project's <c>.pymagic/django.json</c>: Django support on or off, the folder of the Django project,
/// the settings module and the manage.py to use instead of the ones found, variables for manage.py, whether tests use Django's runner, and the
/// template language of each template file type (django, jinja, or none: plain HTML).
/// </summary>
public sealed record DjangoConfig(bool Enabled = true, string? Root = null, string? Settings = null, string? Manage = null,
    Dictionary<string, string>? Env = null, bool TestRunner = true, Dictionary<string, string>? TemplateLanguages = null)
{
    public static readonly string[] TemplateExtensions = { ".html", ".htm", ".jinja", ".jinja2", ".j2" };

    /// <summary>The configuration of the project the host was started on (one per host, as the folder marks).</summary>
    public static DjangoConfig Current { get; set; } = new();

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static string FileOf(string root) => Path.Combine(root, ".pymagic", "django.json");

    public static DjangoConfig Load(string root)
    {
        DjangoConfig config = new();
        try
        {
            if (File.Exists(FileOf(root)) && JsonSerializer.Deserialize<DjangoConfig>(File.ReadAllText(FileOf(root)), Options) is { } read) config = read.Cleaned();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return Current = config;
    }

    public static DjangoConfig Save(string root, DjangoConfig config)
    {
        config = config.Cleaned();
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(root))!);
        File.WriteAllText(FileOf(root), JsonSerializer.Serialize(config, Options));
        return Current = config;
    }

    /// <summary>Paths inside the project only, a module name for the settings, known languages for known types.</summary>
    DjangoConfig Cleaned()
    {
        static string? Rel(string? p) => string.IsNullOrWhiteSpace(p) ? null : p.Replace('\\', '/').Trim().Trim('/') is var r && !r.Contains(':') && !r.Split('/').Contains("..") ? r : null;
        var languages = (TemplateLanguages ?? new()).Where(kv => TemplateExtensions.Contains(kv.Key.ToLowerInvariant()) && kv.Value is "django" or "jinja" or "none")
            .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
        return this with
        {
            Root = Rel(Root), Manage = Rel(Manage), Settings = RunConfigurations.IsModuleName(Settings?.Trim()) ? Settings!.Trim() : null,
            Env = (Env ?? new()).Where(kv => System.Text.RegularExpressions.Regex.IsMatch(kv.Key, @"^[A-Za-z_]\w{0,63}$")).ToDictionary(kv => kv.Key, kv => kv.Value),
            TemplateLanguages = languages,
        };
    }

    /// <summary>How a file of this type is read: "django", "jinja" or "none". Jinja's own types are Jinja; HTML is Django's unless Django is off.</summary>
    public string LanguageOf(string extension)
    {
        string ext = extension.ToLowerInvariant();
        if (!TemplateExtensions.Contains(ext)) return "none";
        string language = TemplateLanguages?.GetValueOrDefault(ext) ?? (ext is ".jinja" or ".jinja2" or ".j2" ? "jinja" : "django");
        return language == "django" && !Enabled ? "none" : language;
    }
}
