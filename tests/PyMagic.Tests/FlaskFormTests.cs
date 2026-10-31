using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Flask (item 207): what a view reads of a submitted form, checked against the forms of the templates that submit to it, and completed.</summary>
public class FlaskFormTests
{
    const string App =
        "from flask import Flask, request, render_template, url_for\n\napp = Flask(__name__)\n\n\n" +
        "@app.route('/login', methods=['GET', 'POST'])\ndef login():\n    if request.method == 'POST':\n        user = request.form['emial']\n        word = request.form.get('password')\n        keep = request.form.get('remember')\n        return user + word + keep\n    return render_template('login.html')\n\n\n" +
        "@app.route('/photo', methods=['POST'])\ndef photo():\n    picture = request.files['picture']\n    caption = request.form['caption']\n    return 'ok'\n\n\n" +
        "@app.route('/search')\ndef search():\n    q = request.args.get('query')\n    page = request.args.get('q')\n    return render_template('search.html', q=q, page=page)\n\n\n" +
        "@app.route('/signup', methods=['POST'])\ndef signup():\n    return request.form['anything']\n";

    static readonly (string Name, string Text)[] Templates =
    {
        ("login.html", "<form method=\"post\">\n  <input type=\"email\" name=\"email\">\n  <input type=\"password\" name=\"password\">\n  <input type=\"checkbox\" name=\"remember\">\n  <button type=\"submit\">Log in</button>\n</form>\n"),
        ("upload.html", "<form action=\"{{ url_for('photo') }}\" method=\"POST\" enctype=\"multipart/form-data\">\n  <input type=\"file\" name=\"photo\">\n  <textarea name=\"caption\"></textarea>\n</form>\n"),
        ("search.html", "<form action=\"/search\">\n  <input name=\"q\" value=\"{{ q }}\">\n</form>\n"),
        ("signup.html", "<form action=\"{{ url_for('signup') }}\" method=\"post\">\n  {{ form.hidden_tag() }}\n  {{ form.email }}\n</form>\n"),
    };

    static (LanguageService Svc, string Uri) Open()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_flaskforms_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "templates"));
        foreach (var (name, text) in Templates) File.WriteAllText(Path.Combine(dir, "templates", name), text);
        File.WriteAllText(Path.Combine(dir, "app.py"), App);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "app.py"));
        svc.Open(uri, App, 1);
        return (svc, uri);
    }

    [Fact]
    public void WhatAViewReadsIsCheckedAgainstTheFormsThatSubmitToIt()
    {
        var (svc, uri) = Open();
        var found = svc.Diagnostics(uri).Where(d => d.Code == "flask-form-field").Select(d => d.Message).ToList();
        // login.html posts back to the view that renders it; upload.html names photo() with url_for; search.html's GET form goes to /search
        Assert.True(found.Any(m => m.Contains("No form that submits to login() sends a field 'emial' (the form in login.html sends: email, password, remember): Flask answers 400 Bad Request")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("sends a file field 'picture'") && m.Contains("photo")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("'query'") && m.Contains("it is always None")), string.Join(" | ", found));
        // the fields of signup.html come from Flask-WTF: not judged
        Assert.True(found.Count == 3, string.Join(" | ", found));
    }

    [Fact]
    public void TheFieldsOfTheFormsComplete()
    {
        var (svc, uri) = Open();
        string marked = App.Replace("request.form['emial']", "request.form['']");
        svc.Open(uri, marked, 2);
        int at = marked.IndexOf("request.form['", StringComparison.Ordinal) + "request.form['".Length;
        var labels = svc.Completion(uri, new LineMap(marked).PositionOf(at)).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("email") && labels.Contains("password") && labels.Contains("remember"), string.Join(", ", labels));
    }
}
