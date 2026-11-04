using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class InterpreterTests
{
    static readonly bool Win = OperatingSystem.IsWindows();

    /// <summary>The python that the machine running the tests has (null when there is none).</summary>
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

    sealed class Sandbox : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "pm_interp_" + Guid.NewGuid().ToString("N"));
        public Sandbox() { Directory.CreateDirectory(Dir); }
        public string Path_(params string[] parts) => Path.Combine(new[] { Dir }.Concat(parts).ToArray());
        public string Touch(params string[] parts)
        {
            string p = Path_(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
            return p;
        }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static string VenvPython(string venv) => Win ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", "python");

    /// <summary>A project folder with the given virtual environments (folders with pyvenv.cfg and an interpreter file).</summary>
    static string FakeVenv(Sandbox s, params string[] relative)
    {
        s.Touch(relative.Concat(new[] { "pyvenv.cfg" }).ToArray());
        string exe = VenvPython(s.Path_(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        return exe;
    }

    static DiscoveryEnvironment Fake(Sandbox s, string root, IEnumerable<string>? pathDirs = null, string? virtualEnv = null, string? conda = null) =>
        new(root, (pathDirs ?? Array.Empty<string>()).ToList(), virtualEnv, conda, s.Path_("home"), s.Path_("localappdata"), s.Path_("appdata"), Win, false);

    // ------------------------------------------------------------ discovery on a fake machine

    [Fact]
    public void ProjectVirtualEnvironmentsComeFirst()
    {
        using var s = new Sandbox();
        string root = s.Path_("work", "proj");
        Directory.CreateDirectory(root);
        string venv = FakeVenv(s, "work", "proj", ".venv");
        string parentVenv = FakeVenv(s, "work", "venv");         // one folder up: shared between projects
        string nested = FakeVenv(s, "work", "proj", "tools", "py");   // not a usual name, and two levels down: not found
        string custom = FakeVenv(s, "work", "proj", "myenv");     // any folder with pyvenv.cfg directly in the project
        string pathDir = s.Path_("bin");
        string onPath = s.Touch(Win ? new[] { "bin", "python.exe" } : new[] { "bin", "python3" });

        var found = InterpreterDiscovery.Discover(Fake(s, root, new[] { pathDir }));
        Assert.Equal(Path.GetFullPath(venv), found[0].Path);
        Assert.Equal("venv", found[0].Kind);
        Assert.Equal(".venv", found[0].Source);
        var paths = found.Select(f => f.Path).ToList();
        Assert.True(paths.Contains(Path.GetFullPath(parentVenv)), "venv in the parent folder");
        Assert.True(paths.Contains(Path.GetFullPath(custom)), "project subfolder with pyvenv.cfg");
        Assert.True(!paths.Contains(Path.GetFullPath(nested)), "deeper folders are not searched");
        Assert.True(paths.IndexOf(Path.GetFullPath(onPath)) > paths.IndexOf(Path.GetFullPath(parentVenv)), "PATH comes after the project's environments");
        Assert.Equal("PATH", found.Single(f => f.Path == Path.GetFullPath(onPath)).Source);
    }

    [Fact]
    public void ActivatedEnvironmentsAreFoundAndNothingIsListedTwice()
    {
        using var s = new Sandbox();
        string root = s.Path_("proj");
        Directory.CreateDirectory(root);
        string active = FakeVenv(s, "elsewhere", "myvenv");
        string conda = Win ? s.Touch("condaenv", "python.exe") : s.Touch("condaenv", "bin", "python");

        var found = InterpreterDiscovery.Discover(Fake(s, root, new[] { s.Path_("elsewhere", "myvenv", Win ? "Scripts" : "bin") },
            virtualEnv: s.Path_("elsewhere", "myvenv"), conda: s.Path_("condaenv")));
        Assert.Equal(1, found.Count(f => f.Path == Path.GetFullPath(active)));      // VIRTUAL_ENV and PATH both point at it
        Assert.Equal("VIRTUAL_ENV", found.Single(f => f.Path == Path.GetFullPath(active)).Source);
        Assert.Equal("conda", found.Single(f => f.Path == Path.GetFullPath(conda)).Kind);
    }

    [Fact]
    public void ManagedInstallLocationsAreRecognised()
    {
        using var s = new Sandbox();
        string root = s.Path_("proj");
        Directory.CreateDirectory(root);
        string uv, pyenv, conda, system;
        if (Win)
        {
            uv = s.Touch("appdata", "uv", "python", "cpython-3.12-windows", "python.exe");
            pyenv = s.Touch("home", ".pyenv", "pyenv-win", "versions", "3.11.9", "python.exe");
            conda = s.Touch("home", "miniconda3", "envs", "data", "python.exe");
            system = s.Touch("localappdata", "Programs", "Python", "Python312", "python.exe");
        }
        else
        {
            uv = s.Touch("home", ".local", "share", "uv", "python", "cpython-3.12", "bin", "python");
            pyenv = s.Touch("home", ".pyenv", "versions", "3.11.9", "bin", "python");
            conda = s.Touch("home", "miniconda3", "envs", "data", "bin", "python");
            system = "";
        }
        var found = InterpreterDiscovery.Discover(Fake(s, root));
        string KindOf(string p) => found.Single(f => f.Path == Path.GetFullPath(p)).Kind;
        Assert.Equal("uv", KindOf(uv));
        Assert.Equal("pyenv", KindOf(pyenv));
        Assert.Equal("conda", KindOf(conda));
        if (Win) Assert.Equal("system", KindOf(system));
    }

    [Fact]
    public void EmptyStoreAliasesAreIgnored()
    {
        if (!Win) return;
        using var s = new Sandbox();
        string root = s.Path_("proj");
        Directory.CreateDirectory(root);
        string alias = s.Path_("Microsoft", "WindowsApps", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        File.WriteAllBytes(alias, Array.Empty<byte>());         // what the Store alias really is: zero bytes
        var found = InterpreterDiscovery.Discover(Fake(s, root, new[] { Path.GetDirectoryName(alias)! }));
        Assert.True(!found.Any(f => f.Path == Path.GetFullPath(alias)), "running it would open the Microsoft Store");
    }

    // ------------------------------------------------------------ asking an interpreter

    [Fact]
    public void InspectionReportsVersionPlatformAndPaths()
    {
        if (RealPython == null) return;
        var r = InterpreterInspector.Inspect(RealPython, "system", "test");
        Assert.True(r.Interpreter != null, r.Error);
        var i = r.Interpreter!;
        Assert.Equal("cpython", i.Implementation);
        Assert.True(i.Major == 3 && i.Minor >= 8, i.Version);
        Assert.Equal(Win ? "win32" : i.Platform, i.Platform);
        Assert.True(i.Stdlib != null && File.Exists(Path.Combine(i.Stdlib, "os.py")), "stdlib folder holds os.py");
        Assert.True(i.SysPath.Count > 0);
        Assert.Contains("Python", i.Label);
        Assert.Contains(i.Version, i.Label);

        // the stdlib is offered as the last library root only when a typeshed provides the stubs
        Assert.True(!i.LibraryRoots(includeStdlib: false).Any(p => string.Equals(p, Path.GetFullPath(i.Stdlib!), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Path.GetFullPath(i.Stdlib!), i.LibraryRoots(includeStdlib: true).Last());
    }

    [Fact]
    public void InspectionFailuresAreReportedNotThrown()
    {
        using var s = new Sandbox();
        Assert.Contains("not found", InterpreterInspector.Inspect(s.Path_("missing.exe")).Error!);
        string notPython = s.Touch("fake.exe");
        var r = InterpreterInspector.Inspect(notPython);
        Assert.True(r.Interpreter == null && !string.IsNullOrEmpty(r.Error));
    }

    // ------------------------------------------------------------ a real virtual environment

    /// <summary>A venv without pip, holding a package and a module that is only reachable through a .pth file.</summary>
    static (string Python, string Site) MakeVenv(Sandbox s)
    {
        var mgr = new InterpreterManager(s.Dir, Fake(s, s.Dir));
        string exe = mgr.CreateVenv(RealPython!, ".venv", withPip: false);
        string site = Directory.GetDirectories(s.Path_(".venv"), "site-packages", SearchOption.AllDirectories).First();
        Directory.CreateDirectory(Path.Combine(site, "fakepkg"));
        File.WriteAllText(Path.Combine(site, "fakepkg", "__init__.py"), "VALUE = 3\n");
        Directory.CreateDirectory(s.Path_("extra"));
        File.WriteAllText(s.Path_("extra", "pthmod.py"), "NUMBER = 4.5\n");
        File.WriteAllText(Path.Combine(site, "extra.pth"), s.Path_("extra") + "\n");
        Directory.SetLastWriteTimeUtc(site, DateTime.UtcNow.AddMinutes(1));   // a plain write may not move the directory's stamp
        return (exe, site);
    }

    [Fact]
    public void AVirtualEnvironmentIsRecognisedWithItsPthEntries()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var (exe, site) = MakeVenv(s);

        var i = InterpreterInspector.Inspect(exe, "venv", ".venv").Interpreter!;
        Assert.True(i.IsVenv, "prefix differs from base_prefix");
        Assert.Contains(".venv", i.Label);
        Assert.True(i.SitePackages.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(site), StringComparison.OrdinalIgnoreCase)));
        Assert.True(i.SysPath.Any(p => string.Equals(Path.GetFullPath(p), s.Path_("extra"), StringComparison.OrdinalIgnoreCase)), ".pth entries are part of sys.path");
        Assert.True(string.Equals(Path.GetFullPath(site), i.LibraryRoots(false).First(), StringComparison.OrdinalIgnoreCase), "site-packages is searched first");
    }

    [Fact]
    public void PackagesAddedLaterAreNoticed()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var (exe, site) = MakeVenv(s);
        var before = InterpreterInspector.Inspect(exe, "venv", ".venv").Interpreter!;
        Assert.True(!before.SysPath.Any(p => p.EndsWith("later", StringComparison.OrdinalIgnoreCase)));

        Directory.CreateDirectory(s.Path_("later"));
        File.WriteAllText(Path.Combine(site, "later.pth"), s.Path_("later") + "\n");
        Directory.SetLastWriteTimeUtc(site, DateTime.UtcNow.AddMinutes(2));
        var after = InterpreterInspector.Inspect(exe, "venv", ".venv").Interpreter!;
        Assert.True(after.SysPath.Any(p => p.EndsWith("later", StringComparison.OrdinalIgnoreCase)), "the cached answer was dropped");
    }

    [Fact]
    public void TheEditorSeesTheEnvironmentsLibrariesAndVersion()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var (exe, _) = MakeVenv(s);
        var interpreter = InterpreterInspector.Inspect(exe, "venv", ".venv").Interpreter!;

        const string uri = "file:///c:/proj/main.py";
        const string code = "import fakepkg\nimport pthmod\nfakepkg.VALUE\npthmod.NUMBER\nfakepkg.missing\n";
        var svc = new LanguageService();
        svc.Open(uri, code, 1);
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Message.Contains("missing")), "without the environment the module is unknown, so nothing can be said about it");
        Assert.Equal(0, svc.Hover(uri, new PyMagic.Server.Position(2, 9)) is { } h0 && h0.Contents.Value.Contains("int") ? 1 : 0);

        svc.ConfigureInterpreter(interpreter);
        Assert.Equal(interpreter.Minor, svc.Environment.Minor);
        Assert.Equal(interpreter.Platform, svc.Environment.Platform);
        Assert.True(svc.Diagnostics(uri).Any(d => d.Message.Contains("missing")), "fakepkg now resolves from site-packages");
        Assert.Contains("int", svc.Hover(uri, new PyMagic.Server.Position(2, 9))!.Contents.Value);
        Assert.Contains("float", svc.Hover(uri, new PyMagic.Server.Position(3, 8))!.Contents.Value);   // pthmod comes in through the .pth file

        svc.ConfigureInterpreter(null);
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Message.Contains("missing")), "back to the defaults");
    }

    [Fact]
    public void ChangingTheInterpreterRepublishesDiagnostics()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var (exe, _) = MakeVenv(s);

        var sent = new List<JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), sent.Add);
        const string uri = "file:///c:/proj/main.py";
        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri +
            "\",\"languageId\":\"python\",\"version\":1,\"text\":\"import fakepkg\\nfakepkg.missing\\n\"}}}")!);
        int CountPublished() => sent.Count(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        int Problems() => sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray().Count;
        Assert.Equal(0, Problems());

        string json = JsonValue.Create(exe)!.ToJsonString();
        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeConfiguration\",\"params\":{\"settings\":{\"pymagic\":{\"pythonPath\":" + json + "}}}}")!);
        Assert.Equal(1, Problems());

        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeConfiguration\",\"params\":{\"settings\":{\"pymagic\":{\"pythonPath\":\"\"}}}}")!);
        Assert.Equal(0, Problems());
        Assert.True(CountPublished() >= 3);
    }

    // ------------------------------------------------------------ choosing and remembering

    [Fact]
    public void TheChoiceIsRememberedInTheProjectFolder()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        string root = s.Path_("proj");
        Directory.CreateDirectory(root);
        var env = Fake(s, root, new[] { Path.GetDirectoryName(RealPython)! });

        var first = new InterpreterManager(root, env);
        var picked = first.Select(RealPython);
        Assert.True(File.Exists(first.SettingsFile));
        Assert.Contains("interpreter", File.ReadAllText(first.SettingsFile));
        Assert.True(File.Exists(Path.Combine(root, ".pymagic", ".gitignore")), "the settings folder ignores itself");

        var second = new InterpreterManager(root, env);
        Assert.Equal(picked.Path, second.InitializeDefault()!.Path);

        File.WriteAllText(first.SettingsFile, "{\"interpreter\": \"" + s.Path_("gone.exe").Replace("\\", "\\\\") + "\"}");
        Assert.True(new InterpreterManager(root, env).InitializeDefault() != null, "a stale choice falls back to discovery");
        Assert.Throws<ServiceException>(() => first.Select(s.Path_("gone.exe")));
    }

    [Fact]
    public void AutomaticChoicePrefersTheProjectsEnvironment()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var (exe, _) = MakeVenv(s);
        var manager = new InterpreterManager(s.Dir, Fake(s, s.Dir, new[] { Path.GetDirectoryName(RealPython)! }));
        var chosen = manager.InitializeDefault()!;
        Assert.Equal(Path.GetFullPath(exe), chosen.Path);
        Assert.Equal("venv", chosen.Kind);

        var all = manager.ListAsync().Result;
        Assert.Equal(chosen.Path, all[0].Path);
        Assert.True(all.Count >= 2 || all.Count == 1);
    }

    [Fact]
    public void CreatingAnEnvironmentRefusesToOverwrite()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var manager = new InterpreterManager(s.Dir, Fake(s, s.Dir));
        string exe = manager.CreateVenv(RealPython, ".venv", withPip: false);
        Assert.True(File.Exists(exe));
        Assert.Throws<ServiceException>(() => manager.CreateVenv(RealPython, ".venv", withPip: false));
        Assert.Throws<ServiceException>(() => manager.CreateVenv(RealPython, "..\\escape", withPip: false));
    }

    [Fact]
    public void AnEnvironmentMadeInTheProjectBecomesItsInterpreterWhenItHadNoneOfItsOwn()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var manager = new InterpreterManager(s.Dir, Fake(s, s.Dir, new[] { Path.GetDirectoryName(RealPython)! }));
        manager.Select(RealPython);   // a Python outside the project, as before uv sync
        Assert.True(manager.AdoptNewEnvironment(".venv", TimeSpan.FromSeconds(1)) == null, "no environment yet");
        string exe = new InterpreterManager(s.Dir, Fake(s, s.Dir)).CreateVenv(RealPython, ".venv", withPip: false);   // made by something else
        var taken = manager.AdoptNewEnvironment(".venv")!;
        Assert.Equal(Path.GetFullPath(exe), taken.Path);
        Assert.Equal(taken.Path, new InterpreterManager(s.Dir, Fake(s, s.Dir)).InitializeDefault()!.Path);   // and remembered
        // one of the project's own is kept: a second one is not taken
        new InterpreterManager(s.Dir, Fake(s, s.Dir)).CreateVenv(RealPython, "venv", withPip: false);
        Assert.True(manager.AdoptNewEnvironment("venv") == null, "the project has its own already");
        Assert.Equal(taken.Path, manager.Current!.Path);
    }

    [Fact]
    public void AUvProjectUsesItsOwnVenvOverAPythonSavedOutsideIt()
    {
        if (RealPython == null) return;
        using var s = new Sandbox();
        var env = Fake(s, s.Dir, new[] { Path.GetDirectoryName(RealPython)! });
        new InterpreterManager(s.Dir, env).Select(RealPython);
        string exe = new InterpreterManager(s.Dir, env).CreateVenv(RealPython, ".venv", withPip: false);
        // not a uv project: the saved choice stays
        Assert.Equal(Path.GetFullPath(RealPython), Path.GetFullPath(new InterpreterManager(s.Dir, env).InitializeDefault()!.Path));
        File.WriteAllText(s.Path_("pyproject.toml"), "[project]\nname = \"shop\"\nversion = \"0.1.0\"\ndependencies = []\n");
        File.WriteAllText(s.Path_("uv.lock"), "version = 1\n");
        Assert.Equal(Path.GetFullPath(exe), new InterpreterManager(s.Dir, env).InitializeDefault()!.Path);
    }

    [Fact]
    public void TheWatcherKnowsAnEnvironmentByItsPyvenvCfg()
    {
        Assert.Equal(".venv", ProjectWatcher.EnvironmentOf(".venv/pyvenv.cfg"));
        Assert.Equal("venv", ProjectWatcher.EnvironmentOf("venv\\pyvenv.cfg"));
        Assert.True(ProjectWatcher.EnvironmentOf("tools/.venv/pyvenv.cfg") == null, "only the project folder's own");
        Assert.True(ProjectWatcher.EnvironmentOf(".venv/Lib/site.py") == null);
        Assert.True(ProjectWatcher.EnvironmentOf("node_modules/pyvenv.cfg") == null);
    }
}
