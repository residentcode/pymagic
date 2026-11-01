using System.Text.Json.Nodes;
using PyMagic.Server;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>
/// Pull requests and merge requests (GitHub, GitLab): the remote, the token store, reading, and that the outward actions are only done with the token of the
/// request that was shown. Only fake servers on this computer (<see cref="FakeForge"/>) are used: the real github.com and gitlab.com are never contacted.
/// </summary>
public class PullRequestTests
{
    static string Temp() { string p = Path.Combine(Path.GetTempPath(), "pm_pr_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }

    static T Wait<T>(Task<T> t) => t.GetAwaiter().GetResult();
    static void Wait(Task t) => t.GetAwaiter().GetResult();
    static string Said(Action a) { try { a(); } catch (ServiceException e) { return e.Message; } return "(no exception)"; }
    static T One<T>(IEnumerable<T> items) { var list = items.ToList(); Assert.Equal(1, list.Count); return list[0]; }
    static string Join(IEnumerable<string> items) => string.Join("|", items);

    static ForgeRemote Remote(FakeForge fake, bool github)
    {
        string name = github ? "PYMAGIC_GITHUB_API" : "PYMAGIC_GITLAB_API";
        Environment.SetEnvironmentVariable(name, fake.BaseUrl);
        try { return PullRequests.FromUrl("origin", github ? "https://github.com/acme/widgets.git" : "https://gitlab.example.org/team/sub/proj.git")!; }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    static (string, string, string) Tuple(ForgeRemote? r) => (r!.Kind, r.Host, r.Path);

    [Fact]
    public void TheRemoteIsFoundInAnyFormOfAddress()
    {
        Assert.Equal(("github", "github.com", "acme/widgets"), Tuple(PullRequests.FromUrl("origin", "https://github.com/acme/widgets.git")));
        Assert.Equal(("github", "github.com", "acme/widgets"), Tuple(PullRequests.FromUrl("origin", "git@github.com:acme/widgets.git")));
        Assert.Equal(("github", "github.com", "acme/widgets"), Tuple(PullRequests.FromUrl("origin", "ssh://git@github.com/acme/widgets")));
        Assert.Equal(("gitlab", "gitlab.com", "team/sub/proj"), Tuple(PullRequests.FromUrl("origin", "https://user@gitlab.com/team/sub/proj.git")));
        Assert.Equal(("gitlab", "gitlab.example.org", "team/proj"), Tuple(PullRequests.FromUrl("o", "git@gitlab.example.org:team/proj.git")));
        Assert.Equal("https://api.github.com", PullRequests.FromUrl("origin", "https://github.com/a/b")!.ApiBase);
        Assert.Equal("https://gitlab.com/api/v4", PullRequests.FromUrl("origin", "https://gitlab.com/a/b")!.ApiBase);
        Assert.True(PullRequests.FromUrl("origin", "https://example.com/a/b.git") == null);
        Assert.True(PullRequests.FromUrl("origin", "C:\\repos\\thing") == null);
        Assert.True(PullRequests.FromUrl("origin", "https://github.com/a") == null);
        Assert.True(PullRequests.FromUrl("origin", "https://github.com/a/b/c") == null);
        Assert.True(PullRequests.FromUrl("origin", "https://github.com/a/../b") == null);
        var remotes = new[] { new GitRemote("backup", "https://example.com/x/y", "https://example.com/x/y"), new GitRemote("fork", "https://github.com/me/w", "https://github.com/me/w"), new GitRemote("origin", "https://github.com/acme/w", "https://github.com/acme/w") };
        Assert.Equal("acme/w", PullRequests.Detect(remotes)!.Path);
        Assert.Equal("me/w", PullRequests.Detect(remotes.Where(r => r.Name != "origin"))!.Path);
        Assert.True(PullRequests.Detect(remotes.Take(1)) == null);
    }

    [Fact]
    public void TheApiAddressComesFromTheEnvironmentAndAnUnsafeOneIsNotUsed()
    {
        try
        {
            Environment.SetEnvironmentVariable("PYMAGIC_GITHUB_API", "http://127.0.0.1:5999/");
            Assert.Equal("http://127.0.0.1:5999", PullRequests.FromUrl("origin", "https://github.com/a/b")!.ApiBase);
            // plain http to another computer would send the token in the clear
            Environment.SetEnvironmentVariable("PYMAGIC_GITHUB_API", "http://example.org/api");
            Assert.True(PullRequests.FromUrl("origin", "https://github.com/a/b") == null);
            Environment.SetEnvironmentVariable("PYMAGIC_GITHUB_API", "ftp://example.org/api");
            Assert.True(PullRequests.FromUrl("origin", "https://github.com/a/b") == null);
            Environment.SetEnvironmentVariable("PYMAGIC_GITLAB_HOSTS", "code.example.org");
            Assert.Equal("gitlab", PullRequests.FromUrl("origin", "https://code.example.org/a/b")!.Kind);
        }
        finally { Environment.SetEnvironmentVariable("PYMAGIC_GITHUB_API", null); Environment.SetEnvironmentVariable("PYMAGIC_GITLAB_HOSTS", null); }
    }

    [Fact]
    public void AGitHubEnterpriseServerIsOneThatTheEnvironmentNames()
    {
        try
        {
            Assert.True(PullRequests.FromUrl("origin", "https://ghe.example.com/acme/widgets.git") == null, "not named yet");
            Environment.SetEnvironmentVariable("PYMAGIC_GITHUB_HOSTS", "other.example.com, GHE.example.com");
            var ghe = PullRequests.FromUrl("origin", "git@ghe.example.com:acme/widgets.git")!;
            Assert.Equal(("github", "ghe.example.com", "acme/widgets"), Tuple(ghe));
            Assert.Equal("https://ghe.example.com/api/v3", ghe.ApiBase);
            Assert.Equal("github:ghe.example.com", ghe.TokenKey);
            // github.com itself is unchanged, and a server that is named still needs owner/repo
            Assert.Equal("https://api.github.com", PullRequests.FromUrl("origin", "https://github.com/acme/widgets")!.ApiBase);
            Assert.True(PullRequests.FromUrl("origin", "https://ghe.example.com/a/b/c") == null);
        }
        finally { Environment.SetEnvironmentVariable("PYMAGIC_GITHUB_HOSTS", null); }
    }

    [Fact]
    public void TheListIsReadOnePageAtATime()
    {
        using var fake = new FakeForge(github: true) { ManyPages = true };
        var remote = Remote(fake, github: true);
        var first = Wait(PullRequests.List(remote, "tok-test"));
        Assert.Equal(PullRequests.PageSize, first.Count);
        Assert.Equal(100, first[0].Number);
        var second = Wait(PullRequests.List(remote, "tok-test", 2));
        Assert.Equal(2, second.Count);
        Assert.Equal(130, second[0].Number);
        Assert.True(fake.Requests.Any(r => r.Path.StartsWith("/repos/acme/widgets/pulls")), "asked for the pulls");
    }

    [Fact]
    public void ATokenIsKeptInThePrivateFileAndNowhereElse()
    {
        string root = Temp();
        try
        {
            Assert.True(!ForgeTokens.Has(root, "github:github.com"));
            ForgeTokens.Set(root, "github:github.com", "  ghp_secret123  ");
            Assert.Equal("ghp_secret123", ForgeTokens.Get(root, "github:github.com"));
            string folder = Path.Combine(root, ".pymagic");
            Assert.Contains("ghp_secret123", File.ReadAllText(Path.Combine(folder, "pullrequests.private.json")));
            Assert.True(File.ReadAllLines(Path.Combine(folder, ".gitignore")).Contains("pullrequests.private.json"), "ignored by git");
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("pullrequests.private.json")))
                Assert.True(!File.ReadAllText(file).Contains("ghp_secret123"), file);
            Assert.Contains("one word", Said(() => ForgeTokens.Set(root, "github:github.com", "two words")));
            Assert.Contains("one word", Said(() => ForgeTokens.Set(root, "github:github.com", "line\nbreak")));
            Assert.Equal("ghp_secret123", ForgeTokens.Get(root, "github:github.com"));
            ForgeTokens.Set(root, "github:github.com", "");
            Assert.True(!ForgeTokens.Has(root, "github:github.com"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GithubPullRequestsAreListedAndOneIsReadInFull()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        Assert.Equal(fake.BaseUrl, r.ApiBase);
        var one = One(Wait(PullRequests.List(r, "tok-test")));
        Assert.Equal((7, "Add the cart", "ada", "cart", "main", "open", false), (one.Number, one.Title, one.Author, one.Source, one.Target, one.State, one.Draft));
        Assert.Equal("pending", one.Checks);   // one run finished, one has not
        var d = Wait(PullRequests.Detail(r, "tok-test", 7));
        Assert.Equal("Adds a cart.\n\nCloses nothing.", d.Body);
        Assert.True(d.Mergeable == true);
        Assert.Equal("cart: the model\n\nbody", One(d.Commits).Message);
        Assert.Equal("cart.py|new.py", Join(d.Files.Select(f => f.Path)));
        Assert.Equal(("modified", 2, 1), (d.Files[0].Status, d.Files[0].Additions, d.Files[0].Deletions));
        Assert.Equal("added", d.Files[1].Status);
        Assert.Contains("-    return 0", d.Files[0].Patch);
        Assert.Equal("bob|cy", Join(d.Comments.Select(c => c.Author)));
        Assert.Equal(("cart.py", 2), (d.Comments[1].Path!, d.Comments[1].Line!.Value));
        Assert.Equal("tests:success|lint:pending", Join(d.Checks.Select(c => c.Name + ":" + c.State)));
        Assert.True(fake.Requests.All(q => q.Method == "GET"), "reading only reads");
        Assert.True(fake.Requests.All(q => q.Auth == "Bearer tok-test"));
    }

    [Fact]
    public void AWrongTokenIsRefusedInWordsThatDoNotRepeatIt()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        string said = Said(() => Wait(PullRequests.List(r, "tok-wrong-9999")));
        Assert.Contains("refused the token (401)", said);
        Assert.Contains("Bad credentials", said);
        Assert.True(!said.Contains("tok-wrong-9999"), said);
        Assert.Contains("No access token", Said(() => Wait(PullRequests.List(r, ""))));
        Assert.True(!fake.Requests.Any(q => q.Auth == "Bearer "), "without a token nothing is even sent");
    }

    [Fact]
    public void ARedirectIsNotFollowedSoTheTokenGoesNowhereElse()
    {
        using var fake = new FakeForge(github: true);
        using var other = new FakeForge(github: true);
        fake.RedirectTo = other.BaseUrl + "/repos/acme/widgets/pulls";
        var r = Remote(fake, true);
        Assert.Contains("redirect", Said(() => Wait(PullRequests.List(r, "tok-test"))));
        Assert.Equal(0, other.Requests.Count);
    }

    [Fact]
    public void PreparingSendsNothingAndTheConfirmationTokenWorksOnceForTheRequestShown()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        var comment = PullRequests.PrepareComment(r, 7, "Thanks, merging soon.");
        Assert.Equal("POST", comment.Request.Method);
        Assert.True(comment.Request.Url.EndsWith("/repos/acme/widgets/issues/7/comments"), comment.Request.Url);
        Assert.Contains("Thanks, merging soon.", comment.Request.BodyText);
        string token = PullRequests.Issue(comment);
        Assert.Equal(0, fake.Requests.Count);
        Assert.True(PullRequests.Redeem("not-a-token") == null);
        Assert.True(PullRequests.Redeem(null) == null);
        var redeemed = PullRequests.Redeem(token);
        Assert.True(redeemed != null);
        Assert.True(PullRequests.Redeem(token) == null, "once");
        Wait(PullRequests.Carry(redeemed!, "tok-test"));
        var sent = One(fake.Writes);
        Assert.Equal("/repos/acme/widgets/issues/7/comments", sent.Path);
        Assert.Equal("Thanks, merging soon.", JsonNode.Parse(sent.Body)!["body"]!.GetValue<string>());
        // what the user was shown is what went: the same JSON
        Assert.Equal(JsonNode.Parse(comment.Request.BodyText)!.ToJsonString(), JsonNode.Parse(sent.Body)!.ToJsonString());
    }

    [Fact]
    public void APullRequestIsCreatedWithWhatTheFormSaid()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        var pending = PullRequests.PrepareCreate(r, "Add search", "Does a search.", "search", "main", draft: true, pushed: false);
        Assert.Contains("not pushed", One(pending.Warnings));
        Assert.Equal(0, fake.Requests.Count);
        var answer = Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(pending))!, "tok-test"));
        var sent = JsonNode.Parse(One(fake.Writes).Body)!;
        Assert.Equal(("Add search", "Does a search.", "search", "main", true), (sent["title"]!.GetValue<string>(), sent["body"]!.GetValue<string>(), sent["head"]!.GetValue<string>(), sent["base"]!.GetValue<string>(), sent["draft"]!.GetValue<bool>()));
        var made = PullRequests.Created(r, answer)!;
        Assert.Equal((8, "Add search", "search"), (made.Number, made.Title, made.Source));
    }

    [Fact]
    public void WhatIsSentIsCheckedBeforeAnythingIsPrepared()
    {
        var r = PullRequests.FromUrl("origin", "https://github.com/acme/widgets")!;
        Assert.Contains("title is empty", Said(() => PullRequests.PrepareCreate(r, "  ", "", "a", "main", false, true)));
        Assert.Contains("control character", Said(() => PullRequests.PrepareCreate(r, "x\ny", "", "a", "main", false, true)));
        Assert.Contains("not a branch name", Said(() => PullRequests.PrepareCreate(r, "t", "", "-x", "main", false, true)));
        Assert.Contains("not a branch name", Said(() => PullRequests.PrepareCreate(r, "t", "", "a..b", "main", false, true)));
        Assert.Contains("same", Said(() => PullRequests.PrepareCreate(r, "t", "", "main", "main", false, true)));
        Assert.Contains("comment is empty", Said(() => PullRequests.PrepareComment(r, 7, " ")));
        Assert.Contains("not a number", Said(() => PullRequests.PrepareComment(r, 0, "x")));
        Assert.Contains("longer than", Said(() => PullRequests.PrepareComment(r, 7, new string('x', 60001))));
    }

    [Fact]
    public void TheLinesOfAPatchThatTakeACommentAreTheNewSideOnes()
    {
        const string patch = "@@ -1,2 +1,3 @@\n def total():\n-    return 0\n+    return 1\n+    # more\n@@ -10,2 +20,2 @@\n keep\n-old\n+new\n\\ No newline at end of file\n";
        Assert.Equal("1,2,3,20,21", string.Join(",", PullRequests.NewSideLines(patch).OrderBy(x => x)));
        Assert.Equal("1", string.Join(",", PullRequests.NewSideLines("@@ -0,0 +1 @@\n+x = 1\n")));
        Assert.Equal(0, PullRequests.NewSideLines("").Count);
        Assert.Equal(0, PullRequests.NewSideLines(null).Count);
    }

    [Fact]
    public void AReviewApprovesAsksForChangesOrCommentsAndIsPinnedToTheHeadThatWasLookedAt()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        var approve = Wait(PullRequests.PrepareReview(r, "tok-test", 7, "approve", ""));
        Assert.Equal("POST", approve.Request.Method);
        Assert.True(approve.Request.Url.EndsWith("/repos/acme/widgets/pulls/7/reviews"), approve.Request.Url);
        var shown = JsonNode.Parse(approve.Request.BodyText)!;
        Assert.Equal(("APPROVE", fake.HeadSha), (shown["event"]!.GetValue<string>(), shown["commit_id"]!.GetValue<string>()));
        Assert.True(shown["body"] == null, "no words with an approval unless there are some");
        Assert.True(approve.Warnings.Any(w => w.Contains("read the changes")));
        Assert.Equal(0, fake.Writes.Count());

        // asking for changes and commenting need words; the words go as typed
        Assert.Contains("few words", Said(() => Wait(PullRequests.PrepareReview(r, "tok-test", 7, "request_changes", " "))));
        Assert.Contains("few words", Said(() => Wait(PullRequests.PrepareReview(r, "tok-test", 7, "comment", ""))));
        Assert.Contains("approves, asks for changes or comments", Said(() => Wait(PullRequests.PrepareReview(r, "tok-test", 7, "merge", "x"))));
        var changes = Wait(PullRequests.PrepareReview(r, "tok-test", 7, "request_changes", "Please test the empty cart."));
        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(changes))!, "tok-test"));
        var sent = JsonNode.Parse(One(fake.Writes).Body)!;
        Assert.Equal(("REQUEST_CHANGES", "Please test the empty cart."), (sent["event"]!.GetValue<string>(), sent["body"]!.GetValue<string>()));

        // somebody pushes meanwhile: the forge refuses a review of what was not looked at
        string looked = fake.HeadSha;
        var stale = Wait(PullRequests.PrepareReview(r, "tok-test", 7, "approve", ""));
        fake.HeadSha = "ffff0000ffff0000";
        Assert.Contains("422", Said(() => Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(stale))!, "tok-test"))));
        fake.HeadSha = looked;
    }

    [Fact]
    public void GitLabHasApprovalsAndTakingThemBack()
    {
        using var fake = new FakeForge(github: false);
        var r = Remote(fake, false);
        var approve = Wait(PullRequests.PrepareReview(r, "tok-test", 3, "approve", ""));
        Assert.True(approve.Request.Url.EndsWith("/merge_requests/3/approve"), approve.Request.Url);
        Assert.Equal(fake.HeadSha, JsonNode.Parse(approve.Request.BodyText)!["sha"]!.GetValue<string>());
        var withWords = Wait(PullRequests.PrepareReview(r, "tok-test", 3, "approve", "looks fine"));
        Assert.True(withWords.Warnings.Any(w => w.Contains("keeps no words")));
        var back = Wait(PullRequests.PrepareReview(r, "tok-test", 3, "unapprove", ""));
        Assert.True(back.Request.Url.EndsWith("/merge_requests/3/unapprove"));
        Assert.Contains("approving and taking an approval back", Said(() => Wait(PullRequests.PrepareReview(r, "tok-test", 3, "request_changes", "x"))));
        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(approve))!, "tok-test"));
        Assert.True(One(fake.Writes).Path.EndsWith("/merge_requests/3/approve"));
        // GitHub has no way to take an approval back from here
        using var gh = new FakeForge(github: true);
        Assert.Contains("approves, asks for changes or comments", Said(() => Wait(PullRequests.PrepareReview(Remote(gh, true), "tok-test", 7, "unapprove", ""))));
    }

    [Fact]
    public void AnInlineCommentGoesOnALineThePatchShowsAndNowhereElse()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        var pending = Wait(PullRequests.PrepareInline(r, "tok-test", 7, "cart.py", 3, "Why one?"));
        Assert.True(pending.Request.Url.EndsWith("/repos/acme/widgets/pulls/7/comments"), pending.Request.Url);
        var shown = JsonNode.Parse(pending.Request.BodyText)!;
        Assert.Equal(("cart.py", 3, "RIGHT", fake.HeadSha, "Why one?"),
            (shown["path"]!.GetValue<string>(), shown["line"]!.GetValue<int>(), shown["side"]!.GetValue<string>(), shown["commit_id"]!.GetValue<string>(), shown["body"]!.GetValue<string>()));
        Assert.Equal(0, fake.Writes.Count());
        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(pending))!, "tok-test"));
        Assert.Equal("/repos/acme/widgets/pulls/7/comments", One(fake.Writes).Path);

        Assert.Contains("not in what the changes show", Said(() => Wait(PullRequests.PrepareInline(r, "tok-test", 7, "cart.py", 40, "x"))));
        Assert.Contains("not in what the changes show", Said(() => Wait(PullRequests.PrepareInline(r, "tok-test", 7, "cart.py", 0, "x"))));
        Assert.Contains("not among the files", Said(() => Wait(PullRequests.PrepareInline(r, "tok-test", 7, "other.py", 1, "x"))));
        Assert.Contains("comment is empty", Said(() => Wait(PullRequests.PrepareInline(r, "tok-test", 7, "cart.py", 1, " "))));
        Assert.Equal("new.py", JsonNode.Parse(Wait(PullRequests.PrepareInline(r, "tok-test", 7, "new.py", 1, "ok")).Request.BodyText)!["path"]!.GetValue<string>());
    }

    [Fact]
    public void OnGitLabAnInlineCommentIsPlacedByTheThreeCommitsOfTheComparison()
    {
        using var fake = new FakeForge(github: false);
        var r = Remote(fake, false);
        var pending = Wait(PullRequests.PrepareInline(r, "tok-test", 3, "a.py", 2, "and y?"));
        Assert.True(pending.Request.Url.EndsWith("/merge_requests/3/discussions"), pending.Request.Url);
        var shown = JsonNode.Parse(pending.Request.BodyText)!;
        var pos = shown["position"]!;
        Assert.Equal(("text", "b0b0b0b0", "5a5a5a5a", fake.HeadSha, "a.py", 2), (pos["position_type"]!.GetValue<string>(), pos["base_sha"]!.GetValue<string>(), pos["start_sha"]!.GetValue<string>(), pos["head_sha"]!.GetValue<string>(), pos["new_path"]!.GetValue<string>(), pos["new_line"]!.GetValue<int>()));
        Assert.Equal("and y?", shown["body"]!.GetValue<string>());
        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(pending))!, "tok-test"));
        Assert.True(One(fake.Writes).Path.EndsWith("/merge_requests/3/discussions"));
        Assert.Contains("not in what the changes show", Said(() => Wait(PullRequests.PrepareInline(r, "tok-test", 3, "a.py", 9, "x"))));
    }

    [Fact]
    public void AMergeIsPinnedToTheCommitThatWasLookedAt()
    {
        using var fake = new FakeForge(github: true);
        var r = Remote(fake, true);
        var pending = Wait(PullRequests.PrepareMerge(r, "tok-test", 7, "squash"));
        Assert.True(pending.Warnings.Any(w => w.Contains("not finished")), "the unfinished checks are said");
        var shown = JsonNode.Parse(pending.Request.BodyText)!;
        Assert.Equal(("squash", fake.HeadSha), (shown["merge_method"]!.GetValue<string>(), shown["sha"]!.GetValue<string>()));
        Assert.Equal(0, fake.Writes.Count());
        // someone pushes meanwhile: the forge refuses and nothing is merged
        string looked = fake.HeadSha;
        fake.HeadSha = "ffff0000ffff0000";
        Assert.Contains("Head branch was modified", Said(() => Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(pending))!, "tok-test"))));
        Assert.True(!fake.Merged);
        // asked again, it is pinned to the new commit and goes through
        var again = Wait(PullRequests.PrepareMerge(r, "tok-test", 7, "merge"));
        Assert.Contains(fake.HeadSha, again.Request.BodyText);
        Assert.True(!again.Request.BodyText.Contains(looked));
        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(again))!, "tok-test"));
        Assert.True(fake.Merged);
        Assert.Contains("nothing to merge", Said(() => Wait(PullRequests.PrepareMerge(r, "tok-test", 7, "merge"))));
        Assert.Contains("merges by merge, squash, rebase", Said(() => Wait(PullRequests.PrepareMerge(r, "tok-test", 7, "fast-forward"))));
    }

    [Fact]
    public void GitlabMergeRequestsAreListedReadCreatedCommentedAndMerged()
    {
        using var fake = new FakeForge(github: false);
        var r = Remote(fake, false);
        Assert.Equal(("gitlab", "team/sub/proj"), (r.Kind, r.Path));
        var one = One(Wait(PullRequests.List(r, "tok-test")));
        Assert.Equal((3, "eve", "tidy", "main", "open", true, "failure"), (one.Number, one.Author, one.Source, one.Target, one.State, one.Draft, one.Checks));
        var d = Wait(PullRequests.Detail(r, "tok-test", 3));
        Assert.Equal("Tidies things.", d.Body);
        Assert.True(d.Mergeable == true);
        Assert.Equal("d1d1d1d1", One(d.Commits).Id);
        var file = One(d.Files);
        Assert.Equal(("a.py", "modified", 1, 0), (file.Path, file.Status, file.Additions, file.Deletions));
        Assert.Equal("dan", One(d.Comments).Author);   // the system note is not a comment
        Assert.True(fake.Requests.All(q => q.Auth == "tok-test"));

        var create = PullRequests.PrepareCreate(r, "Tidy more", "Body", "tidy2", "main", draft: true, pushed: true);
        var made = PullRequests.Created(r, Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(create))!, "tok-test")))!;
        Assert.Equal((4, "Draft: Tidy more"), (made.Number, made.Title));
        var sentCreate = JsonNode.Parse(One(fake.Writes).Body)!;
        Assert.Equal(("tidy2", "main", "Body"), (sentCreate["source_branch"]!.GetValue<string>(), sentCreate["target_branch"]!.GetValue<string>(), sentCreate["description"]!.GetValue<string>()));

        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(PullRequests.PrepareComment(r, 3, "LGTM")))!, "tok-test"));
        Assert.Equal("LGTM", JsonNode.Parse(fake.Writes.Last().Body)!["body"]!.GetValue<string>());
        Assert.Contains("merges by merge, squash", Said(() => Wait(PullRequests.PrepareMerge(r, "tok-test", 3, "rebase"))));
        var merge = Wait(PullRequests.PrepareMerge(r, "tok-test", 3, "squash"));
        Wait(PullRequests.Carry(PullRequests.Redeem(PullRequests.Issue(merge))!, "tok-test"));
        Assert.True(fake.Merged);
        Assert.True(JsonNode.Parse(fake.Writes.Last().Body)!["squash"]!.GetValue<bool>());
    }
}
