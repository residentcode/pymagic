using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Flask (item 128): a view that gives no response, url_for without what the route needs, what a view reads and gives, and the routes that are not decorators.</summary>
public class FlaskTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Head = "from flask import Flask, request, jsonify, render_template, redirect, abort, url_for\n\napp = Flask(__name__)\n\n\n";

    static LanguageService Open(string src)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open(Main, src, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(string src, string code) => Open(src).Diagnostics(Main).Where(d => d.Code == code).ToList();

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    // ------------------------------------------------------------ flask-no-response

    [Fact]
    public void AViewThatNeverReturnsIsReported()
    {
        var d = Found(Head + Src("@app.route('/a')", "def a():", "    x = 1"), "flask-no-response");
        Assert.Equal(1, d.Count);
        Assert.Contains("never returns a response", d[0].Message);
    }

    [Fact]
    public void AViewThatReturnsOnlyNoneIsReported()
    {
        Assert.Equal(1, Found(Head + Src("@app.route('/a')", "def a():", "    return None"), "flask-no-response").Count);
        Assert.Equal(1, Found(Head + Src("@app.route('/b')", "def b():", "    return"), "flask-no-response").Count);
    }

    [Fact]
    public void AViewThatCanFallOffTheEndIsReported()
    {
        var d = Found(Head + Src("@app.route('/a')", "def a():", "    if request.args.get('x'):", "        return 'ok'"), "flask-no-response");
        Assert.Equal(1, d.Count);
        Assert.Contains("can end without returning", d[0].Message);
    }

    [Fact]
    public void ViewsThatAnswerAreFine()
    {
        string src = Head + Src(
            "@app.route('/a')", "def a():", "    return 'ok'", "",
            "@app.route('/b')", "def b():", "    if request.args:", "        return jsonify({})", "    return redirect('/')", "",
            "@app.route('/c')", "def c():", "    abort(404)", "",
            "@app.route('/d')", "def d():", "    raise ValueError()", "",
            "@app.route('/e')", "def e():", "    ...", "",
            "@app.get('/f')", "def f():", "    try:", "        return render_template('x.html')", "    except KeyError:", "        abort(500)");
        Assert.Equal(0, Found(src, "flask-no-response").Count);
    }

    [Fact]
    public void FastApiViewsMayReturnNothing()
    {
        string src = "from fastapi import FastAPI\n\napp = FastAPI()\n\n\n@app.get('/a')\ndef a():\n    x = 1\n";
        Assert.Equal(0, Found(src, "flask-no-response").Count);
    }

    // ------------------------------------------------------------ flask-url-for-arguments

    const string Profile = "@app.route('/user/<int:user_id>')\ndef profile(user_id):\n    return 'x'\n\n\n";

    [Fact]
    public void UrlForWithoutAPathParameterIsReported()
    {
        var d = Found(Head + Profile + "url_for('profile')\n", "flask-url-for-arguments");
        Assert.Equal(1, d.Count);
        Assert.Contains("needs 'user_id'", d[0].Message);
    }

    [Fact]
    public void UrlForWithItsParametersIsFine()
    {
        Assert.Equal(0, Found(Head + Profile + "url_for('profile', user_id=3)\nurl_for('profile', user_id=3, _external=True, page=2)\n", "flask-url-for-arguments").Count);
        Assert.Equal(0, Found(Head + Profile + "values = {}\nurl_for('profile', **values)\n", "flask-url-for-arguments").Count);
        Assert.Equal(0, Found(Head + Profile + "url_for('static', filename='x.css')\n", "flask-url-for-arguments").Count);
    }

    [Fact]
    public void ANonNumberForAnIntParameterIsReported()
    {
        var d = Found(Head + Profile + "url_for('profile', user_id='abc')\nurl_for('profile', user_id='42')\n", "flask-url-for-arguments");
        Assert.Equal(1, d.Count);
        Assert.Contains("is not a number", d[0].Message);
    }

    [Fact]
    public void RoutesWithDefaultsAndTwoRoutesOfOneEndpointAreLeftAlone()
    {
        string defaults = Head + Src("@app.route('/p/', defaults={'page': 1})", "@app.route('/p/<int:page>')", "def pages(page):", "    return 'x'") + "url_for('pages')\n";
        Assert.Equal(0, Found(defaults, "flask-url-for-arguments").Count);
    }

    // ------------------------------------------------------------ what a view reads and gives

    static RouteEntry One(string body, string decorators = "@app.route('/things/<int:id>', methods=['GET', 'POST'])") =>
        Open(Head + decorators + "\n" + body).Routes().Single();

    [Fact]
    public void WhatARequestReadsBecomesParameters()
    {
        var e = One(Src(
            "def things(id):", "    page = request.args.get('page', 1, type=int)", "    q = request.args['q']", "    tags = request.args.getlist('tag')", "    name = request.form['name']",
            "    f = request.files['photo']", "    token = request.headers.get('X-Token')", "    sid = request.cookies.get('sid')", "    data = request.get_json()", "    return 'ok'"));
        RouteParam P(string kind, string name) => e.Params!.Single(p => p.Kind == kind && p.Name == name);
        Assert.Equal("int", P("path", "id").Type);
        Assert.True(!P("query", "page").Required);
        Assert.Equal("int", P("query", "page").Type);
        Assert.True(P("query", "q").Required);
        Assert.Equal("list", P("query", "tag").Detail);
        Assert.True(P("form", "name").Required);
        Assert.Equal("file", P("file", "photo").Kind);
        Assert.True(!P("header", "X-Token").Required);
        Assert.Equal("cookie", P("cookie", "sid").Kind);
        Assert.Equal("body", P("body", "json").Kind);
    }

    [Fact]
    public void TheJsonAttributeIsABodyToo()
    {
        var e = One(Src("def things(id):", "    return request.json['x']"));
        Assert.Equal("body", e.Params!.Single(p => p.Name == "json").Kind);
    }

    [Fact]
    public void WhatAViewReturnsAndAbortsWith()
    {
        var e = One(Src("def things(id):", "    if not id:", "        abort(404)", "    if id > 5:", "        return jsonify({}), 201", "    return render_template('things.html')"));
        Assert.Equal("json, template things.html", e.Response);
        Assert.Equal("201, abort 404", e.Status);
    }

    [Fact]
    public void TheDecoratorsAreWhatTheViewNeeds()
    {
        var e = One(Src("def things(id):", "    return 'x'"), "@app.route('/things/<int:id>')\n@login_required\n@cache.cached(timeout=5)");
        Assert.Equal("login_required,cached", string.Join(",", e.Tags!));
    }

    [Fact]
    public void ABlueprintIsNamedInTheTags()
    {
        string src = "from flask import Flask, Blueprint\n\napp = Flask(__name__)\nbp = Blueprint('shop', __name__, url_prefix='/shop')\n\n\n@bp.route('/')\ndef index():\n    return 'x'\n\n\napp.register_blueprint(bp)\n";
        Assert.Equal("blueprint shop", string.Join(",", Open(src).Routes().Single().Tags!));
    }

    // ------------------------------------------------------------ routes that are not decorators

    [Fact]
    public void AMethodViewRegisteredWithAddUrlRule()
    {
        string src = "from flask import Flask, request\nfrom flask.views import MethodView\n\napp = Flask(__name__)\n\n\nclass UserAPI(MethodView):\n    def get(self, user_id):\n        return {}\n\n    def post(self):\n        data = request.get_json()\n        return {}, 201\n\n\napp.add_url_rule('/users/<int:user_id>', view_func=UserAPI.as_view('users'))\n";
        var e = Open(src).Routes().Single();
        Assert.Equal("GET, POST", e.Methods);
        Assert.Equal("/users/<int:user_id>", e.Path);
        Assert.Equal("UserAPI", e.Name);
        Assert.True(e.Params!.Any(p => p.Kind == "path" && p.Name == "user_id" && p.Type == "int"));
        Assert.True(e.Params!.Any(p => p.Kind == "body"));
    }

    [Fact]
    public void AFunctionGivenToAddUrlRule()
    {
        string src = "from flask import Flask\n\napp = Flask(__name__)\n\n\ndef health():\n    return 'ok'\n\n\napp.add_url_rule('/health', 'health', health, methods=['GET', 'HEAD'])\n";
        var e = Open(src).Routes().Single();
        Assert.Equal("GET, HEAD", e.Methods);
        Assert.Equal("health", e.Name);
        Assert.Equal("/health", e.Path);
    }

    [Fact]
    public void FlaskRestfulResourcesGetARouteForEachUrl()
    {
        string src = "from flask import Flask\nfrom flask_restful import Api, Resource\n\napp = Flask(__name__)\napi = Api(app)\n\n\nclass Todo(Resource):\n    def get(self, todo_id):\n        return {}\n\n    def delete(self, todo_id):\n        return '', 204\n\n\napi.add_resource(Todo, '/todos', '/todos/<int:todo_id>')\n";
        var routes = Open(src).Routes();
        Assert.Equal(2, routes.Count);
        Assert.Equal("/todos,/todos/<int:todo_id>", string.Join(",", routes.Select(r => r.Path).OrderBy(x => x, StringComparer.Ordinal)));
        Assert.True(routes.All(r => r.Methods == "GET, DELETE"));
    }

    [Fact]
    public void ARouteDeclaredByADecoratorIsNotListedTwice()
    {
        var routes = Open(Head + Src("@app.route('/a')", "def a():", "    return 'x'")).Routes();
        Assert.Equal(1, routes.Count);
    }
}
