using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Grammar through LanguageTool (item 246). The live checks need LanguageTool installed (PYMAGIC_LANGUAGETOOL names its folder) and Java.</summary>
public class GrammarTests
{
    [Fact]
    public void TheAnswerOfLanguageToolIsRead()
    {
        const string json = "{\"software\":{},\"matches\":[{\"message\":\"Possible agreement error\",\"offset\":5,\"length\":3,\"replacements\":[{\"value\":\"is\"},{\"value\":\"be\"}],\"rule\":{\"id\":\"THIS_NNS\",\"category\":{\"id\":\"GRAMMAR\",\"name\":\"Grammar\"}}},{\"message\":\"Typo\",\"offset\":0,\"length\":2,\"replacements\":[],\"rule\":{\"id\":\"X\",\"category\":{\"name\":\"Typos\"}}}]}";
        var found = Grammar.Matches(json);
        Assert.Equal(2, found.Count);
        Assert.Equal(5, found[0].Offset);
        Assert.Equal(3, found[0].Length);
        Assert.Equal("THIS_NNS", found[0].RuleId);
        Assert.Equal("Grammar", found[0].Category);
        Assert.Equal("is,be", string.Join(",", found[0].Replacements));
        Assert.Equal(0, found[1].Replacements.Count);
        Assert.Equal(0, Grammar.Matches("{}").Count);
    }

    [Fact]
    public void RulesCanBeSwitchedOffForTheProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_gram_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(0, Grammar.IgnoredRules(root).Count);
            Grammar.IgnoreRule(root, "UPPERCASE_SENTENCE_START");
            Grammar.IgnoreRule(root, "COMMA_PARENTHESIS_WHITESPACE");
            Grammar.IgnoreRule(root, "UPPERCASE_SENTENCE_START");
            Assert.Equal("COMMA_PARENTHESIS_WHITESPACE\nUPPERCASE_SENTENCE_START\n", File.ReadAllText(Grammar.IgnoreFile(root)));
            Assert.Throws<ArgumentException>(() => Grammar.IgnoreRule(root, "bad rule;"));
            Assert.True(Grammar.UnignoreRule(root, "UPPERCASE_SENTENCE_START"));
            Assert.True(!Grammar.UnignoreRule(root, "UPPERCASE_SENTENCE_START"));
            Assert.Equal("COMMA_PARENTHESIS_WHITESPACE", string.Join(",", Grammar.IgnoredRules(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>With PYMAGIC_TEST_GRAMMAR_INSTALL=1 the real download is made into PYMAGIC_LANGUAGETOOL (about 250 MB).</summary>
    [Fact]
    public void LanguageToolIsDownloadedWhenAskedFor()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_GRAMMAR_INSTALL") != "1" || Environment.GetEnvironmentVariable("PYMAGIC_LANGUAGETOOL") == null) return;
        Grammar.StartInstall();
        var end = DateTime.UtcNow.AddMinutes(20);
        while (Grammar.Installing.Phase is "downloading" or "unpacking" && DateTime.UtcNow < end) Thread.Sleep(500);
        Assert.Equal("done", Grammar.Installing.Phase);
        Assert.True(Grammar.Installing.Total > 100_000_000, "the size of the download");
        Assert.True(Grammar.InstalledFolder() != null);
    }

    [Fact]
    public void ATextIsCheckedByTheRunningServer()
    {
        if (Grammar.InstalledFolder() == null || Grammar.JavaVersion() == null) { Console.WriteLine("(LanguageTool or Java is not here)"); return; }
        try
        {
            var found = Grammar.Check("This are a test of the grammar checker. Their going to the store.", "en-US").GetAwaiter().GetResult();
            var ids = string.Join(",", found.Select(f => f.RuleId));
            Assert.True(found.Count >= 2, ids);
            const string text = "This are a test of the grammar checker. Their going to the store.";
            Assert.True(found.Any(f => text.Substring(f.Offset, f.Length).Contains("are")), ids);
            Assert.True(found.Any(f => f.Replacements.Contains("is")) || found.Any(f => f.Replacements.Count > 0), ids);
            Assert.True(Grammar.Running);
            // a rule switched off is not reported
            var rule = found[0].RuleId;
            Assert.True(!Grammar.Check("This are a test of the grammar checker.", "en-US", new[] { rule }).GetAwaiter().GetResult().Any(f => f.RuleId == rule));
            Assert.Equal(0, Grammar.Check("This is a test of the grammar checker.", "en-US").GetAwaiter().GetResult().Count);
        }
        finally { Grammar.Stop(); }
        Assert.True(!Grammar.Running);
    }
}
