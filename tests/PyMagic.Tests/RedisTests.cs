using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Redis in the Database panel (RedisClient, DbServers' redis kind) and a Laravel project's databases offered from its .env (Databases.
/// LaravelServers, Sail's services forwarded to this computer); the live part against the Redis PYMAGIC_TEST_REDIS names (host:port).
/// </summary>
public class RedisTests
{
    [Fact]
    public void ARedisServerIsAKindOfItsOwnAndCommandsAreSplitAsRedisCliSplitsThem()
    {
        var server = DbServers.Parse(new JsonObject { ["kind"] = "redis", ["host"] = "127.0.0.1", ["port"] = "", ["database"] = "" });
        Assert.Equal("redis 6379 0 '' 127.0.0.1:6379/0", $"{server.Kind} {server.Port} {server.Database} '{server.User}' {server.Name}");
        string Why(JsonObject body) { try { DbServers.Parse(body); return "(no error)"; } catch (ServiceException e) { return e.Message; } }
        Assert.Equal("A Redis database is a number: 0, 1, …", Why(new JsonObject { ["kind"] = "redis", ["database"] = "app" }));
        Assert.Equal("Redis's TLS is disable or require here.", Why(new JsonObject { ["kind"] = "redis", ["sslMode"] = "verify-full" }));
        Assert.Equal("SET|greeting|hello world|EX|10", string.Join("|", RedisClient.Words("SET greeting \"hello world\" EX 10")));
        Assert.Equal("HSET|h|f|a\"b\nc", string.Join("|", RedisClient.Words("HSET h f \"a\\\"b\\nc\"")));
        Assert.Equal("GET|it's", string.Join("|", RedisClient.Words("GET \"it's\"")));
    }

    [Fact]
    public void ALaravelProjectsDatabaseAndRedisAreOfferedFromItsEnvWithSailsForwardedPorts()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_laravel_env_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "artisan"), "<?php");
        File.WriteAllText(Path.Combine(root, "config", "database.php"), "<?php return [];");
        try
        {
            // Sail: mysql and redis are services of compose.yaml, reached on this computer at their forwarded ports
            File.WriteAllText(Path.Combine(root, "compose.yaml"), "services:\n    laravel.test:\n        image: x\n    mysql:\n        image: 'mysql/mysql-server:8.0'\n    redis:\n        image: 'redis:alpine'\n");
            File.WriteAllText(Path.Combine(root, ".env"), "DB_CONNECTION=mysql\nDB_HOST=mysql\nDB_PORT=3306\nDB_DATABASE=laravel\nDB_USERNAME=sail\nDB_PASSWORD=password\nFORWARD_DB_PORT=33060\nREDIS_HOST=redis\nREDIS_PASSWORD=null\nREDIS_PORT=6379\n");
            var offers = Databases.LaravelServers(root);
            Assert.Equal("mysql mysql 127.0.0.1:33060 sail/password laravel mysql (laravel) · Sail | redis redis 127.0.0.1:6379 / 0 redis · Sail",
                string.Join(" | ", offers.Select(o => $"{o.Alias} {o.Kind} {o.Fields["host"]}:{o.Fields["port"]} {o.Fields["user"]}/{o.Fields["password"]} {o.Fields["database"]} {o.Fields["name"]}")));
            // they are listed as data sources not connected yet
            var sources = Databases.Find(root).Where(s => s.Offer != null).Select(s => $"{s.Path} {s.Server} {s.Detail}").ToList();
            Assert.Equal("django:.env:mysql mysql 127.0.0.1:33060/laravel | django:.env:redis redis 127.0.0.1:6379/0", string.Join(" | ", sources));

            // without Sail: the .env's own host; Redis only when the app uses it (a plain .env names one either way)
            File.Delete(Path.Combine(root, "compose.yaml"));
            File.WriteAllText(Path.Combine(root, ".env"), "DB_CONNECTION=pgsql\nDB_HOST=db.local\nDB_DATABASE=shop\nDB_USERNAME=app\nREDIS_HOST=127.0.0.1\n");
            Assert.Equal("postgres db.local:5432", string.Join(" | ", Databases.LaravelServers(root).Select(o => $"{o.Kind} {o.Fields["host"]}:{o.Fields["port"]}")));
            File.AppendAllText(Path.Combine(root, ".env"), "CACHE_STORE=redis\n");
            Assert.Equal("postgres db.local:5432 | redis 127.0.0.1:6379", string.Join(" | ", Databases.LaravelServers(root).Select(o => $"{o.Kind} {o.Fields["host"]}:{o.Fields["port"]}")));
            // SQLite (Laravel's default) is a file, found as files are
            File.WriteAllText(Path.Combine(root, ".env"), "DB_CONNECTION=sqlite\n");
            Assert.Equal(0, Databases.LaravelServers(root).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ARedisServersKeysValuesAndChangesAreReadAndMade()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_REDIS") is not { Length: > 0 } where) return;
        string host = where.Split(':')[0];
        int port = int.Parse(where.Split(':')[1]);
        using var redis = RedisClient.Connect(host, port, "", "", 7);
        string p = "pmtest:" + Guid.NewGuid().ToString("N")[..6] + ":";
        try
        {
            Assert.True(redis.Version().StartsWith("Redis "), redis.Version());
            redis.Command("SET", p + "greeting", "hello");
            redis.Command("EXPIRE", p + "greeting", "100");
            redis.Command("HSET", p + "user", "name", "Ada", "age", "36");
            redis.Command("RPUSH", p + "queue", "a", "b", "c");
            redis.Command("SADD", p + "tags", "x", "y");
            redis.Command("ZADD", p + "scores", "2", "bob", "1", "ada");
            redis.Command("XADD", p + "events", "*", "kind", "login");
            var (keys, cursor) = redis.Keys(p + "*");
            Assert.Equal("events:stream greeting:string queue:list scores:zset tags:set user:hash", string.Join(" ", keys.Select(k => $"{k.Key[p.Length..]}:{k.Type}")));
            Assert.Equal("0", cursor);

            var greeting = redis.Get(p + "greeting");
            Assert.True(greeting["ttl"]!.GetValue<long>() is > 90 and <= 100, greeting.ToJsonString());
            Assert.Equal("hello", greeting["value"]!.GetValue<string>());
            Assert.Equal("{\"name\":\"Ada\",\"age\":\"36\"}", redis.Get(p + "user")["value"]!.ToJsonString());
            Assert.Equal("[\"a\",\"b\",\"c\"]", redis.Get(p + "queue")["value"]!.ToJsonString());
            Assert.Equal("[{\"member\":\"ada\",\"score\":\"1\"},{\"member\":\"bob\",\"score\":\"2\"}]", redis.Get(p + "scores")["value"]!.ToJsonString());
            Assert.Equal(1, redis.Get(p + "events")["size"]!.GetValue<long>());

            // changes as the panel makes them: a string kept with its TTL, a field, an item, the TTL, a rename, a delete
            Assert.True(redis.Change(new JsonObject { ["op"] = "set", ["key"] = p + "greeting", ["value"] = "hi" }) == null);
            Assert.True(redis.Get(p + "greeting")["ttl"]!.GetValue<long>() > 0, "SET kept its TTL");
            redis.Change(new JsonObject { ["op"] = "hset", ["key"] = p + "user", ["field"] = "age", ["value"] = "37" });
            redis.Change(new JsonObject { ["op"] = "lset", ["key"] = p + "queue", ["index"] = "1", ["value"] = "B" });
            redis.Change(new JsonObject { ["op"] = "zrem", ["key"] = p + "scores", ["value"] = "bob" });
            redis.Change(new JsonObject { ["op"] = "ttl", ["key"] = p + "greeting", ["ttl"] = "-1" });
            Assert.Equal("37 [\"a\",\"B\",\"c\"] -1 1", $"{redis.Get(p + "user")["value"]!["age"]} {redis.Get(p + "queue")["value"]!.ToJsonString()} {redis.Get(p + "greeting")["ttl"]} {redis.Get(p + "scores")["size"]}");
            Assert.True(redis.Change(new JsonObject { ["op"] = "rename", ["key"] = p + "tags", ["to"] = p + "labels" }) == null);
            Assert.Equal("set", redis.Get(p + "labels")["type"]!.GetValue<string>());
            Assert.Equal("ERR no such key", redis.Change(new JsonObject { ["op"] = "rename", ["key"] = p + "nothing", ["to"] = p + "x" }));
            // a typed command and its error
            Assert.Equal("[\"Ada\",\"37\"]", redis.Command(RedisClient.Words($"HMGET {p}user name age").ToArray())!.ToJsonString());
            Assert.True(redis.Command("NOSUCHCOMMAND") is JsonObject { } err && err["error"]!.ToString().Contains("unknown command"), "an error comes back as one");
        }
        finally
        {
            foreach (var k in redis.Keys(p + "*").Keys) redis.Command("DEL", k.Key);
        }
    }
    [Fact]
    public void AHeldConnectionsMessagesAreShownAsRedisCliShowsThem()
    {
        Assert.True(RedisClient.Holds("subscribe") && RedisClient.Holds("PSUBSCRIBE") && RedisClient.Holds("ssubscribe") && RedisClient.Holds("Monitor"));
        Assert.True(!RedisClient.Holds("PUBLISH") && !RedisClient.Holds("SYNC"));
        Assert.Equal("news: hello", RedisClient.PushText(new JsonArray("message", "news", "hello")));
        Assert.Equal("news.uk (news.*): hi", RedisClient.PushText(new JsonArray("pmessage", "news.*", "news.uk", "hi")));
        Assert.Equal("(subscribed to news, 1 in all)", RedisClient.PushText(new JsonArray("subscribe", "news", 1)));
        Assert.Equal("(unsubscribed from news, 0 left)", RedisClient.PushText(new JsonArray("unsubscribe", "news", 0)));
        Assert.Equal("OK", RedisClient.PushText(JsonValue.Create("OK")));
        Assert.Equal("(error) ERR no", RedisClient.PushText(new JsonObject { ["error"] = "ERR no" }));
    }

    [Fact]
    public void SubscribeAndMonitorHoldAConnectionOfTheirOwn()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_REDIS") is not { Length: > 0 } where) return;
        string host = where.Split(':')[0];
        int port = int.Parse(where.Split(':')[1]);
        string channel = "pmtest-" + Guid.NewGuid().ToString("N")[..6];
        using var sub = RedisClient.Connect(host, port, "", "", 0);
        using var mon = RedisClient.Connect(host, port, "", "", 0);
        using var other = RedisClient.Connect(host, port, "", "", 0);
        sub.Hold("PSUBSCRIBE", channel + ".*");
        Assert.Equal($"(subscribed to {channel}.*, 1 in all)", RedisClient.PushText(sub.Next()));
        mon.Hold("MONITOR");
        Assert.Equal("OK", RedisClient.PushText(mon.Next()));
        // what another connection does: the message, and MONITOR's line of it (after a while: nothing times out on a held connection)
        System.Threading.Thread.Sleep(300);
        Assert.Equal(1L, other.Command("PUBLISH", channel + ".a", "hello there")!.GetValue<long>());
        Assert.Equal($"{channel}.a ({channel}.*): hello there", RedisClient.PushText(sub.Next()));
        string line = RedisClient.PushText(mon.Next());
        Assert.True(line.Contains("\"PUBLISH\"") && line.Contains("\"hello there\""), line);
        // closing it ends what it waits for
        var waiting = Task.Run(() => { try { sub.Next(); return "a message"; } catch (Exception e) when (e is IOException or ObjectDisposedException) { return "closed"; } });
        System.Threading.Thread.Sleep(200);
        sub.Dispose();
        Assert.Equal("closed", waiting.Wait(5000) ? waiting.Result : "still waiting");
    }
}
