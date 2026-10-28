using System.Reflection;

namespace PyMagic.Tests;

/// <summary>Marks a parameterless test method. Optional <see cref="CaseAttribute"/>s add parameter sets.</summary>
[AttributeUsage(AttributeTargets.Method)] public sealed class FactAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CaseAttribute(params object[] args) : Attribute { public object[] Args { get; } = args; }

public sealed class AssertionException(string m) : Exception(m) { }

public static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"expected: {expected}\n  actual: {actual}");
    }
    public static void True(bool c, string? msg = null) { if (!c) throw new AssertionException(msg ?? "expected true"); }
    public static void Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T) { return; }
        throw new AssertionException("expected " + typeof(T).Name);
    }
    public static void Contains(string needle, string hay)
    {
        if (!hay.Contains(needle)) throw new AssertionException($"'{needle}' not found in: {hay}");
    }
}

public static class Program
{
    /// <summary>Parses every .py file under a directory and reports files with syntax diagnostics (usage: corpus &lt;dir&gt;).</summary>
    static int Corpus(string dir)
    {
        int files = 0, bad = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            files++;
            try
            {
                var r = PyMagic.Core.Syntax.Parser.Parse(text);
                if (r.Diagnostics.Count > 0)
                {
                    bad++;
                    if (bad <= 25)
                    {
                        var d = r.Diagnostics[0];
                        int line = text.Take(d.Start).Count(c => c == '\n') + 1;
                        Console.WriteLine($"{f}:{line}: {d.Message}");
                    }
                }
            }
            catch (Exception e) { bad++; Console.WriteLine($"CRASH {f}: {e.GetType().Name} {e.Message}"); }
        }
        Console.WriteLine($"{files} files, {bad} with diagnostics");
        return 0;
    }

    /// <summary>
    /// Runs the type inspections over files of a directory (the directory is also the import search root),
    /// printing timing, crashes, message counts and samples. Optional second argument limits the number of files.
    /// </summary>
    /// <summary>Runs the language service's own diagnostics (class hierarchy inspections included) over a folder of modules; counts by code, with samples.</summary>
    static int ServiceCorpus(string dir)
    {
        var svc = new PyMagic.Server.LanguageService { TypeshedPath = PyMagic.Core.Types.Workspace.DefaultTypeshedPath };
        svc.LibraryRoots.Add(dir);
        foreach (var extra in (System.Environment.GetEnvironmentVariable("PM_LIB") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) svc.LibraryRoots.Add(extra);
        var uris = new List<(string Uri, string Name)>();
        var texts = new Dictionary<string, string>();
        // PM_REAL_URIS=1 opens the files at their real paths, below the folder, so conftest.py files are found the way a project has them
        bool real = System.Environment.GetEnvironmentVariable("PM_REAL_URIS") != null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.py", real ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            string uri = real ? PyMagic.Server.LanguageService.UriOfPath(f) : $"file:///c:/corpus_open/{name}.py";
            string content = File.ReadAllText(f);
            texts[uri] = content;
            svc.Open(uri, content, 1);
            uris.Add((uri, name));
        }
        if (System.Environment.GetEnvironmentVariable("PM_COMPLETE") != null)
        {
            // completion latency at real positions: two letters of a statement, and two letters after a dot
            var times = new List<(double Ms, string Where)>();
            var rng = new Random(7);
            int cases = 0;
            foreach (var (uri, name) in uris)
            {
                string src = texts[uri];
                var lines2 = src.Split('\n');
                for (int n = 0; n < 6 && lines2.Length > 10; n++)
                {
                    int ln = rng.Next(5, lines2.Length - 1);
                    string line = lines2[ln].TrimEnd('\r');
                    var m1 = System.Text.RegularExpressions.Regex.Match(line, @"^(\s+)([A-Za-z_]\w{3,})");
                    var m2 = System.Text.RegularExpressions.Regex.Match(line, @"\.([A-Za-z_]\w{3,})");
                    foreach (var (col, kind) in new[] { (m1.Success ? m1.Groups[2].Index + 2 : -1, "name"), (m2.Success ? m2.Groups[1].Index + 2 : -1, "member") })
                    {
                        if (col < 0) continue;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try { svc.Completion(uri, new PyMagic.Server.Position(ln, col)); }
                        catch (Exception e) { Console.WriteLine($"CRASH {name}.py:{ln + 1} {kind}: {e.GetType().Name} {e.Message}"); }
                        times.Add((sw.Elapsed.TotalMilliseconds, $"{name}.py:{ln + 1} {kind}"));
                        cases++;
                    }
                }
            }
            var sorted = times.OrderBy(t => t.Ms).ToList();
            Console.WriteLine($"completion: {cases} cases, median {sorted[sorted.Count / 2].Ms:F1} ms, p90 {sorted[sorted.Count * 9 / 10].Ms:F1} ms, max {sorted[^1].Ms:F0} ms ({sorted[^1].Where})");
            foreach (var t in sorted.TakeLast(4)) Console.WriteLine($"   slow: {t.Ms:F0} ms  {t.Where}");
            return 0;
        }
        if (System.Environment.GetEnvironmentVariable("PM_MARKERS") != null)
        {
            // gutter marks and the class index for every file: how long, and what comes out
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var kinds = new Dictionary<string, int>();
            int sampleLines = 0;
            foreach (var (uri, name) in uris)
                foreach (var mk in svc.InheritanceMarkers(uri))
                {
                    kinds[mk.Kind] = kinds.GetValueOrDefault(mk.Kind) + 1;
                    if (sampleLines++ % 150 == 0) Console.WriteLine($"   {name}.py:{mk.Line + 1}  {mk.Kind}  {mk.Label}");
                }
            Console.WriteLine($"markers: {string.Join(", ", kinds.Select(k => k.Key + " " + k.Value))} in {clock.Elapsed.TotalSeconds:F1}s for {uris.Count} files");
            return 0;
        }
        var counts = new Dictionary<string, int>();
        var samples = new Dictionary<string, List<string>>();
        foreach (var (uri, name) in uris)
            foreach (var d in svc.Diagnostics(uri))
            {
                string code = d.Code ?? "(none)";
                counts[code] = counts.GetValueOrDefault(code) + 1;
                if (!samples.TryGetValue(code, out var l)) samples[code] = l = new();
                if (l.Count < 12) l.Add($"{name}.py:{d.Range.Start.Line + 1}  {d.Message}");
            }
        foreach (var kv in counts.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"{kv.Value,6}  {kv.Key}");
            var show = (System.Environment.GetEnvironmentVariable("PM_SAMPLE") ?? "").Split(',');
            if (kv.Key is "missing-super-init" or "unimplemented-abstract" or "fixture-not-found" || show.Contains(kv.Key)) foreach (var s in samples[kv.Key]) Console.WriteLine("          " + s);
        }
        return 0;
    }

    static int InspectCorpus(string dir, int limit)
    {
        // the files under test are user code; their imports resolve like a real project (typeshed first, then the library directory)
        var ws = new PyMagic.Core.Types.Workspace(new PyMagic.Core.Types.WorkspaceOptions
        {
            LibraryRoots = new[] { dir },
            TypeshedPath = PyMagic.Core.Types.Workspace.DefaultTypeshedPath,
            // PM_PLATFORM=linux checks Unix-only code the way it would run there (default: this machine)
            Environment = new PyMagic.Core.Semantics.PythonEnvironment(3, 13, System.Environment.GetEnvironmentVariable("PM_PLATFORM") ?? "win32"),
        });
        var counts = new Dictionary<string, int>();
        var raw = new Dictionary<string, int>();
        var samples = new Dictionary<string, List<string>>();
        int files = 0, crashes = 0;
        long loadMs = 0, inspectMs = 0;
        bool trace = System.Environment.GetEnvironmentVariable("PM_TRACE") != null;
        string? match = System.Environment.GetEnvironmentVariable("PM_MATCH");   // print every diagnostic containing this text
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in Directory.EnumerateFiles(dir, "*.py", SearchOption.TopDirectoryOnly).Take(limit))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            try
            {
                if (trace) Console.WriteLine($"-> {name}");
                var swFile = System.Diagnostics.Stopwatch.StartNew();
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                var m = ws.AddFile("corpus_" + name, f);
                loadMs += swLoad.ElapsedMilliseconds;
                if (m == null) continue;
                files++;
                var swInsp = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<PyMagic.Core.Syntax.Diagnostic> found = ws.Evaluator.Inspect(m);
                // PM_INSPECTIONS=1 adds the syntax-level inspections (unused variables, == None, ...) to the count
                if (System.Environment.GetEnvironmentVariable("PM_INSPECTIONS") != null)
                    found = found.Concat(PyMagic.Core.Types.CodeInspections.Run(m).Select(x =>
                        new PyMagic.Core.Syntax.Diagnostic(x.Start, x.Length, $"[{x.Code}] {x.Message}")))
                        .Concat(m.Semantics.Diagnostics).Concat(m.Flow.Analyse().Where(x => x.Message != "Unreachable code")).ToList();
                // findings that sit inside an f-string field are tagged, to see what parsing the fields changed
                var fields = PyMagic.Core.Syntax.AstChildren.Descendants(m.Ast).OfType<PyMagic.Core.Syntax.ConstExpr>()
                    .SelectMany(c => c.Parts).Select(p => (p.Start, p.End)).ToList();
                found = found.Select(x => fields.Any(f => x.Start >= f.Start && x.Start < f.End)
                    ? new PyMagic.Core.Syntax.Diagnostic(x.Start, x.Length, "[in-fstring] " + x.Message) : x).ToList();
                inspectMs += swInsp.ElapsedMilliseconds;
                if (trace || swFile.ElapsedMilliseconds > 1500) Console.WriteLine($"   {name}: {swFile.ElapsedMilliseconds} ms");
                foreach (var d in found)
                {
                    string kind = System.Text.RegularExpressions.Regex.Replace(d.Message, "'[^']*'", "'_'");
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    raw[d.Message] = raw.GetValueOrDefault(d.Message) + 1;
                    if (match != null && d.Message.Contains(match)) Console.WriteLine($"MATCH {name}.py:{m.Source.Take(d.Start).Count(c => c == '\n') + 1}  {d.Message}");
                    if (!samples.TryGetValue(kind, out var list)) samples[kind] = list = new();
                    if (list.Count < 6) list.Add($"{name}.py:{m.Source.Take(d.Start).Count(c => c == '\n') + 1}  {d.Message}");
                }
            }
            catch (Exception e) { crashes++; Console.WriteLine($"CRASH {name}: {e.GetType().Name}: {e.Message}\n   {string.Join("\n   ", (e.StackTrace ?? "").Split('\n').Take(4))}"); }
        }
        foreach (var kv in counts.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"{kv.Value,6}  {kv.Key}");
            foreach (var s in samples[kv.Key]) Console.WriteLine("          " + s);
        }
        Console.WriteLine("-- most frequent exact messages --");
        foreach (var kv in raw.OrderByDescending(k => k.Value).Take(40)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
        Console.WriteLine($"{files} files, {crashes} crashes, {sw.Elapsed.TotalSeconds:F1}s  (load {loadMs} ms, inspect {inspectMs} ms)");
        return 0;
    }

    /// <summary>Real typeshed makes evaluation chains deep; run on a thread with room for them.</summary>
    static int OnBigStack(Func<int> body)
    {
        int result = 0;
        var thread = new Thread(() => result = body(), 512 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>Prints the MRO of a class (module name, class name), marking unresolvable ancestors.</summary>
    static int ShowMro(string module, string cls)
    {
        var ws = new PyMagic.Core.Types.Workspace();
        var m = ws.GetModule(module) ?? throw new Exception("module not found");
        var sym = ws.Evaluator.FindModuleSymbol(m, cls) ?? throw new Exception("class not found");
        var t = ws.Evaluator.TypeOfSymbol(sym) as PyMagic.Core.Types.ClassType ?? throw new Exception("not a class");
        Console.WriteLine(string.Join(" -> ", ws.Evaluator.MroOf(t.Info).Select(e => e is PyMagic.Core.Types.ClassType c ? c.ToString() : "<UNKNOWN>")));
        Console.WriteLine($"protocol: {ws.Evaluator.IsProtocol(t.Info)}, type params: {string.Join(",", ws.Evaluator.TypeParametersOf(t.Info))}, module: {t.Info.Module.Name} ({t.Info.Module.Path})");
        return 0;
    }

    /// <summary>Prints the type of every expression statement in a file (a quick way to poke at the evaluator).</summary>
    static int ShowTypes(string file)
    {
        var ws = new PyMagic.Core.Types.Workspace(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var m = ws.AddModule("__main__", File.ReadAllText(file));
        foreach (var d in m.SyntaxErrors) Console.WriteLine($"syntax error at {d.Start}: {d.Message}");
        foreach (var n in PyMagic.Core.Syntax.AstChildren.Descendants(m.Ast).OfType<PyMagic.Core.Syntax.ExprStmt>())
        {
            int line = m.Source.Take(n.Start).Count(c => c == '\n') + 1;
            Console.WriteLine($"{line,4}: {m.Source.Substring(n.Start, n.End - n.Start),-40} -> {ws.Evaluator.TypeOf(m, n.Value)}");
            if (n.Value is PyMagic.Core.Syntax.CallExpr call)
                foreach (var t in ws.Evaluator.ResolveCall(m, call).Targets)
                    foreach (var p in t.Problems) Console.WriteLine($"        problem ({p.Kind}): {p.Message}");
        }
        return 0;
    }

    /// <summary>Runs the flow analysis over every file; prints timing, counts per message kind and a few examples of each.</summary>
    static int FlowCorpus(string dir)
    {
        var counts = new Dictionary<string, int>();
        var examples = new Dictionary<string, List<string>>();
        int files = 0, crashes = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            files++;
            try
            {
                var r = PyMagic.Core.Syntax.Parser.Parse(text);
                var sem = PyMagic.Core.Semantics.SemanticModel.Build(r.Module);
                foreach (var d in new PyMagic.Core.Flow.FlowModel(sem).Analyse())
                {
                    string kind = d.Message.StartsWith("Local variable") ? (d.Message.Contains("might") ? "maybe-unbound" : "definitely-unbound") : d.Message;
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    if (!examples.TryGetValue(kind, out var list)) examples[kind] = list = new();
                    if (list.Count < 12 && files % 7 == 0 || list.Count < 4)
                        list.Add($"{Path.GetFileName(f)}:{text.Take(d.Start).Count(c => c == '\n') + 1} {d.Message}");
                }
            }
            catch (Exception e) { crashes++; Console.WriteLine($"CRASH {f}: {e.GetType().Name} {e.Message}\n{e.StackTrace?.Split('\n').FirstOrDefault()}"); }
        }
        foreach (var kv in counts) { Console.WriteLine($"{kv.Value,6}  {kv.Key}"); foreach (var e in examples[kv.Key]) Console.WriteLine("          " + e); }
        Console.WriteLine($"{files} files, {crashes} crashes, {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    /// <summary>Builds the semantic model for every file and lists the most common unresolved names (binder sanity check).</summary>
    static int SemanticCorpus(string dir)
    {
        var counts = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        int files = 0, crashes = 0, total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            files++;
            try
            {
                var r = PyMagic.Core.Syntax.Parser.Parse(text);
                var m = PyMagic.Core.Semantics.SemanticModel.Build(r.Module);
                foreach (var d in m.Diagnostics)
                {
                    total++;
                    counts[d.Message] = counts.GetValueOrDefault(d.Message) + 1;
                    if (!examples.ContainsKey(d.Message))
                        examples[d.Message] = $"{f}:{text.Take(d.Start).Count(c => c == '\n') + 1}";
                }
            }
            catch (Exception e) { crashes++; Console.WriteLine($"CRASH {f}: {e.GetType().Name} {e.Message}"); }
        }
        foreach (var kv in counts.OrderByDescending(k => k.Value).Take(30))
            Console.WriteLine($"{kv.Value,5}  {kv.Key}   e.g. {examples[kv.Key]}");
        Console.WriteLine($"{files} files, {crashes} crashes, {total} diagnostics, {counts.Count} distinct");
        return 0;
    }

    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "bench") return OnBigStack(() => BenchCommand.Run(args));
        if (args.Length >= 2 && args[0] == "parsecost") return OnBigStack(() => ParseCost.Run(args));
        if (args.Length >= 2 && args[0] == "asthash") return OnBigStack(() => AstHash.Run(args));
        if (args.Length >= 4 && args[0] == "usages") return OnBigStack(() => UsagesBench.Run(args));
        if (args.Length == 2 && args[0] == "corpus") return Corpus(args[1]);
        if (args.Length == 2 && args[0] == "semantic") return SemanticCorpus(args[1]);
        if (args.Length == 2 && args[0] == "flow") return FlowCorpus(args[1]);
        if (args.Length == 2 && args[0] == "types") return ShowTypes(args[1]);
        if (args.Length == 3 && args[0] == "mro") return ShowMro(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "service") return OnBigStack(() => ServiceCorpus(args[1]));
        if (args.Length >= 2 && args[0] == "inspect") return OnBigStack(() => InspectCorpus(args[1], args.Length > 2 ? int.Parse(args[2]) : int.MaxValue));
        int pass = 0, fail = 0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsClass && t.Name.EndsWith("Tests")))
        {
            var inst = Activator.CreateInstance(type)!;
            foreach (var m in type.GetMethods().Where(m => m.GetCustomAttribute<FactAttribute>() != null))
            {
                var cases = m.GetCustomAttributes<CaseAttribute>().Select(c => c.Args).ToList();
                if (cases.Count == 0) cases.Add([]);
                foreach (var a in cases)
                {
                    string name = $"{type.Name}.{m.Name}({string.Join(", ", a.Select(x => x?.ToString()?.Replace("\n", "\\n")))})";
                    try { m.Invoke(inst, a); pass++; }
                    catch (TargetInvocationException e)
                    {
                        fail++;
                        Console.WriteLine($"FAIL {name}\n  {e.InnerException?.Message}");
                    }
                }
            }
        }
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
