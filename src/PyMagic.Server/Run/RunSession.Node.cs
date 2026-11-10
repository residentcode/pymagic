using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// JavaScript and TypeScript run by Node.js: a file (`node file`) or an npm script of a package.json (`npm run name`), with or without the
/// debugger. Both go through pm_nodedebug.mjs, which starts the program (and, under the debugger, speaks to PyMagic as pm_debug.py does and to
/// each Node process of the run through Node's inspector), so the Run panel, the debugger's panes, Stop and the Services window work as for
/// Python. Node is the one on the PATH, or the one a run configuration names.
/// </summary>
public sealed partial class RunSession
{
    /// <summary>The Node.js program to run with: a path or "node" (the PATH's).</summary>
    public string? Node { get; set; }

    static readonly string[] NodeExtensions = { ".js", ".mjs", ".cjs", ".ts", ".mts", ".cts" };

    /// <summary>A helper written into another helper's folder (one that imports it), when it is not there as it is.</summary>
    static void ExtractBeside(string name, string folder)
    {
        using var s = typeof(RunSession).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " missing from the assembly");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        string path = Path.Combine(folder, name);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
    }

    /// <summary>Whether a file is run by Node rather than Python.</summary>
    public static bool IsNodeFile(string file) => NodeExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs or debugs a JavaScript or TypeScript file, or an npm script (<paramref name="npmScript"/>, in <paramref name="packageDir"/>, the
    /// folder of its package.json). Node 22.18 and later run .ts files themselves (their types left out, the lines as written).
    /// </summary>
    public void StartNode(string root, string? script, string? npmScript, string? packageDir, bool debug, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        _root = root;
        IsDebug = debug;
        if (!Target.IsLocal) { Error("JavaScript runs on this computer only; the interpreter chosen is elsewhere (WSL, Docker or SSH)."); return; }
        string? scriptPath = null;
        if (script != null)
        {
            scriptPath = Path.GetFullPath(Path.Combine(root, script));
            if (!File.Exists(scriptPath)) { Error($"script not found: {script}"); return; }
        }
        else if (npmScript == null || !System.Text.RegularExpressions.Regex.IsMatch(npmScript, @"^[\w:.@/+-]+$"))
        {
            Error("an npm script's name, as package.json has it");
            return;
        }
        string folder = packageDir != null ? Path.GetFullPath(Path.Combine(root, packageDir)) : root;
        if (npmScript != null && !File.Exists(Path.Combine(folder, "package.json"))) { Error($"there is no package.json in {packageDir ?? "the project folder"}"); return; }

        string helper = ExtractHelper("pm_nodedebug.mjs");
        ExtractBeside("pm_sourcemap.mjs", Path.GetDirectoryName(helper)!);   // the helper imports it from its own folder
        var helperArgs = new List<string> { helper };
        string? token = null;
        if (debug)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            helperArgs.AddRange(new[] { "--port", ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(), "--token", token, "--root", root });
            if (AllCode) helperArgs.Add("--all-code");
        }
        if (scriptPath != null) { helperArgs.Add("--script"); helperArgs.Add(scriptPath); }
        else { helperArgs.Add("--npm"); helperArgs.Add(npmScript!); }
        helperArgs.Add("--");
        helperArgs.AddRange(args);

        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(Node) ? "node" : Node!)
        {
            // an npm script runs in its package.json's folder; a file in the folder a configuration names, else in the project
            WorkingDirectory = npmScript != null ? folder : WorkingDirectory ?? root,
        };
        foreach (var a in helperArgs) psi.ArgumentList.Add(a);
        foreach (var (key, value) in ExtraEnvironment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        _runId = null;
        StartProcess(psi, root, token, breakpoints);
    }

    /// <summary>
    /// JavaScript Debug: a page of a dev server (Vite's, Next.js's) opened in Edge or Chrome under the debugger. Its scripts' source maps give
    /// the project's files; <paramref name="webRoot"/> (a folder of the project) is the folder the page's addresses are files of.
    /// </summary>
    public void StartBrowser(string root, string url, string? webRoot, string? browserPath, IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        _root = root;
        IsDebug = true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https" or "file")) { Error("an address to open: http://localhost:5173/"); return; }
        string web = webRoot != null ? Path.GetFullPath(Path.Combine(root, webRoot)) : root;
        string helper = ExtractHelper("pm_nodedebug.mjs");
        ExtractBeside("pm_sourcemap.mjs", Path.GetDirectoryName(helper)!);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(Node) ? "node" : Node!) { WorkingDirectory = web };
        foreach (var a in new[] { helper, "--port", ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(), "--token", token, "--root", root, "--web-root", web, "--browser", page.ToString() })
            psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(browserPath)) { psi.ArgumentList.Add("--browser-path"); psi.ArgumentList.Add(browserPath); }
        if (AllCode) psi.ArgumentList.Add("--all-code");
        _runId = null;
        StartProcess(psi, root, token, breakpoints);
    }

    /// <summary>
    /// The Node.js programs running on this computer, for Attach to Process: pid, its script (from its command line), the inspector's port it
    /// opened itself (--inspect=PORT; 9229 when it names none, or when it has none: PyMagic opens it there); PyMagic's own helpers left out.
    /// </summary>
    public static List<JsonObject> NodeProcesses()
    {
        var list = new List<JsonObject>();
        var commandLines = new Dictionary<int, string>();
        if (OperatingSystem.IsWindows())
            try
            {
                var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='node.exe'\\\" | ForEach-Object { [string]$_.ProcessId + [char]9 + $_.CommandLine }\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                string output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(15_000);
                foreach (var line in output.Split('\n'))
                    if (line.Split('\t', 2) is [var id, var cmd] && int.TryParse(id.Trim(), out int pid)) commandLines[pid] = cmd.Trim();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        foreach (var p in Process.GetProcessesByName("node"))
        {
            try
            {
                string command = commandLines.TryGetValue(p.Id, out var c) ? c : "";
                if (command.Contains("pm_nodedebug.mjs") || command.Contains("pm_nodeboot") || command.Contains("pymagic-node-")) continue;
                var inspect = System.Text.RegularExpressions.Regex.Match(command, @"--inspect(?:-brk|-wait)?(?:=(?:[\w.\-]+:)?(\d+))?(?=\s|$)");
                // the script: the first argument after node's own options that names a file
                string? script = System.Text.RegularExpressions.Regex.Matches(command, @"""([^""]+)""|(\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
                string? started = null;
                try { started = p.StartTime.ToString("HH:mm:ss"); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                list.Add(new JsonObject
                {
                    ["pid"] = p.Id, ["name"] = "node", ["kind"] = "node", ["title"] = script != null ? Path.GetFileName(script) : null, ["path"] = command.Length > 0 ? command : null, ["started"] = started,
                    ["inspectPort"] = inspect.Success ? (inspect.Groups[1].Success ? int.Parse(inspect.Groups[1].Value) : 9229) : null,
                });
            }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return list.OrderByDescending(o => o["pid"]!.GetValue<int>()).ToList();
    }

    /// <summary>
    /// Attach to Node.js: the debugger joins a Node process already running, through its inspector at <paramref name="host"/>:<paramref name="port"/>
    /// (started with --inspect, or opened by PyMagic in the process <paramref name="pid"/> of this computer first). Stop lets go of it and the
    /// program goes on; its output stays where it runs. <paramref name="webRoot"/> is the folder its source maps' files are of.
    /// </summary>
    public void StartNodeAttach(string root, string host, int port, int? pid, string? webRoot, IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        _root = root;
        IsDebug = true;
        _attached = true;   // Stop asks the helper to let go (the program is not PyMagic's to end)
        if (string.IsNullOrWhiteSpace(host) || !System.Text.RegularExpressions.Regex.IsMatch(host, @"^[\w.\-:\[\]]+$")) { Error("the address of the inspector: 127.0.0.1"); return; }
        if (port is <= 0 or > 65535) { Error("the port of the inspector: 9229 unless the program says another (--inspect=PORT)"); return; }
        string web = webRoot != null ? Path.GetFullPath(Path.Combine(root, webRoot)) : root;
        string helper = ExtractHelper("pm_nodedebug.mjs");
        ExtractBeside("pm_sourcemap.mjs", Path.GetDirectoryName(helper)!);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(Node) ? "node" : Node!) { WorkingDirectory = web };
        foreach (var a in new[] { helper, "--port", ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(), "--token", token, "--root", root, "--web-root", web, "--attach", $"{host}:{port}" })
            psi.ArgumentList.Add(a);
        if (pid is > 0) { psi.ArgumentList.Add("--attach-pid"); psi.ArgumentList.Add(pid.Value.ToString()); }
        if (AllCode) psi.ArgumentList.Add("--all-code");
        _runId = null;
        StartProcess(psi, root, token, breakpoints);
    }
}
