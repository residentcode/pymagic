using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Conda environments: told apart, found, made in the project, and their packages listed, installed, updated and removed with conda (pip for what conda cannot take).</summary>
public class CondaTests
{
    sealed class Temp : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_conda_" + Guid.NewGuid().ToString("N"));
        public Temp() { Directory.CreateDirectory(Root); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        /// <summary>A folder laid out like a conda environment on Windows (python.exe at the top, conda-meta beside it).</summary>
        public string Env(string name)
        {
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.Combine(dir, "conda-meta"));
            File.WriteAllText(Path.Combine(dir, "python.exe"), "x");
            return dir;
        }
    }

    const string ListJson = """
        [
          {"base_url": "https://conda.anaconda.org/conda-forge", "channel": "conda-forge", "name": "six", "version": "1.16.0"},
          {"base_url": "https://conda.anaconda.org/pypi", "channel": "pypi", "name": "pmlocal", "version": "1.0"},
          {"channel": "conda-forge", "name": "Python", "version": "3.13.1"}
        ]
        """;

    [Fact]
    public void ACondaEnvironmentIsToldByItsCondaMetaFolder()
    {
        using var t = new Temp();
        string env = t.Env(".conda");
        Assert.True(Conda.IsCondaEnv(env));
        Assert.Equal(env, Conda.PrefixOf(Path.Combine(env, "python.exe")));
        Directory.CreateDirectory(Path.Combine(env, "bin"));
        Assert.Equal(env, Conda.PrefixOf(Path.Combine(env, "bin", "python")));
        A.Null(Conda.PrefixOf(Path.Combine(t.Root, "python.exe")));
        Assert.Equal(("/c/conda", env), Conda.For(Path.Combine(env, "python.exe"), () => "/c/conda")!.Value);
        A.Null(Conda.For(Path.Combine(env, "python.exe"), () => null));
    }

    [Fact]
    public void WhatCondaAnswersIsRead()
    {
        var list = Conda.ParseList(ListJson);
        Assert.Equal("pmlocal 1.0 pypi|Python 3.13.1 conda-forge|six 1.16.0 conda-forge", string.Join("|", list.Select(p => $"{p.Name} {p.Version} {p.Channel}")));
        var updates = Conda.ParseUpdates("""{"actions": {"LINK": [{"name": "six", "version": "1.17.0", "channel": "conda-forge"}, {"name": "newdep", "version": "1.0"}, {"name": "python", "version": "3.13.1"}]}}""", list);
        Assert.Equal("six 1.16.0 -> 1.17.0", string.Join(",", updates.Select(u => $"{u.Name} {u.Version} -> {u.Latest}")));
        A.Empty(Conda.ParseUpdates("""{"success": true, "message": "All requested packages already installed."}""", list).Select(u => u.Name));
        var versions = Conda.ParseSearch("""{"six": [{"version": "1.9.0"}, {"version": "1.16.0"}, {"version": "1.17.0"}, {"version": "1.17.0"}, {"version": "2.0.0rc1"}]}""", "six");
        Assert.Equal("2.0.0rc1,1.17.0,1.16.0,1.9.0", string.Join(",", versions.Select(v => v.Version)));
        Assert.True(versions[0].Pre);
        A.Empty(Conda.ParseSearch("{}", "six").Select(v => v.Version));
    }

    [Fact]
    public void CondaTakesNamesAndSimpleRequirementsAndLeavesTheRestToPip()
    {
        Assert.Equal("install -p P -y --quiet six numpy>=1.26,<3", string.Join(" ", Conda.InstallArgs("P", new[] { "six", "numpy>=1.26,<3" }, upgrade: false)!));
        Assert.Equal("update -p P -y --quiet six", string.Join(" ", Conda.InstallArgs("P", new[] { "six" }, upgrade: true)!));
        Assert.Equal("install -p P -y --quiet six==1.16.0", string.Join(" ", Conda.InstallArgs("P", new[] { "six==1.16.0" }, upgrade: true)!));
        A.Null(Conda.InstallArgs("P", new[] { "uvicorn[standard]" }, false));
        A.Null(Conda.InstallArgs("P", new[] { "six", "attrs~=23.1" }, false));
        A.Null(Conda.InstallArgs("P", Array.Empty<string>(), false));

        var list = Conda.ParseList(ListJson);
        Assert.Equal("remove -p P -y --quiet six", string.Join(" ", Conda.RemoveArgs("P", new[] { "six" }, list)!));
        A.Null(Conda.RemoveArgs("P", new[] { "pmlocal" }, list));        // pip installed it: pip removes it
        A.Null(Conda.RemoveArgs("P", new[] { "unknown" }, list));
        Assert.True(Conda.MixesPip(new[] { "six", "pmlocal" }, list));
        A.False(Conda.MixesPip(new[] { "six", "python" }, list));

        Assert.Equal("create -p C:\\p\\.conda python=3.13 -y --quiet", string.Join(" ", Conda.CreateArgs("C:\\p\\.conda", "3.13")));
        Assert.Equal("create -p X python -y --quiet", string.Join(" ", Conda.CreateArgs("X", "3.13; rm")));
    }

    [Fact]
    public void CondaEnvironmentsAreFoundInTheProjectAndInCondasList()
    {
        using var t = new Temp();
        string root = Path.Combine(t.Root, "proj");
        Directory.CreateDirectory(root);
        string inProject = Path.Combine(root, ".conda");
        Directory.CreateDirectory(Path.Combine(inProject, "conda-meta"));
        File.WriteAllText(Path.Combine(inProject, "python.exe"), "x");
        string home = Path.Combine(t.Root, "home"), elsewhere = t.Env("elsewhere");
        Directory.CreateDirectory(Path.Combine(home, ".conda"));
        File.WriteAllText(Path.Combine(home, ".conda", "environments.txt"), elsewhere + "\n" + Path.Combine(t.Root, "gone") + "\n\n" + elsewhere + "\n");
        Assert.Equal(elsewhere, string.Join("|", Conda.Registered(home)));

        var found = InterpreterDiscovery.Discover(new DiscoveryEnvironment(root, Array.Empty<string>(), null, null, home, null, null, true, false));
        Assert.True(found.Any(c => c.Path == Path.Combine(inProject, "python.exe") && c.Kind == "conda" && c.Source == ".conda"), string.Join(", ", found));
        Assert.True(found.Any(c => c.Path == Path.Combine(elsewhere, "python.exe") && c.Kind == "conda"), string.Join(", ", found));
    }

    /// <summary>
    /// With conda (PYMAGIC_TEST_CONDA names conda.exe; this downloads Python from the installation's channels the first time): a .conda made in a
    /// project, its packages listed by conda, six installed with conda, its versions searched, a wheel installed by pip and told apart, six removed,
    /// and the environment removed with conda again.
    /// </summary>
    [Fact]
    public void LiveACondaEnvironmentIsMadeAndItsPackagesHandledWithConda()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_CONDA") is not { Length: > 0 } conda || !File.Exists(conda)) return;
        using var t = new Temp();
        string env = Path.Combine(t.Root, ".conda");
        try
        {
            string python = new InterpreterManager(t.Root).CreateCondaEnv(conda, "3.13");
            Assert.Equal(Path.Combine(env, "python.exe"), python);
            Assert.Equal(env, Conda.For(python, () => conda)!.Value.Prefix);
            var list = Conda.List(conda, env)!;
            Assert.True(list.Any(p => p.Name == "python" && p.Version.StartsWith("3.13.")), string.Join(",", list.Select(p => p.Name)));

            var (code, _, error) = Conda.Run(conda, Conda.InstallArgs(env, new[] { "six" }, false)!, 600000);
            Assert.True(code == 0, error);
            Assert.True(Conda.List(conda, env)!.Any(p => p.Name == "six" && p.Channel != "pypi"));
            var (sc, so, _) = Conda.Run(conda, new[] { "search", "six", "--json" }, 120000);
            Assert.True(sc == 0 && Conda.ParseSearch(so, "six").Count > 1);

            string wheel = Path.Combine(t.Root, "pmcondalocal-1.0-py3-none-any.whl");
            using (var zip = System.IO.Compression.ZipFile.Open(wheel, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string entry, string text) { using var w = new StreamWriter(zip.CreateEntry(entry).Open()); w.Write(text); }
                Add("pmcondalocal/__init__.py", "V = 1\n");
                Add("pmcondalocal-1.0.dist-info/METADATA", "Metadata-Version: 2.1\nName: pmcondalocal\nVersion: 1.0\n");
                Add("pmcondalocal-1.0.dist-info/WHEEL", "Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
                Add("pmcondalocal-1.0.dist-info/RECORD", "");
            }
            var pip = PackageManager.Exec(python, new[] { "-m", "pip", "install", "--no-index", "--disable-pip-version-check", wheel }, 120000);
            Assert.True(pip.Code == 0, pip.Error);
            var both = PackageManager.List(python);
            Assert.True(both.Packages.Any(p => p.Name == "pmcondalocal") && both.Packages.Any(p => p.Name == "six"), string.Join(",", both.Packages.Select(p => p.Name)));
            var now = Conda.List(conda, env)!;
            Assert.Equal("pypi", now.Single(p => p.Name == "pmcondalocal").Channel);
            A.Null(Conda.RemoveArgs(env, new[] { "pmcondalocal" }, now));

            var (rc, _, rerr) = Conda.Run(conda, Conda.RemoveArgs(env, new[] { "six" }, now)!, 600000);
            Assert.True(rc == 0, rerr);
            A.False(Conda.List(conda, env)!.Any(p => p.Name == "six"));
        }
        finally
        {
            // conda's own removal, so the environment also leaves conda's list of environments
            if (Directory.Exists(env)) Conda.Run(conda, new[] { "env", "remove", "-p", env, "-y", "--quiet" }, 300000);
        }
    }
}
