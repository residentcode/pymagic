using PyMagic.Server;

namespace PyMagic.Tests;

public class InspectionProfileTests
{
    const string Code =
        "import os\n\n\ndef Bad_Name():\n    leftover = 1\n    return 2\n\n\ndef guard():\n    try:\n        pass\n    except:\n        pass\n";

    static (LanguageService Svc, string Dir, string Uri) Make(string code = Code)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_prof_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "app.py"));
        svc.Open(uri, code, 1);
        return (svc, dir, uri);
    }

    static LspDiagnostic? Find(LanguageService svc, string uri, string code) => svc.Diagnostics(uri).FirstOrDefault(d => d.Code == code);

    [Fact]
    public void WithoutAProfileTheDefaultsApply()
    {
        var (svc, _, uri) = Make();
        Assert.Equal(Severity.Information, Find(svc, uri, "naming-function")!.Severity);
        Assert.Equal(Severity.Warning, Find(svc, uri, "bare-except")!.Severity);
        Assert.True(Find(svc, uri, "unused-variable")!.Tags?.Contains(DiagnosticTag.Unnecessary) == true);
        Assert.True(svc.Inspections().All(i => i.Level == i.Default));
    }

    [Fact]
    public void AnInspectionCanBeTurnedOff()
    {
        var (svc, dir, uri) = Make();
        svc.SetInspection("naming-function", "off");
        Assert.True(Find(svc, uri, "naming-function") == null);
        Assert.Equal("off", svc.Inspections().First(i => i.Code == "naming-function").Level);
        Assert.Contains("naming-function", File.ReadAllText(Path.Combine(dir, ".pymagic", "inspections.json")));
        Assert.True(Find(svc, uri, "bare-except") != null, "the others stay");
    }

    [Fact]
    public void ALevelChangesTheSeverity()
    {
        var (svc, _, uri) = Make();
        svc.SetInspection("bare-except", "error");
        Assert.Equal(Severity.Error, Find(svc, uri, "bare-except")!.Severity);
        svc.SetInspection("unused-variable", "warning");
        var d = Find(svc, uri, "unused-variable")!;
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.True(d.Tags == null, "no longer greyed out");
    }

    [Fact]
    public void TheDefaultAgainRemovesTheEntry()
    {
        var (svc, dir, uri) = Make();
        svc.SetInspection("bare-except", "error");
        svc.SetInspection("bare-except", null);
        Assert.Equal(Severity.Warning, Find(svc, uri, "bare-except")!.Severity);
        Assert.True(!File.Exists(Path.Combine(dir, ".pymagic", "inspections.json")), "an empty profile is no file");
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var (svc, _, uri) = Make();
        svc.SetInspection("bare-except", "off");
        svc.SetInspection("naming-function", "off");
        svc.ResetInspections();
        Assert.True(Find(svc, uri, "bare-except") != null && Find(svc, uri, "naming-function") != null);
    }

    [Fact]
    public void AFileWrittenByHandIsRead()
    {
        var (svc, dir, uri) = Make();
        Directory.CreateDirectory(Path.Combine(dir, ".pymagic"));
        File.WriteAllText(Path.Combine(dir, ".pymagic", "inspections.json"), "{ \"bare-except\": \"OFF\", \"naming-function\": \"nonsense\", // a comment\n }");
        Assert.True(Find(svc, uri, "bare-except") == null);
        Assert.True(Find(svc, uri, "naming-function") != null, "an unknown level is ignored");
    }

    [Fact]
    public void TheFixOfAnInspectionThatIsOffIsGone()
    {
        var (svc, _, uri) = Make("import os\n");
        var range = new LineMap("import os\n").RangeOf(0, 9);
        Assert.True(svc.CodeActions(uri, range).Any(a => a.Title.StartsWith("Remove unused import")));
        svc.SetInspection("unused-import", "off");
        Assert.True(!svc.CodeActions(uri, range).Any(a => a.Title.StartsWith("Remove unused import")));
    }

    [Fact]
    public void WrongInputIsRefused()
    {
        var (svc, _, _) = Make();
        Assert.Throws<ServiceException>(() => svc.SetInspection("no-such-thing", "off"));
        Assert.Throws<ServiceException>(() => svc.SetInspection("bare-except", "loud"));
    }

    [Fact]
    public void TheCatalogCoversWhatTheEditorReports()
    {
        var codes = new LanguageService().Inspections().Select(i => i.Code).ToHashSet();
        foreach (var code in new[] { "unresolved-reference", "unused-import", "unused-variable", "naming-class", "fixture-not-found", "django-url-name", "route-path-parameter", "template-structure", "missing-super-init", "redeclared" })
            Assert.True(codes.Contains(code), code);
        Assert.Equal(codes.Count, new LanguageService().Inspections().Count);
    }

    [Fact]
    public void TemplateInspectionsFollowTheProfileToo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_prof_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "templates"));
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        svc.Open(LanguageService.UriOfPath(Path.Combine(dir, "settings.py")), "from django.conf import settings\n", 1);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "templates", "a.html"));
        svc.Open(uri, "{% for a in b %}\n", 1);
        Assert.Equal(Severity.Error, svc.Diagnostics(uri).First(d => d.Code == "template-structure").Severity);
        svc.SetInspection("template-structure", "warning");
        Assert.Equal(Severity.Warning, svc.Diagnostics(uri).First(d => d.Code == "template-structure").Severity);
        svc.SetInspection("template-structure", "off");
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Code == "template-structure"));
    }
}
