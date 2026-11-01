using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>A problem LanguageTool found: where (offset and length in the text sent), what, the rule, and what to write instead.</summary>
public sealed record GrammarMatch(int Offset, int Length, string Message, string RuleId, string Category, IReadOnlyList<string> Replacements);

/// <summary>
/// Grammar (item 246): PyCharm's Grazie needs a grammar checker, and PyMagic uses LanguageTool, a Java program that runs as a small server on this
/// machine. It is downloaded once, when the user says so (about 250 MB, from languagetool.org), kept in one folder of the user's
/// (<c>%APPDATA%\PyMagic\languagetool</c>, or PYMAGIC_LANGUAGETOOL), and started when the first text is checked. Nothing is sent anywhere
/// but to that local server. It needs Java (11 or newer) on the machine.
/// </summary>
public static class Grammar
{
    public const string DownloadUrl = "https://languagetool.org/download/LanguageTool-stable.zip";

    public static string Folder
    {
        get
        {
            string? own = Environment.GetEnvironmentVariable("PYMAGIC_LANGUAGETOOL");
            return !string.IsNullOrWhiteSpace(own) ? Path.GetFullPath(own) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PyMagic", "languagetool");
        }
    }

    static readonly object Gate = new();
    static Process? _server;
    static int _port;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The installed LanguageTool's folder (the one with languagetool-server.jar in it), or null.</summary>
    public static string? InstalledFolder()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            return Directory.EnumerateFiles(Folder, "languagetool-server.jar", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }).Select(Path.GetDirectoryName).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Java on this machine ("java" on the PATH, or JAVA_HOME's), as its version text, or null.</summary>
    public static string? JavaVersion()
    {
        try
        {
            var psi = new ProcessStartInfo(JavaCommand(), "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            return p.WaitForExit(15000) && p.ExitCode == 0 ? text.Split('\n')[0].Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    static string JavaCommand() => Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java")) ? Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java") : "java";

    public static bool Running { get { lock (Gate) return _server is { HasExited: false }; } }

    // ---------------------------------------------------------------- installing

    public sealed class InstallState { public long Bytes; public long Total; public string Phase = "idle"; public string? Error; }
    public static readonly InstallState Installing = new();

    /// <summary>Downloads and unpacks LanguageTool (in the background: <see cref="Installing"/> says how far it is).</summary>
    public static void StartInstall(string? url = null)
    {
        lock (Gate)
        {
            if (Installing.Phase is "downloading" or "unpacking") return;
            Installing.Bytes = 0; Installing.Total = 0; Installing.Error = null; Installing.Phase = "downloading";
        }
        _ = Task.Run(async () =>
        {
            string zip = Path.Combine(Path.GetTempPath(), "pymagic_languagetool_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var res = await client.GetAsync(url ?? DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                res.EnsureSuccessStatusCode();
                Installing.Total = res.Content.Headers.ContentLength ?? 0;
                await using (var input = await res.Content.ReadAsStreamAsync())
                await using (var output = File.Create(zip))
                {
                    var buffer = new byte[256 * 1024];
                    int n;
                    while ((n = await input.ReadAsync(buffer)) > 0) { await output.WriteAsync(buffer.AsMemory(0, n)); Installing.Bytes += n; }
                }
                Installing.Phase = "unpacking";
                string target = Folder;
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(zip, target, true);
                Installing.Phase = InstalledFolder() != null ? "done" : "failed";
                if (Installing.Phase == "failed") Installing.Error = "The download did not hold languagetool-server.jar.";
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
            {
                Installing.Error = e.Message;
                Installing.Phase = "failed";
            }
            finally { try { File.Delete(zip); } catch (IOException) { } }
        });
    }

    // ---------------------------------------------------------------- the server

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Starts LanguageTool's server if it is not running and waits until it answers. <exception cref="ServiceException">Not installed, no Java, or it did not start.</exception></summary>
    public static async Task<int> EnsureStarted()
    {
        string? dir = InstalledFolder();
        if (dir == null) throw new ServiceException("LanguageTool is not installed.");
        lock (Gate) { if (_server is { HasExited: false } && _port != 0) return _port; }
        if (JavaVersion() == null) throw new ServiceException("LanguageTool needs Java (11 or newer), which was not found.");
        int port = FreePort();
        var psi = new ProcessStartInfo(JavaCommand()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        psi.ArgumentList.Add("-Xmx1g");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(Path.Combine(dir, "*"));
        psi.ArgumentList.Add("org.languagetool.server.HTTPServer");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        // no --public: LanguageTool then listens on this machine's loopback only
        Process p;
        try { p = Process.Start(psi) ?? throw new ServiceException("Java could not be started."); }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException("Java could not be started."); }
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        for (int i = 0; i < 240 && !p.HasExited; i++)
        {
            try { using var r = await Http.GetAsync($"http://127.0.0.1:{port}/v2/languages"); if (r.IsSuccessStatusCode) { lock (Gate) { _server = p; _port = port; } return port; } }
            catch (HttpRequestException) { }
            await Task.Delay(500);
        }
        try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
        throw new ServiceException("LanguageTool did not start.");
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { if (_server is { HasExited: false }) _server.Kill(true); } catch (InvalidOperationException) { }
            _server = null; _port = 0;
        }
    }

    // ---------------------------------------------------------------- checking

    /// <summary>The problems in a text, in a language ("en-US", "de-DE", ...), without the rules named.</summary>
    public static async Task<IReadOnlyList<GrammarMatch>> Check(string text, string language, IEnumerable<string>? disabledRules = null)
    {
        int port = await EnsureStarted();
        // spelling is Windows' (item 240): LanguageTool's own word list is not asked
        var form = new Dictionary<string, string> { ["language"] = language, ["text"] = text, ["disabledCategories"] = "TYPOS" };
        string[] disabled = (disabledRules ?? Array.Empty<string>()).Where(r => r.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')).ToArray();
        if (disabled.Length > 0) form["disabledRules"] = string.Join(",", disabled);
        try
        {
            using var res = await Http.PostAsync($"http://127.0.0.1:{port}/v2/check", new FormUrlEncodedContent(form));
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new ServiceException("LanguageTool said: " + body.Trim()[..Math.Min(200, body.Trim().Length)]);
            return Matches(body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new ServiceException("LanguageTool did not answer."); }
    }

    /// <summary>The matches of LanguageTool's JSON answer.</summary>
    public static IReadOnlyList<GrammarMatch> Matches(string json)
    {
        var found = new List<GrammarMatch>();
        if (JsonNode.Parse(json)?["matches"] is not JsonArray matches) return found;
        foreach (var m in matches)
        {
            if (m == null) continue;
            found.Add(new GrammarMatch(m["offset"]?.GetValue<int>() ?? 0, m["length"]?.GetValue<int>() ?? 0, m["message"]?.GetValue<string>() ?? "",
                m["rule"]?["id"]?.GetValue<string>() ?? "", m["rule"]?["category"]?["name"]?.GetValue<string>() ?? "",
                (m["replacements"] as JsonArray)?.Take(5).Select(r => r?["value"]?.GetValue<string>() ?? "").Where(r => r.Length > 0).ToList() ?? new List<string>()));
        }
        return found;
    }

    // ---------------------------------------------------------------- the project's list of rules switched off

    public static string IgnoreFile(string root) => Path.Combine(root, ".pymagic", "grammar-ignore.txt");

    public static List<string> IgnoredRules(string root)
    {
        try { return File.Exists(IgnoreFile(root)) ? File.ReadAllLines(IgnoreFile(root)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList() : new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    public static void IgnoreRule(string root, string rule)
    {
        rule = rule.Trim();
        if (rule.Length == 0 || rule.Length > 80 || rule.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-'))) throw new ArgumentException("not a rule");
        var rules = IgnoredRules(root);
        if (rules.Contains(rule)) return;
        rules.Add(rule);
        Directory.CreateDirectory(Path.GetDirectoryName(IgnoreFile(root))!);
        File.WriteAllText(IgnoreFile(root), string.Join("\n", rules.OrderBy(r => r, StringComparer.Ordinal)) + "\n");
    }

    public static bool UnignoreRule(string root, string rule)
    {
        var rules = IgnoredRules(root);
        if (!rules.Remove(rule.Trim())) return false;
        File.WriteAllText(IgnoreFile(root), rules.Count == 0 ? "" : string.Join("\n", rules) + "\n");
        return true;
    }
}
