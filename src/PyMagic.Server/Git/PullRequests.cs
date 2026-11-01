using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Git;

/// <summary>The forge a project's remote lives on: GitHub or a GitLab, the repository on it, and the address of its API.</summary>
public sealed record ForgeRemote(string Kind, string Host, string Path, string RemoteName, string ApiBase)
{
    /// <summary>What the access token is kept under: one token per forge host.</summary>
    public string TokenKey => Kind + ":" + Host;
    public string Repository => Host + "/" + Path;
}

public sealed record PrSummary(int Number, string Title, string Author, string Source, string Target, string State, bool Draft, string Checks, string Url, string Updated);

public sealed record PrCommit(string Id, string Message, string Author, string Date);

public sealed record PrFile(string Path, string? OldPath, string Status, int Additions, int Deletions, string Patch);

public sealed record PrComment(string Author, string Body, string Date, string? Path, int? Line);

public sealed record PrCheck(string Name, string State);

public sealed record PrDetail(PrSummary Summary, string Body, bool? Mergeable, string HeadSha, List<PrCommit> Commits, List<PrFile> Files, List<PrComment> Comments, List<PrCheck> Checks);

/// <summary>One request to a forge, as it will be sent: shown to the user word for word before it is.</summary>
public sealed record PlannedRequest(string Method, string Url, JsonNode? Body)
{
    public string BodyText => Body == null ? "" : Body.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>An outward action waiting for the user's yes: what it is, and the request that carries it out.</summary>
public sealed record PendingAction(string Action, string Title, ForgeRemote Remote, PlannedRequest Request, IReadOnlyList<string> Lines, IReadOnlyList<string> Warnings);

/// <summary>The access tokens of forges, kept apart like the passwords of Deployment: in <c>.pymagic/pullrequests.private.json</c>, which stays out of version control, and never in a normal settings file.</summary>
public static class ForgeTokens
{
    static string File_(string root) => System.IO.Path.Combine(root, ".pymagic", "pullrequests.private.json");

    static Dictionary<string, string> Load(string root)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File_(root))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static string Get(string root, string key) => Load(root).GetValueOrDefault(key, "");
    public static bool Has(string root, string key) => Get(root, key).Length > 0;

    /// <summary>Keeps a token (an empty one forgets it). <exception cref="ServiceException">The text cannot be a token.</exception></summary>
    public static void Set(string root, string key, string token)
    {
        token = (token ?? "").Trim();
        if (token.Length > 512 || token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) throw new ServiceException("That is not an access token: a token is one word without spaces.");
        var all = Load(root);
        if (token.Length > 0) all[key] = token; else all.Remove(key);
        Directory.CreateDirectory(System.IO.Path.Combine(root, ".pymagic"));
        // the ignore entry first: the token is never in a file git could pick up
        PymagicFolder.IgnoreInGit(System.IO.Path.Combine(root, ".pymagic"), "pullrequests.private.json");
        System.IO.File.WriteAllText(File_(root), JsonSerializer.Serialize(all));
    }
}

/// <summary>
/// Pull requests (GitHub) and merge requests (GitLab) of the project's remote, over the forge's REST API: listing, one in full, and the three things that
/// reach out and change something there (creating, commenting, merging). Those are never done on the page's say-so alone: <see cref="Prepare"/> makes
/// the exact request and a single-use token for it, the page shows the request to the user, and only the token of what was shown is carried out
/// (<see cref="Redeem"/>), like installing from a git address. The API address comes from the environment (PYMAGIC_GITHUB_API, PYMAGIC_GITLAB_API), which
/// is how the tests point everything at a server of their own; a project file cannot change it, or a cloned repository could send the token elsewhere.
/// </summary>
public static class PullRequests
{
    // ------------------------------------------------------------ the remote

    static readonly Regex UrlForm = new(@"^(?:https?|ssh|git)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?/(?<path>.+?)(?:\.git)?/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ScpForm = new(@"^(?:[^@/\s]+@)?(?<host>[^:/\s]+):(?<path>[^\s]+?)(?:\.git)?/?$", RegexOptions.Compiled);
    static readonly Regex SegmentForm = new(@"^[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    public static ForgeRemote? Detect(IEnumerable<GitRemote> remotes)
    {
        var list = remotes.OrderBy(r => r.Name == "origin" ? 0 : 1).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        foreach (var r in list)
            if (FromUrl(r.Name, r.PushUrl.Length > 0 ? r.PushUrl : r.FetchUrl) is { } found) return found;
        return null;
    }

    public static ForgeRemote? FromUrl(string name, string url)
    {
        url = url.Trim();
        var m = url.Contains("://") ? UrlForm.Match(url) : ScpForm.Match(url);
        if (!m.Success) return null;
        string host = m.Groups["host"].Value.ToLowerInvariant();
        string path = m.Groups["path"].Value.Trim('/');
        var segments = path.Split('/');
        if (segments.Length < 2 || segments.Any(s => !SegmentForm.IsMatch(s) || s == "." || s == "..")) return null;
        bool enterprise = IsEnterpriseHost(host);
        string? kind = host is "github.com" or "www.github.com" || enterprise ? "github" : IsGitLabHost(host) ? "gitlab" : null;
        if (kind == null) return null;
        if (kind == "github" && segments.Length != 2) return null;
        if (kind == "github" && !enterprise) host = "github.com";
        string? api = ApiBase(kind, host);
        return api == null ? null : new ForgeRemote(kind, host, path, name, api);
    }

    /// <summary>A GitHub Enterprise server: a host named in PYMAGIC_GITHUB_HOSTS (a list with commas). Like the GitLab hosts, only the environment says so, never the project.</summary>
    static bool IsEnterpriseHost(string host) =>
        (Environment.GetEnvironmentVariable("PYMAGIC_GITHUB_HOSTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(host, StringComparer.OrdinalIgnoreCase);

    static bool IsGitLabHost(string host) =>
        host.Contains("gitlab") || (Environment.GetEnvironmentVariable("PYMAGIC_GITLAB_HOSTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The address of the API; null when the one configured is not a usable address (http is only for this computer, which is what the tests use).</summary>
    static string? ApiBase(string kind, string host)
    {
        string? set = Environment.GetEnvironmentVariable(kind == "github" ? "PYMAGIC_GITHUB_API" : "PYMAGIC_GITLAB_API");
        string text = !string.IsNullOrWhiteSpace(set) ? set.Trim() : kind == "github" ? (host == "github.com" ? "https://api.github.com" : $"https://{host}/api/v3") : $"https://{host}/api/v4";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return text.TrimEnd('/');
        return null;
    }

    // ------------------------------------------------------------ talking to the forge

    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };

    static string Redact(string text, string token) => token.Length >= 4 ? text.Replace(token, "***") : text;

    static string RepoUrl(ForgeRemote r, string tail) =>
        r.Kind == "github" ? $"{r.ApiBase}/repos/{r.Path}{tail}" : $"{r.ApiBase}/projects/{Uri.EscapeDataString(r.Path)}{tail}";

    /// <summary>Sends a request with the token (read when needed, never kept) and gives the JSON that came back. The token is only ever put in the Authorization header.</summary>
    public static async Task<JsonNode?> Send(ForgeRemote r, string token, PlannedRequest request)
    {
        if (token.Length == 0) throw new ServiceException($"No access token for {r.Host} yet: type one in the field above (it stays on this computer).");
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        message.Headers.UserAgent.ParseAdd("PyMagic");
        if (r.Kind == "github")
        {
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else message.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        if (request.Body != null) message.Content = new StringContent(request.Body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Http.SendAsync(message);
            string text = await response.Content.ReadAsStringAsync();
            int code = (int)response.StatusCode;
            if (code is >= 300 and < 400) throw new ServiceException($"{r.Host} answered with a redirect, which is not followed (the token is not sent anywhere else).");
            if (code >= 400) throw new ServiceException(Redact(ErrorOf(r, code, text), token));
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (HttpRequestException e) { throw new ServiceException(Redact($"Could not reach {r.Host}: {e.Message}", token)); }
        catch (TaskCanceledException) { throw new ServiceException($"{r.Host} did not answer in time."); }
        catch (JsonException) { throw new ServiceException($"{r.Host} sent an answer that is not JSON."); }
    }

    static string ErrorOf(ForgeRemote r, int code, string text)
    {
        string? said = null;
        try
        {
            var node = JsonNode.Parse(text);
            said = node?["message"] is JsonValue v ? v.ToString() : node?["message"]?.ToJsonString() ?? node?["error"]?.ToString();
        }
        catch (JsonException) { /* not JSON */ }
        string lead = code switch
        {
            401 => $"{r.Host} refused the token (401): it may be wrong, expired or missing the right to read pull requests.",
            403 => $"{r.Host} says the token is not allowed to do that (403).",
            404 => $"{r.Host} does not know {r.Path}, or the token cannot see it (404).",
            _ => $"{r.Host} answered {code}.",
        };
        return said == null ? lead : $"{lead} It said: {said}";
    }

    static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;
    static string S(JsonNode? node, params string[] path)
    {
        foreach (var p in path) node = Get(node, p);
        return node is JsonValue v ? v.ToString() : "";
    }
    static int I(JsonNode? node, string key) => Get(node, key) is JsonValue v && int.TryParse(v.ToString(), out var n) ? n : 0;
    static bool B(JsonNode? node, string key) => Get(node, key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? new JsonArray();

    static PlannedRequest Read(ForgeRemote r, string tail) => new("GET", RepoUrl(r, tail), null);

    // ------------------------------------------------------------ reading

    /// <summary>Requests listed on a page; a page with this many has another after it.</summary>
    public const int PageSize = 30;

    public static async Task<List<PrSummary>> List(ForgeRemote r, string token, int page = 1)
    {
        page = Math.Clamp(page, 1, 1000);
        if (r.Kind == "github")
        {
            var data = await Send(r, token, Read(r, $"/pulls?state=open&per_page={PageSize}&page={page}"));
            var rows = Items(data).Select(p => (p, sha: S(p, "head", "sha"))).ToList();
            var checks = await Task.WhenAll(rows.Select(x => GithubChecks(r, token, x.sha)));
            return rows.Select((x, i) => GithubSummary(x.p, checks[i].State)).ToList();
        }
        else
        {
            var data = await Send(r, token, Read(r, $"/merge_requests?state=opened&per_page={PageSize}&page={page}"));
            var rows = Items(data).ToList();
            var checks = await Task.WhenAll(rows.Select(p => GitlabChecks(r, token, I(p, "iid"))));
            return rows.Select((p, i) => GitlabSummary(p, checks[i])).ToList();
        }
    }

    static PrSummary GithubSummary(JsonNode? p, string checks) =>
        new(I(p, "number"), S(p, "title"), S(p, "user", "login"), S(p, "head", "ref"), S(p, "base", "ref"),
            B(p, "merged") || S(p, "merged_at").Length > 0 ? "merged" : S(p, "state") == "open" ? "open" : "closed", B(p, "draft"), checks, S(p, "html_url"), S(p, "updated_at"));

    static PrSummary GitlabSummary(JsonNode? p, string checks) =>
        new(I(p, "iid"), S(p, "title"), S(p, "author", "username"), S(p, "source_branch"), S(p, "target_branch"),
            S(p, "state") switch { "opened" => "open", "merged" => "merged", _ => "closed" }, B(p, "draft") || B(p, "work_in_progress"), checks, S(p, "web_url"), S(p, "updated_at"));

    /// <summary>"success", "failure", "pending" or "none" from the check runs and commit statuses of a commit.</summary>
    static async Task<(string State, List<PrCheck> Runs)> GithubChecks(ForgeRemote r, string token, string sha)
    {
        if (sha.Length == 0) return ("none", new());
        var runs = new List<PrCheck>();
        try
        {
            var data = await Send(r, token, Read(r, $"/commits/{Uri.EscapeDataString(sha)}/check-runs"));
            foreach (var c in Items(Get(data, "check_runs")))
            {
                string status = S(c, "status"), conclusion = S(c, "conclusion");
                runs.Add(new PrCheck(S(c, "name"), status != "completed" ? "pending" : conclusion is "success" or "neutral" or "skipped" ? "success" : "failure"));
            }
            var combined = await Send(r, token, Read(r, $"/commits/{Uri.EscapeDataString(sha)}/status"));
            foreach (var s in Items(Get(combined, "statuses")))
                runs.Add(new PrCheck(S(s, "context"), S(s, "state") switch { "success" => "success", "pending" => "pending", _ => "failure" }));
        }
        catch (ServiceException) { return ("unknown", runs); }
        return (runs.Count == 0 ? "none" : runs.Any(c => c.State == "failure") ? "failure" : runs.Any(c => c.State == "pending") ? "pending" : "success", runs);
    }

    static async Task<string> GitlabChecks(ForgeRemote r, string token, int iid)
    {
        try
        {
            var data = await Send(r, token, Read(r, $"/merge_requests/{iid}/pipelines"));
            var first = Items(data).FirstOrDefault();
            if (first == null) return "none";
            return S(first, "status") switch { "success" => "success", "failed" or "canceled" => "failure", "skipped" or "manual" => "none", _ => "pending" };
        }
        catch (ServiceException) { return "unknown"; }
    }

    public static async Task<PrDetail> Detail(ForgeRemote r, string token, int number)
    {
        if (number <= 0) throw new ServiceException("not a number of a pull request");
        if (r.Kind == "github")
        {
            var pr = await Send(r, token, Read(r, $"/pulls/{number}"));
            string sha = S(pr, "head", "sha");
            var commits = Send(r, token, Read(r, $"/pulls/{number}/commits?per_page=100"));
            var files = Send(r, token, Read(r, $"/pulls/{number}/files?per_page=100"));
            var issueComments = Send(r, token, Read(r, $"/issues/{number}/comments?per_page=100"));
            var reviewComments = Send(r, token, Read(r, $"/pulls/{number}/comments?per_page=100"));
            var checks = GithubChecks(r, token, sha);
            await Task.WhenAll(commits, files, issueComments, reviewComments, checks);
            var comments = Items(issueComments.Result).Select(c => new PrComment(S(c, "user", "login"), S(c, "body"), S(c, "created_at"), null, null))
                .Concat(Items(reviewComments.Result).Select(c => new PrComment(S(c, "user", "login"), S(c, "body"), S(c, "created_at"), S(c, "path"), Get(c, "line") is JsonValue v && int.TryParse(v.ToString(), out var line) ? line : null)))
                .OrderBy(c => c.Date, StringComparer.Ordinal).ToList();
            return new PrDetail(GithubSummary(pr, checks.Result.State), S(pr, "body"), Get(pr, "mergeable") is JsonValue m && m.TryGetValue<bool>(out var mb) ? mb : null, sha,
                Items(commits.Result).Select(c => new PrCommit(S(c, "sha"), S(c, "commit", "message"), S(c, "commit", "author", "name"), S(c, "commit", "author", "date"))).ToList(),
                Items(files.Result).Select(f => new PrFile(S(f, "filename"), S(f, "previous_filename") is { Length: > 0 } old ? old : null, S(f, "status") switch { "added" => "added", "removed" => "removed", "renamed" => "renamed", _ => "modified" }, I(f, "additions"), I(f, "deletions"), S(f, "patch"))).ToList(),
                comments, checks.Result.Runs);
        }
        else
        {
            var mr = await Send(r, token, Read(r, $"/merge_requests/{number}"));
            var commits = Send(r, token, Read(r, $"/merge_requests/{number}/commits?per_page=100"));
            var changes = Send(r, token, Read(r, $"/merge_requests/{number}/changes"));
            var notes = Send(r, token, Read(r, $"/merge_requests/{number}/notes?per_page=100&sort=asc"));
            var pipeline = GitlabChecks(r, token, number);
            await Task.WhenAll(commits, changes, notes, pipeline);
            string state = pipeline.Result;
            string status = S(mr, "detailed_merge_status") is { Length: > 0 } d ? d : S(mr, "merge_status");
            bool? mergeable = status.Length == 0 ? null : status is "mergeable" or "can_be_merged";
            return new PrDetail(GitlabSummary(mr, state), S(mr, "description"), mergeable, S(mr, "sha"),
                Items(commits.Result).Select(c => new PrCommit(S(c, "id"), S(c, "message") is { Length: > 0 } msg ? msg : S(c, "title"), S(c, "author_name"), S(c, "created_at"))).ToList(),
                Items(Get(changes.Result, "changes")).Select(f =>
                {
                    string diff = S(f, "diff");
                    bool added = B(f, "new_file"), removed = B(f, "deleted_file"), renamed = B(f, "renamed_file");
                    var lines = diff.Split('\n');
                    return new PrFile(S(f, "new_path"), renamed ? S(f, "old_path") : null, added ? "added" : removed ? "removed" : renamed ? "renamed" : "modified",
                        lines.Count(l => l.StartsWith('+') && !l.StartsWith("+++")), lines.Count(l => l.StartsWith('-') && !l.StartsWith("---")), diff);
                }).ToList(),
                Items(notes.Result).Where(n => !B(n, "system")).Select(n => new PrComment(S(n, "author", "username"), S(n, "body"), S(n, "created_at"), null, null)).ToList(),
                state == "none" ? new() : new() { new PrCheck("pipeline", state) });
        }
    }

    // ------------------------------------------------------------ the outward actions, planned

    static readonly Regex BranchForm = new(@"^[A-Za-z0-9_][A-Za-z0-9._/\-]*$", RegexOptions.Compiled);

    static string CheckText(string label, string? text, int max, bool single)
    {
        text = (text ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) throw new ServiceException($"The {label} is empty.");
        if (text.Length > max) throw new ServiceException($"The {label} is longer than {max} characters.");
        if (text.Any(c => char.IsControl(c) && (single || c is not ('\n' or '\t')))) throw new ServiceException($"The {label} has a control character in it.");
        return text;
    }

    static string CheckBranch(string label, string? name)
    {
        name = (name ?? "").Trim();
        if (!BranchForm.IsMatch(name) || name.Contains("..") || name.EndsWith('/') || name.EndsWith(".lock")) throw new ServiceException($"'{name}' is not a branch name for the {label}.");
        return name;
    }

    public static PendingAction PrepareCreate(ForgeRemote r, string title, string? body, string head, string baseBranch, bool draft, bool pushed)
    {
        title = CheckText("title", title, 256, true);
        string text = (body ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > 60000) throw new ServiceException("The description is longer than 60000 characters.");
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) throw new ServiceException("The description has a control character in it.");
        head = CheckBranch("branch to merge", head);
        baseBranch = CheckBranch("branch to merge into", baseBranch);
        if (head == baseBranch) throw new ServiceException("The branch and the one to merge into are the same.");
        string noun = r.Kind == "github" ? "pull request" : "merge request";
        var req = r.Kind == "github"
            ? new PlannedRequest("POST", RepoUrl(r, "/pulls"), new JsonObject { ["title"] = title, ["body"] = text, ["head"] = head, ["base"] = baseBranch, ["draft"] = draft })
            : new PlannedRequest("POST", RepoUrl(r, "/merge_requests"), new JsonObject { ["source_branch"] = head, ["target_branch"] = baseBranch, ["title"] = draft && !title.StartsWith("Draft:") ? "Draft: " + title : title, ["description"] = text });
        var warnings = new List<string>();
        if (!pushed) warnings.Add($"The branch {head} is not pushed to the remote as far as git can tell; {r.Host} will refuse it until it is.");
        return new PendingAction("create", $"Create a {noun} on {r.Host}", r, req,
            new[] { $"Repository: {r.Repository}", $"Merge {head} into {baseBranch}", $"Title: {title}", draft ? "As a draft" : "Ready for review" }, warnings);
    }

    public static PendingAction PrepareComment(ForgeRemote r, int number, string body)
    {
        if (number <= 0) throw new ServiceException("not a number of a pull request");
        string text = CheckText("comment", body, 60000, false);
        string noun = r.Kind == "github" ? "pull request" : "merge request";
        var req = r.Kind == "github"
            ? new PlannedRequest("POST", RepoUrl(r, $"/issues/{number}/comments"), new JsonObject { ["body"] = text })
            : new PlannedRequest("POST", RepoUrl(r, $"/merge_requests/{number}/notes"), new JsonObject { ["body"] = text });
        return new PendingAction("comment", $"Post a comment on {noun} #{number} on {r.Host}", r, req,
            new[] { $"Repository: {r.Repository}", $"{noun} #{number}", "The comment is public to everyone who can see the repository, under your name." }, Array.Empty<string>());
    }

    static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    /// <summary>The line numbers of the new side that a patch shows (the added lines and the ones around them): the lines a comment can be put on.</summary>
    public static HashSet<int> NewSideLines(string? patch)
    {
        var lines = new HashSet<int>();
        int next = 0;
        foreach (var raw in (patch ?? "").Split('\n'))
        {
            var header = HunkHeader.Match(raw);
            if (header.Success) { next = int.Parse(header.Groups[1].Value); continue; }
            if (next == 0 || raw.Length == 0 || raw[0] == '\\' || raw[0] == '-') continue;
            if (raw[0] is '+' or ' ') lines.Add(next++);
        }
        return lines;
    }

    /// <summary>
    /// A review: an approval, a request for changes, or a comment on the whole (GitHub), or an approval taken back (GitLab, which has no other kind). It is pinned to the head commit
    /// that was looked at, so a push in between makes the forge refuse it instead of approving what was not seen.
    /// </summary>
    public static async Task<PendingAction> PrepareReview(ForgeRemote r, string token, int number, string verdict, string? body)
    {
        if (number <= 0) throw new ServiceException("not a number of a pull request");
        verdict = (verdict ?? "").Trim().ToLowerInvariant();
        var detail = await Detail(r, token, number);
        var s = detail.Summary;
        if (s.State != "open") throw new ServiceException($"#{number} is {s.State}: there is nothing to review.");
        if (detail.HeadSha.Length == 0) throw new ServiceException("The forge did not say which commit the branch is at, so the review cannot be pinned to what was looked at.");
        string noun = r.Kind == "github" ? "pull request" : "merge request";
        string text = string.IsNullOrWhiteSpace(body) ? "" : CheckText("review", body, 60000, false);
        PlannedRequest req;
        string what;
        var warnings = new List<string>();
        if (r.Kind == "github")
        {
            string eventName = verdict switch { "approve" => "APPROVE", "request_changes" => "REQUEST_CHANGES", "comment" => "COMMENT", _ => throw new ServiceException($"{r.Host} takes a review that approves, asks for changes or comments, not '{verdict}'.") };
            if (verdict != "approve" && text.Length == 0) throw new ServiceException(verdict == "comment" ? "A review that only comments needs a few words." : "A request for changes needs a few words saying what to change.");
            var content = new JsonObject { ["event"] = eventName, ["commit_id"] = detail.HeadSha };
            if (text.Length > 0) content["body"] = text;
            req = new PlannedRequest("POST", RepoUrl(r, $"/pulls/{number}/reviews"), content);
            what = verdict switch { "approve" => "Approve it", "request_changes" => "Ask for changes", _ => "Review with a comment" };
        }
        else
        {
            if (verdict == "approve") { req = new PlannedRequest("POST", RepoUrl(r, $"/merge_requests/{number}/approve"), new JsonObject { ["sha"] = detail.HeadSha }); what = "Approve it"; }
            else if (verdict == "unapprove") { req = new PlannedRequest("POST", RepoUrl(r, $"/merge_requests/{number}/unapprove"), null); what = "Take your approval back"; }
            else throw new ServiceException($"{r.Host} knows approving and taking an approval back; a comment goes through Comment.");
            if (text.Length > 0) warnings.Add("GitLab keeps no words with an approval: the text is not sent. Use Comment for it.");
        }
        if (verdict == "approve") warnings.Add($"An approval says you have read the changes; it is shown on {noun} #{number} under your name.");
        return new PendingAction("review", $"{what}: {noun} #{number} on {r.Host}", r, req,
            new[] { $"Repository: {r.Repository}", $"#{number} {s.Title}", what, $"Only if the branch is still at {detail.HeadSha[..Math.Min(10, detail.HeadSha.Length)]}" }, warnings);
    }

    /// <summary>A comment on one line of one file of the changes. The line must be one the patch shows: the forges refuse any other.</summary>
    public static async Task<PendingAction> PrepareInline(ForgeRemote r, string token, int number, string path, int line, string? body)
    {
        if (number <= 0) throw new ServiceException("not a number of a pull request");
        string text = CheckText("comment", body, 60000, false);
        var detail = await Detail(r, token, number);
        var s = detail.Summary;
        if (s.State != "open") throw new ServiceException($"#{number} is {s.State}: comments are for requests that are open.");
        var file = detail.Files.FirstOrDefault(f => f.Path == path) ?? throw new ServiceException($"{path} is not among the files of #{number}.");
        if (line <= 0 || !NewSideLines(file.Patch).Contains(line))
            throw new ServiceException($"Line {line} of {path} is not in what the changes show; {r.Host} takes a comment only on the lines of the changes and the ones around them.");
        string noun = r.Kind == "github" ? "pull request" : "merge request";
        PlannedRequest req;
        if (r.Kind == "github")
        {
            if (detail.HeadSha.Length == 0) throw new ServiceException("The forge did not say which commit the branch is at, so the comment cannot be pinned to what was looked at.");
            req = new PlannedRequest("POST", RepoUrl(r, $"/pulls/{number}/comments"),
                new JsonObject { ["body"] = text, ["commit_id"] = detail.HeadSha, ["path"] = path, ["line"] = line, ["side"] = "RIGHT" });
        }
        else
        {
            // GitLab places a comment by the three commits of the comparison, which only the request itself can tell
            var mr = await Send(r, token, Read(r, $"/merge_requests/{number}"));
            var refs = mr?["diff_refs"] as JsonObject;
            string baseSha = S(refs, "base_sha"), startSha = S(refs, "start_sha"), headSha = S(refs, "head_sha");
            if (baseSha.Length == 0 || startSha.Length == 0 || headSha.Length == 0) throw new ServiceException("GitLab did not say which commits the changes compare, so a comment cannot be placed on a line.");
            req = new PlannedRequest("POST", RepoUrl(r, $"/merge_requests/{number}/discussions"), new JsonObject
            {
                ["body"] = text,
                ["position"] = new JsonObject
                {
                    ["position_type"] = "text", ["base_sha"] = baseSha, ["start_sha"] = startSha, ["head_sha"] = headSha,
                    ["old_path"] = file.OldPath ?? path, ["new_path"] = path, ["new_line"] = line,
                },
            });
        }
        return new PendingAction("inline", $"Comment on {path}:{line} of {noun} #{number} on {r.Host}", r, req,
            new[] { $"Repository: {r.Repository}", $"{noun} #{number}", $"{path}, line {line}", "The comment is public to everyone who can see the repository, under your name." }, Array.Empty<string>());
    }

    /// <summary>The merge of the pull request as it is now: the head commit seen goes with the request, so a push in between makes the forge refuse instead of merging what was not looked at.</summary>
    public static async Task<PendingAction> PrepareMerge(ForgeRemote r, string token, int number, string method)
    {
        string[] allowed = r.Kind == "github" ? new[] { "merge", "squash", "rebase" } : new[] { "merge", "squash" };
        method = (method ?? "merge").Trim().ToLowerInvariant();
        if (!allowed.Contains(method)) throw new ServiceException($"{r.Host} merges by {string.Join(", ", allowed)}, not by '{method}'.");
        var detail = await Detail(r, token, number);
        var s = detail.Summary;
        if (s.State != "open") throw new ServiceException($"#{number} is {s.State}, there is nothing to merge.");
        if (detail.HeadSha.Length == 0) throw new ServiceException("The forge did not say which commit the branch is at, so the merge cannot be pinned to what was looked at.");
        string noun = r.Kind == "github" ? "pull request" : "merge request";
        var req = r.Kind == "github"
            ? new PlannedRequest("PUT", RepoUrl(r, $"/pulls/{number}/merge"), new JsonObject { ["merge_method"] = method, ["sha"] = detail.HeadSha })
            : new PlannedRequest("PUT", RepoUrl(r, $"/merge_requests/{number}/merge"), new JsonObject { ["sha"] = detail.HeadSha, ["squash"] = method == "squash" });
        var warnings = new List<string> { $"Merging changes {s.Target} on {r.Host}; it cannot be undone from here." };
        if (s.Checks == "failure") warnings.Add("The checks of this branch have failed.");
        else if (s.Checks == "pending") warnings.Add("The checks of this branch have not finished.");
        if (detail.Mergeable == false) warnings.Add($"{r.Host} says it cannot be merged cleanly.");
        if (s.Draft) warnings.Add("It is a draft.");
        return new PendingAction("merge", $"Merge {noun} #{number} on {r.Host}", r, req,
            new[] { $"Repository: {r.Repository}", $"#{number} {s.Title}", $"Merge {s.Source} into {s.Target} ({method})", $"Only if the branch is still at {detail.HeadSha[..Math.Min(10, detail.HeadSha.Length)]}" }, warnings);
    }

    // ------------------------------------------------------------ the confirmation: one token for one request that was shown, good once and for a few minutes

    static readonly ConcurrentDictionary<string, (PendingAction Action, DateTime Until)> Issued = new();

    public static string Issue(PendingAction action)
    {
        foreach (var (k, v) in Issued) if (v.Until < DateTime.UtcNow) Issued.TryRemove(k, out _);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Issued[token] = (action, DateTime.UtcNow.AddMinutes(5));
        return token;
    }

    /// <summary>The action for a token, once; null when the token is unknown, used, or too old.</summary>
    public static PendingAction? Redeem(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Issued.TryRemove(token, out var entry)) return null;
        return entry.Until >= DateTime.UtcNow ? entry.Action : null;
    }

    /// <summary>Carries out a confirmed action: exactly the request that was shown, with the token of the forge.</summary>
    public static async Task<JsonNode?> Carry(PendingAction action, string token) => await Send(action.Remote, token, action.Request);

    /// <summary>What a created pull request or merge request is, from the forge's answer.</summary>
    public static PrSummary? Created(ForgeRemote r, JsonNode? answer) => answer == null ? null : r.Kind == "github" ? GithubSummary(answer, "none") : GitlabSummary(answer, "none");
}
