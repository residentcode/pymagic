using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class PackageTests
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

    // ------------------------------------------------------------ names and requirement files

    [Fact]
    public void NamesAreNormalisedLikePip()
    {
        Assert.Equal("typing-extensions", PackageManager.Normalize("Typing_Extensions"));
        Assert.Equal("zope-interface", PackageManager.Normalize("zope.interface"));
        Assert.Equal("a-b", PackageManager.Normalize("A--b"));
    }

    [Fact]
    public void OnlyPlainSpecsAreAccepted()
    {
        foreach (var ok in new[] { "requests", "flask>=3.0", "numpy==1.26.4", "uvicorn[standard]", "django>=4,<5", "a.b-c_d" })
            Assert.True(PackageManager.IsSafeSpec(ok), ok);
        foreach (var bad in new[] { "", "--index-url", "-r", "../x", "a b", "pkg;rm", "http://evil/x.whl", "C:\\x.whl", "$(x)", "a&b", "-e" })
            Assert.True(!PackageManager.IsSafeSpec(bad), bad);
    }

    [Fact]
    public void RequirementsFilesAreParsed()
    {
        var r = RequirementsScanner.ParseRequirementsTxt(
            "# a comment\n" +
            "requests>=2.31\n" +
            "  Flask[async] == 3.0   # inline comment\n" +
            "numpy ; python_version < '3.12'\n" +
            "-r other.txt\n" +
            "-e .\n" +
            "--index-url https://example.org/simple\n" +
            "git+https://github.com/x/y.git#egg=y\n" +
            "./local/pkg\n" +
            "\n" +
            "pytest\n");
        Assert.Equal("requests,Flask,numpy,pytest", string.Join(",", r.Select(x => x.Name)));
        Assert.Equal("requests>=2.31", r[0].Spec);
        Assert.Equal("Flask[async]==3.0", r[1].Spec);
        Assert.Equal("numpy", r[2].Spec);   // the environment marker is dropped
    }

    [Fact]
    public void PyprojectDependenciesAreParsed()
    {
        var r = RequirementsScanner.ParsePyproject(
            "[build-system]\nrequires = [\"setuptools\"]\n\n" +
            "[project]\nname = \"demo\"\ndependencies = [\n  \"httpx>=0.27\",\n  'rich',   # pretty output\n]\n\n" +
            "[project.optional-dependencies]\ndev = [\"pytest\", \"ruff>=0.5\"]\ndocs = [\"mkdocs\"]\n\n" +
            "[tool.other]\ndependencies = [\"not-this\"]\n");
        Assert.Equal("httpx,rich,pytest,ruff,mkdocs", string.Join(",", r.Select(x => x.Name)));
        Assert.True(!r.Any(x => x.Name == "setuptools" || x.Name == "not-this"));
    }

    [Fact]
    public void ScanFindsFilesInTheProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_req_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "requirements"));
        try
        {
            File.WriteAllText(Path.Combine(root, "requirements.txt"), "requests\n");
            File.WriteAllText(Path.Combine(root, "requirements-dev.txt"), "pytest\n");
            File.WriteAllText(Path.Combine(root, "requirements", "docs.txt"), "mkdocs\n");
            File.WriteAllText(Path.Combine(root, "empty.txt"), "x\n");
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\ndependencies = [\"rich\"]\n");
            Assert.Equal("requirements-dev.txt,requirements.txt,requirements/docs.txt,pyproject.toml", string.Join(",", RequirementsScanner.Scan(root).Select(f => f.File)));
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------ real pip in a real virtual environment

    sealed class Project : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_pip_" + Guid.NewGuid().ToString("N"));
        public Project() { Directory.CreateDirectory(Root); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        public string Venv(bool withPip)
        {
            var manager = new InterpreterManager(Root);
            return manager.CreateVenv(RealPython!, ".venv", withPip);
        }

        /// <summary>A minimal wheel for a package that needs no build step and no network.</summary>
        public string Wheel(string name = "demopkg", string version = "1.0")
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
            Add($"{info}/METADATA", $"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\nSummary: A package made for tests\n");
            Add($"{info}/WHEEL", "Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
            Add($"{info}/RECORD", $"{name}/__init__.py,,\n{info}/METADATA,,\n{info}/WHEEL,,\n{info}/RECORD,,\n");
            return path;
        }

        /// <summary>Runs `python -m module args` and returns the exit code and everything it printed.</summary>
        public (int Code, string Output) Module(string python, string module, params string[] args)
        {
            var events = new BlockingCollection<JsonNode>();
            using var session = new RunSession(events.Add) { Python = python };
            session.StartModule(Root, module, args);
            var text = new System.Text.StringBuilder();
            while (true)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(180))) throw new AssertionException("timed out: " + text);
                string type = e["type"]!.GetValue<string>();
                if (type == "output") text.Append(e["text"]!.GetValue<string>());
                if (type == "error") throw new AssertionException(e["message"]!.GetValue<string>());
                if (type == "exited") return (e["code"]!.GetValue<int>(), text.ToString());
            }
        }
    }

    [Fact]
    public void ListInstallAndUninstallWithPip()
    {
        if (RealPython == null) return;
        using var p = new Project();
        string python = p.Venv(withPip: true);

        var before = PackageManager.List(python);
        Assert.True(before.HasPip, before.Error);
        Assert.True(before.Packages.Any(x => x.Name.Equals("pip", StringComparison.OrdinalIgnoreCase)), "pip lists itself");
        Assert.True(!before.Packages.Any(x => x.Name == "demopkg"));

        var install = p.Module(python, "pip", "install", "--no-index", "--disable-pip-version-check", "--no-input", "--progress-bar", "off", p.Wheel());
        Assert.Equal(0, install.Code);
        Assert.Contains("Successfully installed demopkg-1.0", install.Output);

        var installed = PackageManager.List(python).Packages.Single(x => x.Name == "demopkg");
        Assert.Equal("1.0", installed.Version);
        Assert.True(!installed.Editable);

        var info = PackageManager.Show(python, "demopkg")!;
        Assert.Equal("A package made for tests", info.Summary);
        Assert.Equal("1.0", info.Version);
        Assert.Contains("site-packages", info.Location!);
        Assert.Equal(0, info.Requires.Count);
        Assert.True(PackageManager.Show(python, "not-installed-anywhere") == null);

        var removal = p.Module(python, "pip", "uninstall", "-y", "--disable-pip-version-check", "demopkg");
        Assert.Equal(0, removal.Code);
        Assert.True(!PackageManager.List(python).Packages.Any(x => x.Name == "demopkg"));
    }

    [Fact]
    public void AnEnvironmentWithoutPipIsReportedAndCanBeFixed()
    {
        if (RealPython == null) return;
        using var p = new Project();
        string python = p.Venv(withPip: false);

        var none = PackageManager.List(python);
        Assert.True(!none.HasPip);
        // what is installed is still known without pip (an environment uv made has none): Python's own record
        Assert.True(none.Error == null, none.Error);
        Assert.True(!none.Packages.Any(x => x.Name == "shoplib"));
        string site = Directory.GetDirectories(Path.Combine(p.Root, ".venv"), "site-packages", SearchOption.AllDirectories).First();
        Directory.CreateDirectory(Path.Combine(site, "shoplib-2.1.dist-info"));
        File.WriteAllText(Path.Combine(site, "shoplib-2.1.dist-info", "METADATA"), "Metadata-Version: 2.1\nName: shoplib\nVersion: 2.1\n");
        Assert.Equal("2.1", PackageManager.List(python).Packages.Single(x => x.Name == "shoplib").Version);

        var fix = p.Module(python, "ensurepip", "--upgrade");
        Assert.Equal(0, fix.Code);
        Assert.True(PackageManager.List(python).HasPip, "ensurepip installs pip from the copy that ships with Python");
    }

    [Fact]
    public void InstalledPackagesBecomeVisibleToTheEditor()
    {
        if (RealPython == null) return;
        using var p = new Project();
        string python = p.Venv(withPip: true);
        var manager = new InterpreterManager(p.Root);
        manager.Select(python, save: false);
        int announced = 0;
        manager.Changed += _ => announced++;

        const string uri = "file:///c:/proj/main.py";
        const string code = "import demopkg\ndemopkg.VALUE\ndemopkg.missing\n";
        var svc = new LanguageService();
        svc.Open(uri, code, 1);
        svc.ConfigureInterpreter(manager.Current);
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Message.Contains("missing")), "not installed yet: the module is unknown");
        Assert.True(svc.Diagnostics(uri).Any(d => d.Message == "No module named 'demopkg'"), "the import itself is reported while the package is missing");

        Assert.Equal(0, p.Module(python, "pip", "install", "--no-index", "--disable-pip-version-check", "--no-input", p.Wheel()).Code);
        var reloaded = manager.Reload();
        Assert.Equal(1, announced);                       // the host forwards this to every open editor
        svc.ConfigureInterpreter(reloaded);
        Assert.True(svc.Diagnostics(uri).Any(d => d.Message.Contains("missing")), "demopkg resolves from site-packages after the install");
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Code == "unresolved-import"), "the import is fine once the package is installed");
        Assert.Contains("int", svc.Hover(uri, new PyMagic.Server.Position(1, 9))!.Contents.Value);

        Assert.Equal(0, p.Module(python, "pip", "uninstall", "-y", "--disable-pip-version-check", "demopkg").Code);
        svc.ConfigureInterpreter(manager.Reload());
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Message.Contains("missing")), "gone again after the uninstall");
        Assert.True(svc.Diagnostics(uri).Any(d => d.Code == "unresolved-import"), "and reported again after the uninstall");
    }

    [Fact]
    public void WritabilityDistinguishesEnvironmentsFromSystemInstalls()
    {
        if (RealPython == null) return;
        using var p = new Project();
        string python = p.Venv(withPip: false);
        var venv = InterpreterInspector.Inspect(python, "venv", ".venv").Interpreter!;
        Assert.True(PackageManager.CanInstallGlobally(venv), "a virtual environment is always the user's to change");
    }
}
