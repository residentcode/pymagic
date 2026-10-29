using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>What goes wrong in a FastAPI app without a message (item 126): a route nothing reaches, a dependency passed as its result, a call that freezes the event loop.</summary>
public class FastApiInspectionTests
{
    const string Main = "file:///c:/proj/main.py";

    static LanguageService Open(string src, params (string Path, string Text)[] more)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        foreach (var (p, t) in more) svc.Open("file:///c:/proj/" + p, t, 1);
        svc.Open(Main, src, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(string src, string code, params (string Path, string Text)[] more) => Open(src, more).Diagnostics(Main).Where(d => d.Code == code).ToList();

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static string Fix(string title, string src, params (string Path, string Text)[] more)
    {
        var svc = Open(src, more);
        var all = svc.CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).ToList();
        var a = all.FirstOrDefault(x => x.Title.StartsWith(title));
        if (a == null) throw new AssertionException($"no action '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
        var lines = new LineMap(src);
        string text = src;
        foreach (var e in a.Edit!.Changes[Main].OrderByDescending(e => lines.OffsetOf(e.Range.Start)).ThenByDescending(e => lines.OffsetOf(e.Range.End)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    // ------------------------------------------------------------ which path covers which

    [Fact]
    public void AParameterCoversAFixedSegment()
    {
        Assert.True(LanguageService.PathCovers("/items/{id}", "/items/me"));
        Assert.True(LanguageService.PathCovers("/a/{x}/c", "/a/b/c"));
        Assert.True(LanguageService.PathCovers("/{a}/{b}", "/x/{b}"));
    }

    [Fact]
    public void TheOtherWayRoundAndTheSamePathAreNotCovering()
    {
        Assert.True(!LanguageService.PathCovers("/items/me", "/items/{id}"));
        Assert.True(!LanguageService.PathCovers("/items/{id}", "/items/{id}"));
        Assert.True(!LanguageService.PathCovers("/items/{id}", "/items/{other}"));
        Assert.True(!LanguageService.PathCovers("/users/{id}", "/users/{id}/posts"));
        Assert.True(!LanguageService.PathCovers("/a/{x}", "/a/b/c"));
        Assert.True(!LanguageService.PathCovers("/", "/x"));
        Assert.True(!LanguageService.PathCovers("/items", "/items/{id}"));
    }

    [Fact]
    public void ConvertersLimitWhatIsCovered()
    {
        Assert.True(LanguageService.PathCovers("/items/{id:int}", "/items/42"));
        Assert.True(!LanguageService.PathCovers("/items/{id:int}", "/items/me"));
        Assert.True(LanguageService.PathCovers("/files/{p:path}", "/files/a/b"));
        Assert.True(LanguageService.PathCovers("/files/{p:path}", "/files/a"));
        Assert.True(LanguageService.PathCovers("/items/{id}", "/items/{id:int}"));
        Assert.True(!LanguageService.PathCovers("/items/{id:int}", "/items/{id}"));
    }

    // ------------------------------------------------------------ route-shadowed

    const string Head = "from fastapi import FastAPI\n\napp = FastAPI()\n\n";

    [Fact]
    public void AFixedPathAfterOneWithAParameterIsNeverReached()
    {
        var d = Found(Head + Src("@app.get('/items/{item_id}')", "def item(item_id): ...", "", "@app.get('/items/me')", "def me(): ..."), "route-shadowed");
        Assert.Equal(1, d.Count);
        Assert.Contains("never reached", d[0].Message);
        Assert.Contains("'/items/{item_id}'", d[0].Message);
        Assert.Equal(7, d[0].Range.Start.Line);   // the path of the second route
    }

    [Fact]
    public void TheRightOrderIsFine()
    {
        Assert.Equal(0, Found(Head + Src("@app.get('/items/me')", "def me(): ...", "", "@app.get('/items/{item_id}')", "def item(item_id): ..."), "route-shadowed").Count);
    }

    [Fact]
    public void AnotherMethodDoesNotShadow()
    {
        Assert.Equal(0, Found(Head + Src("@app.get('/items/{item_id}')", "def item(item_id): ...", "", "@app.post('/items/me')", "def me(): ..."), "route-shadowed").Count);
        Assert.Equal(1, Found(Head + Src("@app.api_route('/items/{item_id}', methods=['GET', 'POST'])", "def item(item_id): ...", "", "@app.post('/items/me')", "def me(): ..."), "route-shadowed").Count);
    }

    [Fact]
    public void ARouterPrefixIsPartOfThePath()
    {
        string src = "from fastapi import FastAPI, APIRouter\n\napp = FastAPI()\nrouter = APIRouter(prefix='/items')\n\n\n@router.get('/{item_id}')\ndef item(item_id): ...\n\n\n@router.get('/me')\ndef me(): ...\n\n\napp.include_router(router)\n";
        Assert.Equal(1, Found(src, "route-shadowed").Count);
    }

    [Fact]
    public void FlaskRanksFixedRulesFirstWhateverTheOrder()
    {
        string src = "from flask import Flask\n\napp = Flask(__name__)\n\n\n@app.route('/items/<item_id>')\ndef item(item_id): ...\n\n\n@app.route('/items/me')\ndef me(): ...\n";
        Assert.Equal(0, Found(src, "route-shadowed").Count);
    }

    // ------------------------------------------------------------ depends-called

    const string Deps = "from fastapi import Depends\n\n\ndef get_db():\n    db = {}\n    yield db\n\n\n";

    [Fact]
    public void ADependencyThatWasCalledIsReported()
    {
        var d = Found(Deps + Src("def endpoint(db=Depends(get_db())): ..."), "depends-called");
        Assert.Equal(1, d.Count);
        Assert.Contains("get_db()", d[0].Message);
    }

    [Fact]
    public void TheFunctionItselfIsFine()
    {
        Assert.Equal(0, Found(Deps + Src("def endpoint(db=Depends(get_db)): ..."), "depends-called").Count);
    }

    [Fact]
    public void CallablesThatMakeDependenciesAreFine()
    {
        string src = Deps + Src(
            "class Checker:", "    def __init__(self, scope): ...", "    def __call__(self, token: str): ...", "",
            "def require(scope):", "    def inner(token: str): ...", "    return inner", "",
            "def a(x=Depends(Checker('me'))): ...", "def b(x=Depends(require('me'))): ...");
        Assert.Equal(0, Found(src, "depends-called").Count);
    }

    [Fact]
    public void AnAsyncDependencyThatWasCalledIsReportedToo()
    {
        Assert.Equal(1, Found("from fastapi import Depends\n\n\nasync def current(): ...\n\n\ndef e(u=Depends(current())): ...\n", "depends-called").Count);
    }

    [Fact]
    public void TheCallIsTakenOffByTheFix()
    {
        string src = Deps + Src("def endpoint(db=Depends(get_db())): ...");
        Assert.Equal(Deps + Src("def endpoint(db=Depends(get_db)): ..."), Fix("Pass the function itself", src));
    }

    // ------------------------------------------------------------ async-blocking-call

    [Fact]
    public void SleepInAnAsyncFunctionIsReported()
    {
        var d = Found(Src("import time", "", "", "async def handler():", "    time.sleep(1)"), "async-blocking-call");
        Assert.Equal(1, d.Count);
        Assert.Contains("'time.sleep'", d[0].Message);
        Assert.Contains("asyncio.sleep", d[0].Message);
    }

    [Fact]
    public void SleepImportedByNameIsReportedToo()
    {
        Assert.Equal(1, Found(Src("from time import sleep", "", "", "async def handler():", "    sleep(1)"), "async-blocking-call").Count);
    }

    [Fact]
    public void ARequestsCallIsReported()
    {
        var requests = ("requests/__init__.py", "def get(url): ...\n\n\ndef post(url, data=None): ...\n");
        var d = Found(Src("import requests", "", "", "async def handler():", "    return requests.get('http://x')"), "async-blocking-call", requests);
        Assert.Equal(1, d.Count);
        Assert.Contains("'requests.get'", d[0].Message);
    }

    [Fact]
    public void WhatDoesNotBlockIsLeftAlone()
    {
        string src = Src(
            "import asyncio", "import time", "", "",
            "def plain():", "    time.sleep(1)", "", "",
            "async def a():", "    await asyncio.sleep(1)", "    await asyncio.to_thread(time.sleep, 1)", "    loop = asyncio.get_running_loop()",
            "    await loop.run_in_executor(None, time.sleep, 1)", "",
            "    def inner():", "        time.sleep(1)", "", "    return inner");
        Assert.Equal(0, Found(src, "async-blocking-call").Count);
    }

    [Fact]
    public void SleepBecomesAsyncioSleepWithItsImport()
    {
        string src = Src("import time", "", "", "async def handler():", "    time.sleep(1)");
        Assert.Equal(Src("import time", "import asyncio", "", "", "async def handler():", "    await asyncio.sleep(1)"), Fix("Use 'await asyncio.sleep", src));
    }

    [Fact]
    public void TheImportIsNotAddedTwice()
    {
        string src = Src("import asyncio", "import time", "", "", "async def handler():", "    time.sleep(1)");
        Assert.Equal(Src("import asyncio", "import time", "", "", "async def handler():", "    await asyncio.sleep(1)"), Fix("Use 'await asyncio.sleep", src));
    }
}
