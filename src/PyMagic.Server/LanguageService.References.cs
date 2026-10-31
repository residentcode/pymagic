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
        using var searching = Searching(uri, pos);
        if (DjangoReferences(uri, pos, includeDeclaration) is { Count: > 0 } djangoSites) return djangoSites;
        var snap = Full();
        if (snap.ByUri.TryGetValue(uri, out var fm))
        {
            int at = Lines(fm).OffsetOf(pos);
            if (FixtureTargetAt(snap, fm, at) is { Def: not null } fixture)
                return FixtureReferences(snap, fm, at, fixture, includeDeclaration, SymbolReferences(uri, pos, includeDeclaration));
        }
        return SymbolReferences(uri, pos, includeDeclaration);
    }

    IReadOnlyList<Location> SymbolReferences(string uri, Position pos, bool includeDeclaration)
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
        using var searching = Searching(uri, pos);
        if (FixturePrepareRename(uri, pos) is { } fixture) return fixture;
        if (UrlNameRenameTarget(uri, pos) is { } urlName) return urlName;
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return null;
        var target = FindTarget(snap, m, Lines(m).OffsetOf(pos));
        if (target == null) return null;
        EnsureRenameable(snap, target);
        // a method that overrides others: the page asks whether the base method goes with it, or warns that a library's cannot
        var range = Lines(m).RangeOf(target.Token.Start, target.Token.End);
        if (MethodOf(snap, target) is { } method)
        {
            string? baseClass = DeepestProjectSuper(snap, method.Module, method.Class, method.Def.Name)?.Class.Name;
            string? library = LibrarySuper(snap, method.Module, method.Class, method.Def.Name);
            if (baseClass != null || library != null) return new PrepareRenameResult(range, target.Name, method.Class.Name, baseClass, library);
        }
        return new PrepareRenameResult(range, target.Name);
    }

    /// <exception cref="ServiceException">The thing cannot be renamed, or the new name is not valid.</exception>
    /// <param name="renameBase">For a method that overrides one of the project's: rename the method at the top of the chain (and all that override it) instead.</param>
    public WorkspaceEdit Rename(string uri, Position pos, string newName, bool renameBase = false)
    {
        using var searching = Searching(uri, pos);
        if (DjangoRename(uri, pos, newName) is { } urlRename) return urlRename;
        var snap = Full();
        if (snap.ByUri.TryGetValue(uri, out var fm) && FixtureTargetAt(snap, fm, Lines(fm).OffsetOf(pos)) is { } fixture)
            return RenameFixture(snap, fixture, newName);
        return RenameSymbol(uri, pos, newName, renameBase);
    }

    /// <summary>Rename of the thing under the cursor as a name in the program (a fixture is handled by <see cref="RenameFixture"/>).</summary>
    WorkspaceEdit RenameSymbol(string uri, Position pos, string newName, bool renameBase = false)
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

        // a method takes the methods that override it along (all levels down), and with renameBase the method at the top of its chain
        var targets = new List<Target> { target };
        if (MethodOf(snap, target) is { } method)
        {
            var root = (method.Module, method.Class, Method: method.Def);
            if (renameBase && DeepestProjectSuper(snap, method.Module, method.Class, method.Def.Name) is { } top) root = top;
            var family = new List<(ModuleInfo Module, ClassDef Class, FunctionDef Method)> { root };
            family.AddRange(OverridingMethods(snap, root.Class, root.Method.Name));
            foreach (var (module, cls, def) in family)
            {
                if (newName != def.Name && module.Semantics.ScopeOf(cls) is { } scope && scope.Symbols.TryGetValue(newName, out var clash) && clash.Declarations.Count > 0)
                    throw new ServiceException($"'{newName}' is already defined in class {cls.Name}");
                if (ReferenceEquals(def, method.Def) || FindTarget(snap, module, def.NameStart) is not { } t) continue;
                EnsureRenameable(snap, t);
                targets.Add(t);
            }
        }

        var changes = new Dictionary<string, List<TextEdit>>();
        var done = new HashSet<(string, int, int)>();
        foreach (var t in targets)
            foreach (var site in Usages(snap, t, forRename: true))
            {
                var loc = ToLocation(site) ?? throw new ServiceException("a usage lies in a file that cannot be edited");
                if (!done.Add((loc.Uri, loc.Range.Start.Line, loc.Range.Start.Character))) continue;
                if (!changes.TryGetValue(loc.Uri, out var list)) changes[loc.Uri] = list = new List<TextEdit>();
                list.Add(new TextEdit(loc.Range, newName));
            }
        return new WorkspaceEdit(changes);
    }

    /// <summary>The project method a rename target stands for (its def or a use of it), when it is one that overrides or can be overridden.</summary>
    static (ModuleInfo Module, ClassDef Class, FunctionDef Def)? MethodOf(Snapshot snap, Target target)
    {
        var def = target.Nodes.OfType<FunctionDef>().LastOrDefault();
        if (def == null || !Overridable(def.Name) || OwnerModule(def, snap.Project) is not { } module || ClassOfMethod(module, def) is not { } cls) return null;
        return MethodIn(module, cls, def.Name) is { } bound && ReferenceEquals(bound, def) ? (module, cls, def) : null;
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

            // `make` imported from another file is that file's `make`: start from where it is defined as well
            var current = Canon(sym);
            var currentModule = m;
            for (int hops = 0; hops < 12; hops++)
            {
                var hop = ImportHop(snap, currentModule, current);
                if (hop == null || hop.Value.OriginModule == null || hop.Value.Alias.AsName != null) break;
                AddSymbol(t, hop.Value.Origin);
                current = hop.Value.Origin;
                currentModule = hop.Value.OriginModule;
            }
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
        var sites = new List<Site>();
        var seen = new HashSet<(ModuleInfo, int)>();
        void Add(ModuleInfo m, (int Start, int End) span, bool decl)
        {
            if (seen.Add((m, span.Start))) sites.Add(new Site(m, span.Start, span.End, decl));
        }

        bool memberLike = target.Nodes.OfType<AttributeExpr>().Any()
            || target.Symbols.Any(s => s.Scope.Kind is ScopeKind.Class or ScopeKind.Module);

        // A big project is searched by several workers at once (each with an evaluator of its own, the parsed modules shared); a small one by this thread.
        int workers = memberLike && snap.Project.Count >= ParallelUsagesFrom ? Math.Clamp(System.Environment.ProcessorCount / 4, 2, 4) : 1;
        if (workers > 1 && UsagesInParallel(snap, target, forRename, memberLike, workers, Add)) { /* done */ }
        else
            foreach (var m in snap.Project)
                UsagesIn(snap, target, m, forRename, memberLike, (span, decl) => Add(m, span, decl));

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

    /// <summary>From this many modules on, the modules are searched for member usages by several workers (a smaller project is searched by one thread).</summary>
    public int ParallelUsagesFrom { get; set; } = 150;

    /// <summary>The usages of the target in one module.</summary>
    void UsagesIn(Snapshot snap, Target target, ModuleInfo m, bool forRename, bool memberLike, Action<(int Start, int End), bool> add)
    {
        var ev = snap.Workspace.Evaluator;
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
                    if (isTarget) add(DeclSpan(m, d), true);
                    else if (d.Node is ImportAlias ia) add((ia.Start, ia.Start + ia.Name.Length), false);
                }
                if (renamedByAs) continue;   // uses of `as other` keep their own name
                foreach (var r in sym.References) add((r.Node.Start, r.Node.End), false);
            }

        // attribute accesses: module.name, Class.member, obj.member
        if (!memberLike) return;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not AttributeExpr a || a.Attr != target.Name) continue;
            var res = ev.AttributeType(ev.TypeOf(m, a.Value), a.Attr);
            if (res.Declarations.Any(d => target.Nodes.Contains(d.Node)) || res.InstanceSites.Any(target.Nodes.Contains))
                add(AttrSpanOf(a), false);
        }
    }

    /// <summary>
    /// Searches the modules on several workers: this thread and copies of this service (each with an evaluator of its own and the same parsed modules, so the
    /// declarations the target holds are the very same objects) take the modules a few at a time from one queue, so a worker that meets slow files does not hold
    /// the others up. What each module gives is kept by its place in the project and handed on in that order, the order one thread gives (a module a copy
    /// does not have is done here at the end). False when a worker failed, before anything was handed on; the caller then does it all itself.
    /// </summary>
    bool UsagesInParallel(Snapshot snap, Target target, bool forRename, bool memberLike, int workers, Action<ModuleInfo, (int Start, int End), bool> add)
    {
        var modules = snap.Project;
        var uris = modules.Select(m => UriOfModule(m) ?? "").ToArray();
        var files = modules.Select(m => m.Path).Where(p => p != null).Select(p => p!).ToList();
        var found = new List<((int Start, int End) Span, bool Decl)>[modules.Count];
        int next = 0;
        const int Batch = 4;
        bool failed = false;

        // takes batches until the queue is empty; moduleAt gives this searcher's own module for a place in the project (null: it has none)
        void Drain(LanguageService svc, Snapshot s, Func<int, ModuleInfo?> moduleAt)
        {
            for (int from; !Volatile.Read(ref failed) && (from = Interlocked.Add(ref next, Batch) - Batch) < modules.Count;)
                for (int i = from; i < Math.Min(from + Batch, modules.Count); i++)
                    if (moduleAt(i) is { } m)
                    {
                        var list = new List<((int Start, int End) Span, bool Decl)>();
                        svc.UsagesIn(s, target, m, forRename, memberLike, (span, decl) => list.Add((span, decl)));
                        found[i] = list;
                    }
        }

        var tasks = Enumerable.Range(1, workers - 1).Select(_ => Task.Run(() =>
        {
            try
            {
                var worker = Fork(files);
                var wsnap = worker.Full();
                var byUri = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
                foreach (var wm in wsnap.Project) if (worker.UriOfModule(wm) is { } u) byUri[u] = wm;
                Drain(worker, wsnap, i => byUri.GetValueOrDefault(uris[i]));
            }
            catch { Volatile.Write(ref failed, true); throw; }
        })).ToArray();

        try { Drain(this, snap, i => modules[i]); }
        catch { Volatile.Write(ref failed, true); try { Task.WaitAll(tasks); } catch (AggregateException) { } throw; }
        try { Task.WaitAll(tasks); }
        catch (AggregateException) { return false; }
        for (int i = 0; i < modules.Count; i++)
        {
            if (found[i] == null) { UsagesIn(snap, target, modules[i], forRename, memberLike, (span, decl) => add(modules[i], span, decl)); continue; }
            foreach (var (span, decl) in found[i]) add(modules[i], span, decl);
        }
        return true;
    }

    /// <summary>An import binding under a different name (`as other`) can still lead to the target.</summary>
    static bool IsImportAliasFor(Symbol sym, Target target) =>
        sym.Declarations.Count == 1 && sym.Declarations[0].Node is ImportAlias { AsName: not null } a && a.Name == target.Name;
}
