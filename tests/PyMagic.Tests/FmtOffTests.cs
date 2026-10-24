using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary># fmt: off / # fmt: on regions and # fmt: skip lines.</summary>
public class FmtOffTests
{
    static string Fmt(string code) => PythonFormatter.Format(code) ?? throw new AssertionException("declined: " + PythonFormatter.LastMismatch);

    [Fact]
    public void ARegionStaysAsWritten()
    {
        string code = "a=1\n# fmt: off\nmatrix = [1,0,0,\n          0,1,0,\n          0,0,1]\nb   =   2\n# fmt: on\nc=3\n";
        Assert.Equal("a = 1\n# fmt: off\nmatrix = [1,0,0,\n          0,1,0,\n          0,0,1]\nb   =   2\n# fmt: on\nc = 3\n", Fmt(code));
    }

    [Fact]
    public void ARegionWithoutOnRunsToTheEnd()
    {
        string code = "x=1\n# fmt: off\ny   =   2\nz=3\n";
        Assert.Equal("x = 1\n# fmt: off\ny   =   2\nz=3\n", Fmt(code));
    }

    [Fact]
    public void ASkippedLineIsLeftAlone()
    {
        string code = "a=1\nb   =   2  # fmt: skip\nc=3\n";
        Assert.Equal("a = 1\nb   =   2  # fmt: skip\nc = 3\n", Fmt(code));
    }

    [Fact]
    public void ARegionInsideAFunctionKeepsItsIndentation()
    {
        string code = "def f():\n    x=1\n    # fmt: off\n    table = {'a':1,\n             'b':2}\n    # fmt: on\n    y=2\n    return x+y\n";
        string result = Fmt(code);
        Assert.Contains("    table = {'a':1,\n             'b':2}\n", result);
        Assert.Contains("    x = 1\n", result);
        Assert.Contains("    return x + y\n", result);
    }

    [Fact]
    public void BlankLinesInsideTheRegionAreKept()
    {
        string code = "# fmt: off\na=1\n\n\n\nb=2\n# fmt: on\n";
        Assert.Equal(code, Fmt(code));
    }

    [Fact]
    public void FormattingAgainChangesNothing()
    {
        string code = "a=1\n# fmt: off\nb  =  2\n# fmt: on\nc=3\n";
        string once = Fmt(code);
        Assert.Equal(once, Fmt(once));
    }

    [Fact]
    public void ASelectionDoesNotTouchTheRegion()
    {
        string code = "a=1\n# fmt: off\nb  =  2\n# fmt: on\nc=3\n";
        string? result = PythonFormatter.FormatRange(code, 0, code.Length);
        Assert.Equal("a = 1\n# fmt: off\nb  =  2\n# fmt: on\nc = 3\n", result);
    }
}
