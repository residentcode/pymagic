using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>What else follows a module that moves (item 132): the `__all__` of its package, the imports of the project's notebooks, and `{% load %}` in templates.</summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ __all__ of a package

    /// <summary>
    /// A package's `__all__` that names one of its submodules which it does not import (`from pkg import *` imports it then): renamed when the submodule
    /// stays in the package, taken out (and said) when it leaves it. A name the package binds itself (an import kept under its name by the move) stays.
    /// </summary>
    List<Edit> AllListEdits(ModuleInfo x, Func<string, string?> map, List<string> warnings)
    {
        var edits = new List<Edit>();
        string newPackage = map(x.Name) ?? x.Name;
        var bound = BoundAtTop(x);
        foreach (var stmt in x.Ast.Body)
        {
            if (stmt is not AssignStmt { Targets: [NameExpr { Name: "__all__" }], Value: var value }) continue;
            var items = value switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
            if (items == null) continue;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not ConstExpr { Kind: ConstKind.Str } c || FixtureModelString(c) is not { } name || bound.Contains(name)) continue;
                string full = x.Name.Length == 0 ? name : x.Name + "." + name;
                if (map(full) is not { } to || to == full) continue;
                if (Parent(to) == newPackage)
                {
                    // the quotes stay as written
                    int q = x.Source.IndexOf(name, c.Start, StringComparison.Ordinal);
                    if (q >= 0 && q + name.Length <= c.End) edits.Add(new Edit(q, q + name.Length, Last(to)));
                    continue;
                }
                // the item and the comma after it (or before it, for the last one)
                int start = c.Start, end = c.End;
                if (i + 1 < items.Count) end = items[i + 1].Start;
                else if (i > 0) start = items[i - 1].End;
                edits.Add(new Edit(start, end, ""));
                warnings.Add($"\"{name}\" is taken out of the __all__ of {(x.Name.Length == 0 ? "the top package" : x.Name)}: it moves to {to}, so `from {x.Name} import *` no longer brings it.");
            }
        }
        return edits;
    }

    /// <summary>The names a module binds at its top: imports, functions, classes and plain assignments.</summary>
    static HashSet<string> BoundAtTop(ModuleInfo x)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in x.Ast.Body)
        {
            switch (stmt)
            {
                case ImportStmt i: foreach (var a in i.Names) names.Add(a.AsName ?? a.Name.Split('.')[0]); break;
                case ImportFromStmt f: foreach (var a in f.Names) names.Add(a.AsName ?? a.Name); break;
                case FunctionDef fd: names.Add(fd.Name); break;
                case ClassDef cd: names.Add(cd.Name); break;
                case AssignStmt a: foreach (var t in a.Targets) if (t is NameExpr n) names.Add(n.Name); break;
            }
        }
        return names;
    }

    // ------------------------------------------------------------ notebooks

    /// <summary>One string of a notebook's JSON that holds code: where it is in the file, where its text is in the notebook's code, the text, and how it was written.</summary>
    sealed record NotebookPiece(int TokenStart, int TokenEnd, int CodeStart, string Text, bool Ascii);

    /// <summary>
    /// The imports of the project's notebooks (`.ipynb`, read from disk): the code cells are put together as one module (magics and shell lines blanked,
    /// cells with a cell magic left out), the same edits as for a module are made, and each lands on the JSON string of the source line it changes, so
    /// the rest of the file stays as Jupyter wrote it. A change that would join or split lines makes the line's string hold a line break.
    /// </summary>
    Dictionary<string, List<TextEdit>> NotebookMoveEdits(Func<string, string?> map, List<string> warnings)
    {
        var changes = new Dictionary<string, List<TextEdit>>();
        foreach (var path in ProjectFilesWithExtension(".ipynb"))
        {
            string raw;
            try { raw = File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (raw.IndexOf("import", StringComparison.Ordinal) < 0 && raw.IndexOf('.') < 0) continue;
            if (NotebookCode(raw) is not var (code, pieces)) { warnings.Add($"{System.IO.Path.GetFileName(path)} could not be read as a notebook, so its imports are left as they are."); continue; }
            // parsed in a workspace of its own: the notebook's code is not one of the project's modules
            var module = new Workspace(new WorkspaceOptions()).AddModule("__main__", code, cached: false);
            if (module.SyntaxErrors.Count > 0) continue;
            var edits = ImportEditsFor(module, map, "__main__", false, warnings);
            if (edits.Count == 0) continue;
            var lines = new LineMap(raw);
            var result = NotebookTextEdits(pieces, edits).Select(p => new TextEdit(lines.RangeOf(p.Piece.TokenStart, p.Piece.TokenEnd), JsonString(p.Text, p.Piece.Ascii))).ToList();
            if (result.Count > 0) changes[UriOfPath(path)] = result;
        }
        return changes;
    }

    /// <summary>The code of a notebook's code cells as one text, and the JSON strings it comes from; null when the file is not a notebook PyMagic can follow.</summary>
    static (string Code, List<NotebookPiece> Pieces)? NotebookCode(string raw)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) return null;
            var code = new StringBuilder();
            var pieces = new List<NotebookPiece>();
            int cursor = 0;
            foreach (var cell in cells.EnumerateArray())
            {
                if (!cell.TryGetProperty("source", out var source)) continue;
                var strings = source.ValueKind switch
                {
                    JsonValueKind.Array => source.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToList(),
                    JsonValueKind.String => new List<string> { source.GetString()! },
                    _ => new List<string>(),
                };
                // each string is found in the file after the one before it: the JSON strings come in the order of the cells
                var found = new List<(int Start, int End, bool Ascii)>();
                foreach (var s in strings)
                {
                    int at = -1; bool ascii = false;
                    foreach (bool a in new[] { false, true })
                    {
                        at = raw.IndexOf(JsonString(s, a), cursor, StringComparison.Ordinal);
                        if (at >= 0) { ascii = a; break; }
                    }
                    if (at < 0) return null;
                    int end = at + JsonString(s, ascii).Length;
                    found.Add((at, end, ascii));
                    cursor = end;
                }
                bool isCode = cell.TryGetProperty("cell_type", out var kind) && kind.GetString() == "code";
                if (!isCode || strings.Count == 0 || strings[0].TrimStart().StartsWith("%%", StringComparison.Ordinal)) continue;
                for (int k = 0; k < strings.Count; k++)
                {
                    pieces.Add(new NotebookPiece(found[k].Start, found[k].End, code.Length, strings[k], found[k].Ascii));
                    code.Append(Unmagic(strings[k]));
                }
                code.Append('\n');   // between cells: no string of the file
            }
            return (code.ToString(), pieces);
        }
    }

    /// <summary>A line of IPython that is not Python (`%magic`, `!shell`, `name?`, `?name`) as spaces, so that the offsets stay.</summary>
    static string Unmagic(string text) => Regex.Replace(text, @"(?m)^[ \t]*(?:[%!].*|\??[\w.]+\?{1,2}|\?{1,2}[\w.]+)[ \t]*$", m => new string(' ', m.Length));

    /// <summary>The new text of each JSON string the edits touch: an edit inside one string changes it, one across several puts its text in the first and empties the rest.</summary>
    static List<(NotebookPiece Piece, string Text)> NotebookTextEdits(List<NotebookPiece> pieces, List<Edit> edits)
    {
        var text = pieces.ToDictionary(p => p, p => p.Text);
        int PieceAt(int offset, bool end) => pieces.FindIndex(p => end ? offset > p.CodeStart && offset <= p.CodeStart + p.Text.Length : offset >= p.CodeStart && offset < p.CodeStart + p.Text.Length);
        // from the end, so that the offsets of the earlier edits stay right
        foreach (var e in edits.OrderByDescending(e => e.Start))
        {
            int first = PieceAt(e.Start, false), last = e.End == e.Start ? first : PieceAt(e.End, true);
            if (first < 0 || last < first) continue;   // between two cells: not code of any string
            var a = pieces[first];
            var b = pieces[last];
            string head = text[a][..(e.Start - a.CodeStart)];
            string tail = text[b][(e.End - b.CodeStart)..];
            text[a] = head + e.Text + tail;
            for (int k = first + 1; k <= last; k++) text[pieces[k]] = "";
        }
        return pieces.Where(p => text[p] != p.Text).Select(p => (p, text[p])).ToList();
    }

    /// <summary>A string as JSON writes it: as Jupyter does (non-ASCII as it is), or with every non-ASCII character as \uXXXX.</summary>
    static string JsonString(string s, bool ascii)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ascii && ch > 0x7e) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The files of the project with this extension (below the source folders, without the folders PyMagic never looks in).</summary>
    IEnumerable<string> ProjectFilesWithExtension(string extension)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SourceRootsNow())
        {
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                List<string> files, subdirs;
                try { files = Directory.EnumerateFiles(dir, "*" + extension).ToList(); subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files) if (seen.Add(f)) yield return f;
                foreach (var d in subdirs)
                {
                    string name = System.IO.Path.GetFileName(d);
                    if (!SkippedDirectories.Contains(name) && !name.StartsWith('.')) stack.Push(d);
                }
            }
        }
    }

    // ------------------------------------------------------------ {% load %} in templates

    /// <summary>
    /// A Django tag library is loaded by its file name (`{% load shop_tags %}` for `app/templatetags/shop_tags.py`): a library renamed, or moved to another
    /// `templatetags` folder under another name, is loaded by its new name in every template; one moved out of every `templatetags` folder is said.
    /// </summary>
    Dictionary<string, List<TextEdit>> TemplateLoadEdits(Snapshot snap, Func<string, string?> map, List<string> warnings)
    {
        static string? LibraryOf(string module) => Parent(module) is var p && (p == "templatetags" || p.EndsWith(".templatetags", StringComparison.Ordinal)) ? Last(module) : null;
        var renamed = new Dictionary<string, string?>(StringComparer.Ordinal);   // old library name → new (null: no longer a library)
        foreach (var m in snap.Project)
        {
            if (m.IsStub || m.IsPackage || LibraryOf(m.Name) is not { } old || map(m.Name) is not { } to) continue;
            string? now = LibraryOf(to);
            if (now != old) renamed[old] = now;
        }
        var changes = new Dictionary<string, List<TextEdit>>();
        if (renamed.Count == 0) return changes;
        var lost = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (uri, text) in TemplateTexts())
        {
            if (text.IndexOf("load", StringComparison.Ordinal) < 0) continue;
            var list = new List<TextEdit>();
            LineMap? lines = null;
            foreach (var tag in DjangoTemplateParser.Parse(text).Tags.Where(t => t.Name == "load"))
            {
                var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
                int from = args.FindIndex(a => a.Text == "from");
                // {% load a b from library %} names one library, after "from"; {% load x y %} names libraries only
                var libraries = from > 0 ? args.Skip(from + 1).Take(1) : args;
                foreach (var a in libraries)
                {
                    if (!renamed.TryGetValue(a.Text, out var now)) continue;
                    if (now == null) { lost.Add(a.Text); continue; }
                    lines ??= new LineMap(text);
                    list.Add(new TextEdit(lines.RangeOf(a.Offset, a.Offset + a.Text.Length), now));
                }
            }
            if (list.Count > 0) changes[uri] = list;
        }
        foreach (var name in lost)
            warnings.Add($"Templates load the tag library \"{name}\", which moves out of a templatetags folder: Django will not find it there.");
        return changes;
    }

    /// <summary>Every template's text, by URI: the open buffers win over the files.</summary>
    IEnumerable<(string Uri, string Text)> TemplateTexts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uri, doc) in _templateDocs)
        {
            if (PathOfUri(uri) is { } p) seen.Add(p);
            yield return (uri, doc.Text);
        }
        foreach (var t in Templates())
        {
            if (!seen.Add(t.Path)) continue;
            if (TemplateTextOf(t.Path) is { } text) yield return (UriOfPath(t.Path), text);
        }
    }
}
