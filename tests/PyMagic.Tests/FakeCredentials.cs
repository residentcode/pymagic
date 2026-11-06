namespace PyMagic.Tests;

/// <summary>
/// Made-up credentials for the tests of how PyMagic keeps, hides and passes on passwords and tokens. None is a real one. They are put
/// together when the tests run, so that a secret scanner reading the source does not take them for real ones.
/// </summary>
static class Fake
{
    static string Of(params string[] parts) => string.Concat(parts);

    /// <summary>A password of a database or a server.</summary>
    public static readonly string Password = Of("fake", "-pass", "word");
    /// <summary>Another one, where a test needs two (a database's and its SSH machine's).</summary>
    public static readonly string OtherPassword = Of("fake", "-other-", "pass");
    /// <summary>A token (a Jupyter server's).</summary>
    public static readonly string Token = Of("fake", "-tok", "en");
    /// <summary>A password made of the characters cmd.exe reads as its own (&amp; ! | " % &lt; &gt;): it must come through as it is.</summary>
    public static readonly string ShellPassword = Of("a&b", "!c|d", "\"e%PA", "TH%<f>");
}
