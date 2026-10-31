using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Completion of the strings a place takes (item 211): a dictionary's keys and the values of a `Literal[...]`, in a string and where a value begins.</summary>
public class LiteralCompletionTests
{
    const string Main = "file:///c:/proj/main.py";

    static List<CompletionItem> Items(string marked)
    {
        int at = marked.IndexOf('|');
        string src = marked.Remove(at, 1);
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc.Completion(Main, new LineMap(src).PositionOf(at)).ToList();
    }

    static List<string> Labels(string marked) => Items(marked).Select(i => i.Label).ToList();

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void TheKeysOfADictionaryInsideTheString()
    {
        string src = Src("config = {'host': 'x', 'port': 1}", "config['debug'] = True", "");
        var keys = Labels(src + "config['|");
        Assert.True(keys.SequenceEqual(new[] { "host", "port", "debug" }));
        Assert.True(Labels(src + "config[\"p|").Contains("port"));
        Assert.True(Labels(Src("opts = dict(verbose=True, level=2)", "opts['|")).SequenceEqual(new[] { "verbose", "level" }));
        // a string that indexes nothing known
        Assert.True(Labels(src + Src("other = {}", "other['|")).Count == 0);
    }

    [Fact]
    public void TheKeysQuotedWhereTheIndexBegins()
    {
        string src = Src("config = {'host': 'x', 'port': 1}", "");
        var items = Items(src + "config[|");
        Assert.Equal("\"host\"", items[0].Label);
        Assert.Equal("\"port\"", items[1].Label);
        Assert.Equal("\"host\"", items[0].InsertText);
        // names still come after them
        Assert.True(items.Any(i => i.Label == "config"));
    }

    [Fact]
    public void TheValuesOfALiteralWhereOneIsWanted()
    {
        string src = Src("from typing import Literal", "", "def open_file(path: str, mode: Literal['r', 'w', 'a'] = 'r') -> None:", "    pass", "");
        Assert.True(Labels(src + "open_file('x', '|").SequenceEqual(new[] { "r", "w", "a" }));
        Assert.True(Labels(src + "open_file('x', mode='|").SequenceEqual(new[] { "r", "w", "a" }));
        var quoted = Items(src + "open_file('x', mode=|");
        Assert.Equal("\"r\"", quoted[0].Label);
        Assert.Equal("\"a\"", quoted[2].Label);
        // an annotated assignment, a return and a comparison
        Assert.True(Labels(Src("from typing import Literal", "Color = Literal['red', 'green']", "c: Color = '|")).SequenceEqual(new[] { "red", "green" }));
        Assert.True(Labels(Src("from typing import Literal", "def pick() -> Literal['yes', 'no']:", "    return '|")).SequenceEqual(new[] { "yes", "no" }));
        Assert.True(Labels(Src("from typing import Literal", "def f(state: Literal['on', 'off']):", "    if state == '|")).SequenceEqual(new[] { "on", "off" }));
        // a header that already has its colon is not given another one
        Assert.True(Labels(src + "if True: open_file('x', mode='|").SequenceEqual(new[] { "r", "w", "a" }));
        // a plain str parameter has nothing to offer
        Assert.True(Labels(src + "open_file('|").Count == 0);
    }
}
