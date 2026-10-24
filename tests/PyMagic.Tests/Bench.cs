using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Simulates typing in an editor and reports what each kind of request costs (usage: bench &lt;root&gt; &lt;file relative to root&gt; [edits] [open files]).
/// The typeshed is taken from PYMAGIC_TYPESHED.
/// </summary>
public static class BenchCommand
{
    public static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        string rel = args[2];
        int edits = args.Length > 3 ? int.Parse(args[3]) : 20;
        int others = args.Length > 4 ? int.Parse(args[4]) : 10;

        var svc = new LanguageService();
        svc.SourceRoots.Add(root);
        string target = Path.GetFullPath(Path.Combine(root, rel));
        string uri = LanguageService.UriOfPath(target);
        string text = File.ReadAllText(target);

        int opened = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*.py", SearchOption.TopDirectoryOnly).Take(others + 1))
        {
            if (Path.GetFullPath(f) == target) continue;
            svc.Open(LanguageService.UriOfPath(f), File.ReadAllText(f), 1);
            opened++;
        }
        svc.Open(uri, text, 1);

        var sw = Stopwatch.StartNew();
        var times = new Dictionary<string, List<double>>();
        void Time(string what, Action a)
        {
            var t = Stopwatch.StartNew();
            a();
            if (!times.TryGetValue(what, out var l)) times[what] = l = new();
            l.Add(t.Elapsed.TotalMilliseconds);
        }

        // an attribute access near the end of the file for hover/completion
        int last = text.LastIndexOf('.');
        var lines = new LineMap(text);
        var pos = lines.PositionOf(Math.Max(last + 2, 1));

        Time("first diagnostics", () => svc.Diagnostics(uri));
        Time("first hover", () => svc.Hover(uri, pos));
        for (int i = 0; i < edits; i++)
        {
            svc.Change(uri, text + $"\n# edit {i}\n", 2 + i);
            Time("diagnostics after edit", () => svc.Diagnostics(uri));
            Time("hover after edit", () => svc.Hover(uri, pos));
            Time("completion after edit", () => svc.Completion(uri, pos));
            if (i % 5 == 0) Time("references (project-wide)", () => svc.References(uri, pos, true));
        }

        Console.WriteLine($"{opened + 1} documents open, {edits} edits, total {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var (what, l) in times)
            Console.WriteLine($"  {what,-28} n={l.Count,3}  avg {l.Average(),8:F1} ms   max {l.Max(),8:F1} ms");
        return 0;
    }
}
