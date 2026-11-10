using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// PHPStan's problems of a PHP file, as the editor's: the project's own vendor/bin/phpstan (with its phpstan.neon or phpstan.neon.dist, so
/// Larastan too when it includes it) run on the file as it is saved, by the PHP of Settings > PHP on this computer or on the machine reached by
/// SSH, its JSON report read back as { line, message, identifier, tip }.
/// </summary>
public static class PhpAnalysis
{
    /// <summary>Whether the project has PHPStan (Larastan brings it).</summary>
    public static bool Available(string root) => File.Exists(Path.Combine(root, "vendor", "bin", "phpstan"));

    static List<string> Arguments(string file) => new() { "vendor/bin/phpstan", "analyse", "--error-format=json", "--no-progress", "--no-interaction", "--memory-limit=1G", file };

    /// <summary>The problems PHPStan finds in a file of the project (relative path); a ServiceException says why it could not look.</summary>
    public static JsonArray Analyse(string root, string? phpSetting, string rel)
    {
        string full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ServiceException("not a file of the project");
        string relSlash = Path.GetRelativePath(root, full).Replace('\\', '/');
        int code;
        string output;
        if (SailTarget.Of(root, phpSetting) is { } sail)
        {
            // Sail's container: the project is shared, the file as saved is there already
            if (!sail.Running()) sail.Up();
            (code, output, var err) = sail.Exec(string.Join(" ", Arguments(relSlash).Select(SshTarget.Quote).Prepend("php")), 300_000);
            if (!output.Contains('{')) output += err;
        }
        else if (PhpTools.Remote(phpSetting) is { } remote)
        {
            var ssh = remote.Target;
            ssh.Project = root;
            ssh.Prepare(root, null, null);
            string args = string.Join(" ", Arguments(relSlash).Select(SshTarget.Quote));
            (code, output, var err) = ssh.Remote($"cd {SshTarget.Quote(ssh.ProjectDir)} && {SshTarget.Quote(remote.Php)} {args}", 300_000);
            if (!output.Contains('{')) output += err;
        }
        else
        {
            string php = PhpTools.FindPhp(phpSetting) ?? throw new ServiceException("PHP was not found: install PHP 8.2 or later, or name its php.exe in Settings > PHP.");
            var psi = new ProcessStartInfo(php) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in Arguments(relSlash)) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var p = Process.Start(psi) ?? throw new ServiceException("PHPStan could not be started.");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException("PHPStan did not finish in five minutes."); }
            code = p.ExitCode;
            output = stdout.Result;
            if (!output.Contains('{')) output += stderr.Result;
        }
        return Problems(output, code, relSlash);
    }

    /// <summary>The problems of PHPStan's JSON report for one file (exit code 0: none, 1: some); anything else is its failure's reason.</summary>
    public static JsonArray Problems(string output, int code, string rel)
    {
        int start = output.IndexOf("{\"totals\"", StringComparison.Ordinal);
        JsonNode? report = null;
        if (start >= 0) try { report = JsonNode.Parse(output[start..].Trim()); } catch (System.Text.Json.JsonException) { }
        // PHPStan 2.3 started by an AI coding agent writes its own report instead ({"tool":"phpstan", "error_details": {file: [{line, message,
        // identifier}]}}): read as the JSON one
        int agent = output.IndexOf("{\"tool\":\"phpstan\"", StringComparison.Ordinal);
        if (report == null && agent >= 0)
            try
            {
                var a = JsonNode.Parse(output[agent..].Trim());
                var byFile = new JsonObject();
                foreach (var (path, list) in a?["error_details"] as JsonObject ?? new JsonObject())
                    byFile[path] = new JsonObject { ["messages"] = list?.DeepClone() };
                report = new JsonObject { ["files"] = byFile, ["errors"] = a?["general_errors"]?.DeepClone() ?? new JsonArray() };
            }
            catch (System.Text.Json.JsonException) { }
        if (report == null || code is not (0 or 1))
        {
            var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('{')).ToList();
            throw new ServiceException("PHPStan could not analyse the file: " + (lines.Count > 0 ? string.Join(" ", lines.TakeLast(3)) : $"it ended with {code}"));
        }
        var problems = new JsonArray();
        if (report["files"] is JsonObject files)
            foreach (var (path, entry) in files)
            {
                // the report names files as PHP does there (absolute, this machine's or the other's): the one asked for is the one whose end it is
                if (!path.Replace('\\', '/').EndsWith("/" + rel, StringComparison.OrdinalIgnoreCase) && !path.Replace('\\', '/').Equals(rel, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var m in entry?["messages"] as JsonArray ?? new JsonArray())
                    problems.Add(new JsonObject
                    {
                        ["line"] = m?["line"]?.GetValue<int>() ?? 1,
                        ["message"] = m?["message"]?.GetValue<string>() ?? "",
                        ["identifier"] = m?["identifier"]?.GetValue<string>(),
                        ["tip"] = m?["tip"]?.GetValue<string>(),
                    });
            }
        // errors that are not of a line (a broken phpstan.neon, a class that cannot be found): on the first line
        foreach (var e in report["errors"] as JsonArray ?? new JsonArray())
            problems.Add(new JsonObject { ["line"] = 1, ["message"] = e?.GetValue<string>() ?? "" });
        return problems;
    }
}
