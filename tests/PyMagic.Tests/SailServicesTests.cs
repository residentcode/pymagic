using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// A Sail app with MySQL and Redis running (PYMAGIC_TEST_SAIL_DB_DIR, made by sail:add mysql,redis and `docker compose up`): Phpactor in its
/// container (the project's paths mapped both ways), and its .env's MySQL and Redis offered to the Database panel, Redis reached at the port
/// Sail forwards.
/// </summary>
public class SailServicesTests
{
    static string? Dir() => Environment.GetEnvironmentVariable("PYMAGIC_TEST_SAIL_DB_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void PhpactorRunsInSailsContainerWithTheProjectsPathsMapped()
    {
        if (Dir() is not { } dir || SailTarget.Of(dir, "sail") is not { } sail || !sail.Running() || PhpLanguageServer.Phar() == null) return;
        string file = Path.Combine(dir, "routes", "pm-sail-phpactor.php");
        string text = "<?php\nuse App\\Models\\User;\n$user = new User();\n$user->\n";
        File.WriteAllText(file, text);
        try
        {
            var log = new ConcurrentQueue<string>();
            using var server = new PhpLanguageServer(sail, dir, _ => { }, log.Enqueue);
            string uri = LanguageService.UriOfPath(file);
            server.Notify("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "php", ["version"] = 1, ["text"] = text } });
            JsonObject At(int line, int character) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };
            var labels = new List<string>();
            for (var until = DateTime.UtcNow.AddSeconds(300); !labels.Contains("notify") && DateTime.UtcNow < until && server.Failed == null; Thread.Sleep(2000))
                labels = (server.Request("textDocument/completion", At(3, 7)).GetAwaiter().GetResult()?["items"] as JsonArray ?? new JsonArray()).Select(i => i!["label"]!.GetValue<string>()).ToList();
            Assert.True(server.Failed == null, server.Failed + " | " + string.Join(" | ", log.TakeLast(8)));
            // Phpactor's methods of the model (Notifiable's notify) and the users table's columns
            Assert.True(labels.Contains("notify") && labels.Contains("email"), string.Join(",", labels.Take(30)) + " | " + string.Join(" | ", log.TakeLast(5)));
            // Go to Definition of User: the model's file of this computer, not the container's /var/www/html/…
            var definition = server.Request("textDocument/definition", At(2, 13)).GetAwaiter().GetResult();
            string target = (definition is JsonArray a ? a[0] : definition)?["uri"]?.GetValue<string>() ?? definition?.ToJsonString() ?? "nothing";
            Assert.Equal(LanguageService.UriOfPath(Path.Combine(dir, "app", "Models", "User.php")), target);
            // the phar where the container sees it, beside the project's other PyMagic files
            Assert.True(File.Exists(Path.Combine(dir, ".pymagic", "phpactor.phar")), "the phar in .pymagic");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SailsMySqlAndRedisAreOfferedAndRedisIsReachedAtItsForwardedPort()
    {
        if (Dir() is not { } dir || SailTarget.Of(dir, "sail") is not { } sail || !sail.Running()) return;
        var offers = Databases.LaravelServers(dir);
        Assert.Equal("mysql 127.0.0.1:3306 sail laravel | redis 127.0.0.1:6379  0", string.Join(" | ", offers.Select(o => $"{o.Kind} {o.Fields["host"]}:{o.Fields["port"]} {o.Fields["user"]} {o.Fields["database"]}")));
        var r = offers.Single(o => o.Kind == "redis").Fields;
        using var redis = RedisClient.Connect(r["host"], int.Parse(r["port"]), r["user"], r["password"], int.Parse(r["database"]));
        Assert.True(redis.Version().StartsWith("Redis "), redis.Version());
        // Laravel's own cache in it: a key the app writes is the panel's to read
        var (code, _, err) = sail.Exec("php artisan tinker --execute=\"Illuminate\\Support\\Facades\\Redis::set('pm:from-laravel', 'hello');\"", 120_000);
        Assert.True(code == 0, err);
        var keys = redis.Keys("*pm:from-laravel").Keys;
        Assert.True(keys.Count == 1, string.Join(",", keys.Select(k => k.Key)));
        Assert.Equal("hello", redis.Get(keys[0].Key)["value"]!.GetValue<string>());
        redis.Command("DEL", keys[0].Key);
    }
}
