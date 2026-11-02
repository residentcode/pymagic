using System.Net.Http;
using System.Text.Json;

namespace PyMagic.Server;

/// <summary>
/// What the person decided about unresolved imports (item 215): imports to ignore, kept in <c>.pymagic/ignored-imports.json</c> of the project, and
/// whether a package to install exists on PyPI before it is offered; and the two commands of those quick fixes, for any LSP client.
/// </summary>
public sealed partial class LanguageService
{
    (string Path, DateTime Stamp, List<string> Modules)? _ignoredImports;

    string? IgnoredImportsPath() => SourceRoots.Count == 0 ? null : System.IO.Path.Combine(SourceRoots[0], ".pymagic", "ignored-imports.json");

    /// <summary>The modules whose imports are not reported (a module also covers its submodules), as the file lists them.</summary>
    public IReadOnlyList<string> IgnoredImports()
    {
        string? path = IgnoredImportsPath();
        if (path == null || !File.Exists(path)) { _ignoredImports = null; return Array.Empty<string>(); }
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_ignoredImports is { } cached && cached.Path == path && cached.Stamp == stamp) return cached.Modules;
        var modules = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) modules.Add(s.Trim());
        }
        catch (Exception e) when (e is JsonException or IOException) { /* a broken file ignores nothing */ }
        _ignoredImports = (path, stamp, modules);
        return modules;
    }

    bool IsIgnoredImport(string module) => IgnoredImports().Any(i => module == i || module.StartsWith(i + ".", StringComparison.Ordinal));

    /// <summary>Stops reporting the imports of a module (and its submodules) in this project: added to the list and written.</summary>
    /// <exception cref="ServiceException">There is no project folder, or the file cannot be written.</exception>
    public void IgnoreImport(string module)
    {
        string path = IgnoredImportsPath() ?? throw new ServiceException("there is no project folder to keep the list in");
        var modules = IgnoredImports().ToList();
        if (modules.Contains(module)) return;
        modules.Add(module);
        modules.Sort(StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(modules, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServiceException("the list cannot be written: " + e.Message); }
        _ignoredImports = null;
    }

    /// <summary>Reports the imports of a module again (the Settings window's list): taken off the list, which is written; the file goes when the list is empty.</summary>
    /// <exception cref="ServiceException">There is no project folder, or the file cannot be written.</exception>
    public void UnignoreImport(string module)
    {
        string path = IgnoredImportsPath() ?? throw new ServiceException("there is no project folder to keep the list in");
        var modules = IgnoredImports().Where(m => m != module).ToList();
        try
        {
            if (modules.Count == 0) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllText(path, JsonSerializer.Serialize(modules, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServiceException("the list cannot be written: " + e.Message); }
        _ignoredImports = null;
    }

    // ------------------------------------------------------------ PyPI

    /// <summary>
    /// Whether a package exists on PyPI: true or false, null when that cannot be known (offline, slow). Off (null) unless the host sets it, as
    /// PyMagic.Web does with <see cref="AskPyPI"/>; tests set their own.
    /// </summary>
    public Func<string, bool?>? PackageIndexLookup { get; set; }

    readonly Dictionary<string, bool?> _onIndex = new(StringComparer.OrdinalIgnoreCase);

    static readonly HttpClient PyPI = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static bool? AskPyPI(string package)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://pypi.org/pypi/{Uri.EscapeDataString(package)}/json");
            using var response = PyPI.Send(request);
            return response.IsSuccessStatusCode ? true : response.StatusCode == System.Net.HttpStatusCode.NotFound ? false : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException) { return null; }
    }

    /// <summary>What PyPI says of a package, asked once per name (an answer of "cannot know" is asked again next time).</summary>
    bool? PackageOnIndex(string package)
    {
        if (PackageIndexLookup == null) return null;
        lock (_onIndex)
            if (_onIndex.TryGetValue(package, out var known)) return known;
        var answer = PackageIndexLookup(package);
        if (answer != null) lock (_onIndex) _onIndex[package] = answer;
        return answer;
    }

    // ------------------------------------------------------------ the commands of the quick fixes

    /// <summary>
    /// `workspace/executeCommand`: `pymagic.ignoreImport` adds a module to the list; `pymagic.installPackage` runs pip of the interpreter (the PyMagic
    /// page runs its own, in the Packages tab). The answer is what to tell the person.
    /// </summary>
    /// <exception cref="ServiceException">An unknown command, a missing argument, or pip failed.</exception>
    public string ExecuteCommand(string command, IReadOnlyList<string> args)
    {
        switch (command)
        {
            case "pymagic.ignoreImport" when args.Count > 0:
                IgnoreImport(args[0]);
                return $"Imports of '{args[0]}' are no longer reported in this project (Settings > Inspections lists them).";
            case "pymagic.unignoreImport" when args.Count > 0:
                UnignoreImport(args[0]);
                return $"Imports of '{args[0]}' are reported again.";
            case "pymagic.installPackage" when args.Count > 0:
            {
                if (Interpreter == null) throw new ServiceException("no interpreter is selected");
                if (!PackageManager.IsSafeSpec(args[0])) throw new ServiceException($"'{args[0]}' is not a package name");
                var (code, output, error) = PackageManager.Exec(Interpreter.Python, new[] { "-m", "pip", "install", args[0] }, 600_000, Interpreter.Target);
                if (code != 0) throw new ServiceException($"pip install {args[0]} failed: " + (error.Trim().Length > 0 ? error.Trim() : output.Trim()));
                return $"Installed {args[0]}.";
            }
            default:
                throw new ServiceException($"'{command}' is not a command of PyMagic, or it is missing its argument");
        }
    }
}
