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
        ("call-arguments", "Python", "Wrong arguments in a call", "Too many or too few arguments, an unknown keyword, a parameter given twice.", "warning"),
        ("argument-type", "Python", "Argument of the wrong type", "An argument the parameter's annotation does not accept (needs the type mismatch option).", "warning"),
        ("not-callable", "Python", "Object is not callable", "A call of a module or of an instance that has no __call__.", "warning"),
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
        ("statement-effect", "Python", "Statement has no effect", "An expression statement that only computes a value nobody uses.", "weak"),
        ("trailing-semicolon", "Python", "Trailing semicolon", "A semicolon at the end of a statement.", "weak"),
        ("redundant-parentheses", "Python", "Redundant parentheses", "Parentheses around the whole condition of if or while, or the value of return.", "weak"),
        ("chained-comparison", "Python", "Chained comparison can be simplified", "a < b and b < c is a < b < c.", "weak"),
        ("simplify-boolean", "Python", "Comparison with True or False", "x == True can be written x.", "weak"),
        ("list-creation", "Python", "List creation can be a literal", "A list followed by append calls that could be in the literal.", "weak"),
        ("dict-creation", "Python", "Dictionary creation can be a literal", "A dict followed by item assignments that could be in the literal.", "weak"),
        ("set-literal", "Python", "set() of a list can be a set literal", "set([1, 2]) is {1, 2}.", "weak"),
        ("augment-assignment", "Python", "Assignment can be augmented", "x = x + 1 is x += 1.", "weak"),
        ("unhashable", "Python", "Unhashable dictionary key or set item", "A list, dict or set used as a key or an item.", "warning"),
        ("global-undefined", "Python", "Global variable undefined at module level", "global x with no x at the module level.", "warning"),
        ("super-arguments", "Python", "Wrong first argument of super()", "super(A, self) written in another class.", "warning"),
        ("invalid-escape", "Python", "Invalid escape sequence", "A backslash in a non-raw string that starts no escape.", "weak"),
        ("unnecessary-backslash", "Python", "Unnecessary backslash", "A line continuation inside brackets.", "weak"),
        ("string-format", "Python", "String format does not fit its arguments", "Too few or too many arguments for % or str.format.", "warning"),
        ("method-may-be-static", "Python classes", "Method may be static", "A method that never uses self.", "weak"),
        ("attribute-outside-init", "Python classes", "Instance attribute defined outside __init__", "self.x assigned first in a method other than __init__.", "weak"),
        ("return-from-init", "Python classes", "__init__ returns a value", "__init__ should return None.", "warning"),
        ("first-arg-assignment", "Python classes", "Assignment to self or cls", "The first argument of a method is overwritten.", "weak"),
        ("loop-variable-assignment", "Python", "Loop or with variable overwritten", "Assigning to the variable of a for loop or with statement inside it.", "weak"),
        ("inconsistent-returns", "Python", "Inconsistent return statements", "Some paths return a value and others return nothing.", "weak"),
        ("pep8-line-length", "PEP 8", "Line too long (E501)", "A line over 120 characters.", "weak"),
        ("pep8-whitespace", "PEP 8", "Whitespace (W291, W293, W292, W391)", "Trailing whitespace, blank lines with spaces, no newline at the end of the file, blank lines at the end.", "weak"),
        ("pep8-blank-lines", "PEP 8", "Blank lines (E301, E302, E303, E305)", "Two blank lines around top-level definitions, one between methods, no more than two.", "weak"),
        ("pep8-comments", "PEP 8", "Comments (E261, E262, E265)", "Two spaces before an inline comment, a space after the hash.", "weak"),
        ("pep8-statements", "PEP 8", "Statements (E401, E701, E702, E731)", "Several imports or statements on one line, a lambda assigned to a name.", "weak"),
        ("pep8-ambiguous-name", "PEP 8", "Ambiguous variable name (E741)", "l, O or I as a name.", "weak"),
        ("protected-member", "Python", "Access to a protected member", "x._name used on an object or module from outside its class.", "weak"),
        ("tuple-item-assignment", "Python", "Tuple item assignment", "t[0] = 1 where t is a tuple.", "warning"),
        ("none-function-assignment", "Python", "Function returns nothing", "x = f() where f has no return value.", "weak"),
        ("except-clauses-order", "Python", "Exception clause already handled", "An except clause for a class an earlier clause already catches.", "warning"),
        ("exception-inherit", "Python", "Not an exception class", "raise or except of something that does not derive from BaseException.", "warning"),
        ("property-access", "Python classes", "Property cannot be set", "Assignment to a property that has no setter.", "warning"),
        ("naming-class", "PEP 8 naming", "Class names", "Classes are written CapWords.", "weak"),
        ("naming-function", "PEP 8 naming", "Function and method names", "Functions are written snake_case.", "weak"),
        ("naming-argument", "PEP 8 naming", "Parameter names", "Parameters are written snake_case.", "weak"),
        ("naming-variable", "PEP 8 naming", "Variable names", "Variables are written snake_case.", "weak"),
        ("shadowed-builtin", "PEP 8 naming", "Shadowing a built-in name", "A variable, parameter or function that hides a built-in.", "weak"),
        ("fixture-not-found", "pytest", "Fixture not found", "A test parameter that no fixture provides.", "weak"),
        ("django-field", "Django", "Unknown field in a query", "A keyword or string that is not a field of the model.", "warning"),
        ("django-meta-field", "Django", "Unknown field in Meta, form, serializer or admin", "A name in fields, ordering, list_display, search_fields and the like that the model does not have.", "warning"),
        ("django-view-serializer", "Django", "Serializer for another model than the queryset", "A REST framework view whose serializer_class is for a model its queryset does not return.", "warning"),
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

    /// <summary>The severity an inspection has without a profile: the default of its catalog entry.</summary>
    static int DefaultSeverity(string? code) =>
        code != null && InspectionCatalog.FirstOrDefault(i => i.Code == code) is { Code: not null } entry ? SeverityOf(entry.Default) : Severity.Warning;

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
