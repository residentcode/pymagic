using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// The npm packages installed for a package.json: what its node_modules has at the top (a package's folder with its own package.json, scoped ones
/// as @scope/name), with the version each says. Read from the folders, as npm and PyCharm do; nothing is run.
/// </summary>
public static class NodePackages
{
    /// <summary>Null when the folder has no node_modules (nothing installed yet); otherwise name -> version ("" when its package.json has none).</summary>
    public static Dictionary<string, string>? Installed(string folder)
    {
        string modules = Path.Combine(folder, "node_modules");
        if (!Directory.Exists(modules)) return null;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        void Read(string dir, string name)
        {
            string manifest = Path.Combine(dir, "package.json");
            if (!File.Exists(manifest)) return;
            string version = "";
            try { version = JsonNode.Parse(File.ReadAllText(manifest))?["version"]?.GetValue<string>() ?? ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            found[name] = version;
        }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(modules))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;   // .bin, .cache, .pnpm
                if (name.StartsWith('@'))
                {
                    foreach (var scoped in Directory.EnumerateDirectories(dir)) Read(scoped, name + "/" + Path.GetFileName(scoped));
                }
                else Read(dir, name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }
}
