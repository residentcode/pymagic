using System.Diagnostics;
using PyMagic.Core.Syntax;

namespace PyMagic.Tests;

/// <summary>Where the time and the memory go when files are read in: lexing and parsing, binding, flow (usage: parsecost &lt;dir&gt; [max files]).</summary>
public static class ParseCost
{
    /// <summary>Samples the allocations of the process (the runtime reports one event per 100 KB) by type.</summary>
    sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
    {
        public readonly Dictionary<string, long> Counts = new();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
            string type = e.Payload != null && e.PayloadNames != null ? e.Payload[e.PayloadNames.IndexOf("TypeName")]?.ToString() ?? "?" : "?";
            lock (Counts) Counts[type] = Counts.GetValueOrDefault(type) + 1;
        }
    }

    public static int Run(string[] args)
    {
        var files = Directory.EnumerateFiles(args[1], "*.py", SearchOption.AllDirectories).Take(args.Length > 2 ? int.Parse(args[2]) : 600).ToList();
        long bytes = files.Sum(f => new FileInfo(f).Length);
        var texts = files.Select(File.ReadAllText).ToList();
        using var sampler = args.Contains("--sample") ? new AllocationSampler() : null;
        for (int pass = 0; pass < 2; pass++)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            int tokenCount = 0;
            foreach (var t in texts) tokenCount += Lexer.Tokenize(t).Count;
            Console.WriteLine($"  lexing alone: {sw.ElapsedMilliseconds} ms, {tokenCount} tokens");
            sw.Restart(); a0 = GC.GetAllocatedBytesForCurrentThread();
            var parsed = texts.Select(t => Parser.Parse(t)).ToList();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            double tParse = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var env = PyMagic.Core.Semantics.PythonEnvironment.Default;
            var bound = parsed.Select(r => PyMagic.Core.Semantics.SemanticModel.Build(r.Module, env)).ToList();
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            double tBind = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"pass {pass}: {files.Count} files, {bytes / 1048576.0:F1} MB of source; parse {tParse:F0} ms, {(a1 - a0) / 1048576} MB allocated; bind {tBind:F0} ms, {(a2 - a1) / 1048576} MB allocated");
        }
        if (sampler != null)
            foreach (var kv in sampler.Counts.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"  {kv.Value * 100 / 1024.0,9:F1} MB  {kv.Key}");
        return 0;
    }
}
