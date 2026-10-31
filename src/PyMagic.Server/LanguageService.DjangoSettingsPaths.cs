using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// The dotted paths of a settings file: INSTALLED_APPS (a package or an AppConfig), MIDDLEWARE, ROOT_URLCONF, WSGI_APPLICATION and
/// ASGI_APPLICATION (a module and a name in it), and AUTH_USER_MODEL (`app_label.Model`), checked and followed with Ctrl+click.
/// Nothing is reported while Django itself cannot be found: the installed packages are not known then.
/// </summary>
public sealed partial class LanguageService
{
    enum SettingsPathKind { App, ModuleAttribute, Module, UserModel }

    sealed record SettingsPath(string Setting, SettingsPathKind Kind, string Value, int Start, int End);

    static readonly Dictionary<string, SettingsPathKind> SettingsPathKinds = new(StringComparer.Ordinal)
    {
        ["INSTALLED_APPS"] = SettingsPathKind.App, ["MIDDLEWARE"] = SettingsPathKind.ModuleAttribute, ["ROOT_URLCONF"] = SettingsPathKind.Module,
        ["WSGI_APPLICATION"] = SettingsPathKind.ModuleAttribute, ["ASGI_APPLICATION"] = SettingsPathKind.ModuleAttribute, ["AUTH_USER_MODEL"] = SettingsPathKind.UserModel,
    };

    /// <summary>The strings of the module's top-level `NAME = ...` and `NAME += ...` for the settings above.</summary>
    static IEnumerable<SettingsPath> SettingsPathsIn(ModuleInfo m)
    {
        if (m.IsStub || !SettingsPathKinds.Keys.Any(k => m.Source.Contains(k, StringComparison.Ordinal))) yield break;
        foreach (var stmt in m.Ast.Body)
        {
            var (target, value) = stmt switch
            {
                AssignStmt { Targets: [NameExpr t] } a => (t.Name, a.Value),
                AugAssignStmt { Target: NameExpr t } aug => (t.Name, aug.Value),
                _ => (null, null),
            };
            if (target == null || value == null || !SettingsPathKinds.TryGetValue(target, out var kind)) continue;
            var items = value switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => new[] { value } };
            foreach (var item in items)
                if (item is ConstExpr c && FixtureModel.StringValue(c) is { Length: > 0 } text && !text.Contains(' '))
                    yield return new SettingsPath(target, kind, text, c.Start + 1, c.End - 1);
        }
    }

    sealed record SettingsPathTarget(List<Location> Targets, string? Problem);

    /// <summary>Where a path leads, or what is wrong with it; null when nothing can be said.</summary>
    SettingsPathTarget? ResolveSettingsPath(Snapshot snap, ModuleInfo settings, SettingsPath p)
    {
        var ws = snap.Workspace;
        if (p.Kind == SettingsPathKind.UserModel)
        {
            var parts = p.Value.Split('.');
            if (parts.Length != 2) return new(new(), $"AUTH_USER_MODEL must be of the form 'app_label.ModelName', not '{p.Value}'");
            var apps = SettingsPathsIn(settings).Where(x => x.Kind == SettingsPathKind.App).Select(x => x.Value).ToList();
            string? app = apps.FirstOrDefault(a => AppLabelOf(ws, a) == parts[0]);
            if (app == null)
                return apps.Count == 0 || parts[0] is "auth" or "admin" or "contenttypes" or "sessions" ? null : new(new(), $"No app of INSTALLED_APPS has the label '{parts[0]}': Django raises ImproperlyConfigured");
            string package = AppPackageOf(ws, app);
            if (ws.GetModule(package + ".models") is not { } models) return null;
            if (models.Semantics.ModuleScope.Symbols.GetValueOrDefault(parts[1])?.Declarations.FirstOrDefault(d => d.Node is ClassDef) is { Node: ClassDef cd }
                && FixtureLocation(models, cd.NameStart, cd.NameStart + cd.Name.Length) is { } at)
                return new(new() { at }, null);
            if (models.Semantics.ModuleScope.Symbols.ContainsKey(parts[1])) return new(new(), null);
            return new(new(), $"'{package}.models' has no model '{parts[1]}': Django raises ImproperlyConfigured");
        }

        var names = p.Value.Split('.');
        // with Django itself not found the interpreter's packages are not known, and a missing package proves nothing
        if (ws.GetModule("django") == null) return null;
        if (ws.GetModule(names[0]) == null) return new(new(), $"No module '{names[0]}': it is neither in the project nor installed");
        // the longest prefix that is a module, then a name in it
        int length = names.Length;
        ModuleInfo? module = null;
        for (; length > 0; length--)
            if ((module = ws.GetModule(string.Join('.', names.Take(length)))) != null) break;
        string modulePath = string.Join('.', names.Take(length));
        var rest = names.Skip(length).ToList();
        if (rest.Count == 0)
        {
            if (p.Kind == SettingsPathKind.ModuleAttribute) return new(new(), $"'{p.Value}' is a module: {p.Setting} names something in a module ('{p.Value}.Name')");
            return new(FixtureLocation(module!, 0, 0) is { } top ? new() { top } : new(), null);
        }
        if (p.Kind == SettingsPathKind.Module) return new(new(), $"No module '{p.Value}' (found '{modulePath}')");
        if (rest.Count > 1) return new(new(), $"'{modulePath}' has no module '{rest[0]}'");
        if (module!.Semantics.ModuleScope.Symbols.GetValueOrDefault(rest[0]) is not { } sym)
            return module.Source.Length == 0 && module.Path == null ? null : new(new(), $"'{modulePath}' has no '{rest[0]}'{(p.Kind == SettingsPathKind.App ? " (an app is a package or an AppConfig class)" : "")}");
        var decl = sym.Declarations.FirstOrDefault();
        var node = decl?.Node;
        int start = node is ClassDef c ? c.NameStart : node is FunctionDef f ? f.NameStart : node?.Start ?? 0;
        int end = node is ClassDef c2 ? c2.NameStart + c2.Name.Length : node is FunctionDef f2 ? f2.NameStart + f2.Name.Length : node?.End ?? 0;
        return new(node != null && FixtureLocation(module, start, end) is { } loc ? new() { loc } : new(), null);
    }

    /// <summary>The package of an INSTALLED_APPS entry: itself, or for an AppConfig class (`shop.apps.ShopConfig`) its `name`.</summary>
    static string AppPackageOf(Workspace ws, string app)
    {
        if (ws.GetModule(app) != null) return app;
        int dot = app.LastIndexOf('.');
        if (dot > 0 && ws.GetModule(app[..dot]) is { } module && AppConfigValue(module, app[(dot + 1)..], "name") is { } name) return name;
        // shop.apps.ShopConfig: the package two levels up
        var parts = app.Split('.');
        return parts.Length > 2 ? string.Join('.', parts.Take(parts.Length - 2)) : app;
    }

    /// <summary>An app's label: its AppConfig's `label`, else the last part of its package.</summary>
    static string AppLabelOf(Workspace ws, string app)
    {
        int dot = app.LastIndexOf('.');
        if (ws.GetModule(app) == null && dot > 0 && ws.GetModule(app[..dot]) is { } module && AppConfigValue(module, app[(dot + 1)..], "label") is { } label) return label;
        string package = AppPackageOf(ws, app);
        return package[(package.LastIndexOf('.') + 1)..];
    }

    static string? AppConfigValue(ModuleInfo module, string className, string attribute)
    {
        var cls = module.Ast.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == className);
        return cls?.Body.OfType<AssignStmt>().Where(a => a.Targets is [NameExpr { Name: var n }] && n == attribute).Select(a => FixtureModel.StringValue(a.Value)).FirstOrDefault(v => v != null);
    }

    IReadOnlyList<Location>? SettingsPathDefinition(Snapshot snap, ModuleInfo m, int offset)
    {
        var p = SettingsPathsIn(m).FirstOrDefault(x => offset >= x.Start && offset <= x.End);
        return p == null ? null : ResolveSettingsPath(snap, m, p)?.Targets;
    }

    IEnumerable<Diag> SettingsPathDiagnostics(ModuleInfo m)
    {
        var paths = SettingsPathsIn(m).ToList();
        if (paths.Count == 0) yield break;
        var snap = Current();
        foreach (var p in paths)
        {
            if (ResolveSettingsPath(snap, m, p) is not { Problem: { } problem }) continue;
            var finding = new Finding(p.Start, p.End - p.Start, problem, "django-settings-path", FindingLevel.Warning);
            if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
        }
    }
}
