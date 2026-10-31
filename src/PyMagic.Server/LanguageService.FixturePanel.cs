using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>A fixture of the project for the Fixtures view: where it is defined, its scope, what it gives, and how many tests and fixtures ask for it.</summary>
/// <param name="Where">conftest, module, class, plugin or pytest.</param>
public sealed record FixtureEntry(string Name, string Scope, bool Autouse, string Where, string Origin, string? Uri, int Line, string? Type, int Uses, string? Doc);

public sealed partial class LanguageService
{
    /// <summary>
    /// Every fixture the project's tests can use: those of the conftest.py files and test modules (and their test classes), of the plugins pytest loads
    /// for them (installed, `pytest_plugins`, `-p`, objects registered at run time), and pytest's own, each with how often a test or fixture asks for it.
    /// </summary>
    public IReadOnlyList<FixtureEntry> Fixtures()
    {
        var snap = Full();
        var ws = snap.Workspace;
        var ev = ws.Evaluator;
        if (FixturesOf(ws) is not { } ctx) return Array.Empty<FixtureEntry>();
        var modules = snap.Project.Where(m => !m.IsStub && (FixtureModel.IsTestModule(m) || m.Name == "conftest" || m.Name.EndsWith(".conftest", StringComparison.Ordinal))).ToList();

        // how often each name is asked for: parameters of tests and fixtures, usefixtures(...) and getfixturevalue(...)
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in modules)
        {
            foreach (var f in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
                foreach (var p in FixtureModel.RequestedParameters(m, f)) uses[p.Name] = uses.GetValueOrDefault(p.Name) + 1;
            foreach (var s in FixtureModel.StringRequests(m)) uses[s.Name] = uses.GetValueOrDefault(s.Name) + 1;
        }

        var result = new List<FixtureEntry>();
        var seen = new HashSet<FunctionDef>(ReferenceEqualityComparer.Instance);
        void Add(FixtureDef d, string where, string origin)
        {
            if (!seen.Add(d.Def)) return;
            var type = ev.FixtureValueType(d.Module, d.Def);
            string? doc = d.Def.Body.FirstOrDefault() is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } c } ? FixtureModel.StringValue(c)?.Trim().Split('\n')[0] : null;
            string? uri = UriOfModule(d.Module);
            result.Add(new FixtureEntry(d.Name, d.Scope, d.Autouse, where, origin, uri, uri == null ? 0 : Lines(d.Module).PositionOf(d.Def.NameStart).Line,
                type is UnknownType ? null : type.Display(), uses.GetValueOrDefault(d.Name), doc));
        }
        string Rel(ModuleInfo m) => UriOfModule(m) is { } u && PathOfUri(u) is { } path ? RelativeToRoots(path) : m.Name;

        foreach (var m in modules.OrderBy(m => m.Name.Count(ch => ch == '.')).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            bool conftest = m.Name == "conftest" || m.Name.EndsWith(".conftest", StringComparison.Ordinal);
            foreach (var d in ctx.Resolver.DefinedIn(ws, m, null).Where(d => ReferenceEquals(d.Module, m))) Add(d, conftest ? "conftest" : "module", Rel(m));
            // a class whose object is registered as a plugin first: its fixtures are everyone's, not the class's
            foreach (var (module, cls) in FixtureHooks.RegisteredPluginClasses(ws, m))
                foreach (var d in ctx.Resolver.DefinedIn(ws, module, cls)) Add(d, "plugin", $"{cls.Name}, registered in {Rel(m)}");
            foreach (var cls in AstChildren.Descendants(m.Ast).OfType<ClassDef>())
                foreach (var d in ctx.Resolver.DefinedIn(ws, m, cls)) Add(d, "class", $"{Rel(m)} · {cls.Name}");
        }
        foreach (var m in modules)
            foreach (var plugin in ctx.Resolver.PluginsOf(ws, m))
                foreach (var d in ctx.Resolver.DefinedIn(ws, plugin, null)) Add(d, "plugin", plugin.Name);
        foreach (var b in FixtureModel.Builtins.Values.OrderBy(b => b.Name, StringComparer.Ordinal))
            result.Add(new FixtureEntry(b.Name, b.Scope, false, "pytest", "pytest", null, 0, b.Class, uses.GetValueOrDefault(b.Name), b.Description));
        return result;
    }

    /// <summary>A path as the project shows it: relative to the source root it is under.</summary>
    string RelativeToRoots(string path)
    {
        foreach (var root in SourceRootsNow())
        {
            string full = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            if (path.StartsWith(full, StringComparison.OrdinalIgnoreCase)) return path[full.Length..].Replace('\\', '/');
        }
        return path.Replace('\\', '/');
    }
}
