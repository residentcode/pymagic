using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>BDD in .feature files beyond the checks: going to a step's definition and back, completing steps, and Gherkin in other languages.</summary>
public class BddTests
{
    const string Root = "file:///c:/proj/";

    static LanguageService Project(params (string File, string Text)[] files)
    {
        var svc = new LanguageService { TypeshedPath = PyMagic.Core.Types.Workspace.DefaultTypeshedPath };
        svc.SourceRoots.Add(@"c:\proj");
        foreach (var (f, t) in files) svc.Open(Root + f, t, 1);
        return svc;
    }

    const string Steps = "from behave import given, when, then\n\n\n" +
        "@given('a shop')\ndef step_impl(context):\n    pass\n\n\n" +                  // 3, 4
        "@given('{n:d} apples in the basket')\ndef step_impl(context, n):\n    pass\n\n\n" +   // 9, 10
        "@when('I pay')\ndef step_impl(context):\n    pass\n\n\n" +                     // 15, 16
        "@then('the basket is empty')\ndef step_impl(context):\n    pass\n";          // 21, 22

    const string Feature = "Feature: Shop\n" +
        "  Scenario: Buying\n" +
        "    Given a shop\n" +                        // 2
        "    And 3 apples in the basket\n" +          // 3
        "    When I pay\n" +                          // 4
        "    Then the basket is empty\n" +            // 5
        "\n  Scenario: Again\n    Given 5 apples in the basket\n";   // 8

    static string Where(IEnumerable<Location> locs) => string.Join(" | ", locs.Select(l => $"{l.Uri[Root.Length..]}:{l.Range.Start.Line}:{l.Range.Start.Character}"));

    [Fact]
    public void GoToAStepsDefinition()
    {
        var svc = Project(("features/steps/shop.py", Steps), ("features/shop.feature", Feature));
        // on the text, and on the keyword of an And (a given, as the step before)
        Assert.Equal("features/steps/shop.py:9:4", Where(svc.Definition(Root + "features/shop.feature", new Position(3, 12))));
        Assert.Equal("features/steps/shop.py:9:4", Where(svc.Definition(Root + "features/shop.feature", new Position(3, 5))));
        Assert.Equal("features/steps/shop.py:14:4", Where(svc.Definition(Root + "features/shop.feature", new Position(4, 10))));
        // a line that is not a step
        Assert.Equal("", Where(svc.Definition(Root + "features/shop.feature", new Position(1, 5))));
    }

    [Fact]
    public void FindUsagesOfAStepDefinition()
    {
        var svc = Project(("features/steps/shop.py", Steps), ("features/shop.feature", Feature));
        // on the pattern: the steps it matches, in every scenario
        Assert.Equal("features/shop.feature:3:8 | features/shop.feature:8:10", Where(svc.References(Root + "features/steps/shop.py", new Position(8, 10), false)));
        // on the function's name, with the declaration
        Assert.Equal("features/steps/shop.py:14:4 | features/shop.feature:4:9", Where(svc.References(Root + "features/steps/shop.py", new Position(14, 6), true)));
    }

    [Fact]
    public void PytestBddOutlineStepsAreUsages()
    {
        string steps = "from pytest_bdd import given, then, parsers\n\n\n" +
            "@given(parsers.parse('{count:d} cucumbers'))\ndef cucumbers(count):\n    pass\n\n\n" +
            "@then('nothing')\ndef nothing():\n    pass\n";
        string feature = "Feature: F\n  Scenario Outline: O\n    Given <start> cucumbers\n    Then nothing\n\n    Examples:\n      | start |\n      | 12    |\n";
        var svc = Project(("tests/test_c.py", steps), ("tests/c.feature", feature));
        Assert.Equal("tests/c.feature:2:10", Where(svc.References(Root + "tests/test_c.py", new Position(4, 5), false)));
        Assert.Equal("tests/test_c.py:4:4", Where(svc.Definition(Root + "tests/c.feature", new Position(2, 12))));
    }

    [Fact]
    public void CompletingSteps()
    {
        string feature = "Feature: Shop\n  Scenario: S\n    Given a shop\n    And \n    When I\n";
        var svc = Project(("features/steps/shop.py", Steps), ("features/shop.feature", feature));
        // after And: the givens, the parameter a placeholder, replacing what follows the keyword
        var items = svc.Completion(Root + "features/shop.feature", new Position(3, 8));
        Assert.Equal("a shop | {n:d} apples in the basket", string.Join(" | ", items.Select(i => i.Label)));
        var apples = items.Single(i => i.Label.StartsWith('{'));
        Assert.Equal("${1:n} apples in the basket", apples.InsertText);
        Assert.Equal("3:8-3:8", $"{apples.TextEdit!.Range.Start.Line}:{apples.TextEdit.Range.Start.Character}-{apples.TextEdit.Range.End.Line}:{apples.TextEdit.Range.End.Character}");
        // after When with something typed: the whens, over what is typed
        var whens = svc.Completion(Root + "features/shop.feature", new Position(4, 10));
        Assert.Equal("I pay", string.Join(" | ", whens.Select(i => i.Label)));
        Assert.Equal("4:9-4:10", $"{whens[0].TextEdit!.Range.Start.Line}:{whens[0].TextEdit!.Range.Start.Character}-{whens[0].TextEdit!.Range.End.Line}:{whens[0].TextEdit!.Range.End.Character}");
        // not after a keyword
        Assert.Equal(0, svc.Completion(Root + "features/shop.feature", new Position(1, 5)).Count);
    }

    [Fact]
    public void StepPatternsAsSnippets()
    {
        Assert.Equal(("${1:n} apples in the basket", "n apples in the basket"), LanguageService.StepSnippet("{n:d} apples in the basket", "parse"));
        Assert.Equal(("a {brace\\} costs \\$1", "a {brace} costs $1"), LanguageService.StepSnippet("a {{brace}} costs $1", "parse"));
        Assert.Equal(("I have ${1:left} ${2}", "I have left ..."), LanguageService.StepSnippet(@"^I have (?P<left>\d+) .*$", "re"));
        Assert.Equal(("I (${1}) ${2} times.", "I (...) ... times."), LanguageService.StepSnippet(@"I \((\w+)\) \d+ times.", "re"));
        Assert.Equal(("pay \\$5", "pay $5"), LanguageService.StepSnippet("pay $5", "exact"));
    }

    // ------------------------------------------------------------ languages

    [Fact]
    public void GermanKeywords()
    {
        string feature = "# language: de\nFunktionalität: Laden\n  Szenario: Kaufen\n    Angenommen a shop\n    Und 3 apples in the basket\n    Wenn I leave\n    Dann the basket is empty\n" +
            "\n  Szenariogrundriss: Viele\n    Gegeben seien <n> apples in the basket\n\n    Beispiele:\n      | n |\n      | 2 |\n";
        var svc = Project(("features/steps/shop.py", Steps), ("features/shop.feature", feature));
        var d = svc.Diagnostics(Root + "features/shop.feature").Select(x => $"{x.Range.Start.Line}: {x.Message}").ToList();
        Assert.Equal("5: Undefined step reference: 'I leave'", string.Join(" | ", d));
        Assert.Equal("features/steps/shop.py:9:4", Where(svc.Definition(Root + "features/shop.feature", new Position(9, 20))));
        var parsed = LanguageService.ParseGherkin(feature);
        Assert.Equal("de", parsed.Dialect.Code);
        Assert.Equal("Angenommen given a shop | Und given 3 apples in the basket | Wenn when I leave | Dann then the basket is empty | Gegeben seien given <n> apples in the basket",
            string.Join(" | ", parsed.Scenarios.SelectMany(s => s.Steps).Select(s => $"{s.Keyword} {s.Kind} {s.Text}")));
        Assert.True(parsed.Scenarios[1].Outline);
        Assert.Equal(1, parsed.Scenarios[1].Examples.Count);
    }

    [Fact]
    public void ChineseKeywordsWithoutASpace()
    {
        var parsed = LanguageService.ParseGherkin("# language: zh-CN\n功能: 商店\n  场景: 买\n    假如有一个商店\n    那么篮子是空的\n");
        Assert.Equal("假如 given 有一个商店 | 那么 then 篮子是空的", string.Join(" | ", parsed.Scenarios.SelectMany(s => s.Steps).Select(s => $"{s.Keyword} {s.Kind} {s.Text}")));
    }

    [Fact]
    public void LanguageCommentOnlyBeforeTheFeature()
    {
        Assert.Equal("en", LanguageService.ParseGherkin("Feature: F\n# language: de\n  Scenario: S\n    Given x\n").Dialect.Code);
        Assert.Equal("fr", LanguageService.ParseGherkin("\n# a comment\n#language:fr\nFonctionnalité: F\n").Dialect.Code);
        // a language Cucumber does not have: English
        Assert.Equal("en", LanguageService.ParseGherkin("# language: xx\nFeature: F\n").Dialect.Code);
        Assert.True(GherkinDialect.Codes.Count() >= 70);
    }

    [Fact]
    public void AnUnknownLanguageIsMarked()
    {
        var svc = Project(("features/steps/shop.py", Steps), ("features/shop.feature", "# language: xx\nFeature: F\n  Scenario: S\n    Given a shop\n"),
            ("features/de.feature", "# language: de\nFunktionalität: F\n"));
        var d = svc.Diagnostics(Root + "features/shop.feature").Where(x => x.Code == "bdd-unknown-language").ToList();
        Assert.Equal("0:12 Unknown language: 'xx' (read as English)", string.Join(" | ", d.Select(x => $"{x.Range.Start.Line}:{x.Range.Start.Character} {x.Message}")));
        Assert.Equal(0, svc.Diagnostics(Root + "features/de.feature").Count(x => x.Code == "bdd-unknown-language"));
    }
}
