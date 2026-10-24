using PyMagic.Server;

namespace PyMagic.Tests;

public class CallCodeTests
{
    const string Code = "def f(a):\n    return a\n\n\nf(1, 2)\nprint(undefined_name)\n";

    static (LanguageService Svc, string Uri) Make()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_calls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        File.WriteAllText(Path.Combine(dir, "helper.py"), "VALUE = 1\n");
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "app.py"));
        svc.Open(uri, Code, 1);
        return (svc, uri);
    }

    [Fact]
    public void CallFindingsCarryCodes()
    {
        var (svc, uri) = Make();
        var found = svc.Diagnostics(uri);
        Assert.True(found.Any(d => d.Code == "call-arguments"), "too many arguments");
    }

    [Fact]
    public void ACallFindingCanBeSwitchedOffOnItsOwn()
    {
        var (svc, uri) = Make();
        svc.SetInspection("call-arguments", "off");
        var found = svc.Diagnostics(uri);
        Assert.True(!found.Any(d => d.Code == "call-arguments"), "switched off");
        Assert.True(found.Any(d => d.Code == "unresolved-reference"), "the other stays");
        Assert.True(svc.Inspections().Any(i => i.Code == "argument-type") && svc.Inspections().Any(i => i.Code == "not-callable"));
    }
}
