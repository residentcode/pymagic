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
}
