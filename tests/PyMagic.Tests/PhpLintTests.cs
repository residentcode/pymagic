using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PHP's syntax check while typing (PhpLint, php -l on the editor's text): its output read back, and, when PYMAGIC_PHP names a php.exe, PHP's
/// own answers on texts with a missing semicolon, a broken function, a missing brace and none.
/// </summary>
public class PhpLintTests
{
    static string? Php => Environment.GetEnvironmentVariable("PYMAGIC_PHP") is { Length: > 0 } p && File.Exists(p) ? p : null;

    [Fact]
    public void PhpsLintOutputIsReadAsItsLineAndMessage()
    {
        var f = PhpLint.Parse("PHP Parse error:  syntax error, unexpected token \"$y\", expecting \",\" or \";\" in Standard input code on line 3\nErrors parsing Standard input code\n");
        Assert.True(f != null);
        Assert.Equal(3, f!.Line);
        Assert.Equal("syntax error, unexpected token \"$y\", expecting \",\" or \";\"", f.Message);
        // the form PHP writes when display_errors goes to the output, and a fatal error of compiling
        Assert.Equal(7, PhpLint.Parse("\nParse error: syntax error, unexpected end of file in - on line 7\n")!.Line);
        Assert.Equal("Cannot redeclare f()", PhpLint.Parse("PHP Fatal error:  Cannot redeclare f() in /tmp/x.php on line 4")!.Message);
        Assert.True(PhpLint.Parse("No syntax errors detected in Standard input code\n") == null);
    }

    [Fact]
    public void PhpItselfFindsTheSyntaxErrorsOfTheText()
    {
        if (Php is not { } php) return;
        string root = Path.GetTempPath();
        var semicolon = PhpLint.Lint(root, php, "<?php\necho \"x\"\n$y = 2;\n");
        Assert.True(semicolon != null);
        Assert.Equal(3, semicolon!.Line);
        Assert.Contains("unexpected", semicolon.Message);
        var function = PhpLint.Lint(root, php, "<?php\nfunction f( {\n  return 1;\n}\n");
        Assert.Equal(2, function!.Line);
        var brace = PhpLint.Lint(root, php, "<?php\nif ($a) {\n  echo 1;\n");
        Assert.Equal("Unclosed '{' on line 2", brace!.Message);   // PHP 8's words
        Assert.True(PhpLint.Lint(root, php, "<?php\n$a = [1, 2];\necho count($a);\n") == null);
        // not PHP at all after the tag is still read; text outside <?php is HTML, not an error
        Assert.True(PhpLint.Lint(root, php, "<p>hello</p>\n<?php echo 1; ?>\n") == null);
    }
}
