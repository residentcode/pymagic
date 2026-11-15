using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Which PHP expressions a hover, a watch or an AI agent (over MCP) may have Xdebug run while the program is paused: the ones that change nothing.</summary>
public class PhpEvaluateTests
{
    [Fact]
    public void AnExpressionThatOnlyReadsIsRunAndOneThatMayChangeTheProgramIsNot()
    {
        foreach (var e in new[] { "$w * $h + 1", "$user->name . ' ' . $user?->last", "$items['a'][0] ?? 'none'", "count", "$a === null || !$b", "($x + 2) / 3",
                     "$price > 10 ? 'dear' : 'cheap'", "\"plain text\"", "2 ** 8", "$a <=> $b", "true and $x", "[1, 2, $x]" })
            Assert.True(e == "count" ? !XdebugBridge.ExpressionChangesNothing(e) : XdebugBridge.ExpressionChangesNothing(e), e);
        foreach (var e in new[] { "$x = 1", "$x++", "--$x", "$a .= 'x'", "$total += 1", "count($items)", "$obj->save()", "$f()", "($g)()", "new Item()", "`ls`",
                     "include 'x.php'", "$$name", "\"$x\"", "Foo::bar()", "$o::run()", "exit", "", "echo 1", "(int) $x", "$a; $b" })
            Assert.True(!XdebugBridge.ExpressionChangesNothing(e), e);
    }
}
