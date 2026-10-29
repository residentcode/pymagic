using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>What an endpoint takes and gives (item 126), read from its code: where each parameter comes from, the response, the dependencies behind it.</summary>
public class FastApiEndpointTests
{
    const string Main = "file:///c:/proj/main.py";

    const string FastApiStub =
        "class FastAPI:\n    def get(self, path, **kw): ...\n    def post(self, path, **kw): ...\n\n\nclass Request: ...\n\n\nclass UploadFile: ...\n\n\n" +
        "def Depends(dependency=None, **kw): ...\n\n\ndef Query(default=..., **kw): ...\n\n\ndef Header(default=..., **kw): ...\n\n\ndef Security(dependency=None, **kw): ...\n";

    static IReadOnlyList<RouteEntry> RoutesOf(string code)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open("file:///c:/proj/fastapi/__init__.py", FastApiStub, 1);
        svc.Open("file:///c:/proj/pydantic/__init__.py", "class BaseModel: ...\n", 1);
        svc.Open(Main, code, 1);
        return svc.Routes();
    }

    const string Head =
        "from typing import Annotated\nfrom fastapi import FastAPI, Depends, Query, Header, Request, UploadFile, Security\nfrom pydantic import BaseModel\n\napp = FastAPI()\n\n\n" +
        "class Item(BaseModel):\n    name: str\n\n\nclass ItemOut(BaseModel):\n    id: int\n\n\ndef get_db():\n    yield {}\n\n\n" +
        "def current_user(token: Annotated[str, Header()], db=Depends(get_db)): ...\n\n\nDB = Annotated[dict, Depends(get_db)]\n\n\n";

    const string Create =
        "@app.post('/items/{item_id}', response_model=ItemOut, status_code=201, tags=['items'], dependencies=[Depends(get_db)])\n" +
        "async def create(item_id: int, item: Item, request: Request, db: DB, q: Annotated[str | None, Query(max_length=50)] = None, page: int = 1,\n" +
        "                 size: int = Query(10, ge=1), tag: str = Query(...), user=Depends(current_user), upload: UploadFile = None): ...\n";

    static RouteEntry Create_() => RoutesOf(Head + Create).Single(r => r.Name == "create");

    static RouteParam P(RouteEntry e, string name) => e.Params!.Single(p => p.Name == name);

    [Fact]
    public void WhereEachParameterComesFrom()
    {
        var e = Create_();
        Assert.Equal("path", P(e, "item_id").Kind);
        Assert.Equal("body", P(e, "item").Kind);
        Assert.Equal("query", P(e, "q").Kind);
        Assert.Equal("query", P(e, "page").Kind);
        Assert.Equal("query", P(e, "size").Kind);
        Assert.Equal("query", P(e, "tag").Kind);
        Assert.Equal("file", P(e, "upload").Kind);
        Assert.Equal("dependency", P(e, "user").Kind);
    }

    [Fact]
    public void WhatFastApiHandsOverItselfIsLeftOut()
    {
        Assert.True(!Create_().Params!.Any(p => p.Name == "request"));
    }

    [Fact]
    public void RequiredAndOptional()
    {
        var e = Create_();
        Assert.True(P(e, "item_id").Required);
        Assert.True(P(e, "item").Required);
        Assert.True(P(e, "tag").Required);          // Query(...)
        Assert.True(!P(e, "q").Required);           // = None
        Assert.True(!P(e, "page").Required);        // = 1
        Assert.True(!P(e, "size").Required);        // Query(10, ...)
        Assert.True(!P(e, "upload").Required);
    }

    [Fact]
    public void TypesAndConstraints()
    {
        var e = Create_();
        Assert.Equal("int", P(e, "item_id").Type);
        Assert.Equal("Item", P(e, "item").Type);
        Assert.Equal("str | None", P(e, "q").Type);
        Assert.Equal("max_length=50", P(e, "q").Detail);
        Assert.Equal("ge=1", P(e, "size").Detail);
        Assert.True(P(e, "page").Detail == null);
    }

    [Fact]
    public void TheResponseTheStatusAndTheTags()
    {
        var e = Create_();
        Assert.Equal("ItemOut", e.Response);
        Assert.Equal("201", e.Status);
        Assert.Equal("items", string.Join(",", e.Tags!));
    }

    [Fact]
    public void ADependencyShowsWhatItTakesInTurn()
    {
        var user = P(Create_(), "user");
        Assert.Equal("current_user", user.Detail);
        Assert.Equal(2, user.Children!.Count);
        Assert.Equal("header", user.Children[0].Kind);
        Assert.Equal("token", user.Children[0].Name);
        Assert.True(user.Children[0].Required);
        Assert.Equal("dependency", user.Children[1].Kind);
        Assert.Equal("get_db", user.Children[1].Detail);
    }

    [Fact]
    public void AnAnnotatedAliasIsFollowed()
    {
        var db = P(Create_(), "db");
        Assert.Equal("dependency", db.Kind);
        Assert.Equal("get_db", db.Detail);
    }

    [Fact]
    public void TheRoutesOwnDependenciesComeFirst()
    {
        var shared = Create_().Params![0];
        Assert.Equal("dependency", shared.Kind);
        Assert.Equal("(every request)", shared.Name);
        Assert.Equal("get_db", shared.Detail);
    }

    [Fact]
    public void TheReturnAnnotationIsTheResponseWhenThereIsNoResponseModel()
    {
        var e = RoutesOf(Head + "@app.get('/one')\ndef one() -> ItemOut: ...\n\n\n@app.get('/two')\ndef two(): ...\n");
        Assert.Equal("ItemOut", e.Single(r => r.Name == "one").Response);
        Assert.True(e.Single(r => r.Name == "two").Response == null);
    }

    [Fact]
    public void ASecurityDependencyShowsItsScopes()
    {
        var e = RoutesOf(Head + "@app.get('/me')\ndef me(user: Annotated[dict, Security(current_user, scopes=['me', 'items'])]): ...\n").Single(r => r.Name == "me");
        Assert.Equal("dependency", P(e, "user").Kind);
        Assert.Contains("scopes ['me', 'items']", P(e, "user").Detail!);
    }

    [Fact]
    public void FlaskRoutesHaveNoDetails()
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open(Main, "from flask import Flask\n\napp = Flask(__name__)\n\n\n@app.route('/x/<int:i>')\ndef x(i): ...\n", 1);
        Assert.True(svc.Routes().Single().Params == null);
    }
}
