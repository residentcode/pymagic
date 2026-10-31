using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>INSTALLED_APPS: an entry nothing can import, and the models of an app the list leaves out (Django refuses both when it starts).</summary>
public sealed partial class LanguageService
{
    (DateTime At, string Roots, List<string> Names)? _settingsModules;

    /// <summary>The project's settings modules (`settings.py`, and the modules of a `settings` package), as module names.</summary>
    List<string> SettingsModuleNames()
    {
        string roots = string.Join("|", SourceRootsNow());
        if (_settingsModules is { } cached && cached.Roots == roots && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(5)) return cached.Names;
        var names = new List<string>();
        foreach (var path in ProjectFiles())
        {
            string file = System.IO.Path.GetFileName(path);
            if (file != "settings.py" && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) != "settings") continue;
            var (name, _) = ModuleNameOf(path);
            if (!name.StartsWith("__", StringComparison.Ordinal)) names.Add(name);
        }
        _settingsModules = (DateTime.UtcNow, roots, names);
        return names;
    }

    /// <summary>The string entries of `INSTALLED_APPS = [...]`, `INSTALLED_APPS += [...]` and the `*_APPS` lists of a module, with the list and where each is written.</summary>
    static IEnumerable<(string Value, ConstExpr Literal, string List)> AppEntries(ModuleInfo m)
    {
        foreach (var stmt in AstChildren.Descendants(m.Ast))
        {
            (string Name, Expr Value)? target = stmt switch
            {
                AssignStmt { Targets: [NameExpr n] } a when n.Name.EndsWith("APPS", StringComparison.Ordinal) => (n.Name, a.Value),
                AugAssignStmt { Target: NameExpr n } a when n.Name.EndsWith("APPS", StringComparison.Ordinal) => (n.Name, a.Value),
                _ => null,
            };
            if (target == null) continue;
            foreach (var literal in AstChildren.Descendants(target.Value.Value).OfType<ConstExpr>())
                if (FixtureModel.StringValue(literal) is { } value) yield return (value, literal, target.Value.Name);
        }
    }

    /// <summary>Every entry of INSTALLED_APPS in the project's settings; null when no settings module assigns INSTALLED_APPS.</summary>
    HashSet<string>? InstalledAppEntries(Snapshot snap)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        bool any = false;
        var names = SettingsModuleNames().Concat(snap.Project.Where(p => !p.IsStub && (p.Name == "settings" || p.Name.EndsWith(".settings", StringComparison.Ordinal) || p.Name.Contains(".settings.", StringComparison.Ordinal))).Select(p => p.Name));
        foreach (var name in names.Distinct())
        {
            if (snap.Workspace.GetModule(name) is not { IsStub: false } settings || !settings.Source.Contains("INSTALLED_APPS", StringComparison.Ordinal)) continue;
            any = true;
            foreach (var (value, _, _) in AppEntries(settings)) entries.Add(value);
        }
        return any ? entries : null;
    }

    IEnumerable<Diag> InstalledAppsDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        var snap = ReferenceEquals(Current().Workspace.Evaluator, ev) ? Current() : Full();
        var ws = snap.Workspace;

        // the lists INSTALLED_APPS is built from (DJANGO_APPS + LOCAL_APPS): each entry is a module, or an AppConfig class of one
        // (INSTALLED_APPS's own entries are checked with the other dotted paths of the settings)
        if (m.Source.Contains("INSTALLED_APPS", StringComparison.Ordinal) && ws.GetModule("django") is { IsStub: false })
            foreach (var (value, literal, list) in AppEntries(m))
            {
                if (list == "INSTALLED_APPS" || value.Length == 0 || value.Contains(' ') || value.Contains('/') || Importable(ws, value)) continue;
                var finding = new Finding(literal.Start, literal.End - literal.Start,
                    $"No module or AppConfig '{value}' can be imported, and {list} goes into INSTALLED_APPS: Django fails with ModuleNotFoundError (or ImportError) when it starts", "django-installed-apps", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }

        // a models module of an app that INSTALLED_APPS leaves out
        int at = m.Name.IndexOf(".models", StringComparison.Ordinal);
        if (at <= 0 || !(m.Name.Length == at + 7 || m.Name[at + 7] == '.') || m.Source.IndexOf("Model", StringComparison.Ordinal) < 0) yield break;
        string app = m.Name[..at];
        if (InstalledAppEntries(snap) is not { } installed || installed.Any(e => e == app || e.StartsWith(app + ".apps.", StringComparison.Ordinal))) yield break;
        // an AppConfig elsewhere may name the app (`name = "shop"`)
        if (installed.Any(e => AppConfigName(ws, e) == app)) yield break;
        foreach (var cd in m.Ast.Body.OfType<ClassDef>())
        {
            if (ClassInfoOf(m, cd, ev) is not { } info || !ev.IsDjangoModel(info) || MetaSays(cd, "abstract") || MetaSays(cd, "app_label")) continue;
            var finding = new Finding(cd.NameStart, cd.Name.Length,
                $"The app '{app}' of {cd.Name} is not in INSTALLED_APPS: Django raises RuntimeError (\"Model class {m.Name}.{cd.Name} doesn't declare an explicit app_label and isn't in an application in INSTALLED_APPS\")",
                "django-installed-apps", FindingLevel.Warning);
            if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
        }
    }

    /// <summary>A module, or a class of a module (an AppConfig: `shop.apps.ShopConfig`).</summary>
    static bool Importable(Workspace ws, string dotted)
    {
        if (ws.GetModule(dotted) != null) return true;
        int dot = dotted.LastIndexOf('.');
        return dot > 0 && ws.GetModule(dotted[..dot]) is { } parent && parent.Semantics.ModuleScope.Symbols.ContainsKey(dotted[(dot + 1)..]);
    }

    /// <summary>The `name = "..."` of an AppConfig an entry names, if it is one.</summary>
    static string? AppConfigName(Workspace ws, string dotted)
    {
        int dot = dotted.LastIndexOf('.');
        if (dot <= 0 || ws.GetModule(dotted[..dot]) is not { IsStub: false } module) return null;
        var cls = module.Ast.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == dotted[(dot + 1)..]);
        return cls?.Body.OfType<AssignStmt>().Where(a => a.Targets is [NameExpr { Name: "name" }]).Select(a => FixtureModel.StringValue(a.Value as ConstExpr)).FirstOrDefault(v => v != null);
    }

    /// <summary>A model's `class Meta` sets the attribute (`abstract = True`, `app_label = "shop"`).</summary>
    static bool MetaSays(ClassDef model, string attribute) =>
        model.Body.OfType<ClassDef>().Where(c => c.Name == "Meta").SelectMany(c => c.Body.OfType<AssignStmt>())
            .Any(a => a.Targets is [NameExpr n] && n.Name == attribute && !(a.Value is ConstExpr { Kind: ConstKind.False }));
}
