using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Core.Syntax;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The New Project dialog's projects (NewProject.cs).</summary>
public class NewProjectTests
{
    static string TempFolder() => Path.Combine(Path.GetTempPath(), "pm_new_" + Guid.NewGuid().ToString("N"), "My Site");

    [Fact]
    public void EachKindHasItsStartingFilesAndTheyAreValidPython()
    {
        Assert.Equal("main.py", string.Join(",", NewProject.Files("python", "demo", true).Keys));
        Assert.Equal(0, NewProject.Files("python", "demo", false).Count);
        Assert.Equal("main.py,test_main.http,requirements.txt", string.Join(",", NewProject.Files("fastapi", "demo", true).Keys));
        Assert.Equal("app.py,templates/.gitkeep,static/.gitkeep,requirements.txt", string.Join(",", NewProject.Files("flask", "demo", true).Keys));
        var django = NewProject.Files("django", "My Site", true);
        Assert.Equal("manage.py,my_site/__init__.py,my_site/settings.py,my_site/urls.py,my_site/wsgi.py,my_site/asgi.py,templates/.gitkeep,requirements.txt", string.Join(",", django.Keys));
        Assert.True(django["manage.py"].Contains("\"my_site.settings\""));
        Assert.True(django["my_site/settings.py"].Contains("ROOT_URLCONF = \"my_site.urls\""));
        var nb = JsonNode.Parse(NewProject.Files("jupyter", "demo", true)["notebook.ipynb"])!;
        Assert.Equal(4, nb["nbformat"]!.GetValue<int>());
        foreach (var kind in NewProject.Kinds)
            foreach (var (path, text) in NewProject.Files(kind, "x", true))
                if (path.EndsWith(".py")) Assert.Equal(0, Parser.Parse(text).Diagnostics.Count);
        Assert.Equal("_2024_report", NewProject.ModuleName("2024 Report"));
        try { NewProject.Files("rails", "x", true); throw new AssertionException("no error"); }
        catch (ServiceException) { }
    }

    [Fact]
    public void MakesTheFolderWithGitAndRefusesOneThatIsNotEmpty()
    {
        string folder = TempFolder();
        try
        {
            var r = NewProject.Create(new NewProjectRequest(folder, "flask", true, true, "none", null, null, null));
            Assert.Equal(".gitignore,app.py,requirements.txt,static/.gitkeep,templates/.gitkeep", string.Join(",", r.Files));
            Assert.True(File.Exists(Path.Combine(folder, "app.py")) && Directory.Exists(Path.Combine(folder, "templates")));
            Assert.True(Directory.Exists(Path.Combine(folder, ".git")), "git init ran");
            Assert.True(r.Interpreter == null);
            try { NewProject.Create(new NewProjectRequest(folder, "python", false, true, "none", null, null, null)); throw new AssertionException("no error"); }
            catch (ServiceException e) { Assert.Equal("The folder is not empty.", e.Message); }
            try { NewProject.Create(new NewProjectRequest("relative/path", "python", false, true, "none", null, null, null)); throw new AssertionException("no error"); }
            catch (ServiceException e) { Assert.True(e.Message.Contains("full path")); }
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void MakesAProjectVenvAndRemembersItsInterpreter()
    {
        string python = Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";
        try { using var p = Process.Start(new ProcessStartInfo(python, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }); if (p == null || !p.WaitForExit(20000) || p.ExitCode != 0) return; }
        catch (System.ComponentModel.Win32Exception) { return; }
        string? exe = null;
        try
        {
            using var w = Process.Start(new ProcessStartInfo(python, "-c \"import sys; print(sys.executable)\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            exe = w.StandardOutput.ReadToEnd().Trim();
        }
        catch (System.ComponentModel.Win32Exception) { return; }
        string folder = TempFolder();
        try
        {
            var r = NewProject.Create(new NewProjectRequest(folder, "python", false, true, "venv", exe, null, null));
            Assert.True(r.Interpreter != null && File.Exists(r.Interpreter) && r.Interpreter.Contains(".venv"), r.Interpreter ?? "none");
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ".pymagic", "settings.json")))!;
            Assert.Equal(r.Interpreter, settings["interpreter"]!.GetValue<string>());
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
