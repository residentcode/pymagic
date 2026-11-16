using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>
/// Values a library's types say more of than it does: a Django view's `action = None` that as_view(action=...) sets, and a bs4 Tag's
/// attribute that is never a list unless it is one BeautifulSoup splits (class, rel, ...).
/// </summary>
public class LibraryPlaceholderTests
{
    const string DjangoView = "class View:\n    @classmethod\n    def as_view(cls, **initkwargs): ...\n";

    const string Bs4Element = "from typing import Optional, Union\n\n\nclass AttributeValueList(list[str]): ...\n\n\n_AttributeValue = Union[str, AttributeValueList]\n\n\nclass Tag:\n    def get(self, key: str, default: Optional[_AttributeValue] = None) -> Optional[_AttributeValue]: ...\n    def __getitem__(self, key: str) -> _AttributeValue: ...\n    def select_one(self, selector: str) -> Optional[Tag]: ...\n";

    static List<string> Found(string src, out Workspace ws)
    {
        ws = new Workspace();
        ws.AddModule("django.views.generic.base", DjangoView);
        ws.AddModule("bs4.element", Bs4Element);
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void AViewAttributeLeftNoneForAsViewIsNotNone()
    {
        string src = "from django.views.generic.base import View\n\n\nclass Test(View):\n    action = None\n    other: str | None = None\n\n    async def get(self, request):\n        return await getattr(self, self.action)(request)\n\n    def post(self, request):\n        return getattr(self, self.other)\n\n\nclass Plain:\n    action = None\n\n    def get(self):\n        return getattr(self, self.action)\n";
        var found = Found(src, out _);
        // the annotated one and the one outside a view are still None for what they say
        Assert.Equal(2, found.Count(f => f.StartsWith("Expected type 'str'")));
        Assert.True(found.Any(f => f.Contains("got 'str | None'")), string.Join("; ", found));
        Assert.True(found.Any(f => f.Contains("got 'None'")), string.Join("; ", found));
    }

    [Fact]
    public void ATagAttributeBs4DoesNotSplitIsAStr()
    {
        string src = "from bs4.element import Tag\n\n\ndef f(t: Tag, d: dict[str, str]):\n    a = t.get('data-ajax')\n    a.split('=')\n    b = t['data-lc']\n    d.get(b)\n    c = t.get('class')\n    c.split()\n    e = t['rel']\n    d.get(e)\n";
        var found = Found(src, out _);
        Assert.True(found.Count == 2, string.Join("; ", found));
        Assert.True(found.Any(f => f == "Cannot find reference 'split' in 'AttributeValueList'"), string.Join("; ", found));
        Assert.True(found.Any(f => f.Contains("got 'str | AttributeValueList'")), string.Join("; ", found));
    }
}
