using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>A PostgreSQL or MySQL server the Database panel is connected to. The password is not part of it: it is kept apart.</summary>
public sealed record DbServer(string Id, string Name, string Kind, string Host, int Port, string User, string Database);

/// <summary>
/// The database servers of a project. Their settings are in <c>.pymagic/db-servers.json</c> (nothing secret), the passwords in
/// <c>.pymagic/db-servers.private.json</c>, which belongs outside version control (the panel says so). The connection itself is made by
/// pm_dbserver.py with the project's Python and its psycopg or PyMySQL package.
/// </summary>
public static class DbServers
{
    public const string Prefix = "server:";

    static string SettingsFile(string root) => Path.Combine(root, ".pymagic", "db-servers.json");
    static string PasswordFile(string root) => Path.Combine(root, ".pymagic", "db-servers.private.json");
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string PathOf(string id) => Prefix + id;

    public static bool IsServerPath(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    public static List<DbServer> Load(string root)
    {
        try { return JsonSerializer.Deserialize<List<DbServer>>(File.ReadAllText(SettingsFile(root)), Options) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    static Dictionary<string, string> Passwords(string root)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PasswordFile(root))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>The request fields <c>server</c> of pm_dbserver.py for a server of the project (with its password), or null.</summary>
    public static JsonObject? Connection(string root, string path)
    {
        if (!IsServerPath(path)) return null;
        string id = path[Prefix.Length..];
        var server = Load(root).FirstOrDefault(s => s.Id == id);
        if (server == null) return null;
        return Describe(server, Passwords(root).GetValueOrDefault(id, ""));
    }

    public static JsonObject Describe(DbServer server, string password) => new()
    {
        ["kind"] = server.Kind, ["host"] = server.Host, ["port"] = server.Port, ["user"] = server.User, ["password"] = password, ["database"] = server.Database,
    };

    /// <summary>A server from what the panel's form sent, checked: a known kind, a port, names without control characters.</summary>
    /// <exception cref="ServiceException">A field is not usable.</exception>
    public static DbServer Parse(JsonObject body)
    {
        string kind = (body["kind"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (kind is not ("postgres" or "mysql")) throw new ServiceException("The kind of server is PostgreSQL or MySQL.");
        string host = (body["host"]?.GetValue<string>() ?? "").Trim();
        if (host.Length == 0) host = "localhost";
        int port = body["port"] is { } p && int.TryParse(p.ToString(), out var n) ? n : (kind == "postgres" ? 5432 : 3306);
        if (port is < 1 or > 65535) throw new ServiceException("The port is a number from 1 to 65535.");
        string user = (body["user"]?.GetValue<string>() ?? "").Trim();
        string database = (body["database"]?.GetValue<string>() ?? "").Trim();
        foreach (var field in new[] { host, user, database })
            if (field.Any(char.IsControl)) throw new ServiceException("A name has a control character in it.");
        if (user.Length == 0) user = kind == "postgres" ? "postgres" : "root";
        if (database.Length == 0 && kind == "postgres") database = "postgres";
        string name = (body["name"]?.GetValue<string>() ?? "").Trim();
        if (name.Length == 0) name = $"{user}@{host}" + (database.Length > 0 ? "/" + database : "");
        string id = new string((kind + "-" + host + "-" + port + "-" + user + "-" + database).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return new DbServer(id, name, kind, host, port, user, database);
    }

    public static void Save(string root, DbServer server, string password)
    {
        var list = Load(root).Where(s => s.Id != server.Id).ToList();
        list.Add(server);
        Directory.CreateDirectory(Path.Combine(root, ".pymagic"));
        File.WriteAllText(SettingsFile(root), JsonSerializer.Serialize(list, Options));
        var passwords = Passwords(root);
        if (password.Length > 0) passwords[server.Id] = password; else passwords.Remove(server.Id);
        File.WriteAllText(PasswordFile(root), JsonSerializer.Serialize(passwords));
    }

    public static void Remove(string root, string id)
    {
        if (!File.Exists(SettingsFile(root))) return;
        File.WriteAllText(SettingsFile(root), JsonSerializer.Serialize(Load(root).Where(s => s.Id != id).ToList(), Options));
        var passwords = Passwords(root);
        if (passwords.Remove(id)) File.WriteAllText(PasswordFile(root), JsonSerializer.Serialize(passwords));
    }
}
