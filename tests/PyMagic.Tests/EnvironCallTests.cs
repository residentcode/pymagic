using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>django-environ's `env('NAME', cast=..., default=...)` is what its cast, scheme or default says, not every type its untyped body can make.</summary>
public class EnvironCallTests
{
    // the shape of django-environ's Env: an untyped get_value that can return whatever a cast makes
    const string Environ = "class NoValue: ...\n\n\nclass Env:\n    NOTSET = NoValue()\n\n    def __init__(self, **scheme):\n        self.scheme = scheme\n\n    def __call__(self, var: str, cast=None, default=NOTSET, parse_default: bool = False):\n        return self.get_value(var, cast=cast, default=default)\n\n    def get_value(self, var, cast=None, default=NOTSET):\n        if cast is bool:\n            return True\n        if cast is list:\n            return [var]\n        if cast is tuple:\n            return (var,)\n        return default\n";

    static List<string> Types(string src)
    {
        var ws = new Workspace();
        ws.AddModule("environ.environ", Environ);
        ws.AddModule("environ", "from environ.environ import Env\n");
        var m = ws.AddModule("main", src);
        return AstChildren.Descendants(m.Ast).OfType<AssignStmt>().Where(a => a.Targets is [NameExpr { Name: var n }] && n.StartsWith('V'))
            .Select(a => $"{((NameExpr)a.Targets[0]).Name}: {ws.Evaluator.TypeOf(m, a.Value)}").ToList();
    }

    [Fact]
    public void ACallIsWhatItsCastSchemeOrDefaultSays()
    {
        var t = Types("import environ\n\nenv = environ.Env(DEBUG=(bool, False), HOSTS=list, PORT=(int, 8000))\n\nV1 = env('D_REFERER')\nV2 = env('DEBUG')\nV3 = env('HOSTS')\nV4 = env('PORT')\nV5 = env('X', default=None)\nV6 = env('X', default='a')\nV7 = env('X', cast=int)\nV8 = env('X', cast=float, default=None)\nV9 = env('X', default=False)\n");
        Assert.Equal("V1: str | V2: bool | V3: list[str] | V4: int | V5: str | None | V6: str | V7: int | V8: float | None | V9: bool", string.Join(" | ", t));
    }
}
