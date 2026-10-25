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
names = set(sys.builtin_module_names) | set(getattr(sys, 'stdlib_module_names', ()))
if not hasattr(sys, 'stdlib_module_names'):
    std = p.get('stdlib')
    if std and os.path.isdir(std):
        for n in os.listdir(std):
            if n.endswith('.py'): names.add(n[:-3])
            elif os.path.isdir(os.path.join(std, n)) and n.isidentifier(): names.add(n)
        for extra in ('lib-dynload', os.path.join(os.path.dirname(std), 'DLLs')):
            d = os.path.join(std, extra) if not os.path.isabs(extra) else extra
            if os.path.isdir(d):
                for n in os.listdir(d):
                    base = n.split('.')[0]
                    if base.isidentifier(): names.add(base)
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
  'stdlib_modules': sorted(names),
}))";

    static readonly ConcurrentDictionary<string, (long Ticks, long Length, string Site, InspectResult Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes when a package or a .pth file is added to or removed from the interpreter's site-packages (directory timestamps).</summary>
    static string SiteStamp(PythonInterpreter i) =>
        string.Join("|", i.SitePackages.Select(d => d + ":" + (Directory.Exists(d) ? Directory.GetLastWriteTimeUtc(d).Ticks : 0)));

    public static InspectResult Inspect(string path, string kind = "system", string source = "", int timeoutMs = 15000)
    {
        if (ExecutionTarget.Parse(path) is var (target, python)) return InspectRemote(target, python, source, Math.Max(timeoutMs, 60000));
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

    // an interpreter in WSL or Docker is asked again after this long (there is no executable file to watch)
    static readonly ConcurrentDictionary<string, (DateTime At, InspectResult Result)> RemoteCache = new();

    /// <summary>
    /// An interpreter of another target: run it there once. Its folders come back as paths of that target, so each one is
    /// turned into a path this machine can read (a WSL distribution's network share, or a copy made from a Docker image);
    /// folders that cannot be read from here are left out.
    /// </summary>
    static InspectResult InspectRemote(ExecutionTarget target, string python, string source, int timeoutMs)
    {
        string spec = target.Spec(python);
        if (RemoteCache.TryGetValue(spec, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30) && hit.Result.Interpreter != null)
            return new InspectResult(hit.Result.Interpreter with { Source = source }, null);

        var run = Run(null, target.Kind, source, timeoutMs, target, python);
        if (run.Interpreter is not { } raw) return run;

        if (target is DockerTarget docker)
            docker.Mirror(raw.SysPath.Concat(raw.SitePackages).Concat(raw.Stdlib is { } sl ? new[] { sl } : Array.Empty<string>()));
        string? Map(string? p) => p == null ? null : target.Readable(p);
        var site = raw.SitePackages.Select(Map).Where(x => x != null).Select(x => x!).ToList();
        var path = raw.SysPath.Select(Map).Where(x => x != null).Select(x => x!).ToList();
        var mapped = raw with
        {
            Path = spec,
            Prefix = Map(raw.Prefix) ?? raw.Prefix,
            BasePrefix = Map(raw.BasePrefix) ?? raw.BasePrefix,
            Stdlib = Map(raw.Stdlib),
            SysPath = MergeEditableRoots(path, site),
            SitePackages = site,
            Kind = target.Kind,
            Source = source,
            Target = target,
            TargetPython = python,
        };
        var result = new InspectResult(mapped, null);
        RemoteCache[spec] = (DateTime.UtcNow, result);
        return result;
    }

    static InspectResult Run(string? exe, string kind, string source, int timeoutMs, ExecutionTarget? target = null, string? targetPython = null)
    {
        var args = new[] { "-I", "-c", Script };
        ProcessStartInfo psi;
        if (target == null)
        {
            psi = new ProcessStartInfo(exe!);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
        }
        else psi = target.Start(targetPython!, Path.GetTempPath(), args, new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" });
        exe ??= targetPython!;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

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
                MergeEditableRoots(j["path"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(), j["site"]!.AsArray().Select(x => x!.GetValue<string>()).ToList()),
                j["site"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
                j["stdlib_modules"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal));
            return new InspectResult(interpreter, null);
        }
        catch (System.ComponentModel.Win32Exception e) { return new InspectResult(null, e.Message); }
        catch (System.Text.Json.JsonException e) { return new InspectResult(null, "unexpected answer: " + e.Message); }
    }

    /// <summary>sys.path plus the folders that editable installs map their packages from.</summary>
    static List<string> MergeEditableRoots(List<string> path, List<string> sitePackages)
    {
        var seen = new HashSet<string>(path, StringComparer.OrdinalIgnoreCase);
        foreach (var site in sitePackages)
            foreach (var root in EditableInstalls.RootsIn(site))
                if (seen.Add(root)) path.Add(root);
        return path;
    }

    static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
