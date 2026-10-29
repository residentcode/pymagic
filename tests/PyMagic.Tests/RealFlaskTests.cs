using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Flask as the real package describes itself (item 128). Skipped where Flask is not installed.</summary>
public class RealFlaskTests
{
    static readonly string? Site = new[] { Environment.GetEnvironmentVariable("PYMAGIC_FLASK_SITE") }
        .FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "flask", "__init__.py")));

    static bool Missing => Site == null;

    const string App =
        "from flask import Flask, Blueprint, request, jsonify, render_template, redirect, abort, url_for, make_response, send_file, session, g, flash\n" +
        "from flask.views import MethodView\n\n" +
        "app = Flask(__name__)\napp.secret_key = 'x'\nbp = Blueprint('shop', __name__, url_prefix='/shop')\n\n\n" +
        "@app.route('/')\ndef index():\n    return render_template('index.html', user=g.get('user'))\n\n\n" +
        "@app.route('/user/<int:user_id>')\ndef profile(user_id):\n    if user_id < 0:\n        abort(404)\n    return jsonify(id=user_id)\n\n\n" +
        "@app.route('/login', methods=['GET', 'POST'])\ndef login():\n    if request.method == 'POST':\n        session['name'] = request.form['name']\n        flash('Welcome')\n        return redirect(url_for('profile', user_id=1))\n    return render_template('login.html')\n\n\n" +
        "@app.get('/search')\ndef search():\n    page = request.args.get('page', 1, type=int)\n    q = request.args['q']\n    tags = request.args.getlist('tag')\n    token = request.headers.get('X-Token')\n    return {'page': page, 'q': q, 'tags': tags, 'token': token}\n\n\n" +
        "@app.post('/upload')\ndef upload():\n    f = request.files['photo']\n    data = request.get_json(silent=True) or {}\n    resp = make_response(jsonify(name=f.filename, data=data), 201)\n    resp.set_cookie('sid', request.cookies.get('sid', ''))\n    return resp\n\n\n" +
        "@app.route('/download')\ndef download():\n    return send_file('report.pdf')\n\n\n" +
        "@bp.route('/items')\ndef items():\n    return jsonify([])\n\n\n" +
        "class UserAPI(MethodView):\n    def get(self, user_id):\n        return jsonify(id=user_id)\n\n    def post(self, user_id):\n        payload = request.get_json()\n        return jsonify(payload), 201\n\n\n" +
        "app.add_url_rule('/api/users/<int:user_id>', view_func=UserAPI.as_view('users'))\napp.register_blueprint(bp)\n\n\n" +
        "@app.errorhandler(404)\ndef not_found(e):\n    return 'missing', 404\n\n\n" +
        "@app.before_request\ndef before():\n    g.user = session.get('name')\n\n\n" +
        "if __name__ == '__main__':\n    app.run(debug=True)\n";

    static (LanguageService svc, string uri, string dir) Project(string? app = null)
    {
        var svc = new LanguageService();
        string dir = Path.Combine(Path.GetTempPath(), "pm_realflask_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string full = Path.Combine(dir, "app.py");
        File.WriteAllText(full, app ?? App);
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(Site!);
        string uri = LanguageService.UriOfPath(full);
        svc.Open(uri, app ?? App, 1);
        return (svc, uri, dir);
    }

    [Fact]
    public void ARealFlaskAppHasNoWarnings()
    {
        if (Missing) return;
        var (svc, uri, dir) = Project();
        var problems = svc.Diagnostics(uri).Where(d => d.Severity <= 2).Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Message}").ToList();
        Assert.Equal("", string.Join("\n", problems));
    }

    [Fact]
    public void EveryRouteOfARealFlaskAppIsListed()
    {
        if (Missing) return;
        var lines = Project().svc.Routes().Select(r => $"{r.Methods} {r.Path} {r.Name}").OrderBy(x => x, StringComparer.Ordinal);
        var expected = new[]
        {
            "GET / index", "GET /download download", "GET /shop/items items", "GET /user/<int:user_id> profile", "GET /search search",
            "GET, POST /api/users/<int:user_id> UserAPI", "GET, POST /login login", "POST /upload upload",
        }.OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(string.Join("\n", expected), string.Join("\n", lines));
    }

    [Fact]
    public void WhatARealViewReadsAndGives()
    {
        if (Missing) return;
        var routes = Project().svc.Routes();
        var search = routes.Single(r => r.Name == "search");
        Assert.Equal("page:query:int,q:query:,tag:query:,X-Token:header:", string.Join(",", search.Params!.Select(p => $"{p.Name}:{p.Kind}:{p.Type}")));
        var profile = routes.Single(r => r.Name == "profile");
        Assert.Equal("json", profile.Response);
        Assert.Equal("abort 404", profile.Status);
        var upload = routes.Single(r => r.Name == "upload");
        Assert.True(upload.Params!.Any(p => p.Kind == "file" && p.Name == "photo"));
        Assert.True(upload.Params!.Any(p => p.Kind == "body"));
        Assert.True(upload.Params!.Any(p => p.Kind == "cookie" && p.Name == "sid"));
    }

    [Fact]
    public void ARealFlaskTemplateKnowsTheRequestAndTheRoutes()
    {
        if (Missing) return;
        var (svc, uri, dir) = Project();
        string page = "{% extends 'base.html' %}\n{% block content %}\n<a href=\"{{ url_for('profile', user_id=1) }}\">{{ request.path }}</a>\n" +
            "{% for m in get_flashed_messages() %}{{ m }}{% endfor %}{{ session.get('name') }}{{ g.user }}{{ config['DEBUG'] }}\n{% endblock %}\n";
        string path = Path.Combine(dir, "templates", "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, page);
        File.WriteAllText(Path.Combine(dir, "templates", "base.html"), "{% block content %}{% endblock %}");
        string t = LanguageService.UriOfPath(path);
        svc.Open(t, page, 1);
        Assert.Equal("", string.Join("\n", svc.Diagnostics(t).Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Message}")));
        var hover = svc.Hover(t, new Position(2, page.Split('\n')[2].IndexOf("path", StringComparison.Ordinal) + 1))?.Contents.Value ?? "";
        Assert.Contains("str", hover);
        string wrong = page.Replace("user_id=1", "user_id='me'");
        svc.Open(t, wrong, 2);
        Assert.Equal(1, svc.Diagnostics(t).Count(d => d.Code == "flask-url-for-arguments"));
    }

    [Fact]
    public void TheRealFlaskChecksStillFire()
    {
        if (Missing) return;
        string app = App + "\n\n@app.route('/save', methods=['POST'])\ndef save():\n    session['x'] = request.form['x']\n\n\nurl_for('profile')\nurl_for('profile', user_id='abc')\n" +
            "\n\n@app.route('/count')\ndef count():\n    return len(session)\n\n\n@app.route('/pair')\ndef pair():\n    return make_response('x'), 2.5\n\n\nbp.add_url_rule('/extra', 'extra', count)\n";
        var (svc, uri, _) = Project(app);
        var codes = svc.Diagnostics(uri).Select(d => d.Code).ToList();
        Assert.Equal(1, codes.Count(c => c == "flask-no-response"));
        Assert.Equal(2, codes.Count(c => c == "flask-url-for-arguments"));
        Assert.Equal(2, codes.Count(c => c == "flask-response-type"));
        Assert.True(svc.Routes().Any(r => r.Path == "/shop/extra"));
    }
}
