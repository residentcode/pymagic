using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Spelling (item 240): Windows' spell checker, and the project's dictionary.</summary>
public class SpellingTests
{
    static string Root() { string r = Path.Combine(Path.GetTempPath(), "pm_spell_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(r); return r; }

    [Fact]
    public void WindowsFlagsATypoAndSuggestsTheWord()
    {
        if (!Spelling.Available("en-US")) { Console.WriteLine("(no English spell checker on this machine)"); return; }
        string root = Root();
        try
        {
            var bad = Spelling.Misspelled(root, "en-US", new[] { "receive", "recieve", "function", "fucntion", "database", "Pytohn" });
            Assert.Equal("Pytohn,fucntion,recieve", string.Join(",", bad.OrderBy(x => x, StringComparer.Ordinal)));
            Assert.True(Spelling.Suggest("en-US", "recieve").Contains("receive"), string.Join(",", Spelling.Suggest("en-US", "recieve")));
            Assert.Equal(0, Spelling.Suggest("en-US", "").Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AWordOfTheProjectsDictionaryIsNotATypo()
    {
        string root = Root();
        try
        {
            Assert.Equal(0, Spelling.ProjectWords(root).Count);
            Spelling.AddWord(root, "Pytohn");
            Spelling.AddWord(root, "pytohn");                                // once
            Spelling.AddWord(root, "zork");
            Assert.Equal("pytohn\nzork\n", File.ReadAllText(Spelling.DictionaryFile(root)));
            Assert.Throws<ArgumentException>(() => Spelling.AddWord(root, "not a word"));
            Assert.Throws<ArgumentException>(() => Spelling.AddWord(root, "a;b"));
            if (Spelling.Available("en-US")) Assert.Equal(0, Spelling.Misspelled(root, "en-US", new[] { "Pytohn", "zork" }).Count);
            Assert.True(Spelling.RemoveWord(root, "ZORK"));
            Assert.True(!Spelling.RemoveWord(root, "zork"));
            Assert.Equal("pytohn\n", File.ReadAllText(Spelling.DictionaryFile(root)));
        }
        finally { Directory.Delete(root, true); }
    }
}
