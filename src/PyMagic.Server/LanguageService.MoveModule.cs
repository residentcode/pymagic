using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>What moving or renaming a module or package changes in the project: the edits (keyed by the files' present URIs) and what could not be done.</summary>
public sealed record ModuleMovePlan(Dictionary<string, List<TextEdit>> Changes, List<string> Warnings, string? From, string? To);

public sealed partial class LanguageService
{
    // ------------------------------------------------------------ a module or package moves to another place

    /// <summary>
    /// The edits that keep the project's imports working when the file or folder at <paramref name="fromUri"/> moves to <paramref name="toUri"/> (Explorer's
    /// drag, cut and paste, and rename): `import pkg.b`, `from pkg.b import x`, `from pkg import b`, the dotted uses of `import pkg.b` (`pkg.b.f()`), relative
    /// imports that would point elsewhere from the new place (also those of the moved code itself), and strings that name the module (`mock.patch("pkg.b.f")`).
    /// The edits are made before the move, on the files where they are now.
    /// </summary>
    public ModuleMovePlan PlanModuleMove(string fromUri, string toUri)
    {
        var snap = Full();
        var warnings = new List<string>();
        var changes = new Dictionary<string, List<TextEdit>>();
        string? fromPath = PathOfUri(fromUri), toPath = PathOfUri(toUri);
        if (fromPath == null || toPath == null) return new ModuleMovePlan(changes, warnings, null, null);
        fromPath = System.IO.Path.GetFullPath(fromPath).TrimEnd('\\', '/');
        toPath = System.IO.Path.GetFullPath(toPath).TrimEnd('\\', '/');
        char sep = System.IO.Path.DirectorySeparatorChar;

        bool Under(string path) => string.Equals(path, fromPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(fromPath + sep, StringComparison.OrdinalIgnoreCase);
        // an open file's module is known by its URI, the others by their path
        string? FileOf(ModuleInfo m) => (snap.UriOf.TryGetValue(m, out var u) ? PathOfUri(u) : null) ?? m.Path;
        var moved = snap.Project.Where(m => !m.IsStub && FileOf(m) is { } f && (f.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase))
            && Under(System.IO.Path.GetFullPath(f))).ToList();
        if (moved.Count == 0) return new ModuleMovePlan(changes, warnings, null, null);

        // the old and new dotted names: the folder that holds the top package is the same before and after
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? oldPrefix = null, newPrefix = null;
        foreach (var m in moved)
        {
            string file = System.IO.Path.GetFullPath(FileOf(m)!);
            string? root = System.IO.Path.GetDirectoryName(file);
            int up = m.Name.Split('.').Length - (m.IsPackage ? 0 : 1);
            for (int i = 0; i < up && root != null; i++) root = System.IO.Path.GetDirectoryName(root);
            if (root == null) continue;
            string newFile = toPath + file[fromPath.Length..];
            string? newName = DottedName(root, newFile);
            if (newName == null)
            {
                warnings.Add($"{m.Name} would not be importable from its new place (outside the source folder, or a folder name that is not a Python name), so its imports are left as they are.");
                return new ModuleMovePlan(new(), warnings, null, null);
            }
            map[m.Name] = newName;
            // the moved folder itself (a namespace package has no module of its own)
            if (oldPrefix == null && DottedName(root, fromPath) is { } op && DottedName(root, toPath) is { } np) { oldPrefix = op; newPrefix = np; }
        }
        bool folder = Directory.Exists(fromPath);
        string? Map(string name)
        {
            if (map.TryGetValue(name, out var exact)) return exact;
            if (folder && oldPrefix is { Length: > 0 } && name.StartsWith(oldPrefix + ".", StringComparison.Ordinal)) return newPrefix + name[oldPrefix.Length..];
            if (folder && oldPrefix is { Length: > 0 } && name == oldPrefix) return newPrefix;
            return null;
        }
        if (map.All(kv => kv.Key == kv.Value)) return new ModuleMovePlan(changes, warnings, null, null);

        foreach (var x in snap.Project)
        {
            if (x.IsStub || !snap.UriOf.TryGetValue(x, out var xUri) || x.SyntaxErrors.Count > 0) continue;
            // the code of an open notebook (.pymagic/notebooks/...): the notebook's own file takes the edits
            if (xUri.Contains("/.pymagic/", StringComparison.Ordinal)) continue;
            var edits = ImportEditsFor(x, Map, Map(x.Name) ?? x.Name, x.IsPackage, warnings);
            if (x.IsPackage) edits.AddRange(AllListEdits(x, Map, warnings));
            if (edits.Count == 0) continue;
            var lines = Lines(x);
            changes[xUri] = edits.OrderBy(e => e.Start).Select(e => new TextEdit(lines.RangeOf(e.Start, e.End), e.Text)).ToList();
        }
        // the notebooks' imports, and the templates that load a tag library by its file name
        foreach (var (uri, edits) in NotebookMoveEdits(Map, warnings)) changes[uri] = edits;
        foreach (var (uri, edits) in TemplateLoadEdits(snap, Map, warnings)) changes[uri] = edits;
        warnings.AddRange(NewImportCycles(snap, Map));
        string first = moved.OrderBy(m => m.Name.Length).First().Name;
        return new ModuleMovePlan(changes, warnings, first, Map(first));
    }

    // ------------------------------------------------------------ circular imports a move makes

    /// <summary>
    /// The circular imports that exist only after the move. A move renames modules, so the imports keep their shape, except that importing a module first runs
    /// the `__init__.py` of each package it is in: `app` importing `other.b` now runs `other/__init__.py`, and when that imports `app` the two import each
    /// other (Python stops with "partially initialized module"). Packages the importing module is itself in are already running and add nothing.
    /// </summary>
    static List<string> NewImportCycles(Snapshot snap, Func<string, string?> map)
    {
        var oldNames = new HashSet<string>(snap.Project.Where(m => !m.IsStub).Select(m => m.Name), StringComparer.Ordinal);
        string New(string name) => map(name) ?? name;
        var newNames = new HashSet<string>(oldNames.Select(New), StringComparer.Ordinal);

        // what each module imports, by the names before the move
        var raw = new List<(string From, string To)>();
        foreach (var m in snap.Project)
        {
            if (m.IsStub) continue;
            foreach (var n in AstChildren.Descendants(m.Ast))
            {
                switch (n)
                {
                    case ImportStmt i:
                        foreach (var al in i.Names) raw.Add((m.Name, al.Name));
                        break;
                    case ImportFromStmt f when Workspace.AbsoluteName(m, f.Level, f.Module) is { } abs:
                        if (abs.Length > 0) raw.Add((m.Name, abs));
                        foreach (var al in f.Names)
                        {
                            string sub = abs.Length == 0 ? al.Name : abs + "." + al.Name;
                            if (oldNames.Contains(sub)) raw.Add((m.Name, sub));
                        }
                        break;
                }
            }
        }

        static IEnumerable<string> SelfAndPackages(string name)
        {
            for (string n = name; n.Length > 0; n = Parent(n)) yield return n;
        }
        // the edges of a graph: the module imported and the packages around it, less those the importer is in
        Dictionary<string, HashSet<string>> Graph(Func<string, string> rename, HashSet<string> names)
        {
            var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (from, to) in raw)
            {
                string f = rename(from), t = rename(to);
                var own = new HashSet<string>(SelfAndPackages(f), StringComparer.Ordinal);
                if (!graph.TryGetValue(f, out var set)) graph[f] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var target in SelfAndPackages(t))
                    if (names.Contains(target) && !own.Contains(target)) set.Add(target);
            }
            return graph;
        }
        var before = Graph(n => n, oldNames);
        var after = Graph(New, newNames);
        var back = oldNames.ToDictionary(New, n => n, StringComparer.Ordinal);

        bool Reaches(Dictionary<string, HashSet<string>> g, string start, string goal, out List<string> path)
        {
            var from = new Dictionary<string, string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(start);
            from[start] = "";
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!g.TryGetValue(cur, out var next)) continue;
                foreach (var nx in next)
                {
                    if (nx == goal)
                    {
                        path = new List<string> { goal };
                        for (string p = cur; p.Length > 0; p = from[p]) path.Add(p);
                        path.Reverse();
                        return true;
                    }
                    if (from.TryAdd(nx, cur)) queue.Enqueue(nx);
                }
            }
            path = new List<string>();
            return false;
        }

        var warnings = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, targets) in after)
            foreach (var t in targets)
            {
                // an edge of a loop after the move, whose loop was not there before
                if (!Reaches(after, t, node, out var path)) continue;
                // the two already imported each other, one way or another, before the move
                if (back.TryGetValue(node, out var oldNode) && back.TryGetValue(t, out var oldT) && Reaches(before, oldNode, oldT, out _) && Reaches(before, oldT, oldNode, out _)) continue;
                var loop = new List<string> { node };
                loop.AddRange(path);
                string key = string.Join(",", loop.Skip(1).OrderBy(x => x, StringComparer.Ordinal));
                if (!reported.Add(key)) continue;
                warnings.Add($"After the move {string.Join(" → ", loop)} import each other in a loop (importing a module runs the __init__.py of its package first): Python may stop with \"partially initialized module\". Move the import into a function, or choose another place.");
            }
        return warnings;
    }

    /// <summary>The dotted name of a file or folder under a source folder: `root/pkg/b.py` is `pkg.b`, `root/pkg/__init__.py` is `pkg`; null when it cannot be imported.</summary>
    static string? DottedName(string root, string path)
    {
        string rel = System.IO.Path.GetRelativePath(root, path);
        if (rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel)) return null;
        if (rel == ".") return "";
        var parts = rel.Split('\\', '/').ToList();
        string last = parts[^1];
        if (last.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || last.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase)) parts[^1] = System.IO.Path.GetFileNameWithoutExtension(last);
        if (parts[^1] == "__init__") parts.RemoveAt(parts.Count - 1);
        if (parts.Any(p => !Regex.IsMatch(p, @"^[A-Za-z_]\w*$"))) return null;
        return string.Join(".", parts);
    }

    static string Parent(string dotted) => dotted.Contains('.') ? dotted[..dotted.LastIndexOf('.')] : "";
    static string Last(string dotted) => dotted.Contains('.') ? dotted[(dotted.LastIndexOf('.') + 1)..] : dotted;

    /// <summary>The edits of one module: its import statements, the dotted uses of `import a.b`, and strings that name a moved module.</summary>
    List<Edit> ImportEditsFor(ModuleInfo x, Func<string, string?> map, string newName, bool isPackage, List<string> warnings)
    {
        var edits = new List<Edit>();
        string src = x.Source;
        string newPackage = isPackage ? newName : Parent(newName);
        string nl = NewLine(x);

        foreach (var node in AstChildren.Descendants(x.Ast))
        {
            switch (node)
            {
                case ImportStmt imp:
                    foreach (var alias in imp.Names)
                    {
                        if (map(alias.Name) is not { } to || to == alias.Name) continue;
                        if (to.Length == 0) { warnings.Add($"`import {alias.Name}` in {x.Name} cannot follow the move."); continue; }
                        int at = src.IndexOf(alias.Name, alias.Start, StringComparison.Ordinal);
                        if (at < 0 || at > alias.End) continue;
                        // `import pkg` binds pkg: written `import lib.pkg as pkg` so that what uses pkg keeps working
                        bool keepName = alias.AsName == null && !alias.Name.Contains('.') && to != alias.Name;
                        edits.Add(new Edit(at, at + alias.Name.Length, keepName ? $"{to} as {alias.Name}" : to));
                        // `import pkg.b` binds pkg: what reads pkg.b.something reads it by the new name
                        if (alias.AsName == null && alias.Name.Contains('.')) edits.AddRange(DottedUses(x, alias.Name, to));
                    }
                    break;
                case ImportFromStmt { IsStar: true } star:
                    edits.AddRange(StarImportEdits(x, star, map, newPackage, nl));
                    break;
                case ImportFromStmt from:
                    edits.AddRange(FromImportEdits(x, from, map, newPackage, nl));
                    break;
                case ConstExpr { Kind: ConstKind.Str } text when FixtureModelString(text) is { } value && value.Contains('.') && Regex.IsMatch(value, @"^[A-Za-z_][\w.]*$"):
                {
                    // the longest leading part that is a moved module: "pkg.b.func" in mock.patch
                    var parts = value.Split('.');
                    for (int k = parts.Length; k >= 1; k--)
                    {
                        string head = string.Join(".", parts.Take(k));
                        if (map(head) is not { } to || to == head || to.Length == 0) continue;
                        string replaced = to + value[head.Length..];
                        int q = src.IndexOf(value, text.Start, StringComparison.Ordinal);
                        if (q >= 0 && q + value.Length <= text.End) edits.Add(new Edit(q, q + value.Length, replaced));
                        break;
                    }
                    break;
                }
            }
        }
        // two edits of the same text (an import listed twice) cannot both apply
        return edits.GroupBy(e => (e.Start, e.End)).Select(g => g.First()).OrderBy(e => e.Start).ToList();
    }

    static string? FixtureModelString(ConstExpr c) => PyMagic.Server.Pytest.FixtureModel.StringValue(c);

    /// <summary>
    /// `from pkg import *` and code that uses a submodule it brings (`b.VALUE`, the package listing `b` in `__all__` or importing it): when the submodule is
    /// renamed in the package, the uses take the new name, which the star now brings; when it leaves the package, it is imported by name after the star.
    /// The star of a module that itself moves is followed by FromImportEdits' rules for the module part.
    /// </summary>
    IEnumerable<Edit> StarImportEdits(ModuleInfo x, ImportFromStmt star, Func<string, string?> map, string newPackage, string nl)
    {
        foreach (var e in FromImportEdits(x, star, map, newPackage, nl)) yield return e;
        string? package = Workspace.AbsoluteName(x, star.Level, star.Module);
        if (package == null) yield break;
        string newPackageName = map(package) ?? package;
        // the names the code reads that it does not bind itself, so that they can only come from a star
        var uses = AstChildren.Descendants(x.Ast).OfType<NameExpr>().Where(n => x.Semantics.Resolve(n) is null || x.Semantics.Resolve(n)!.Declarations.All(d => d.Kind == DeclKind.Import && d.Node is ImportFromStmt { IsStar: true })).ToList();
        var added = new List<string>();
        foreach (var name in uses.Select(n => n.Name).Distinct())
        {
            string full = package.Length == 0 ? name : package + "." + name;
            if (map(full) is not { Length: > 0 } to || to == full) continue;
            string expected = newPackageName.Length == 0 ? name : newPackageName + "." + name;
            if (to == expected) continue;   // the package moved with it: the star still brings it by that name
            string last = Last(to);
            if (Parent(to) == newPackageName)
            {
                // renamed in the package: the star brings it by its new name
                foreach (var n in uses.Where(n => n.Name == name)) yield return new Edit(n.Start, n.End, last);
                continue;
            }
            added.Add(Parent(to).Length == 0 ? (last == name ? $"import {last}" : $"import {last} as {name}") : $"from {Parent(to)} import {(last == name ? last : $"{last} as {name}")}");
        }
        if (added.Count > 0)
        {
            string indent = LineIndent(x.Source, star.Start);
            yield return new Edit(star.End, star.End, string.Concat(added.Select(a => nl + indent + a)));
        }
    }

    /// <summary>`pkg.b.f()` after `import pkg.b`: the chain `pkg.b` becomes the new dotted name.</summary>
    static IEnumerable<Edit> DottedUses(ModuleInfo x, string oldName, string newName)
    {
        string root = oldName.Split('.')[0];
        foreach (var attr in AstChildren.Descendants(x.Ast).OfType<AttributeExpr>())
        {
            if (DottedText(attr) != oldName || x.ParentOf(attr) is ImportAlias) continue;
            if (Root(attr) is not NameExpr { Name: var n } || n != root) continue;
            if (x.Source.Substring(attr.Start, attr.End - attr.Start) != oldName) continue;   // spaces or a line break inside: left alone
            yield return new Edit(attr.Start, attr.End, newName);
        }
    }

    static string? DottedText(Expr e) => e switch
    {
        NameExpr n => n.Name,
        AttributeExpr a when DottedText(a.Value) is { } inner => inner + "." + a.Attr,
        _ => null,
    };

    static Expr Root(Expr e) => e is AttributeExpr a ? Root(a.Value) : e;

    /// <summary>
    /// `from <module> import a, b`: the module part follows a moved module (written absolute unless the same relative import still reaches it), the moved
    /// module's own relative imports stay pointed at what they meant, and a module imported by name from its package (`from pkg import b`) goes to its new package
    /// in a statement of its own, keeping the name it is used by (`from other import c as b`).
    /// </summary>
    IEnumerable<Edit> FromImportEdits(ModuleInfo x, ImportFromStmt from, Func<string, string?> map, string newPackage, string nl)
    {
        string? target = Workspace.AbsoluteName(x, from.Level, from.Module);
        if (target == null) yield break;
        string newTarget = map(target) ?? target;
        // how a module is written in this statement: as it was when the same relative import still reaches it from where this file will be, else relative
        // with as many dots when it is that package or below it, else absolute
        string Spell(string module)
        {
            if (from.Level == 0) return module;
            if (Workspace.AbsoluteName(newPackage, from.Level, from.Module) == module) return new string('.', from.Level) + (from.Module ?? "");
            if (Workspace.AbsoluteName(newPackage, from.Level, null) is { } package)
            {
                if (module == package) return new string('.', from.Level);
                if (package.Length == 0 ? module.Length > 0 : module.StartsWith(package + ".", StringComparison.Ordinal))
                    return new string('.', from.Level) + (package.Length == 0 ? module : module[(package.Length + 1)..]);
            }
            return module;
        }
        string modulePart = Spell(newTarget);
        bool moduleChanged = modulePart != new string('.', from.Level) + (from.Module ?? "");

        // names that are modules which moved somewhere else than under the new module part
        var keep = new List<ImportAlias>();
        var apart = new List<string>();
        foreach (var alias in from.Names)
        {
            string full = target.Length == 0 ? alias.Name : target + "." + alias.Name;
            string expected = newTarget.Length == 0 ? alias.Name : newTarget + "." + alias.Name;
            if (from.IsStar || map(full) is not { } to || to == expected) { keep.Add(alias); continue; }
            string bound = alias.AsName ?? alias.Name;
            string parent = Parent(to), last = Last(to);
            string name = last == bound ? last : $"{last} as {bound}";
            string spelled = parent.Length == 0 ? "" : Spell(parent);
            apart.Add(spelled.Length == 0 ? (last == bound ? $"import {last}" : $"import {last} as {bound}") : $"from {spelled} import {name}");
        }
        if (apart.Count == 0 && !moduleChanged) yield break;
        if (modulePart.Length == 0) modulePart = ".";   // `from  import x` cannot be written; a package of the root has no name

        string src = x.Source;
        if (apart.Count == 0)
        {
            // only the module part: the names, their parentheses and comments stay as written
            // the line may go on with a backslash: `from pkg.b \` then `import VALUE`
            var m = Regex.Match(src[from.Start..from.End], @"^from(?:\s|\\\r?\n)+(?<mod>\.*[\w.]*)(?:\s|\\\r?\n)+import\b");
            if (!m.Success) yield break;
            int s = from.Start + m.Groups["mod"].Index;
            yield return new Edit(s, s + m.Groups["mod"].Length, modulePart);
            yield break;
        }
        string indent = LineIndent(src, from.Start);
        var lines = new List<string>();
        if (keep.Count > 0) lines.Add($"from {modulePart} import {string.Join(", ", keep.Select(AliasText))}");
        lines.AddRange(apart);
        yield return new Edit(from.Start, from.End, string.Join(nl + indent, lines));
    }
}
