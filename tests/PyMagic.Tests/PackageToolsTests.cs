using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Package index settings, version lists, installing from a path or git address (with a confirmation), dependency trees and lock files.</summary>
public class PackageToolsTests
{
    static readonly string? RealPython = FindPython();

    static string? FindPython()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("import sys; print(sys.executable)");
            using var p = Process.Start(psi);
            string path = p!.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(20000) && p.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    sealed class Temp : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_tools_" + Guid.NewGuid().ToString("N"));
        public Temp() { Directory.CreateDirectory(Root); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        /// <summary>A wheel that needs no build step; <paramref name="requires"/> are Requires-Dist lines.</summary>
        public string Wheel(string name, string version, params string[] requires)
        {
            string path = Path.Combine(Root, $"{name}-{version}-py3-none-any.whl");
            string info = $"{name}-{version}.dist-info";
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            void Add(string entry, string text)
            {
                var e = zip.CreateEntry(entry);
                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }
            Add($"{name}/__init__.py", "VALUE = 7\n");
            Add($"{info}/METADATA", $"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\nSummary: A package made for tests\n" + string.Concat(requires.Select(r => "Requires-Dist: " + r + "\n")));
            Add($"{info}/WHEEL", "Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
            Add($"{info}/RECORD", $"{name}/__init__.py,,\n{info}/METADATA,,\n{info}/WHEEL,,\n{info}/RECORD,,\n");
            return path;
        }
    }

    // ------------------------------------------------------------ index settings

    [Fact]
    public void SettingsBecomePipOptionsAndAreKept()
    {
        using var t = new Temp();
        var s = new PackageSettings { IndexUrl = "https://pkgs.example.org/simple", ExtraIndexUrls = { "https://extra.example.org/simple", "http://10.0.0.5:8080/simple" }, TrustedHosts = { "10.0.0.5:8080" }, Proxy = "http://proxy.example.org:3128" };
        Assert.Equal("--index-url https://pkgs.example.org/simple --extra-index-url https://extra.example.org/simple --extra-index-url http://10.0.0.5:8080/simple --trusted-host 10.0.0.5:8080 --proxy http://proxy.example.org:3128", string.Join(" ", s.PipArgs()));
        PackageSettings.Save(t.Root, s);
        var back = PackageSettings.Load(t.Root);
        Assert.Equal(s.IndexUrl, back.IndexUrl);
        Assert.Equal(string.Join(",", s.ExtraIndexUrls), string.Join(",", back.ExtraIndexUrls));
        Assert.Equal(string.Join(",", s.TrustedHosts), string.Join(",", back.TrustedHosts));
        Assert.Equal(s.Proxy, back.Proxy);
        PackageSettings.Save(t.Root, new PackageSettings());   // empty settings remove the file
        A.False(File.Exists(Path.Combine(t.Root, ".pymagic", "packages.json")));
        Assert.True(PackageSettings.Load(t.Root).IsEmpty);
    }

    [Fact]
    public void SettingsThatCouldBeOptionsOrNonsenseAreRefused()
    {
        using var t = new Temp();
        foreach (var bad in new PackageSettings[]
        {
            new() { IndexUrl = "--find-links" },
            new() { IndexUrl = "file:///etc/passwd" },
            new() { ExtraIndexUrls = { "https://a b/simple" } },
            new() { TrustedHosts = { "-x" } },
            new() { TrustedHosts = { "host name" } },
            new() { Proxy = "--proxy" },
            new() { Proxy = "ftp://p" },
        })
            Assert.Throws<ServiceException>(() => PackageSettings.Save(t.Root, bad));
        Assert.True(PackageSettings.IsAddress("socks5://127.0.0.1:1080", proxy: true));
        A.False(PackageSettings.IsAddress("socks5://127.0.0.1:1080"));
        // a hand-edited file with a bad value is not used
        Directory.CreateDirectory(Path.Combine(t.Root, ".pymagic"));
        File.WriteAllText(Path.Combine(t.Root, ".pymagic", "packages.json"), "{\"indexUrl\": \"--evil\"}");
        Assert.True(PackageSettings.Load(t.Root).IsEmpty);
    }

    [Fact]
    public void InstallCommandsCarryTheSettingsBeforeThePackages()
    {
        var s = new PackageSettings { IndexUrl = "https://pkgs.example.org/simple", TrustedHosts = { "pkgs.example.org" } };
        var args = PipCommands.Install(new[] { "requests==2.31.0" }, upgrade: true, user: true, isVenv: false, s);
        Assert.Equal("install --disable-pip-version-check --no-input --progress-bar off --upgrade --user --index-url https://pkgs.example.org/simple --trusted-host pkgs.example.org requests==2.31.0", string.Join(" ", args));
        A.DoesNotContain("--user", PipCommands.Install(new[] { "x" }, false, true, isVenv: true, null));
        Assert.Equal("list --outdated --proxy http://p:1", string.Join(" ", PipCommands.WithIndex(new[] { "list", "--outdated" }, new PackageSettings { Proxy = "http://p:1" })));
    }

    // ------------------------------------------------------------ versions

    [Fact]
    public void VersionsAreSortedLikePep440()
    {
        string[] sorted = { "2.0.0", "2.0.0rc1", "2.0.0b2", "2.0.0a1", "2.0.0.dev3", "1.10.post1", "1.10", "1.9", "1.0", "0.9.1", "0.9" };
        var shuffled = sorted.OrderBy(v => v.GetHashCode()).ToList();
        shuffled.Sort((a, b) => PackageVersions.Compare(b, a));
        Assert.Equal(string.Join(",", sorted), string.Join(",", shuffled));
        Assert.Equal(0, PackageVersions.Compare("1.0", "1.0.0"));
        Assert.True(PackageVersions.Compare("1!0.1", "2.0") > 0);
        Assert.True(PackageVersions.IsPre("1.0rc1") && PackageVersions.IsPre("2.0.dev1") && !PackageVersions.IsPre("1.0.post1") && !PackageVersions.IsPre("1.0"));
    }

    [Fact]
    public void TheIndexAnswerIsReadIntoReleases()
    {
        var list = PackageVersions.FromPyPi("""
            {"info": {"name": "demo"}, "releases": {
              "1.0": [{"yanked": false}], "1.1": [{"yanked": true}, {"yanked": true}], "2.0rc1": [{"yanked": false}], "0.5": [], "2.0": [{"yanked": false}] }}
            """);
        Assert.Equal("2.0,2.0rc1,1.1,1.0", string.Join(",", list.Select(r => r.Version)));   // 0.5 has no files
        Assert.True(list[1].Pre && !list[0].Pre);
        Assert.True(list[2].Yanked && !list[3].Yanked);
        var fromPip = PackageVersions.FromPipIndex("demo (1.1)\nAvailable versions: 1.1, 1.0, 1.2b1\n  INSTALLED: 1.0\n");
        Assert.Equal("1.2b1,1.1,1.0", string.Join(",", fromPip.Select(r => r.Version)));
        A.Empty(PackageVersions.FromPipIndex("ERROR: no matching distribution"));
    }

    [Fact]
    public void ReadOnlyRequestToTheRealIndexGivesVersions()
    {
        string json;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            json = http.GetStringAsync("https://pypi.org/pypi/pip/json").GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return; }   // no network: nothing to check
        var list = PackageVersions.FromPyPi(json);
        Assert.True(list.Count > 50);
        A.Contains(list, r => r.Version == "24.0");
        for (int i = 1; i < list.Count; i++) Assert.True(PackageVersions.Compare(list[i - 1].Version, list[i].Version) >= 0, $"{list[i - 1].Version} before {list[i].Version}");
    }

    // ------------------------------------------------------------ installing from a path, a git address, a link

    [Fact]
    public void ASourceIsCheckedBeforeItIsInstalled()
    {
        using var t = new Temp();
        Directory.CreateDirectory(Path.Combine(t.Root, "proj"));
        File.WriteAllText(Path.Combine(t.Root, "proj", "pyproject.toml"), "[build-system]\nrequires = [\"setuptools\"]\nbuild-backend = \"setuptools.build_meta\"\n\n[project]\nname = \"proj-demo\"\nversion = \"0.1\"\n");
        Directory.CreateDirectory(Path.Combine(t.Root, "empty"));
        string wheel = t.Wheel("demopkg", "1.0");

        var folder = PackageSource.Inspect(t.Root, "proj", editable: true);
        Assert.Equal(("path", Path.Combine(t.Root, "proj"), true), (folder.Kind, folder.Source, folder.Editable));
        A.Contains("Name: proj-demo", folder.Details);
        A.Contains("Build backend: setuptools.build_meta", folder.Details);
        A.Contains(folder.Warnings, w => w.Contains("build backend"));

        var file = PackageSource.Inspect(t.Root, "\"" + wheel + "\"", editable: false);
        Assert.Equal(wheel, file.Source);

        var git = PackageSource.Inspect(t.Root, "git+https://github.com/example/proj.git@v1.2", false);
        Assert.Equal("git", git.Kind);
        A.Contains("Revision: v1.2", git.Details);
        A.Contains(PackageSource.Inspect(t.Root, "git+https://github.com/example/proj.git", false).Warnings, w => w.Contains("no tag or commit"));

        var link = PackageSource.Inspect(t.Root, "https://example.org/x/demo-1.0-py3-none-any.whl#sha256=abc123", false);
        Assert.Equal("link", link.Kind);
        A.Empty(link.Warnings);
        A.Contains(PackageSource.Inspect(t.Root, "http://example.org/demo.tar.gz", false).Warnings, w => w.Contains("not encrypted"));

        foreach (var bad in new[] { "", "-e .", "--index-url=x", "empty", "nowhere", "git+ftp://x/y.git", "git+https://x/y.git;rm -rf", "https://example.org/page.html", "file.txt" })
            Assert.Throws<ServiceException>(() => PackageSource.Inspect(t.Root, bad, false));
        File.WriteAllText(Path.Combine(t.Root, "file.txt"), "x");
        Assert.Throws<ServiceException>(() => PackageSource.Inspect(t.Root, "file.txt", false));
        Assert.Throws<ServiceException>(() => PackageSource.Inspect(t.Root, wheel, editable: true));
    }

    [Fact]
    public void AConfirmationCountsOnceAndOnlyForWhatWasShown()
    {
        using var t = new Temp();
        var check = PackageSource.Inspect(t.Root, "git+https://github.com/example/proj.git@v1", false);
        string token = PackageSource.Issue(check);
        A.Null(PackageSource.Redeem("0000"));
        A.Null(PackageSource.Redeem(null));
        Assert.Equal(check, PackageSource.Redeem(token));
        A.Null(PackageSource.Redeem(token));
        var args = PipCommands.InstallSource(check, user: false, isVenv: true, new PackageSettings { IndexUrl = "https://pkgs.example.org/simple" });
        Assert.Equal("install --disable-pip-version-check --no-input --progress-bar off --index-url https://pkgs.example.org/simple git+https://github.com/example/proj.git@v1", string.Join(" ", args));
    }

    // ------------------------------------------------------------ dependency trees

    const string FlatMetadata = """
        {"app": {"name": "App", "version": "1.0", "requires": [["lib-a", ">=1"], ["lib-b", ""]]},
         "lib-a": {"name": "lib_a", "version": "1.2", "requires": [["lib-c", ""], ["ghost", "<2"]]},
         "lib-b": {"name": "lib-b", "version": "2.0", "requires": [["lib-c", ">=3"]]},
         "lib-c": {"name": "lib-c", "version": "3.1", "requires": [["lib-a", ""]]},
         "alone": {"name": "alone", "version": "0.1", "requires": []}}
        """;

    [Fact]
    public void ARequirementTreeShowsMissingPackagesAndCycles()
    {
        var dists = DependencyTree.Parse(FlatMetadata);
        var roots = DependencyTree.Build(dists);
        Assert.Equal("alone,App", string.Join(",", roots.Select(r => r.Name)));   // "app" and "alone" are required by nobody
        var app = roots[1];
        Assert.Equal("lib_a,lib-b", string.Join(",", app.Children.Select(c => c.Name)));
        Assert.Equal(">=1", app.Children[0].Specifier);
        var libA = app.Children[0];
        Assert.Equal("ghost,lib-c", string.Join(",", libA.Children.Select(c => c.Name)));
        Assert.True(libA.Children[0].Missing);
        var libC = libA.Children[1];
        Assert.True(libC.Children[0].Cycle, "lib-c needs lib-a, which is already on the way down");

        var single = DependencyTree.Build(dists, "LIB_B");
        Assert.Equal("lib-b", single[0].Name);
        Assert.Equal("lib-c", single[0].Children[0].Name);
        var reverse = DependencyTree.Build(dists, "lib-c", reverse: true);
        Assert.Equal("lib-a,lib-b", string.Join(",", reverse[0].Children.Select(c => PackageManager.Normalize(c.Name))));
        A.Empty(DependencyTree.Build(dists, "nothing-here"));
    }

    [Fact]
    public void TheTreeIsReadFromTheInterpretersOwnMetadata()
    {
        if (RealPython == null) return;
        using var t = new Temp();
        string exe = new InterpreterManager(t.Root).CreateVenv(RealPython, ".venv", true);
        t.Wheel("treelib", "2.5");
        t.Wheel("treeapp", "1.0", "treelib>=2", "treeextra>=1; extra == 'more'", "never-installed; python_version < '2.7'");
        var (code, output, error) = PackageManager.Exec(exe, new[] { "-m", "pip", "install", "--no-index", "--find-links", t.Root, "--disable-pip-version-check", "treeapp" }, 180000);
        Assert.True(code == 0, output + error);
        var dists = DependencyTree.Read(exe, null);
        Assert.True(dists.ContainsKey("treeapp") && dists.ContainsKey("treelib"));
        Assert.Equal("treelib", string.Join(",", dists["treeapp"].Requires.Select(r => r.Name)));   // the extra and the marker that is false are left out
        Assert.Equal(">=2", dists["treeapp"].Requires[0].Specifier);
        var tree = DependencyTree.Build(dists, "treeapp");
        Assert.Equal("2.5", tree[0].Children[0].Version);
        var up = DependencyTree.Build(dists, "treelib", reverse: true);
        Assert.Equal("treeapp", up[0].Children[0].Name);
    }

    // ------------------------------------------------------------ a real index, a real install from a path

    sealed class TinyIndex : IDisposable
    {
        readonly HttpListener _listener = new();
        public readonly string Url;
        public readonly ConcurrentBag<string> Requests = new();

        public TinyIndex(string wheelPath)
        {
            int port;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{port}/simple";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            string file = Path.GetFileName(wheelPath);
            Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                    Requests.Add(c.Request.Url!.AbsolutePath);
                    string path = c.Request.Url.AbsolutePath;
                    byte[] body;
                    if (path.StartsWith("/simple/") && path.TrimEnd('/').EndsWith("/" + file.Split('-')[0]))
                    { body = System.Text.Encoding.UTF8.GetBytes($"<html><body><a href=\"/files/{file}\">{file}</a></body></html>"); c.Response.ContentType = "text/html"; }
                    else if (path == "/files/" + file) body = File.ReadAllBytes(wheelPath);
                    else { c.Response.StatusCode = 404; body = Array.Empty<byte>(); }
                    c.Response.OutputStream.Write(body);
                    c.Response.Close();
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { } }
    }

    [Fact]
    public void PipUsesTheProjectsIndexAndInstallsAFilePathAndTheVersionsAreListed()
    {
        if (RealPython == null) return;
        using var t = new Temp();
        string exe = new InterpreterManager(t.Root).CreateVenv(RealPython, ".venv", true);
        string wheel = t.Wheel("indexpkg", "1.3");
        using var index = new TinyIndex(wheel);
        var settings = new PackageSettings { IndexUrl = index.Url, TrustedHosts = { new Uri(index.Url).Authority } };

        // by name, from the index named in the settings (the real package index is not asked: it has no such package anyway)
        var install = PipCommands.Install(new[] { "indexpkg==1.3" }, false, false, true, settings);
        var (code, output, error) = PackageManager.Exec(exe, new[] { "-m", "pip" }.Concat(install), 180000);
        Assert.True(code == 0, output + error);
        A.Contains(index.Requests, r => r.StartsWith("/simple/indexpkg"));
        A.Contains(PackageManager.List(exe).Packages, p => p.Name == "indexpkg" && p.Version == "1.3");

        // the versions pip lists from that index
        var (vcode, vout, verr) = PackageManager.Exec(exe, PipCommands.WithIndex(new[] { "-m", "pip", "index", "versions", "--pre", "--disable-pip-version-check", "indexpkg" }, settings), 120000);
        if (vcode == 0) Assert.Equal("1.3", PackageVersions.FromPipIndex(vout).Single().Version);   // `pip index` needs pip 21.2; older ones are not asked
        else A.Contains("index", (verr + vout).ToLowerInvariant());
        PackageManager.Exec(exe, new[] { "-m", "pip", "uninstall", "-y", "indexpkg" }, 60000);

        // from a file path, once checked
        var check = PackageSource.Inspect(t.Root, wheel, false);
        var redeemed = PackageSource.Redeem(PackageSource.Issue(check))!;
        var fromPath = PipCommands.InstallSource(redeemed, false, true, null);
        (code, output, error) = PackageManager.Exec(exe, new[] { "-m", "pip" }.Concat(fromPath), 180000);
        Assert.True(code == 0, output + error);
        A.Contains(PackageManager.List(exe).Packages, p => p.Name == "indexpkg" && p.Version == "1.3");

        // a proxy that is not there stops an install that otherwise works
        PackageManager.Exec(exe, new[] { "-m", "pip", "uninstall", "-y", "indexpkg" }, 60000);
        settings.Proxy = "http://127.0.0.1:1";
        var viaProxy = PipCommands.Install(new[] { "indexpkg==1.3" }, false, false, true, settings);
        (code, _, _) = PackageManager.Exec(exe, new[] { "-m", "pip" }.Concat(viaProxy).Concat(new[] { "--retries", "0", "--timeout", "3" }), 120000);
        A.NotEqual(0, code);
    }

    // ------------------------------------------------------------ lock files

    [Fact]
    public void LockFilesAreFoundAndSyncOnlyWithTheirTool()
    {
        using var t = new Temp();
        File.WriteAllText(Path.Combine(t.Root, "pyproject.toml"), "[project]\nname = \"x\"\n");
        File.WriteAllText(Path.Combine(t.Root, "uv.lock"), "version = 1\n");
        File.WriteAllText(Path.Combine(t.Root, "poetry.lock"), "# poetry\n");
        File.WriteAllText(Path.Combine(t.Root, "requirements.txt"), "#\n# This file is autogenerated by pip-compile with Python 3.12\n#\nrequests==2.31.0\n");
        string? Which(string n) => n == "uv" ? "/bin/uv" : null;

        var found = LockFiles.Detect(t.Root, Which, hasPipTools: false);
        Assert.Equal("uv.lock,poetry.lock,requirements.txt", string.Join(",", found.Select(f => f.File)));
        Assert.True(found[0].Available);
        Assert.Equal("uv sync --frozen", found[0].Command);
        A.False(found[1].Available);
        Assert.Equal("poetry is not installed", found[1].Note);
        A.False(found[2].Available);
        Assert.True(LockFiles.Detect(t.Root, Which, hasPipTools: true)[2].Available);

        var plan = LockFiles.MakePlan(t.Root, "uv.lock", "/venv/bin/python", "/venv", isVenv: true, isLocal: true, Which, false);
        Assert.Equal(("/bin/uv", "sync --frozen"), (plan.Program, string.Join(" ", plan.Args)));
        Assert.Equal("/venv", plan.Env["UV_PROJECT_ENVIRONMENT"]);
        var tools = LockFiles.MakePlan(t.Root, "requirements.txt", "/venv/bin/python", "/venv", true, true, Which, hasPipTools: true);
        Assert.Equal("/venv/bin/python", tools.Program);
        Assert.Equal("-m piptools sync " + Path.Combine(t.Root, "requirements.txt"), string.Join(" ", tools.Args));

        // refused: a tool that is not there, a system interpreter, another machine, a file that is not a lock file
        Assert.Throws<ServiceException>(() => LockFiles.MakePlan(t.Root, "poetry.lock", "p", "/venv", true, true, Which, false));
        Assert.Throws<ServiceException>(() => LockFiles.MakePlan(t.Root, "uv.lock", "p", "/usr", isVenv: false, true, Which, false));
        Assert.Throws<ServiceException>(() => LockFiles.MakePlan(t.Root, "uv.lock", "p", "/venv", true, isLocal: false, Which, false));
        Assert.Throws<ServiceException>(() => LockFiles.MakePlan(t.Root, "pyproject.toml", "p", "/venv", true, true, Which, false));
    }

    [Fact]
    public void ARequirementsInFileMakesItALockFileToo()
    {
        using var t = new Temp();
        File.WriteAllText(Path.Combine(t.Root, "requirements.txt"), "requests==2.31.0\n");
        A.Empty(LockFiles.Detect(t.Root, _ => null, true));
        File.WriteAllText(Path.Combine(t.Root, "requirements.in"), "requests\n");
        Assert.Equal("requirements.txt", LockFiles.Detect(t.Root, _ => null, true).Single().File);
    }

    // ------------------------------------------------------------ passwords in index addresses

    [Fact]
    public void APasswordInAnIndexAddressIsKeptApartAndNeverShown()
    {
        using var t = new Temp();
        var s = new PackageSettings
        {
            IndexUrl = "https://me:s3cret@pkgs.example.org/simple",
            ExtraIndexUrls = { "https://TOKEN123@extra.example.org/simple", "https://open.example.org/simple" },
            Proxy = "http://proxyuser:pw@proxy.example.org:3128",
        };
        PackageSettings.Save(t.Root, s);

        string saved = File.ReadAllText(Path.Combine(t.Root, ".pymagic", "packages.json"));
        A.False(saved.Contains("s3cret") || saved.Contains("TOKEN123") || saved.Contains(":pw@"), saved);
        A.Contains("https://me:***@pkgs.example.org/simple", saved);
        A.Contains("https://***@extra.example.org/simple", saved);
        A.Contains("http://proxyuser:***@proxy.example.org:3128", saved);
        A.Contains("s3cret", File.ReadAllText(Path.Combine(t.Root, ".pymagic", "packages.private.json")));
        A.Contains("packages.private.json", File.ReadAllText(Path.Combine(t.Root, ".pymagic", ".gitignore")));

        // pip gets the whole address, the page the masked one
        var loaded = PackageSettings.Load(t.Root);
        Assert.Equal("https://me:s3cret@pkgs.example.org/simple", loaded.IndexUrl);
        A.Contains("https://TOKEN123@extra.example.org/simple", loaded.PipArgs());
        var shown = loaded.Masked();
        Assert.Equal("https://me:***@pkgs.example.org/simple", shown.IndexUrl);
        Assert.Equal("https://***@extra.example.org/simple,https://open.example.org/simple", string.Join(",", shown.ExtraIndexUrls));

        // the page sends the masked addresses back (another setting changed): the passwords stay
        shown.TrustedHosts.Add("open.example.org");
        PackageSettings.Save(t.Root, shown);
        Assert.Equal("https://me:s3cret@pkgs.example.org/simple", PackageSettings.Load(t.Root).IndexUrl);
        Assert.Equal("http://proxyuser:pw@proxy.example.org:3128", PackageSettings.Load(t.Root).Proxy);

        // a new password replaces the old one; no passwords left removes the private file
        shown.IndexUrl = "https://me:newer@pkgs.example.org/simple";
        PackageSettings.Save(t.Root, shown);
        Assert.Equal("https://me:newer@pkgs.example.org/simple", PackageSettings.Load(t.Root).IndexUrl);
        PackageSettings.Save(t.Root, new PackageSettings { IndexUrl = "https://open.example.org/simple" });
        A.False(File.Exists(Path.Combine(t.Root, ".pymagic", "packages.private.json")));
    }

    [Fact]
    public void APasswordWrittenIntoPackagesJsonByHandIsMovedOut()
    {
        using var t = new Temp();
        Directory.CreateDirectory(Path.Combine(t.Root, ".pymagic"));
        File.WriteAllText(Path.Combine(t.Root, ".pymagic", "packages.json"), "{ \"indexUrl\": \"https://u:handpw@idx.example.org/simple\" }");
        Assert.Equal("https://u:handpw@idx.example.org/simple", PackageSettings.Load(t.Root).IndexUrl);
        A.False(File.ReadAllText(Path.Combine(t.Root, ".pymagic", "packages.json")).Contains("handpw"));
        Assert.Equal("https://u:handpw@idx.example.org/simple", PackageSettings.Load(t.Root).IndexUrl);
    }

    [Fact]
    public void CredentialsAreMaskedInAddressesAndInText()
    {
        Assert.Equal(("https://a:***@h/x", (string?)"a:b"), PackageSettings.SplitCredentials("https://a:b@h/x"));
        Assert.Equal(("https://***@h", (string?)"tok"), PackageSettings.SplitCredentials("https://tok@h"));
        Assert.Equal(("https://h/x@y", (string?)null), PackageSettings.SplitCredentials("https://h/x@y"));
        Assert.Equal(("https://a:***@h", (string?)null), PackageSettings.SplitCredentials("https://a:***@h"));
        Assert.Equal("ERROR: 401 for https://me:***@idx.example.org/simple/x/ and http://***@p:1",
            PackageSettings.MaskText("ERROR: 401 for https://me:pw@idx.example.org/simple/x/ and http://tok@p:1"));
    }

    // ------------------------------------------------------------ is a lock file out of date

    [Fact]
    public void ACompiledRequirementsFileIsComparedWithItsInFile()
    {
        const string txt = "#\n# This file is autogenerated by pip-compile\n#\ncertifi==2024.2.2\n    # via requests\nrequests==2.31.0 \\\n    --hash=sha256:abc\n    # via\n    #   -r requirements.in\n    #   other\nflask[async]==3.0.0\n    # via -r requirements.in\n";
        A.Null(LockFiles.CompiledDifference("requests>=2.30\nflask[async]  # the web part\n", txt));
        A.Null(LockFiles.CompiledDifference("-r base.in\n--index-url https://x\nrequests ; python_version > '3'\nFlask\n", txt));
        Assert.Equal("click is in requirements.in but not in requirements.txt", LockFiles.CompiledDifference("requests\nflask\nclick\n", txt));
        Assert.Equal("requirements.in asks for requests>=3, requirements.txt has 2.31.0", LockFiles.CompiledDifference("requests>=3\nflask\n", txt));
        // flask was there only for the .in; requests is wanted by something else too, so taking it out is not a difference
        Assert.Equal("flask was taken out of requirements.in but is still in requirements.txt", LockFiles.CompiledDifference("requests\n", txt));
        A.Null(LockFiles.CompiledDifference("flask\n", txt));
    }

    [Fact]
    public void VersionSpecifiersAreChecked()
    {
        Assert.True(LockFiles.Satisfies("2.31.0", ">=2,<3"));
        A.False(LockFiles.Satisfies("3.0", ">=2,<3"));
        Assert.True(LockFiles.Satisfies("1.4.5", "~=1.4.2"));
        A.False(LockFiles.Satisfies("1.5.0", "~=1.4.2"));
        Assert.True(LockFiles.Satisfies("1.9", "~=1.4"));
        Assert.True(LockFiles.Satisfies("1.2.3", "==1.2.*"));
        A.False(LockFiles.Satisfies("1.3", "==1.2.*"));
        A.False(LockFiles.Satisfies("1.2", "!=1.2"));
        Assert.True(LockFiles.Satisfies("1.0", "==1.0.0"));
        Assert.True(LockFiles.Satisfies("1.0", "@ whatever"));
    }

    [Fact]
    public void UvAndPoetryAreAskedWhetherTheirLockFileIsOutOfDate()
    {
        using var t = new Temp();
        File.WriteAllText(Path.Combine(t.Root, "pyproject.toml"), "[project]\nname = \"x\"\n");
        File.WriteAllText(Path.Combine(t.Root, "uv.lock"), "version = 1\n");
        File.WriteAllText(Path.Combine(t.Root, "poetry.lock"), "# poetry\n");
        string? Which(string n) => "/bin/" + n;
        var asked = new List<string>();
        var answers = new Dictionary<string, (int, string, string)>
        {
            ["/bin/uv"] = (1, "Resolved 2 packages", "error: The lockfile at `uv.lock` needs to be updated, but `--check` was provided.\n\nhint: To update the lockfile, run `uv lock`."),
            ["/bin/poetry"] = (0, "All set!", ""),
        };
        var checkedLocks = LockFiles.CheckStale(t.Root, LockFiles.Detect(t.Root, Which, false), Which, (program, args, folder) =>
        {
            asked.Add(program + " " + string.Join(" ", args) + " in " + (folder == t.Root ? "root" : folder));
            return answers[program];
        });
        Assert.Equal("/bin/uv lock --check --offline --no-python-downloads in root|/bin/poetry check --lock --no-interaction in root", string.Join("|", asked));
        Assert.True(checkedLocks[0].Stale == true);
        Assert.Equal("pyproject.toml changed since uv.lock was made: run uv lock", checkedLocks[0].StaleNote);
        Assert.True(checkedLocks[1].Stale == false);

        // an answer that says something else: not known, and the reason (with any password masked)
        answers["/bin/uv"] = (2, "", "error: Failed to fetch https://me:pw@idx.example.org/simple/six/\nhint: try again");
        var unknown = LockFiles.CheckStale(t.Root, LockFiles.Detect(t.Root, Which, false), Which, (program, args, folder) => answers[program])[0];
        A.Null(unknown.Stale);
        Assert.Equal("uv could not tell: error: Failed to fetch https://me:***@idx.example.org/simple/six/", unknown.StaleNote);
        // without the tool nothing is run and nothing is said
        var none = LockFiles.CheckStale(t.Root, LockFiles.Detect(t.Root, _ => null, false), _ => null, (p, a, f) => throw new InvalidOperationException("run"));
        Assert.True(none.All(f => f.Stale == null && f.StaleNote == null));
    }

    [Fact]
    public void RequirementsInIsCompiledWithTheIndexSettingsKeptOutOfTheFile()
    {
        using var t = new Temp();
        File.WriteAllText(Path.Combine(t.Root, "requirements.in"), "requests\n");
        File.WriteAllText(Path.Combine(t.Root, "requirements.txt"), "#\n# This file was autogenerated by uv via the following command:\n#    uv pip compile requirements.in -o requirements.txt\nrequests==2.31.0\n");
        string? Which(string n) => n == "uv" ? "/bin/uv" : null;
        var settings = new PackageSettings { IndexUrl = "https://me:pw@idx.example.org/simple", TrustedHosts = { "idx.example.org" } };

        Assert.Equal("uv pip compile requirements.in -o requirements.txt", LockFiles.Detect(t.Root, Which, hasPipTools: true).Single().Compile);
        var uv = LockFiles.CompilePlan(t.Root, "/venv/bin/python", true, Which, hasPipTools: true, settings);
        Assert.Equal(("/bin/uv", "pip compile requirements.in -o requirements.txt --no-progress"), (uv.Program, string.Join(" ", uv.Args)));
        Assert.Equal("https://me:pw@idx.example.org/simple", uv.Env["UV_INDEX_URL"]);
        Assert.Equal("/venv/bin/python", uv.Env["UV_PYTHON"]);
        Assert.Equal("idx.example.org", uv.Env["UV_INSECURE_HOST"]);
        A.False(uv.Args.Any(a => a.Contains("pw")));

        // a file of pip-compile goes to pip-tools when the interpreter has it, and the index lines are not written into it
        File.WriteAllText(Path.Combine(t.Root, "requirements.txt"), "# This file is autogenerated by pip-compile\nrequests==2.31.0\n");
        var tools = LockFiles.CompilePlan(t.Root, "/venv/bin/python", true, Which, hasPipTools: true, settings);
        Assert.Equal(("/venv/bin/python", "-m piptools compile requirements.in -o requirements.txt --no-emit-index-url --no-emit-trusted-host"), (tools.Program, string.Join(" ", tools.Args)));
        Assert.Equal("https://me:pw@idx.example.org/simple", tools.Env["PIP_INDEX_URL"]);
        Assert.Equal("/bin/uv", LockFiles.CompilePlan(t.Root, "p", true, Which, hasPipTools: false, settings).Program);

        Assert.Throws<ServiceException>(() => LockFiles.CompilePlan(t.Root, "p", isLocal: false, Which, true, settings));
        Assert.Throws<ServiceException>(() => LockFiles.CompilePlan(t.Root, "p", true, _ => null, hasPipTools: false, settings));
        A.Null(LockFiles.Detect(t.Root, _ => null, hasPipTools: false).Single().Compile);
        File.Delete(Path.Combine(t.Root, "requirements.in"));
        Assert.Throws<ServiceException>(() => LockFiles.CompilePlan(t.Root, "p", true, Which, true, settings));
    }

    [Fact]
    public void ALockFileIsSyncedInAnotherSystemWithThatInterpretersOwnModules()
    {
        using var t = new Temp();
        File.WriteAllText(Path.Combine(t.Root, "pyproject.toml"), "[project]\nname = \"x\"\n");
        File.WriteAllText(Path.Combine(t.Root, "uv.lock"), "version = 1\n");
        File.WriteAllText(Path.Combine(t.Root, "poetry.lock"), "# poetry\n");
        File.WriteAllText(Path.Combine(t.Root, "requirements.in"), "requests\n");
        File.WriteAllText(Path.Combine(t.Root, "requirements.txt"), "requests==2.31.0\n");
        var wsl = new WslTarget("Ubuntu");
        var modules = new HashSet<string> { "uv", "piptools" };

        var found = LockFiles.DetectIn(t.Root, modules);
        Assert.Equal("python -m uv sync --frozen", found[0].Command);
        Assert.True(found[0].Available);
        Assert.Equal("poetry is not installed in that interpreter", found[1].Note);
        Assert.True(found[2].Available);
        A.Null(found[2].Compile);

        var uv = LockFiles.MakeRemotePlan(t.Root, "uv.lock", wsl, "/home/me/venv", isVenv: true, modules);
        Assert.Equal(("uv", "sync --frozen", "/home/me/venv"), (uv.Module, string.Join(" ", uv.Args), uv.Env["UV_PROJECT_ENVIRONMENT"]));
        var tools = LockFiles.MakeRemotePlan(t.Root, "requirements.txt", wsl, "/home/me/venv", true, modules);
        Assert.Equal(("piptools", "sync requirements.txt"), (tools.Module, string.Join(" ", tools.Args)));
        var poetry = LockFiles.MakeRemotePlan(t.Root, "poetry.lock", wsl, "/v", true, new HashSet<string> { "poetry" });
        Assert.Equal(("poetry", "/v", "false"), (poetry.Module, poetry.Env["VIRTUAL_ENV"], poetry.Env["POETRY_VIRTUALENVS_CREATE"]));

        // refused: a tool the interpreter lacks, not a virtual environment, a new container each run, this computer
        Assert.Throws<ServiceException>(() => LockFiles.MakeRemotePlan(t.Root, "poetry.lock", wsl, "/v", true, modules));
        Assert.Throws<ServiceException>(() => LockFiles.MakeRemotePlan(t.Root, "uv.lock", wsl, "/usr", isVenv: false, modules));
        string refusal = "";
        try { LockFiles.MakeRemotePlan(t.Root, "uv.lock", new DockerTarget("python:3.12-slim"), "/v", true, modules); }
        catch (ServiceException e) { refusal = e.Message; }
        A.Contains("new container", refusal);
        Assert.Throws<ServiceException>(() => LockFiles.MakeRemotePlan(t.Root, "uv.lock", ExecutionTarget.Local, "/v", true, modules));
    }

    /// <summary>With uv on this computer: its own check of uv.lock, offline (a project without dependencies, so nothing is fetched).</summary>
    [Fact]
    public void LiveUvTellsWhetherUvLockIsOutOfDate()
    {
        if (LockFiles.Find("uv") is not { } uv || RealPython == null) return;
        using var t = new Temp();
        string py = Path.Combine(t.Root, "pyproject.toml");
        File.WriteAllText(py, "[project]\nname = \"demo\"\nversion = \"0.1\"\nrequires-python = \">=3.8\"\ndependencies = []\n");
        var made = LockFiles.RunTool(uv, new[] { "lock", "--offline", "--no-python-downloads", "--python", RealPython }, t.Root);
        Assert.True(made.Code == 0, made.Error);
        LockFiles.Found Check() => LockFiles.CheckStale(t.Root, LockFiles.Detect(t.Root, LockFiles.Find, false), LockFiles.Find, LockFiles.RunTool).Single();
        Assert.True(Check().Stale == false, Check().StaleNote);
        File.WriteAllText(py, File.ReadAllText(py).Replace("version = \"0.1\"", "version = \"0.2\""));
        var after = Check();
        Assert.True(after.Stale == true, after.StaleNote);
    }

    /// <summary>With uv on this computer: requirements.in compiled from a wheel in a folder (no index, offline), the way the Compile button runs it.</summary>
    [Fact]
    public void LiveUvCompilesRequirementsIn()
    {
        if (LockFiles.Find("uv") is not { } uv || RealPython == null) return;
        using var t = new Temp();
        string wheels = Path.Combine(t.Root, "wheels");
        Directory.CreateDirectory(wheels);
        File.Move(t.Wheel("pmtiny", "1.2.0"), Path.Combine(wheels, "pmtiny-1.2.0-py3-none-any.whl"));
        File.WriteAllText(Path.Combine(t.Root, "requirements.in"), "--no-index\n--find-links wheels\npmtiny>=1\n");
        File.WriteAllText(Path.Combine(t.Root, "requirements.txt"), "# This file was autogenerated by uv via the following command:\n#    uv pip compile requirements.in -o requirements.txt\n");
        var plan = LockFiles.CompilePlan(t.Root, RealPython, true, LockFiles.Find, hasPipTools: false, new PackageSettings());
        var psi = new ProcessStartInfo(plan.Program) { WorkingDirectory = t.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in plan.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in plan.Env) psi.Environment[k] = v;
        psi.Environment["UV_OFFLINE"] = "1";
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.True(p.ExitCode == 0, err);
        string txt = File.ReadAllText(Path.Combine(t.Root, "requirements.txt"));
        A.Contains("pmtiny==1.2.0", txt);
        A.Contains("uv pip compile requirements.in -o requirements.txt", txt);
        A.Null(LockFiles.CompiledDifference(File.ReadAllText(Path.Combine(t.Root, "requirements.in")), txt));
        Assert.Equal("requirements.in asks for pmtiny>=2, requirements.txt has 1.2.0", LockFiles.CompiledDifference("pmtiny>=2\n", txt));
    }

    // ------------------------------------------------------------ environments made by uv

    [Fact]
    public void PipCommandsBecomeUvPipCommands()
    {
        var settings = new PackageSettings { IndexUrl = "https://idx.example.org/simple", TrustedHosts = { "idx.example.org" }, Proxy = "http://proxy.example.org:3128" };
        var install = UvPip.Translate("/bin/uv", "/p/.venv/Scripts/python.exe", PipCommands.Install(new[] { "requests>=2" }, upgrade: true, user: false, isVenv: true, settings))!;
        Assert.Equal("/bin/uv", install.Program);
        Assert.Equal("pip install --python /p/.venv/Scripts/python.exe --no-progress --upgrade --index-url https://idx.example.org/simple --allow-insecure-host idx.example.org requests>=2", string.Join(" ", install.Args));
        Assert.Equal("http://proxy.example.org:3128", install.Env["HTTPS_PROXY"]);
        Assert.Equal("pip uninstall --python py --no-progress six", string.Join(" ", UvPip.Translate("uv", "py", new[] { "uninstall", "-y", "--disable-pip-version-check", "six" })!.Args));
        Assert.Equal("pip list --python py --no-progress --format=json --outdated", string.Join(" ", UvPip.Translate("uv", "py", new[] { "list", "--format=json", "--outdated" })!.Args));
        Assert.Equal("pip install --python py --no-progress --prerelease allow x", string.Join(" ", UvPip.Translate("uv", "py", new[] { "install", "--pre", "x" })!.Args));
        // what uv has no counterpart for stays pip's
        A.Null(UvPip.Translate("uv", "py", new[] { "index", "versions", "x" }));
        A.Null(UvPip.Translate("uv", "py", new[] { "install", "--user", "x" }));
        A.Null(UvPip.Translate("uv", "py", Array.Empty<string>()));
    }

    [Fact]
    public void AnEnvironmentMadeByUvIsToldByItsPyvenvCfg()
    {
        using var t = new Temp();
        string env = Path.Combine(t.Root, ".venv"), scripts = Path.Combine(env, "Scripts");
        Directory.CreateDirectory(scripts);
        string python = Path.Combine(scripts, "python.exe");
        File.WriteAllText(python, "");
        File.WriteAllText(Path.Combine(env, "pyvenv.cfg"), "home = C:\\Python313\ninclude-system-site-packages = false\nversion = 3.13.1\n");
        Assert.Equal(env, UvPip.PrefixOf(python));
        A.False(UvPip.IsUvVenv(env));
        A.Null(UvPip.For(python, _ => "/bin/uv"));
        File.AppendAllText(Path.Combine(env, "pyvenv.cfg"), "uv = 0.12.3\n");
        Assert.True(UvPip.IsUvVenv(env));
        Assert.Equal("/bin/uv", UvPip.For(python, _ => "/bin/uv"));
        A.Null(UvPip.For(python, _ => null));                    // uv is not installed: pip is tried
        A.Null(UvPip.PrefixOf(Path.Combine(t.Root, "python.exe")));
    }

    /// <summary>With uv on this computer: a .venv made by uv, and its packages listed, installed, shown and removed through uv pip (from a wheel file, nothing is fetched).</summary>
    [Fact]
    public void LiveAnEnvironmentMadeByUvIsManagedWithUvPip()
    {
        if (LockFiles.Find("uv") is not { } uv || RealPython == null) return;
        using var t = new Temp();
        string python = new InterpreterManager(t.Root).CreateVenv(RealPython, ".venv", uv: uv);
        Assert.True(UvPip.IsUvVenv(Path.Combine(t.Root, ".venv")));
        Assert.Equal(uv, UvPip.For(python));
        var empty = PackageManager.List(python);
        Assert.True(empty.HasPip && empty.Error == null, empty.Error ?? "no pip");
        A.Empty(empty.Packages.Select(p => p.Name));

        string wheel = t.Wheel("pmuvlib", "3.1.0");
        var installed = PackageManager.Exec(python, new[] { "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--progress-bar", "off", "--offline", wheel }, 120000);
        Assert.True(installed.Code == 0, installed.Error);
        Assert.Equal("pmuvlib 3.1.0", string.Join(",", PackageManager.List(python).Packages.Select(p => p.Name + " " + p.Version)));
        var info = PackageManager.Show(python, "pmuvlib");
        Assert.Equal(("pmuvlib", "3.1.0"), (info!.Name, info.Version));
        var removed = PackageManager.Exec(python, new[] { "-m", "pip", "uninstall", "-y", "--disable-pip-version-check", "pmuvlib" }, 60000);
        Assert.True(removed.Code == 0, removed.Error);
        A.Empty(PackageManager.List(python).Packages.Select(p => p.Name));
        // the environment really has no pip: the same list through its own python fails
        var own = PackageManager.Exec(python, new[] { "-c", "import pip" }, 30000);
        A.NotEqual(0, own.Code);
    }

    // ------------------------------------------------------------ pipenv

    [Fact]
    public void PipfileLockIsSyncedVerifiedAndLockedWithPipenvInTheSelectedEnvironment()
    {
        using var t = new Temp();
        File.WriteAllText(Path.Combine(t.Root, "Pipfile.lock"), "{}");
        string? Which(string n) => n == "pipenv" ? "/bin/pipenv" : null;
        Assert.Equal("there is no Pipfile next to it", LockFiles.Detect(t.Root, Which, false).Single().Note);
        File.WriteAllText(Path.Combine(t.Root, "Pipfile"), "[packages]\n");
        Assert.Equal("pipenv is not installed", LockFiles.Detect(t.Root, _ => null, false).Single().Note);
        var row = LockFiles.Detect(t.Root, Which, false).Single();
        Assert.True(row.Available);
        Assert.Equal(("pipenv sync", "pipenv lock"), (row.Command, row.Compile));

        var sync = LockFiles.MakePlan(t.Root, "Pipfile.lock", "/venv/bin/python", "/venv", isVenv: true, isLocal: true, Which, false);
        Assert.Equal(("/bin/pipenv", "sync", "/venv", "0"), (sync.Program, string.Join(" ", sync.Args), sync.Env["VIRTUAL_ENV"], sync.Env["PIPENV_IGNORE_VIRTUALENVS"]));
        var lockPlan = LockFiles.PipenvLockPlan(t.Root, "/venv", isVenv: true, isLocal: true, Which);
        Assert.Equal(("/bin/pipenv", "lock", "/venv"), (lockPlan.Program, string.Join(" ", lockPlan.Args), lockPlan.Env["VIRTUAL_ENV"]));
        Assert.Throws<ServiceException>(() => LockFiles.PipenvLockPlan(t.Root, "/usr", isVenv: false, true, Which));
        Assert.Throws<ServiceException>(() => LockFiles.PipenvLockPlan(t.Root, "/venv", true, isLocal: false, Which));
        Assert.Throws<ServiceException>(() => LockFiles.PipenvLockPlan(t.Root, "/venv", true, true, _ => null));

        string answer = "Pipfile.lock is out-of-date. Run $ pipenv lock to update.";
        var stale = LockFiles.CheckStale(t.Root, new[] { row }, Which, (program, args, folder) => (1, answer, ""))[0];
        Assert.True(stale.Stale == true);
        Assert.Equal("the Pipfile changed since Pipfile.lock was made: run pipenv lock", stale.StaleNote);
        Assert.True(LockFiles.CheckStale(t.Root, new[] { row }, Which, (program, args, folder) => (0, "Pipfile.lock is up-to-date.", ""))[0].Stale == false);
    }

    [Fact]
    public void TheEnvironmentsPipenvMadeForTheProjectAreFound()
    {
        using var t = new Temp();
        string root = Path.Combine(t.Root, "my proj"), workon = Path.Combine(t.Root, "workon");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "my_proj-AbCd1234", "my_proj-short", "my_projx-AbCd1234", "my_proj-NoCfg123" })
        {
            Directory.CreateDirectory(Path.Combine(workon, name));
            if (name != "my_proj-NoCfg123") File.WriteAllText(Path.Combine(workon, name, "pyvenv.cfg"), "home = x\n");
        }
        Assert.Equal("my_proj-AbCd1234", string.Join(",", InterpreterDiscovery.PipenvEnvironments(workon, root).Select(Path.GetFileName)));
        A.Empty(InterpreterDiscovery.PipenvEnvironments(Path.Combine(t.Root, "none"), root));
    }

    /// <summary>With pipenv (PYMAGIC_TEST_PIPENV names pipenv.exe): Pipfile.lock made, told up to date, synced into a venv of the test, told out of date after the Pipfile changes; no environment of pipenv's own is made.</summary>
    [Fact]
    public void LivePipenvLocksSyncsAndVerifiesInTheSelectedEnvironment()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_PIPENV") is not { Length: > 0 } pipenv || !File.Exists(pipenv) || RealPython == null) return;
        string? Which(string n) => n == "pipenv" ? pipenv : LockFiles.Find(n);
        using var t = new Temp();
        string wheels = Path.Combine(t.Root, "wheels");
        Directory.CreateDirectory(wheels);
        File.Move(t.Wheel("pmpipenv", "2.0.0"), Path.Combine(wheels, "pmpipenv-2.0.0-py3-none-any.whl"));
        File.WriteAllText(Path.Combine(t.Root, "Pipfile"), "[packages]\npmpipenv = {file = \"wheels/pmpipenv-2.0.0-py3-none-any.whl\"}\n\n[dev-packages]\n");
        string python = new InterpreterManager(t.Root).CreateVenv(RealPython, ".venv", withPip: false);
        string prefix = Path.Combine(t.Root, ".venv");
        void Run(LockFiles.Plan plan)
        {
            var psi = new ProcessStartInfo(plan.Program) { WorkingDirectory = t.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in plan.Args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in plan.Env) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            string err = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(240000) && p.ExitCode == 0, plan.Program + " " + string.Join(" ", plan.Args) + ": " + err + output.Result);
        }
        Run(LockFiles.PipenvLockPlan(t.Root, prefix, true, true, Which));
        A.Contains("pmpipenv", File.ReadAllText(Path.Combine(t.Root, "Pipfile.lock")));
        LockFiles.Found Check() => LockFiles.CheckStale(t.Root, LockFiles.Detect(t.Root, Which, false), Which, LockFiles.RunTool).Single(f => f.Kind == "pipenv");
        Assert.True(Check().Stale == false, Check().StaleNote);

        Run(LockFiles.MakePlan(t.Root, "Pipfile.lock", python, prefix, true, true, Which, false));
        Assert.Equal("pmpipenv 2.0.0", string.Join(",", DependencyTree.Read(python, null).Values.Where(d => d.Name.StartsWith("pmpipenv")).Select(d => d.Name + " " + d.Version)));

        File.AppendAllText(Path.Combine(t.Root, "Pipfile"), "\n[requires]\npython_version = \"3.99\"\n");
        Assert.True(Check().Stale == true, Check().StaleNote);
        // pipenv worked in the test's environment: none of its own under ~/.virtualenvs for this folder
        A.Empty(InterpreterDiscovery.PipenvEnvironments(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".virtualenvs"), t.Root));
    }

    [Fact]
    public void AProgramRunsInTheProjectAndItsOutputStreams()
    {
        if (RealPython == null) return;
        using var t = new Temp();
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add);
        session.StartProgram(t.Root, RealPython, new[] { "-c", "import os; print('sync', os.environ['PM_TEST_VALUE'], os.path.basename(os.getcwd()))" }, new Dictionary<string, string> { ["PM_TEST_VALUE"] = "ok" });
        var text = new System.Text.StringBuilder();
        while (true)
        {
            Assert.True(events.TryTake(out var e, TimeSpan.FromSeconds(60)), "timed out: " + text);
            string type = e!["type"]!.GetValue<string>();
            if (type == "output") text.Append(e["text"]!.GetValue<string>());
            if (type == "error") throw new AssertionException(e["message"]!.GetValue<string>());
            if (type == "exited") { Assert.Equal(0, e["code"]!.GetValue<int>()); break; }
        }
        A.Contains("sync ok " + Path.GetFileName(t.Root), text.ToString());
    }
}

/// <summary>The checks the harness's Assert does not have.</summary>
static class A
{
    public static void False(bool c, string? msg = null) => Assert.True(!c, msg ?? "expected false");
    public static void Null(object? o) => Assert.True(o == null, "expected null");
    public static void NotEqual(int a, int b) => Assert.True(a != b, $"expected not {a}");
    public static void Empty<T>(IEnumerable<T> list) => Assert.True(!list.Any(), "expected nothing, got " + string.Join(", ", list));
    public static void Contains(string needle, string hay) => Assert.Contains(needle, hay);
    public static void Contains(string needle, IEnumerable<string> list) => Assert.True(list.Contains(needle), $"'{needle}' not in: " + string.Join(" | ", list));
    public static void Contains<T>(IEnumerable<T> list, Func<T, bool> match) => Assert.True(list.Any(match), "no item matches");
    public static void DoesNotContain(string needle, IEnumerable<string> list) => Assert.True(!list.Contains(needle), $"'{needle}' is in the list");
}
