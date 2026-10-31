using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Coverage suites: each coverage run kept beside the last one (.pymagic/coverage/&lt;time&gt;.json) with what it ran, so the page
/// can show one run or several together, as PyCharm's Show Coverage Data (Ctrl+Alt+F6) does. The newest twelve are kept.
/// </summary>
public static class CoverageSuites
{
    public const int Kept = 12;

    public sealed record Suite(string Name, string Label, DateTime When, double Percent);

    static string Folder(string root) => Path.Combine(root, ".pymagic", "coverage");

    static readonly Regex SafeName = new(@"^[\w.-]+$", RegexOptions.Compiled);

    /// <summary>What a run of these test ids is called in the list.</summary>
    public static string LabelOf(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "All tests" : ids.Count == 1 ? ids[0] : $"{ids[0]} and {ids.Count - 1} more";

    /// <summary>Keeps the last coverage run (.pymagic/coverage.json) as a suite; its name, or null when there was none to keep.</summary>
    public static string? Keep(string root, string label, DateTime? when = null)
    {
        string last = RunSession.CoveragePath(root);
        if (!File.Exists(last)) return null;
        JsonObject? data;
        try { data = JsonNode.Parse(File.ReadAllText(last)) as JsonObject; }
        catch (JsonException) { return null; }
        if (data == null) return null;
        var time = when ?? DateTime.Now;
        data["label"] = label;
        data["when"] = time.ToUniversalTime().ToString("o");
        Directory.CreateDirectory(Folder(root));
        string name = time.ToString("yyyyMMdd-HHmmss-fff");
        File.WriteAllText(Path.Combine(Folder(root), name + ".json"), data.ToJsonString());
        foreach (var old in Directory.GetFiles(Folder(root), "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Kept))
            try { File.Delete(old); } catch (IOException) { }
        return name;
    }

    /// <summary>The kept suites, the newest first.</summary>
    public static List<Suite> List(string root)
    {
        var result = new List<Suite>();
        if (!Directory.Exists(Folder(root))) return result;
        foreach (var file in Directory.GetFiles(Folder(root), "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject data) continue;
                long total = 0, hit = 0;
                if (data["files"] is JsonObject files)
                    foreach (var (_, f) in files)
                    {
                        total += (f?["executable"] as JsonArray)?.Count ?? 0;
                        hit += (f?["executed"] as JsonArray)?.Count ?? 0;
                    }
                var when = DateTime.TryParse(data["when"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var w) ? w : File.GetLastWriteTimeUtc(file);
                result.Add(new Suite(Path.GetFileNameWithoutExtension(file), data["label"]?.GetValue<string>() ?? "", when, total == 0 ? 100 : 100.0 * hit / total));
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { }
        }
        return result;
    }

    /// <summary>A suite's data, or null when there is no suite of that name.</summary>
    public static JsonObject? Read(string root, string name)
    {
        if (!SafeName.IsMatch(name)) return null;
        string path = Path.Combine(Folder(root), name + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static bool Delete(string root, string name)
    {
        if (!SafeName.IsMatch(name)) return false;
        string path = Path.Combine(Folder(root), name + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
