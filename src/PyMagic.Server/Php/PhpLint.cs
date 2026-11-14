using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// The syntax check of a PHP file while it is typed: PHP's own lint (<c>php -l</c>, as PhpStorm's "PHP lint") on the editor's text, given on its
/// standard input (no file is written). Phpactor's problems do not have PHP's syntax errors, so these are its own. PHP stops at the first one, so
/// there is one finding at most. Run by the PHP of this computer when there is one (quickest), else by Sail's container while it runs, else on the
/// machine Settings > PHP reaches by SSH.
/// </summary>
public static class PhpLint
{
    public sealed record Finding(int Line, string Message);

    /// <summary>What a run says when there is no PHP to check with.</summary>
    public const string NoPhp = "PHP's syntax is not checked: PHP was not found. Install PHP, or name its php.exe in Settings > PHP.";

    static readonly Regex Error = new(@"(?:PHP\s+)?(?:Parse|Fatal) error:\s+(?<m>.+?) in (?:Standard input code|.+?) on line (?<l>\d+)", RegexOptions.CultureInvariant);
    static readonly ConcurrentDictionary<string, Finding?> Seen = new();

    /// <summary>PHP's first syntax error of a lint's output, or null when it has none.</summary>
    public static Finding? Parse(string output)
    {
        var m = Error.Match(output);
        return m.Success ? new Finding(int.Parse(m.Groups["l"].Value), m.Groups["m"].Value.Trim()) : null;
    }

    /// <summary>The first syntax error of the text, or null; a ServiceException when it cannot be checked (<see cref="NoPhp"/>, or why).</summary>
    public static Finding? Lint(string root, string? phpSetting, string text)
    {
        if (text.Length > 2_000_000) return null;   // not checked: larger than any file typed in an editor
        var remote = PhpTools.Remote(phpSetting);
        bool sailSetting = SailTarget.IsSetting(phpSetting);
        // this computer's PHP: the one Settings > PHP names, or, when that is another machine or Sail, any found here
        string? local = remote == null && !sailSetting ? PhpTools.FindPhp(phpSetting) : PhpTools.FindPhp(null);
        if (local is "sail" || (local != null && PhpTools.Remote(local) != null)) local = null;
        string key = (local ?? phpSetting ?? "") + "\n" + text;
        if (Seen.TryGetValue(key, out var known)) return known;

        string output;
        if (local != null) output = RunLocal(local, text);
        else if (sailSetting && SailTarget.Of(root, phpSetting) is { } sail && sail.Running())
        {
            var (_, o, e) = sail.Exec("php -n -d display_errors=1 -l", 20_000, text);
            output = o + e;
        }
        else if (remote is { } r)
        {
            // over SSH there is no standard input to give: the text goes as base64 in the command (a file typed in an editor fits in it)
            string b64 = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text));
            var (_, o, e) = r.Target.Remote($"printf %s {SshTarget.Quote(b64)} | base64 -d | {SshTarget.Quote(r.Php)} -n -d display_errors=1 -l", 20_000);
            output = o + e;
        }
        else throw new ServiceException(sailSetting ? "PHP's syntax is not checked: Sail's container is not running (and there is no PHP on this computer)." : NoPhp);

        var found = Parse(output);
        if (Seen.Count > 200) Seen.Clear();
        Seen[key] = found;
        return found;
    }

    static string RunLocal(string php, string text)
    {
        // -n: without php.ini, so no extension's start-up warning (Xdebug's) mixes with the answer
        var psi = new ProcessStartInfo(php) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (var a in new[] { "-n", "-d", "display_errors=1", "-l" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("PHP could not be started.");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using (var input = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false))) input.Write(text);
            if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException("PHP's lint did not finish in 20 seconds."); }
            return stdout.Result + stderr.Result;
        }
        catch (System.ComponentModel.Win32Exception e) { throw new ServiceException($"PHP could not be started: {e.Message}"); }
    }
}
