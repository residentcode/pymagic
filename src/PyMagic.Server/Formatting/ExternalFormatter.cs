using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Formatting;

/// <summary>A way to format a file: the built-in formatter, or a tool installed in the project's interpreter.</summary>
public sealed record FormatterInfo(string Id, string Label, bool Available, string? Version);

/// <summary>Runs black or ruff through the interpreter (<c>python -m black -</c>), sending the text on stdin and reading the result from stdout.</summary>
public static class ExternalFormatter
{
    public static readonly string[] Tools = { "black", "ruff" };

    /// <summary>Whether the tool is installed for this interpreter, and its version.</summary>
    public static FormatterInfo Probe(string python, string tool, ExecutionTarget? target = null)
    {
        var result = Run(python, new[] { "-m", tool, "--version" }, "", TimeSpan.FromSeconds(15), target);
        if (result.ExitCode != 0 || result.Output.Trim().Length == 0)
            return new FormatterInfo(tool, tool, false, null);
        string first = result.Output.Split('\n')[0].Trim();
        var match = Regex.Match(first, @"\d+(\.\d+)+\S*");
        return new FormatterInfo(tool, tool, true, match.Success ? match.Value : first);
    }

    /// <summary>The text formatted by the tool.</summary>
    /// <param name="filePath">Where the text lives, so that the tool finds the project's pyproject.toml.</param>
    /// <param name="lines">1-based first and last line to format (black only); null for the whole text.</param>
    /// <exception cref="ServiceException">The tool is not installed, rejects the text, or takes too long.</exception>
    public static string Format(string python, string tool, string source, string filePath, (int First, int Last)? lines = null, ExecutionTarget? target = null)
    {
        var args = new List<string> { "-m", tool };
        if (tool == "black")
        {
            args.Add("-q");
            if (lines != null)
            {
                // --line-ranges arrived in 23.11
                var info = Probe(python, "black", target);
                if (!info.Available) throw NotInstalled(python, tool);
                var v = Regex.Match(info.Version ?? "", @"^(\d+)\.(\d+)");
                if (!v.Success || int.Parse(v.Groups[1].Value) * 100 + int.Parse(v.Groups[2].Value) < 2311)
                    throw new ServiceException($"This black ({info.Version}) cannot format a selection; black 23.11 or newer can.");
                args.Add($"--line-ranges={lines.Value.First}-{lines.Value.Last}");
            }
            args.Add("--stdin-filename"); args.Add(filePath);
            args.Add("-");
        }
        else
        {
            args.Add("format");
            args.Add("--stdin-filename"); args.Add(filePath);
            args.Add("-");
        }

        var r = Run(python, args, source, TimeSpan.FromSeconds(30), target);
        if (r.ExitCode != 0)
        {
            string error = (r.Error.Trim().Length > 0 ? r.Error : r.Output).Trim();
            if (error.Contains("No module named " + tool) || error.Contains("No module named '" + tool + "'")) throw NotInstalled(python, tool);
            string message = string.Join(" ", error.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
            throw new ServiceException(message.Length > 300 ? message[..300] + "…" : (message.Length == 0 ? $"{tool} failed." : message));
        }
        // the result keeps the line endings of the text that went in, whatever the tool or the platform does to them
        return source.Contains("\r\n")
            ? Regex.Replace(r.Output, "(?<!\r)\n", "\r\n")
            : r.Output.Replace("\r\n", "\n");
    }

    /// <summary>The text with its imports sorted by ruff's own sorter (rule I, with the project's isort settings); only that rule is applied.</summary>
    /// <exception cref="ServiceException">ruff is not installed, rejects the text, or takes too long.</exception>
    public static string SortImports(string python, string source, string filePath, ExecutionTarget? target = null)
    {
        var args = new List<string> { "-m", "ruff", "check", "--select", "I", "--fix", "--exit-zero", "--quiet", "--stdin-filename", filePath, "-" };
        var r = Run(python, args, source, TimeSpan.FromSeconds(30), target);
        if (r.ExitCode != 0)
        {
            string error = (r.Error.Trim().Length > 0 ? r.Error : r.Output).Trim();
            if (error.Contains("No module named ruff") || error.Contains("No module named 'ruff'")) throw NotInstalled(python, "ruff");
            throw new ServiceException(error.Length == 0 ? "ruff failed." : string.Join(" ", error.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3)));
        }
        return source.Contains("\r\n")
            ? Regex.Replace(r.Output, "(?<!\r)\n", "\r\n")
            : r.Output.Replace("\r\n", "\n");
    }

    static ServiceException NotInstalled(string python, string tool) =>
        new($"{tool} is not installed for {python}. Install it from the Packages tab.");

    sealed record Result(int ExitCode, string Output, string Error);

    static Result Run(string python, IEnumerable<string> args, string input, TimeSpan timeout, ExecutionTarget? target = null)
    {
        // not the project folder: `python -m` would put it first on sys.path and a file called black.py there would win
        var psi = (target ?? ExecutionTarget.Local).Start(python, Path.GetTempPath(), args, new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1" });
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.StandardInputEncoding = new UTF8Encoding(false); psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false);

        try
        {
            using var process = Process.Start(psi) ?? throw new ServiceException("The Python interpreter could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(input); process.StandardInput.Close(); }
            catch (IOException) { /* the tool stopped reading: its exit code tells why */ }
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new ServiceException($"{Path.GetFileNameWithoutExtension(psi.ArgumentList.Skip(1).FirstOrDefault() ?? "the formatter")} took too long.");
            }
            return new Result(process.ExitCode, output.Result, error.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ServiceException($"The Python interpreter '{python}' could not be started.");
        }
    }
}
