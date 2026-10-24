using PyMagic.Core.Types;
using PyMagic.Server.Formatting;

namespace PyMagic.Server;

public sealed partial class LanguageService
{
    /// <summary>Which import section a top-level module name belongs to: the standard library, this project, or installed packages.</summary>
    Func<string, ImportSection> ImportClassifier(Snapshot snap)
    {
        var project = new HashSet<string>(snap.Project.Where(p => !p.IsStub).Select(p => p.Name.Split('.')[0]), StringComparer.Ordinal);
        return top => StdlibNames.All.Contains(top) ? ImportSection.Stdlib : project.Contains(top) ? ImportSection.FirstParty : ImportSection.ThirdParty;
    }

    /// <summary>One edit that turns the old text into the new one, covering only what differs.</summary>
    List<TextEdit> DiffEdit(ModuleInfo m, string newText)
    {
        string old = m.Source;
        if (old == newText) return new List<TextEdit>();
        int prefix = 0;
        while (prefix < old.Length && prefix < newText.Length && old[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < old.Length - prefix && suffix < newText.Length - prefix && old[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;
        var lines = Lines(m);
        return new List<TextEdit> { new(lines.RangeOf(prefix, old.Length - suffix), newText[prefix..^suffix]) };
    }

    /// <summary>Format the document: imports are sorted and the code is laid out. Empty when nothing changes.</summary>
    /// <exception cref="ServiceException">The file has syntax errors or opts out with `fmt: off`.</exception>
    public IReadOnlyList<TextEdit> FormatDocument(string uri, bool sortImports = true)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        string text = m.Source;
        if (sortImports) text = ImportSorter.Sort(text, ImportClassifier(snap)) ?? text;
        string formatted = PythonFormatter.Format(text)
            ?? throw new ServiceException(m.SyntaxErrors.Count > 0 ? "Fix the syntax errors first: a file that does not parse cannot be formatted." : "This file cannot be formatted (it has `fmt: off`, or the result could have changed the code).");
        return DiffEdit(m, formatted);
    }

    /// <summary>Removes the unused imports and sorts the rest. Empty when the imports are already in order.</summary>
    public IReadOnlyList<TextEdit> OrganizeImports(string uri)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        return DiffEdit(m, OrganizedText(snap, m) ?? throw new ServiceException("The imports of this file cannot be sorted (syntax errors, or `isort: skip_file`)."));
    }

    string? OrganizedText(Snapshot snap, ModuleInfo m)
    {
        string text = m.Source;
        if (m.SyntaxErrors.Count > 0) return null;
        var unused = UnusedImports(m).ToList();
        if (unused.Count > 0)
        {
            var edits = unused.GroupBy(u => u.Statement).Select(g => RemoveAliases(m, g.Key, g.Select(u => u.Alias).ToList()))
                .OrderByDescending(e => e.Start).ToList();
            foreach (var e in edits) text = text[..e.Start] + e.Text + text[e.End..];
        }
        return ImportSorter.Sort(text, ImportClassifier(snap));
    }
}
