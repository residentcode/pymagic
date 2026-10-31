using System.Text.Json;

namespace PyMagic.Server;

/// <summary>
/// Mark Directory As (the project tree's menu): folders marked as sources roots (imports start there too), test sources (where Generate… puts a test)
/// and excluded (left out of the analysis, the index, Find in Files and the test runner). Kept in the project, in <c>.pymagic/folders.json</c>, as paths
/// from the project folder with <c>/</c>.
/// </summary>
/// <param name="Templates">Template folders (Mark Directory As > Template folder): every file under them is a template, by its path from there.</param>
/// <param name="Resources">Resources roots: shown as such (static files, fixtures).</param>
public sealed record FolderMarks(List<string> Sources, List<string> Tests, List<string> Excluded, List<string>? Templates = null, List<string>? Resources = null)
{
    public static FolderMarks None => new(new(), new(), new(), new(), new());
}

public static class ProjectFolders
{
    static string? _root;
    static FolderMarks _marks = FolderMarks.None;
    static string[] _excluded = Array.Empty<string>();

    static string FileOf(string root) => Path.Combine(root, ".pymagic", "folders.json");

    /// <summary>Reads the marks of the project the host was started on (and again after each change).</summary>
    public static FolderMarks Load(string root)
    {
        FolderMarks marks = FolderMarks.None;
        try
        {
            if (File.Exists(FileOf(root)) && JsonSerializer.Deserialize<FolderMarks>(File.ReadAllText(FileOf(root)), Options) is { } read)
                marks = new FolderMarks(Clean(read.Sources), Clean(read.Tests), Clean(read.Excluded), Clean(read.Templates), Clean(read.Resources));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        Use(root, marks);
        return marks;
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // paths under the project only: no "..", no drive, no leading slash
    static List<string> Clean(List<string>? list) => (list ?? new()).Select(p => p.Replace('\\', '/').Trim('/'))
        .Where(p => p.Length > 0 && !p.Contains(':') && !p.Split('/').Contains("..")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static void Use(string root, FolderMarks marks)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
        _marks = marks;
        _excluded = marks.Excluded.Select(p => Path.GetFullPath(Path.Combine(_root, p)).TrimEnd('\\', '/')).ToArray();
    }

    public static FolderMarks Marks => _marks;

    /// <summary>Marks a folder as one kind ("sources", "tests", "excluded"), or takes its mark away ("none"); a folder has one mark at most.</summary>
    public static FolderMarks Mark(string root, string rel, string kind)
    {
        rel = rel.Replace('\\', '/').Trim('/');
        var marks = Load(root);
        List<string> Without(List<string>? list) => (list ?? new()).Where(p => !p.Equals(rel, StringComparison.OrdinalIgnoreCase)).ToList();
        var next = new FolderMarks(Without(marks.Sources), Without(marks.Tests), Without(marks.Excluded), Without(marks.Templates), Without(marks.Resources));
        switch (kind)
        {
            case "sources": next.Sources.Add(rel); break;
            case "tests": next.Tests.Add(rel); break;
            case "excluded": next.Excluded.Add(rel); break;
            case "templates": next.Templates!.Add(rel); break;
            case "resources": next.Resources!.Add(rel); break;
        }
        next = new FolderMarks(Clean(next.Sources), Clean(next.Tests), Clean(next.Excluded), Clean(next.Templates), Clean(next.Resources));
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(root))!);
        File.WriteAllText(FileOf(root), JsonSerializer.Serialize(next, Options));
        Use(root, next);
        return next;
    }

    /// <summary>Whether a folder is excluded, or inside one that is.</summary>
    public static bool IsExcluded(string dir)
    {
        var list = _excluded;
        if (list.Length == 0) return false;
        string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
        foreach (var e in list)
            if (full.Equals(e, StringComparison.OrdinalIgnoreCase) || full.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The full paths of the folders marked as sources roots, or as test sources.</summary>
    public static IEnumerable<string> SourceRoots => _root == null ? Array.Empty<string>() : _marks.Sources.Select(p => Path.Combine(_root, p));
    public static IEnumerable<string> TestRoots => _root == null ? Array.Empty<string>() : _marks.Tests.Select(p => Path.Combine(_root, p));

    /// <summary>The folders found in TEMPLATES['DIRS'] of the Django settings, marked by themselves (paths from the project folder).</summary>
    public static List<string> AutoTemplates { get; set; } = new();

    /// <summary>The full paths of the template folders: the ones marked, and the ones Django's settings name.</summary>
    public static IEnumerable<string> TemplateRoots => _root == null ? Array.Empty<string>()
        : (_marks.Templates ?? new()).Concat(AutoTemplates).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => Path.Combine(_root, p));
}
