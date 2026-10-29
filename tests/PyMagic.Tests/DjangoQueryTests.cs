using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Field names in queries through a related manager, in `Prefetch(...)` and in `OuterRef(...)` (item 154), with the real Django (skipped where it is
/// not installed). The app is in Projects/django-queries (see its README).
/// </summary>
public class DjangoQueryTests
{
    static List<string> Found()
    {
        string from = Path.Combine(AppContext.BaseDirectory, "Projects", "django-queries");
        string dir = Path.Combine(Path.GetTempPath(), "pm_djqueries_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shop"));
        File.WriteAllText(Path.Combine(dir, "shop", "__init__.py"), "");
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(RealDjangoTests.Site!);
        foreach (var name in new[] { "models.py", "views.py" })
        {
            string path = Path.Combine(dir, "shop", name);
            File.Copy(Path.Combine(from, name), path);
            svc.Open(LanguageService.UriOfPath(path), File.ReadAllText(path), 1);
        }
        return svc.Diagnostics(LanguageService.UriOfPath(Path.Combine(dir, "shop", "views.py"))).Where(d => d.Code == "django-field")
            .OrderBy(d => d.Range.Start.Line).ThenBy(d => d.Range.Start.Character).Select(d => $"{d.Range.Start.Line + 1}: {d.Message}").ToList();
    }

    [Fact]
    public void RelatedManagersPrefetchAndOuterRefAreChecked()
    {
        if (RealDjangoTests.Site == null) return;
        var found = Found();
        Assert.Equal(string.Join("\n", new[]
        {
            // author.book_set.filter(titel=...): the related manager's model is Book
            "8: Cannot resolve keyword 'titel' into a field of Book. Choices are: pk, title, author, id",
            // Prefetch("bok_set"): prefetch walks attributes, so book_set is right and bok_set is not
            "11: Cannot find 'bok_set' on Author to prefetch: prefetch_related follows the relations of the model (book_set, a related_name), and Django raises AttributeError. Choices are: book_set",
            "11: Cannot resolve keyword 'titel' into a field of Book. Choices are: pk, title, author, id",
            // OuterRef("pkk") of the inner query in `newest`: Subquery(newest) is used in Author's query
            "13: Cannot resolve 'pkk' into a field of Author, the model of the outer query OuterRef reads from. Choices are: pk, name, id, book",
        }), string.Join("\n", found));
    }
}
