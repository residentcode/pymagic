using System.Text.Json;

namespace PyMagic.Server;

/// <summary>One inspection the editor can report: what it is called, how it is shown by default and how it is shown now.</summary>
public sealed record InspectionInfo(string Code, string Group, string Title, string Description, string Default, string Level);

/// <summary>Which inspections are reported and how severe each is: the profile of the project, kept in <c>.pymagic/inspections.json</c>.</summary>
public sealed partial class LanguageService
{
    static readonly (string Code, string Group, string Title, string Description, string Default)[] InspectionCatalog =
    {
        ("unresolved-reference", "Python", "Unresolved reference", "A name that is not defined anywhere it can be found.", "warning"),
        ("missing-attribute", "Python", "Unresolved attribute reference", "An attribute the type of the value does not have.", "warning"),
        ("unbound-local", "Python", "Local variable referenced before assignment", "A local that may not be assigned yet when it is read.", "warning"),
        ("unreachable", "Python", "Unreachable code", "Statements no path of the program reaches.", "hint"),
        ("unresolved-import", "Python", "Unresolved import", "A module that cannot be found.", "warning"),
        ("unresolved-import-name", "Python", "Unresolved name in an import", "A name the module does not define.", "warning"),
        ("package-not-installed", "Python", "Package is not installed", "An import of a package the interpreter does not have.", "warning"),
        ("unused-import", "Python", "Unused import", "An imported name that is never used.", "hint"),
        ("unused-variable", "Python", "Unused local variable", "A local variable that is assigned and never read.", "hint"),
        ("unused-argument", "Python", "Unused parameter", "A parameter the function never uses.", "hint"),
        ("redeclared", "Python", "Redeclared definition", "A function or class defined again before it is used.", "warning"),
        ("assert-tuple", "Python", "Assertion on a tuple", "assert (x, 'message') is always true.", "warning"),
        ("bare-except", "Python", "Bare 'except:'", "Catches everything, including KeyboardInterrupt.", "warning"),
        ("duplicate-key", "Python", "Duplicate dictionary key", "A key written twice in a dict display.", "warning"),
        ("is-literal", "Python", "'is' comparison with a literal", "x is 'text' compares identity, not value.", "warning"),
        ("missing-self", "Python", "Method without 'self'", "A method in a class that takes no receiver.", "warning"),
        ("mutable-default", "Python", "Mutable default argument", "A list, dict or set shared by every call.", "warning"),
        ("none-comparison", "Python", "Comparison with None using ==", "Use 'is None' or 'is not None'.", "warning"),
        ("self-assignment", "Python", "Assignment to itself", "x = x does nothing.", "warning"),
        ("missing-super-init", "Python classes", "Missing call to super().__init__()", "An __init__ that does not call the one of its base class.", "warning"),
        ("unimplemented-abstract", "Python classes", "Abstract methods not implemented", "A concrete class that leaves abstract methods open.", "warning"),
        ("naming-class", "PEP 8 naming", "Class names", "Classes are written CapWords.", "weak"),
        ("naming-function", "PEP 8 naming", "Function and method names", "Functions are written snake_case.", "weak"),
        ("naming-argument", "PEP 8 naming", "Parameter names", "Parameters are written snake_case.", "weak"),
        ("naming-variable", "PEP 8 naming", "Variable names", "Variables are written snake_case.", "weak"),
        ("shadowed-builtin", "PEP 8 naming", "Shadowing a built-in name", "A variable, parameter or function that hides a built-in.", "weak"),
        ("fixture-not-found", "pytest", "Fixture not found", "A test parameter that no fixture provides.", "weak"),
        ("django-field", "Django", "Unknown field in a query", "A keyword or string that is not a field of the model.", "warning"),
        ("django-url-name", "Django", "Unknown URL name", "A URL name no path() defines.", "weak"),
        ("django-template", "Django", "Missing template", "A template name no templates folder has.", "weak"),
        ("route-path-parameter", "Web routes", "Path parameter not taken by the function", "{id} in the route and no parameter 'id' (a warning for Flask, weak for FastAPI).", "warning"),
        ("route-unknown-parameter", "Web routes", "Flask view parameter the route never gives", "A parameter Flask cannot fill from the path.", "warning"),
        ("duplicate-route", "Web routes", "Route declared twice", "The same method and path handled by two functions.", "warning"),
        ("route-endpoint", "Web routes", "Unknown endpoint name", "url_for or url_path_for with a name no route has.", "weak"),
        ("template-structure", "Django templates", "Unclosed or unmatched tags", "Block tags that do not close, extends that is not first, a block defined twice.", "error"),
        ("template-tag", "Django templates", "Unknown tag", "A tag no loaded library provides.", "weak"),
        ("template-filter", "Django templates", "Unknown filter", "A filter no loaded library provides.", "weak"),
        ("template-static", "Django templates", "Missing static file", "A static tag path no static folder has.", "weak"),
        ("template-block", "Django templates", "Block no parent has", "A block whose content is never shown.", "weak"),
        ("template-url-arguments", "Django templates", "Wrong number of URL arguments", "A url tag with more or fewer arguments than the route takes.", "warning"),
        ("template-attribute", "Django templates", "Unknown attribute", "An attribute a class of the project does not have.", "weak"),
    };

    static readonly string[] InspectionLevels = { "error", "warning", "weak", "hint", "off" };

    (string Path, DateTime Stamp, Dictionary<string, string> Levels)? _profile;

    string? ProfilePath() => SourceRoots.Count == 0 ? null : System.IO.Path.Combine(SourceRoots[0], ".pymagic", "inspections.json");

    /// <summary>The levels the project overrides: code to error, warning, weak, hint or off.</summary>
    Dictionary<string, string> InspectionProfile()
    {
        string? path = ProfilePath();
        if (path == null || !File.Exists(path)) { _profile = null; return new Dictionary<string, string>(); }
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_profile is { } cached && cached.Path == path && cached.Stamp == stamp) return cached.Levels;
        var levels = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var e in doc.RootElement.EnumerateObject())
                if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString()?.ToLowerInvariant() is { } level && InspectionLevels.Contains(level)) levels[e.Name] = level;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { /* a broken file changes nothing */ }
        _profile = (path, stamp, levels);
        return levels;
    }

    public IReadOnlyList<InspectionInfo> Inspections()
    {
        var profile = InspectionProfile();
        return InspectionCatalog.Select(i => new InspectionInfo(i.Code, i.Group, i.Title, i.Description, i.Default, profile.GetValueOrDefault(i.Code, i.Default))).ToList();
    }

    /// <summary>Sets how one inspection is shown (null: the default again) and writes the profile.</summary>
    /// <exception cref="ServiceException">The level or the code is not known, or the file cannot be written.</exception>
    public void SetInspection(string code, string? level)
    {
        if (!InspectionCatalog.Any(i => i.Code == code)) throw new ServiceException($"'{code}' is not an inspection");
        if (level != null && !InspectionLevels.Contains(level)) throw new ServiceException($"'{level}' is not a level (error, warning, weak, hint, off)");
        string path = ProfilePath() ?? throw new ServiceException("there is no project folder to keep the profile in");
        var levels = new SortedDictionary<string, string>(InspectionProfile(), StringComparer.Ordinal);
        if (level == null || level == InspectionCatalog.First(i => i.Code == code).Default) levels.Remove(code); else levels[code] = level;
        try
        {
            if (levels.Count == 0) { if (File.Exists(path)) File.Delete(path); }
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(levels, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServiceException("the profile cannot be written: " + e.Message); }
        _profile = null;
    }

    public void ResetInspections()
    {
        foreach (var code in InspectionProfile().Keys.ToList()) SetInspection(code, null);
    }

    /// <summary>The URIs of every open document, Python and templates.</summary>
    public IEnumerable<string> AllOpenUris => _docs.Keys.Concat(_templateDocs.Keys);

    static int SeverityOf(string level) => level switch { "error" => Severity.Error, "warning" => Severity.Warning, "weak" => Severity.Information, _ => Severity.Hint };

    /// <summary>The findings the profile leaves in, with the severity it gives them.</summary>
    List<Diag> ApplyProfile(List<Diag> diagnostics)
    {
        var profile = InspectionProfile();
        if (profile.Count == 0) return diagnostics;
        var result = new List<Diag>(diagnostics.Count);
        foreach (var d in diagnostics)
        {
            if (d.Code == null || !profile.TryGetValue(d.Code, out var level)) { result.Add(d); continue; }
            if (level == "off") continue;
            int severity = SeverityOf(level);
            // the greyed-out look belongs to the "unused" inspections shown as hints
            result.Add(d with { Severity = severity, Tags = severity == Severity.Hint ? d.Tags : null });
        }
        return result;
    }

    IReadOnlyList<LspDiagnostic> ApplyTemplateProfile(List<LspDiagnostic> found)
    {
        var profile = InspectionProfile();
        if (profile.Count == 0) return found;
        var kept = new List<LspDiagnostic>();
        foreach (var d in found)
        {
            if (d.Code == null || !profile.TryGetValue(d.Code, out var level)) { kept.Add(d); continue; }
            if (level != "off") kept.Add(d with { Severity = SeverityOf(level) });
        }
        return kept;
    }
}
