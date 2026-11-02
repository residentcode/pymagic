using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// The package index settings of a project (<c>.pymagic/packages.json</c>): another index, extra indexes, hosts to trust without
/// https, and a proxy. They become options of every pip call that reaches an index (install, outdated, versions).
/// The user name and password of an address (<c>https://user:secret@host/simple</c>) are kept apart, like the passwords of Deployment:
/// packages.json has <c>https://user:***@host/simple</c> and the secret is in <c>.pymagic/packages.private.json</c>, out of version control.
/// The page only ever sees the masked address (<see cref="Masked"/>); <see cref="Load"/> gives the whole one, for pip.
/// </summary>
public sealed class PackageSettings
{
    public string IndexUrl { get; set; } = "";
    public List<string> ExtraIndexUrls { get; set; } = new();
    public List<string> TrustedHosts { get; set; } = new();
    public string Proxy { get; set; } = "";

    static string PathOf(string root) => Path.Combine(root, ".pymagic", "packages.json");
    static string PrivatePathOf(string root) => Path.Combine(root, ".pymagic", "packages.private.json");

    /// <summary>What stands for a password (or a token given as the user name) in an address that is shown or saved.</summary>
    public const string Mask = "***";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public bool IsEmpty => IndexUrl.Length == 0 && ExtraIndexUrls.Count == 0 && TrustedHosts.Count == 0 && Proxy.Length == 0;

    /// <summary>The settings with the passwords put back (what pip is given). A packages.json written by hand with a password in it is rewritten with the password moved out.</summary>
    public static PackageSettings Load(string root)
    {
        PackageSettings s;
        try
        {
            s = JsonSerializer.Deserialize<PackageSettings>(File.ReadAllText(PathOf(root)), Json) ?? new PackageSettings();
            s.Clean();
            if (Validate(s) != null) return new PackageSettings();   // a hand-edited file with a bad value is not used
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new PackageSettings(); }
        bool plain = s.Addresses().Any(u => SplitCredentials(u).Secret != null);
        s.Restore(LoadSecrets(root));
        if (plain) { try { Save(root, s); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ServiceException) { } }
        return s;
    }

    /// <summary>Keeps the settings. An address that comes back from the page masked gets its kept password again; a new password goes to packages.private.json.</summary>
    public static void Save(string root, PackageSettings settings)
    {
        settings.Clean();
        settings.Restore(LoadSecrets(root));
        if (Validate(settings) is { } problem) throw new ServiceException(problem);
        Directory.CreateDirectory(Path.Combine(root, ".pymagic"));
        var secrets = new Dictionary<string, string>();
        foreach (var u in settings.Addresses())
            if (SplitCredentials(u) is { Secret: { } secret } split) secrets[split.Masked] = secret;
        if (secrets.Count > 0)
        {
            // the ignore entry first: the password is never in a file git could pick up
            PymagicFolder.IgnoreInGit(Path.Combine(root, ".pymagic"), "packages.private.json");
            File.WriteAllText(PrivatePathOf(root), JsonSerializer.Serialize(secrets));
        }
        else { try { File.Delete(PrivatePathOf(root)); } catch (IOException) { } }
        if (settings.IsEmpty) { try { File.Delete(PathOf(root)); } catch (IOException) { } return; }
        File.WriteAllText(PathOf(root), JsonSerializer.Serialize(settings.Masked(), Json));
    }

    /// <summary>A copy with every user name and password replaced by <see cref="Mask"/> (a user name next to a password stays).</summary>
    public PackageSettings Masked() => new()
    {
        IndexUrl = SplitCredentials(IndexUrl).Masked,
        ExtraIndexUrls = ExtraIndexUrls.Select(u => SplitCredentials(u).Masked).ToList(),
        TrustedHosts = TrustedHosts.ToList(),
        Proxy = SplitCredentials(Proxy).Masked,
    };

    /// <summary>
    /// The credentials of an address: <c>https://user:secret@host/x</c> is shown as <c>https://user:***@host/x</c>, and a lone
    /// name (often a token) as <c>https://***@host/x</c>. An address without credentials, or one masked already, has no secret.
    /// </summary>
    public static (string Masked, string? Secret) SplitCredentials(string url)
    {
        var m = Regex.Match(url ?? "", @"^([A-Za-z][A-Za-z0-9+.\-]*://)([^/?#@]+)@(.*)$", RegexOptions.Singleline);
        if (!m.Success) return (url ?? "", null);
        string info = m.Groups[2].Value;
        int colon = info.IndexOf(':');
        string shown = colon >= 0 ? info[..colon] + ":" + Mask : Mask;
        return info == shown ? (url!, null) : (m.Groups[1].Value + shown + "@" + m.Groups[3].Value, info);
    }

    /// <summary>Text with the passwords of any addresses in it masked (an error line of pip or uv, a command line).</summary>
    public static string MaskText(string text) =>
        Regex.Replace(text ?? "", @"[A-Za-z][A-Za-z0-9+.\-]*://[^\s/?#@]+@[^\s]*", m => SplitCredentials(m.Value).Masked);

    IEnumerable<string> Addresses() => new[] { IndexUrl, Proxy }.Concat(ExtraIndexUrls).Where(u => u.Length > 0);

    static Dictionary<string, string> LoadSecrets(string root)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PrivatePathOf(root))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Puts the kept credentials back into masked addresses (one with no kept password stays masked, and pip is refused by the index).</summary>
    void Restore(IReadOnlyDictionary<string, string> secrets)
    {
        string Back(string u)
        {
            if (!secrets.TryGetValue(u, out var secret)) return u;
            var m = Regex.Match(u, @"^([A-Za-z][A-Za-z0-9+.\-]*://)[^/?#@]+@(.*)$", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value + secret + "@" + m.Groups[2].Value : u;
        }
        IndexUrl = Back(IndexUrl);
        Proxy = Back(Proxy);
        ExtraIndexUrls = ExtraIndexUrls.Select(Back).ToList();
    }

    void Clean()
    {
        IndexUrl = (IndexUrl ?? "").Trim();
        Proxy = (Proxy ?? "").Trim();
        ExtraIndexUrls = (ExtraIndexUrls ?? new()).Select(u => (u ?? "").Trim()).Where(u => u.Length > 0).Distinct().ToList();
        TrustedHosts = (TrustedHosts ?? new()).Select(u => (u ?? "").Trim()).Where(u => u.Length > 0).Distinct().ToList();
    }

    /// <summary>A web address (http or https; a proxy may also be a socks one) that cannot be read as an option.</summary>
    public static bool IsAddress(string text, bool proxy = false)
    {
        if (text.Length is 0 or > 500 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return false;
        return uri.Scheme is "http" or "https" || (proxy && uri.Scheme is "socks5" or "socks5h" or "socks4");
    }

    public static bool IsHost(string text) => Regex.IsMatch(text, @"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?(:\d{1,5})?$");

    public static string? Validate(PackageSettings s)
    {
        if (s.IndexUrl.Length > 0 && !IsAddress(s.IndexUrl)) return "the index URL must be an http:// or https:// address";
        foreach (var u in s.ExtraIndexUrls) if (!IsAddress(u)) return $"'{u}' is not an http:// or https:// address";
        foreach (var h in s.TrustedHosts) if (!IsHost(h)) return $"'{h}' is not a host name (name or name:port)";
        if (s.Proxy.Length > 0 && !IsAddress(s.Proxy, proxy: true)) return "the proxy must be an address such as http://proxy.example:8080";
        return null;
    }

    /// <summary>The options pip is given for these settings (for a command that reaches an index).</summary>
    public List<string> PipArgs()
    {
        var args = new List<string>();
        if (IndexUrl.Length > 0) { args.Add("--index-url"); args.Add(IndexUrl); }
        foreach (var u in ExtraIndexUrls) { args.Add("--extra-index-url"); args.Add(u); }
        foreach (var h in TrustedHosts) { args.Add("--trusted-host"); args.Add(h); }
        if (Proxy.Length > 0) { args.Add("--proxy"); args.Add(Proxy); }
        return args;
    }
}

/// <summary>Builds the arguments of the pip commands the page can ask for (never raw text from the page).</summary>
public static class PipCommands
{
    static readonly string[] Quiet = { "--disable-pip-version-check", "--no-input", "--progress-bar", "off" };

    public static List<string> Install(IEnumerable<string> specs, bool upgrade, bool user, bool isVenv, PackageSettings? settings)
    {
        var args = new List<string> { "install" };
        args.AddRange(Quiet);
        if (upgrade) args.Add("--upgrade");
        if (user && !isVenv) args.Add("--user");
        if (settings != null) args.AddRange(settings.PipArgs());
        args.AddRange(specs);
        return args;
    }

    /// <summary>Installing a path, a git address or a link: only for a source that was checked and confirmed (see <see cref="PackageSource"/>).</summary>
    public static List<string> InstallSource(PackageSource.Check source, bool user, bool isVenv, PackageSettings? settings)
    {
        var args = new List<string> { "install" };
        args.AddRange(Quiet);
        if (user && !isVenv) args.Add("--user");
        if (settings != null) args.AddRange(settings.PipArgs());
        if (source.Editable) args.Add("-e");
        args.Add(source.Source);
        return args;
    }

    /// <summary>`pip list --outdated` and `pip index versions` reach the index too and take the same options.</summary>
    public static List<string> WithIndex(IEnumerable<string> args, PackageSettings? settings)
    {
        var list = args.ToList();
        if (settings != null) list.AddRange(settings.PipArgs());
        return list;
    }
}

/// <summary>
/// A path, git address or link to install from. Installing builds the project, which runs its code, so the page first asks
/// for a <see cref="Check"/> (what it is, what will happen) and shows it; the install is only accepted with the token of that
/// check, once.
/// </summary>
public static class PackageSource
{
    public sealed record Check(string Kind, string Source, bool Editable, string Title, IReadOnlyList<string> Details, IReadOnlyList<string> Warnings);

    static readonly Regex GitAddress = new(@"^git\+(https?|ssh|file)://[^\s""'<>|&;$`\\]+$", RegexOptions.Compiled);
    static readonly Regex LinkAddress = new(@"^https?://[^\s""'<>|&;$`\\]+\.(whl|tar\.gz|zip)(#[A-Za-z0-9=_.\-]+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Check Inspect(string root, string text, bool editable)
    {
        text = (text ?? "").Trim().Trim('"', '\'');
        if (text.Length == 0) throw new ServiceException("nothing to install from");
        if (text.StartsWith('-') || text.Any(c => char.IsControl(c))) throw new ServiceException("that is not a path or an address");
        if (text.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) return Git(text, editable);
        if (text.Contains("://")) return Link(text, editable);
        return LocalPath(root, text, editable);
    }

    static Check Git(string text, bool editable)
    {
        if (!GitAddress.IsMatch(text)) throw new ServiceException("a git address looks like git+https://host/user/project.git@tag (https, http, ssh or file)");
        var uri = new Uri(text[4..]);
        string path = uri.AbsolutePath;
        string? revision = null;
        int at = path.LastIndexOf('@');
        if (at > 0) revision = path[(at + 1)..];
        var details = new List<string> { $"Repository: {uri.GetLeftPart(UriPartial.Authority)}{(at > 0 ? path[..at] : path)}" };
        details.Add(revision != null ? $"Revision: {revision}" : "Revision: the default branch");
        if (uri.Fragment.Contains("subdirectory=")) details.Add("Subdirectory: " + uri.Fragment.Split("subdirectory=")[1].Split('&')[0]);
        var warnings = new List<string> { "pip clones the repository and builds the project: its build scripts run on this computer." };
        if (revision == null) warnings.Add("The address names no tag or commit, so what is installed changes as the branch does.");
        else if (!Regex.IsMatch(revision, @"^[0-9a-f]{7,40}$") && !Regex.IsMatch(revision, @"^v?\d")) warnings.Add("A branch name moves; a tag or a commit does not.");
        if (uri.Scheme == "http") warnings.Add("http is not encrypted.");
        return new Check("git", text, editable, "Install from git", details, warnings);
    }

    static Check Link(string text, bool editable)
    {
        if (!LinkAddress.IsMatch(text)) throw new ServiceException("a link must be an http(s) address of a .whl, .tar.gz or .zip file");
        if (editable) throw new ServiceException("a link cannot be installed in editable mode");
        var uri = new Uri(text);
        var warnings = new List<string>();
        if (uri.Scheme == "http") warnings.Add("http is not encrypted.");
        if (!text.Contains("#sha256=", StringComparison.OrdinalIgnoreCase)) warnings.Add("The address has no #sha256= hash: nothing checks that the file is the one you expect.");
        if (!uri.AbsolutePath.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)) warnings.Add("An archive of source is built on this computer: its build scripts run.");
        return new Check("link", text, false, "Install from a link", new[] { $"File: {Path.GetFileName(uri.AbsolutePath)}", $"From: {uri.Host}" }, warnings);
    }

    static Check LocalPath(string root, string text, bool editable)
    {
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, text)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { throw new ServiceException("that is not a path"); }
        if (Directory.Exists(full))
        {
            string[] markers = { "pyproject.toml", "setup.py", "setup.cfg" };
            var found = markers.Where(m => File.Exists(Path.Combine(full, m))).ToList();
            if (found.Count == 0) throw new ServiceException("that folder has no pyproject.toml, setup.py or setup.cfg, so pip cannot install it");
            var details = new List<string> { "Folder: " + full, "Describes itself in: " + string.Join(", ", found) };
            if (found.Contains("pyproject.toml"))
            {
                string toml = SafeRead(Path.Combine(full, "pyproject.toml"));
                if (Regex.Match(toml, @"(?m)^\s*name\s*=\s*[""']([^""']+)[""']") is { Success: true } n) details.Add("Name: " + n.Groups[1].Value);
                if (Regex.Match(toml, @"(?m)^\s*build-backend\s*=\s*[""']([^""']+)[""']") is { Success: true } b) details.Add("Build backend: " + b.Groups[1].Value);
            }
            details.Add(editable ? "Editable: the folder itself is used, later changes show without installing again." : "A copy is installed.");
            var warnings = new List<string> { "pip builds the project: its build backend and setup script run on this computer." };
            return new Check("path", full, editable, "Install from a folder", details, warnings);
        }
        if (File.Exists(full))
        {
            string lower = full.ToLowerInvariant();
            if (!(lower.EndsWith(".whl") || lower.EndsWith(".tar.gz") || lower.EndsWith(".zip") || lower.EndsWith(".tgz")))
                throw new ServiceException("only a folder, a .whl, a .tar.gz or a .zip can be installed");
            if (editable) throw new ServiceException("only a folder can be installed in editable mode");
            var warnings = new List<string>();
            if (!lower.EndsWith(".whl")) warnings.Add("An archive of source is built on this computer: its build scripts run.");
            else warnings.Add("A wheel is installed as it is; it can still contain code that runs when it is imported.");
            return new Check("path", full, false, "Install from a file", new[] { "File: " + full }, warnings);
        }
        throw new ServiceException("there is no file or folder " + full);
    }

    static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); } catch (IOException) { return ""; }
    }

    // ---- the confirmation: a token for the source that was shown, good once and for a few minutes

    static readonly ConcurrentDictionary<string, (Check Check, DateTime Until)> Issued = new();

    public static string Issue(Check check)
    {
        foreach (var (k, v) in Issued) if (v.Until < DateTime.UtcNow) Issued.TryRemove(k, out _);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Issued[token] = (check, DateTime.UtcNow.AddMinutes(5));
        return token;
    }

    /// <summary>The checked source for a token, once; null when the token is unknown, used, or too old.</summary>
    public static Check? Redeem(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Issued.TryRemove(token, out var entry)) return null;
        return entry.Until >= DateTime.UtcNow ? entry.Check : null;
    }
}

/// <summary>The releases of a package, newest first, from the package index's JSON or from `pip index versions`.</summary>
public static class PackageVersions
{
    public sealed record Release(string Version, bool Pre, bool Yanked);

    public static List<Release> FromPyPi(string json)
    {
        var list = new List<Release>();
        var releases = JsonNode.Parse(json)?["releases"] as JsonObject;
        if (releases == null) return list;
        foreach (var (version, files) in releases)
        {
            var arr = files as JsonArray;
            if (arr == null || arr.Count == 0) continue;   // a version with no files cannot be installed
            bool yanked = arr.All(f => f?["yanked"]?.GetValue<bool>() == true);
            list.Add(new Release(version, IsPre(version), yanked));
        }
        list.Sort((a, b) => Compare(b.Version, a.Version));
        return list;
    }

    /// <summary>The output of `pip index versions name`: a line "Available versions: 2.0, 1.1, 1.0".</summary>
    public static List<Release> FromPipIndex(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("Available versions:", StringComparison.OrdinalIgnoreCase)) continue;
            return line["Available versions:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(v => new Release(v, IsPre(v), false)).OrderBy(r => r, Comparer<Release>.Create((a, b) => Compare(b.Version, a.Version))).ToList();
        }
        return new List<Release>();
    }

    static readonly Regex Pep440 = new(@"^v?(?:(?<epoch>\d+)!)?(?<rel>\d+(?:\.\d+)*)(?:[-_.]?(?<pl>a|b|c|rc|alpha|beta|pre|preview)[-_.]?(?<pn>\d*))?(?:(?:-(?<pon>\d+))|(?:[-_.]?(?:post|rev|r)[-_.]?(?<po>\d*)))?(?:[-_.]?dev[-_.]?(?<dev>\d*))?(?:\+[a-z0-9.\-_]+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPre(string version)
    {
        var m = Pep440.Match(version);
        return m.Success && (m.Groups["pl"].Success || m.Groups["dev"].Success);
    }

    /// <summary>PEP 440 order (epoch, release, pre-releases before the release, post-releases after it, dev before all); versions it cannot read go first as text.</summary>
    public static int Compare(string a, string b)
    {
        var ma = Pep440.Match(a); var mb = Pep440.Match(b);
        if (!ma.Success || !mb.Success)
            return ma.Success == mb.Success ? string.CompareOrdinal(a, b) : (ma.Success ? 1 : -1);
        var ka = Key(ma); var kb = Key(mb);
        int c = ka.Epoch.CompareTo(kb.Epoch);
        if (c != 0) return c;
        int n = Math.Max(ka.Release.Count, kb.Release.Count);
        for (int i = 0; i < n; i++)
        {
            long x = i < ka.Release.Count ? ka.Release[i] : 0, y = i < kb.Release.Count ? kb.Release[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        c = ka.Pre.CompareTo(kb.Pre); if (c != 0) return c;
        c = ka.Post.CompareTo(kb.Post); if (c != 0) return c;
        return ka.Dev.CompareTo(kb.Dev);
    }

    static (long Epoch, List<long> Release, (int, long) Pre, long Post, long Dev) Key(Match m)
    {
        long Num(string s) => s.Length == 0 ? 0 : (long.TryParse(s, out var v) ? v : long.MaxValue);
        var release = m.Groups["rel"].Value.Split('.').Select(Num).ToList();
        bool hasPre = m.Groups["pl"].Success;
        bool hasPost = m.Groups["pon"].Success || m.Groups["po"].Success;
        bool hasDev = m.Groups["dev"].Success;
        (int, long) pre;
        if (hasPre)
        {
            string l = m.Groups["pl"].Value.ToLowerInvariant();
            int rank = l is "a" or "alpha" ? 0 : l is "b" or "beta" ? 1 : 2;
            pre = (rank, Num(m.Groups["pn"].Value));
        }
        else if (!hasPost && hasDev) pre = (-1, 0);          // 1.0.dev1 comes before 1.0a1
        else pre = (3, 0);                                    // no pre-release: after the pre-releases
        long post = hasPost ? Num(m.Groups["pon"].Success ? m.Groups["pon"].Value : m.Groups["po"].Value) : -1;
        long dev = hasDev ? Num(m.Groups["dev"].Value) : long.MaxValue;
        return (m.Groups["epoch"].Success ? Num(m.Groups["epoch"].Value) : 0, release, pre, post, dev);
    }
}

/// <summary>What the installed packages require of each other, from the interpreter's own metadata (importlib.metadata): nothing is downloaded.</summary>
public static class DependencyTree
{
    public sealed class Node
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Specifier { get; init; } = "";
        public bool Missing { get; init; }
        public bool Cycle { get; init; }
        public List<Node> Children { get; init; } = new();
    }

    public sealed record Dist(string Name, string Version, IReadOnlyList<(string Name, string Specifier)> Requires);

    public const string Script = """
import json, re
from importlib import metadata
def norm(n):
    return re.sub(r"[-_.]+", "-", n).lower()
try:
    from pip._vendor.packaging.requirements import Requirement
except Exception:
    try:
        from packaging.requirements import Requirement
    except Exception:
        Requirement = None
out = {}
for d in metadata.distributions():
    name = d.metadata["Name"]
    if not name or norm(name) in out:
        continue
    reqs = []
    for r in d.requires or []:
        if Requirement is not None:
            try:
                q = Requirement(r)
                if q.marker is not None and not q.marker.evaluate({"extra": ""}):
                    continue
                reqs.append([norm(q.name), str(q.specifier)])
            except Exception:
                continue
        else:
            if "extra ==" in r or "extra==" in r:
                continue
            m = re.match(r"[A-Za-z0-9][A-Za-z0-9._-]*", r)
            if m:
                reqs.append([norm(m.group(0)), ""])
    out[norm(name)] = {"name": name, "version": d.version, "requires": reqs}
print(json.dumps(out))
""";

    public static Dictionary<string, Dist> Parse(string json)
    {
        var result = new Dictionary<string, Dist>();
        if (JsonNode.Parse(json) is not JsonObject all) return result;
        foreach (var (key, v) in all)
        {
            var reqs = (v!["requires"] as JsonArray)?.Select(r => (r![0]!.GetValue<string>(), r[1]?.GetValue<string>() ?? "")).ToList() ?? new();
            result[key] = new Dist(v["name"]!.GetValue<string>(), v["version"]!.GetValue<string>(), reqs);
        }
        return result;
    }

    public const int MaxNodes = 4000;

    /// <summary>The packages nobody requires, each with what it needs below it (or, with <paramref name="name"/>, that one package; <paramref name="reverse"/> turns it around to who needs it).</summary>
    public static List<Node> Build(IReadOnlyDictionary<string, Dist> dists, string? name = null, bool reverse = false, int maxDepth = 12)
    {
        int count = 0;
        Dictionary<string, List<(string Name, string Spec)>> requiredBy = new();
        foreach (var (key, d) in dists)
            foreach (var (n, spec) in d.Requires)
                (requiredBy.TryGetValue(n, out var l) ? l : requiredBy[n] = new()).Add((key, spec));

        Node Make(string key, string spec, HashSet<string> path, int depth)
        {
            count++;
            bool known = dists.TryGetValue(key, out var d);
            if (!known) return new Node { Name = key, Specifier = spec, Missing = true };
            if (path.Contains(key)) return new Node { Name = d!.Name, Version = d.Version, Specifier = spec, Cycle = true };
            var node = new Node { Name = d!.Name, Version = d.Version, Specifier = spec };
            if (depth >= maxDepth || count >= MaxNodes) return node;
            path.Add(key);
            IEnumerable<(string, string)> next = reverse
                ? (requiredBy.TryGetValue(key, out var up) ? up.OrderBy(u => u.Name, StringComparer.Ordinal) : Enumerable.Empty<(string, string)>())
                : d.Requires.OrderBy(r => r.Name, StringComparer.Ordinal);
            foreach (var (childKey, childSpec) in next)
            {
                if (count >= MaxNodes) break;
                node.Children.Add(Make(childKey, childSpec, path, depth + 1));
            }
            path.Remove(key);
            return node;
        }

        if (name != null)
        {
            string key = PackageManager.Normalize(name);
            return dists.ContainsKey(key) ? new List<Node> { Make(key, "", new(), 0) } : new List<Node>();
        }
        var roots = dists.Keys.Where(k => !requiredBy.ContainsKey(k) || requiredBy[k].All(r => r.Name == k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (roots.Count == 0) roots = dists.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var list = new List<Node>();
        foreach (var r in roots)
        {
            if (count >= MaxNodes) break;
            list.Add(Make(r, "", new(), 0));
        }
        return list;
    }

    public static Dictionary<string, Dist> Read(string python, ExecutionTarget? target, int timeoutMs = 60000)
    {
        var (code, output, error) = PackageManager.Exec(python, new[] { "-c", Script }, timeoutMs, target);
        if (code != 0) throw new ServiceException(error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "the interpreter did not answer");
        try { return Parse(output); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException) { throw new ServiceException("unexpected answer from the interpreter"); }
    }
}

/// <summary>Lock files of the project and what keeps an environment equal to them: uv, poetry, pip-tools. The tools are only run when they are already there.</summary>
public static class LockFiles
{
    /// <param name="Stale">Whether the lock file no longer matches what it was made from (null: it could not be told).</param>
    /// <param name="Compile">The command that makes the lock file again from its source (requirements.in), when there is one and a tool for it.</param>
    public sealed record Found(string File, string Kind, string Tool, bool Available, string Command, string? Note,
        bool? Stale = null, string? StaleNote = null, string? Compile = null);

    /// <summary>Runs a program in a folder: (exit code, output, error output).</summary>
    public delegate (int Code, string Output, string Error) Runner(string program, IReadOnlyList<string> args, string folder);

    /// <summary>What to run: the program, its arguments and the environment variables that point it at the project's virtual environment.</summary>
    public sealed record Plan(string Program, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

    /// <param name="which">Finds a program on the PATH (null when it is not there).</param>
    /// <param name="hasPipTools">Whether the selected interpreter has pip-tools installed.</param>
    /// <param name="inInterpreter">The tools are looked for in an interpreter of another system (<c>python -m uv</c>), not on this computer's PATH.</param>
    public static List<Found> Detect(string root, Func<string, string?> which, bool hasPipTools, bool inInterpreter = false)
    {
        string where = inInterpreter ? " in that interpreter" : "";
        var list = new List<Found>();
        bool pyproject = File.Exists(Path.Combine(root, "pyproject.toml"));
        if (File.Exists(Path.Combine(root, "uv.lock")))
        {
            bool ok = which("uv") != null && pyproject;
            list.Add(new Found("uv.lock", "uv", "uv", ok, (inInterpreter ? "python -m " : "") + "uv sync --frozen", ok ? null : !pyproject ? "there is no pyproject.toml next to it" : "uv is not installed" + where));
        }
        if (File.Exists(Path.Combine(root, "poetry.lock")))
        {
            bool ok = which("poetry") != null && pyproject;
            list.Add(new Found("poetry.lock", "poetry", "poetry", ok, (inInterpreter ? "python -m " : "") + "poetry install --sync --no-interaction", ok ? null : !pyproject ? "there is no pyproject.toml next to it" : "poetry is not installed" + where));
        }
        string req = Path.Combine(root, "requirements.txt");
        bool compiled = File.Exists(req) && (File.ReadLines(req).Take(8).Any(l => l.Contains("pip-compile", StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(root, "requirements.in")));
        if (compiled)
        {
            string? compile = File.Exists(Path.Combine(root, "requirements.in")) ? CompilerOf(root, which, hasPipTools) switch
            {
                "uv" => "uv pip compile requirements.in -o requirements.txt",
                "pip-tools" => "python -m piptools compile requirements.in -o requirements.txt",
                _ => null,
            } : null;
            list.Add(new Found("requirements.txt", "pip-tools", "pip-tools", hasPipTools, "python -m piptools sync requirements.txt", hasPipTools ? null : "pip-tools is not installed in this interpreter", Compile: compile));
        }
        if (File.Exists(Path.Combine(root, "Pipfile.lock")))
            list.Add(new Found("Pipfile.lock", "pipenv", "pipenv", false, "pipenv sync", "pipenv is not supported here"));
        return list;
    }

    /// <summary>
    /// Whether each lock file still matches what it was made from. uv and poetry are asked themselves (<c>uv lock --check</c>, offline and
    /// without downloading a Python; <c>poetry check --lock</c>), only when they are installed. A requirements.txt compiled from requirements.in is
    /// compared with it here: a requirement missing, one whose pinned version no longer fits, or one taken out of the .in file.
    /// </summary>
    public static List<Found> CheckStale(string root, IReadOnlyList<Found> found, Func<string, string?> which, Runner run)
    {
        var list = new List<Found>();
        foreach (var f in found)
        {
            (bool? stale, string? note) = (null, null);
            try
            {
                switch (f.Kind)
                {
                    case "uv" when f.Available && which("uv") is { } uv:
                    {
                        var (code, output, error) = run(uv, new[] { "lock", "--check", "--offline", "--no-python-downloads" }, root);
                        string text = output + "\n" + error;
                        if (code == 0) stale = false;
                        else if (text.Contains("needs to be updated", StringComparison.OrdinalIgnoreCase)) (stale, note) = (true, "pyproject.toml changed since uv.lock was made: run uv lock");
                        else note = "uv could not tell: " + PackageSettings.MaskText(LastLine(text));
                        break;
                    }
                    case "poetry" when f.Available && which("poetry") is { } poetry:
                    {
                        var (code, output, error) = run(poetry, new[] { "check", "--lock", "--no-interaction" }, root);
                        string text = output + "\n" + error;
                        if (code == 0) stale = false;
                        else if (Regex.IsMatch(text, @"poetry\.lock|lock file", RegexOptions.IgnoreCase)) (stale, note) = (true, "pyproject.toml changed since poetry.lock was made: run poetry lock");
                        else note = "poetry could not tell: " + PackageSettings.MaskText(LastLine(text));
                        break;
                    }
                    case "pip-tools" when File.Exists(Path.Combine(root, "requirements.in")):
                        note = CompiledDifference(File.ReadAllText(Path.Combine(root, "requirements.in")), File.ReadAllText(Path.Combine(root, f.File)));
                        stale = note != null;
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { note = "could not be checked: " + e.Message; }
            list.Add(f with { Stale = stale, StaleNote = note });
        }
        return list;
    }

    static string LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)) ?? "no answer";

    static readonly Regex Requirement = new(@"^([A-Za-z0-9][A-Za-z0-9._\-]*)\s*(\[[^\]]*\])?\s*(.*)$");

    /// <summary>
    /// What makes a compiled requirements.txt out of date with its requirements.in (null when nothing does): a requirement of the .in that the .txt
    /// does not have, a pinned version that does not fit the .in's specifier, or a package that was there only for the .in (<c># via -r requirements.in</c>) and is no longer in it.
    /// Lines the check cannot read (options, includes, links) are passed over.
    /// </summary>
    public static string? CompiledDifference(string inText, string txtText)
    {
        var wanted = new List<(string Name, string Spec)>();
        foreach (var raw in inText.Split('\n'))
        {
            string line = Regex.Replace(raw, @"(^|\s)#.*$", "").Trim();
            if (line.Length == 0 || line.StartsWith('-') || line.Contains("://")) continue;
            var m = Requirement.Match(line.Split(';')[0].Trim());
            if (!m.Success) continue;
            string rest = m.Groups[3].Value.Trim();
            if (rest.StartsWith('@')) rest = "";
            wanted.Add((m.Groups[1].Value, rest));
        }
        // the pinned packages of the .txt, and what each is there for (its "# via" lines)
        var pins = new Dictionary<string, string?>();
        var via = new Dictionary<string, List<string>>();
        string? current = null;
        foreach (var raw in txtText.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#') && !line.StartsWith('-'))
            {
                var m = Regex.Match(line, @"^([A-Za-z0-9][A-Za-z0-9._\-]*)(\[[^\]]*\])?\s*(==\s*([^\s;\\]+)|@)?");
                if (!m.Success) { current = null; continue; }
                current = PackageManager.Normalize(m.Groups[1].Value);
                pins[current] = m.Groups[4].Success ? m.Groups[4].Value : null;
                via[current] = new List<string>();
                continue;
            }
            if (line.StartsWith('-') && !line.StartsWith("-e") && !line.StartsWith("--editable")) { current = null; continue; }
            var c = Regex.Match(line, @"^\s+#\s*(via\b)?\s*(.*)$");
            if (current != null && c.Success && c.Groups[2].Value.Length > 0) via[current].Add(c.Groups[2].Value.Trim());
            else if (!c.Success && line.Length > 0 && !char.IsWhiteSpace(line[0])) current = null;
        }
        foreach (var (name, spec) in wanted)
        {
            string key = PackageManager.Normalize(name);
            if (!pins.TryGetValue(key, out var version)) return $"{name} is in requirements.in but not in requirements.txt";
            if (version != null && spec.Length > 0 && !Satisfies(version, spec)) return $"requirements.in asks for {name}{spec}, requirements.txt has {version}";
        }
        var asked = wanted.Select(w => PackageManager.Normalize(w.Name)).ToHashSet();
        foreach (var (name, reasons) in via)
            if (!asked.Contains(name) && reasons.Count > 0 && reasons.All(r => Regex.IsMatch(r, @"^-r\s+\S*requirements\.in$")))
                return $"{name} was taken out of requirements.in but is still in requirements.txt";
        return null;
    }

    /// <summary>Whether a version fits a specifier such as <c>&gt;=2,&lt;3</c>, <c>~=1.4</c> or <c>==1.*</c> (a part it cannot read counts as fitting).</summary>
    public static bool Satisfies(string version, string spec)
    {
        foreach (var part in spec.Split(','))
        {
            var m = Regex.Match(part.Trim(), @"^(===|==|!=|~=|>=|<=|>|<)\s*(\S+)$");
            if (!m.Success) continue;
            string op = m.Groups[1].Value, want = m.Groups[2].Value;
            if (want.EndsWith(".*"))
            {
                string prefix = want[..^2];
                bool match = version == prefix || version.StartsWith(prefix + ".");
                if (op == "==" ? !match : op == "!=" && match) return false;
                continue;
            }
            int cmp = PackageVersions.Compare(version, want);
            bool ok = op switch
            {
                "===" => version == want,
                "==" => cmp == 0,
                "!=" => cmp != 0,
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                "<" => cmp < 0,
                // ~=1.4.2 means >=1.4.2 and ==1.4.*
                _ => cmp >= 0 && want.Split('.') is { Length: > 1 } r && (version + ".").StartsWith(string.Join(".", r[..^1]) + "."),
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Which tool compiles requirements.in: the one whose name is in the header of requirements.txt, else pip-tools when the interpreter has it, else uv (null: neither).</summary>
    static string? CompilerOf(string root, Func<string, string?> which, bool hasPipTools)
    {
        string head = "";
        try { head = string.Join("\n", File.ReadLines(Path.Combine(root, "requirements.txt")).Take(8)); } catch (IOException) { }
        bool uv = which("uv") != null;
        if (head.Contains("uv pip compile") && uv) return "uv";
        if (hasPipTools) return "pip-tools";
        return uv ? "uv" : null;
    }

    /// <summary>
    /// The command that makes requirements.txt again from requirements.in, with the project's index settings. They are given as environment
    /// variables, never as options, so neither the command written at the top of the file nor its <c>--index-url</c> lines carry a password.
    /// </summary>
    public static Plan CompilePlan(string root, string python, bool isLocal, Func<string, string?> which, bool hasPipTools, PackageSettings settings)
    {
        if (!isLocal) throw new ServiceException("requirements.in is compiled on this computer only");
        if (!File.Exists(Path.Combine(root, "requirements.in"))) throw new ServiceException("this project has no requirements.in");
        var env = new Dictionary<string, string>();
        switch (CompilerOf(root, which, hasPipTools))
        {
            case "uv":
                env["UV_PYTHON"] = python;
                if (settings.IndexUrl.Length > 0) env["UV_INDEX_URL"] = settings.IndexUrl;
                if (settings.ExtraIndexUrls.Count > 0) env["UV_EXTRA_INDEX_URL"] = string.Join(" ", settings.ExtraIndexUrls);
                if (settings.TrustedHosts.Count > 0) env["UV_INSECURE_HOST"] = string.Join(" ", settings.TrustedHosts);
                if (settings.Proxy.Length > 0) { env["HTTPS_PROXY"] = settings.Proxy; env["HTTP_PROXY"] = settings.Proxy; }
                return new Plan(which("uv")!, new[] { "pip", "compile", "requirements.in", "-o", "requirements.txt", "--no-progress" }, env);
            case "pip-tools":
                if (settings.IndexUrl.Length > 0) env["PIP_INDEX_URL"] = settings.IndexUrl;
                if (settings.ExtraIndexUrls.Count > 0) env["PIP_EXTRA_INDEX_URL"] = string.Join(" ", settings.ExtraIndexUrls);
                if (settings.TrustedHosts.Count > 0) env["PIP_TRUSTED_HOST"] = string.Join(" ", settings.TrustedHosts);
                if (settings.Proxy.Length > 0) env["PIP_PROXY"] = settings.Proxy;
                return new Plan(python, new[] { "-m", "piptools", "compile", "requirements.in", "-o", "requirements.txt", "--no-emit-index-url", "--no-emit-trusted-host" }, env);
            default:
                throw new ServiceException("neither pip-tools (in this interpreter) nor uv is installed to compile requirements.in");
        }
    }

    /// <summary>Runs a tool in the project and waits for it (a check that only reads, such as <c>uv lock --check</c>).</summary>
    public static (int Code, string Output, string Error) RunTool(string program, IReadOnlyList<string> args, string folder)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(program) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, "", "it did not answer in 30 seconds"); }
        return (p.ExitCode, output.Result, error.Result);
    }

    /// <summary>The command that syncs the interpreter's virtual environment to a detected lock file; only for a virtual environment of this computer.</summary>
    public static Plan MakePlan(string root, string file, string python, string venvPrefix, bool isVenv, bool isLocal, Func<string, string?> which, bool hasPipTools)
    {
        if (!isLocal) throw new ServiceException("lock files are synced on this computer only");
        if (!isVenv) throw new ServiceException("select the project's virtual environment first: a sync removes packages the lock file does not list");
        var found = Detect(root, which, hasPipTools).FirstOrDefault(f => f.File == file) ?? throw new ServiceException("that is not a lock file of this project");
        if (!found.Available) throw new ServiceException($"{found.Tool} cannot be used: {found.Note}");
        switch (found.Kind)
        {
            case "uv":
                return new Plan(which("uv")!, new[] { "sync", "--frozen" }, new Dictionary<string, string> { ["UV_PROJECT_ENVIRONMENT"] = venvPrefix });
            case "poetry":
                return new Plan(which("poetry")!, new[] { "install", "--sync", "--no-interaction" },
                    new Dictionary<string, string> { ["VIRTUAL_ENV"] = venvPrefix, ["POETRY_VIRTUALENVS_CREATE"] = "false" });
            case "pip-tools":
                return new Plan(python, new[] { "-m", "piptools", "sync", Path.Combine(root, "requirements.txt") }, new Dictionary<string, string>());
            default:
                throw new ServiceException("that kind of lock file is not supported");
        }
    }

    /// <summary>A sync in an interpreter of another system: a module of that interpreter run in the project folder as that system sees it.</summary>
    public sealed record RemotePlan(string Module, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

    /// <summary>The modules an interpreter has to sync with: uv and poetry are Python packages too (<c>python -m uv</c>), pip-tools is <c>piptools</c>. Asked without pip.</summary>
    public static HashSet<string> SyncModules(string python, ExecutionTarget target)
    {
        var (code, output, _) = PackageManager.Exec(python, new[] { "-c", "import importlib.util as u; print(' '.join(m for m in ('uv', 'poetry', 'piptools') if u.find_spec(m)))" }, 60000, target);
        return code == 0 ? output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet() : new HashSet<string>();
    }

    /// <summary>The lock files as an interpreter of another system can sync them (its own modules; see <see cref="SyncModules"/>).</summary>
    public static List<Found> DetectIn(string root, IReadOnlySet<string> modules) =>
        Detect(root, m => modules.Contains(m) ? m : null, modules.Contains("piptools"), inInterpreter: true)
            .Select(f => f with { Compile = null }).ToList();   // compiling requirements.in is for this computer only

    /// <summary>
    /// The sync of a lock file in an interpreter of WSL, SSH, a running container or a Compose service run with exec, into that system's
    /// virtual environment. Not for a Docker image (or a Compose service started anew each time): every run is a new container, and what
    /// a sync put in it would be gone.
    /// </summary>
    public static RemotePlan MakeRemotePlan(string root, string file, ExecutionTarget target, string prefix, bool isVenv, IReadOnlySet<string> modules)
    {
        if (target.IsLocal) throw new ServiceException("that interpreter is of this computer");
        if (target.Kind == "docker" || target is ComposeTarget { Exec: false })
            throw new ServiceException("every run of this interpreter starts a new container, so what a sync installs would be gone: use a container that is running, or a Compose service with exec");
        if (!isVenv) throw new ServiceException("select a virtual environment of that system first: a sync removes packages the lock file does not list");
        var found = DetectIn(root, modules).FirstOrDefault(f => f.File == file) ?? throw new ServiceException("that is not a lock file of this project");
        if (!found.Available) throw new ServiceException($"{found.Tool} cannot be used: {found.Note}");
        return found.Kind switch
        {
            "uv" => new RemotePlan("uv", new[] { "sync", "--frozen" }, new Dictionary<string, string> { ["UV_PROJECT_ENVIRONMENT"] = prefix }),
            "poetry" => new RemotePlan("poetry", new[] { "install", "--sync", "--no-interaction" }, new Dictionary<string, string> { ["VIRTUAL_ENV"] = prefix, ["POETRY_VIRTUALENVS_CREATE"] = "false" }),
            "pip-tools" => new RemotePlan("piptools", new[] { "sync", "requirements.txt" }, new Dictionary<string, string>()),
            _ => throw new ServiceException("that kind of lock file is not supported"),
        };
    }

    /// <summary>A program on the PATH, with the Windows extensions (null when there is none).</summary>
    public static string? Find(string name)
    {
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in extensions)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        return null;
    }
}

/// <summary>
/// A virtual environment made by uv (its pyvenv.cfg has a <c>uv = </c> line) has no pip of its own: its packages are listed, installed and
/// removed with <c>uv pip ... --python</c> that environment's interpreter, the way uv's own commands do. The pip commands PyMagic builds are
/// turned into uv's (<see cref="Translate"/>); one uv has no counterpart for (<c>pip index versions</c>, <c>--user</c>) stays pip's.
/// Only for an environment on this computer, and only when uv is installed.
/// </summary>
public static class UvPip
{
    public sealed record Command(string Program, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

    static readonly HashSet<string> Supported = new() { "install", "uninstall", "list", "show", "freeze", "check" };

    /// <summary>Whether the environment in this folder was made by uv.</summary>
    public static bool IsUvVenv(string prefix)
    {
        try { return File.ReadLines(Path.Combine(prefix, "pyvenv.cfg")).Any(l => Regex.IsMatch(l, @"^\s*uv\s*=")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The folder of the virtual environment an interpreter belongs to (Scripts\python.exe or bin/python inside it), or null.</summary>
    public static string? PrefixOf(string python)
    {
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)));
            return folder != null && File.Exists(Path.Combine(folder, "pyvenv.cfg")) ? folder : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>uv, when this interpreter is that of an environment uv made and uv is installed (null otherwise: pip is used).</summary>
    public static string? For(string python, Func<string, string?>? which = null) =>
        PrefixOf(python) is { } prefix && IsUvVenv(prefix) ? (which ?? LockFiles.Find)("uv") : null;

    /// <summary>The uv command for the arguments of a pip command (what follows <c>-m pip</c>), or null when uv has no counterpart.</summary>
    public static Command? Translate(string uv, string python, IEnumerable<string> pipArgs)
    {
        var a = pipArgs.ToList();
        if (a.Count == 0 || !Supported.Contains(a[0])) return null;
        var args = new List<string> { "pip", a[0], "--python", python, "--no-progress" };
        var env = new Dictionary<string, string> { ["NO_COLOR"] = "1" };
        for (int i = 1; i < a.Count; i++)
        {
            string x = a[i];
            switch (x)
            {
                case "--disable-pip-version-check" or "--no-input" or "-y" or "--yes": break;   // uv neither checks its version nor asks
                case "--progress-bar": i++; break;
                case "--trusted-host" when i + 1 < a.Count: args.Add("--allow-insecure-host"); args.Add(a[++i]); break;
                case "--proxy" when i + 1 < a.Count: env["HTTPS_PROXY"] = env["HTTP_PROXY"] = a[++i]; break;
                case "--pre": args.Add("--prerelease"); args.Add("allow"); break;
                case "--user": return null;   // uv installs into an environment, never for the user
                default:
                    if (!x.StartsWith("--progress-bar=")) args.Add(x);
                    break;
            }
        }
        return new Command(uv, args, env);
    }
}
