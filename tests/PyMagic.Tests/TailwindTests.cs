using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Whether a project uses Tailwind CSS (the Tailwind CSS plugin completes its classes only there), and the stylesheet whose @theme counts.</summary>
public class TailwindTests
{
    static string Project(params (string Rel, string Text)[] files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_tw_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void AStylesheetThatImportsTailwindIsFoundWithItsTheme()
    {
        var use = Tailwind.Detect(Project(("app.py", "print()"), ("static/css/input.css", "@import \"tailwindcss\";\n@theme { --color-brand: #3366ff; }")));
        Assert.True(use.Used);
        Assert.Equal("static/css/input.css", use.Css);
        // version 3's @tailwind
        Assert.Equal("src/main.css", Tailwind.Detect(Project(("src/main.css", "@tailwind base;\n@tailwind utilities;"))).Css);
    }

    [Fact]
    public void PackageJsonAConfigOrTheCdnInATemplateMeanTailwind()
    {
        Assert.True(Tailwind.Detect(Project(("package.json", "{ \"devDependencies\": { \"tailwindcss\": \"^4.1.0\" } }"))).Used);
        Assert.True(Tailwind.Detect(Project(("tailwind.config.js", "module.exports = {}"))).Used);
        var cdn = Tailwind.Detect(Project(("templates/base.html", "<script src=\"https://cdn.tailwindcss.com\"></script>")));
        Assert.True(cdn.Used);
        Assert.Equal("templates/base.html", cdn.Why);
        Assert.True(cdn.Css == null);
        Assert.True(Tailwind.Detect(Project(("index.html", "<script src=\"https://cdn.jsdelivr.net/npm/@tailwindcss/browser@4\"></script>"))).Used);
    }

    [Fact]
    public void AProjectWithoutTailwindIsNotTakenForOne()
    {
        Assert.True(!Tailwind.Detect(Project(("app.py", "print('tailwind')"), ("static/site.css", "body { margin: 0 }"), ("package.json", "{ \"dependencies\": { \"bootstrap\": \"5\" } }"))).Used);
        // a library's copy in node_modules is not the project's
        Assert.True(!Tailwind.Detect(Project(("node_modules/x/a.css", "@import \"tailwindcss\";"))).Used);
    }
}
