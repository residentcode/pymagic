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

    /// <summary>The interpreter that runs black and ruff: the project's, else PYMAGIC_PYTHON, else whatever `python` is.</summary>
    string FormatterPython => Interpreter?.Python ?? System.Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";
    ExecutionTarget FormatterTarget => Interpreter?.Target ?? ExecutionTarget.Local;

    /// <summary>The built-in formatter, and black and ruff with what is known of them for the project's interpreter.</summary>
    public IReadOnlyList<FormatterInfo> Formatters()
    {
        var list = new List<FormatterInfo> { new("builtin", "Built-in", true, null) };
        foreach (var tool in ExternalFormatter.Tools)
        {
            FormatterInfo info;
            try { info = ExternalFormatter.Probe(FormatterPython, tool, FormatterTarget); }
            catch (ServiceException) { info = new FormatterInfo(tool, tool, false, null); }
            list.Add(info with { Label = info.Available ? $"{tool} {info.Version}" : $"{tool} (not installed)" });
        }
        return list;
    }

    static string FilePathOf(string uri) =>
        uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uri).LocalPath : "main.py";

    /// <summary>The quotes strings get when the project's pyproject.toml, ruff.toml or .editorconfig does not say (the page's
    /// setting, single quotes by default there); null keeps black's double quotes.</summary>
    public QuoteStyle? DefaultQuotes { get; set; }

    /// <summary>The settings the built-in formatter uses for a file: from pyproject.toml or ruff.toml of its project, else black's defaults.</summary>
    public FormatOptions FormatConfigOf(string uri) =>
        uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? FormatConfig.For(FilePathOf(uri), DefaultQuotes)
            : DefaultQuotes is { } q ? FormatOptions.Default with { Quotes = q } : FormatOptions.Default;

    /// <summary>Formats text with black or ruff and checks that what came back is still Python.</summary>
    string RunExternal(string engine, string text, string uri, (int, int)? lines = null)
    {
        if (!ExternalFormatter.Tools.Contains(engine)) throw new ServiceException($"Unknown formatter '{engine}'.");
        // PyMagic's quote setting, when the project does not name one: ruff is told it; black has no single quotes, so it leaves them as written
        var quotes = DefaultQuotes is { } q && q != QuoteStyle.Double && !FormatConfig.QuotesStated(FilePathOf(uri)) ? q : (QuoteStyle?)null;
        string result = ExternalFormatter.Format(FormatterPython, engine, text, FilePathOf(uri), lines, FormatterTarget, quotes);
        if (PyMagic.Core.Syntax.Parser.Parse(result).Diagnostics.Count > 0)
            throw new ServiceException($"{engine} returned text that does not parse; the file was left as it is.");
        return result;
    }

    /// <summary>Format the document: imports are sorted and the code is laid out. Empty when nothing changes.</summary>
    /// <param name="engine">builtin, black or ruff.</param>
    /// <exception cref="ServiceException">The file has syntax errors, opts out with `fmt: off`, or the tool is missing or fails.</exception>
    public IReadOnlyList<TextEdit> FormatDocument(string uri, bool sortImports = true, string engine = "builtin")
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        if (m.SyntaxErrors.Count > 0) throw new ServiceException("Fix the syntax errors first: a file that does not parse cannot be formatted.");
        string text = m.Source;
        var options = FormatConfigOf(uri);
        if (sortImports)
        {
            string? sorted = null;
            if (engine == "ruff")
            {
                // ruff's own sorter, with the isort settings of the project; ours when ruff cannot be run or returns something that does not parse
                try
                {
                    sorted = ExternalFormatter.SortImports(FormatterPython, text, FilePathOf(uri), FormatterTarget);
                    if (PyMagic.Core.Syntax.Parser.Parse(sorted).Diagnostics.Count > 0) sorted = null;
                }
                catch (ServiceException) { sorted = null; }
            }
            text = sorted ?? ImportSorter.Sort(text, ImportClassifier(snap), options.LineLength, options.Imports) ?? text;
        }
        string formatted = engine is "builtin" or ""
            ? PythonFormatter.Format(text, options) ?? throw new ServiceException("This file cannot be formatted (it has `fmt: off`, or the result could have changed the code).")
            : RunExternal(engine, text, uri);
        return DiffEdit(m, formatted);
    }

    /// <summary>
    /// Formats a piece of text that is not a document of its own (a cell of a notebook), with the settings of the project <paramref name="uri"/> belongs to.
    /// A cell has no newline at its end by convention, so none is left there when the text had none. Imports are not sorted: they are another cell's business.
    /// </summary>
    /// <exception cref="ServiceException">The text has syntax errors, opts out with `fmt: off`, or the tool is missing or fails.</exception>
    public string FormatText(string text, string uri, string engine = "builtin", Range? range = null)
    {
        // `%magic` and `!command` lines are set aside while the rest is laid out, and put back after
        var magics = CellMagics.Mask(text);
        string code = magics?.Text ?? text;
        if (PyMagic.Core.Syntax.Parser.Parse(code).Diagnostics.Count > 0) throw new ServiceException("Fix the syntax errors first: code that does not parse cannot be formatted.");
        string formatted;
        if (range is { } r)
        {
            // only the lines the selection touches (the placeholders keep the lines where they were, so the range still fits), by the tool
            // chosen in the toolbar when it is black or ruff
            if (engine is "black" or "ruff")
                formatted = RunExternal(engine, code, uri, SelectedLines(r));
            else
            {
                var lm = new LineMap(code);
                int start = lm.OffsetOf(new Position(r.Start.Line, 0)), end = lm.OffsetOf(r.End);
                formatted = PythonFormatter.FormatRange(code, start, end, FormatConfigOf(uri))
                    ?? throw new ServiceException("This selection cannot be formatted on its own (it has `fmt: off`, or formatting it would change the code): select whole statements, or format the cell.");
            }
        }
        else
            formatted = engine is "builtin" or ""
                ? PythonFormatter.Format(code, FormatConfigOf(uri)) ?? throw new ServiceException("This code cannot be formatted (it has `fmt: off`, or the result could have changed it).")
                : RunExternal(engine, code, uri);
        if (magics is { } set) formatted = CellMagics.Unmask(formatted, set.Lines);
        return text.EndsWith('\n') ? formatted : formatted.TrimEnd('\n');
    }

    /// <summary>The 1-based first and last line of a selection; one that ends at the start of a line does not include that line.</summary>
    static (int First, int Last) SelectedLines(Range range)
    {
        int last = range.End.Line + 1 - (range.End.Character == 0 && range.End.Line > range.Start.Line ? 1 : 0);
        return (range.Start.Line + 1, Math.Max(last, range.Start.Line + 1));
    }

    /// <summary>Formats the lines the range touches. black (23.11 or newer) and ruff (0.2 or newer) format them themselves; the built-in formatter otherwise.</summary>
    /// <exception cref="ServiceException">The selection cannot be formatted on its own.</exception>
    public IReadOnlyList<TextEdit> FormatRange(string uri, Range range, string engine = "builtin")
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        if (m.SyntaxErrors.Count > 0) throw new ServiceException("Fix the syntax errors first: a file that does not parse cannot be formatted.");
        var lm = Lines(m);
        int start = lm.OffsetOf(range.Start), end = lm.OffsetOf(range.End);

        string formatted;
        if (engine is "black" or "ruff")
            formatted = RunExternal(engine, m.Source, uri, SelectedLines(range));
        else
            formatted = PythonFormatter.FormatRange(m.Source, start, end, FormatConfigOf(uri))
                ?? throw new ServiceException("This selection cannot be formatted on its own (it has `fmt: off`, or formatting it would change the code): select whole statements, or format the file.");
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
        var options = snap.UriOf.TryGetValue(m, out var uri) ? FormatConfigOf(uri) : FormatOptions.Default;
        return ImportSorter.Sort(text, ImportClassifier(snap), options.LineLength, options.Imports);
    }
}
