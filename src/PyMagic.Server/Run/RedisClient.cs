using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// Redis for the Database panel, spoken here (RESP2, no driver): a connection to a server of the project (its password, an ACL user, the
/// database number, TLS, through its SSH tunnel), its keys by SCAN with a pattern, a key's type, TTL and value whatever its type, changes to a
/// key, and any command typed in the console. Replies are JSON: text, numbers, arrays, null.
/// </summary>
public sealed class RedisClient : IDisposable
{
    readonly TcpClient _tcp;
    readonly Stream _stream;
    readonly BufferedStream _in;

    RedisClient(TcpClient tcp, Stream stream) { _tcp = tcp; _stream = stream; _in = new BufferedStream(stream, 64 * 1024); }

    /// <summary>Connects, says who it is (AUTH) and picks the database (SELECT).</summary>
    /// <exception cref="ServiceException">No server there, the password is refused, or the database is not one.</exception>
    public static RedisClient Connect(string host, int port, string user, string password, int database, bool tls = false, int timeoutMs = 10_000)
    {
        var tcp = new TcpClient { ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs)) throw new ServiceException($"{host}:{port}: no answer in {timeoutMs / 1000} seconds (is Redis running?)");
        }
        catch (AggregateException e) when (e.InnerException is SocketException s) { tcp.Dispose(); throw new ServiceException($"{host}:{port}: {s.Message} (is Redis running?)"); }
        Stream stream = tcp.GetStream();
        if (tls)
        {
            var ssl = new SslStream(stream, false);
            try { ssl.AuthenticateAsClient(host); }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { tcp.Dispose(); throw new ServiceException("TLS: " + e.Message); }
            stream = ssl;
        }
        var client = new RedisClient(tcp, stream);
        try
        {
            if (password.Length > 0)
            {
                var auth = user.Length > 0 ? client.Command("AUTH", user, password) : client.Command("AUTH", password);
                if (auth is JsonObject { } err && err["error"] != null) throw new ServiceException("Redis refused the password: " + err["error"]);
            }
            if (database != 0 && client.Command("SELECT", database.ToString(CultureInfo.InvariantCulture)) is JsonObject { } bad && bad["error"] != null)
                throw new ServiceException($"database {database}: {bad["error"]}");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>One command; a Redis error is {error: "..."} (the connection goes on), a lost connection a ServiceException.</summary>
    public JsonNode? Command(params string[] args)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(args.Length).Append("\r\n");
        foreach (var a in args)
        {
            int n = Encoding.UTF8.GetByteCount(a);
            sb.Append('$').Append(n).Append("\r\n").Append(a).Append("\r\n");
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            _stream.Write(bytes);
            _stream.Flush();
            return Read();
        }
        catch (IOException e) { throw new ServiceException("Redis: " + e.Message); }
    }

    JsonNode? Read()
    {
        int type = _in.ReadByte();
        if (type < 0) throw new IOException("the server closed the connection");
        string line = ReadLine();
        switch ((char)type)
        {
            case '+': return JsonValue.Create(line);
            case '-': return new JsonObject { ["error"] = line };
            case ':': return JsonValue.Create(long.Parse(line, CultureInfo.InvariantCulture));
            case '$':
            {
                int n = int.Parse(line, CultureInfo.InvariantCulture);
                if (n < 0) return null;
                var data = new byte[n];
                int at = 0;
                while (at < n) { int r = _in.Read(data, at, n - at); if (r <= 0) throw new IOException("the server closed the connection"); at += r; }
                ReadLine();
                return JsonValue.Create(Encoding.UTF8.GetString(data));
            }
            case '*':
            {
                int n = int.Parse(line, CultureInfo.InvariantCulture);
                if (n < 0) return null;
                var list = new JsonArray();
                for (int i = 0; i < n; i++) list.Add(Read());
                return list;
            }
            default: throw new IOException($"not a Redis answer ({(char)type})");
        }
    }

    string ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int c = _in.ReadByte();
            if (c < 0) throw new IOException("the server closed the connection");
            if (c == '\r') { _in.ReadByte(); return sb.ToString(); }
            sb.Append((char)c);
        }
    }

    public void Dispose() { try { _stream.Dispose(); } catch (IOException) { } _tcp.Dispose(); }

    // ------------------------------------------------------------ what the panel asks

    /// <summary>The server's version (INFO server's redis_version).</summary>
    public string Version()
    {
        string info = Command("INFO", "server")?.ToString() ?? "";
        var line = info.Split('\n').FirstOrDefault(l => l.StartsWith("redis_version:", StringComparison.Ordinal));
        return line == null ? "Redis" : "Redis " + line[14..].Trim();
    }

    /// <summary>Up to <paramref name="limit"/> keys that match a pattern (SCAN, so a big database is not blocked), with their types, and the cursor to go on from (0 at the end).</summary>
    public (List<(string Key, string Type)> Keys, string Cursor) Keys(string pattern, string cursor = "0", int limit = 200)
    {
        var keys = new List<string>();
        int rounds = 0;
        do
        {
            if (Command("SCAN", cursor, "MATCH", pattern.Length > 0 ? pattern : "*", "COUNT", "500") is not JsonArray { Count: 2 } r) break;
            cursor = r[0]!.ToString();
            foreach (var k in r[1]!.AsArray()) if (k != null && keys.Count < limit) keys.Add(k.ToString());
        } while (cursor != "0" && keys.Count < limit && ++rounds < 200);
        var typed = keys.Distinct().OrderBy(k => k, StringComparer.Ordinal).Select(k => (k, Command("TYPE", k)?.ToString() ?? "?")).ToList();
        return (typed, cursor);
    }

    /// <summary>A key: its type, TTL (seconds, -1 for none) and value: text, {field: value}, [items], [{member, score}], or a stream's last entries.</summary>
    public JsonObject Get(string key, int limit = 1000)
    {
        string type = Command("TYPE", key)?.ToString() ?? "none";
        long ttl = Command("TTL", key) is JsonValue t && t.TryGetValue<long>(out var tl) ? tl : -1;
        JsonNode? value = null;
        long size = 0;
        string l = (limit - 1).ToString(CultureInfo.InvariantCulture);
        switch (type)
        {
            case "string": value = Command("GET", key); size = value?.ToString().Length ?? 0; break;
            case "hash":
            {
                var flat = Command("HGETALL", key) as JsonArray ?? new JsonArray();
                var obj = new JsonObject();
                for (int i = 0; i + 1 < flat.Count; i += 2) obj[flat[i]!.ToString()] = flat[i + 1]?.DeepClone();
                value = obj; size = obj.Count;
                break;
            }
            case "list": value = Command("LRANGE", key, "0", l); size = Num(Command("LLEN", key)); break;
            case "set":
                value = new JsonArray((Command("SMEMBERS", key) as JsonArray ?? new JsonArray()).Take(limit).Select(x => x?.DeepClone()).OrderBy(x => x?.ToString(), StringComparer.Ordinal).ToArray());
                size = Num(Command("SCARD", key));
                break;
            case "zset":
            {
                var flat = Command("ZRANGE", key, "0", l, "WITHSCORES") as JsonArray ?? new JsonArray();
                var list = new JsonArray();
                for (int i = 0; i + 1 < flat.Count; i += 2) list.Add(new JsonObject { ["member"] = flat[i]?.DeepClone(), ["score"] = flat[i + 1]?.DeepClone() });
                value = list; size = Num(Command("ZCARD", key));
                break;
            }
            case "stream": value = Command("XREVRANGE", key, "+", "-", "COUNT", Math.Min(limit, 100).ToString(CultureInfo.InvariantCulture)); size = Num(Command("XLEN", key)); break;
        }
        return new JsonObject { ["key"] = key, ["type"] = type, ["ttl"] = ttl, ["size"] = size, ["value"] = value };
    }

    static long Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;

    /// <summary>
    /// A change to a key from the panel: set a string; set or remove a hash's field; set, add or remove an item of a list, set or sorted set;
    /// its TTL (seconds, -1 for none); rename; delete. Returns the error Redis gave, or null.
    /// </summary>
    public string? Change(JsonObject c)
    {
        string key = c["key"]?.GetValue<string>() ?? throw new ServiceException("a key");
        string S(string name) => c[name]?.ToString() ?? "";
        JsonNode? r = S("op") switch
        {
            "set" => Command("SET", key, S("value"), "KEEPTTL"),
            "hset" => Command("HSET", key, S("field"), S("value")),
            "hdel" => Command("HDEL", key, S("field")),
            "lset" => Command("LSET", key, S("index"), S("value")),
            "rpush" => Command("RPUSH", key, S("value")),
            "lrem" => Command("LREM", key, "1", S("value")),
            "sadd" => Command("SADD", key, S("value")),
            "srem" => Command("SREM", key, S("value")),
            "zadd" => Command("ZADD", key, S("score"), S("value")),
            "zrem" => Command("ZREM", key, S("value")),
            "ttl" => long.TryParse(S("ttl"), out var ttl) && ttl > 0 ? Command("EXPIRE", key, ttl.ToString(CultureInfo.InvariantCulture)) : Command("PERSIST", key),
            "rename" => Command("RENAME", key, S("to")),
            "delete" => Command("DEL", key),
            _ => throw new ServiceException("unknown change"),
        };
        return r is JsonObject o && o["error"] != null ? o["error"]!.ToString() : null;
    }

    /// <summary>A command as typed in the console (words, "quoted words" with \" inside), split as redis-cli splits it.</summary>
    public static List<string> Words(string line)
    {
        var words = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            var sb = new StringBuilder();
            if (line[i] is '"' or '\'')
            {
                char q = line[i++];
                while (i < line.Length && line[i] != q)
                {
                    if (line[i] == '\\' && i + 1 < line.Length && q == '"') { i++; sb.Append(line[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => line[i] }); }
                    else sb.Append(line[i]);
                    i++;
                }
                i++;
            }
            else while (i < line.Length && !char.IsWhiteSpace(line[i])) sb.Append(line[i++]);
            words.Add(sb.ToString());
        }
        return words;
    }
}
