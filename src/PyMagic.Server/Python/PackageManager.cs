using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

public sealed record InstalledPackage(string Name, string Version, bool Editable, string? EditableLocation);
public sealed record OutdatedPackage(string Name, string Version, string Latest, string? Type);
public sealed record PackageList(bool HasPip, string? Error, IReadOnlyList<InstalledPackage> Packages);

/// <summary>What `pip show` knows about one package.</summary>
public sealed record PackageInfo(string Name, string Version, string Summary, string? HomePage, string? License, string? Location,
    IReadOnlyList<string> Requires, IReadOnlyList<string> RequiredBy, bool Editable);

/// <summary>Reads what is installed in an interpreter by asking its own pip (installing and removing go through <see cref="RunSession.StartModule"/> so the output streams).</summary>
public static class PackageManager
{
    /// <summary>PEP 503 normalisation: case-insensitive, runs of - _ . are the same.</summary>
    public static string Normalize(string name) => Regex.Replace(name.Trim(), @"[-_.]+", "-").ToLowerInvariant();

    /// <summary>A single requirement or package name as accepted for installation (no options, no spaces, no paths).</summary>
    public static bool IsSafeSpec(string spec) =>
        spec.Length is > 0 and <= 200 && Regex.IsMatch(spec, @"^[A-Za-z0-9][A-Za-z0-9._\-\[\],<>=!~*+]*$");

    public static PackageList List(string python, int timeoutMs = 60000, ExecutionTarget? target = null)
    {
        var (code, output, error) = Exec(python, new[] { "-m", "pip", "list", "--format=json", "--disable-pip-version-check" }, timeoutMs, target);
        if (code != 0)
        {
            bool noPip = error.Contains("No module named pip", StringComparison.OrdinalIgnoreCase);
            return new PackageList(!noPip, noPip ? "pip is not installed for this interpreter" : LastLine(error) ?? $"pip exited with {code}", Array.Empty<InstalledPackage>());
        }
        try
        {
            var list = JsonNode.Parse(output)!.AsArray().Select(p => new InstalledPackage(
                p!["name"]!.GetValue<string>(), p["version"]!.GetValue<string>(),
                p["editable_project_location"] != null, p["editable_project_location"]?.GetValue<string>()))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return new PackageList(true, null, list);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return new PackageList(true, "unexpected answer from pip", Array.Empty<InstalledPackage>());
        }
    }

    /// <summary>Packages with a newer release (asks the package index, so it needs the network and can take a while).</summary>
    public static IReadOnlyList<OutdatedPackage> Outdated(string python, int timeoutMs = 120000, ExecutionTarget? target = null)
    {
        var (code, output, _) = Exec(python, new[] { "-m", "pip", "list", "--outdated", "--format=json", "--disable-pip-version-check" }, timeoutMs, target);
        if (code != 0) return Array.Empty<OutdatedPackage>();
        try
        {
            return JsonNode.Parse(output)!.AsArray().Select(p => new OutdatedPackage(
                p!["name"]!.GetValue<string>(), p["version"]!.GetValue<string>(), p["latest_version"]!.GetValue<string>(),
                p["latest_filetype"]?.GetValue<string>())).ToList();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return Array.Empty<OutdatedPackage>(); }
    }

    public static PackageInfo? Show(string python, string name, int timeoutMs = 30000, ExecutionTarget? target = null)
    {
        if (!IsSafeSpec(name)) return null;
        var (code, output, _) = Exec(python, new[] { "-m", "pip", "show", "--disable-pip-version-check", name }, timeoutMs, target);
        if (code != 0) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && !line.StartsWith(' ')) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        static IReadOnlyList<string> Split(string? s) => string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? Get(string k) => fields.GetValueOrDefault(k);
        return new PackageInfo(Get("Name") ?? name, Get("Version") ?? "", Get("Summary") ?? "", Get("Home-page") is { Length: > 0 } h ? h : null,
            Get("License") is { Length: > 0 } l ? l : null, Get("Location"), Split(Get("Requires")), Split(Get("Required-by")),
            fields.ContainsKey("Editable project location"));
    }

    /// <summary>True when the interpreter can be changed by pip without elevated rights (a virtual environment, or a writable site-packages).</summary>
    public static bool CanInstallGlobally(PythonInterpreter interpreter) =>
        interpreter.IsVenv || interpreter.SitePackages.All(IsWritable);

    static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".pymagic-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ------------------------------------------------------------ plumbing

    public static (int Code, string Output, string Error) Exec(string python, IEnumerable<string> args, int timeoutMs, ExecutionTarget? target = null)
    {
        var psi = (target ?? ExecutionTarget.Local).Start(python, Path.GetTempPath(), args, new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" });
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "cannot start " + python);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "", "timed out");
            }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, "", e.Message); }
    }

    static string? LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
