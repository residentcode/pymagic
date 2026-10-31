using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;

namespace PyMagic.Server;

/// <summary>A link of the margin: in a view, from the line naming a template to its file; in a template, to a view that renders it.</summary>
public sealed record TemplateLink(int Line, string Label, string Uri, int TargetLine);

/// <summary>Between views and templates: the margin's links both ways, the template folders, and Create template for a name no folder has.</summary>
public sealed partial class LanguageService
{
    /// <summary>The links of a file: a Python file's template names to their files, or a template to the views and functions that render it.</summary>
    public IReadOnlyList<TemplateLink> TemplateLinks(string uri)
    {
        var templates = Templates();
        var links = new List<TemplateLink>();
        if (_templateDocs.ContainsKey(uri) || IsTemplateUri(uri))
        {
            string? path = PathOfUri(uri);
            if (path == null) return links;
            var names = templates.Where(t => string.Equals(System.IO.Path.GetFullPath(t.Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count == 0) return links;
            var snap = Full();
            foreach (var m in snap.Project.Where(p => InProject(snap, p)))
                foreach (var s in NamedStringsIn(m, null))
                {
                    if (s.Kind != DjangoNamed.Template || !names.Contains(s.Value)) continue;
                    // the function or class around the string: the view
                    string owner = m.Name;
                    Node at = (Node?)s.Call ?? AstChildren.Descendants(m.Ast).OfType<AssignStmt>().FirstOrDefault(a => a.Start <= s.Start && s.End <= a.End) ?? (Node)m.Ast;
                    for (var p = m.ParentOf(at); p != null; p = m.ParentOf(p))
                        if (p is FunctionDef f) { owner = f.Name; if (m.ParentOf(f) is ClassDef c) owner = c.Name + "." + f.Name; break; }
                        else if (p is ClassDef c2) { owner = c2.Name; break; }
                    links.Add(new TemplateLink(0, $"{owner} ({m.Name})", UriOfModule(m) ?? "", Lines(m).RangeOf(s.Start, s.Start).Start.Line));
                }
            return links.DistinctBy(l => (l.Uri, l.TargetLine)).ToList();
        }
        var snapshot = Full();
        if (!snapshot.ByUri.TryGetValue(uri, out var module)) return links;
        var lines = Lines(module);
        foreach (var s in NamedStringsIn(module, null))
        {
            if (s.Kind != DjangoNamed.Template) continue;
            foreach (var t in templates.Where(t => t.Name == s.Value).Take(3))
                links.Add(new TemplateLink(lines.RangeOf(s.Start, s.Start).Start.Line, t.Name, UriOfPath(t.Path), 0));
        }
        return links;
    }

    /// <summary>The folders templates are looked for in: every `templates` folder of the project, and the template folders marked or named in TEMPLATES['DIRS'].</summary>
    public IReadOnlyList<string> TemplateFolders()
    {
        var found = new List<string>();
        foreach (var root in SourceRoots)
        {
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && found.Count < 200)
            {
                string dir = stack.Pop();
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var d in subdirs)
                {
                    string name = System.IO.Path.GetFileName(d);
                    if (SkippedDirectories.Contains(name) || name.StartsWith('.')) continue;
                    if (name == "templates") found.Add(d);
                    else stack.Push(d);
                }
            }
        }
        found.AddRange(ProjectFolders.TemplateRoots.Where(Directory.Exists));
        return found.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static readonly Regex MissingTemplate = new(@"^Template '(?<name>[^']+)' is not in any templates folder", RegexOptions.Compiled);

    /// <summary>Create template for each "not in any templates folder" finding the range touches: the page asks which folder and makes the file.</summary>
    IEnumerable<CodeAction> CreateTemplateActions(string uri, Range range)
    {
        foreach (var d in Diagnostics(uri))
        {
            if (d.Code != "django-template" || MissingTemplate.Match(d.Message) is not { Success: true } match) continue;
            if (d.Range.End.Line < range.Start.Line || d.Range.Start.Line > range.End.Line) continue;
            string name = match.Groups["name"].Value;
            yield return new CodeAction($"Create template '{name}'", QuickFixKind, null, true,
                new LspCommand($"Create template {name}", "pymagic.createTemplate", new object[] { name, TemplateFolders() }));
        }
    }
}
