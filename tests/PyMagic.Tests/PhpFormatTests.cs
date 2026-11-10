using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Format of a PHP file with the project's own formatter (PhpFormat): Laravel's Pint of a copy of Laravel's app (PYMAGIC_TEST_LARAVEL_DIR, run by
/// the PHP PYMAGIC_PHP names) with its default preset and with the project's pint.json, a file that is not valid PHP, and a project without one.
/// </summary>
public class PhpFormatTests
{
    static string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    const string Messy = "<?php\nnamespace App;\nclass  Demo{\npublic function  run( $a,$b ){\n if($a==$b){return  array(1,2);}\nreturn [ ];\n}\n}\n";

    /// <summary>A copy of the app with only what Pint needs: composer.json and vendor are big, so vendor is linked by path (Pint runs from the source).</summary>
    static string CopyOf(string source)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_phpformat_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, f);
            if (rel.StartsWith("node_modules") || rel.StartsWith(".git" + Path.DirectorySeparatorChar) || rel.StartsWith("storage" + Path.DirectorySeparatorChar)) continue;
            string to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dir;
    }

    [Fact]
    public void PintOfTheProjectFormatsTheTextWithItsSettings()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_LARAVEL_DIR") is not { } source || !File.Exists(Path.Combine(source, "vendor", "bin", "pint"))) return;
        string dir = CopyOf(source);
        try
        {
            Assert.Equal("Pint", PhpFormat.Tool(dir)!.Value.Name);
            // Laravel's preset: braces on their own line, short arrays, spaces around operators, a blank line before return
            string formatted = PhpFormat.Format(dir, php, Messy);
            Assert.Equal("<?php\n\nnamespace App;\n\nclass Demo\n{\n    public function run($a, $b)\n    {\n        if ($a == $b) {\n            return [1, 2];\n        }\n\n        return [];\n    }\n}\n", formatted.Replace("\r\n", "\n"));
            // formatting it again changes nothing
            Assert.Equal(formatted, PhpFormat.Format(dir, php, formatted));
            // the project's pint.json: a rule of its own (long arrays kept)
            File.WriteAllText(Path.Combine(dir, "pint.json"), "{\"preset\": \"laravel\", \"rules\": {\"array_syntax\": {\"syntax\": \"long\"}}}");
            Assert.Contains("return array(1, 2);", PhpFormat.Format(dir, php, Messy));
            // not PHP: the reason, and nothing changed
            try { PhpFormat.Format(dir, php, "<?php\nfunction (\n"); Assert.True(false, "a file with a syntax error was formatted"); }
            catch (ServiceException e) { Assert.Contains("not valid PHP", e.Message); }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void AProjectWithoutAFormatterIsToldWhatToAdd()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_phpformat_none_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(PhpFormat.Tool(dir) == null, "no formatter");
            try { PhpFormat.Format(dir, null, Messy); Assert.True(false, "formatted without a formatter"); }
            catch (ServiceException e) { Assert.Contains("composer require --dev laravel/pint", e.Message); }
        }
        finally { Directory.Delete(dir, true); }
    }
}
