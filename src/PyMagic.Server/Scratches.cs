using System.Text;

namespace PyMagic.Server;

/// <summary>A scratch file: a file of any language kept outside the project, for a try-out that does not belong in it.</summary>
public sealed record ScratchFile(string Name, long Size, DateTime Modified);

/// <summary>
/// Scratch files (item 238). They live in one folder of the user's (<c>%APPDATA%\PyMagic\scratches</c>, or PYMAGIC_SCRATCHES), shared by
/// every project like PyCharm's, and are reached from the project under the made-up folder <c>.scratches/</c>: the page opens
/// <c>.scratches/scratch.py</c> like a file of the project, and the server (not the disk of the project) answers where that is.
/// </summary>
public static class Scratches
{
    public const string Prefix = ".scratches/";

    public static string Folder
    {
        get
        {
            string? own = Environment.GetEnvironmentVariable("PYMAGIC_SCRATCHES");
            return !string.IsNullOrWhiteSpace(own) ? Path.GetFullPath(own) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PyMagic", "scratches");
        }
    }

    public static bool IsScratchPath(string rel) => rel.Replace('\\', '/').StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file a scratch name is, inside the folder; throws when the name is a path or has characters a file name cannot.</summary>
    /// <exception cref="UnauthorizedAccessException">The name leaves the folder.</exception>
    public static string PathOf(string name)
    {
        name = name.Replace('\\', '/');
        if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) name = name[Prefix.Length..];
        if (name.Length == 0 || name.Contains('/') || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UnauthorizedAccessException("not the name of a scratch file");
        return Path.Combine(Folder, name);
    }

    public static IReadOnlyList<ScratchFile> List()
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<ScratchFile>();
            return new DirectoryInfo(Folder).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).Select(f => new ScratchFile(f.Name, f.Length, f.LastWriteTimeUtc)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<ScratchFile>(); }
    }

    /// <summary>What a new scratch of a kind holds: a first line saying what it is, so it is not an empty tab.</summary>
    public static string Starter(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".py" => "# scratch\n",
        ".js" or ".mjs" or ".ts" => "// scratch\n",
        ".json" => "{\n}\n",
        ".md" => "# scratch\n",
        ".sql" => "-- scratch\n",
        ".html" or ".htm" => "<!doctype html>\n<title>scratch</title>\n",
        ".css" => "/* scratch */\n",
        ".yml" or ".yaml" => "# scratch\n",
        ".toml" or ".ini" => "# scratch\n",
        ".http" => "### scratch\nGET https://example.org/\n",
        ".sh" => "#!/bin/sh\n",
        ".bat" => "@echo off\n",
        _ => "",
    };

    /// <summary>Makes a scratch file; when the name is taken, a number goes before the extension (scratch_1.py). Gives the name it got.</summary>
    public static string Create(string name, string? text = null)
    {
        PathOf(name);
        Directory.CreateDirectory(Folder);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string made = name;
        for (int i = 1; File.Exists(PathOf(made)); i++) made = $"{stem}_{i}{ext}";
        File.WriteAllText(PathOf(made), text ?? Starter(made), new UTF8Encoding(false));
        return made;
    }

    public static bool Delete(string name)
    {
        string path = PathOf(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Gives a scratch another name; false when there is no such scratch or the new name is taken.</summary>
    public static bool Rename(string from, string to)
    {
        string a = PathOf(from), b = PathOf(to);
        if (!File.Exists(a) || File.Exists(b)) return false;
        File.Move(a, b);
        return true;
    }
}
