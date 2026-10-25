using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Finding manage.py, the apps with their migrations and the management commands in a folder.</summary>
public class DjangoProjectTests
{
    static string Project(Dictionary<string, string> files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_dj_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    const string Manage = "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'mysite.settings')\nfrom django.core.management import execute_from_command_line\n";

    [Fact]
    public void AFolderWithoutManagePyIsNotADjangoProject()
    {
        string root = Project(new() { ["app.py"] = "print(1)\n" });
        Assert.True(!DjangoProject.Scan(root).Found);
    }

    [Fact]
    public void ManagePyThatDoesNotMentionDjangoIsIgnored()
    {
        string root = Project(new() { ["manage.py"] = "print('manage')\n" });
        Assert.True(!DjangoProject.Scan(root).Found);
    }

    [Fact]
    public void ManagePySettingsAndAppsAreFound()
    {
        string root = Project(new()
        {
            ["manage.py"] = Manage,
            ["mysite/__init__.py"] = "",
            ["mysite/settings.py"] = "",
            ["shop/__init__.py"] = "",
            ["shop/apps.py"] = "from django.apps import AppConfig\nclass ShopConfig(AppConfig): pass\n",
            ["shop/models.py"] = "",
            ["shop/migrations/__init__.py"] = "",
            ["shop/migrations/0002_book_price.py"] = "",
            ["shop/migrations/0001_initial.py"] = "",
            ["shop/management/commands/seed.py"] = "",
            ["shop/management/commands/_helpers.py"] = "",
            ["blog/__init__.py"] = "",
            ["blog/models/__init__.py"] = "",
            ["utils/__init__.py"] = "",
            ["utils/helpers.py"] = "",
        });
        var project = DjangoProject.Scan(root);
        Assert.True(project.Found);
        Assert.Equal("manage.py", project.Manage);
        Assert.Equal("mysite.settings", project.SettingsModule);
        Assert.Equal("blog,shop", string.Join(",", project.Apps.Select(a => a.Name)));
        var shop = project.Apps.Single(a => a.Name == "shop");
        Assert.True(shop.HasModels);
        Assert.Equal("0001_initial,0002_book_price", string.Join(",", shop.Migrations.Select(m => m.Name)));
        Assert.Equal("shop/migrations/0001_initial.py", shop.Migrations[0].Path);
        Assert.Equal("seed", string.Join(",", shop.Commands));
        Assert.True(project.Apps.Single(a => a.Name == "blog").HasModels);
    }

    [Fact]
    public void ManagePyInASubfolderIsFoundAndPathsStayRelativeToTheProject()
    {
        string root = Project(new()
        {
            ["backend/manage.py"] = Manage,
            ["backend/core/__init__.py"] = "",
            ["backend/core/models.py"] = "",
            [".venv/Lib/manage.py"] = Manage,
        });
        var project = DjangoProject.Scan(root);
        Assert.Equal("backend/manage.py", project.Manage);
        Assert.Equal("backend/core", project.Apps.Single().Path);
    }

    [Fact]
    public void ManagePyInAVirtualEnvironmentIsNotTheProjects()
    {
        string root = Project(new() { [".venv/manage.py"] = Manage, ["venv/manage.py"] = Manage });
        Assert.True(!DjangoProject.Scan(root).Found);
    }
}
