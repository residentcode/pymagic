using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// The Problems of files that did not change, remembered between runs of the Problems tab. A file's findings are kept under a key made from the file itself and
/// from everything its types can come from: the modules it imports, transitively, the project's and the libraries' alike (the import lists of
/// <see cref="ImportedNames"/>, the same the symbol index keeps), the submodules an attribute of a package can stand for (<c>xml.dom</c> after
/// <c>import xml</c>), the conftest.py files and plugins of a test module, the modules the type evaluator asks for by name whatever a file imports
/// (builtins, typing, ...) with what they import, and the settings. The modules the analyses looked up by name beyond the project (libraries, stubs, names
/// that were not found) are remembered too and checked again first: when one of them is no longer what it was, nothing is remembered any more. A file whose
/// findings can rest on project files it does not import is always worked out again: one whose own checks read the whole project (Django's URL names,
/// settings, admin and templates, the routes of the web frameworks; models are judged against their migrations), and one that imports Django or a web
/// framework, even through other files (their classes are typed from the whole project).
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>What earlier runs found, shared by a service and the copies it makes for the Problems tab.</summary>
    sealed class ProblemMemory
    {
        public readonly object Gate = new();
        /// <summary>Per document: the key its findings were worked out under, and the findings (of every severity).</summary>
        public readonly Dictionary<string, ((ulong A, ulong B) Key, ProblemEntry[] Entries)> Files = new(StringComparer.Ordinal);
        /// <summary>The modules the analyses looked up by name beyond the project's files, with what was found then (0: nothing).</summary>
        public readonly Dictionary<string, ulong> Resolved = new(StringComparer.Ordinal);

        public void Forget() { Files.Clear(); Resolved.Clear(); }
    }

    ProblemMemory _problemMemory = new();

    /// <summary>Remember the Problems of unchanged files between runs (on by default; off works every file out each time).</summary>
    public bool RememberProblems { get; set; } = true;

    /// <summary>How many files the last run of <see cref="Problems"/> worked out; the others were remembered.</summary>
    public int ProblemsWorkedOut { get; private set; }

    static readonly ConditionalWeakTable<ModuleInfo, StrongBox<ulong>> Identities = new();

    /// <summary>What a module is: its name, where it was found and its text (0 for none). Two modules with the same identity give the same findings.</summary>
    static ulong IdentityOf(ModuleInfo? m) => m == null ? 0 : Identities.GetValue(m, x =>
    {
        var h = Mix(Mix(Mix((0x452821E638D01377UL, 0xBE5466CF34E90C6CUL), NameHash(x.Name)), NameHash(x.Path ?? "")),
            (ulong)x.Kind << 2 | (x.IsPackage ? 2UL : 0) | (x.IsStub ? 1UL : 0));
        var words = MemoryMarshal.Cast<char, ulong>(x.Source.AsSpan());
        foreach (var w in words) h = Mix(h, w);
        for (int i = words.Length * 4; i < x.Source.Length; i++) h = Mix(h, x.Source[i]);
        h = Mix(h, (ulong)x.Source.Length);
        return new StrongBox<ulong>(h.A ^ h.B << 1 | 1);
    }).Value;

    static readonly ConditionalWeakTable<ModuleInfo, string[]> AttributeNames = new();

    /// <summary>The names a module's code reads as attributes (<c>x.name</c>): where the value is a package, the name can be one of its submodules.</summary>
    static string[] AttributeNamesOf(ModuleInfo m) => AttributeNames.GetValue(m, x =>
        AstChildren.Descendants(x.Ast).OfType<AttributeExpr>().Select(a => a.Attr).Distinct(StringComparer.Ordinal).ToArray());

    /// <summary>Modules the type evaluator asks for by name, whatever a file imports.</summary>
    static readonly string[] ImplicitModules = { "builtins", "typing", "typing_extensions", "types", "abc", "collections", "collections.abc", "_typeshed" };

    /// <summary>Text on which a file's own checks look at the whole project: Django (settings, URL names, templates, the admin, migrations) and the web frameworks (routes).</summary>
    static readonly Regex ReadsTheWholeProject = new(@"django|rest_framework|\b(?:fastapi|starlette|flask|quart|jinja2)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool BeyondItsImports(ModuleInfo m) =>
        ReadsTheWholeProject.IsMatch(m.Source) || AppOfModels(m.Name) != null && m.Source.Contains("models", StringComparison.Ordinal);

    /// <summary>Packages whose classes the evaluator types from the whole project (models, settings, dependencies): whatever imports them, even through other files, is worked out again.</summary>
    static readonly HashSet<string> WholeProjectPackages = new(StringComparer.Ordinal) { "django", "rest_framework", "fastapi", "starlette", "flask", "quart", "jinja2" };

    static bool IsWholeProjectPackage(string importedName) => WholeProjectPackages.Contains(importedName.Split('.')[0]);

    /// <summary>pytest may hand the module's functions fixtures from conftest.py files and plugins.</summary>
    static bool MayAskForFixtures(ModuleInfo m) =>
        FixtureModel.IsTestModule(m) || m.Source.Contains("fixture", StringComparison.Ordinal)
        || AstChildren.Descendants(m.Ast).OfType<FunctionDef>().Any(f => FixtureModel.RequestsFixtures(m, f));

    /// <summary>
    /// The modules of the project and those looked up beyond it as one graph of imports, folded into strongly connected parts: what each module's findings
    /// rest on, and the part of the key every file shares.
    /// </summary>
    sealed class ProblemKeyParts
    {
        public required Dictionary<ModuleInfo, int> Index { get; init; }
        public required int[] Part { get; init; }
        public required List<(ulong A, ulong B)> PartHash { get; init; }
        /// <summary>The part reaches Django or a web framework through its imports.</summary>
        public required List<bool> PartFramework { get; init; }
        /// <summary>The module's own checks can read more than its imports.</summary>
        public required bool[] Alone { get; init; }
        /// <summary>What every file rests on: the settings and the modules the evaluator asks for by name, with all they import. Null when those reach a framework.</summary>
        public required (ulong A, ulong B)? Global { get; init; }

        /// <summary>The key of a project module, or null when it is always worked out again.</summary>
        public (ulong A, ulong B)? KeyOf(ModuleInfo m)
        {
            if (Global is not { } g || !Index.TryGetValue(m, out int i) || Alone[i] || PartFramework[Part[i]]) return null;
            var h = PartHash[Part[i]];
            return Mix(Mix(Mix(g, h.A), h.B), IdentityOf(m));
        }
    }

    /// <summary>
    /// The modules the earlier runs looked up by name beyond the project, as they are now; forgets everything remembered when one of them is no longer the same.
    /// </summary>
    Dictionary<string, ModuleInfo?> ModulesFromOutside(Snapshot snap)
    {
        var ws = snap.Workspace;
        var fromOutside = new Dictionary<string, ModuleInfo?>(StringComparer.Ordinal);
        lock (_problemMemory.Gate)
            foreach (var (name, identity) in _problemMemory.Resolved)
            {
                var now = ws.GetModule(name);
                if (IdentityOf(now) != identity) { _problemMemory.Forget(); fromOutside.Clear(); break; }
                fromOutside[name] = now;
            }
        return fromOutside;
    }

    /// <summary>The project's modules and those looked up beyond it (<paramref name="fromOutside"/>) folded into the parts their findings rest on.</summary>
    ProblemKeyParts ProblemKeys(Snapshot snap, Dictionary<string, ModuleInfo?> fromOutside)
    {
        var ws = snap.Workspace;
        // the modules by the name an import finds them under: the project's (the last one registered, as in the workspace), then those found beyond it
        var nodes = new List<ModuleInfo>();
        var index = new Dictionary<ModuleInfo, int>(ReferenceEqualityComparer.Instance);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in snap.Project)
        {
            if (!index.TryGetValue(m, out int i)) { index[m] = i = nodes.Count; nodes.Add(m); }
            byName[m.Name] = i;
        }
        int inProject = nodes.Count;
        var project = new HashSet<string>(byName.Keys, StringComparer.Ordinal);
        foreach (var (name, m) in fromOutside.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (m == null || project.Contains(name)) continue;
            if (!index.TryGetValue(m, out int i)) { index[m] = i = nodes.Count; nodes.Add(m); }
            byName[name] = i;
        }
        int count = nodes.Count;


        var edges = new List<int>[count];
        var outside = new List<ulong>[count];   // names a module imports that nothing was found for (whether something is found for them is checked through Resolved)
        var alone = new bool[count];
        var framework = new bool[count];         // it imports Django or a web framework: so does everything that imports it
        for (int i = 0; i < count; i++)
        {
            var m = nodes[i];
            edges[i] = new List<int>();
            outside[i] = new List<ulong>();
            foreach (var name in ImportedNames(m))
            {
                if (byName.TryGetValue(name, out int j)) edges[i].Add(j); else outside[i].Add(NameHash(name));
                framework[i] |= IsWholeProjectPackage(name);
            }
            if (i >= inProject) continue;   // a library's own checks are not run
            alone[i] = BeyondItsImports(m);
            if (!m.IsStub && MayAskForFixtures(m))
                foreach (var extra in snap.Fixtures.ConftestsOf(ws, m).Concat(snap.Fixtures.PluginsOf(ws, m)))
                {
                    if (index.TryGetValue(extra, out int j)) edges[i].Add(j);
                    else if (extra.Name.StartsWith("__conftest_", StringComparison.Ordinal)) framework[i] = true;   // a conftest.py outside the project: its imports are not followed
                    else { outside[i].Add(NameHash(extra.Name)); outside[i].Add(IdentityOf(extra)); }
                }
        }

        // a package leads to its submodules whose names some code reads as an attribute: `pkg.name` stands for the submodule when the package does not define
        // `name` (the code that reads it is among what the file rests on, and so is the package, since the value was found through its imports)
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in nodes) attributes.UnionWith(AttributeNamesOf(m));
        foreach (var (name, j) in byName)
        {
            int dot = name.LastIndexOf('.');
            if (dot > 0 && attributes.Contains(name[(dot + 1)..]) && byName.TryGetValue(name[..dot], out int parent) && nodes[parent].IsPackage) edges[parent].Add(j);
        }

        // strongly connected parts, the deepest first (Tarjan's order, without recursion): a part's hash covers its modules, the names they import that were
        // not found and the hashes of the parts below it, so it changes when anything a module can reach through its imports changes
        var order = new int[count];
        var low = new int[count];
        var part = new int[count];
        var onStack = new bool[count];
        Array.Fill(order, -1);
        Array.Fill(part, -1);
        var stack = new Stack<int>();
        var work = new Stack<(int Node, int Edge)>();
        var partHash = new List<(ulong A, ulong B)>();
        var partFramework = new List<bool>();
        int counter = 0;
        for (int s = 0; s < count; s++)
        {
            if (order[s] >= 0) continue;
            work.Push((s, 0));
            while (work.Count > 0)
            {
                var (v, e) = work.Pop();
                if (e == 0) { order[v] = low[v] = counter++; stack.Push(v); onStack[v] = true; }
                else low[v] = Math.Min(low[v], low[edges[v][e - 1]]);   // back from the child the previous edge led to
                bool descended = false;
                for (; e < edges[v].Count; e++)
                {
                    int w = edges[v][e];
                    if (order[w] < 0) { work.Push((v, e + 1)); work.Push((w, 0)); descended = true; break; }
                    if (onStack[w]) low[v] = Math.Min(low[v], order[w]);
                }
                if (descended || low[v] != order[v]) continue;

                int id = partHash.Count;
                var members = new List<int>();
                int u;
                do { u = stack.Pop(); onStack[u] = false; part[u] = id; members.Add(u); } while (u != v);
                var identities = new List<ulong>();
                var names = new List<ulong>();
                var below = new List<(ulong A, ulong B)>();
                bool reachesFramework = false;
                foreach (int x in members)
                {
                    identities.Add(IdentityOf(nodes[x]));
                    names.AddRange(outside[x]);
                    reachesFramework |= framework[x];
                    foreach (int w in edges[x])
                        if (part[w] != id) { below.Add(partHash[part[w]]); reachesFramework |= partFramework[part[w]]; }
                }
                identities.Sort(); names.Sort(); below.Sort();
                var h = Mix(Mix((0x243F6A8885A308D3UL, 0x13198A2E03707344UL), (ulong)members.Count), (ulong)names.Count);
                foreach (var x in identities) h = Mix(h, x);
                foreach (var x in names) h = Mix(h, x);
                (ulong, ulong)? previous = null;
                foreach (var b in below)
                {
                    if (previous == b) continue;
                    previous = b;
                    h = Mix(Mix(h, b.A), b.B);
                }
                partHash.Add(h);
                partFramework.Add(reachesFramework);
            }
        }

        // the settings, and what Django's checks look for in any file: the URL names of the open files and the project's templates
        string settings = string.Join("\n", ReportTypeMismatches, ReportUnresolvedImports, Interpreter?.Path, Interpreter?.Version, Interpreter?.Platform, Environment,
            TypeshedPath, string.Join("|", SourceRoots.Concat(ImplicitSourceRoots())), string.Join("|", EffectiveLibraryRoots()),
            string.Join("|", InspectionProfile().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)),
            string.Join("|", DjangoIndexOf(Current()).Urls.Select(u => u.Qualified).Order(StringComparer.Ordinal)),
            string.Join("|", Templates().Select(t => t.Name).Order(StringComparer.Ordinal)));
        // what every file rests on: the settings and the parts of the modules the evaluator asks for by name (a name not found counts by its name)
        (ulong A, ulong B)? global = Mix((0xA4093822299F31D0UL, 0x082EFA98EC4E6C89UL), NameHash(settings));
        foreach (var name in ImplicitModules)
        {
            if (!byName.TryGetValue(name, out int j)) { global = Mix(global.Value, NameHash(name)); continue; }
            if (partFramework[part[j]]) { global = null; break; }
            global = Mix(Mix(global.Value, partHash[part[j]].A), partHash[part[j]].B);
        }
        return new ProblemKeyParts { Index = index, Part = part, PartHash = partHash, PartFramework = partFramework, Alone = alone, Global = global };
    }

    /// <summary>
    /// Keeps the findings of this run under keys that also cover the modules its analyses looked up by name (with those of the earlier runs), so that the next
    /// run finds them; forgets the documents that are gone, and everything when a module looked up by name changed during the run.
    /// </summary>
    void RememberProblemsOf(Snapshot snap, Dictionary<string, ModuleInfo?> fromOutside, List<(ModuleInfo Module, string Uri, List<ProblemEntry> Entries)> found,
        IEnumerable<(string Name, ModuleInfo? Module)> resolved)
    {
        var project = new HashSet<ModuleInfo>(snap.Project, ReferenceEqualityComparer.Instance);
        lock (_problemMemory.Gate)
        {
            foreach (var (name, module) in resolved)
            {
                if (module != null && project.Contains(module)) continue;
                ulong identity = IdentityOf(module);
                if (_problemMemory.Resolved.TryGetValue(name, out ulong known) && known != identity) { _problemMemory.Forget(); return; }   // it changed during the run
                _problemMemory.Resolved[name] = identity;
                fromOutside[name] = module;
            }
        }
        // the keys as the next run will make them, from all the modules looked up so far
        var parts = ProblemKeys(snap, fromOutside);
        lock (_problemMemory.Gate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (module, uri, entries) in found)
            {
                present.Add(uri);
                if (parts.KeyOf(module) is { } key) _problemMemory.Files[uri] = (key, entries.ToArray());
                else _problemMemory.Files.Remove(uri);
            }
            foreach (var gone in _problemMemory.Files.Keys.Where(u => !present.Contains(u)).ToList()) _problemMemory.Files.Remove(gone);
        }
    }

    static ulong NameHash(string s)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (char c in s) h = unchecked((h ^ c) * 0x100000001B3UL);
        return h;
    }

    /// <summary>Folds one value into a hash of two independent halves (each step a bijective mix, so the order of the values counts).</summary>
    static (ulong A, ulong B) Mix((ulong A, ulong B) h, ulong v)
    {
        unchecked
        {
            ulong a = h.A ^ v;
            a = (a ^ (a >> 30)) * 0xBF58476D1CE4E5B9UL;
            a = (a ^ (a >> 27)) * 0x94D049BB133111EBUL;
            a ^= a >> 31;
            ulong b = h.B + v + 0x9E3779B97F4A7C15UL;
            b = (b ^ (b >> 33)) * 0xFF51AFD7ED558CCDUL;
            b = (b ^ (b >> 33)) * 0xC4CEB9FE1A85EC53UL;
            b ^= b >> 33;
            return (a, b);
        }
    }
}
