using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// Stubs of the compiled modules of an interpreter's packages (lxml.etree, psutil's _psutil_windows, msgpack._cmsgpack): they have no source, so
/// PyMagic asks the interpreter what is in them (Run/pm_skeletons.py imports each one in a child process) and keeps a .pyi of each in a folder of
/// its own, %LOCALAPPDATA%\PyMagic\skeletons\&lt;interpreter&gt;, which is the last of the interpreter's library roots. A module that has a .pyi or .py
/// of its own, or a -stubs package, is left alone. Made when the interpreter is chosen and again when its packages change; only what changed
/// is done again.
/// </summary>
public static class Skeletons
{
    /// <summary>Where the stubs of an interpreter go (one folder per interpreter path).</summary>
    public static string DirFor(string interpreterPath)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(interpreterPath).ToLowerInvariant())))[..16];
        return Path.Combine(Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyMagic", "skeletons"), key);
    }

    /// <summary>The folder that holds every interpreter's stubs: PYMAGIC_SKELETONS when set (tests and e2e runs keep them in %TEMP%).</summary>
    public static string? Root { get; set; } = Environment.GetEnvironmentVariable("PYMAGIC_SKELETONS") is { Length: > 0 } fromEnvironment ? fromEnvironment : null;

    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Makes the stubs that are missing or out of date for a local interpreter and removes those of modules that went. True when anything changed
    /// (so the analysis should read its libraries again); false when nothing did, the interpreter is not on this computer, or it could not run.
    /// </summary>
    public static async Task<bool> RefreshAsync(PythonInterpreter interpreter, TimeSpan? timeout = null)
    {
        if (!interpreter.Target.IsLocal || !File.Exists(interpreter.Path)) return false;
        var sites = interpreter.SitePackages.Where(Directory.Exists).ToList();
        if (sites.Count == 0) return false;
        await Gate.WaitAsync();
        try
        {
            string script = RunSession.ExtractHelper("pm_skeletons.py");
            var psi = new ProcessStartInfo(interpreter.Path)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(DirFor(interpreter.Path));
            foreach (var s in sites) psi.ArgumentList.Add(s);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } return false; }
            string last = (await output).Trim().Split('\n').LastOrDefault() ?? "";
            if (p.ExitCode != 0) { Console.Error.WriteLine("Stubs of compiled modules: " + (await errors).Trim().Split('\n').LastOrDefault()); return false; }
            if (JsonNode.Parse(last) is not JsonObject summary) return false;
            int made = summary["made"]?.GetValue<int>() ?? 0, removed = summary["removed"]?.GetValue<int>() ?? 0;
            return made + removed > 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine("Stubs of compiled modules: " + e.Message);
            return false;
        }
        finally { Gate.Release(); }
    }
}
