using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>
/// Black's last corner cases (item 199), each expected text produced by black 26.10.1 on the input: redundant parentheses around yield,
/// await operands and with-items, with-items in parentheses for Python 3.9 and later, trailing comments of split statements, `# type: ignore`,
/// dummy implementations, `case [...]`, and a lambda whose parameters are not items.
/// </summary>
public class BlackCornerTests
{
    static string Fmt(string text, FormatOptions? options = null) =>
        PythonFormatter.Format(text, options) ?? throw new AssertionException("the formatter declined (" + PythonFormatter.LastMismatch + "):\n" + text);

    static void Same(string input, string black, FormatOptions? options = null)
    {
        string once = Fmt(input, options);
        Assert.Equal(black, once);
        Assert.Equal(once, Fmt(once, options));
    }

    [Fact]
    public void ParenthesesAroundYieldAndAwaitOperands()
    {
        Same("def g():\n    x = (yield)\n    y = (yield 1)\n    print((yield))\n",
             "def g():\n    x = yield\n    y = yield 1\n    print((yield))\n");
        Same("async def f():\n    await (x)\n    await (x.y)\n    await (x[0])\n    await (x())\n    await (x.y.z(1)[2])\n    await (-x)\n    await (not x)\n    await (x if y else z)\n    await (a ** b)\n    await (await x)\n    y = (await x)\n",
             "async def f():\n    await x\n    await x.y\n    await x[0]\n    await x()\n    await x.y.z(1)[2]\n    await (-x)\n    await (not x)\n    await (x if y else z)\n    await (a**b)\n    await (await x)\n    y = await x\n");
    }

    [Fact]
    public void WithItemsLoseTheirParenthesesAndGetThemBackFor39()
    {
        Same("with (open(\"a\")) as f:\n    pass\nwith (open(\"a\") as f, open(\"b\") as g):\n    pass\n",
             "with open(\"a\") as f:\n    pass\nwith open(\"a\") as f, open(\"b\") as g:\n    pass\n");
        string longWith = "with open(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaa.txt\") as first_file, open(\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.txt\") as second_file:\n    pass\n";
        // code for any Python 3: the managers' own brackets are split
        Same(longWith, "with open(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaa.txt\") as first_file, open(\n    \"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.txt\"\n) as second_file:\n    pass\n");
        string parenthesized = "with (\n    open(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaa.txt\") as first_file,\n    open(\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.txt\") as second_file,\n):\n    pass\n";
        // target-version py39 and later, or a file that already needs 3.9 (a match statement)
        Same(longWith, parenthesized, FormatOptions.Default with { ParenthesizedWith = true });
        Same(longWith + "match x:\n    case 1:\n        pass\n", parenthesized + "match x:\n    case 1:\n        pass\n");
        Assert.True(FormatConfig.FromToml("[tool.black]\ntarget-version = [\"py39\", \"py310\"]\n")!.ParenthesizedWith);
        Assert.True(!FormatConfig.FromToml("[tool.black]\ntarget-version = [\"py38\", \"py39\"]\n")!.ParenthesizedWith);
        Assert.True(FormatConfig.FromToml("[tool.ruff]\ntarget-version = \"py311\"\n")!.ParenthesizedWith);
    }

    [Fact]
    public void TheCommentEndingASplitStatementEndsItAfterTheParenthesis()
    {
        string call = "session.query(models.Customer.id).filter(models.Customer.account_id == 10000, models.Customer.email == email_address,).count()";
        string split = "result = (\n    session.query(models.Customer.id)\n    .filter(\n        models.Customer.account_id == 10000,\n        models.Customer.email == email_address,\n    )\n    .count()\n)";
        Same($"result = {call}  # note\n", split + "  # note\n");
        Same($"result = {call}  # type: int\n", split + "  # type: int\n");
        Same("class D:\n    def __init__(self):\n        self.force_into = False  # Force Step Into: this one step may stop in library code\n        self.value = compute(alpha, beta)  # a long comment that makes this line too long for black\n",
             "class D:\n    def __init__(self):\n        self.force_into = (\n            False  # Force Step Into: this one step may stop in library code\n        )\n        self.value = compute(\n            alpha, beta\n        )  # a long comment that makes this line too long for black\n");
    }

    [Fact]
    public void ATypeIgnoreAloneDoesNotSplitALine()
    {
        string line = "call_something_long_named(argument_number_one, argument_number_two, argument_three)";
        Same(line + "  # type: ignore\n", line + "  # type: ignore\n");
        Same(line + "  # type: ignore[attr]\n", line + "  # type: ignore[attr]\n");
        Same(line + "  # noqa: E501\n", "call_something_long_named(\n    argument_number_one, argument_number_two, argument_three\n)  # noqa: E501\n");
    }

    [Fact]
    public void DummyImplementationsStayOnTheirDefLine()
    {
        Same("class Proto:\n    ...\n\n\ndef f():\n    ...\n\n\ndef g():\n    ...  # comment\n\n\ndef h():\n    \"\"\"Doc.\"\"\"\n    ...\n\n\nif x:\n    ...\n",
             "class Proto: ...\n\n\ndef f(): ...\n\n\ndef g(): ...  # comment\n\n\ndef h():\n    \"\"\"Doc.\"\"\"\n    ...\n\n\nif x:\n    ...\n");
        Same("@overload\ndef k(a: int) -> int:\n    ...\n@overload\ndef k(a: str) -> str:\n    ...\ndef k(a):\n    return a\n",
             "@overload\ndef k(a: int) -> int: ...\n@overload\ndef k(a: str) -> str: ...\ndef k(a):\n    return a\n");
    }

    [Fact]
    public void CaseKeepsItsSpaceAndALambdasParametersAreNotItems()
    {
        Same("match args:\n    case [\"partialdef\"]:\n        pass\n    case (a, b):\n        pass\n", "match args:\n    case [\"partialdef\"]:\n        pass\n    case (a, b):\n        pass\n");
        Same("def f(internal_type):\n    if internal_type == \"FloatField\":\n        return lambda value, expression, connection: (\n            None if value is None else float(value)\n        )\n",
             "def f(internal_type):\n    if internal_type == \"FloatField\":\n        return lambda value, expression, connection: (\n            None if value is None else float(value)\n        )\n");
    }
}
