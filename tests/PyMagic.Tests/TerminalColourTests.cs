using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The terminal asks programs for colours (item 135), unless the user said NO_COLOR.</summary>
public class TerminalColourTests
{
    [Fact]
    public void ColoursAreAskedFor()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "x" };
        TerminalSession.AskForColours(env);
        Assert.Equal("1", env["FORCE_COLOR"]);
        Assert.Equal("1", env["PY_COLORS"]);
        Assert.Equal("1", env["PYTHON_COLORS"]);
        Assert.Equal("color.ui", env["GIT_CONFIG_KEY_0"]);
    }

    [Fact]
    public void NoColorAndTheUsersOwnValuesWin()
    {
        var none = new Dictionary<string, string?> { ["NO_COLOR"] = "1" };
        TerminalSession.AskForColours(none);
        Assert.Equal(1, none.Count);

        var own = new Dictionary<string, string?> { ["force_color"] = "0", ["GIT_CONFIG_COUNT"] = "2" };
        TerminalSession.AskForColours(own);
        Assert.Equal("0", own["force_color"]);
        Assert.True(!own.ContainsKey("FORCE_COLOR"));
        Assert.Equal("2", own["GIT_CONFIG_COUNT"]);
    }
}
