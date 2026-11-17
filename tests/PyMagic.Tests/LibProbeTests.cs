using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The library sweep (tools/library-sweep): the type the hover gives each V_n name of PROBE_DIR's files, with PROBE_LIBS (';') as the installed
/// packages, written to PROBE_DIR/out_&lt;shard&gt;.txt (PROBE_SHARD "i/n" splits the files among processes). Does nothing without PROBE_DIR.
/// </summary>
public class LibProbeTests
{
    [Fact]
    public void Probe()
    {
        if (Environment.GetEnvironmentVariable("PROBE_DIR") is not { } src) return;
        var svc = new LanguageService();
        svc.AddSourceRoot(src);
        foreach (var lib in (Environment.GetEnvironmentVariable("PROBE_LIBS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) svc.LibraryRoots.Add(lib);
        var output = new List<string>();
        var shard = (Environment.GetEnvironmentVariable("PROBE_SHARD") ?? "0/1").Split('/').Select(int.Parse).ToArray();
        var files = Directory.GetFiles(src, "*.py").OrderBy(f => f).Where((_, i) => i % shard[1] == shard[0]).ToList();
        string outFile = Path.Combine(src, $"out_{shard[0]}.txt");
        File.WriteAllText(outFile, "");
        foreach (var path in files)
        {
            string uri = LanguageService.UriOfPath(path);
            string text = File.ReadAllText(path);
            svc.Open(uri, text, 1);
            output.Add("== " + Path.GetFileName(path));
            if (Environment.GetEnvironmentVariable("PROBE_NODIAG") == null) foreach (var d in svc.Diagnostics(uri).Where(d => d.Severity <= 3)) output.Add($"  {d.Range.Start.Line + 1}: [{d.Severity}] {d.Message}");
            var map = new LineMap(text);
            int offset = 0;
            foreach (var line in text.Split('\n'))
            {
                var found = System.Text.RegularExpressions.Regex.Match(line, @"\bV_\d+\b");
                if (found.Success)
                {
                    var h = svc.Hover(uri, map.PositionOf(offset + found.Index + 1));
                    output.Add(found.Value + "\t" + (h?.Contents.Value.Split('\n').ElementAtOrDefault(1) ?? "(no hover)"));
                }
                offset += line.Length + 1;
            }
            File.AppendAllLines(outFile, output);
            output.Clear();
            svc.Close(uri);
        }
    }
}
