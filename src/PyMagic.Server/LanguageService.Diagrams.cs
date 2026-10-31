using PyModule = PyMagic.Core.Syntax.Module;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>A box of a diagram: a class (its fields and methods), a module, or a Django model (its fields).</summary>
public sealed record DiagramNode(string Id, string Label, string Kind, IReadOnlyList<string> Members, string Uri, int Line, bool Outside = false);

/// <summary>An arrow: extends (a class to its base), imports (a module to one it imports), or a model's relation (foreignKey, oneToOne, manyToMany) named by its field.</summary>
public sealed record DiagramEdge(string From, string To, string Kind, string? Label = null);

public sealed record Diagram(string Title, IReadOnlyList<DiagramNode> Nodes, IReadOnlyList<DiagramEdge> Edges, bool Truncated);

/// <summary>Diagrams of the project: the classes of a file with what they derive from and what derives from them, the modules and their imports, and Django's models with their relations.</summary>
public sealed partial class LanguageService
{
    const int DiagramLimit = 120;

    /// <param name="kind">classes (of the file at <paramref name="uri"/>, or of the whole project when it is null), modules, or models.</param>
    public Diagram Diagram(string kind, string? uri)
    {
        var snap = Full();
        var project = snap.Project.Where(m => InProject(snap, m)).OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        return kind switch
        {
            "modules" => ModuleDiagram(snap, project),
            "models" => ModelDiagram(snap, project),
            _ => ClassDiagram(snap, project, uri != null && snap.ByUri.TryGetValue(uri, out var m) ? m : null),
        };
    }

    static string ClassId(ModuleInfo m, ClassDef cd) => $"{m.Name}.{cd.Name}@{cd.Start}";

    DiagramNode ClassNode(Snapshot snap, ModuleInfo m, ClassDef cd, bool outside = false)
    {
        var members = new List<string>();
        foreach (var s in cd.Body)
            switch (s)
            {
                case AnnAssignStmt { Target: NameExpr n } a: members.Add($"{n.Name}: {m.Source[a.Annotation.Start..a.Annotation.End]}"); break;
                case AssignStmt { Targets: [NameExpr n] }: members.Add(n.Name); break;
            }
        // the attributes __init__ sets, then the methods
        if (cd.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__") is { } init && SelfName(init) is { } self)
            foreach (var a in AstChildren.Descendants(init).OfType<AttributeExpr>())
                if (a.Value is NameExpr { Name: var v } && v == self && m.ParentOf(a) is AssignStmt or AnnAssignStmt && !members.Any(x => x == a.Attr || x.StartsWith(a.Attr + ":"))) members.Add(a.Attr);
        foreach (var f in cd.Body.OfType<FunctionDef>())
            members.Add($"{f.Name}()");
        var lines = Lines(m);
        return new DiagramNode(ClassId(m, cd), cd.Name, "class", members.Take(40).ToList(), UriOfModule(m) ?? "", lines.RangeOf(cd.NameStart, cd.NameStart).Start.Line, outside);
    }

    Diagram ClassDiagram(Snapshot snap, List<ModuleInfo> project, ModuleInfo? file)
    {
        var nodes = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
        var edges = new List<DiagramEdge>();
        var queue = new Queue<(ModuleInfo M, ClassDef C, bool Outside)>();
        var start = file != null
            ? AstChildren.Descendants(file.Ast).OfType<ClassDef>().Select(c => (file, c, false))
            : project.SelectMany(m => AstChildren.Descendants(m.Ast).OfType<ClassDef>().Select(c => (m, c, false)));
        foreach (var x in start) queue.Enqueue(x);
        bool truncated = false;
        var index = SubclassIndex(snap);
        while (queue.Count > 0)
        {
            var (m, cd, outside) = queue.Dequeue();
            string id = ClassId(m, cd);
            if (nodes.ContainsKey(id)) continue;
            if (nodes.Count >= DiagramLimit) { truncated = true; break; }
            bool mine = InProject(snap, m);
            nodes[id] = ClassNode(snap, m, cd, outside || !mine);
            if (!mine) continue;   // a library's class is shown, not followed
            foreach (var (bm, bc) in DirectBases(snap, m, cd))
            {
                if (bc.Name == "object" && bm.Name == "builtins") continue;
                edges.Add(new DiagramEdge(id, ClassId(bm, bc), "extends"));
                queue.Enqueue((bm, bc, file != null && !ReferenceEquals(bm, file)));
            }
            // a file's diagram also shows what derives from its classes
            if (file != null && ReferenceEquals(m, file))
                foreach (var (sm, sc) in index.GetValueOrDefault(cd) ?? new())
                    queue.Enqueue((sm, sc, !ReferenceEquals(sm, file)));
        }
        var known = nodes.Keys.ToHashSet();
        return new Diagram(file != null ? $"Classes of {file.Name}" : "Classes of the project", nodes.Values.ToList(),
            edges.Where(e => known.Contains(e.From) && known.Contains(e.To)).Distinct().ToList(), truncated);
    }

    Diagram ModuleDiagram(Snapshot snap, List<ModuleInfo> project)
    {
        var byName = project.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var nodes = project.Take(DiagramLimit).Select(m =>
        {
            var defs = m.Ast.Body.Select(s => s switch { ClassDef c => c.Name, FunctionDef f => f.Name + "()", _ => null }).OfType<string>().Take(30).ToList();
            return new DiagramNode(m.Name, m.Name, "module", defs, UriOfModule(m) ?? "", 0);
        }).ToList();
        var shown = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = new HashSet<DiagramEdge>();
        foreach (var m in project.Where(p => shown.Contains(p.Name)))
            foreach (var n in AstChildren.Descendants(m.Ast))
            {
                var targets = n switch
                {
                    ImportStmt s => s.Names.Select(a => a.Name),
                    ImportFromStmt f when Workspace.AbsoluteName(m, f.Level, f.Module) is { } b => f.Names.Select(a => b + "." + a.Name).Prepend(b),
                    _ => Enumerable.Empty<string>(),
                };
                foreach (var t in targets)
                {
                    // the longest name of a project module the import starts with (`pkg.mod.func` is pkg.mod)
                    string? hit = null;
                    for (string probe = t; probe.Length > 0; probe = probe.Contains('.') ? probe[..probe.LastIndexOf('.')] : "")
                        if (byName.ContainsKey(probe)) { hit = probe; break; }
                    if (hit != null && hit != m.Name && shown.Contains(hit)) edges.Add(new DiagramEdge(m.Name, hit, "imports"));
                }
            }
        return new Diagram("Modules of the project and their imports", nodes, edges.ToList(), project.Count > DiagramLimit);
    }

    static readonly Dictionary<string, string> RelationKinds = new(StringComparer.Ordinal)
    {
        ["ForeignKey"] = "foreignKey", ["OneToOneField"] = "oneToOne", ["ManyToManyField"] = "manyToMany", ["ParentalKey"] = "foreignKey",
    };

    Diagram ModelDiagram(Snapshot snap, List<ModuleInfo> project)
    {
        var ev = snap.Workspace.Evaluator;
        var models = new List<(ModuleInfo M, ClassDef C)>();
        foreach (var m in project)
            foreach (var cd in m.Ast.Body.OfType<ClassDef>())
                if (ClassInfoOf(m, cd, ev) is { } info && KnownMro(info, ev) is { } mro
                    && mro.Skip(1).Any(c => c.Info.Name == "Model" && c.Info.Module.Name.StartsWith("django.db.models", StringComparison.Ordinal)))
                    models.Add((m, cd));
        var byName = models.GroupBy(x => x.C.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var nodes = new List<DiagramNode>();
        var edges = new List<DiagramEdge>();
        foreach (var (m, cd) in models.Take(DiagramLimit))
        {
            var fields = new List<string>();
            foreach (var s in cd.Body.OfType<AssignStmt>())
            {
                if (s.Targets is not [NameExpr target] || s.Value is not CallExpr call) continue;
                string field = call.Func switch { AttributeExpr a => a.Attr, NameExpr n => n.Name, _ => "" };
                if (!field.EndsWith("Field") && !RelationKinds.ContainsKey(field)) continue;
                fields.Add($"{target.Name}: {field}");
                if (!RelationKinds.TryGetValue(field, out var relation) || call.Args.FirstOrDefault(a => a.Name is null or "to") is not { } first) continue;
                // the model a relation points to: a name, "Model", "app.Model" or "self"
                string? to = first.Value switch
                {
                    NameExpr n => n.Name,
                    AttributeExpr a => a.Attr,
                    ConstExpr { Kind: ConstKind.Str } c => System.Text.RegularExpressions.Regex.Replace(c.Text, @"^[rRbBuU]*(['""]+)(.*?)\1$", "$2").Split('.')[^1],
                    _ => null,
                };
                if (to == "self") to = cd.Name;
                if (to != null && byName.TryGetValue(to, out var target2)) edges.Add(new DiagramEdge(ClassId(m, cd), ClassId(target2.M, target2.C), relation, target.Name));
            }
            nodes.Add(new DiagramNode(ClassId(m, cd), cd.Name, "model", fields, UriOfModule(m) ?? "", Lines(m).RangeOf(cd.NameStart, cd.NameStart).Start.Line));
            foreach (var (bm, bc) in DirectBases(snap, m, cd))
                if (byName.TryGetValue(bc.Name, out var parent) && ReferenceEquals(parent.C, bc)) edges.Add(new DiagramEdge(ClassId(m, cd), ClassId(bm, bc), "extends"));
        }
        return new Diagram("Django models and their relations", nodes, edges, models.Count > DiagramLimit);
    }
}
