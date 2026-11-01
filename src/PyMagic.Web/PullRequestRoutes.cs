using System.Text.Json.Nodes;
using PyMagic.Server;
using PyMagic.Server.Git;

/// <summary>
/// The routes of the Pull Requests tool window. Reading goes straight through; creating a pull request, commenting and merging take two steps:
/// <c>/api/pr/prepare</c> makes the request and answers with exactly what would be sent and a token, <c>/api/pr/confirm</c> carries out the request
/// of that token, once. The token of the forge never leaves the engine: the page can set it and learn that there is one, nothing more.
/// </summary>
public static class PullRequestRoutes
{
    public static void Map(WebApplication app, string root, Func<GitRepository?> findGit, Func<string?> gitReason)
    {
        (ForgeRemote? Remote, string? Reason) Locate()
        {
            var repo = findGit();
            if (repo == null) return (null, gitReason() ?? "This folder is not a git repository.");
            try
            {
                var remotes = repo.Remotes();
                if (remotes.Count == 0) return (null, "The repository has no remote yet. Add one in the Git window.");
                var found = PullRequests.Detect(remotes);
                return found == null
                    ? (null, "None of the remotes is on github.com or a GitLab host (a host with gitlab in its name; others can be named in PYMAGIC_GITLAB_HOSTS).")
                    : (found, null);
            }
            catch (GitException e) { return (null, e.Message); }
        }

        async Task<IResult> Guard(Func<ForgeRemote, string, Task<object?>> work)
        {
            var (remote, reason) = Locate();
            if (remote == null) return Results.Json(new { error = reason });
            try { return Results.Json(await work(remote, ForgeTokens.Get(root, remote.TokenKey))); }
            catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
            catch (GitException e) { return Results.Json(new { error = e.Message }); }
        }

        app.MapGet("/api/pr/info", () =>
        {
            var (remote, reason) = Locate();
            if (remote == null) return Results.Json(new { available = false, reason });
            string? branch = null, suggested = null;
            bool pushed = false;
            var bases = new List<string>();
            try
            {
                var repo = findGit()!;
                var status = repo.Status();
                branch = status.Branch;
                pushed = status.Upstream != null && status.Ahead == 0;
                var tracked = repo.RemoteBranches();
                bases = tracked.Where(b => b.StartsWith(remote.RemoteName + "/")).Select(b => b[(remote.RemoteName.Length + 1)..]).ToList();
                // the remote has not been fetched under its own name: the branches of the others are the nearest thing known
                if (bases.Count == 0) bases = tracked.Select(b => b[(b.IndexOf('/') + 1)..]).Distinct().ToList();
                if (status.HasCommits && repo.Log(null, 1, 0).FirstOrDefault() is { } last) suggested = last.Subject;
            }
            catch (GitException) { /* the form is left to fill in */ }
            return Results.Json(new
            {
                available = true, kind = remote.Kind, host = remote.Host, repo = remote.Path, apiBase = remote.ApiBase, remote = remote.RemoteName,
                hasToken = ForgeTokens.Has(root, remote.TokenKey), branch, bases, suggestedTitle = suggested, pushed,
            });
        });

        app.MapPost("/api/pr/token", async (HttpRequest req) =>
        {
            var body = await JsonNode.ParseAsync(req.Body);
            var (remote, reason) = Locate();
            if (remote == null) return Results.Json(new { error = reason });
            try { ForgeTokens.Set(root, remote.TokenKey, body?["token"]?.GetValue<string>() ?? ""); }
            catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
            return Results.Json(new { hasToken = ForgeTokens.Has(root, remote.TokenKey) });
        });

        app.MapGet("/api/pr/list", () => Guard(async (r, t) => new { items = await PullRequests.List(r, t) }));
        app.MapGet("/api/pr/get", (int number) => Guard(async (r, t) => await PullRequests.Detail(r, t, number)));

        app.MapPost("/api/pr/prepare", async (HttpRequest req) =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject ?? new JsonObject();
            string Str(string key) => body[key]?.GetValue<string>() ?? "";
            int number = body["number"] is JsonValue n && n.TryGetValue<int>(out var i) ? i : 0;
            return await Guard(async (r, t) =>
            {
                PendingAction pending;
                switch (Str("action"))
                {
                    case "create": pending = PullRequests.PrepareCreate(r, Str("title"), Str("body"), Str("head"), Str("base"), body["draft"]?.GetValue<bool>() == true, body["pushed"]?.GetValue<bool>() != false); break;
                    case "comment": pending = PullRequests.PrepareComment(r, number, Str("body")); break;
                    case "merge": pending = await PullRequests.PrepareMerge(r, t, number, Str("method")); break;
                    default: throw new ServiceException("That is not something to send.");
                }
                return new
                {
                    token = PullRequests.Issue(pending), action = pending.Action, title = pending.Title, lines = pending.Lines, warnings = pending.Warnings,
                    method = pending.Request.Method, url = pending.Request.Url, body = pending.Request.BodyText, host = r.Host,
                };
            });
        });

        app.MapPost("/api/pr/confirm", async (HttpRequest req) =>
        {
            var body = await JsonNode.ParseAsync(req.Body);
            if (PullRequests.Redeem(body?["token"]?.GetValue<string>()) is not { } pending)
                return Results.Json(new { error = "That confirmation is not valid any more (it can be used once, for five minutes). Ask again." });
            var (remote, _) = Locate();
            if (remote == null || remote.Repository != pending.Remote.Repository || remote.ApiBase != pending.Remote.ApiBase)
                return Results.Json(new { error = "The remote of the project is not the one that was shown. Nothing was sent." });
            try
            {
                var answer = await PullRequests.Carry(pending, ForgeTokens.Get(root, remote.TokenKey));
                return Results.Json(new { ok = true, action = pending.Action, created = pending.Action == "create" ? PullRequests.Created(remote, answer) : null });
            }
            catch (ServiceException e) { return Results.Json(new { error = e.Message }); }
        });
    }
}
