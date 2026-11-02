using System.Text.Json;

namespace PyMagic.Server;

/// <summary>
/// The plugins switched off in Settings > Plugins (the "plugins" object of the person's settings: id -> false). The page leaves out what a plugin owns
/// in the window; the engine asks here for what it does on its own (Django's inspections, completion and navigation, Docker's listing of images).
/// Every plugin is on unless the settings say false.
/// </summary>
public static class Plugins
{
    static volatile HashSet<string> _off = new();

    /// <summary>Whether a plugin is on.</summary>
    public static bool On(string id) => !_off.Contains(id);

    /// <summary>Reads the plugins that are off from the settings' JSON text; text that does not parse leaves every plugin on.</summary>
    public static void Load(string? settingsJson)
    {
        var off = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
                foreach (var p in plugins.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.False) off.Add(p.Name);
        }
        catch (JsonException) { }
        _off = off;
    }
}
