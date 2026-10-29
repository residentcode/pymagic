using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// A Django app's models against its migrations (item 150): the migrations are replayed (CreateModel, AddField, RemoveField, RenameField, AlterField,
/// DeleteModel, RenameModel; a squashed migration in place of those it replaces) and what the models have that they do not is what `makemigrations` would
/// write. Until it is written and applied, a query fails with "no such table" or "no such column" (checked with Django 6.1's makemigrations --dry-run).
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>The state the migrations leave: each model (by lower-case name) with its written name and its fields' classes.</summary>
    sealed class MigratedApp
    {
        public Dictionary<string, (string Name, Dictionary<string, string> Fields)> Models { get; } = new(StringComparer.Ordinal);
        public int Count;
    }

    /// <summary>The app a models module belongs to: `shop` for `shop.models` and `shop.models.book`; null for a module that is not a models module.</summary>
    static string? AppOfModels(string module)
    {
        var parts = module.Split('.');
        int at = Array.IndexOf(parts, "models");
        return at > 0 ? string.Join(".", parts.Take(at)) : null;
    }

    /// <summary>
    /// The migrations of the app whose models module is at <paramref name="modelsPath"/>, replayed in the order of their file names. They are read from the
    /// app's migrations folder (their names, `0001_initial`, are not importable module names, so they are not modules of the project).
    /// </summary>
    static MigratedApp? MigrationsOf(string modelsPath)
    {
        // shop/models.py or shop/models/book.py: the app folder holds models and migrations
        string? dir = System.IO.Path.GetDirectoryName(modelsPath);
        if (dir != null && System.IO.Path.GetFileName(dir) == "models") dir = System.IO.Path.GetDirectoryName(dir);
        string? folder = dir == null ? null : System.IO.Path.Combine(dir, "migrations");
        if (folder == null || !Directory.Exists(folder)) return null;
        List<string> files;
        string stamp;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.py").Where(f => !System.IO.Path.GetFileName(f).StartsWith('_')).OrderBy(f => f, StringComparer.Ordinal).ToList();
            stamp = string.Join("|", files.Select(f => System.IO.Path.GetFileName(f) + "@" + File.GetLastWriteTimeUtc(f).Ticks));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        // replayed once while no migration file changes
        if (MigrationCache.TryGetValue(folder, out var cached) && cached.Stamp == stamp) return cached.State;
        var state = Replay(files);
        MigrationCache[folder] = (stamp, state);
        return state;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, MigratedApp? State)> MigrationCache = new(StringComparer.OrdinalIgnoreCase);

    static MigratedApp? Replay(List<string> files)
    {
        var modules = new List<(string Name, ModuleInfo Module)>();
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            // parsed in a workspace of their own: only their statements are read
            modules.Add((System.IO.Path.GetFileNameWithoutExtension(f), new Workspace(new WorkspaceOptions()).AddModule("migration", text, cached: false)));
        }
        if (modules.Count == 0) return null;
        var classes = modules.Select(x => (x.Name, Class: x.Module.Ast.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == "Migration"))).Where(x => x.Class != null).ToList();
        // a squashed migration stands in for the ones it replaces
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, cls) in classes)
            if (ClassListAssign(cls!, "replaces") is { } list)
                foreach (var item in list.OfType<TupleExpr>().Where(t => t.Items.Count == 2)) if (FixtureModel.StringValue(item.Items[1]) is { } name) replaced.Add(name);
        var state = new MigratedApp();
        foreach (var (name, cls) in classes)
        {
            if (replaced.Contains(name)) continue;
            if (ClassListAssign(cls!, "operations") is not { } operations) continue;
            state.Count++;
            foreach (var op in operations.OfType<CallExpr>()) Apply(state, op);
        }
        return state;
    }

    static IReadOnlyList<Expr>? ClassListAssign(ClassDef cls, string name)
    {
        var stmt = cls.Body.OfType<AssignStmt>().FirstOrDefault(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr n && n.Name == name);
        return stmt?.Value switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
    }

    static void Apply(MigratedApp state, CallExpr op)
    {
        string? Arg(string name, int position) => FixtureModel.StringValue(op.Args.FirstOrDefault(a => a.Name == name)?.Value ?? op.Args.Where(a => a.Name == null).ElementAtOrDefault(position)?.Value);
        Expr? ArgExpr(string name, int position) => op.Args.FirstOrDefault(a => a.Name == name)?.Value ?? op.Args.Where(a => a.Name == null).ElementAtOrDefault(position)?.Value;
        static string FieldClass(Expr? e) => e is CallExpr c ? LastSegment(c.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => "" }) : "";
        string? kind = op.Func switch { AttributeExpr a => a.Attr, NameExpr n => n.Name, _ => null };
        switch (kind)
        {
            case "CreateModel" when Arg("name", 0) is { } name:
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ArgExpr("fields", 1) is ListExpr list)
                    foreach (var t in list.Items.OfType<TupleExpr>().Where(t => t.Items.Count >= 2))
                        if (FixtureModel.StringValue(t.Items[0]) is { } field) fields[field] = FieldClass(t.Items[1]);
                state.Models[name.ToLowerInvariant()] = (name, fields);
                break;
            }
            case "DeleteModel" when Arg("name", 0) is { } name:
                state.Models.Remove(name.ToLowerInvariant());
                break;
            case "RenameModel" when Arg("old_name", 0) is { } from && Arg("new_name", 1) is { } to && state.Models.Remove(from.ToLowerInvariant(), out var moved):
                state.Models[to.ToLowerInvariant()] = (to, moved.Fields);
                break;
            case "AddField" or "AlterField" when Arg("model_name", 0) is { } model && Arg("name", 1) is { } field && state.Models.TryGetValue(model.ToLowerInvariant(), out var m):
                m.Fields[field] = FieldClass(ArgExpr("field", 2));
                break;
            case "RemoveField" when Arg("model_name", 0) is { } model && Arg("name", 1) is { } field && state.Models.TryGetValue(model.ToLowerInvariant(), out var m):
                m.Fields.Remove(field);
                break;
            case "RenameField" when Arg("model_name", 0) is { } model && Arg("old_name", 1) is { } from && Arg("new_name", 2) is { } to && state.Models.TryGetValue(model.ToLowerInvariant(), out var m)
                && m.Fields.Remove(from, out var cls):
                m.Fields[to] = cls;
                break;
        }
    }

    /// <summary>A model's own Meta says `abstract = True`, `proxy = True` or `managed = False`: it has no table of its own that migrations make.</summary>
    static bool HasNoTableOfItsOwn(ClassInfo model) =>
        model.Node.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == "Meta") is { } meta && meta.Body.OfType<AssignStmt>().Any(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr
        {
            Name: "abstract" or "proxy" or "managed"
        } n && a.Value is ConstExpr { Kind: var k } && (n.Name == "managed" ? k == ConstKind.False : k == ConstKind.True));

    IEnumerable<Diag> MigrationDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || AppOfModels(m.Name) is not { } app || m.Source.IndexOf("models", StringComparison.Ordinal) < 0) yield break;
        string? path = (Current().UriOf.TryGetValue(m, out var uri) ? PathOfUri(uri) : null) ?? m.Path;
        if (path == null || MigrationsOf(path) is not { Count: > 0 } state) yield break;   // an app without migrations is not judged
        var findings = new List<Finding>();
        string Make() => $"python manage.py makemigrations {LastSegment(app)}";
        foreach (var cd in m.Ast.Body.OfType<ClassDef>())
        {
            if (ClassInfoOf(m, cd, ev) is not { } model || !ev.IsDjangoModel(model) || HasNoTableOfItsOwn(model)) continue;
            string table = $"{LastSegment(app)}_{model.Name.ToLowerInvariant()}";
            if (!state.Models.TryGetValue(model.Name.ToLowerInvariant(), out var migrated))
            {
                findings.Add(new Finding(cd.NameStart, cd.Name.Length, $"{model.Name} is in no migration of {app}: until `{Make()}` and `migrate`, its queries fail with \"no such table: {table}\"", "django-migration", FindingLevel.Warning));
                continue;
            }
            // the fields of its table: its own, and those of abstract bases
            var fields = ev.DjangoFieldsOf(model).Where(f => f.Owner == model || HasNoTableOfItsOwn(f.Owner)).ToList();
            foreach (var f in fields)
            {
                var at = f.Call;
                var assign = cd.Body.OfType<AssignStmt>().FirstOrDefault(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr n && n.Name == f.Name);
                int start = assign?.Targets[0].Start ?? cd.NameStart, length = assign != null ? f.Name.Length : cd.Name.Length;
                if (!migrated.Fields.TryGetValue(f.Name, out var cls))
                {
                    string column = f.Kind is "foreign" or "one-to-one" ? f.Name + "_id" : f.Name;
                    string why = f.Kind == "many-to-many" ? $"its table is not made: reading it fails with \"no such table\"" : $"queries of {model.Name} fail with \"no such column: {table}.{column}\"";
                    findings.Add(new Finding(start, length, $"'{f.Name}' is in no migration of {app}: until `{Make()}` and `migrate`, {why}", "django-migration", FindingLevel.Warning));
                }
                else if (cls.Length > 0 && cls != f.FieldClass.Name)
                    findings.Add(new Finding(start, length, $"'{f.Name}' is a {f.FieldClass.Name} here and a {cls} in the migrations of {app}: `{Make()}` will write an AlterField", "django-migration", FindingLevel.Weak));
            }
            // fields the migrations have and the model no longer does (the implicit id aside)
            var gone = migrated.Fields.Keys.Where(k => !fields.Any(f => f.Name == k) && !(k == "id" && !fields.Any(f => f.PrimaryKey))).ToList();
            if (gone.Count > 0 && OnlyProjectModelBases(ev, model))
                findings.Add(new Finding(cd.NameStart, cd.Name.Length, $"The migrations of {app} still have {string.Join(", ", gone.Select(g => $"'{g}'"))} on {model.Name}, which the model no longer has: `{Make()}` will remove {(gone.Count == 1 ? "it" : "them")}", "django-migration", FindingLevel.Weak));
        }
        foreach (var f in findings) if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    /// <summary>Every base of a model with fields is the project's (a library base may bring fields the editor cannot see).</summary>
    static bool OnlyProjectModelBases(TypeEvaluator ev, ClassInfo model) =>
        ev.MroOf(model).OfType<ClassType>().All(t => !t.Info.Module.IsStub && !t.Info.Module.Name.StartsWith("django", StringComparison.Ordinal) && t.Info.Module.Kind is ResolutionKind.UserCode or ResolutionKind.InMemory
            || t.Info.Module.Name.StartsWith("django", StringComparison.Ordinal) || t.Info.QualifiedName == "builtins.object");
}
