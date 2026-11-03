using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// SSH tunnels to database servers that are reached through another machine, as PyCharm's SSH/SSL tab of a data source does it:
/// the system's OpenSSH client (ssh.exe, part of Windows) forwards a free port of this computer to the server's host and port as the
/// SSH machine sees them (<c>ssh -N -L 127.0.0.1:port:host:dbport user@sshhost</c>). A tunnel is opened at the first request on its
/// server and kept for the next ones while ssh runs; it is closed when the server is disconnected or the engine stops.
/// Authentication is the user's own: a key file, ssh-agent, or a password, given to ssh by a small askpass helper (pm_askpass.cmd)
/// that prints it from the environment of that one ssh process.
/// </summary>
public static class SshTunnels
{
    sealed record Tunnel(Process Process, int Port, string Settings);

    static readonly Dictionary<string, Tunnel> Open = new();
    static readonly object Gate = new();

    /// <summary>The program started as ssh and the words before its own arguments (tests start a stand-in); null finds ssh.exe.</summary>
    public static (string Program, string[] Prefix)? Launcher { get; set; }

    static SshTunnels()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAll();
    }

    /// <summary>The OpenSSH client: Windows' own in System32\OpenSSH (it runs the askpass helper), else ssh on the PATH.</summary>
    public static string? FindSsh()
    {
        string exe = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", exe);
        if (OperatingSystem.IsWindows() && File.Exists(system)) return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string f = Path.Combine(dir.Trim('"'), exe); if (File.Exists(f)) return f; }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>The arguments of ssh for a tunnel from <paramref name="localPort"/> to the server (without the password, which never is an argument).</summary>
    public static List<string> Arguments(DbServer s, int localPort, bool withPassword)
    {
        var args = new List<string>
        {
            "-N", "-T",
            "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=30", "-o", "ConnectTimeout=10",
            // the host's key is remembered the first time (as a first connection in a terminal would after its question), checked after
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", withPassword ? "NumberOfPasswordPrompts=1" : "BatchMode=yes",
            "-L", $"127.0.0.1:{localPort}:{s.Host}:{s.Port}",
            "-p", s.SshPort.ToString(),
        };
        if (s.SshKey.Length > 0) { args.Add("-i"); args.Add(s.SshKey); }
        if (s.SshUser.Length > 0) { args.Add("-l"); args.Add(s.SshUser); }
        args.Add(s.SshHost);
        return args;
    }

    static string SettingsOf(DbServer s, string password) => string.Join("\u0001", s.SshHost, s.SshPort, s.SshUser, s.SshKey, s.Host, s.Port, password.GetHashCode());

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>The local port that reaches the server through its SSH machine: the tunnel already open, or a new one, once it accepts connections.</summary>
    /// <exception cref="ServiceException">ssh is not there, or it ended before the tunnel was up (with what it said: a refused key, a wrong password, an unknown host).</exception>
    public static int Reach(DbServer s, string password, int timeoutMs = 20000)
    {
        string settings = SettingsOf(s, password);
        lock (Gate)
        {
            if (Open.TryGetValue(s.Id, out var known))
            {
                if (!known.Process.HasExited && known.Settings == settings) return known.Port;
                Stop(known);
                Open.Remove(s.Id);
            }
        }
        string program; IEnumerable<string> prefix;
        if (Launcher is { } l) { program = l.Program; prefix = l.Prefix; }
        else
        {
            program = FindSsh() ?? throw new ServiceException("SSH needs the OpenSSH client (ssh.exe), which is part of Windows: Settings > System > Optional features > OpenSSH Client.");
            prefix = Array.Empty<string>();
        }
        int port = FreePort();
        var psi = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in Arguments(s, port, password.Length > 0)) psi.ArgumentList.Add(a);
        if (password.Length > 0)
        {
            psi.Environment["SSH_ASKPASS"] = RunSession.ExtractHelper("pm_askpass.cmd");
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            psi.Environment["DISPLAY"] = psi.Environment.TryGetValue("DISPLAY", out var d) && !string.IsNullOrEmpty(d) ? d : ":0";
            psi.Environment["PYMAGIC_SSH_PASSWORD"] = password;
        }
        Process process;
        try { process = Process.Start(psi) ?? throw new ServiceException($"'{program}' could not be started."); }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException($"The SSH client '{program}' could not be started."); }
        var said = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (said) said.AppendLine(e.Data); };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (process.HasExited)
            {
                process.WaitForExit();
                string text; lock (said) text = said.ToString().Trim();
                process.Dispose();
                throw new ServiceException($"The SSH tunnel through {s.SshHost} could not be opened" + (text.Length > 0 ? ": " + text : "."));
            }
            try
            {
                using var probe = new TcpClient();
                if (probe.ConnectAsync(IPAddress.Loopback, port).Wait(300) && probe.Connected)
                {
                    lock (Gate) Open[s.Id] = new Tunnel(process, port, settings);
                    return port;
                }
            }
            catch (AggregateException) { }
            catch (SocketException) { }
            Thread.Sleep(150);
        }
        Stop(new Tunnel(process, port, settings));
        throw new ServiceException($"The SSH tunnel through {s.SshHost} did not open in {timeoutMs / 1000} seconds.");
    }

    static void Stop(Tunnel t)
    {
        try { if (!t.Process.HasExited) t.Process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        t.Process.Dispose();
    }

    /// <summary>Closes the tunnel of a server, if it has one.</summary>
    public static void Close(string id)
    {
        lock (Gate)
            if (Open.Remove(id, out var t)) Stop(t);
    }

    public static void CloseAll()
    {
        lock (Gate)
        {
            foreach (var t in Open.Values) Stop(t);
            Open.Clear();
        }
    }

    /// <summary>Whether a tunnel of the server is open now (for the tests).</summary>
    public static bool IsOpen(string id) { lock (Gate) return Open.TryGetValue(id, out var t) && !t.Process.HasExited; }
}
