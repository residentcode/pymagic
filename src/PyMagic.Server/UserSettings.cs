using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// The settings of the person using PyMagic (theme, editor font, ...): one JSON object in their own folder, the same for every project. It is
/// kept by the engine and not in the page's storage, because the page's address changes with the port and its storage would be lost with it.
/// </summary>
public sealed class UserSettings
{
    /// <summary>Most a settings file may hold; it is a handful of choices.</summary>
    public const int MaxBytes = 64 * 1024;

    readonly string _file;

    public UserSettings(string? file = null)
    {
        _file = file ?? System.Environment.GetEnvironmentVariable("PYMAGIC_SETTINGS_FILE") ?? DefaultFile();
    }

    public static string DefaultFile() =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "PyMagic", "settings.json");

    /// <summary>The settings as JSON text; <c>{}</c> when there is no file or it cannot be read as an object.</summary>
    public string Read()
    {
        try
        {
            if (!File.Exists(_file)) return "{}";
            return JsonNode.Parse(File.ReadAllText(_file)) is JsonObject o ? o.ToJsonString() : "{}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return "{}"; }
    }

    /// <exception cref="ServiceException">The text is not one JSON object, or it is too big.</exception>
    public void Write(string json)
    {
        if (json.Length > MaxBytes) throw new ServiceException("the settings are too big");
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new ServiceException("the settings must be one JSON object"); }
        catch (System.Text.Json.JsonException) { throw new ServiceException("the settings are not valid JSON"); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            // written beside the file first, so a failure half way never leaves half a file
            string temp = _file + ".tmp";
            File.WriteAllText(temp, o.ToJsonString());
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServiceException("the settings could not be saved: " + e.Message); }
    }
}
