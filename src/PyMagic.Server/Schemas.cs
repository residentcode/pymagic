using System.Diagnostics;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// The JSON Schemas of SchemaStore that files are checked against (package.json, pyproject.toml, GitHub workflows). They are
/// downloaded once, when the user has said yes, and kept in .pymagic/schemas; nothing is fetched while that folder has the file.
/// </summary>
public static class Schemas
{
    public static readonly IReadOnlyDictionary<string, string> Urls = new Dictionary<string, string>
    {
        ["package"] = "https://json.schemastore.org/package.json",
        ["pyproject"] = "https://json.schemastore.org/pyproject.json",
        ["workflow"] = "https://json.schemastore.org/github-workflow.json",
    };

    public static string FolderOf(string root) => Path.Combine(root, ".pymagic", "schemas");

    public static string PathOf(string root, string name) => Path.Combine(FolderOf(root), name + ".json");

    /// <summary>The schema's text from the kept file, or null when it was not downloaded yet.</summary>
    public static string? Kept(string root, string name) => Urls.ContainsKey(name) && File.Exists(PathOf(root, name)) ? File.ReadAllText(PathOf(root, name)) : null;

    /// <summary>Downloads one schema (the caller has the user's yes) and keeps it. Throws ServiceException when it cannot.</summary>
    public static async Task<string> Download(string root, string name, HttpClient http)
    {
        if (!Urls.TryGetValue(name, out var url)) throw new ServiceException("not a schema PyMagic checks against");
        string text;
        try { text = await http.GetStringAsync(url); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new ServiceException("SchemaStore is not reachable"); }
        try { System.Text.Json.Nodes.JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { throw new ServiceException("SchemaStore did not answer with a schema"); }
        Directory.CreateDirectory(FolderOf(root));
        File.WriteAllText(PathOf(root, name), text);
        return text;
    }

    /// <summary>Which schemas are kept: name to size in bytes.</summary>
    public static Dictionary<string, long> List(string root) =>
        Urls.Keys.Where(n => File.Exists(PathOf(root, n))).ToDictionary(n => n, n => new FileInfo(PathOf(root, n)).Length);

    /// <summary>A TOML text as JSON (Python's tomllib; dates become strings), or null when it does not parse or Python is older than 3.11.</summary>
    public static string? TomlToJson(string python, string toml)
    {
        const string script = "import sys, json\ntry:\n    import tomllib\nexcept ImportError:\n    sys.exit(3)\ntry:\n    print(json.dumps(tomllib.loads(sys.stdin.buffer.read().decode('utf-8')), default=str))\nexcept Exception:\n    sys.exit(2)\n";
        try
        {
            var psi = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardInput.BaseStream.Write(Encoding.UTF8.GetBytes(toml));
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
