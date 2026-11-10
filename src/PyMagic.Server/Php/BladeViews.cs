using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Blade templates under the debugger: Laravel runs a template as the PHP file it compiles it into (storage/framework/views/HASH.php), whose
/// last line names the template (<c>/**PATH …\welcome.blade.php ENDPATH**/</c>). A breakpoint of a template is set on its compiled file, and a
/// stop in a compiled file is shown in its template. Their lines are matched by the lines Blade leaves as they are (HTML, text): a line between
/// two of them is as far from the one before as in the template (Blade compiles most directives and echoes in their own line; a component's
/// tag may take more lines, and the lines after it are matched again by the next line that is the same).
/// </summary>
public static class BladeViews
{
    sealed record Compiled(long Stamp, string? Source, List<(int Source, int Compiled)> Anchors);

    static readonly ConcurrentDictionary<string, Compiled> Known = new(StringComparer.OrdinalIgnoreCase);
    static readonly Regex EndPath = new(@"/\*\*PATH (.+?) ENDPATH\*\*/", RegexOptions.Compiled);

    /// <summary>Folders here that hold copies of another machine's compiled views, and how a template's path there is its file here (null: none here).</summary>
    static readonly ConcurrentDictionary<string, Func<string, string?>> SourceMaps = new(StringComparer.OrdinalIgnoreCase);

    public static void MapSources(string views, Func<string, string?>? map)
    {
        string key = Canonical(views);
        if (map == null) SourceMaps.TryRemove(key, out _); else SourceMaps[key] = map;
        foreach (var k in Known.Keys.Where(k => k.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList()) Known.TryRemove(k, out _);
    }

    public static bool IsTemplate(string? file) => file != null && file.EndsWith(".blade.php", StringComparison.OrdinalIgnoreCase);

    static string Canonical(string path) { try { return Path.GetFullPath(path).TrimEnd('\\', '/'); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; } }

    static Compiled? Read(string compiled)
    {
        long stamp;
        try { if (!File.Exists(compiled)) return null; stamp = File.GetLastWriteTimeUtc(compiled).Ticks; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (Known.TryGetValue(compiled, out var known) && known.Stamp == stamp) return known;
        Compiled result;
        try
        {
            string text = File.ReadAllText(compiled);
            var m = EndPath.Match(text, Math.Max(0, text.Length - 2048));
            string? source = m.Success ? m.Groups[1].Value.Trim() : null;
            // a copy of another machine's compiled view: its template's path there is the project's file here
            if (source != null && SourceMaps.TryGetValue(Canonical(Path.GetDirectoryName(compiled)!), out var map)) source = map(source);
            if (source != null) source = Canonical(source);
            var anchors = new List<(int, int)>();
            if (source != null && File.Exists(source)) anchors = Align(File.ReadAllLines(source), text.Split('\n'));
            result = new Compiled(stamp, source, anchors);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        Known[compiled] = result;
        return result;
    }

    /// <summary>The lines (1-based) of the template that are in the compiled file as they are, in order: (template line, compiled line).</summary>
    static List<(int, int)> Align(string[] source, string[] compiled)
    {
        var anchors = new List<(int, int)> { (1, 1) };
        int c = 0;
        for (int s = 0; s < source.Length; s++)
        {
            string line = source[s].Trim();
            // a line of Blade's own changes when compiled; an empty line or a bracket alone is the same in too many places
            if (line.Length < 3 || line.Contains('@') || line.Contains("{{") || line.Contains("{!!") || line.Contains("<x-") || line.Contains("</x-") || line.Contains("<?")) continue;
            for (int k = c; k < Math.Min(compiled.Length, c + 400); k++)
                if (compiled[k].TrimEnd('\r').Trim() == line) { anchors.Add((s + 1, k + 1)); c = k + 1; break; }
        }
        return anchors;
    }

    /// <summary>The template a compiled view was made from, or null for any other file.</summary>
    public static string? TemplateOf(string compiledFile) => IsCompiled(compiledFile) ? Read(compiledFile)?.Source : null;

    static bool IsCompiled(string file) => file.EndsWith(".php", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(file.Replace('\\', '/'), @"/storage/framework/views/[^/]+\.php$", RegexOptions.IgnoreCase);

    /// <summary>A place of a compiled view as its template's (file, line); the place itself when it is not a compiled view.</summary>
    public static (string File, int Line) ToTemplate(string file, int line)
    {
        if (!IsCompiled(file) || Read(file) is not { Source: { } source } c) return (file, line);
        var before = c.Anchors.LastOrDefault(a => a.Compiled <= line);
        var after = c.Anchors.FirstOrDefault(a => a.Compiled > line);
        int s = before.Source + (line - before.Compiled);
        if (after.Source > 0 && s >= after.Source) s = Math.Max(before.Source, after.Source - 1);
        return (source, Math.Max(1, s));
    }

    /// <summary>
    /// The compiled files of a template in the project (storage/framework/views): none when Laravel has not compiled it yet, and more than one when
    /// it was compiled under two spellings of its path (`php artisan view:cache` and the server name it differently on Windows).
    /// </summary>
    public static List<string> CompiledOf(string root, string template) => CompiledIn(Path.Combine(root, "storage", "framework", "views"), template);

    /// <summary>The compiled files of a template in a folder of compiled views.</summary>
    public static List<string> CompiledIn(string views, string template)
    {
        var found = new List<string>();
        if (!Directory.Exists(views)) return found;
        string wanted = Canonical(template);
        try
        {
            foreach (var f in Directory.EnumerateFiles(views, "*.php"))
                if (Read(f) is { Source: { } s } && string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase)) found.Add(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>A line of a template as the line of its compiled file.</summary>
    public static int ToCompiled(string compiled, int line)
    {
        if (Read(compiled) is not { } c) return line;
        var before = c.Anchors.LastOrDefault(a => a.Source <= line);
        var after = c.Anchors.FirstOrDefault(a => a.Source > line);
        int k = before.Compiled + (line - before.Source);
        if (after.Compiled > 0 && k >= after.Compiled) k = Math.Max(before.Compiled, after.Compiled - 1);
        return Math.Max(1, k);
    }
}

/// <summary>
/// The compiled views of PHP on another machine (its storage/framework/views, which stay there): copied into a folder here as they change, so a
/// Blade template's breakpoints are set in the files that machine runs and its stops are shown in the template, as with PHP of this computer.
/// </summary>
public sealed class RemoteBladeViews : IDisposable
{
    readonly SshTarget _ssh;
    readonly string _top;
    readonly Dictionary<string, string> _stamps = new(StringComparer.Ordinal);
    readonly object _gate = new();
    DateTime _checked = DateTime.MinValue;

    /// <summary>The folder here that holds the copies (it ends in storage/framework/views, as Laravel's own does).</summary>
    public string LocalDir { get; }

    /// <summary>The compiled views' folder there.</summary>
    public string RemoteDir { get; }

    public RemoteBladeViews(SshTarget ssh)
    {
        _ssh = ssh;
        _top = Path.Combine(Path.GetTempPath(), "pymagic", "blade-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        LocalDir = Path.Combine(_top, "storage", "framework", "views");
        RemoteDir = ssh.ProjectDir + "/storage/framework/views";
        Directory.CreateDirectory(LocalDir);
        // a view compiled there names its template by its path there; one copied there from this computer's folder names a path here: not that machine's
        BladeViews.MapSources(LocalDir, path => path.StartsWith('/') ? ssh.ToHost(path) : null);
    }

    /// <summary>Copies the compiled views that changed there since the last look (at most every second and a half, unless <paramref name="now"/>).</summary>
    public void Refresh(bool now = false)
    {
        lock (_gate)
        {
            if (!now && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(1.5)) return;
            _checked = DateTime.UtcNow;
            string output;
            try { output = _ssh.Remote($"cd {SshTarget.Quote(RemoteDir)} 2>/dev/null && stat -c '%Y %n' -- *.php 2>/dev/null; true", 20_000).Out; }
            catch (ServiceException) { return; }
            var there = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int sp = line.IndexOf(' ');
                if (sp > 0 && line[(sp + 1)..] is { } name && name.EndsWith(".php") && !name.Contains('/')) there[name] = line[..sp];
            }
            var changed = there.Where(kv => !_stamps.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();
            if (changed.Count > 0)
                try
                {
                    if (_ssh.FetchFiles(RemoteDir, changed, LocalDir)) foreach (var name in changed) _stamps[name] = there[name];
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            foreach (var gone in _stamps.Keys.Where(k => !there.ContainsKey(k)).ToList())
            {
                _stamps.Remove(gone);
                try { File.Delete(Path.Combine(LocalDir, gone)); } catch (IOException) { }
            }
        }
    }

    /// <summary>The copy here of a compiled view there (copied first when it is not here yet), or null for any other path.</summary>
    public string? LocalOf(string remotePath)
    {
        if (!remotePath.StartsWith(RemoteDir + "/", StringComparison.Ordinal)) return null;
        string name = remotePath[(RemoteDir.Length + 1)..];
        if (name.Contains('/') || !name.EndsWith(".php")) return null;
        string local = Path.Combine(LocalDir, name);
        if (!File.Exists(local)) Refresh(now: true);
        return local;
    }

    /// <summary>The compiled view there of a copy here, or null for any other file.</summary>
    public string? RemoteOf(string localPath)
    {
        string full = Path.GetFullPath(localPath);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(LocalDir), StringComparison.OrdinalIgnoreCase) ? RemoteDir + "/" + Path.GetFileName(full) : null;
    }

    public void Dispose()
    {
        BladeViews.MapSources(LocalDir, null);
        try { Directory.Delete(_top, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
