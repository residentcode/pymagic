using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server.Pytest;

/// <summary>A function marked `@pytest.fixture`. <paramref name="NameArg"/> is the `name="..."` literal when the fixture is registered under another name.</summary>
internal sealed record FixtureDef(string Name, FunctionDef Def, ModuleInfo Module, string Scope, bool Autouse, ConstExpr? NameArg);

/// <summary>A fixture pytest itself provides.</summary>
internal sealed record BuiltinFixture(string Name, string Scope, string Description, string? Module = null, string? Class = null, bool StrArgument = false);

/// <summary>A fixture asked for by a literal: `usefixtures("db")` or `request.getfixturevalue("db")`.</summary>
internal sealed record NamedFixtureUse(string Name, ConstExpr Literal, Node? Context);

/// <summary>How pytest finds fixtures and the functions that ask for them, read off the syntax.</summary>
internal static class FixtureModel
{
    public static readonly IReadOnlyDictionary<string, BuiltinFixture> Builtins = new[]
    {
        new BuiltinFixture("tmp_path", "function", "A temporary directory unique to the test, as a pathlib.Path.", "pathlib", "Path"),
        new BuiltinFixture("tmp_path_factory", "session", "Makes temporary directories for session-scoped fixtures.", "_pytest.tmpdir", "TempPathFactory"),
        new BuiltinFixture("tmpdir", "function", "A temporary directory unique to the test, as a py.path.local (legacy: prefer tmp_path)."),
        new BuiltinFixture("tmpdir_factory", "session", "Makes temporary directories, as py.path.local (legacy)."),
        new BuiltinFixture("monkeypatch", "function", "Patches attributes, dictionaries and the environment for the length of the test.", "_pytest.monkeypatch", "MonkeyPatch"),
        new BuiltinFixture("capsys", "function", "Captures text written to sys.stdout and sys.stderr.", "_pytest.capture", "CaptureFixture", true),
        new BuiltinFixture("capsysbinary", "function", "Captures bytes written to sys.stdout and sys.stderr."),
        new BuiltinFixture("capfd", "function", "Captures text written to file descriptors 1 and 2.", "_pytest.capture", "CaptureFixture", true),
        new BuiltinFixture("capfdbinary", "function", "Captures bytes written to file descriptors 1 and 2."),
        new BuiltinFixture("caplog", "function", "Gives access to and controls log capturing.", "_pytest.logging", "LogCaptureFixture"),
        new BuiltinFixture("request", "function", "Information about the requesting test and the fixture being set up.", "_pytest.fixtures", "FixtureRequest"),
        new BuiltinFixture("pytestconfig", "session", "The pytest config object.", "_pytest.config", "Config"),
        new BuiltinFixture("recwarn", "function", "Records the warnings raised by the test.", "_pytest.recwarn", "WarningsRecorder"),
        new BuiltinFixture("cache", "session", "Stores values between test runs.", "_pytest.cacheprovider", "Cache"),
        new BuiltinFixture("doctest_namespace", "session", "A dict injected into the namespace of doctests."),
        new BuiltinFixture("record_property", "function", "Adds extra properties to the test report."),
        new BuiltinFixture("record_xml_attribute", "function", "Adds an extra attribute to the test's XML element."),
        new BuiltinFixture("record_testsuite_property", "session", "Adds a property to the test suite's XML."),
        new BuiltinFixture("pytester", "function", "Runs and inspects pytest runs (the pytester plugin).", "_pytest.pytester", "Pytester"),
        new BuiltinFixture("testdir", "function", "Legacy form of pytester."),
        new BuiltinFixture("subtests", "function", "Reports several results from one test."),
    }.ToDictionary(b => b.Name);

    /// <summary>Fixtures that popular pytest plugins register; a request for one of them is not reported as unknown.</summary>
    static readonly HashSet<string> PluginFixtures = new(StringComparer.Ordinal)
    {
        "mocker", "mock", "class_mocker", "module_mocker", "session_mocker", "client", "async_client", "aiohttp_client", "aiohttp_server",
        "db", "transactional_db", "django_db_setup", "django_db_blocker", "settings", "rf", "admin_client", "admin_user", "live_server",
        "django_user_model", "django_username_field", "django_assert_num_queries", "django_assert_max_num_queries",
        "event_loop", "event_loop_policy", "anyio_backend", "faker", "freezer", "freezegun", "httpx_mock", "requests_mock", "responses",
        "benchmark", "snapshot", "vcr", "cov", "mongodb", "redis", "postgresql", "app", "tornado_application", "http_client", "base_url",
        "qapp", "qtbot", "qtmodeltester", "page", "browser", "context", "playwright", "selenium", "capfdbinary", "monkeymodule", "xdist_worker", "worker_id", "testrun_uid",
        "caplog_handler", "unused_tcp_port", "unused_tcp_port_factory", "loop", "aiohttp_unused_port", "pytest_httpserver", "httpserver",
    };

    public static bool IsPluginFixture(string name) => PluginFixtures.Contains(name) || name.StartsWith("django_") || name.StartsWith("pytest_");

    // ------------------------------------------------------------ literals

    static readonly Regex PlainString = new("^([rRuU]?)(\"\"\"|'''|\"|')(?<body>.*)\\2$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The text of a plain (non-f, non-bytes, not implicitly concatenated) string literal.</summary>
    public static string? StringValue(Expr? e)
    {
        if (e is not ConstExpr { Kind: ConstKind.Str, IsFString: false } c) return null;
        var m = PlainString.Match(c.Text);
        if (m.Success)
        {
            string body = m.Groups["body"].Value;
            if (!body.Contains('\\') && !body.Contains(m.Groups[2].Value)) return body;
        }
        // adjacent literals ('a,' 'b') are one string
        if (!Concatenated.IsMatch(c.Text)) return null;
        return string.Concat(Literal.Matches(c.Text).Select(x => x.Groups["b"].Value));
    }

    static readonly Regex Concatenated = new(@"^(?:[rRuU]?(?:""[^""\\\r\n]*""|'[^'\\\r\n]*')\s*)+$", RegexOptions.Compiled);
    static readonly Regex Literal = new(@"[rRuU]?(?:""(?<b>[^""\\\r\n]*)""|'(?<b>[^'\\\r\n]*)')", RegexOptions.Compiled);

    /// <summary>The offsets of what is between the quotes of a literal that <see cref="StringValue"/> accepts.</summary>
    public static (int Start, int End)? StringSpan(ConstExpr c)
    {
        var m = PlainString.Match(c.Text);
        if (!m.Success || m.Groups["body"].Value.Contains(m.Groups[2].Value) || m.Groups["body"].Value.Contains('\\')) return null;
        var body = m.Groups["body"];
        return (c.Start + body.Index, c.Start + body.Index + body.Length);
    }

    // ------------------------------------------------------------ fixture definitions

    static Expr Callee(Expr d) => d is CallExpr call ? call.Func : d;

    static bool IsFixtureCallee(Expr e) => e is NameExpr { Name: "fixture" or "yield_fixture" } || e is AttributeExpr { Attr: "fixture" or "yield_fixture" };

    public static bool IsFixture(FunctionDef f) => f.Decorators.Any(d => IsFixtureCallee(Callee(d)));

    /// <summary>The fixture a function defines: its registered name, scope and autouse flag, or null for an ordinary function.</summary>
    public static FixtureDef? Parse(ModuleInfo m, FunctionDef f)
    {
        foreach (var d in f.Decorators)
        {
            if (!IsFixtureCallee(Callee(d))) continue;
            string name = f.Name, scope = "function";
            bool autouse = false;
            ConstExpr? nameArg = null;
            if (d is CallExpr call)
            {
                if (call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0) is { } positional && StringValue(positional.Value) is { } s0) scope = s0;
                foreach (var a in call.Args)
                {
                    switch (a.Name)
                    {
                        case "name" when StringValue(a.Value) is { } n && a.Value is ConstExpr lit: name = n; nameArg = lit; break;
                        case "scope" when StringValue(a.Value) is { } sc: scope = sc; break;
                        case "autouse" when a.Value is ConstExpr { Kind: ConstKind.True }: autouse = true; break;
                    }
                }
            }
            return new FixtureDef(name, f, m, scope, autouse, nameArg);
        }
        return null;
    }

    // ------------------------------------------------------------ who asks for fixtures

    /// <summary>pytest collects test_*.py and *_test.py.</summary>
    public static bool IsTestModule(ModuleInfo m)
    {
        string last = m.Name.Contains('.') ? m.Name[(m.Name.LastIndexOf('.') + 1)..] : m.Name;
        return last.StartsWith("test_") || last.EndsWith("_test");
    }

    static bool IsTestClass(ClassDef c) =>
        c.Name.StartsWith("Test") && !c.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")
        && !c.Bases.Any(b => b.Name == null && b.Value is NameExpr { Name: "TestCase" } or AttributeExpr { Attr: "TestCase" });

    static ClassDef? EnclosingClass(ModuleInfo m, Node n)
    {
        for (var p = m.ParentOf(n); p != null; p = m.ParentOf(p))
        {
            if (p is ClassDef c) return c;
            if (p is FunctionDef or LambdaExpr) return null;
        }
        return null;
    }

    public static bool IsTestFunction(ModuleInfo m, FunctionDef f)
    {
        if (!f.Name.StartsWith("test") || !IsTestModule(m)) return false;
        var cls = EnclosingClass(m, f);
        return cls == null ? m.ParentOf(f) is Core.Syntax.Module : IsTestClass(cls);
    }

    /// <summary>Tests and fixtures ask for fixtures by naming parameters.</summary>
    public static bool RequestsFixtures(ModuleInfo m, FunctionDef f) => IsFixture(f) || IsTestFunction(m, f);

    static string CalleeText(ModuleInfo m, Expr e) => m.Source[e.Start..e.End];

    static IEnumerable<ClassDef> ClassesAround(ModuleInfo m, Node n)
    {
        for (var p = m.ParentOf(n); p != null; p = m.ParentOf(p))
        {
            if (p is ClassDef c) yield return c;
            else if (p is FunctionDef or LambdaExpr) yield break;
        }
    }

    static readonly Regex KnownDecorator = new(@"^(pytest\.|_pytest\.)?(mark\.|fixture|yield_fixture)|^pytest_asyncio\.|^(staticmethod|classmethod|property)$|(^|\.)patch(\.object|\.dict|\.multiple)?$", RegexOptions.Compiled);

    /// <summary>A decorator that is neither pytest's nor a plain mock patch (hypothesis's `@given`, say) may supply arguments of its own.</summary>
    public static bool HasArgumentSupplyingDecorator(ModuleInfo m, FunctionDef f) =>
        f.Decorators.Any(d => !KnownDecorator.IsMatch(CalleeText(m, d is CallExpr c ? c.Func : d)));

    /// <summary>`@pytest.mark.parametrize("a, b", ...)` names arguments, not fixtures.</summary>
    static HashSet<string> ParametrizedNames(ModuleInfo m, FunctionDef f)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // a decorator on the class applies to every method of it
        var decorators = f.Decorators.Concat(ClassesAround(m, f).SelectMany(c => c.Decorators));
        foreach (var d in decorators)
        {
            if (d is not CallExpr call || !CalleeText(m, call.Func).EndsWith("parametrize")) continue;
            var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "argnames")?.Value;
            if (StringValue(arg) is { } list)
                foreach (var n in list.Split(',')) names.Add(n.Trim());
            else if (arg is ListExpr l) foreach (var i in l.Items) { if (StringValue(i) is { } s) names.Add(s); }
            else if (arg is TupleExpr t) foreach (var i in t.Items) { if (StringValue(i) is { } s) names.Add(s); }
        }
        return names;
    }

    /// <summary>Every argument name any `parametrize(...)` of the module mentions. A fixture may ask for such a name: the tests parametrized with it provide it.</summary>
    public static HashSet<string> AllParametrizedNames(ModuleInfo m)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not CallExpr call || !CalleeText(m, call.Func).EndsWith("parametrize")) continue;
            var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "argnames")?.Value;
            if (StringValue(arg) is { } list)
                foreach (var part in list.Split(',')) names.Add(part.Trim());
            else if (arg is ListExpr l) foreach (var i in l.Items) { if (StringValue(i) is { } s) names.Add(s); }
            else if (arg is TupleExpr t) foreach (var i in t.Items) { if (StringValue(i) is { } s) names.Add(s); }
        }
        return names;
    }

    /// <summary>`@mock.patch("x")` hands its mock to the first parameters; those are not fixtures either.</summary>
    static int PatchInjected(ModuleInfo m, FunctionDef f)
    {
        int count = 0;
        foreach (var d in f.Decorators)
        {
            if (d is not CallExpr call) continue;
            string callee = CalleeText(m, call.Func);
            bool isObject = Regex.IsMatch(callee, @"(^|\.)patch\.object$");
            if (!isObject && !Regex.IsMatch(callee, @"(^|\.)patch$")) continue;
            int positional = call.Args.Count(a => a.Name == null && a.Stars == 0);
            if (positional == (isObject ? 2 : 1) && !call.Args.Any(a => a.Name == "new")) count++;
        }
        return count;
    }

    /// <summary>The parameters of a test or fixture that name fixtures.</summary>
    public static List<Parameter> RequestedParameters(ModuleInfo m, FunctionDef f)
    {
        var result = new List<Parameter>();
        if (!RequestsFixtures(m, f)) return result;
        var parametrized = ParametrizedNames(m, f);
        int skip = PatchInjected(m, f);
        bool isMethod = EnclosingClass(m, f) != null && !f.Decorators.Any(d => d is NameExpr { Name: "staticmethod" });
        bool first = true;
        foreach (var p in f.Params)
        {
            if (p.Kind != ParamKind.Normal) { if (p.Kind is ParamKind.VarArgs or ParamKind.KwArgs) break; continue; }
            bool isReceiver = first && isMethod;
            first = false;
            if (isReceiver || p.Default != null || parametrized.Contains(p.Name)) continue;
            if (skip > 0) { skip--; continue; }
            result.Add(p);
        }
        return result;
    }

    /// <summary>Every `usefixtures("name")` and `getfixturevalue("name")` literal of a module, with the node whose scope the name is looked up in.</summary>
    public static List<NamedFixtureUse> StringRequests(ModuleInfo m)
    {
        var uses = new List<NamedFixtureUse>();
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not CallExpr call) continue;
            bool usefixtures = CalleeText(m, call.Func).EndsWith("usefixtures");
            bool lookup = call.Func is AttributeExpr { Attr: "getfixturevalue" };
            if (!usefixtures && !lookup) continue;
            Node? context = null;
            for (var p = m.ParentOf(call); p != null; p = m.ParentOf(p))
                if (p is FunctionDef or ClassDef) { context = p; break; }
            foreach (var a in call.Args)
            {
                if (a.Name != null || a.Stars != 0 || a.Value is not ConstExpr lit || StringValue(lit) is not { } name) continue;
                uses.Add(new NamedFixtureUse(name, lit, context));
                if (lookup) break;
            }
        }
        return uses;
    }
}
