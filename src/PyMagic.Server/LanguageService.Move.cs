using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>A file a top-level function or class can be moved to.</summary>
public sealed record MoveTarget(string Uri, string Module);

public sealed partial class LanguageService
{
    // ------------------------------------------------------------ move a top-level function or class to another file

    /// <summary>The top-level function or class whose name is under the offset.</summary>
    static Stmt? TopLevelDefAt(ModuleInfo m, int offset)
    {
        foreach (var s in m.Ast.Body)
        {
            if (s is FunctionDef f && offset >= f.NameStart && offset <= f.NameStart + f.Name.Length) return f;
            if (s is ClassDef c && offset >= c.NameStart && offset <= c.NameStart + c.Name.Length) return c;
        }
        return null;
    }

    static string DefName(Stmt s) => s is FunctionDef f ? f.Name : ((ClassDef)s).Name;
    static int DefNameStart(Stmt s) => s is FunctionDef f ? f.NameStart : ((ClassDef)s).NameStart;

    public bool CanMove(string uri, Position pos)
    {
        var snap = Full();
        return snap.ByUri.TryGetValue(uri, out var m) && m.SyntaxErrors.Count == 0 && TopLevelDefAt(m, Lines(m).OffsetOf(pos)) != null;
    }

    /// <summary>The project files (other than this one) that the definition under the cursor can be moved to.</summary>
    public IReadOnlyList<MoveTarget> MoveTargets(string uri)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<MoveTarget>();
        return snap.Project.Where(x => !ReferenceEquals(x, m) && !x.IsStub && snap.UriOf.ContainsKey(x))
            .Select(x => new MoveTarget(snap.UriOf[x], x.Name)).OrderBy(x => x.Module, StringComparer.Ordinal).ToList();
    }

    public WorkspaceEdit MoveSymbol(string uri, Position pos, string targetUri)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        if (!snap.ByUri.TryGetValue(targetUri, out var target)) throw new ServiceException("The target file is not part of the project.");
        if (m.SyntaxErrors.Count > 0) throw new ServiceException("Fix the syntax errors in this file first.");
        var edits = Move(snap, m, Lines(m).OffsetOf(pos), target);

        var changes = new Dictionary<string, List<TextEdit>>();
        foreach (var (module, list) in edits)
        {
            string u = UriOfModule(module) ?? throw new ServiceException("a file that has to change cannot be edited");
            var ml = Lines(module);
            changes[u] = list.OrderBy(e => e.Start).ThenBy(e => e.End).Select(e => new TextEdit(ml.RangeOf(e.Start, e.End), e.Text)).ToList();
        }
        return new WorkspaceEdit(changes);
    }

    static readonly string[] ModuleBoundNames = { "__file__", "__name__", "__package__", "__spec__", "__path__", "__loader__", "__builtins__", "__doc__" };

    Dictionary<ModuleInfo, List<Edit>> Move(Snapshot snap, ModuleInfo a, int offset, ModuleInfo b)
    {
        var def = TopLevelDefAt(a, offset) ?? throw new ServiceException("Put the cursor on the name of a top-level function or class.");
        string name = DefName(def);
        if (ReferenceEquals(a, b)) throw new ServiceException("Choose another file.");
        if (b.SyntaxErrors.Count > 0) throw new ServiceException($"Fix the syntax errors in {b.Name} first.");
        if (b.IsStub) throw new ServiceException("The target must be a source file of the project.");

        var sym = a.Semantics.ModuleScope.Symbols.GetValueOrDefault(name) is { } s0 ? Canon(s0) : null;
        if (sym == null || sym.Declarations.Count != 1) throw new ServiceException($"'{name}' is defined more than once in this file.");
        if (b.Semantics.ModuleScope.Symbols.ContainsKey(name)) throw new ServiceException($"'{name}' already exists in {b.Name}.");
        int defEnd = TrueEnd(a.Source, def);

        var inside = AstChildren.Descendants(def).ToList();
        if (inside.OfType<GlobalStmt>().Any() || inside.OfType<NonlocalStmt>().Any())
            throw new ServiceException($"'{name}' uses `global`, so it depends on this file's variables.");
        if (inside.OfType<NameExpr>().Any(n => ModuleBoundNames.Contains(n.Name) && a.Semantics.Resolve(n) == null))
            throw new ServiceException($"'{name}' uses a module attribute such as __file__ or __name__, which would change.");

        // ---- what the moved code needs: names bound at the top level of this file
        var needed = new List<Symbol>();
        void Need(Symbol? s)
        {
            if (s == null) return;
            s = Canon(s);
            if (ReferenceEquals(s, sym) || s.Scope.Kind != ScopeKind.Module || !ReferenceEquals(snap.Workspace.ModuleOf(s), a)) return;
            if (!needed.Contains(s)) needed.Add(s);
        }
        foreach (var n in inside.OfType<NameExpr>()) Need(a.Semantics.Resolve(n));
        foreach (var c in inside.OfType<ConstExpr>().Where(c => c.IsFString))
            foreach (Match w in Regex.Matches(c.Text, @"(?<![\w.])[\p{L}_][\p{L}\p{N}_]*"))
                if (a.Semantics.ModuleScope.Symbols.TryGetValue(w.Value, out var s)) Need(s);

        string nl = NewLine(b);
        var importLines = new List<string>();
        bool dependsOnA = false;
        var removable = new List<(Stmt Stmt, ImportAlias Alias)>();
        foreach (var dep in needed)
        {
            if (dep.Declarations.All(d => d.Node is ImportAlias))
            {
                foreach (var d in dep.Declarations)
                {
                    var alias = (ImportAlias)d.Node;
                    var stmt = a.ParentOf(alias) as Stmt;
                    string line;
                    switch (stmt)
                    {
                        case ImportFromStmt { IsStar: true }:
                            throw new ServiceException($"'{dep.Name}' comes from a star import; import it explicitly first.");
                        case ImportFromStmt f:
                            string? abs = Workspace.AbsoluteName(a, f.Level, f.Module);
                            if (string.IsNullOrEmpty(abs)) throw new ServiceException($"The import of '{dep.Name}' cannot be written for {b.Name}.");
                            line = $"from {abs} import {AliasText(alias)}";
                            break;
                        case ImportStmt:
                            line = $"import {AliasText(alias)}";
                            break;
                        default:
                            throw new ServiceException($"'{dep.Name}' is imported in a way that cannot be moved.");
                    }
                    if (stmt is ImportFromStmt fromB && alias.AsName == null && Workspace.AbsoluteName(a, fromB.Level, fromB.Module) == b.Name
                        && b.Semantics.ModuleScope.Symbols.ContainsKey(alias.Name))
                    {
                        if (stmt != null) removable.Add((stmt, alias));
                        continue;   // the target file defines it itself
                    }
                    if (b.Semantics.ModuleScope.Symbols.TryGetValue(dep.Name, out var existing))
                    {
                        if (SameImport(b, existing, alias, a, stmt)) continue;
                        throw new ServiceException($"'{dep.Name}' means something else in {b.Name}.");
                    }
                    if (!importLines.Contains(line)) importLines.Add(line);
                    if (stmt != null) removable.Add((stmt, alias));
                }
            }
            else
            {
                dependsOnA = true;
                if (b.Semantics.ModuleScope.Symbols.TryGetValue(dep.Name, out var existing))
                {
                    if (existing.Declarations.Count == 1 && existing.Declarations[0].Node is ImportAlias { AsName: null } ia
                        && b.ParentOf(ia) is ImportFromStmt ef && Workspace.AbsoluteName(b, ef.Level, ef.Module) == a.Name && ia.Name == dep.Name) continue;
                    throw new ServiceException($"'{dep.Name}' means something else in {b.Name}.");
                }
                string line = $"from {a.Name} import {dep.Name}";
                if (!importLines.Contains(line)) importLines.Add(line);
            }
        }

        // ---- every place that mentions the name
        var findTarget = FindTarget(snap, a, DefNameStart(def)) ?? throw new ServiceException("The name cannot be analysed.");
        var sites = Usages(snap, findTarget, forRename: false)
            .Where(s => !(ReferenceEquals(s.Module, a) && s.Start >= def.Start && s.End <= defEnd)).ToList();

        foreach (var module in snap.Project)
        {
            if (module.Ast.Body.OfType<ImportFromStmt>().Any(f => f.IsStar && Workspace.AbsoluteName(module, f.Level, f.Module) == a.Name && !ReferenceEquals(module, a)))
                throw new ServiceException($"{module.Name} imports everything from {a.Name} (`import *`); import the names explicitly first.");
            if (MentionedInFStrings(module, name, c => ReferenceEquals(module, a) && c.Start >= def.Start && c.End <= defEnd))
                throw new ServiceException($"'{name}' appears inside an f-string in {module.Name}, which cannot be updated yet.");
            if (AstChildren.Descendants(module.Ast).OfType<ConstExpr>().Any(c => c.Kind == ConstKind.Str && Regex.IsMatch(c.Text, @"^[rRbBuU]*['""]" + Regex.Escape(name) + @"['""]$")))
                throw new ServiceException($"'{name}' is mentioned in a string in {module.Name} (for example in __all__).");
        }

        var result = new Dictionary<ModuleInfo, List<Edit>>(ReferenceEqualityComparer.Instance);
        List<Edit> For(ModuleInfo module)
        {
            if (!result.TryGetValue(module, out var l)) result[module] = l = new List<Edit>();
            return l;
        }

        bool usedInA = false;
        var importingModules = new List<ModuleInfo>();
        var rewritten = new HashSet<Stmt>(ReferenceEqualityComparer.Instance);
        foreach (var site in sites)
        {
            var x = site.Module;
            var alias = AstChildren.Descendants(x.Ast).OfType<ImportAlias>().FirstOrDefault(i => i.Start == site.Start && x.ParentOf(i) is ImportFromStmt);
            if (alias != null)
            {
                var from = (ImportFromStmt)x.ParentOf(alias)!;
                if (Workspace.AbsoluteName(x, from.Level, from.Module) != a.Name || alias.Name != name) continue;   // a re-export elsewhere keeps working
                if (!rewritten.Add(from)) continue;
                string indent = LineIndent(x.Source, from.Start);
                string xnl = NewLine(x);
                var rest = from.Names.Where(n => !ReferenceEquals(n, alias)).ToList();
                var moved = $"from {b.Name} import {AliasText(alias)}";
                string text = rest.Count == 0
                    ? moved
                    : $"from {new string('.', from.Level)}{from.Module} import {string.Join(", ", rest.Select(AliasText))}{xnl}{indent}{moved}";
                For(x).Add(new Edit(from.Start, from.End, text));
                if (!ReferenceEquals(x, b)) importingModules.Add(x);
                continue;
            }
            int before = site.Start - 1;
            while (before >= 0 && char.IsWhiteSpace(x.Source[before])) before--;
            if (before >= 0 && x.Source[before] == '.')
                throw new ServiceException($"'{name}' is used as a module attribute in {x.Name}; import it by name there first.");
            if (ReferenceEquals(x, a)) usedInA = true;   // in other files the name is bound by an import, which is rewritten above
        }

        // ---- circular imports
        var newEdges = new List<(string From, string To)>();
        if (usedInA) newEdges.Add((a.Name, b.Name));
        if (dependsOnA) newEdges.Add((b.Name, a.Name));
        foreach (var l in importLines)
        {
            var mm = Regex.Match(l, @"^from (\S+) import");
            if (mm.Success) newEdges.Add((b.Name, mm.Groups[1].Value));
        }
        foreach (var x in importingModules) newEdges.Add((x.Name, b.Name));
        CheckNoCycle(snap, newEdges);

        // ---- the moved code, in the target file
        string code = a.Source[def.Start..defEnd];
        if (nl != NewLine(a)) code = code.Replace("\r\n", "\n").Replace("\n", nl);
        var bEdits = For(b);
        if (importLines.Count > 0)
        {
            var (at, text) = ModuleConstantPlace(b, string.Join(nl, importLines), nl);
            bEdits.Add(new Edit(at, at, text));
        }
        string bs = b.Source;
        if (bs.Trim().Length == 0) bEdits.Add(new Edit(0, bs.Length, code + nl));
        else bEdits.Add(new Edit(bs.Length, bs.Length, (bs.EndsWith('\n') ? "" : nl) + nl + nl + code + nl));

        // ---- this file: the code goes, the names it alone used go, and what still uses it imports it
        var aEdits = For(a);
        aEdits.Add(DeleteDefinition(a, def, defEnd));
        if (usedInA)
        {
            var (at, text) = ModuleConstantPlace(a, $"from {b.Name} import {name}", NewLine(a));
            aEdits.Add(new Edit(at, at, text));
        }
        if (!a.IsPackage && !a.IsStub)
        {
            var strings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in AstChildren.Descendants(a.Ast))
                if (n is ConstExpr { Kind: ConstKind.Str } c)
                    foreach (Match w in Regex.Matches(c.Text, @"[\p{L}_][\p{L}\p{N}_]*")) strings.Add(w.Value);
            foreach (var group in removable.GroupBy(r => r.Stmt))
            {
                var drop = new List<ImportAlias>();
                foreach (var (stmt, alias) in group)
                {
                    var bound = alias.AsName ?? alias.Name.Split('.')[0];
                    var symbol = a.Semantics.ModuleScope.Symbols.GetValueOrDefault(bound);
                    if (symbol == null || strings.Contains(bound)) continue;
                    bool otherUse = symbol.References.Any(r => !(r.Node.Start >= def.Start && r.Node.End <= defEnd));
                    if (!otherUse && !MentionedInFStrings(a, bound, c => c.Start >= def.Start && c.End <= defEnd)) drop.Add(alias);
                }
                if (drop.Count > 0) aEdits.Add(RemoveAliases(a, group.Key, drop));
            }
        }
        return result;
    }

    static bool SameImport(ModuleInfo b, Symbol existing, ImportAlias alias, ModuleInfo a, Stmt? stmt)
    {
        if (existing.Declarations.Count != 1 || existing.Declarations[0].Node is not ImportAlias other) return false;
        if (other.Name != alias.Name || other.AsName != alias.AsName) return false;
        var bs = b.ParentOf(other);
        if (bs is ImportStmt && stmt is ImportStmt) return true;
        return bs is ImportFromStmt bf && stmt is ImportFromStmt af
            && Workspace.AbsoluteName(b, bf.Level, bf.Module) == Workspace.AbsoluteName(a, af.Level, af.Module);
    }

    /// <summary>Deletes the definition and the blank lines that would be left doubled up.</summary>
    static Edit DeleteDefinition(ModuleInfo m, Stmt def, int defEnd)
    {
        string text = m.Source;
        int ls = LineStart(text, def.Start);
        int le = LineEndInclusive(text, defEnd);
        bool BlankLine(int from, out int next)
        {
            int i = from;
            while (i < text.Length && text[i] is ' ' or '\t') i++;
            next = i;
            if (i < text.Length && text[i] == '\r') i++;
            if (i < text.Length && text[i] == '\n') { next = i + 1; return true; }
            return false;
        }
        int precedingBlank = 0;
        for (int p = ls; p > 0;)
        {
            int prevEnd = p - 1;
            if (prevEnd > 0 && text[prevEnd - 1] == '\r') prevEnd--;
            int prevStart = LineStart(text, prevEnd);
            if (text[prevStart..prevEnd].Trim().Length != 0) break;
            precedingBlank++; p = prevStart;
        }
        while (precedingBlank > 0 && BlankLine(le, out int next)) { le = next; precedingBlank--; if (precedingBlank == 0) break; }
        if (ls == 0) while (BlankLine(le, out int next)) le = next;   // nothing before it: do not leave leading blank lines
        return new Edit(ls, le, "");
    }

    /// <summary>Refuses when one of the new import edges would close a loop between project modules.</summary>
    void CheckNoCycle(Snapshot snap, List<(string From, string To)> newEdges)
    {
        var names = new HashSet<string>(snap.Project.Select(p => p.Name), StringComparer.Ordinal);
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var m in snap.Project)
        {
            var set = graph[m.Name] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in AstChildren.Descendants(m.Ast))
            {
                switch (n)
                {
                    case ImportStmt i:
                        foreach (var al in i.Names) if (names.Contains(al.Name)) set.Add(al.Name);
                        break;
                    case ImportFromStmt f:
                        string? abs = Workspace.AbsoluteName(m, f.Level, f.Module);
                        if (abs == null) break;
                        if (names.Contains(abs)) set.Add(abs);
                        foreach (var al in f.Names)
                            if (names.Contains(abs.Length == 0 ? al.Name : abs + "." + al.Name)) set.Add(abs.Length == 0 ? al.Name : abs + "." + al.Name);
                        break;
                }
            }
        }
        foreach (var (from, to) in newEdges)
        {
            if (!graph.TryGetValue(from, out var s)) graph[from] = s = new HashSet<string>(StringComparer.Ordinal);
            s.Add(to);
        }
        foreach (var (from, to) in newEdges)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { to };
            var queue = new Queue<string>();
            queue.Enqueue(to);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (cur == from) throw new ServiceException($"This would make {from} and {to} import each other (a circular import).");
                if (graph.TryGetValue(cur, out var next))
                    foreach (var nx in next) if (seen.Add(nx)) queue.Enqueue(nx);
            }
        }
    }
}
