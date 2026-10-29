using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// A Django app's models against its migrations (item 150), with the real Django (skipped where it is not installed). The app is in
/// Projects/django-migrations (see its README): Django 6.1's `makemigrations --dry-run` found in it the four changes expected.txt names.
/// </summary>
public class DjangoMigrationTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Projects", "django-migrations", name));

    /// <summary>The diagnostics of shop/models.py, written from <paramref name="models"/>, with these migrations: "line: message".</summary>
    static List<string> Found(string models, params string[] migrations)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_migrations_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shop", "migrations"));
        File.WriteAllText(Path.Combine(dir, "shop", "__init__.py"), "");
        File.WriteAllText(Path.Combine(dir, "shop", "migrations", "__init__.py"), "");
        foreach (var name in migrations) File.WriteAllText(Path.Combine(dir, "shop", "migrations", name), Fixture(name));
        string path = Path.Combine(dir, "shop", "models.py");
        string text = Fixture(models);
        File.WriteAllText(path, text);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(RealDjangoTests.Site!);
        string uri = LanguageService.UriOfPath(path);
        svc.Open(uri, text, 1);
        return svc.Diagnostics(uri).Where(d => d.Code == "django-migration").OrderBy(d => d.Range.Start.Line).Select(d => $"{d.Range.Start.Line + 1}: {d.Message}").ToList();
    }

    static readonly string[] Both = { "0001_initial.py", "0002_book_rating.py" };

    [Fact]
    public void WhatMakemigrationsWouldWriteIsReported()
    {
        if (RealDjangoTests.Site == null) return;
        Assert.Equal(Fixture("expected.txt").TrimEnd('\n'), string.Join("\n", Found("models.py", Both)));
    }

    [Fact]
    public void ModelsTheMigrationsMatchAreNotReported()
    {
        if (RealDjangoTests.Site == null) return;
        Assert.Equal("", string.Join("\n", Found("models_matching.py", Both)));
        // an app without migrations is not judged
        Assert.Equal("", string.Join("\n", Found("models.py")));
    }

    [Fact]
    public void ASquashedMigrationStandsInForTheOnesItReplaces()
    {
        if (RealDjangoTests.Site == null) return;
        var found = Found("models.py", Both.Append("0001_squashed_0002_book_rating.py").ToArray());
        // what the squashed one has, the models have; only Review is new
        Assert.Equal(1, found.Count);
        Assert.True(found[0].StartsWith("16: Review is in no migration of shop", StringComparison.Ordinal), found[0]);
    }
}
