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
            var edits = ImportEditsFor(x, Map, Map(x.Name) ?? x.Name, x.IsPackage, warnings);
            if (edits.Count == 0) continue;
            var lines = Lines(x);
            changes[xUri] = edits.OrderBy(e => e.Start).Select(e => new TextEdit(lines.RangeOf(e.Start, e.End), e.Text)).ToList();
        }
        string first = moved.OrderBy(m => m.Name.Length).First().Name;
        return new ModuleMovePlan(changes, warnings, first, Map(first));
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
            var m = Regex.Match(src[from.Start..from.End], @"^from\s+(?<mod>\.*[\w.]*)\s+import\b");
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
