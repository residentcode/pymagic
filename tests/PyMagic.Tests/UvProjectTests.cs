using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>A uv project: told apart, its dependencies read, and its packages added and removed with uv add / uv remove; New Project making one.</summary>
public class UvProjectTests
{
    sealed class Temp : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_uvp_" + Guid.NewGuid().ToString("N"));
        public Temp() { Directory.CreateDirectory(Root); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        /// <summary>A .venv laid out as on Windows; <paramref name="byUv"/> writes the line uv puts in pyvenv.cfg.</summary>
        public string Venv(bool byUv)
        {
            string venv = Path.Combine(Root, ".venv");
            Directory.CreateDirectory(Path.Combine(venv, "Scripts"));
            File.WriteAllText(Path.Combine(venv, "Scripts", "python.exe"), "x");
            File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = C:\\Python313\n" + (byUv ? "uv = 0.12.3\n" : ""));
            return Path.Combine(venv, "Scripts", "python.exe");
        }
    }

    const string Pyproject = """
        [project]
        name = "shop"
        version = "0.1.0"
        requires-python = ">=3.13"
        dependencies = [
            "django>=6.1.2",
            "Jinja2==3.1.6",
            'uvicorn[standard]>=0.30',
        ]

        [dependency-groups]
        dev = ["pytest"]
        """;

    [Fact]
    public void TheDirectDependenciesAreReadFromPyproject()
    {
        Assert.Equal("django,jinja2,uvicorn", string.Join(",", UvProject.DependenciesIn(Pyproject)));
        A.Empty(UvProject.DependenciesIn("[project]\nname = \"x\"\n"));
        A.Empty(UvProject.DependenciesIn("[tool.poetry]\ndependencies = [\"x\"]\n"));
        Assert.Equal("requests", string.Join(",", UvProject.DependenciesIn("[project]\nname = \"x\"\ndependencies = [\"requests\"]\n")));
    }

    [Fact]
    public void PackagesAreAddedAndRemovedWithUv()
    {
        Assert.Equal("add --no-progress six requests>=2", string.Join(" ", UvProject.AddArgs(new[] { "six", "requests>=2" }, false)));
        Assert.Equal("add --no-progress --upgrade-package six six", string.Join(" ", UvProject.AddArgs(new[] { "six" }, true)));
        var deps = UvProject.DependenciesIn(Pyproject);
        Assert.Equal("remove --no-progress Django", string.Join(" ", UvProject.RemoveArgs(new[] { "Django" }, deps)!));
        A.Null(UvProject.RemoveArgs(new[] { "asgiref" }, deps));          // pulled in by Django: uv pip removes it
        A.Null(UvProject.RemoveArgs(new[] { "django", "asgiref" }, deps));
        Assert.Equal("django,jinja2", string.Join(",", UvProject.PackagesOf("django", true)));
        Assert.Equal("fastapi,uvicorn[standard]", string.Join(",", UvProject.PackagesOf("fastapi", false)));
        A.Empty(UvProject.PackagesOf("python", false));
    }

    [Fact]
    public void AUvProjectIsOneUvKeepsUsedWithItsOwnVenv()
    {
        using var t = new Temp();
        string python = t.Venv(byUv: true);
        A.Null(UvProject.For(t.Root, python, _ => "uv"));                                    // no pyproject.toml
        // a pyproject.toml uv does not keep (poetry's, setuptools'), even beside a .venv uv made: its dependencies are not uv's to change
        File.WriteAllText(Path.Combine(t.Root, "pyproject.toml"), Pyproject);
        A.Null(UvProject.For(t.Root, python, _ => "uv"));
        Assert.True(!UvProject.IsUvProject(t.Root));
        // uv's own table (as New Project writes it), or uv.lock
        File.WriteAllText(Path.Combine(t.Root, "pyproject.toml"), Pyproject + "\n[tool.uv]\npackage = false\n");
        Assert.Equal("uv", UvProject.For(t.Root, python, _ => "uv"));
        File.WriteAllText(Path.Combine(t.Root, "pyproject.toml"), Pyproject);
        File.WriteAllText(Path.Combine(t.Root, "uv.lock"), "version = 1\n");
        Assert.True(UvProject.IsUvProject(t.Root));
        Assert.Equal("uv", UvProject.For(t.Root, python, _ => "uv"));
        A.Null(UvProject.For(t.Root, Path.Combine(Path.GetTempPath(), "other", "Scripts", "python.exe"), _ => "uv"));   // another interpreter
        A.Null(UvProject.For(t.Root, python, _ => null));                                   // uv is not installed
    }

    static string TempProject() => Path.Combine(Path.GetTempPath(), "pm_uvnew_" + Guid.NewGuid().ToString("N"), "My Site");

    /// <summary>
    /// With uv installed: New Project with uv makes a uv project (uv init, uv add of the kind's packages), no requirements.txt. Installing left out, the
    /// packages are written into pyproject.toml only (uv add --frozen, no download); with PYMAGIC_TEST_UV_INSTALL=1 they are installed (a download from
    /// PyPI), uv.lock pins them, and Django's own startproject writes the project.
    /// </summary>
    [Fact]
    public void NewProjectWithUvMakesAUvProject()
    {
        if (LockFiles.Find("uv") is not { } uv) return;
        string folder = TempProject();
        try
        {
            var made = NewProject.Create(new NewProjectRequest(folder, "django", false, false, "uv", null, uv, null, new DjangoProjectOptions(AppName: "blog"), InstallPackages: false));
            string pyproject = File.ReadAllText(Path.Combine(folder, "pyproject.toml"));
            Assert.Equal("django", string.Join(",", UvProject.DependenciesIn(pyproject)));
            Assert.True(pyproject.Contains("name = \"my-site\"") || pyproject.Contains("name = \"my_site\""), pyproject);
            Assert.True(!File.Exists(Path.Combine(folder, "requirements.txt")));
            Assert.True(made.Files.Contains("pyproject.toml") && !made.Files.Contains("requirements.txt"), string.Join(",", made.Files));
            Assert.True(made.Notes.Any(n => n.Contains("uv sync")), string.Join(" | ", made.Notes));
            Assert.True(pyproject.Contains("[tool.uv]\npackage = false"), pyproject);
            Assert.Equal(uv, UvProject.For(folder, made.Interpreter!));
            // without Django installed, Django's own template wrote the project
            Assert.True(File.ReadAllText(Path.Combine(folder, "my_site", "settings.py")).Contains("'blog',\n]"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_UV_INSTALL") != "1") return;
        folder = TempProject();
        try
        {
            var made = NewProject.Create(new NewProjectRequest(folder, "django", false, false, "uv", null, uv, null, new DjangoProjectOptions(AppName: "blog")));
            Assert.True(File.Exists(Path.Combine(folder, "uv.lock")), string.Join(" | ", made.Notes));
            Assert.True(UvProject.IsUvProject(folder));
            string version = DjangoTemplates.VersionOf(made.Interpreter!) ?? throw new AssertionException("Django is not in .venv: " + string.Join(" | ", made.Notes));
            Assert.True(File.ReadAllText(Path.Combine(folder, "my_site", "settings.py")).Contains($"using Django {version}."));
            Assert.True(made.Notes.Any(n => n.Contains("uv added django")), string.Join(" | ", made.Notes));
            // the panel's commands, run as it runs them: a package added, then removed again, pyproject.toml following
            static (int, string) Uv(string uvExe, string cwd, IEnumerable<string> args)
            {
                var psi = new ProcessStartInfo(uvExe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(300000);
                return (p.ExitCode, o);
            }
            var (added, addOut) = Uv(uv, folder, UvProject.AddArgs(new[] { "six" }, false));
            Assert.True(added == 0, addOut);
            Assert.Equal("django,six", string.Join(",", UvProject.Dependencies(folder)));
            var (removed, removeOut) = Uv(uv, folder, UvProject.RemoveArgs(new[] { "six" }, UvProject.Dependencies(folder))!);
            Assert.True(removed == 0, removeOut);
            Assert.Equal("django", string.Join(",", UvProject.Dependencies(folder)));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
