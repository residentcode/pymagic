using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Find usages and rename, across every module of the project.</summary>
public sealed partial class LanguageService
{
    /// <summary>What the cursor denotes: the symbols (and declaration nodes) every usage must resolve to.</summary>
    sealed class Target
    {
        public required string Name { get; init; }
        public List<Symbol> Symbols { get; } = new();
        /// <summary>Declaration nodes and `self.x` sites that count as this thing (compared by identity).</summary>
        public HashSet<Node> Nodes { get; } = new(ReferenceEqualityComparer.Instance);
        public required ModuleInfo Module { get; init; }
        public required (int Start, int End) Token { get; init; }
    }

    sealed record Site(ModuleInfo Module, int Start, int End, bool IsDeclaration);

    // ------------------------------------------------------------ public API

    public IReadOnlyList<Location> References(string uri, Position pos, bool includeDeclaration)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<Location>();
        var target = FindTarget(snap, m, Lines(m).OffsetOf(pos));
        if (target == null) return Array.Empty<Location>();

        return Usages(snap, target, forRename: false)
            .Where(s => includeDeclaration || !s.IsDeclaration)
            .Select(ToLocation).Where(l => l != null).Select(l => l!)
            .OrderBy(l => l.Uri, StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line).ThenBy(l => l.Range.Start.Character)
            .ToList();
    }

    public PrepareRenameResult? PrepareRename(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return null;
        var target = FindTarget(snap, m, Lines(m).OffsetOf(pos));
        if (target == null) return null;
        EnsureRenameable(snap, target);
        return new PrepareRenameResult(Lines(m).RangeOf(target.Token.Start, target.Token.End), target.Name);
    }

    /// <exception cref="ServiceException">The thing cannot be renamed, or the new name is not valid.</exception>
    public WorkspaceEdit Rename(string uri, Position pos, string newName)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        var target = FindTarget(snap, m, Lines(m).OffsetOf(pos)) ?? throw new ServiceException("nothing to rename here");

        if (!Regex.IsMatch(newName, @"^[\p{L}_][\p{L}\p{N}_]*$") || Lexer.Keywords.Contains(newName))
            throw new ServiceException($"'{newName}' is not a valid identifier");
        EnsureRenameable(snap, target);
        foreach (var s in target.Symbols)
            if (newName != target.Name && s.Scope.Symbols.TryGetValue(newName, out var clash) && (clash.Redirect ?? clash).Declarations.Count > 0)
                throw new ServiceException($"'{newName}' is already defined in this scope");

        var changes = new Dictionary<string, List<TextEdit>>();
        foreach (var site in Usages(snap, target, forRename: true))
        {
            var loc = ToLocation(site) ?? throw new ServiceException("a usage lies in a file that cannot be edited");
            if (!changes.TryGetValue(loc.Uri, out var list)) changes[loc.Uri] = list = new List<TextEdit>();
            list.Add(new TextEdit(loc.Range, newName));
        }
        return new WorkspaceEdit(changes);
    }

    void EnsureRenameable(Snapshot snap, Target target)
    {
        foreach (var s in target.Symbols)
        {
            var owner = snap.Workspace.ModuleOf(s);
            if (owner == null || !snap.Project.Contains(owner))
                throw new ServiceException($"'{target.Name}' is defined in a library and cannot be renamed");
        }
        if (target.Symbols.Count == 0 && target.Nodes.Count == 0)
            throw new ServiceException($"'{target.Name}' cannot be renamed");
        foreach (var n in target.Nodes)
            if (OwnerModule(n, snap.Project) == null)
                throw new ServiceException($"'{target.Name}' is defined in a library and cannot be renamed");
    }

    Location? ToLocation(Site s)
    {
        var uri = UriOfModule(s.Module);
        return uri == null ? null : new Location(uri, Lines(s.Module).RangeOf(s.Start, s.End));
    }

    // ------------------------------------------------------------ what is under the cursor

    static IEnumerable<Scope> AllScopes(Scope s)
    {
        yield return s;
        foreach (var c in s.Children)
            foreach (var x in AllScopes(c)) yield return x;
    }

    static Symbol Canon(Symbol s) => s.Redirect ?? s;

    Target? FindTarget(Snapshot snap, ModuleInfo m, int offset)
    {
        var ws = snap.Workspace;
        var ev = ws.Evaluator;

        // the name inside `from x import name`
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not ImportAlias alias || offset < alias.Start || offset > alias.Start + alias.Name.Length) continue;
            if (m.ParentOf(alias) is not ImportFromStmt from) break;
            var tm = ws.GetModule(Workspace.AbsoluteName(m, from.Level, from.Module) ?? "");
            var sym = tm == null ? null : ev.FindModuleSymbol(tm, alias.Name);
            if (sym == null) return null;
            var t = new Target { Name = alias.Name, Module = m, Token = (alias.Start, alias.Start + alias.Name.Length) };
            AddSymbol(t, Canon(sym));
            return t;
        }

        var at = NameLikeAt(m, offset);
        if (at is NameExpr ne)
        {
            var sym = m.Semantics.Resolve(ne);
            if (sym == null) return null;
            var t = new Target { Name = ne.Name, Module = m, Token = (ne.Start, ne.End) };
            AddSymbol(t, Canon(sym));
            return t;
        }
        if (at is AttributeExpr ae)
        {
            var res = ev.AttributeType(ev.TypeOf(m, ae.Value), ae.Attr);
            var t = new Target { Name = ae.Attr, Module = m, Token = AttrSpan(ae) };
            foreach (var d in res.Declarations) t.Nodes.Add(d.Node);
            foreach (var site in res.InstanceSites) t.Nodes.Add(site);
            if (t.Nodes.Count == 0) return null;
            AttachSymbols(snap, t);
            return t;
        }

        // a declaration that is not a reference: def/class names, parameters, except/match/type-parameter names
        foreach (var scope in AllScopes(m.Semantics.ModuleScope))
            foreach (var sym in scope.Symbols.Values)
            {
                if (sym.Redirect != null) continue;
                foreach (var d in sym.Declarations)
                {
                    var (s, e) = DeclSpan(m, d);
                    if (offset < s || offset > e) continue;
                    var t = new Target { Name = sym.Name, Module = m, Token = (s, e) };
                    AddSymbol(t, sym);
                    return t;
                }
            }
        return null;
    }

    static void AddSymbol(Target t, Symbol s)
    {
        if (!t.Symbols.Contains(s)) t.Symbols.Add(s);
        foreach (var d in s.Declarations) t.Nodes.Add(d.Node);
    }

    /// <summary>Links declaration nodes found through attribute lookup back to the class-scope symbols that own them.</summary>
    static void AttachSymbols(Snapshot snap, Target t)
    {
        foreach (var m in snap.Project)
            foreach (var scope in AllScopes(m.Semantics.ModuleScope))
                if (scope.Symbols.TryGetValue(t.Name, out var sym) && sym.Redirect == null
                    && sym.Declarations.Any(d => t.Nodes.Contains(d.Node)))
                    AddSymbol(t, sym);
    }

    // ------------------------------------------------------------ declaration spans

    static (int Start, int End) AttrSpanOf(AttributeExpr a) => (a.End - a.Attr.Length, a.End);

    /// <summary>The identifier a declaration introduces.</summary>
    static (int Start, int End) DeclSpan(ModuleInfo m, Declaration d)
    {
        switch (d.Node)
        {
            case FunctionDef f: return (f.NameStart, f.NameStart + f.Name.Length);
            case ClassDef c: return (c.NameStart, c.NameStart + c.Name.Length);
            case TypeAliasStmt t: return (t.NameStart, t.NameStart + t.Name.Length);
            case NameExpr n: return (n.Start, n.End);
            case ImportAlias a:
                return WordSpan(m.Source, a.Start, a.End, d.Name, last: a.AsName != null);
            case ExceptHandler h:
                return WordSpan(m.Source, h.Type?.End ?? h.Start, h.End, d.Name, last: false);
            case MatchAs ma:
                return WordSpan(m.Source, ma.Pattern.End, ma.End, d.Name, last: false);
            default:
                return WordSpan(m.Source, d.Node.Start, d.Node.End, d.Name, last: false);
        }
    }

    static (int Start, int End) WordSpan(string text, int from, int to, string word, bool last)
    {
        to = Math.Min(to, text.Length);
        int found = -1;
        for (int i = Math.Max(from, 0); i + word.Length <= to; i++)
        {
            i = text.IndexOf(word, i, to - i, StringComparison.Ordinal);
            if (i < 0) break;
            bool startOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            bool endOk = i + word.Length >= text.Length || !(char.IsLetterOrDigit(text[i + word.Length]) || text[i + word.Length] == '_');
            if (startOk && endOk)
            {
                found = i;
                if (!last) break;
            }
        }
        return found < 0 ? (from, Math.Max(from, Math.Min(to, from + word.Length))) : (found, found + word.Length);
    }

    // ------------------------------------------------------------ import hops

    /// <summary>For `from m import name [as alias]` bound to exactly this symbol: where it comes from.</summary>
    (Symbol Origin, ModuleInfo? OriginModule, ImportAlias Alias)? ImportHop(Snapshot snap, ModuleInfo x, Symbol t)
    {
        if (t.Declarations.Count != 1 || t.Declarations[0].Kind != DeclKind.Import || t.Declarations[0].Node is not ImportAlias alias) return null;
        if (x.ParentOf(alias) is not ImportFromStmt from || from.IsStar) return null;
        var tm = snap.Workspace.GetModule(Workspace.AbsoluteName(x, from.Level, from.Module) ?? "");
        if (tm == null) return null;
        var s = snap.Workspace.Evaluator.FindModuleSymbol(tm, alias.Name);
        if (s == null) return null;
        s = Canon(s);
        return (s, snap.Workspace.ModuleOf(s), alias);
    }

    bool ReachesTarget(Snapshot snap, ModuleInfo x, Symbol t, Target target)
    {
        var cur = t;
        var curModule = x;
        for (int hops = 0; hops < 12; hops++)
        {
            if (target.Symbols.Contains(cur)) return true;
            var hop = ImportHop(snap, curModule, cur);
            if (hop == null || hop.Value.OriginModule == null) return false;
            cur = hop.Value.Origin;
            curModule = hop.Value.OriginModule;
        }
        return false;
    }

    // ------------------------------------------------------------ usages

    List<Site> Usages(Snapshot snap, Target target, bool forRename)
    {
        var ev = snap.Workspace.Evaluator;
        var sites = new List<Site>();
        var seen = new HashSet<(ModuleInfo, int)>();
        void Add(ModuleInfo m, (int Start, int End) span, bool decl)
        {
            if (seen.Add((m, span.Start))) sites.Add(new Site(m, span.Start, span.End, decl));
        }

        bool memberLike = target.Nodes.OfType<AttributeExpr>().Any()
            || target.Symbols.Any(s => s.Scope.Kind is ScopeKind.Class or ScopeKind.Module);

        foreach (var m in snap.Project)
        {
            // plain names (and their imports) resolved through the scope tables
            foreach (var scope in AllScopes(m.Semantics.ModuleScope))
                foreach (var sym in scope.Symbols.Values)
                {
                    if (sym.Redirect != null || sym.Name != target.Name && !IsImportAliasFor(sym, target)) continue;
                    if (!ReachesTarget(snap, m, sym, target)) continue;

                    bool isTarget = target.Symbols.Contains(sym);
                    bool renamedByAs = !isTarget && forRename && sym.Declarations.Count == 1
                        && sym.Declarations[0].Node is ImportAlias { AsName: not null };

                    foreach (var d in sym.Declarations)
                    {
                        if (isTarget) Add(m, DeclSpan(m, d), true);
                        else if (d.Node is ImportAlias ia) Add(m, (ia.Start, ia.Start + ia.Name.Length), false);
                    }
                    if (renamedByAs) continue;   // uses of `as other` keep their own name
                    foreach (var r in sym.References) Add(m, (r.Node.Start, r.Node.End), false);
                }

            // attribute accesses: module.name, Class.member, obj.member
            if (!memberLike) continue;
            foreach (var n in AstChildren.Descendants(m.Ast))
            {
                if (n is not AttributeExpr a || a.Attr != target.Name) continue;
                var res = ev.AttributeType(ev.TypeOf(m, a.Value), a.Attr);
                if (res.Declarations.Any(d => target.Nodes.Contains(d.Node)) || res.InstanceSites.Any(target.Nodes.Contains))
                    Add(m, AttrSpanOf(a), false);
            }
        }

        // declaration sites reached through attribute lookup (class members, `self.x = ...`)
        foreach (var n in target.Nodes)
        {
            var owner = OwnerModule(n, snap.Project);
            if (owner == null) continue;
            if (n is AttributeExpr a) Add(owner, AttrSpanOf(a), true);
            else
            {
                var sym = target.Symbols.FirstOrDefault(s => s.Declarations.Any(d => ReferenceEquals(d.Node, n)));
                var decl = sym?.Declarations.First(d => ReferenceEquals(d.Node, n));
                Add(owner, decl != null ? DeclSpan(owner, decl) : DeclSpan(owner, new Declaration(target.Name, DeclKind.Variable, n)), true);
            }
        }
        return sites;
    }

    /// <summary>An import binding under a different name (`as other`) can still lead to the target.</summary>
    static bool IsImportAliasFor(Symbol sym, Target target) =>
        sym.Declarations.Count == 1 && sym.Declarations[0].Node is ImportAlias { AsName: not null } a && a.Name == target.Name;
}
