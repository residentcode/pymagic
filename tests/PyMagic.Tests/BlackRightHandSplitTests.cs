using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>
/// Black 24-26's way of splitting a line (item 200), each expected text produced by black 26.10.1 on the input: optional
/// parentheses left out when one delimiter reads well without them, conditional expressions and annotations in parentheses,
/// `%` and implicitly joined strings, tuple targets, chained assignments and call chains, backslash continuations joined,
/// `# type: ignore`, comments after an operator, the trailing comma after `*args` for Python 3.6 code, exponents.
/// </summary>
public class BlackRightHandSplitTests
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
    public void OmitsOptionalParenthesesWhenOneDelimiter()
    {
        Same("def f():\n    if self.isDefault_majloc and not (info.majloc is not None and self.major.locator != info.majloc_value):\n        pass\n    if info.majloc is not None and self.major.locator != info.majloc and self.isDefault_majloc:\n        pass\n",
             "def f():\n    if self.isDefault_majloc and not (\n        info.majloc is not None and self.major.locator != info.majloc_value\n    ):\n        pass\n    if (\n        info.majloc is not None\n        and self.major.locator != info.majloc\n        and self.isDefault_majloc\n    ):\n        pass\n");
    }

    [Fact]
    public void ConditionalExpressionsGetParentheses()
    {
        Same("config = {\n    \"key\": some_function_name(argument_one) if condition_value else other_function(argument_two),\n}\ncall_something(first_argument, value_for_true if some_long_condition_name else value_for_false_case)\n",
             "config = {\n    \"key\": (\n        some_function_name(argument_one)\n        if condition_value\n        else other_function(argument_two)\n    ),\n}\ncall_something(\n    first_argument, value_for_true if some_long_condition_name else value_for_false_case\n)\n");
    }

    [Fact]
    public void AnnotationsGetParentheses()
    {
        Same("def plot(self, data, orient: Literal[\"vertical\", \"horizontal\", \"both_directions\"] | None = None):\n    pass\ndef very_long_function_name_for_testing() -> Set[\"Muy\", \"Long\", \"Annotation\", \"Here\", \"Okay\"]:\n    pass\ndef xxxxxxxxxxxxxxxxxxxxxxxxxxxx(a, b) -> Set[\"Muy\", \"Long\", \"Annotation\", \"Here\", \"Okay\"]:\n    pass\n",
             "def plot(\n    self,\n    data,\n    orient: Literal[\"vertical\", \"horizontal\", \"both_directions\"] | None = None,\n):\n    pass\n\n\ndef very_long_function_name_for_testing() -> (\n    Set[\"Muy\", \"Long\", \"Annotation\", \"Here\", \"Okay\"]\n):\n    pass\n\n\ndef xxxxxxxxxxxxxxxxxxxxxxxxxxxx(\n    a, b\n) -> Set[\"Muy\", \"Long\", \"Annotation\", \"Here\", \"Okay\"]:\n    pass\n");
    }

    [Fact]
    public void PercentAndJoinedStrings()
    {
        Same("def f():\n    message = \"the value %s was not found in the collection %s of the object\" % (value, name)\n    raise ValueError(\"This database backend does not support specifying an order on \" \"aggregates.\")\n    text = \"Use 'through' to create a separate model for \" \"M2M and then set column_name.\"\n",
             "def f():\n    message = \"the value %s was not found in the collection %s of the object\" % (\n        value,\n        name,\n    )\n    raise ValueError(\n        \"This database backend does not support specifying an order on \" \"aggregates.\"\n    )\n    text = (\n        \"Use 'through' to create a separate model for \" \"M2M and then set column_name.\"\n    )\n");
    }

    [Fact]
    public void TupleTargetsAndEmptyCollections()
    {
        Same("(x, y) = compute()\nfirst_long_target_name, second_long_target_name, third_long_target_name = function_name(argument)\nregistry = {}  # a long comment that makes this line longer than the limit of black, it goes on\n",
             "x, y = compute()\nfirst_long_target_name, second_long_target_name, third_long_target_name = function_name(\n    argument\n)\nregistry = (\n    {}\n)  # a long comment that makes this line longer than the limit of black, it goes on\n");
    }

    [Fact]
    public void ChainedAssignmentsAndCallChains()
    {
        Same("units.registry[np.datetime64] = units.registry[datetime.date] = units.registry[datetime.datetime] = Conv()\nresult = transforms.Affine2D().scale(width * 0.5, height * 0.5).rotate_deg(self.angle).translate(x, y)\n",
             "units.registry[np.datetime64] = units.registry[datetime.date] = units.registry[\n    datetime.datetime\n] = Conv()\nresult = (\n    transforms.Affine2D()\n    .scale(width * 0.5, height * 0.5)\n    .rotate_deg(self.angle)\n    .translate(x, y)\n)\n");
    }

    [Fact]
    public void BackslashContinuationsAreJoined()
    {
        Same("x = 1 + \\\n    2\nif a and \\\n   b:\n    pass\nyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy = zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.wwwwwwwwwwwwwwwwwwwwwwww \\\n    + qqqqqqqqqqqqqqqqqqqqq\n",
             "x = 1 + 2\nif a and b:\n    pass\nyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy = (\n    zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.wwwwwwwwwwwwwwwwwwwwwwww\n    + qqqqqqqqqqqqqqqqqqqqq\n)\n");
    }

    [Fact]
    public void TypeIgnoreAndOperatorComments()
    {
        Same("def f():\n    result = result.where(cond, lower, inplace=inplace)  # type: ignore[assignment]\n    func, columns, order = normalize_keyword_aggregation(kwargs)  # type: ignore[assignment]\nRGBAColorType = (\n    str |  # \"none\" or \"#RRGGBBAA\"/\"#RGBA\" hex strings\n    tuple[float, float, float, float] |\n    tuple[str, float]  # (color, alpha) pairs\n)\n",
             "def f():\n    result = result.where(cond, lower, inplace=inplace)  # type: ignore[assignment]\n    func, columns, order = normalize_keyword_aggregation(kwargs)  # type: ignore[assignment]\n\n\nRGBAColorType = (\n    str  # \"none\" or \"#RRGGBBAA\"/\"#RGBA\" hex strings\n    | tuple[float, float, float, float]\n    | tuple[str, float]  # (color, alpha) pairs\n)\n");
    }

    [Fact]
    public void StarArgumentsGetACommaOnlyFor36()
    {
        Same("call_function_with_long_name(argument_number_one, argument_number_two, argument_number_three, argument_four, *args, **kwargs)\n",
             "call_function_with_long_name(\n    argument_number_one,\n    argument_number_two,\n    argument_number_three,\n    argument_four,\n    *args,\n    **kwargs\n)\n");
    }

    [Fact]
    public void StarArgumentsGetACommaInCodeFor36()
    {
        Same("x = f\"{y}\"\ncall_function_with_long_name(argument_number_one, argument_number_two, argument_number_three, argument_four, *args, **kwargs)\n",
             "x = f\"{y}\"\ncall_function_with_long_name(\n    argument_number_one,\n    argument_number_two,\n    argument_number_three,\n    argument_four,\n    *args,\n    **kwargs,\n)\n");
    }

    [Fact]
    public void ExponentsLoseTheirPlusSign()
    {
        Same("x = [1.00000000e+00, 2.5E+10, 3e-05]\n",
             "x = [1.00000000e00, 2.5e10, 3e-05]\n");
    }

    // ------------------------------------------------------------ item 218: lines that hold a multi-line string (black 26's multiline_string_handling)

    [Fact]
    public void AMultilineStringAmongArgumentsSplitsTheCall()
    {
        Same("call(arg, \"\"\"\ntext\n\"\"\")\n", "call(\n    arg,\n    \"\"\"\ntext\n\"\"\",\n)\n");
        Same("parser.add_argument(\"--flag\", help=\"\"\"Some help\nthat goes on.\"\"\")\n",
             "parser.add_argument(\n    \"--flag\",\n    help=\"\"\"Some help\nthat goes on.\"\"\",\n)\n");
        Same("cursor.execute(\"\"\"\n    SELECT 1\n\"\"\", (a, b))\n", "cursor.execute(\n    \"\"\"\n    SELECT 1\n\"\"\",\n    (a, b),\n)\n");
        Same("register(name=\"n\", doc=\"\"\"\ndoc\n\"\"\", extra=1)\n", "register(\n    name=\"n\",\n    doc=\"\"\"\ndoc\n\"\"\",\n    extra=1,\n)\n");
    }

    [Fact]
    public void AMultilineStringOnItsOwnStaysAsWritten()
    {
        // the sole argument is hugged, an assignment, a method of the string, an assert message and `%` stay on their lines
        Same("call(\"\"\"\ntext\n\"\"\")\n", "call(\"\"\"\ntext\n\"\"\")\n");
        Same("def f():\n    return textwrap.dedent(\"\"\"\\\n        hello\n    \"\"\")\n", "def f():\n    return textwrap.dedent(\"\"\"\\\n        hello\n    \"\"\")\n");
        Same("x = \"\"\"\nabc\n\"\"\"\n", "x = \"\"\"\nabc\n\"\"\"\n");
        Same("y = \"\"\"\na {}\n\"\"\".format(1)\n", "y = \"\"\"\na {}\n\"\"\".format(1)\n");
        Same("assert some_condition(value), \"\"\"\nmessage\n\"\"\"\n", "assert some_condition(value), \"\"\"\nmessage\n\"\"\"\n");
        Same("print(\"\"\"a\nb\"\"\" % x)\n", "print(\"\"\"a\nb\"\"\" % x)\n");
        // already split by hand with a trailing comma: kept so
        Same("w = [\n    \"\"\"\nitem\n\"\"\",\n]\n", "w = [\n    \"\"\"\nitem\n\"\"\",\n]\n");
    }
}
