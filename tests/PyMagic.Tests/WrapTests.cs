using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>Line wrapping and magic trailing commas; the expected texts are what black produces.</summary>
public class WrapTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static string Fmt(string text) =>
        PythonFormatter.Format(text) ?? throw new AssertionException("the formatter declined (" + PythonFormatter.LastMismatch + "):\n" + text);

    static void Same(string expected, string input)
    {
        string once = Fmt(input);
        Assert.Equal(expected, once);
        Assert.Equal(expected, Fmt(once));   // and a second pass changes nothing
    }

    [Fact]
    public void ACallThatDoesNotFitPutsItsArgumentsOnOneIndentedLine()
    {
        Same(Src("result = some_function_name(", "    a_long_argument_one, a_long_argument_two, a_long_argument_three", ")"),
            Src("result = some_function_name(a_long_argument_one, a_long_argument_two, a_long_argument_three, a_long_argument_four)").Replace(", a_long_argument_four", ""));
    }

    [Fact]
    public void ArgumentsGoOnePerLineWithATrailingCommaWhenOneLineIsNotEnough()
    {
        Same(Src("result = some_function_name(", "    a_long_argument_one,", "    a_long_argument_two,", "    a_long_argument_three,", "    a_long_argument_four,", ")"),
            Src("result = some_function_name(a_long_argument_one, a_long_argument_two, a_long_argument_three, a_long_argument_four)"));
    }

    [Fact]
    public void AMagicTrailingCommaKeepsACollectionExploded()
    {
        Same(Src("foo(", "    a,", "    b,", ")"), Src("foo(a, b,)"));
        Same(Src("x = [", "    1,", "    2,", "]"), Src("x = [", "    1,", "    2,", "]"));
        Same(Src("d = {", "    \"a\": 1,", "}"), Src("d = {'a': 1,}"));
    }

    [Fact]
    public void OneElementTuplesAndSubscriptsAreNotMagic()
    {
        Same(Src("t = (1,)", "x = y[1,]"), Src("t = (1,)", "x = y[1,]"));
    }

    [Fact]
    public void ParametersOfADefAreSplitOnTheirOwn()
    {
        Same(Src("def function_with_many_parameters(", "    first_parameter, second_parameter, third_parameter, fourth_param", "):", "    pass"),
            Src("def function_with_many_parameters(first_parameter, second_parameter, third_parameter, fourth_param):", "    pass"));
        Same(Src("def f(", "    a_very_long_parameter_name_that_goes_on_and_on_and_on_and_on_and_on_and_on_and_on_and_on,", "):", "    pass"),
            Src("def f(a_very_long_parameter_name_that_goes_on_and_on_and_on_and_on_and_on_and_on_and_on_and_on):", "    pass"));
    }

    [Fact]
    public void ListsAndDictsAreExplodedOneItemPerLine()
    {
        Same(Src("values = [", "    first_value_in_list,", "    second_value_in_list,", "    third_value_in_list,", "    fourth_value_in_list,", "]"),
            Src("values = [first_value_in_list, second_value_in_list, third_value_in_list, fourth_value_in_list]"));
        Same(Src("config = {", "    \"first_key\": first_value,", "    \"second_key\": second_value,", "    \"third_key\": third_value,", "    \"fourth\": 4,", "}"),
            Src("config = {'first_key': first_value, 'second_key': second_value, 'third_key': third_value, 'fourth': 4}"));
    }

    [Fact]
    public void BooleanConditionsGetParenthesesAndOneOperandPerLine()
    {
        Same(Src("if (", "    first_condition_is_long", "    and second_condition_is_also_long", "    and third_condition_is_long_too", "    and fourth_condition", "):", "    pass"),
            Src("if first_condition_is_long and second_condition_is_also_long and third_condition_is_long_too and fourth_condition:", "    pass"));
    }

    [Fact]
    public void ArithmeticOnTheRightOfAnAssignmentIsWrappedInParentheses()
    {
        Same(Src("total = (", "    first_value_that_is_long", "    + second_value_that_is_long", "    + third_value_that_is_long", "    + fourth_value_that_is_long", ")"),
            Src("total = first_value_that_is_long + second_value_that_is_long + third_value_that_is_long + fourth_value_that_is_long"));
        Same(Src("total = (", "    first_value_that_is_long + second_value_that_is_long + third_value_that_is_longer", ")"),
            Src("total = first_value_that_is_long + second_value_that_is_long + third_value_that_is_longer"));
    }

    [Fact]
    public void NestedCallsSplitFromTheOutsideIn()
    {
        Same(Src("def f():", "    return some_function(", "        argument_one,", "        another_function(inner_argument_one, inner_argument_two, inner_argument_three),", "        argument_three,", "    )"),
            Src("def f():", "    return some_function(argument_one, another_function(inner_argument_one, inner_argument_two, inner_argument_three), argument_three)"));
        Same(Src("foo(", "    bar(", "        a,", "        b,", "    ),", "    c,", ")"), Src("foo(bar(a, b,), c)"));
    }

    [Fact]
    public void ComprehensionsSplitBeforeForAndIf()
    {
        Same(Src("result = [", "    function_applied_to(element)", "    for element in some_long_collection_of_elements", "    if element > threshold_value", "]"),
            Src("result = [function_applied_to(element) for element in some_long_collection_of_elements if element > threshold_value]"));
    }

    [Fact]
    public void ChainedCallsBecomeOneCallPerLine()
    {
        Same(Src("result = (", "    session.query(models.Customer.id)", "    .filter(models.Customer.account_id == 10000)", "    .filter(models.Customer.email == email_address)", "    .count()", ")"),
            Src("result = session.query(models.Customer.id).filter(models.Customer.account_id == 10000).filter(models.Customer.email == email_address).count()"));
    }

    [Fact]
    public void LongImportsGetParenthesesAndShortOnesLoseThem()
    {
        Same(Src("from package.module import (", "    name_five,", "    name_four,", "    name_one,", "    name_seven,", "    name_six,", "    name_three,", "    name_two,", ")").Replace("name_five,\n    name_four,\n    name_one,\n    name_seven,\n    name_six,\n    name_three,\n    name_two,", "name_one,\n    name_two,\n    name_three,\n    name_four,\n    name_five,\n    name_six,\n    name_seven,"),
            Src("from package.module import name_one, name_two, name_three, name_four, name_five, name_six, name_seven"));
        Same(Src("from a import b, c"), Src("from a import (b, c)"));
        Same(Src("from a import (", "    b,", "    c,", ")"), Src("from a import (b, c,)"));
    }

    [Fact]
    public void WhatCannotBeSplitIsLeftAlone()
    {
        string longString = "message = \"" + new string('x', 100) + "\"";
        Same(Src(longString), Src(longString));
        string noBrackets = "value = " + new string('y', 95);
        Same(Src(noBrackets), Src(noBrackets));
    }

    [Fact]
    public void CommentsAndTrailingCommentsSurvive()
    {
        Same(Src("result = some_function_name(", "    a_long_argument_one, a_long_argument_two, a_long_argument_three", ")  # why", "x = 1  # short"),
            Src("result = some_function_name(a_long_argument_one, a_long_argument_two, a_long_argument_three, a_long_argument_four)  # why", "x = 1 # short").Replace(", a_long_argument_four", ""));
        // a comment inside brackets keeps the lines as they were
        string keep = Src("x = foo(", "    a,  # first", "    b,", ")");
        Same(keep, keep);
    }

    [Fact]
    public void HandWrappedCodeThatFitsIsJoined()
    {
        Same(Src("x = foo(1, 2, 3)", "y = [1, 2]"), Src("x = foo(1,", "        2,", "        3)", "y = [1,", "     2]"));
    }

    [Fact]
    public void ATargetTupleIsSplitInsteadOfWrappingTheValue()
    {
        Same(Src("(", "    first_name_here,", "    second_name_here,", "    third_name_here,", "    fourth_name_here,", "    fifth_name_here,", ") = values"),
            Src("(first_name_here, second_name_here, third_name_here, fourth_name_here, fifth_name_here) = values"));
    }

    [Fact]
    public void ASingleLongArgumentGetsNoTrailingComma()
    {
        Same(Src("print(", "    this_is_a_very_long_argument_name_for_testing_the_wrapper_behaviour_of_a_single_argument", ")"),
            Src("print(this_is_a_very_long_argument_name_for_testing_the_wrapper_behaviour_of_a_single_argument)"));
    }

    [Fact]
    public void ReturnedTuplesAndTernariesAreWrappedInParentheses()
    {
        Same(Src("def f():", "    return (", "        first_value_in_the_tuple_is_long,", "        second_value_in_the_tuple_is_long,", "        third_value_in_tuple,", "    )"),
            Src("def f():", "    return first_value_in_the_tuple_is_long, second_value_in_the_tuple_is_long, third_value_in_tuple"));
        Same(Src("value = (", "    first_option_expression", "    if some_condition_holds_true_here", "    else second_option_expression_x", ")"),
            Src("value = first_option_expression if some_condition_holds_true_here else second_option_expression_x"));
    }
}
