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
        Console.WriteLine($"GC: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, allocated {GC.GetTotalAllocatedBytes() / 1048576} MB");
        Console.WriteLine($"peak memory {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
        return 0;
    }
}
