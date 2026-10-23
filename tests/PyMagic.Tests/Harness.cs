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
    static int InspectCorpus(string dir, int limit)
    {
        var ws = new PyMagic.Core.Types.Workspace(dir);
        var counts = new Dictionary<string, int>();
        var raw = new Dictionary<string, int>();
        var samples = new Dictionary<string, List<string>>();
        int files = 0, crashes = 0;
        long loadMs = 0, inspectMs = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in Directory.EnumerateFiles(dir, "*.py", SearchOption.TopDirectoryOnly).Take(limit))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                var m = ws.GetModule(name);
                loadMs += swLoad.ElapsedMilliseconds;
                if (m == null) continue;
                files++;
                var swInsp = System.Diagnostics.Stopwatch.StartNew();
                var found = ws.Evaluator.Inspect(m);
                inspectMs += swInsp.ElapsedMilliseconds;
                foreach (var d in found)
                {
                    string kind = System.Text.RegularExpressions.Regex.Replace(d.Message, "'[^']*'", "'_'");
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    raw[d.Message] = raw.GetValueOrDefault(d.Message) + 1;
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
        if (args.Length == 2 && args[0] == "corpus") return Corpus(args[1]);
        if (args.Length == 2 && args[0] == "semantic") return SemanticCorpus(args[1]);
        if (args.Length == 2 && args[0] == "flow") return FlowCorpus(args[1]);
        if (args.Length == 2 && args[0] == "types") return ShowTypes(args[1]);
        if (args.Length >= 2 && args[0] == "inspect") return InspectCorpus(args[1], args.Length > 2 ? int.Parse(args[2]) : int.MaxValue);
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
