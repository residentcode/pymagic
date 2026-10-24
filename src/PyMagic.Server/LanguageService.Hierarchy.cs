using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>A function, method or class as the hierarchy views show it. The selection range is its name; the position of it identifies the definition again.</summary>
public sealed record HierarchyItem(string Name, int Kind, string? Detail, string Uri, Range Range, Range SelectionRange);

public sealed record IncomingCall(HierarchyItem From, List<Range> FromRanges);

public sealed record OutgoingCall(HierarchyItem To, List<Range> FromRanges);

/// <summary>A gutter mark: this method overrides or implements one above it, or is overridden or implemented below; or this class has subclasses.</summary>
/// <param name="Kind">overrides, implements, overridden, implemented or subclassed.</param>
public sealed record InheritanceMarker(int Line, string Kind, string Label, List<Location> Targets);

/// <summary>Class hierarchies and call hierarchies: who derives from what, who overrides what, who calls whom.</summary>
public sealed partial class LanguageService
{
    const int KindModule = 2, KindClass = 5, KindMethod = 6, KindFunction = 12;

    /// <summary>The project's classes by the class they derive from, built once per snapshot.</summary>
    static Dictionary<ClassDef, List<(ModuleInfo Module, ClassDef Class)>> SubclassIndex(Snapshot snap)
    {
        if (snap.Subclasses != null) return snap.Subclasses;
        var index = new Dictionary<ClassDef, List<(ModuleInfo, ClassDef)>>(ReferenceEqualityComparer.Instance);
        var ev = snap.Workspace.Evaluator;
        foreach (var module in snap.Project)
        {
            if (module.IsStub) continue;
            foreach (var cd in AstChildren.Descendants(module.Ast).OfType<ClassDef>())
                foreach (var baseClass in DirectBases(snap, module, cd))
                {
                    if (!index.TryGetValue(baseClass.Class, out var list)) index[baseClass.Class] = list = new();
                    list.Add((module, cd));
                }
        }
        return snap.Subclasses = index;
    }

    /// <summary>The classes named in a class's header.</summary>
    static List<(ModuleInfo Module, ClassDef Class)> DirectBases(Snapshot snap, ModuleInfo module, ClassDef cd)
    {
        var result = new List<(ModuleInfo, ClassDef)>();
        var ev = snap.Workspace.Evaluator;
        foreach (var arg in cd.Bases)
        {
            if (arg.Name != null || arg.Stars != 0) continue;
            var expr = arg.Value is SubscriptExpr sub ? sub.Value : arg.Value;   // Base[int] is still Base
            if (ev.TypeOf(module, expr) is ClassType { IsInstance: false } bt && !ReferenceEquals(bt.Info.Node, cd))
                result.Add((bt.Info.Module, bt.Info.Node));
        }
        return result;
    }

    /// <summary>Every class that derives from this one, directly or not.</summary>
    static List<(ModuleInfo Module, ClassDef Class)> Descendants(Snapshot snap, ClassDef cd)
    {
        var index = SubclassIndex(snap);
        var result = new List<(ModuleInfo, ClassDef)>();
        var seen = new HashSet<ClassDef>(ReferenceEqualityComparer.Instance) { cd };
        var queue = new Queue<ClassDef>();
        queue.Enqueue(cd);
        while (queue.Count > 0)
            if (index.TryGetValue(queue.Dequeue(), out var children))
                foreach (var child in children)
                    if (seen.Add(child.Class)) { result.Add(child); queue.Enqueue(child.Class); }
        return result;
    }

    /// <summary>The classes this one derives from, nearest first (itself and object left out).</summary>
    static List<(ModuleInfo Module, ClassDef Class)> Ancestors(Snapshot snap, ModuleInfo module, ClassDef cd)
    {
        var result = new List<(ModuleInfo, ClassDef)>();
        var ev = snap.Workspace.Evaluator;
        if (ClassInfoOf(module, cd, ev) is not { } info) return result;
        foreach (var t in ev.MroOf(info).Skip(1))
            if (t is ClassType c && !ReferenceEquals(c.Info.Node, cd) && !(c.Info.Name == "object" && c.Info.Module.Name == "builtins"))
                result.Add((c.Info.Module, c.Info.Node));
        return result;
    }

    /// <summary>The def that a class body binds to this name, if any.</summary>
    static FunctionDef? MethodIn(ModuleInfo module, ClassDef cd, string name)
    {
        var scope = module.Semantics.ScopeOf(cd);
        if (scope == null || !scope.Symbols.TryGetValue(name, out var sym)) return null;
        for (int i = sym.Declarations.Count - 1; i >= 0; i--)
            if (sym.Declarations[i].Kind == DeclKind.Function && sym.Declarations[i].Node is FunctionDef f) return f;
        return null;
    }

    static ClassDef? ClassOfMethod(ModuleInfo module, FunctionDef f) =>
        module.Semantics.ScopeOf(f)?.Parent is { Kind: ScopeKind.Class, Node: ClassDef c } ? c : null;

    ModuleInfo? ModuleAtUri(Snapshot snap, string uri)
    {
        if (snap.ByUri.TryGetValue(uri, out var m)) return m;
        foreach (var candidate in snap.Project)
            if (snap.UriOf.TryGetValue(candidate, out var u) && string.Equals(u, uri, StringComparison.OrdinalIgnoreCase)) return candidate;
        return null;
    }

    // ------------------------------------------------------------ items

    HierarchyItem? ItemOf(ModuleInfo m, Node def)
    {
        var uri = UriOfModule(m);
        if (uri == null) return null;
        var lines = Lines(m);
        (string name, int nameStart, int kind, string? detail) = def switch
        {
            ClassDef c => (c.Name, c.NameStart, KindClass, (string?)m.Name),
            FunctionDef f when ClassOfMethod(m, f) is { } owner => (f.Name, f.NameStart, KindMethod, owner.Name),
            FunctionDef f => (f.Name, f.NameStart, KindFunction, m.Name),
            _ => ("", 0, 0, null),
        };
        if (kind == 0) return null;
        int end = Math.Max(def.End, def.Start);
        while (end > def.Start && end <= m.Source.Length && end > 0 && m.Source[end - 1] is '\n' or '\r') end--;
        return new HierarchyItem(name, kind, detail, uri, lines.RangeOf(def.Start, end), lines.RangeOf(nameStart, nameStart + name.Length));
    }

    HierarchyItem? ModuleItem(ModuleInfo m)
    {
        var uri = UriOfModule(m);
        if (uri == null) return null;
        var start = StartOf(m);
        return new HierarchyItem("<module>", KindModule, m.Name, uri, start, start);
    }

    Range StartOf(ModuleInfo m) => Lines(m).RangeOf(0, 0);

    /// <summary>The definition an item stands for.</summary>
    (ModuleInfo Module, Node Def)? DefOf(Snapshot snap, HierarchyItem item)
    {
        var m = ModuleAtUri(snap, item.Uri);
        if (m == null) return null;
        int offset = Lines(m).OffsetOf(item.SelectionRange.Start);
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            switch (n)
            {
                case FunctionDef f when f.NameStart == offset && f.Name == item.Name: return (m, f);
                case ClassDef c when c.NameStart == offset && c.Name == item.Name: return (m, c);
            }
        }
        return null;
    }

    /// <summary>The functions and classes a name or call at the cursor refers to, and the def under it when the cursor is on a def's name.</summary>
    List<(ModuleInfo Module, Node Def)> DefsAt(Snapshot snap, ModuleInfo m, int offset)
    {
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is FunctionDef f && offset >= f.NameStart && offset <= f.NameStart + f.Name.Length) return new() { (m, f) };
            if (n is ClassDef c && offset >= c.NameStart && offset <= c.NameStart + c.Name.Length) return new() { (m, c) };
        }
        return NameLikeAt(m, offset) is { } expr ? CalleeDefs(snap, m, expr) : new();
    }

    /// <summary>The functions and classes an expression names.</summary>
    static List<(ModuleInfo Module, Node Def)> CalleeDefs(Snapshot snap, ModuleInfo m, Expr callee)
    {
        var result = new List<(ModuleInfo, Node)>();
        var ev = snap.Workspace.Evaluator;
        void Add(ModuleInfo module, Node def)
        {
            if (!result.Any(r => ReferenceEquals(r.Item2, def))) result.Add((module, def));
        }
        foreach (var t in ev.TypeOf(m, callee).Members)
            switch (t)
            {
                case ClassType { IsInstance: false } c: Add(c.Info.Module, c.Info.Node); break;
                case FunctionType f when f.Info.Node is FunctionDef: Add(f.Info.Module, f.Info.Node); break;
                case OverloadedType o: foreach (var f in o.Overloads) if (f.Info.Node is FunctionDef) Add(f.Info.Module, f.Info.Node); break;
            }
        // a decorated function the engine cannot see through is still the def its name is bound to
        if (result.Count == 0 && callee is NameExpr ne && m.Semantics.Resolve(ne) is { } sym)
            foreach (var d in sym.Declarations)
                if (d.Node is FunctionDef or ClassDef) Add(m, d.Node);
        return result;
    }

    // ------------------------------------------------------------ go to implementation

    public IReadOnlyList<Location> Implementation(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<Location>();
        var found = new List<Location>();
        foreach (var (module, def) in DefsAt(snap, m, Lines(m).OffsetOf(pos)))
        {
            if (def is ClassDef cd)
            {
                foreach (var (sm, sc) in Descendants(snap, cd))
                    if (UriOfModule(sm) is { } u) found.Add(new Location(u, Lines(sm).RangeOf(sc.NameStart, sc.NameStart + sc.Name.Length)));
            }
            else if (def is FunctionDef f && ClassOfMethod(module, f) is { } owner)
            {
                foreach (var (sm, sc) in Descendants(snap, owner))
                    if (MethodIn(sm, sc, f.Name) is { } override_ && UriOfModule(sm) is { } u)
                        found.Add(new Location(u, Lines(sm).RangeOf(override_.NameStart, override_.NameStart + override_.Name.Length)));
            }
        }
        return found;
    }

    // ------------------------------------------------------------ gutter marks

    static readonly HashSet<string> UnmarkedMethods = new(StringComparer.Ordinal) { "__init__", "__new__", "__init_subclass__", "__class_getitem__", "__post_init__" };

    public IReadOnlyList<InheritanceMarker> InheritanceMarkers(string uri)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m) || m.IsStub) return Array.Empty<InheritanceMarker>();
        var lines = Lines(m);
        var markers = new List<InheritanceMarker>();

        Location? Where(ModuleInfo module, FunctionDef f) =>
            snap.Project.Contains(module) && UriOfModule(module) is { } u ? new Location(u, Lines(module).RangeOf(f.NameStart, f.NameStart + f.Name.Length)) : null;

        foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>())
        {
            var descendants = Descendants(snap, cd);
            var ancestors = Ancestors(snap, m, cd);

            var direct = SubclassIndex(snap).GetValueOrDefault(cd);
            if (direct is { Count: > 0 })
            {
                var targets = direct.Select(x => UriOfModule(x.Module) is { } u
                    ? new Location(u, Lines(x.Module).RangeOf(x.Class.NameStart, x.Class.NameStart + x.Class.Name.Length)) : null).OfType<Location>().ToList();
                if (targets.Count > 0)
                    markers.Add(new InheritanceMarker(lines.PositionOf(cd.NameStart).Line, "subclassed",
                        targets.Count == 1 ? "Has a subclass" : $"Has {targets.Count} subclasses", targets));
            }

            foreach (var f in cd.Body.OfType<FunctionDef>())
            {
                if (UnmarkedMethods.Contains(f.Name) || MethodIn(m, cd, f.Name) is not { } method || !ReferenceEquals(method, f)) continue;
                int line = lines.PositionOf(f.NameStart).Line;
                bool abstractMethod = IsAbstractDef(f);

                foreach (var (am, ac) in ancestors)
                {
                    if (MethodIn(am, ac, f.Name) is not { } above) continue;
                    if (Where(am, above) is { } where)
                        markers.Add(new InheritanceMarker(line, IsAbstractDef(above) ? "implements" : "overrides",
                            $"{(IsAbstractDef(above) ? "Implements" : "Overrides")} {ac.Name}.{f.Name}", new List<Location> { where }));
                    break;
                }

                var below = descendants.Select(d => (d.Module, Method: MethodIn(d.Module, d.Class, f.Name))).Where(x => x.Method != null)
                    .Select(x => Where(x.Module, x.Method!)).OfType<Location>().ToList();
                if (below.Count > 0)
                    markers.Add(new InheritanceMarker(line, abstractMethod ? "implemented" : "overridden",
                        abstractMethod ? $"Implemented in {below.Count} class{(below.Count == 1 ? "" : "es")}" : $"Overridden in {below.Count} class{(below.Count == 1 ? "" : "es")}", below));
            }
        }
        return markers;
    }

    // ------------------------------------------------------------ type hierarchy

    public IReadOnlyList<HierarchyItem> PrepareTypeHierarchy(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<HierarchyItem>();
        int offset = Lines(m).OffsetOf(pos);
        var ev = snap.Workspace.Evaluator;

        var defs = DefsAt(snap, m, offset).Where(d => d.Def is ClassDef).ToList();
        if (defs.Count == 0 && NameLikeAt(m, offset) is { } expr)
            foreach (var t in ev.TypeOf(m, expr).Members)
                if (t is ClassType c) defs.Add((c.Info.Module, c.Info.Node));   // a variable stands for its class
        if (defs.Count == 0)
            for (var p = NodeAt(m, offset) is { } at ? at : null; p != null; p = m.ParentOf(p))
                if (p is ClassDef enclosing) { defs.Add((m, enclosing)); break; }
        return defs.Select(d => ItemOf(d.Module, d.Def)).OfType<HierarchyItem>().Take(1).ToList();
    }

    public IReadOnlyList<HierarchyItem> Supertypes(HierarchyItem item)
    {
        var snap = Full();
        if (DefOf(snap, item) is not { Def: ClassDef cd } d) return Array.Empty<HierarchyItem>();
        return DirectBases(snap, d.Module, cd).Where(b => !(b.Class.Name == "object" && b.Module.Name == "builtins"))
            .Select(b => ItemOf(b.Module, b.Class)).OfType<HierarchyItem>().ToList();
    }

    public IReadOnlyList<HierarchyItem> Subtypes(HierarchyItem item)
    {
        var snap = Full();
        if (DefOf(snap, item) is not { Def: ClassDef cd }) return Array.Empty<HierarchyItem>();
        return (SubclassIndex(snap).GetValueOrDefault(cd) ?? new()).Select(s => ItemOf(s.Module, s.Class)).OfType<HierarchyItem>().ToList();
    }

    // ------------------------------------------------------------ call hierarchy

    public IReadOnlyList<HierarchyItem> PrepareCallHierarchy(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<HierarchyItem>();
        return DefsAt(snap, m, Lines(m).OffsetOf(pos)).Select(d => ItemOf(d.Module, d.Def)).OfType<HierarchyItem>().Take(1).ToList();
    }

    public IReadOnlyList<IncomingCall> IncomingCalls(HierarchyItem item)
    {
        var snap = Full();
        if (DefOf(snap, item) is not { } target) return Array.Empty<IncomingCall>();

        // calling a class calls its __init__, so the calls of the class are the calls of the constructor
        var names = new List<(ModuleInfo Module, Node Def)> { target };
        if (target.Def is FunctionDef { Name: "__init__" } init && ClassOfMethod(target.Module, init) is { } owner) names.Add((target.Module, owner));

        var callers = new Dictionary<Node, (ModuleInfo Module, List<Range> Ranges)>(ReferenceEqualityComparer.Instance);
        var order = new List<Node>();
        foreach (var (module, def) in names)
        {
            int nameStart = def switch { FunctionDef f => f.NameStart, ClassDef c => c.NameStart, _ => -1 };
            if (nameStart < 0 || FindTarget(snap, module, nameStart) is not { } t) continue;
            foreach (var site in Usages(snap, t, forRename: false))
            {
                if (site.IsDeclaration || CallAt(site) is not { } call) continue;
                var caller = EnclosingCaller(site.Module, call);
                if (!callers.TryGetValue(caller, out var entry)) { callers[caller] = entry = (site.Module, new List<Range>()); order.Add(caller); }
                entry.Ranges.Add(Lines(site.Module).RangeOf(site.Start, site.End));
            }
        }

        var result = new List<IncomingCall>();
        foreach (var caller in order)
        {
            var (module, ranges) = callers[caller];
            var from = caller is PyMagic.Core.Syntax.Module ? ModuleItem(module) : ItemOf(module, caller);
            if (from != null) result.Add(new IncomingCall(from, ranges.OrderBy(r => r.Start.Line).ThenBy(r => r.Start.Character).ToList()));
        }
        return result.OrderBy(c => c.From.Uri, StringComparer.Ordinal).ThenBy(c => c.From.SelectionRange.Start.Line).ToList();
    }

    /// <summary>The call expression whose callee is the name or attribute at a usage site.</summary>
    static CallExpr? CallAt(Site site)
    {
        var m = site.Module;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            bool hit = n switch
            {
                NameExpr ne => ne.Start == site.Start && ne.End == site.End,
                AttributeExpr ae => AttrSpan(ae) == (site.Start, site.End),
                _ => false,
            };
            if (hit) return m.ParentOf(n) is CallExpr c && ReferenceEquals(c.Func, n) ? c : null;
        }
        return null;
    }

    /// <summary>The function, class or module the call is written in.</summary>
    static Node EnclosingCaller(ModuleInfo m, Node call)
    {
        for (var p = m.ParentOf(call); p != null; p = m.ParentOf(p))
            if (p is FunctionDef or ClassDef) return p;
        return m.Ast;
    }

    public IReadOnlyList<OutgoingCall> OutgoingCalls(HierarchyItem item)
    {
        var snap = Full();
        if (DefOf(snap, item) is not { } source) return Array.Empty<OutgoingCall>();
        var (module, def) = source;
        // a class calls what its __init__ calls
        Node body = def is ClassDef cd && MethodIn(module, cd, "__init__") is { } ctor ? ctor : def;

        var callees = new Dictionary<Node, (ModuleInfo Module, List<Range> Ranges)>(ReferenceEqualityComparer.Instance);
        var order = new List<Node>();
        foreach (var call in CallsIn(body))
        {
            foreach (var (cm, target) in CalleeDefs(snap, module, call.Func))
            {
                if (cm.IsBundledStub || ReferenceEquals(target, def)) continue;
                if (!callees.TryGetValue(target, out var entry)) { callees[target] = entry = (cm, new List<Range>()); order.Add(target); }
                int end = call.Func is AttributeExpr ae ? ae.End : call.Func.End;
                int start = call.Func is AttributeExpr ae2 ? AttrSpan(ae2).Start : call.Func.Start;
                entry.Ranges.Add(Lines(module).RangeOf(start, end));
            }
        }
        var result = new List<OutgoingCall>();
        foreach (var target in order)
            if (ItemOf(callees[target].Module, target) is { } to) result.Add(new OutgoingCall(to, callees[target].Ranges));
        return result;
    }

    /// <summary>The calls a function makes itself: nested defs and classes are other functions' business.</summary>
    static IEnumerable<CallExpr> CallsIn(Node def)
    {
        var stack = new Stack<Node>();
        foreach (var c in AstChildren.Of(def).Reverse()) stack.Push(c);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is FunctionDef or ClassDef) continue;
            if (n is CallExpr call) yield return call;
            foreach (var c in AstChildren.Of(n).Reverse()) stack.Push(c);
        }
    }
}
