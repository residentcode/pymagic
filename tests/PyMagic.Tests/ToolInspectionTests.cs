using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>PyCharm's inspections for other tools: pandas, unittest.mock, notebooks, BDD (behave, pytest-bdd) and PySpark.</summary>
public class ToolInspectionTests
{
    const string Root = "file:///c:/proj/";

    /// <summary>The scan's Python, which has pandas 3.1 and pandas-stubs; null when it is not on this machine.</summary>
    static string? ScanSite()
    {
        string site = Path.Combine(Path.GetTempPath(), "pm-scan-venv", "Lib", "site-packages");
        return Directory.Exists(Path.Combine(site, "pandas-stubs")) ? site : null;
    }

    static LanguageService Project(string? site, params (string File, string Text)[] files)
    {
        var svc = new LanguageService { TypeshedPath = PyMagic.Core.Types.Workspace.DefaultTypeshedPath };
        svc.SourceRoots.Add(@"c:\proj");
        if (site != null) svc.LibraryRoots.Add(site);
        foreach (var (f, t) in files) svc.Open(Root + f, t, 1);
        return svc;
    }

    static List<string> Found(LanguageService svc, string file, string code) =>
        svc.Diagnostics(Root + file).Where(d => d.Code == code).Select(d => $"{d.Range.Start.Line}: {d.Message}").ToList();

    static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var lines = new LineMap(text);
        foreach (var e in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    /// <summary>The text after the quick fix with this title at the start of the finding on that line.</summary>
    static string Fixed(LanguageService svc, string file, string text, string code, int line, string title)
    {
        var d = svc.Diagnostics(Root + file).First(x => x.Code == code && x.Range.Start.Line == line);
        var action = svc.CodeActions(Root + file, d.Range).Single(a => a.Title == title);
        return Apply(text, action.Edit!.Changes[Root + file]);
    }

    // ------------------------------------------------------------ pandas

    /// <summary>A small stand-in for pandas, shaped as pandas-stubs has it.</summary>
    const string FakePandas = "from typing import Any\n\n" +
        "class NDFrame:\n    @property\n    def empty(self) -> bool: ...\n    def __bool__(self) -> bool: ...\n\n" +
        "class Series(NDFrame):\n    @property\n    def values(self) -> Arr: ...\n    def to_numpy(self) -> Arr: ...\n    def to_list(self) -> list[Any]: ...\n    def __gt__(self, other: Any) -> Series: ...\n\n" +
        "class Arr:\n    def tolist(self) -> list[Any]: ...\n\n" +
        "class DataFrame(NDFrame):\n    def __init__(self, data: Any = ...) -> None: ...\n    def __getitem__(self, key: str) -> Series: ...\n    @property\n    def values(self) -> Arr: ...\n\n" +
        "class Index:\n    def __bool__(self) -> bool: ...\n";

    const string PandasUse = "import pandas as pd\n\n" +
        "df = pd.DataFrame({'a': [1]})\n" +                 // 2
        "items = df['a'].values.tolist()\n" +              // 3
        "rows = df.values.tolist()\n" +                    // 4: a DataFrame has no to_list
        "more = df['a'].to_numpy().tolist()\n" +           // 5
        "if df:\n    pass\n" +                             // 6
        "if not df['a']:\n    pass\n" +                    // 8
        "ok = df['a'] > 1 and True\n" +                    // 10
        "while df.empty or df['a'] > 2:\n    break\n" +    // 11
        "fine = df if True else None\n" +                  // 13: df is the value, not the test
        "x = [v for v in [1] if df['a']]\n" +              // 14
        "assert df.empty\n";                               // 15

    [Fact]
    public void PandasSeriesToListAndTruthValue()
    {
        var svc = Project(null, ("pandas/__init__.pyi", FakePandas), ("m.py", PandasUse));
        Assert.Equal("3: Method Series.to_list() is recommended | 5: Method Series.to_list() is recommended", string.Join(" | ", Found(svc, "m.py", "pandas-series-to-list")));
        var truth = Found(svc, "m.py", "pandas-truth-value");
        Assert.Equal("6 8 10 11 14", string.Join(" ", truth.Select(t => t.Split(':')[0])));
        Assert.Contains("The truth value of a DataFrame is ambiguous", truth[0]);
        Assert.Contains("The truth value of a Series is ambiguous", truth[1]);
    }

    [Fact]
    public void PandasFixes()
    {
        var svc = Project(null, ("pandas/__init__.pyi", FakePandas), ("m.py", PandasUse));
        Assert.Contains("items = df['a'].to_list()\n", Fixed(svc, "m.py", PandasUse, "pandas-series-to-list", 3, "Replace with to_list()"));
        Assert.Contains("more = df['a'].to_list()\n", Fixed(svc, "m.py", PandasUse, "pandas-series-to-list", 5, "Replace with to_list()"));
        Assert.Contains("if not df.empty:\n", Fixed(svc, "m.py", PandasUse, "pandas-truth-value", 6, "Replace with 'not df.empty'"));
        Assert.Contains("if df['a'].empty:\n", Fixed(svc, "m.py", PandasUse, "pandas-truth-value", 8, "Replace with 'df['a'].empty'"));
        Assert.Contains("ok = not (df['a'] > 1).empty and True\n", Fixed(svc, "m.py", PandasUse, "pandas-truth-value", 10, "Replace with 'not (df['a'] > 1).empty'"));
    }

    [Fact]
    public void PandasWithTheRealStubs()
    {
        if (ScanSite() is not { } site) return;   // pandas and pandas-stubs are in the scan's Python only
        string code = "import pandas as pd\n\n" +
            "df = pd.read_csv('data.csv')\n" +
            "names = df['name'].values.tolist()\n" +
            "if df['age'] > 30:\n    pass\n" +
            "if df.empty or len(df) == 0:\n    pass\n" +
            "s = pd.Series([1, 2])\n" +
            "if not s:\n    pass\n" +
            "if s.any():\n    pass\n" +
            "idx = df.columns\n" +
            "if idx:\n    pass\n";
        var svc = Project(site, ("m.py", code));
        Assert.Equal("3: Method Series.to_list() is recommended", string.Join(" | ", Found(svc, "m.py", "pandas-series-to-list")));
        Assert.Equal("4 9 14", string.Join(" ", Found(svc, "m.py", "pandas-truth-value").Select(t => t.Split(':')[0])));
    }

    // ------------------------------------------------------------ unittest.mock

    const string Services = "import os\n\n\nclass Client:\n    retries = 3\n\n    def __init__(self):\n        self.host = 'h'\n\n    def send(self, data):\n        return data\n\n\ndef notify(text):\n    return Client().send(text)\n";

    [Fact]
    public void PatchTargetsThatDoNotExist()
    {
        string tests = "from unittest import mock\nfrom unittest.mock import patch\nfrom app import services\nfrom app.services import Client\n\n\n" +
            "@patch('app.services.notfy')\n" +                       // 6: misspelt
            "@patch('app.services.notify')\n" +                      // 7
            "@patch('app.services.Client.send')\n" +                 // 8
            "@patch('app.services.Client.sendd')\n" +                // 9
            "@patch('app.services.os.remove')\n" +                   // 10: what the module imported
            "@patch('app.services.extra', create=True)\n" +          // 11
            "@patch('notinstalled.thing')\n" +                       // 12: nothing to say
            "def test_a(*mocks):\n    pass\n\n\n" +
            "def test_b():\n" +
            "    with patch('app.services.Clientt'):\n        pass\n" +   // 18
            "    with mock.patch.object(Client, 'retries'):\n        pass\n" +
            "    with mock.patch.object(Client, 'retry'):\n        pass\n" +  // 22
            "    with patch.object(services, 'notify'), patch.object(services, 'nothing'):\n        pass\n" +  // 24
            "    with patch('nodots'):\n        pass\n";              // 26
        var svc = Project(null, ("app/__init__.py", ""), ("app/services.py", Services), ("test_s.py", tests));
        Assert.Equal("6: Cannot find 'notfy' in 'app.services': patch raises AttributeError | " +
            "9: Cannot find 'sendd' in 'app.services.Client': patch raises AttributeError | " +
            "18: Cannot find 'Clientt' in 'app.services': patch raises AttributeError | " +
            "22: class 'Client' has no attribute 'retry': patch.object raises AttributeError | " +
            "24: module 'app.services' has no attribute 'nothing': patch.object raises AttributeError | " +
            "26: 'nodots' is not a dotted path: patch raises TypeError (\"Need a valid target to patch\")",
            string.Join(" | ", Found(svc, "test_s.py", "mock-patch-target")));
    }

    [Fact]
    public void AttributesAMockDoesNotHave()
    {
        string tests = "from unittest.mock import Mock, MagicMock, create_autospec, patch\nfrom app.services import Client\n\n\n" +
            "def test_spec():\n" +
            "    m = Mock(spec=Client)\n" +                          // 5
            "    m.send(1)\n    m.host\n    m.retries\n" +
            "    m.sendd(1)\n" +                                     // 9
            "    m.assert_called_once_with(1)\n" +
            "    m.assert_called_once_wiht(1)\n" +                    // 11
            "    m.anything_set = 1\n" +
            "    a = create_autospec(Client)\n    a.nope\n" +          // 14
            "    free = MagicMock()\n    free.whatever.deep\n    free.assret_called()\n" +   // 17
            "    with patch('app.services.Client', autospec=True) as k:\n        k.send\n        k.sned\n" +   // 20
            "\n\n@patch('app.services.Client')\ndef test_param(client):\n    client.assert_not_caled()\n    client.anything\n";   // 25
        var svc = Project(null, ("app/__init__.py", ""), ("app/services.py", Services), ("test_s.py", tests));
        Assert.Equal("9: The mock's spec 'Client' has no attribute 'sendd': it raises AttributeError | " +
            "11: 'assert_called_once_wiht' is not an assertion of mock: it raises AttributeError | " +
            "14: The mock's spec 'Client' has no attribute 'nope': it raises AttributeError | " +
            "17: 'assret_called' is not an assertion of mock: it raises AttributeError | " +
            "20: The mock's spec 'Client' has no attribute 'sned': it raises AttributeError | " +
            "25: 'assert_not_caled' is not an assertion of mock: it raises AttributeError",
            string.Join(" | ", Found(svc, "test_s.py", "mock-attribute")));
    }

    // ------------------------------------------------------------ BDD

    const string Feature = "Feature: Shopping\n\n" +
        "  Background:\n    Given a shop\n\n" +                                 // 3
        "  Scenario: Buying\n" +
        "    Given 3 apples in the basket\n" +                                  // 6
        "    When I pay\n" +                                                    // 7
        "    And I leave the shop\n" +                                          // 8: undefined (a when)
        "    Then the basket is empty\n" +                                      // 9
        "    * nothing is left\n" +                                             // 10: undefined
        "    Then the bill says:\n      | item  | price |\n      | apple | 1     |\n      | pear  |\n" +   // 11..14: row 14 is broken
        "\n  Scenario Outline: Many\n" +                                        // 16
        "    Given <count> apples in the basket\n" +                            // 17
        "    Then I have <left> <unit>\n" +                                     // 18: <unit> has no column
        "\n    Examples:\n      | count | left | spare |\n      | 2     | 2    | x     |\n" +   // 20, 21: spare unused
        "\n  Scenario Outline: Nothing to run\n    Given a shop\n";            // 24

    const string BehaveSteps = "from behave import given, when, then, step, use_step_matcher\n\n\n" +
        "@given('a shop')\ndef step_impl(context):\n    pass\n\n\n" +
        "@given('{n:d} apples in the basket')\ndef step_impl(context, n):\n    pass\n\n\n" +
        "@when('I pay')\ndef step_impl(context):\n    pass\n\n\n" +
        "@then('the basket is empty')\ndef step_impl(context):\n    pass\n\n\n" +
        "@then('the bill says:')\ndef step_impl(context):\n    pass\n\n\n" +
        "use_step_matcher('re')\n\n\n" +
        "@then(r'I have (?P<left>\\d+) .*')\ndef step_impl(context, left):\n    pass\n\n\n" +
        "@step('nobody uses this')\ndef never(context):\n    pass\n";

    [Fact]
    public void FeatureFileChecks()
    {
        var svc = Project(null, ("features/steps/shop_steps.py", BehaveSteps), ("features/shop.feature", Feature));
        var d = svc.Diagnostics(Root + "features/shop.feature").Select(x => $"{x.Range.Start.Line} {x.Code}: {x.Message}").ToList();
        Assert.Equal(string.Join("\n", new[]
        {
            "8 bdd-undefined-step: Undefined step reference: 'I leave the shop'",
            "10 bdd-undefined-step: Undefined step reference: 'nothing is left'",
            "21 bdd-examples-column: Column 'spare' is not used in the steps of the outline",
            "18 bdd-examples-column: No Examples column for '<unit>'",
            "24 bdd-outline-examples: Scenario Outline has no Examples",
            "14 bdd-table: This row has 1 cells, the table's first row 2",
        }), string.Join("\n", d));
    }

    [Fact]
    public void UnusedStepDefinition()
    {
        var svc = Project(null, ("features/steps/shop_steps.py", BehaveSteps), ("features/shop.feature", Feature));
        Assert.Equal("36: Step definition 'never' is not used by any step of the project's .feature files", string.Join(" | ", Found(svc, "features/steps/shop_steps.py", "bdd-unused-step")));
    }

    [Fact]
    public void PytestBddStepsAndCreatingADefinition()
    {
        string steps = "import pytest_bdd\nfrom pytest_bdd import given, parsers, scenarios\n\nscenarios('features')\n\n\n" +
            "@given('a shop')\ndef a_shop():\n    pass\n\n\n" +
            "@given(parsers.parse('{n:d} apples in the basket'))\ndef apples(n):\n    pass\n\n\n" +
            "@pytest_bdd.when(parsers.re(r'I (?P<what>pay|leave)'))\ndef act(what):\n    pass\n";
        string feature = "Feature: F\n  Scenario: S\n    Given a shop\n    And 2 apples in the basket\n    When I pay\n    Then I see \"done\"\n";
        var svc = Project(null, ("tests/test_shop.py", steps), ("tests/features/f.feature", feature));
        var d = svc.Diagnostics(Root + "tests/features/f.feature").Select(x => $"{x.Range.Start.Line}: {x.Message}").ToList();
        Assert.Equal("5: Undefined step reference: 'I see \"done\"'", string.Join(" | ", d));
        Assert.Equal("", string.Join(" | ", Found(svc, "tests/test_shop.py", "bdd-unused-step")));

        var diag = svc.Diagnostics(Root + "tests/features/f.feature").Single();
        var action = svc.CodeActions(Root + "tests/features/f.feature", diag.Range).Single();
        Assert.Equal("Create step definition in test_shop.py", action.Title);
        string after = Apply(steps, action.Edit!.Changes[Root + "tests/test_shop.py"]);
        // the file imports pytest_bdd itself: the decorator is written through it, and nothing is imported
        Assert.True(after.StartsWith("import pytest_bdd\n"), after);
        Assert.True(after.EndsWith("def act(what):\n    pass\n\n\n@pytest_bdd.then('I see \"done\"')\ndef i_see_done():\n    raise NotImplementedError('STEP: Then I see \"done\"')\n"), after);
    }

    [Fact]
    public void BehaveCreateStepDefinitionEscapesBraces()
    {
        string feature = "Feature: F\n  Scenario: S\n    Given a shop\n    When I type {x}\n";
        var svc = Project(null, ("features/steps/s.py", "from behave import *\n\n\n@given('a shop')\ndef step_impl(context):\n    pass\n"), ("features/f.feature", feature));
        var diag = svc.Diagnostics(Root + "features/f.feature").Single();
        var action = svc.CodeActions(Root + "features/f.feature", diag.Range).Single();
        string after = Apply("from behave import *\n\n\n@given('a shop')\ndef step_impl(context):\n    pass\n", action.Edit!.Changes[Root + "features/steps/s.py"]);
        Assert.True(after.EndsWith("    pass\n\n\n@when('I type {{x}}')\ndef step_impl(context):\n    raise NotImplementedError('STEP: When I type {x}')\n"), after);
    }

    // ------------------------------------------------------------ notebooks

    [Fact]
    public void NotebookNameDefinedInALaterCell()
    {
        // the page's file for a notebook: its code cells one after the other
        string uri = Root + ".pymagic/notebooks/analysis_ipynb.py";
        string cells = "print(total)\n" +                          // 0: total comes later
            "def f():\n    return later\n" +                       // 1: runs when called
            "class Report:\n    size = total\n" +                  // 4: a class body runs at once
            "squares = [total * k for k in range(3)]\n" +           // 5
            "total = 1\nlater = 2\nprint(total)\n" +              // 6..8
            "for i in range(3):\n    pass\nprint(i)\n" +
            "def g():\n    global made\n    made = 1\n" +
            "print(made)\nmade = 2\n";
        var svc = Project(null, (".pymagic/notebooks/analysis_ipynb.py", cells));
        Assert.Equal("0 4 5", string.Join(" ", svc.Diagnostics(uri).Where(d => d.Code == "notebook-cell-order").Select(d => d.Range.Start.Line)));
        // the same text in an ordinary file is not a notebook
        var plain = Project(null, ("script.py", cells));
        Assert.Equal(0, plain.Diagnostics(Root + "script.py").Count(d => d.Code == "notebook-cell-order"));
    }

    // ------------------------------------------------------------ PySpark

    /// <summary>A small stand-in for pyspark (it is not installed here): the parts the check reads.</summary>
    static readonly (string, string)[] FakeSpark =
    {
        ("pyspark/__init__.pyi", ""),
        ("pyspark/sql/__init__.pyi", "from pyspark.sql.session import SparkSession as SparkSession\nfrom pyspark.sql.dataframe import DataFrame as DataFrame\n"),
        ("pyspark/sql/column.pyi", "from typing import Any\n\nclass Column:\n    def __gt__(self, other: Any) -> Column: ...\n    def __add__(self, other: Any) -> Column: ...\n    def alias(self, name: str) -> Column: ...\n"),
        ("pyspark/sql/dataframe.pyi", "from typing import Any\nfrom pyspark.sql.column import Column\n\nclass GroupedData:\n    def count(self) -> DataFrame: ...\n\n" +
            "class DataFrame:\n    def select(self, *cols: Any) -> DataFrame: ...\n    def filter(self, condition: Any) -> DataFrame: ...\n    def where(self, condition: Any) -> DataFrame: ...\n" +
            "    def withColumn(self, name: str, col: Column) -> DataFrame: ...\n    def withColumnRenamed(self, existing: str, new: str) -> DataFrame: ...\n" +
            "    def drop(self, *cols: Any) -> DataFrame: ...\n    def groupBy(self, *cols: Any) -> GroupedData: ...\n    def orderBy(self, *cols: Any) -> DataFrame: ...\n" +
            "    def distinct(self) -> DataFrame: ...\n    def show(self) -> None: ...\n    def count(self) -> int: ...\n    def __getitem__(self, item: str) -> Column: ...\n    def __getattr__(self, name: str) -> Column: ...\n"),
        ("pyspark/sql/session.pyi", "from typing import Any\nfrom pyspark.sql.dataframe import DataFrame\n\nclass SparkSession:\n    def createDataFrame(self, data: Any, schema: Any = ...) -> DataFrame: ...\n"),
        ("pyspark/sql/functions.pyi", "from pyspark.sql.column import Column\n\ndef col(name: str) -> Column: ...\n"),
    };

    [Fact]
    public void SparkColumnsTheDataFrameDoesNotHave()
    {
        string code = "from pyspark.sql import SparkSession\nfrom pyspark.sql import functions as F\nfrom pyspark.sql.types import StructType, StructField\n\n" +
            "spark: SparkSession = SparkSession()\n" +
            "people = spark.createDataFrame([('a', 1)], ['name', 'age'])\n" +           // 5
            "people.select('name', 'agee').show()\n" +                                  // 6
            "people.filter(F.col('Age') > 1).select(people.nmae)\n" +                   // 7: Age is age (no case), nmae is not
            "older = people.withColumn('next', F.col('age') + 1).withColumnRenamed('name', 'who')\n" +
            "older.select('who', 'next', 'name').show()\n" +                            // 9: name was renamed
            "print(older['age'], older.count())\n" +                                     // 10
            "small = older.drop('age')\n" +
            "small.orderBy('age').show()\n" +                                            // 12
            "ddl = spark.createDataFrame([], 'id INT, label STRING')\n" +
            "ddl.groupBy('lable').count()\n" +                                           // 14
            "if True:\n    ddl = ddl.withColumn('x', F.col('id'))\n" +
            "ddl.select('anything')\n" +                                                 // 17: not known after the if
            "unknown = spark.createDataFrame([])\nunknown.select('whatever')\n";
        var files = FakeSpark.Append(("job.py", code)).ToArray();
        var svc = Project(null, files);
        Assert.Equal(string.Join("\n", new[]
        {
            "6: Column 'agee' is not in the DataFrame (its columns: name, age): Spark raises AnalysisException",
            "7: Column 'nmae' is not in the DataFrame (its columns: name, age): Spark raises AnalysisException",
            "9: Column 'name' is not in the DataFrame (its columns: who, age, next): Spark raises AnalysisException",
            "12: Column 'age' is not in the DataFrame (its columns: who, next): Spark raises AnalysisException",
            "14: Column 'lable' is not in the DataFrame (its columns: id, label): Spark raises AnalysisException",
        }), string.Join("\n", Found(svc, "job.py", "spark-column")));
    }
}
