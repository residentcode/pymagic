using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

public sealed record InspectResult(PythonInterpreter? Interpreter, string? Error);

/// <summary>
/// Asks an interpreter about itself by running it once (isolated mode, so user settings and PYTHONPATH do not change
/// the answer). sys.path comes from the interpreter, which means site-packages, <c>.pth</c> entries and the standard library
/// are exactly what that interpreter would import from. Results are cached until the executable changes.
/// </summary>
public static class InterpreterInspector
{
    const string Script = @"
import sys, os, json, sysconfig
p = sysconfig.get_paths()
print(json.dumps({
  'version': list(sys.version_info[:3]),
  'releaselevel': sys.version_info.releaselevel,
  'implementation': sys.implementation.name,
  'platform': sys.platform,
  'prefix': sys.prefix,
  'base_prefix': sys.base_prefix,
  'stdlib': p.get('stdlib'),
  'site': [x for x in {p.get('purelib'), p.get('platlib')} if x],
  'path': [x for x in sys.path if x and os.path.isdir(x)],
}))";

    static readonly ConcurrentDictionary<string, (long Ticks, long Length, string Site, InspectResult Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes when a package or a .pth file is added to or removed from the interpreter's site-packages (directory timestamps).</summary>
    static string SiteStamp(PythonInterpreter i) =>
        string.Join("|", i.SitePackages.Select(d => d + ":" + (Directory.Exists(d) ? Directory.GetLastWriteTimeUtc(d).Ticks : 0)));

    public static InspectResult Inspect(string path, string kind = "system", string source = "", int timeoutMs = 15000)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (ArgumentException e) { return new InspectResult(null, e.Message); }
        if (!File.Exists(full)) return new InspectResult(null, $"not found: {path}");

        var info = new FileInfo(full);
        if (Cache.TryGetValue(full, out var hit) && hit.Ticks == info.LastWriteTimeUtc.Ticks && hit.Length == info.Length
            && (hit.Result.Interpreter == null || hit.Site == SiteStamp(hit.Result.Interpreter)))
            return hit.Result.Interpreter == null ? hit.Result : new InspectResult(hit.Result.Interpreter with { Kind = kind, Source = source }, null);

        var result = Run(full, kind, source, timeoutMs);
        if (result.Interpreter != null) Cache[full] = (info.LastWriteTimeUtc.Ticks, info.Length, SiteStamp(result.Interpreter), result);
        return result;
    }

    static InspectResult Run(string exe, string kind, string source, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Script);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new InspectResult(null, $"cannot start {exe}");
            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new InspectResult(null, "the interpreter did not answer in time");
            }
            string output = stdout.Result.Trim();
            if (p.ExitCode != 0 || output.Length == 0)
                return new InspectResult(null, FirstLine(stderr.Result) ?? $"exit code {p.ExitCode}");

            var j = JsonNode.Parse(output)!;
            var v = j["version"]!.AsArray();
            var interpreter = new PythonInterpreter(
                exe, kind, source,
                v[0]!.GetValue<int>(), v[1]!.GetValue<int>(), v[2]!.GetValue<int>(),
                j["releaselevel"]!.GetValue<string>(),
                j["implementation"]!.GetValue<string>(),
                j["platform"]!.GetValue<string>(),
                j["prefix"]!.GetValue<string>(),
                j["base_prefix"]!.GetValue<string>(),
                j["stdlib"]?.GetValue<string>(),
                j["path"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
                j["site"]!.AsArray().Select(x => x!.GetValue<string>()).ToList());
            return new InspectResult(interpreter, null);
        }
        catch (System.ComponentModel.Win32Exception e) { return new InspectResult(null, e.Message); }
        catch (System.Text.Json.JsonException e) { return new InspectResult(null, "unexpected answer: " + e.Message); }
    }

    static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
