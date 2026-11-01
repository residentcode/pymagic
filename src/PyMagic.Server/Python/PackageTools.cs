using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// The package index settings of a project (<c>.pymagic/packages.json</c>): another index, extra indexes, hosts to trust without
/// https, and a proxy. They become options of every pip call that reaches an index (install, outdated, versions).
/// </summary>
public sealed class PackageSettings
{
    public string IndexUrl { get; set; } = "";
    public List<string> ExtraIndexUrls { get; set; } = new();
    public List<string> TrustedHosts { get; set; } = new();
    public string Proxy { get; set; } = "";

    static string PathOf(string root) => Path.Combine(root, ".pymagic", "packages.json");

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public bool IsEmpty => IndexUrl.Length == 0 && ExtraIndexUrls.Count == 0 && TrustedHosts.Count == 0 && Proxy.Length == 0;

    public static PackageSettings Load(string root)
    {
        try
        {
            var s = JsonSerializer.Deserialize<PackageSettings>(File.ReadAllText(PathOf(root)), Json) ?? new PackageSettings();
            s.Clean();
            return Validate(s) == null ? s : new PackageSettings();   // a hand-edited file with a bad value is not used
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new PackageSettings(); }
    }

    public static void Save(string root, PackageSettings settings)
    {
        settings.Clean();
        if (Validate(settings) is { } problem) throw new ServiceException(problem);
        Directory.CreateDirectory(Path.Combine(root, ".pymagic"));
        if (settings.IsEmpty) { try { File.Delete(PathOf(root)); } catch (IOException) { } return; }
        File.WriteAllText(PathOf(root), JsonSerializer.Serialize(settings, Json));
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
    public sealed record Found(string File, string Kind, string Tool, bool Available, string Command, string? Note);

    /// <summary>What to run: the program, its arguments and the environment variables that point it at the project's virtual environment.</summary>
    public sealed record Plan(string Program, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

    /// <param name="which">Finds a program on the PATH (null when it is not there).</param>
    /// <param name="hasPipTools">Whether the selected interpreter has pip-tools installed.</param>
    public static List<Found> Detect(string root, Func<string, string?> which, bool hasPipTools)
    {
        var list = new List<Found>();
        bool pyproject = File.Exists(Path.Combine(root, "pyproject.toml"));
        if (File.Exists(Path.Combine(root, "uv.lock")))
        {
            bool ok = which("uv") != null && pyproject;
            list.Add(new Found("uv.lock", "uv", "uv", ok, "uv sync --frozen", ok ? null : !pyproject ? "there is no pyproject.toml next to it" : "uv is not installed"));
        }
        if (File.Exists(Path.Combine(root, "poetry.lock")))
        {
            bool ok = which("poetry") != null && pyproject;
            list.Add(new Found("poetry.lock", "poetry", "poetry", ok, "poetry install --sync --no-interaction", ok ? null : !pyproject ? "there is no pyproject.toml next to it" : "poetry is not installed"));
        }
        string req = Path.Combine(root, "requirements.txt");
        bool compiled = File.Exists(req) && (File.ReadLines(req).Take(8).Any(l => l.Contains("pip-compile", StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(root, "requirements.in")));
        if (compiled)
            list.Add(new Found("requirements.txt", "pip-tools", "pip-tools", hasPipTools, "python -m piptools sync requirements.txt", hasPipTools ? null : "pip-tools is not installed in this interpreter"));
        if (File.Exists(Path.Combine(root, "Pipfile.lock")))
            list.Add(new Found("Pipfile.lock", "pipenv", "pipenv", false, "pipenv sync", "pipenv is not supported here"));
        return list;
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
