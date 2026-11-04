using System.Diagnostics;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// A command line for the project: each line runs in the platform's shell in the current folder, with the project's
/// interpreter first on the PATH (a virtual environment is activated by its variables). The folder is kept between
/// lines (<c>cd</c> is handled here), output streams back as it arrives, and a running command can be fed lines or killed.
/// It is not a pseudo-terminal: programs that need one (full-screen editors) do not work, line-based ones do.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    readonly string _root;
    readonly Func<string?> _python;
    Process? _running;
    readonly object _gate = new();

    public string Cwd { get; private set; }

    /// <param name="root">The project folder, where the session starts and where <c>cd</c> without arguments goes.</param>
    /// <param name="python">The path of the project's interpreter, if one is selected.</param>
    public TerminalSession(string root, Func<string?> python)
    {
        _root = Path.GetFullPath(root);
        _python = python;
        Cwd = _root;
    }

    public bool IsRunning { get { lock (_gate) return _running is { HasExited: false }; } }

    /// <summary>Starts a command. <paramref name="output"/> receives ("out" | "err", text) chunks, <paramref name="exited"/> the exit code and the folder after it.</summary>
    /// <exception cref="InvalidOperationException">A command is still running.</exception>
    public void Run(string commandLine, Action<string, string> output, Action<int, string> exited)
    {
        lock (_gate)
        {
            if (_running is { HasExited: false }) throw new InvalidOperationException("a command is still running");
        }
        string line = commandLine.Trim();
        if (line.Length == 0) { exited(0, Cwd); return; }

        if (TryChangeFolder(line, out string? message))
        {
            if (message != null) output("err", message + "\n");
            exited(message == null ? 0 : 1, Cwd);
            return;
        }

        bool windows = OperatingSystem.IsWindows();
        var info = new ProcessStartInfo
        {
            FileName = windows ? "powershell.exe" : "/bin/sh",
            WorkingDirectory = Cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (windows)
        {
            foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command" }) info.ArgumentList.Add(a);
            // uv init makes an application here as in the Terminal panel (the shell list's uv function)
            string uv = ConsoleShells.Catalogue().FirstOrDefault(p => p.Id == "powershell")?.StartCommand is { Length: > 0 } f ? f + "; " : "";
            info.ArgumentList.Add("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + uv + line);
        }
        else { info.ArgumentList.Add("-c"); info.ArgumentList.Add(line); }
        PrepareEnvironment(info);

        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("the shell did not start"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output("err", $"The shell could not be started: {e.Message}\n");
            exited(127, Cwd);
            return;
        }
        lock (_gate) _running = process;

        var pumps = new[]
        {
            Task.Run(() => Pump(process.StandardOutput, "out", output)),
            Task.Run(() => Pump(process.StandardError, "err", output)),
        };
        Task.Run(() =>
        {
            process.WaitForExit();
            Task.WaitAll(pumps, TimeSpan.FromSeconds(2));
            int code;
            try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }
            lock (_gate) { if (ReferenceEquals(_running, process)) _running = null; }
            process.Dispose();
            exited(code, Cwd);
        });
    }

    static void Pump(StreamReader reader, string stream, Action<string, string> output)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0) output(stream, new string(buffer, 0, n));
        }
        catch (IOException) { /* the process was killed */ }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The terminal reads output through pipes, so programs think nobody is looking and print no colours; these ask them to anyway (the page draws them):
    /// `FORCE_COLOR` (rich, click, many others), `CLICOLOR_FORCE`, `PY_COLORS` (pytest), `PYTHON_COLORS` (Python 3.13's tracebacks) and git's `color.ui`.
    /// Nothing is added when the user said `NO_COLOR`, and a value the user set stays.
    /// </summary>
    public static void AskForColours(IDictionary<string, string?> env)
    {
        if (env.Keys.Any(k => string.Equals(k, "NO_COLOR", StringComparison.OrdinalIgnoreCase))) return;
        void Put(string key, string value)
        {
            if (!env.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) env[key] = value;
        }
        Put("FORCE_COLOR", "1");
        Put("CLICOLOR_FORCE", "1");
        Put("PY_COLORS", "1");
        Put("PYTHON_COLORS", "1");
        if (!env.Keys.Any(k => k.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase)))
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "color.ui";
            env["GIT_CONFIG_VALUE_0"] = "always";
        }
    }

    /// <summary>Puts the interpreter's folders first on the PATH, sets VIRTUAL_ENV for an environment, and makes Python write UTF-8 without waiting.</summary>
    void PrepareEnvironment(ProcessStartInfo info)
    {
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        AskForColours(info.Environment);
        WithInterpreter(info.Environment, _python());
    }

    /// <summary>The project's interpreter first on the PATH (with its Scripts folder), and its virtual environment active (`VIRTUAL_ENV`, no `PYTHONHOME`).</summary>
    public static void WithInterpreter(IDictionary<string, string?> env, string? python)
    {
        if (python == null || !File.Exists(python)) return;
        string folder = Path.GetDirectoryName(python)!;
        var added = new List<string> { folder };
        string? parent = Path.GetDirectoryName(folder);
        if (parent != null && File.Exists(Path.Combine(parent, "pyvenv.cfg")))
        {
            env["VIRTUAL_ENV"] = parent;
            foreach (var k in env.Keys.Where(k => string.Equals(k, "PYTHONHOME", StringComparison.OrdinalIgnoreCase)).ToList()) env.Remove(k);
        }
        else if (OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(folder, "Scripts"))) added.Add(Path.Combine(folder, "Scripts"));
        string key = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        env[key] = string.Join(Path.PathSeparator, added) + Path.PathSeparator + (env.TryGetValue(key, out var old) ? old : "");
    }

    /// <summary>`cd`, `cd path`, `Set-Location path` change the folder of the session itself (a child process cannot).</summary>
    bool TryChangeFolder(string line, out string? error)
    {
        error = null;
        string[] verbs = { "cd", "chdir", "Set-Location", "sl" };
        string? verb = verbs.FirstOrDefault(v => line.Equals(v, StringComparison.OrdinalIgnoreCase) || line.StartsWith(v + " ", StringComparison.OrdinalIgnoreCase));
        if (verb == null || line.Contains(';') || line.Contains("&&") || line.Contains('|')) return false;
        string target = line[verb.Length..].Trim().Trim('"', '\'');
        if (target.StartsWith("/d ", StringComparison.OrdinalIgnoreCase)) target = target[3..].Trim().Trim('"', '\'');
        string next;
        if (target.Length == 0 || target == "~") next = target == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _root;
        else
        {
            try { next = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Cwd, target)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { error = $"cd: invalid path '{target}'"; return true; }
        }
        if (!Directory.Exists(next)) { error = $"cd: no such folder '{target}'"; return true; }
        Cwd = next;
        return true;
    }

    /// <summary>Sends a line to the standard input of the running command.</summary>
    public void WriteLine(string text)
    {
        Process? p;
        lock (_gate) p = _running;
        if (p == null || p.HasExited) return;
        try { p.StandardInput.WriteLine(text); p.StandardInput.Flush(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Ends the running command and what it started.</summary>
    public void Kill()
    {
        Process? p;
        lock (_gate) p = _running;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => Kill();
}
