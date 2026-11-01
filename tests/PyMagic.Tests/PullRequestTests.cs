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
