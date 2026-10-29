using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

public sealed partial class LanguageService
{
    /// <summary>One place that asks for a fixture by name: a parameter of a test or fixture, or a `usefixtures("name")` / `getfixturevalue("name")` literal.</summary>
    sealed record FixtureUse(ModuleInfo Module, string Name, int Start, int End, FunctionDef? Function, Node? Context, Parameter? Param, ConstExpr? Literal);

    /// <summary>What the cursor is on: a fixture definition or a request for one (Def is null for pytest's own fixtures).</summary>
    sealed record FixtureTarget(FixtureDef? Def, FixtureUse? Use, BuiltinFixture? Builtin, (int Start, int End) Token, ModuleInfo Module);

    readonly ConditionalWeakTable<Workspace, FixtureContext> _fixtureContexts = new();

    FixtureContext? FixturesOf(Workspace ws) => _fixtureContexts.TryGetValue(ws, out var c) ? c : null;

    // ------------------------------------------------------------ finding uses and definitions

    static (int Start, int End) ParamNameSpan(ModuleInfo m, Parameter p)
    {
        int i = m.Source.IndexOf(p.Name, p.Start, StringComparison.Ordinal);
        return i >= 0 && i < p.End ? (i, i + p.Name.Length) : (p.Start, p.Start + p.Name.Length);
    }

    static IEnumerable<FixtureUse> FixtureUsesIn(ModuleInfo m)
    {
        if (m.IsStub) yield break;
        foreach (var f in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
            foreach (var p in FixtureModel.RequestedParameters(m, f))
            {
                var (s, e) = ParamNameSpan(m, p);
                yield return new FixtureUse(m, p.Name, s, e, f, f, p, null);
            }
        foreach (var u in FixtureModel.StringRequests(m))
            if (FixtureModel.StringSpan(u.Literal) is { } span)
                yield return new FixtureUse(m, u.Name, span.Start, span.End, u.Context as FunctionDef, u.Context, null, u.Literal);
    }

    static IEnumerable<FixtureDef> FixtureDefsIn(ModuleInfo m)
    {
        if (m.IsStub) yield break;
        foreach (var f in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
            if (FixtureModel.Parse(m, f) is { } fx) yield return fx;
    }

    /// <summary>The span that names a fixture where it is defined: the function name, or the `name="..."` string.</summary>
    static (int Start, int End) DefinitionSpan(FixtureDef d) =>
        d.NameArg != null && FixtureModel.StringSpan(d.NameArg) is { } s ? s : (d.Def.NameStart, d.Def.NameStart + d.Def.Name.Length);

    FixtureResolution ResolveUse(Snapshot snap, FixtureUse use) =>
        snap.Fixtures.Resolver.Resolve(snap.Workspace, use.Module, use.Context, use.Name,
            use.Param != null && use.Function != null && FixtureModel.IsFixture(use.Function) ? use.Function : null);

    FixtureTarget? FixtureTargetAt(Snapshot snap, ModuleInfo m, int offset)
    {
        foreach (var def in FixtureDefsIn(m))
        {
            var span = DefinitionSpan(def);
            if (offset >= span.Start && offset <= span.End) return new FixtureTarget(def, null, null, span, m);
        }
        foreach (var use in FixtureUsesIn(m))
        {
            if (offset < use.Start || offset > use.End) continue;
            var res = ResolveUse(snap, use);
            return res.Found ? new FixtureTarget(res.Def, use, res.Builtin, (use.Start, use.End), m) : null;
        }
        return null;
    }

    List<FixtureUse> UsesOf(Snapshot snap, FixtureDef def)
    {
        var uses = new List<FixtureUse>();
        foreach (var module in snap.Project)
            foreach (var use in FixtureUsesIn(module))
                if (use.Name == def.Name && ResolveUse(snap, use).Def is { } d && ReferenceEquals(d.Def, def.Def)) uses.Add(use);
        return uses;
    }

    Location? FixtureLocation(ModuleInfo m, int start, int end) =>
        UriOfModule(m) is { } uri ? new Location(uri, Lines(m).RangeOf(start, end)) : null;

    // ------------------------------------------------------------ go to definition, usages

    IReadOnlyList<Location> FixtureDefinition(string uri, Position pos)
    {
        var snap = Current();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<Location>();
        int offset = Lines(m).OffsetOf(pos);
        var use = FixtureUsesIn(m).FirstOrDefault(u => offset >= u.Start && offset <= u.End);
        if (use == null || ResolveUse(snap, use).Def is not { } def) return Array.Empty<Location>();
        var (s, e) = DefinitionSpan(def);
        return FixtureLocation(def.Module, s, e) is { } loc ? new[] { loc } : Array.Empty<Location>();
    }

    IReadOnlyList<Location> FixtureReferences(Snapshot snap, ModuleInfo m, int offset, FixtureTarget target, bool includeDeclaration,
        IReadOnlyList<Location> bySymbol)
    {
        var result = new List<Location>();
        var seen = new HashSet<(string, int, int)>();
        void Add(Location? l)
        {
            if (l != null && seen.Add((l.Uri, l.Range.Start.Line, l.Range.Start.Character))) result.Add(l);
        }
        var def = target.Def!;
        if (includeDeclaration) { var (s, e) = DefinitionSpan(def); Add(FixtureLocation(def.Module, s, e)); }
        foreach (var use in UsesOf(snap, def)) Add(FixtureLocation(use.Module, use.Start, use.End));
        // the cursor on the function's own name also finds the ordinary references: imports of it, direct calls
        if (target.Use == null && def.NameArg == null) foreach (var l in bySymbol) Add(l);
        return result.OrderBy(l => l.Uri, StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line).ThenBy(l => l.Range.Start.Character).ToList();
    }

    // ------------------------------------------------------------ rename

    PrepareRenameResult? FixturePrepareRename(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return null;
        var target = FixtureTargetAt(snap, m, Lines(m).OffsetOf(pos));
        if (target == null) return null;
        if (target.Def == null) throw new ServiceException($"'{target.Builtin!.Name}' is a fixture of pytest itself and cannot be renamed");
        return new PrepareRenameResult(Lines(m).RangeOf(target.Token.Start, target.Token.End), target.Def.Name);
    }

    WorkspaceEdit RenameFixture(Snapshot snap, FixtureTarget target, string newName)
    {
        if (!Regex.IsMatch(newName, @"^[\p{L}_][\p{L}\p{N}_]*$") || Lexer.Keywords.Contains(newName))
            throw new ServiceException($"'{newName}' is not a valid identifier");
        var def = target.Def ?? throw new ServiceException($"'{target.Builtin!.Name}' is a fixture of pytest itself and cannot be renamed");
        if (!snap.Project.Contains(def.Module)) throw new ServiceException($"'{def.Name}' is defined in a library and cannot be renamed");

        var changes = new Dictionary<string, List<TextEdit>>();
        var seen = new HashSet<(string, int, int)>();
        void Edit(ModuleInfo m, int start, int end)
        {
            var uri = UriOfModule(m) ?? throw new ServiceException("a usage lies in a file that cannot be edited");
            var range = Lines(m).RangeOf(start, end);
            if (!seen.Add((uri, range.Start.Line, range.Start.Character))) return;
            if (!changes.TryGetValue(uri, out var list)) changes[uri] = list = new List<TextEdit>();
            list.Add(new TextEdit(range, newName));
        }

        // the definition: the function (with its imports and calls, as an ordinary rename), or the name="..." literal
        if (def.NameArg != null)
        {
            var (s, e) = DefinitionSpan(def);
            Edit(def.Module, s, e);
        }
        else
        {
            var defUri = UriOfModule(def.Module) ?? throw new ServiceException("the fixture lies in a file that cannot be edited");
            foreach (var (uri, edits) in RenameSymbol(defUri, Lines(def.Module).PositionOf(def.Def.NameStart), newName).Changes)
            {
                if (!changes.TryGetValue(uri, out var list)) changes[uri] = list = new List<TextEdit>();
                foreach (var ed in edits)
                    if (seen.Add((uri, ed.Range.Start.Line, ed.Range.Start.Character))) list.Add(ed);
            }
        }

        // the requests: the parameter, and every use of it inside the function
        foreach (var use in UsesOf(snap, def))
        {
            if (use.Param == null) { Edit(use.Module, use.Start, use.End); continue; }
            var scope = use.Module.Semantics.ScopeOf(use.Function!);
            if (scope != null && scope.Symbols.TryGetValue(newName, out var clash) && clash.Declarations.Count > 0)
                throw new ServiceException($"'{newName}' is already defined in {use.Function!.Name}");
            Edit(use.Module, use.Start, use.End);
            if (scope != null && scope.Symbols.TryGetValue(use.Name, out var sym))
                foreach (var r in (sym.Redirect ?? sym).References) Edit(use.Module, r.Node.Start, r.Node.End);
        }
        return new WorkspaceEdit(changes);
    }

    // ------------------------------------------------------------ hover

    /// <summary>True when a plugin module brings this fixture by importing it (`from .fixtures import db` in pytest_django.plugin).</summary>
    static bool DefinesThrough(Snapshot snap, ModuleInfo plugin, FixtureDef def) =>
        snap.Fixtures.Resolver.DefinedIn(snap.Workspace, plugin, null).Any(f => ReferenceEquals(f.Def, def.Def));

    Hover? FixtureHover(Snapshot snap, ModuleInfo m, int offset)
    {
        var target = FixtureTargetAt(snap, m, offset);
        if (target == null) return null;
        var ev = snap.Workspace.Evaluator;
        var range = Lines(m).RangeOf(target.Token.Start, target.Token.End);
        string name = target.Def?.Name ?? target.Builtin!.Name;

        string header = $"(fixture) {name}";
        var details = new List<string>();
        if (target.Def is { } def)
        {
            var value = ev.FixtureValueType(def.Module, def.Def);
            if (value is not UnknownType) header += ": " + FormatType(ev, value);
            details.Add($"scope: `{def.Scope}`" + (def.Autouse ? ", autouse" : ""));
            string file = UriOfModule(def.Module) is { } u ? System.IO.Path.GetFileName(PathOfUri(u) ?? u) : def.Module.Name;
            details.Add($"defined in `{file}:{Lines(def.Module).PositionOf(def.Def.NameStart).Line + 1}`");
            // a fixture of an installed plugin, or of a module pytest_plugins names
            if (snap.Fixtures.Resolver.PluginsOf(snap.Workspace, m).FirstOrDefault(p => ReferenceEquals(p, def.Module) || DefinesThrough(snap, p, def)) is { } plugin)
                details.Add($"from the pytest plugin `{plugin.Name}`");
            if (FirstParagraphOfDocstring(def.Def) is { } doc) details.Add(doc);
        }
        else
        {
            var b = target.Builtin!;
            if (b.Module != null && b.Class != null && ev.InstanceOf(b.Module, b.Class) is { } t) header += ": " + FormatType(ev, t);
            details.Add($"scope: `{b.Scope}`, provided by pytest");
            details.Add(b.Description);
        }
        return new Hover(new MarkupContent("markdown", "```python\n" + header + "\n```\n\n" + string.Join("\n\n", details)), range);
    }

    static string? FirstParagraphOfDocstring(FunctionDef f)
    {
        if (f.Body.Count == 0 || f.Body[0] is not ExprStmt { Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } c }) return null;
        var m = Regex.Match(c.Text, "^[rRuU]?(\"\"\"|'''|\"|')(?<b>.*)\\1$", RegexOptions.Singleline);
        if (!m.Success) return null;
        string first = Regex.Split(m.Groups["b"].Value.Trim(), @"\r?\n\s*\r?\n")[0];
        first = Regex.Replace(first, @"\s*\r?\n\s*", " ").Trim();
        return first.Length == 0 ? null : first.Length > 300 ? first[..300] + "..." : first;
    }

    // ------------------------------------------------------------ completion

    static readonly Regex FixtureStringContext = new(@"(usefixtures|getfixturevalue)\s*\((?:[^()]*,\s*)?(?<q>[""'])(?<p>\w*)$", RegexOptions.Compiled);
    static readonly Regex DefHeader = new(@"^\s*(async\s+)?def\s+(?<n>\w+)\s*$", RegexOptions.Compiled);
    static readonly Regex FixtureDecoratorLine = new(@"^\s*@\s*([\w.]+\.)?(yield_)?fixture\b", RegexOptions.Compiled);

    /// <summary>Fixture names where one is expected: the parameters of a test or fixture, and the literal of usefixtures / getfixturevalue.</summary>
    IReadOnlyList<CompletionItem>? FixtureCompletion(Doc doc, int start, int offset, string prefix)
    {
        string text = doc.Text;
        var snap = Current();
        if (!snap.ByUri.TryGetValue(doc.Uri, out var m)) return null;

        int lineStart = LineStart(text, offset);
        var literal = FixtureStringContext.Match(text[lineStart..offset]);
        var exclude = new HashSet<string>(StringComparer.Ordinal);
        if (literal.Success) prefix = literal.Groups["p"].Value;
        else
        {
            // the innermost bracket that is still open must be the parameter list of a test or fixture
            int depth = 0, open = -1;
            for (int i = start - 1; i >= 0 && start - i < 4000; i--)
            {
                char c = text[i];
                if (c is ')' or ']' or '}') depth++;
                else if (c is '(' or '[' or '{')
                {
                    if (depth == 0) { open = i; break; }
                    depth--;
                }
            }
            if (open < 0 || text[open] != '(') return null;
            var header = DefHeader.Match(text[LineStart(text, open)..open]);
            if (!header.Success) return null;
            int before = start - 1;
            while (before > open && char.IsWhiteSpace(text[before])) before--;
            if (before != open && text[before] != ',') return null;   // an annotation or a default, not a parameter name

            bool asksForFixtures = header.Groups["n"].Value.StartsWith("test") && FixtureModel.IsTestModule(m);
            for (int ln = LineStart(text, open); !asksForFixtures && ln > 0;)
            {
                int prevEnd = ln - 1;
                while (prevEnd > 0 && text[prevEnd - 1] is '\n' or '\r') prevEnd--;
                int prevStart = LineStart(text, Math.Max(prevEnd - 1, 0));
                string line = text[prevStart..prevEnd];
                if (!line.TrimStart().StartsWith('@')) break;
                asksForFixtures = FixtureDecoratorLine.IsMatch(line);
                ln = prevStart;
            }
            if (!asksForFixtures) return null;

            // what the parameter list already names
            int close = open + 1, d2 = 0;
            for (; close < text.Length && close - open < 4000; close++)
            {
                char c = text[close];
                if (c is '(' or '[' or '{') d2++;
                else if (c is ')' or ']' or '}') { if (d2 == 0) break; d2--; }
            }
            foreach (var part in text[(open + 1)..Math.Min(close, text.Length)].Split(','))
            {
                var name = Regex.Match(part, @"^\s*\*{0,2}\s*(\w+)");
                if (name.Success) exclude.Add(name.Groups[1].Value);
            }
            exclude.Remove(prefix);
        }

        var ctx = snap.Fixtures;
        var items = new List<CompletionItem>();
        foreach (var res in ctx.Resolver.Visible(snap.Workspace, m, null))
        {
            string name = res.Def?.Name ?? res.Builtin!.Name;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || exclude.Contains(name) || name.StartsWith('_')) continue;
            string detail = res.Def is { } d
                ? $"fixture · {d.Scope} · {(UriOfModule(d.Module) is { } u ? System.IO.Path.GetFileName(PathOfUri(u) ?? u) : d.Module.Name)}"
                : $"fixture · {res.Builtin!.Scope} · pytest";
            items.Add(new CompletionItem(name, CompletionKind.Variable, detail, (res.Def != null ? "0" : "1") + name));
        }
        return items;
    }

    // ------------------------------------------------------------ inspection: a fixture nobody defines

    IEnumerable<Diag> FixtureDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || FixturesOf(ev.Workspace) is not { } ctx) yield break;
        if (!FixtureModel.IsTestModule(m) && !AstChildren.Descendants(m.Ast).OfType<FunctionDef>().Any(FixtureModel.IsFixture)) yield break;
        var ws = ev.Workspace;
        bool pytestAround = m.Source.Contains("pytest") || ctx.ConftestsOf(ws, m).Count > 0;
        bool dynamic(string marker) => m.Source.Contains(marker) || ctx.ConftestsOf(ws, m).Any(c => c.Source.Contains(marker));
        // pytest_generate_tests can provide any name, and so can a pytest_plugins whose modules cannot be read
        bool pluginsKnown = new[] { m }.Concat(ctx.ConftestsOf(ws, m))
            .All(src => FixtureModel.PytestPluginsReadable(src) && (FixtureModel.PytestPluginsOf(src) ?? new List<string>()).All(n => ws.GetModule(n) != null));
        if (!pytestAround || !pluginsKnown || dynamic("pytest_generate_tests")) yield break;
        var parametrized = FixtureModel.AllParametrizedNames(m);

        foreach (var f in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            if (FixtureModel.HasArgumentSupplyingDecorator(m, f) || !ctx.Resolver.IsClosedWorld(ws, m, f)) continue;
            foreach (var p in FixtureModel.RequestedParameters(m, f))
            {
                if (FixtureModel.IsPluginFixture(p.Name) || FixtureModel.IsFixture(f) && parametrized.Contains(p.Name)) continue;
                var res = ctx.Resolver.Resolve(ws, m, f, p.Name, FixtureModel.IsFixture(f) ? f : null);
                if (res.Found) continue;
                var (s, e) = ParamNameSpan(m, p);
                var finding = new Finding(s, e - s, $"Fixture '{p.Name}' not found", "fixture-not-found", FindingLevel.Weak);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        }
    }

    /// <summary>`@pytest.fixture def name(): ...` above the test that asks for it (and `import pytest` when the module lacks it).</summary>
    void CreateFixtureFix(ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(d => FixtureModel.RequestedParameters(m, d).Any(p => ParamNameSpan(m, p).Start == start));
        var p = f == null ? null : FixtureModel.RequestedParameters(m, f).First(x => ParamNameSpan(m, x).Start == start);
        if (f == null || p == null) return;

        Node top = f;
        for (var parent = m.ParentOf(top); parent != null && parent is not PyMagic.Core.Syntax.Module; parent = m.ParentOf(top)) top = parent;
        string text = m.Source;
        int at = LineStart(text, top.Start);
        string fixture = $"@pytest.fixture\ndef {p.Name}():\n    raise NotImplementedError\n\n\n";
        bool hasPytest = m.Semantics.ModuleScope.Symbols.TryGetValue("pytest", out var sym) && sym.Declarations.Count > 0;
        var import = hasPytest ? null : ImportEdit(m, null, "pytest");
        // an import that lands at the same offset is joined to it: two insertions at one place have no dependable order
        var edits = import == null ? new[] { new Edit(at, at, fixture) }
            : import.Start == at ? new[] { new Edit(at, at, import.Text + fixture) }
            : new[] { import, new Edit(at, at, fixture) };
        add($"Create fixture '{p.Name}'", true, edits);
    }
}
