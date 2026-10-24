using PyMagic.Server;

namespace PyMagic.Tests;

public class WebRouteTests
{
    const string Main = "file:///c:/proj/app/main.py";
    const string Users = "file:///c:/proj/app/users.py";
    const string Tests = "file:///c:/proj/tests/test_api.py";

    const string FastApiLib =
        "class FastAPI:\n    def __init__(self, **kw): ...\n    def get(self, path, **kw): ...\n    def post(self, path, **kw): ...\n    def include_router(self, router, prefix='', **kw): ...\n    def url_path_for(self, name, **kw): ...\n\n" +
        "class APIRouter:\n    def __init__(self, prefix='', **kw): ...\n    def get(self, path, **kw): ...\n    def post(self, path, **kw): ...\n    def delete(self, path, **kw): ...\n\n" +
        "def Depends(dependency=None): ...\n";

    const string FlaskLib =
        "class Flask:\n    def __init__(self, name): ...\n    def route(self, rule, **kw): ...\n    def register_blueprint(self, bp, url_prefix=None, **kw): ...\n\n" +
        "class Blueprint:\n    def __init__(self, name, import_name, url_prefix=None): ...\n    def route(self, rule, **kw): ...\n\n" +
        "def url_for(endpoint, **values): ...\ndef render_template(name, **context): ...\n";

    static (LanguageService Svc, Position Pos) Open(string uri, string marked, params (string Path, string Text)[] more)
    {
        int i = marked.IndexOf('|');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("fastapi/__init__.py", FastApiLib).With("flask/__init__.py", FlaskLib);
        foreach (var (p, t) in more) b.With(p, t);
        b.With(uri["file:///c:/proj/".Length..], text);
        return (b.Service, i < 0 ? new Position(0, 0) : new LineMap(text).PositionOf(i));
    }

    const string UsersApi =
        "from fastapi import APIRouter\n\nrouter = APIRouter(prefix=\"/users\")\n\n\n@router.get(\"/{user_id}\")\ndef read_user(user_id: int):\n    return user_id\n\n\n@router.post(\"/\")\ndef make_user():\n    ...\n";

    const string MainApi =
        "from fastapi import FastAPI\nfrom app import users\n\napp = FastAPI()\napp.include_router(users.router, prefix=\"/api\")\n\n\n@app.get(\"/health\")\ndef health():\n    return 1\n";

    static List<RouteEntry> RoutesOf(params (string Path, string Text)[] files)
    {
        var (svc, _) = Open(Main, MainApi, files);
        return svc.Routes().ToList();
    }

    // ------------------------------------------------------------ the list

    [Fact]
    public void RoutersAndIncludesComposeThePath()
    {
        var routes = RoutesOf(("app/__init__.py", ""), ("app/users.py", UsersApi));
        Assert.True(routes.Any(r => r.Path == "/health" && r.Methods == "GET"), "the app's own route");
        Assert.True(routes.Any(r => r.Path == "/api/users/{user_id}" && r.Name == "read_user"), "prefix of the include and of the router");
        Assert.True(routes.Any(r => r.Path == "/api/users/" && r.Methods == "POST"), "a trailing slash stays");
    }

    [Fact]
    public void BlueprintsFollowTheirRegistration()
    {
        var (svc, _) = Open("file:///c:/proj/shop/app.py",
            "from flask import Flask\nfrom shop.admin import bp\n\napp = Flask(__name__)\napp.register_blueprint(bp, url_prefix=\"/admin\")\n\n\n@app.route(\"/\")\ndef home():\n    return 1\n",
            ("shop/__init__.py", ""),
            ("shop/admin.py", "from flask import Blueprint\n\nbp = Blueprint(\"admin\", __name__)\n\n\n@bp.route(\"/users/<int:uid>\", methods=[\"GET\", \"POST\"])\ndef user(uid):\n    return uid\n"));
        var routes = svc.Routes();
        Assert.True(routes.Any(r => r.Path == "/admin/users/<int:uid>" && r.Methods == "GET, POST" && r.Framework == "flask"), "registered under /admin");
        Assert.True(routes.Any(r => r.Path == "/" && r.Name == "home"));
    }

    [Fact]
    public void TwoRoutersWithTheSameVariableNameKeepTheirOwnPrefixes()
    {
        var (svc, _) = Open(Main,
            "from fastapi import FastAPI\nfrom app import users, items\n\napp = FastAPI()\napp.include_router(users.router, prefix=\"/u\")\napp.include_router(items.router, prefix=\"/i\")\n",
            ("app/__init__.py", ""),
            ("app/users.py", "from fastapi import APIRouter\nrouter = APIRouter()\n\n@router.get(\"/a\")\ndef a(): ...\n"),
            ("app/items.py", "from fastapi import APIRouter\nrouter = APIRouter()\n\n@router.get(\"/a\")\ndef b(): ...\n"));
        var routes = svc.Routes();
        Assert.True(routes.Any(r => r.Path == "/u/a" && r.Name == "a"));
        Assert.True(routes.Any(r => r.Path == "/i/a" && r.Name == "b"));
    }

    // ------------------------------------------------------------ path against function

    static List<LspDiagnostic> Checks(string uri, string code, params (string Path, string Text)[] more)
    {
        var (svc, _) = Open(uri, code, more);
        return svc.Diagnostics(uri).Where(d => d.Code is "route-path-parameter" or "route-unknown-parameter" or "duplicate-route" or "route-endpoint").ToList();
    }

    [Fact]
    public void APathParameterTheFunctionLacks()
    {
        var found = Checks(Main, "from fastapi import FastAPI\napp = FastAPI()\n\n@app.get(\"/items/{item_id}\")\ndef read(q: str):\n    ...\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'item_id'", found[0].Message);
        Assert.Equal(3, found[0].Range.Start.Line);
        Assert.Equal(1, found[0].Severity == 3 ? 1 : 0);   // weak: FastAPI quietly leaves it out
    }

    [Fact]
    public void AMatchingParameterIsQuiet()
    {
        Assert.Equal(0, Checks(Main, "from fastapi import FastAPI\napp = FastAPI()\n\n@app.get(\"/items/{item_id:int}\")\ndef read(item_id: int, q: str = ''):\n    ...\n").Count);
    }

    [Fact]
    public void ADependencyMayTakeThePathParameter()
    {
        string code = "from fastapi import FastAPI, Depends\napp = FastAPI()\n\ndef get_item(item_id: int): ...\n\n@app.get(\"/items/{item_id}\")\ndef read(item=Depends(get_item)):\n    ...\n";
        Assert.Equal(0, Checks(Main, code).Count);
    }

    [Fact]
    public void FlaskNeedsTheParameterAndNothingElse()
    {
        var found = Checks(Main, "from flask import Flask\napp = Flask(__name__)\n\n@app.route(\"/u/<int:uid>\")\ndef user(name):\n    ...\n");
        Assert.True(found.Any(d => d.Code == "route-path-parameter" && d.Message.Contains("'uid'")), "uid is not taken");
        Assert.True(found.Any(d => d.Code == "route-unknown-parameter" && d.Message.Contains("'name'")), "name is never given");
    }

    [Fact]
    public void TheMissingParameterIsAddedWithItsType()
    {
        string src = "from flask import Flask\napp = Flask(__name__)\n\n@app.route(\"/u/<int:uid>\")\ndef user(page=1):\n    ...\n";
        var (svc, _) = Open(Main, src);
        var action = svc.CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).First(a => a.Title.StartsWith("Add parameter 'uid: int'"));
        var lines = new LineMap(src);
        string text = src;
        foreach (var e in action.Edit!.Changes[Main].OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        Assert.Contains("def user(uid: int, page=1):", text);
    }

    [Fact]
    public void TheSameRouteTwice()
    {
        var found = Checks(Main, "from fastapi import FastAPI\napp = FastAPI()\n\n@app.get(\"/a\")\ndef one(): ...\n\n@app.get(\"/a\")\ndef two(): ...\n\n@app.post(\"/a\")\ndef three(): ...\n");
        Assert.Equal(1, found.Count);
        Assert.Equal(6, found[0].Range.Start.Line);
        Assert.Contains("one()", found[0].Message);
    }

    [Fact]
    public void ParametersWithOtherNamesAreTheSameRoute()
    {
        var found = Checks(Main, "from fastapi import FastAPI\napp = FastAPI()\n\n@app.get(\"/a/{x}\")\ndef one(x): ...\n\n@app.get(\"/a/{y}\")\ndef two(y): ...\n");
        Assert.Equal(1, found.Count);
    }

    // ------------------------------------------------------------ names and URLs lead to routes

    [Fact]
    public void UrlForLeadsToTheView()
    {
        var (svc, pos) = Open("file:///c:/proj/shop/app.py", "from flask import Flask, url_for\napp = Flask(__name__)\n\n@app.route(\"/profile/<name>\")\ndef profile(name): ...\n\nurl_for('prof|ile', name='x')\n",
            ("shop/__init__.py", ""));
        var found = svc.Definition("file:///c:/proj/shop/app.py", pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(4, found[0].Range.Start.Line);
    }

    [Fact]
    public void ABlueprintEndpointIsQualified()
    {
        var (svc, pos) = Open("file:///c:/proj/shop/app.py", "from flask import url_for\n\nurl_for('admin.us|er', uid=3)\n", ("shop/__init__.py", ""),
            ("shop/admin.py", "from flask import Blueprint\nbp = Blueprint(\"admin\", __name__)\n\n@bp.route(\"/u/<int:uid>\")\ndef user(uid): ...\n"));
        Assert.Equal(1, svc.Definition("file:///c:/proj/shop/app.py", pos).Count);
        Assert.Equal(0, Checks("file:///c:/proj/shop/app.py", "from flask import url_for\n\nurl_for('admin.user', uid=3)\n", ("shop/__init__.py", ""),
            ("shop/admin.py", "from flask import Blueprint\nbp = Blueprint(\"admin\", __name__)\n\n@bp.route(\"/u/<int:uid>\")\ndef user(uid): ...\n")).Count);
    }

    [Fact]
    public void AnUnknownEndpointIsWeak()
    {
        var found = Checks("file:///c:/proj/shop/app.py", "from flask import Flask, url_for\napp = Flask(__name__)\n\n@app.route(\"/a\")\ndef a(): ...\n\nurl_for('b')\nurl_for('static', filename='x.css')\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'b'", found[0].Message);
    }

    [Fact]
    public void ATestClientUrlLeadsToTheHandler()
    {
        var (svc, pos) = Open(Tests, "def test_it(client):\n    client.get('/api/users/4|2')\n", ("app/__init__.py", ""), ("app/users.py", UsersApi), ("app/main.py", MainApi));
        var found = svc.Definition(Tests, pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(Users, found[0].Uri);
        Assert.Equal(6, found[0].Range.Start.Line);
        Assert.Contains("GET /api/users/{user_id}", svc.Hover(Tests, pos)!.Contents.Value);
    }

    [Fact]
    public void AFixedPathBeatsAParameterOne()
    {
        var (svc, pos) = Open(Tests, "def test_it(client):\n    client.get('/api/users/me|')\n", ("app/__init__.py", ""), ("app/main.py", MainApi),
            ("app/users.py", UsersApi + "\n\n@router.get(\"/me\")\ndef me(): ...\n"));
        var found = svc.Definition(Tests, pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(16, found[0].Range.Start.Line);
    }

    [Fact]
    public void HoverOnTheDecoratorShowsTheFullPath()
    {
        var (svc, pos) = Open(Users, UsersApi.Replace("\"/{user_id}\"", "\"/{us|er_id}\""), ("app/__init__.py", ""), ("app/main.py", MainApi));
        string text = svc.Hover(Users, pos)!.Contents.Value;
        Assert.Contains("GET /api/users/{user_id}", text);
        Assert.Contains("user_id", text);
    }

    // ------------------------------------------------------------ completion

    [Fact]
    public void EndpointNamesComplete()
    {
        var (svc, pos) = Open("file:///c:/proj/shop/app.py", "from flask import Flask, url_for\napp = Flask(__name__)\n\n@app.route(\"/profile\")\ndef profile(): ...\n\nurl_for('pro|')\n", ("shop/__init__.py", ""));
        Assert.True(svc.Completion("file:///c:/proj/shop/app.py", pos).Any(i => i.Label == "profile"));
    }

    [Fact]
    public void ParametersCompleteInsideTheBraces()
    {
        var (svc, pos) = Open(Main, "from fastapi import FastAPI\napp = FastAPI()\n\n@app.get(\"/items/{it|\")\ndef read(item_id: int, other: str): ...\n");
        var labels = svc.Completion(Main, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("item_id"));
        Assert.True(!labels.Contains("other"), "only names that start the same");
    }

    // ------------------------------------------------------------ templates

    [Fact]
    public void RenderTemplateNamesAFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_flask_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "templates"));
        File.WriteAllText(Path.Combine(dir, "templates", "index.html"), "<html></html>");
        var (svc, pos) = Open("file:///c:/proj/shop/app.py", "from flask import render_template\n\nrender_template('ind|ex.html')\nrender_template('missing.html')\n", ("shop/__init__.py", ""));
        svc.SourceRoots.Add(dir);
        Assert.Equal(1, svc.Definition("file:///c:/proj/shop/app.py", pos).Count);
        var weak = svc.Diagnostics("file:///c:/proj/shop/app.py").Where(d => d.Message.StartsWith("Template")).ToList();
        Assert.Equal(1, weak.Count);
        Assert.Contains("missing.html", weak[0].Message);
    }

    // ------------------------------------------------------------ dependencies

    static string TypeAt(string marked, params (string Path, string Text)[] more)
    {
        var (svc, pos) = Open(Main, marked, more);
        return svc.Hover(Main, pos)!.Contents.Value;
    }

    [Fact]
    public void ADependencyGivesItsReturnType()
    {
        string hover = TypeAt("from fastapi import Depends\n\nclass Session:\n    def commit(self): ...\n\ndef get_db() -> Session: ...\n\ndef read(db=Depends(get_db)):\n    d|b\n");
        Assert.Contains("Session", hover);
    }

    [Fact]
    public void AGeneratorDependencyGivesWhatItYields()
    {
        string hover = TypeAt("from fastapi import Depends\n\nclass Session:\n    def commit(self): ...\n\ndef get_db():\n    s = Session()\n    try:\n        yield s\n    finally:\n        pass\n\ndef read(db=Depends(get_db)):\n    d|b\n");
        Assert.Contains("Session", hover);
    }

    [Fact]
    public void AnAnnotationWinsOverTheDependency()
    {
        string hover = TypeAt("from fastapi import Depends\n\ndef get_db() -> int: ...\n\ndef read(db: str = Depends(get_db)):\n    d|b\n");
        Assert.Contains("str", hover);
    }

    [Fact]
    public void AClassAsDependencyIsAnInstance()
    {
        string hover = TypeAt("from fastapi import Depends\n\nclass Pager:\n    page: int = 1\n\ndef read(pg=Depends(Pager)):\n    p|g\n");
        Assert.Contains("Pager", hover);
    }
}
