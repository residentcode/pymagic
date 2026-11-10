using System.Diagnostics;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// Format of a PHP file with the project's own formatter: Laravel's Pint (vendor/bin/pint, with the project's pint.json) or PHP CS Fixer
/// (vendor/bin/php-cs-fixer, with its .php-cs-fixer(.dist).php), run by the PHP of Settings > PHP on this computer or on the machine reached by
/// SSH. Neither reads standard input: the editor's text is written to a file of its own, formatted there, and read back.
/// </summary>
public static class PhpFormat
{
    /// <summary>The project's formatter: (its name, its script under vendor/bin), or null when it has neither.</summary>
    public static (string Name, string Script)? Tool(string root)
    {
        if (File.Exists(Path.Combine(root, "vendor", "bin", "pint"))) return ("Pint", "vendor/bin/pint");
        if (File.Exists(Path.Combine(root, "vendor", "bin", "php-cs-fixer"))) return ("PHP CS Fixer", "vendor/bin/php-cs-fixer");
        return null;
    }

    /// <summary>PHP's arguments for the formatter on one file: its settings file of the project (a path as the machine that runs it names it).</summary>
    public static List<string> Arguments(string root, (string Name, string Script) tool, string file, Func<string, string> pathThere)
    {
        var args = new List<string> { tool.Script };
        if (tool.Name == "Pint")
        {
            if (File.Exists(Path.Combine(root, "pint.json"))) { args.Add("--config"); args.Add(pathThere(Path.Combine(root, "pint.json"))); }
        }
        else
        {
            args.AddRange(new[] { "fix", "--using-cache=no", "--show-progress=none" });
            foreach (var name in new[] { ".php-cs-fixer.php", ".php-cs-fixer.dist.php" })
                if (File.Exists(Path.Combine(root, name))) { args.Add("--config=" + pathThere(Path.Combine(root, name))); break; }
        }
        args.Add(file);
        return args;
    }

    /// <summary>The text formatted by the project's formatter; a ServiceException says why it could not be.</summary>
    public static string Format(string root, string? phpSetting, string text)
    {
        var tool = Tool(root) ?? throw new ServiceException("The project has no PHP formatter: add Laravel's Pint (composer require --dev laravel/pint) or PHP CS Fixer (composer require --dev friendsofphp/php-cs-fixer).");
        if (SailTarget.Of(root, phpSetting) is { } sail)
        {
            if (!sail.Running()) sail.Up();
            return FormatThere(SailTarget.Home, sail.ToTarget, (command, input) => sail.Exec(command, 120_000, input), "php", "Sail's container", tool, text, root);
        }
        if (PhpTools.Remote(phpSetting) is { } remote)
        {
            var ssh = remote.Target;
            ssh.Project = root;
            ssh.Prepare(root, null, null);
            return FormatThere(ssh.ProjectDir, ssh.ToTarget, (command, input) => ssh.RemoteWithInput(command, input, 120_000), remote.Php, ssh.Destination, tool, text, root);
        }
        return FormatHere(root, phpSetting, tool, text);
    }

    static string FormatHere(string root, string? phpSetting, (string Name, string Script) tool, string text)
    {
        string php = PhpTools.FindPhp(phpSetting) ?? throw new ServiceException("PHP was not found: install PHP 8.2 or later, or name its php.exe in Settings > PHP.");
        string dir = Path.Combine(Path.GetTempPath(), "pymagic", "format-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "code.php");
        try
        {
            File.WriteAllText(file, text, new UTF8Encoding(false));
            var psi = new ProcessStartInfo(php) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in Arguments(root, tool, file, p => p)) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            // PHP CS Fixer refuses a PHP newer than it knows unless told it may
            psi.Environment["PHP_CS_FIXER_IGNORE_ENV"] = "1";
            using var p = Process.Start(psi) ?? throw new ServiceException($"{tool.Name} could not be started.");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException($"{tool.Name} did not finish in two minutes."); }
            Fail(tool.Name, p.ExitCode, stdout.Result + stderr.Result);
            return File.ReadAllText(file);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>The formatter where the project is run (an SSH machine, Sail's container: <paramref name="there"/>), the text through a shell command's standard input.</summary>
    static string FormatThere(string there, Func<string, string> pathThere, Func<string, string, (int Code, string Out, string Err)> run, string php, string where, (string Name, string Script) tool, string text, string root)
    {
        // the text through standard input into a file of its own there, formatted, and the file's text after a marker line
        const string Marker = "--- pymagic formatted ---";
        string args = string.Join(" ", Arguments(root, tool, "\"$t\"", pathThere).Select(a => a == "\"$t\"" ? a : SshTarget.Quote(a)));
        string command = $"cd {SshTarget.Quote(there)} && t=$(mktemp -d)/code.php && cat > \"$t\" && PHP_CS_FIXER_IGNORE_ENV=1 {SshTarget.Quote(php)} {args} 2>&1; c=$?; echo {SshTarget.Quote(Marker)}; cat \"$t\"; rm -rf \"$(dirname \"$t\")\"; exit $c";
        var (code, output, err) = run(command, text);
        int at = output.IndexOf(Marker + "\n", StringComparison.Ordinal);
        if (at < 0) throw new ServiceException($"{tool.Name} did not run on {where}: {(output + err).Trim()}");
        Fail(tool.Name, code, output[..at]);
        return output[(at + Marker.Length + 1)..];
    }

    /// <summary>A formatter's failure (a file it could not read as PHP, a broken settings file) as the reason it gives.</summary>
    static void Fail(string tool, int code, string output)
    {
        // Pint ends with 0 (also when it changed the file); PHP CS Fixer with 0, or 8 in its dry run
        bool parseError = output.Contains("\"result\":\"error\"", StringComparison.Ordinal) || output.Contains("Parse error", StringComparison.OrdinalIgnoreCase) || output.Contains("syntax error", StringComparison.OrdinalIgnoreCase);
        if (code == 0 && !parseError) return;
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string why = parseError ? "the file is not valid PHP (fix its syntax errors first)" : string.Join(" ", lines.TakeLast(3));
        throw new ServiceException($"{tool} could not format the file: {why}");
    }
}
