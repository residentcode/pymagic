using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyMagic.Server;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>
/// A GitHub or a GitLab of the tests' own: an HttpListener on this computer that answers the few calls pull requests make and writes down what it was sent.
/// Nothing here, and nothing in these tests, talks to a real github.com or gitlab.com; the engine is pointed at this server through PYMAGIC_GITHUB_API / PYMAGIC_GITLAB_API.
/// </summary>
public sealed class FakeForge : IDisposable
{
    public sealed record Seen(string Method, string Path, string Body, string Auth);

    readonly HttpListener _listener = new();
    readonly bool _github;
    readonly string _token;
    public string BaseUrl { get; }
    public List<Seen> Requests { get; } = new();
    public string HeadSha = "aaaa1111bbbb2222";
    public bool Merged;
    /// <summary>With this on, the list of open requests runs over two pages (30, then 2), as a busy repository's does.</summary>
    public bool ManyPages;
    public string? RedirectTo;
    readonly object _gate = new();

    public FakeForge(bool github, string token = "tok-test")
    {
        _github = github;
        _token = token;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public IEnumerable<Seen> Writes => Requests.Where(r => r.Method != "GET");

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch (Exception) { return; }
            try { Handle(c); } catch (Exception e) { Reply(c, 500, new JsonObject { ["message"] = e.Message }); }
        }
    }

    static void Reply(HttpListenerContext c, int code, JsonNode? body)
    {
        c.Response.StatusCode = code;
        c.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body?.ToJsonString() ?? "");
        c.Response.OutputStream.Write(bytes);
        c.Response.Close();
    }

    void Handle(HttpListenerContext c)
    {
        string body = new StreamReader(c.Request.InputStream).ReadToEnd();
        string raw = c.Request.RawUrl!;
        string path = raw.Split('?')[0];
        string auth = _github ? c.Request.Headers["Authorization"] ?? "" : c.Request.Headers["PRIVATE-TOKEN"] ?? "";
        lock (_gate) Requests.Add(new Seen(c.Request.HttpMethod, path, body, auth));
        if (RedirectTo != null) { c.Response.StatusCode = 302; c.Response.Headers["Location"] = RedirectTo; c.Response.Close(); return; }
        if (auth != (_github ? "Bearer " + _token : _token)) { Reply(c, 401, new JsonObject { ["message"] = "Bad credentials" }); return; }
        var json = body.Length > 0 ? JsonNode.Parse(body) : null;
        string method = c.Request.HttpMethod;
        if (_github) Github(c, method, path, json); else Gitlab(c, method, path, json);
    }

    static JsonNode Arr(params JsonNode[] items) => new JsonArray(items);

    JsonObject GithubPr() => new()
    {
        ["number"] = 7, ["title"] = "Add the cart", ["user"] = new JsonObject { ["login"] = "ada" }, ["state"] = Merged ? "closed" : "open", ["merged"] = Merged, ["draft"] = false,
        ["head"] = new JsonObject { ["ref"] = "cart", ["sha"] = HeadSha }, ["base"] = new JsonObject { ["ref"] = "main" }, ["html_url"] = "https://github.com/acme/widgets/pull/7",
        ["updated_at"] = "2026-10-30T10:00:00Z", ["body"] = "Adds a cart.\n\nCloses nothing.", ["mergeable"] = true,
    };

    void Github(HttpListenerContext c, string method, string path, JsonNode? body)
    {
        const string repo = "/repos/acme/widgets";
        if (method == "GET" && path == repo + "/pulls")
        {
            if (!ManyPages) { Reply(c, 200, Arr(GithubPr())); return; }
            bool second = c.Request.QueryString["page"] == "2";
            var many = new JsonArray();
            for (int n = second ? 130 : 100; n < (second ? 132 : 130); n++) { var pr = GithubPr(); pr["number"] = n; pr["title"] = "Request " + n; many.Add(pr); }
            Reply(c, 200, many);
            return;
        }
        if (method == "GET" && path == repo + "/pulls/7") { Reply(c, 200, GithubPr()); return; }
        if (method == "GET" && path == repo + "/pulls/7/commits")
        {
            Reply(c, 200, Arr(new JsonObject { ["sha"] = "c1c1c1c1", ["commit"] = new JsonObject { ["message"] = "cart: the model\n\nbody", ["author"] = new JsonObject { ["name"] = "Ada", ["date"] = "2026-10-29T09:00:00Z" } } })); return;
        }
        if (method == "GET" && path == repo + "/pulls/7/files")
        {
            Reply(c, 200, Arr(
                new JsonObject { ["filename"] = "cart.py", ["status"] = "modified", ["additions"] = 2, ["deletions"] = 1, ["patch"] = "@@ -1,2 +1,3 @@\n def total():\n-    return 0\n+    return 1\n+    # more\n" },
                new JsonObject { ["filename"] = "new.py", ["status"] = "added", ["additions"] = 1, ["deletions"] = 0, ["patch"] = "@@ -0,0 +1 @@\n+x = 1\n" })); return;
        }
        if (method == "GET" && path == repo + "/issues/7/comments") { Reply(c, 200, Arr(new JsonObject { ["user"] = new JsonObject { ["login"] = "bob" }, ["body"] = "Looks fine", ["created_at"] = "2026-10-30T11:00:00Z" })); return; }
        if (method == "GET" && path == repo + "/pulls/7/comments")
        {
            Reply(c, 200, Arr(new JsonObject { ["user"] = new JsonObject { ["login"] = "cy" }, ["body"] = "Why 1?", ["created_at"] = "2026-10-30T12:00:00Z", ["path"] = "cart.py", ["line"] = 2 })); return;
        }
        if (method == "GET" && path == repo + $"/commits/{HeadSha}/check-runs")
        {
            Reply(c, 200, new JsonObject { ["check_runs"] = Arr(new JsonObject { ["name"] = "tests", ["status"] = "completed", ["conclusion"] = "success" }, new JsonObject { ["name"] = "lint", ["status"] = "in_progress", ["conclusion"] = null }) }); return;
        }
        if (method == "GET" && path == repo + $"/commits/{HeadSha}/status") { Reply(c, 200, new JsonObject { ["state"] = "pending", ["statuses"] = new JsonArray() }); return; }
        if (method == "POST" && path == repo + "/pulls")
        {
            var created = GithubPr();
            created["number"] = 8; created["title"] = body?["title"]?.GetValue<string>(); created["head"]!["ref"] = body?["head"]?.GetValue<string>(); created["base"]!["ref"] = body?["base"]?.GetValue<string>();
            Reply(c, 201, created); return;
        }
        if (method == "POST" && path == repo + "/issues/7/comments") { Reply(c, 201, new JsonObject { ["id"] = 1, ["body"] = body?["body"]?.GetValue<string>() }); return; }
        if (method == "POST" && path == repo + "/pulls/7/reviews")
        {
            if (body?["commit_id"]?.GetValue<string>() != HeadSha) { Reply(c, 422, new JsonObject { ["message"] = "commit_id is not the head" }); return; }
            Reply(c, 200, new JsonObject { ["id"] = 31, ["state"] = body?["event"]?.GetValue<string>() == "APPROVE" ? "APPROVED" : "COMMENTED" }); return;
        }
        if (method == "POST" && path == repo + "/pulls/7/comments")
        {
            Reply(c, 201, new JsonObject { ["id"] = 32, ["path"] = body?["path"]?.GetValue<string>(), ["line"] = body?["line"]?.DeepClone(), ["body"] = body?["body"]?.GetValue<string>() }); return;
        }
        if (method == "PUT" && path == repo + "/pulls/7/merge")
        {
            if (body?["sha"]?.GetValue<string>() != HeadSha) { Reply(c, 409, new JsonObject { ["message"] = "Head branch was modified. Review and try the merge again." }); return; }
            Merged = true;
            Reply(c, 200, new JsonObject { ["merged"] = true, ["sha"] = "merge-sha" }); return;
        }
        Reply(c, 404, new JsonObject { ["message"] = "Not Found" });
    }

    JsonObject GitlabMr() => new()
    {
        ["iid"] = 3, ["title"] = "Draft: tidy", ["author"] = new JsonObject { ["username"] = "eve" }, ["state"] = Merged ? "merged" : "opened", ["draft"] = true, ["source_branch"] = "tidy", ["target_branch"] = "main",
        ["web_url"] = "https://gitlab.example.org/team/sub/proj/-/merge_requests/3", ["updated_at"] = "2026-10-30T10:00:00Z", ["sha"] = HeadSha, ["description"] = "Tidies things.", ["detailed_merge_status"] = "mergeable",
        ["diff_refs"] = new JsonObject { ["base_sha"] = "b0b0b0b0", ["start_sha"] = "5a5a5a5a", ["head_sha"] = HeadSha },
    };

    void Gitlab(HttpListenerContext c, string method, string path, JsonNode? body)
    {
        const string project = "/projects/team%2Fsub%2Fproj";
        if (method == "GET" && path == project + "/merge_requests") { Reply(c, 200, Arr(GitlabMr())); return; }
        if (method == "GET" && path == project + "/merge_requests/3") { Reply(c, 200, GitlabMr()); return; }
        if (method == "GET" && path == project + "/merge_requests/3/pipelines") { Reply(c, 200, Arr(new JsonObject { ["status"] = "failed" })); return; }
        if (method == "GET" && path == project + "/merge_requests/3/commits") { Reply(c, 200, Arr(new JsonObject { ["id"] = "d1d1d1d1", ["title"] = "tidy", ["message"] = "tidy\n", ["author_name"] = "Eve", ["created_at"] = "2026-10-29T09:00:00Z" })); return; }
        if (method == "GET" && path == project + "/merge_requests/3/changes")
        {
            Reply(c, 200, new JsonObject { ["changes"] = Arr(new JsonObject { ["old_path"] = "a.py", ["new_path"] = "a.py", ["new_file"] = false, ["deleted_file"] = false, ["renamed_file"] = false, ["diff"] = "@@ -1 +1,2 @@\n x\n+y\n" }) }); return;
        }
        if (method == "GET" && path == project + "/merge_requests/3/notes")
        {
            Reply(c, 200, Arr(new JsonObject { ["author"] = new JsonObject { ["username"] = "sys" }, ["body"] = "added 1 commit", ["system"] = true, ["created_at"] = "2026-10-29T09:00:00Z" },
                new JsonObject { ["author"] = new JsonObject { ["username"] = "dan" }, ["body"] = "ok by me", ["system"] = false, ["created_at"] = "2026-10-30T09:00:00Z" })); return;
        }
        if (method == "POST" && path == project + "/merge_requests")
        {
            var created = GitlabMr();
            created["iid"] = 4; created["title"] = body?["title"]?.GetValue<string>(); created["source_branch"] = body?["source_branch"]?.GetValue<string>(); created["target_branch"] = body?["target_branch"]?.GetValue<string>();
            Reply(c, 201, created); return;
        }
        if (method == "POST" && path == project + "/merge_requests/3/notes") { Reply(c, 201, new JsonObject { ["id"] = 9, ["body"] = body?["body"]?.GetValue<string>() }); return; }
        if (method == "POST" && path == project + "/merge_requests/3/approve") { Reply(c, 201, new JsonObject { ["approved"] = true }); return; }
        if (method == "POST" && path == project + "/merge_requests/3/unapprove") { Reply(c, 201, new JsonObject { ["approved"] = false }); return; }
        if (method == "POST" && path == project + "/merge_requests/3/discussions") { Reply(c, 201, new JsonObject { ["id"] = "d1", ["notes"] = Arr(new JsonObject { ["body"] = body?["body"]?.GetValue<string>() }) }); return; }
        if (method == "PUT" && path == project + "/merge_requests/3/merge")
        {
            if (body?["sha"]?.GetValue<string>() != HeadSha) { Reply(c, 409, new JsonObject { ["message"] = "SHA does not match HEAD of source branch" }); return; }
            Merged = true;
            Reply(c, 200, GitlabMr()); return;
        }
        Reply(c, 404, new JsonObject { ["message"] = "404 Not found" });
    }

    public void Dispose() { _listener.Stop(); _listener.Close(); }
}

