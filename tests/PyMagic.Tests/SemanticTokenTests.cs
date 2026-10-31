using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Semantic highlighting (item 197): what each name of a file is, as the editor colours it.</summary>
public class SemanticTokenTests
{
    const string Uri = "file:///c:/proj/app.py";

    /// <summary>The tokens as "text:type[.modifier...]", in the order of the file.</summary>
    static List<string> Tokens(string code)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open(Uri, code, 1);
        var data = svc.SemanticTokens(Uri);
        var lines = code.Split('\n');
        var result = new List<string>();
        int line = 0, ch = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            ch = data[i] == 0 ? ch + data[i + 1] : data[i + 1];
            string word = lines[line].Substring(ch, data[i + 2]);
            var mods = LanguageService.SemanticTokenModifiers.Where((_, b) => (data[i + 4] & (1 << b)) != 0);
            result.Add(string.Join(".", new[] { word + ":" + LanguageService.SemanticTokenTypes[data[i + 3]] }.Concat(mods)));
        }
        return result;
    }

    [Fact]
    public void EachKindOfNameHasItsToken()
    {
        string code =
            "import json\nimport os.path as osp\nfrom enum import Enum\nfrom dataclasses import dataclass\n\nLIMIT = 10\n\n\n" +
            "class Color(Enum):\n    RED = 1\n\n\n" +
            "@dataclass\nclass Shape:\n    name: str\n\n    def area(self, scale: float) -> float:\n        return len(self.name) * scale * LIMIT\n\n\n" +
            "async def run[T](item: T) -> T:\n    shape = Shape('a')\n    print(shape.area(2), Color.RED, json.dumps)\n    match item:\n        case _:\n            pass\n    return item\n";
        var t = Tokens(code);
        string Of(string word) => t.First(x => x.StartsWith(word + ":"));
        Assert.Equal("os:namespace", Of("os"));
        Assert.Equal("osp:namespace", Of("osp"));
        Assert.Equal("Enum:class", Of("Enum"));
        Assert.Equal("dataclass:decorator", t.First(x => x.StartsWith("dataclass:") && x.Contains("decorator")));
        Assert.Equal("LIMIT:variable.readonly.declaration", t.First(x => x.StartsWith("LIMIT:")));
        Assert.Equal("Color:enum.declaration", Of("Color"));
        Assert.Equal("Shape:class.declaration", Of("Shape"));
        Assert.Equal("area:method.declaration", Of("area"));
        Assert.Equal("self:selfParameter.declaration", Of("self"));
        Assert.Equal("scale:parameter.declaration", Of("scale"));
        Assert.True(t.Contains("len:function.defaultLibrary"), "len:function.defaultLibrary" + " not in: " + string.Join(" ", t));
        Assert.True(t.Contains("str:class.defaultLibrary"), "str:class.defaultLibrary" + " not in: " + string.Join(" ", t));
        Assert.True(t.Contains("name:property"), "name:property" + " not in: " + string.Join(" ", t));
        Assert.True(t.Contains("self:selfParameter"), "self:selfParameter" + " not in: " + string.Join(" ", t));
        Assert.Equal("run:function.async.declaration", Of("run"));
        Assert.Equal("T:typeParameter.declaration", Of("T"));
        Assert.True(t.Contains("shape:variable.declaration"), "shape:variable.declaration" + " not in: " + string.Join(" ", t));
        Assert.True(t.Contains("RED:enumMember.readonly"), "RED:enumMember.readonly" + " not in: " + string.Join(" ", t));
        Assert.True(t.Contains("match:softKeyword"), "match:softKeyword" + " not in: " + string.Join(" ", t));
        Assert.True(t.Contains("case:softKeyword"), "case:softKeyword" + " not in: " + string.Join(" ", t));
    }

    [Fact]
    public void ANameCalledTypeIsAVariableAndTheStatementIsAKeyword()
    {
        var t = Tokens("type Pair = tuple[int, int]\ntype = 3\nprint(type)\n");
        Assert.Equal("type:softKeyword", t[0]);
        Assert.Equal("Pair:class.declaration", t[1]);
        Assert.True(t.Count(x => x.StartsWith("type:variable")) >= 1, string.Join(" ", t));
    }
}
