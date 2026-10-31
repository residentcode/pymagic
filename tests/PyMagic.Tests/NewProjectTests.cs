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
    public void ADjangoProjectWithItsTemplateLanguageFolderFirstAppAndAdmin()
    {
        var files = NewProject.Files("django", "Shop", true, new DjangoProjectOptions("jinja2", "site/tpl", "Blog Posts", Admin: false));
        Assert.Equal("manage.py,shop/__init__.py,shop/settings.py,shop/urls.py,shop/wsgi.py,shop/asgi.py,site/tpl/.gitkeep," +
            "blog_posts/__init__.py,blog_posts/apps.py,blog_posts/models.py,blog_posts/views.py,blog_posts/tests.py,blog_posts/migrations/__init__.py,requirements.txt", string.Join(",", files.Keys));
        string settings = files["shop/settings.py"];
        Assert.True(settings.Contains("\"BACKEND\": \"django.template.backends.jinja2.Jinja2\",\n        \"DIRS\": [BASE_DIR / \"site/tpl\"]"), settings);
        Assert.True(settings.Contains("django.template.backends.django.DjangoTemplates\",\n        \"DIRS\": []"));
        Assert.True(!settings.Contains("django.contrib.admin") && settings.Contains("    \"blog_posts\",\n]"));
        Assert.True(!files["shop/urls.py"].Contains("admin"));
        Assert.True(files["blog_posts/apps.py"].Contains("class BlogPostsConfig(AppConfig):"));
        Assert.Equal("django\njinja2\n", files["requirements.txt"]);
        // the defaults are what startproject makes, with the admin
        var plain = NewProject.Files("django", "Shop", true);
        Assert.True(plain["shop/settings.py"].Contains("\"django.contrib.admin\"") && plain["shop/urls.py"].Contains("admin.site.urls") && plain.ContainsKey("templates/.gitkeep"));
        try { NewProject.Files("django", "shop", true, new DjangoProjectOptions(AppName: "shop")); throw new AssertionException("no error"); }
        catch (ServiceException) { }
        foreach (var (path, text) in files)
            if (path.EndsWith(".py")) Assert.Equal(0, Parser.Parse(text).Diagnostics.Count);

        // with Django on the computer, Django's own check passes on what was made
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_DJANGO") is not { Length: > 0 } python) return;
        string folder = TempFolder();
        try
        {
            NewProject.Create(new NewProjectRequest(folder, "django", false, false, "none", null, null, null, new DjangoProjectOptions("django", "templates", "blog", Admin: false)));
            var psi = new ProcessStartInfo(python) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("manage.py");
            psi.ArgumentList.Add("check");
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            Assert.True(p.ExitCode == 0 && output.Contains("no issues"), output);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
