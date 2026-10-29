using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>FastAPI and pydantic as the real packages describe themselves (item 126). Skipped where they are not installed.</summary>
public class RealFastApiTests
{
    internal static readonly string? Site = FindSite();

    static string? FindSite()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PYMAGIC_FASTAPI_SITE"),
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".unsloth", "studio", "unsloth_studio", "Lib", "site-packages"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "fastapi", "__init__.py")) && File.Exists(Path.Combine(c, "pydantic", "__init__.py")));
    }

    internal sealed class Project
    {
        public LanguageService Svc = null!;
        public string Uri = "";
        public string Code = "";

        public string Hover(string needle, int delta)
        {
            int at = Code.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) throw new Exception("not in the code: " + needle);
            return (Svc.Hover(Uri, new LineMap(Code).PositionOf(at + delta))?.Contents.Value ?? "(nothing)").Replace("\n", " ");
        }

        public List<LspDiagnostic> Problems() => Svc.Diagnostics(Uri).ToList();
    }

    internal static Project Open(string code)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_realfastapi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(Site!);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "main.py"));
        svc.Open(uri, code, 1);
        return new Project { Svc = svc, Uri = uri, Code = code };
    }

    const string App = @"
from typing import Annotated
from fastapi import FastAPI, Depends, Query, Path, Body, HTTPException, Security, status, APIRouter, BackgroundTasks, Header
from fastapi.security import OAuth2PasswordBearer, SecurityScopes
from pydantic import BaseModel, Field

app = FastAPI()
oauth2 = OAuth2PasswordBearer(tokenUrl='token')


class Item(BaseModel):
    name: str
    price: float = Field(gt=0)
    tags: list[str] = []


class ItemOut(BaseModel):
    id: int
    name: str


class User(BaseModel):
    username: str


def get_db():
    db = {}
    yield db


async def current_user(token: Annotated[str, Depends(oauth2)]) -> User:
    return User(username=token)


async def scoped(security_scopes: SecurityScopes, token: Annotated[str, Depends(oauth2)]) -> User:
    return User(username=token)


DB = Annotated[dict, Depends(get_db)]
Current = Annotated[User, Depends(current_user)]


@app.post('/items/', response_model=ItemOut, status_code=status.HTTP_201_CREATED)
async def create_item(item: Item, db: DB, q: Annotated[str | None, Query(max_length=50)] = None) -> ItemOut:
    price = item.price
    names = item.tags
    typo = item.nmae
    return ItemOut(id=1, name=item.name)


@app.get('/items/{item_id}')
def read_item(item_id: Annotated[int, Path(gt=0)], user: Current, db: DB, background: BackgroundTasks, x_token: Annotated[str, Header()]):
    login = user.username
    who = user.usernam
    found = db.get(item_id)
    if not found:
        raise HTTPException(status_code=404, detail='Not found')
    return found


@app.get('/me')
async def me(user: Annotated[User, Security(scoped, scopes=['me'])]):
    return user.username
";

    [Fact]
    public void ModelsAndDependenciesAreTypedAndTyposReported()
    {
        if (Site == null) return;
        var p = Open(App);
        Assert.Contains("price: float", p.Hover("price = item.price", 1));
        Assert.Contains("names: list[str]", p.Hover("names = item.tags", 1));
        Assert.Contains("login: str", p.Hover("login = user.username", 1));
        var missing = p.Problems().Where(d => d.Code == "missing-attribute").Select(d => d.Message).ToList();
        Assert.Equal(2, missing.Count);
        Assert.True(missing.Any(m => m.Contains("'nmae'")) && missing.Any(m => m.Contains("'usernam'")));
    }

    [Fact]
    public void ACleanAppGetsNoneOfTheNewWarnings()
    {
        if (Site == null) return;
        var p = Open(App);
        Assert.Equal(0, p.Problems().Count(d => d.Code is "route-shadowed" or "depends-called" or "async-blocking-call"));
    }

    const string Mistakes = @"
import time
from fastapi import Depends, FastAPI

app = FastAPI()


def get_db():
    yield {}


@app.get('/items/{item_id}')
def read_item(item_id: int): ...


@app.get('/items/me')
def read_me(): ...


@app.post('/items/')
async def create(db=Depends(get_db())):
    time.sleep(1)
";

    [Fact]
    public void TheThreeMistakesAreFoundOnTheRealFastApi()
    {
        if (Site == null) return;
        var found = Open(Mistakes).Problems().Where(d => d.Code is "route-shadowed" or "depends-called" or "async-blocking-call").Select(d => d.Code).OrderBy(c => c).ToList();
        Assert.Equal("async-blocking-call,depends-called,route-shadowed", string.Join(",", found));
    }
}
