using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Times find usages of a word in a project (usage: usages &lt;root&gt; &lt;file relative to root&gt; &lt;word&gt; [occurrence, default 1]). Each round makes an edit first, so no snapshot is reused.
/// The typeshed is taken from PYMAGIC_TYPESHED.
/// </summary>
public static class UsagesBench
{
    public static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        string target = Path.GetFullPath(Path.Combine(root, args[2]));
        string word = args[3];
        int nth = args.Length > 4 ? int.Parse(args[4]) : 1;
        string text = File.ReadAllText(target);
        using var sampler = System.Environment.GetEnvironmentVariable("PM_SAMPLE") != null ? new ParseCost.AllocationSampler() : null;
        if (word == "--problems")
        {
            // the Problems panel over the whole project (several workers at once)
            // PM_FORGET=1: every run works out every file, as before the Problems were remembered
            var ps = new LanguageService { RememberProblems = System.Environment.GetEnvironmentVariable("PM_FORGET") == null };
            ps.SourceRoots.Add(root);
            ps.Open(LanguageService.UriOfPath(target), text, 1);
            // beside it, an editor that keeps asking for hovers: the pauses of the garbage collector show as slow answers
            var hoverService = new LanguageService();
            hoverService.Open("file:///c:/hover/a.py", string.Join('\n', "import os", "", "print(os.path.join('a', 'b'))", ""), 1);
            var hoverPos = new Position(2, 12);
            hoverService.Hover("file:///c:/hover/a.py", hoverPos);
            var latencies = new List<double>();
            bool running = true;
            var editor = new Thread(() =>
            {
                while (running)
                {
                    var t = Stopwatch.StartNew();
                    hoverService.Hover("file:///c:/hover/a.py", hoverPos);
                    lock (latencies) latencies.Add(t.Elapsed.TotalMilliseconds);
                    Thread.Sleep(10);
                }
            });
            editor.Start();
            var clock = Stopwatch.StartNew();
            var all = ps.Problems(3, 100000);
            Console.WriteLine($"round 0: {all.Count} problems in {clock.ElapsedMilliseconds} ms");
            // then an edit of the file, and a run with nothing changed: what is remembered is not worked out again
            for (int round = 1; round <= 2; round++)
            {
                if (round == 1) ps.Change(LanguageService.UriOfPath(target), text + "\n# edit\n", 2);
                clock.Restart();
                var again = ps.Problems(3, 100000);
                Console.WriteLine($"round {round}: {again.Count} problems in {clock.ElapsedMilliseconds} ms, {ps.ProblemsWorkedOut} files worked out");
                // PM_VERIFY=1: the same answers as a service that works every file out
                if (round == 2 && System.Environment.GetEnvironmentVariable("PM_VERIFY") != null)
                {
                    var fresh = new LanguageService { RememberProblems = false };
                    fresh.SourceRoots.Add(root);
                    fresh.Open(LanguageService.UriOfPath(target), text + "\n# edit\n", 2);
                    var full = fresh.Problems(3, 100000);
                    Console.WriteLine($"same as a full run: {full.SequenceEqual(again)} ({full.Count} problems)");
                }
            }
            running = false;
            editor.Join();
            lock (latencies)
            {
                latencies.Sort();
                Console.WriteLine($"hover while it ran: {latencies.Count} asks, median {latencies[latencies.Count / 2]:F1} ms, p99 {latencies[latencies.Count * 99 / 100]:F0} ms, worst {latencies[^1]:F0} ms");
            }
            long kept = Process.GetCurrentProcess().WorkingSet64 / 1048576;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            Console.WriteLine($"peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
            Console.WriteLine($"memory kept after a full collection: working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB (was {kept} MB), managed {GC.GetTotalMemory(true) / 1048576} MB");
            Console.WriteLine($"GC: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, allocated {GC.GetTotalAllocatedBytes() / 1048576} MB");
            if (sampler != null)
                foreach (var kv in sampler.Counts.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"  {kv.Value * 100 / 1024.0,9:F1} MB  {kv.Key}");
            return 0;
        }
        // the definition when the file has one ("class X" / "def x"), else the n-th mention
        int at = -1;
        foreach (var head in new[] { "class " + word, "def " + word })
            if (at < 0 && text.IndexOf(head, StringComparison.Ordinal) is var d and >= 0) at = d + head.Length - word.Length;
        if (at < 0)
            for (int i = 0, from = 0; i < nth; i++, from = at + 1) if ((at = text.IndexOf(word, from, StringComparison.Ordinal)) < 0) break;
        if (at < 0) { Console.WriteLine("the word is not in the file"); return 1; }

        var svc = new LanguageService();
        svc.SourceRoots.Add(root);
        string uri = LanguageService.UriOfPath(target);
        svc.Open(uri, text, 1);
        var pos = new LineMap(text).PositionOf(at + word.Length / 2);

        for (int round = 0; round < 3; round++)
        {
            svc.Change(uri, text + $"\n# edit {round}\n", 2 + round);
            var sw = Stopwatch.StartNew();
            var found = svc.References(uri, pos, true);
            Console.WriteLine($"round {round}: {found.Count} usages in {found.Select(l => l.Uri).Distinct().Count()} files, {sw.ElapsedMilliseconds} ms");
        }
        long beforeCollect = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        Console.WriteLine($"memory kept after a full collection: working set {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576} MB (was {beforeCollect} MB), managed {GC.GetTotalMemory(true) / 1048576} MB");
        Console.WriteLine($"GC: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, allocated {GC.GetTotalAllocatedBytes() / 1048576} MB");
        Console.WriteLine($"peak memory {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
        if (sampler != null)
            foreach (var kv in sampler.Counts.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"  {kv.Value * 100 / 1024.0,9:F1} MB  {kv.Key}");
        return 0;
    }
}
