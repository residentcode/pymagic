using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The FastAPI line of item 206, on the real FastAPI and pydantic (skipped where they are not installed): a body on a GET, a JSON body beside form
/// fields, Security scopes the OAuth2 scheme does not declare, and go to definition from a parameter to its dependency.
/// </summary>
public class RealFastApiMoreTests
{
    const string App = @"
from typing import Annotated
from fastapi import Depends, FastAPI, File, Form, Security, UploadFile
from fastapi.security import OAuth2PasswordBearer, SecurityScopes
from pydantic import BaseModel

app = FastAPI()
oauth2 = OAuth2PasswordBearer(tokenUrl='token', scopes={'me': 'Read yourself', 'items': 'Read items'})


class Item(BaseModel):
    name: str


class Filters(BaseModel):
    q: str = ''


class Paging:
    def __init__(self, page: int = 1):
        self.page = page


def get_user(scopes: SecurityScopes, token: Annotated[str, Depends(oauth2)]):
    return token


def item_filter(filters: Filters):
    return filters


CurrentUser = Annotated[str, Security(get_user, scopes=['me', 'itemz'])]


@app.get('/items')
def list_items(item: Item, user: CurrentUser):
    return []


@app.get('/search')
def search(filters: Annotated[Filters, Depends(item_filter)], paging: Annotated[Paging, Depends()]):
    return []


@app.post('/upload')
def upload(meta: Item, file: UploadFile = File(...), note: str = Form('')):
    return {}


@app.post('/items')
def create(item: Item, user: Annotated[str, Security(get_user, scopes=['items'])]):
    return item
";

    [Fact]
    public void WhatARequestMayCarryAndTheScopesOfTheScheme()
    {
        if (RealFastApiTests.Site == null) return;
        var p = RealFastApiTests.Open(App);
        var found = p.Problems().Where(d => d.Code is "fastapi-request-body" or "fastapi-security-scopes").Select(d => d.Range.Start.Line + ": " + d.Message).ToList();
        Assert.True(found.Any(m => m.Contains("A GET request with a body: FastAPI reads 'item' from the JSON body")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("'filters' (through its dependency, 'filters')") || m.Contains("A GET request with a body: FastAPI reads 'filters'")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("'meta' is a JSON body while 'file' is a form field")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("declares no scope 'itemz' (it has items, me)")), string.Join(" | ", found));
        Assert.True(found.Count == 4, string.Join(" | ", found));
    }

    [Fact]
    public void AParametersNameLeadsToItsDependency()
    {
        if (RealFastApiTests.Site == null) return;
        var p = RealFastApiTests.Open(App);
        var lines = new LineMap(p.Code);
        Position At(string needle, int delta = 1) => lines.PositionOf(p.Code.IndexOf(needle, StringComparison.Ordinal) + delta);
        // an alias of an Annotated with Security(get_user)
        var user = p.Svc.Definition(p.Uri, At("user: CurrentUser"));
        Assert.Equal(1, user.Count);
        Assert.Equal(lines.PositionOf(p.Code.IndexOf("def get_user", StringComparison.Ordinal) + 4).Line, user[0].Range.Start.Line);
        // Depends(item_filter) in the Annotated, and Depends() with the class
        Assert.Equal(lines.PositionOf(p.Code.IndexOf("def item_filter", StringComparison.Ordinal)).Line, p.Svc.Definition(p.Uri, At("filters: Annotated"))[0].Range.Start.Line);
        Assert.Equal(lines.PositionOf(p.Code.IndexOf("class Paging", StringComparison.Ordinal)).Line, p.Svc.Definition(p.Uri, At("paging: Annotated"))[0].Range.Start.Line);
    }
}
