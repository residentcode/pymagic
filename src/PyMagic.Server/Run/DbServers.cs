using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>A PostgreSQL or MySQL server the Database panel is connected to. The passwords are not part of it: they are kept apart.</summary>
/// <param name="SslMode">TLS: "" (the driver's own default), "disable", "allow", "prefer", "require", "verify-ca" or "verify-full" (libpq's names; MySQL takes disable, require and the two verify ones).</param>
/// <param name="SslCa">The certificate of the authority the server's certificate is checked against (a file); SslCert and SslKey the client's own.</param>
/// <param name="SshHost">The machine the server is reached through, by an SSH tunnel; empty for none. Host and Port are then as that machine sees them.</param>
/// <param name="SshKey">A private key file for SSH; empty for ssh-agent, the default keys or the SSH password.</param>
public sealed record DbServer(string Id, string Name, string Kind, string Host, int Port, string User, string Database,
    string SslMode = "", string SslCa = "", string SslCert = "", string SslKey = "",
    string SshHost = "", int SshPort = 22, string SshUser = "", string SshKey = "");

/// <summary>
/// The database servers of a project. Their settings are in <c>.pymagic/db-servers.json</c> (nothing secret), the passwords (the
/// database's, and the SSH machine's under <c>id:ssh</c>) in <c>.pymagic/db-servers.private.json</c>, which belongs outside version control
/// (the panel says so). The connection itself is made by pm_dbserver.py with the project's Python and its psycopg or PyMySQL package,
/// through an SSH tunnel (SshTunnels) when the server has an SSH machine.
/// </summary>
public static class DbServers
{
    public const string Prefix = "server:";
    public static readonly string[] SslModes = { "", "disable", "allow", "prefer", "require", "verify-ca", "verify-full" };

    static string SettingsFile(string root) => Path.Combine(root, ".pymagic", "db-servers.json");
    static string PasswordFile(string root) => Path.Combine(root, ".pymagic", "db-servers.private.json");
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string PathOf(string id) => Prefix + id;

    /// <summary>The path of another database of a server than the one it was connected with: <c>server:id/name</c>.</summary>
    public static string PathOf(string id, string database) => Prefix + id + "/" + database;

    public static bool IsServerPath(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The server's id and the database a path names (null for the server's own).</summary>
    public static (string Id, string? Database) Split(string path)
    {
        string rest = path[Prefix.Length..];
        int slash = rest.IndexOf('/');
        return slash < 0 ? (rest, null) : (rest[..slash], rest[(slash + 1)..]);
    }

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

    /// <summary>
    /// The request field <c>server</c> of pm_dbserver.py for a server of the project (with its password; with the database the path names,
    /// and through its SSH tunnel, which is opened here), or null when the project has no such server.
    /// </summary>
    /// <exception cref="ServiceException">The SSH tunnel could not be opened, or the database's name is not usable.</exception>
    public static JsonObject? Connection(string root, string path)
    {
        if (!IsServerPath(path)) return null;
        var (id, database) = Split(path);
        var server = Load(root).FirstOrDefault(s => s.Id == id);
        if (server == null) return null;
        if (database != null)
        {
            if (database.Length == 0 || database.Any(char.IsControl)) throw new ServiceException("Not a name of a database.");
            server = server with { Database = database };
        }
        var passwords = Passwords(root);
        return Reach(root, server, passwords.GetValueOrDefault(id, ""), passwords.GetValueOrDefault(id + ":ssh", ""));
    }

    /// <summary>What pm_dbserver.py is told of a server: where it is, who connects, TLS; without the tunnel (Reach adds it).</summary>
    public static JsonObject Describe(DbServer server, string password, string root = "")
    {
        var o = new JsonObject
        {
            ["kind"] = server.Kind, ["host"] = server.Host, ["port"] = server.Port, ["user"] = server.User, ["password"] = password, ["database"] = server.Database,
        };
        if (server.SslMode.Length > 0 || server.SslCa.Length > 0 || server.SslCert.Length > 0)
            o["ssl"] = new JsonObject { ["mode"] = server.SslMode, ["ca"] = FileOf(root, server.SslCa), ["cert"] = FileOf(root, server.SslCert), ["key"] = FileOf(root, server.SslKey) };
        return o;
    }

    /// <summary>Describe, and through the SSH tunnel when the server has an SSH machine (pm_dbserver.py connects to <c>via</c> then).</summary>
    public static JsonObject Reach(string root, DbServer server, string password, string sshPassword)
    {
        var o = Describe(server, password, root);
        if (server.SshHost.Length > 0)
        {
            int port = SshTunnels.Reach(server with { SshKey = FileOf(root, server.SshKey) }, sshPassword);
            o["via"] = new JsonObject { ["host"] = "127.0.0.1", ["port"] = port };
        }
        return o;
    }

    // a certificate or key file: absolute, ~ for the user's folder, or from the project's folder
    static string FileOf(string root, string file)
    {
        if (file.Length == 0) return "";
        if (file.StartsWith("~/") || file.StartsWith("~\\")) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[2..]);
        return Path.IsPathRooted(file) || root.Length == 0 ? file : Path.GetFullPath(Path.Combine(root, file));
    }

    /// <summary>A server from what the panel's form sent, checked: a known kind, a port, names without control characters.</summary>
    /// <exception cref="ServiceException">A field is not usable.</exception>
    public static DbServer Parse(JsonObject body)
    {
        string Text(string key) => (body[key]?.ToString() ?? "").Trim();
        string kind = Text("kind").ToLowerInvariant();
        if (kind is not ("postgres" or "mysql")) throw new ServiceException("The kind of server is PostgreSQL or MySQL.");
        string host = Text("host");
        if (host.Length == 0) host = "localhost";
        int port = body["port"] is { } p && int.TryParse(p.ToString(), out var n) ? n : (kind == "postgres" ? 5432 : 3306);
        if (port is < 1 or > 65535) throw new ServiceException("The port is a number from 1 to 65535.");
        string user = Text("user");
        string database = Text("database");
        string sslMode = Text("sslMode").ToLowerInvariant();
        if (!SslModes.Contains(sslMode)) throw new ServiceException("TLS is one of: " + string.Join(", ", SslModes.Where(m => m.Length > 0)) + ".");
        if (kind == "mysql" && sslMode is "allow" or "prefer") throw new ServiceException("MySQL's TLS is disable, require, verify-ca or verify-full here.");
        string sslCa = Text("sslCa"), sslCert = Text("sslCert"), sslKey = Text("sslKey");
        if (sslMode is "verify-ca" or "verify-full" && sslCa.Length == 0 && kind == "mysql") throw new ServiceException("Checking the server's certificate needs the CA file.");
        string sshHost = Text("sshHost"), sshUser = Text("sshUser"), sshKey = Text("sshKey");
        int sshPort = body["sshPort"] is { } sp && int.TryParse(sp.ToString(), out var sn) ? sn : 22;
        if (sshPort is < 1 or > 65535) throw new ServiceException("The SSH port is a number from 1 to 65535.");
        foreach (var field in new[] { host, user, database, sslCa, sslCert, sslKey, sshHost, sshUser, sshKey })
            if (field.Any(char.IsControl)) throw new ServiceException("A name has a control character in it.");
        // ssh would read a host or user starting with - as an option of its own
        if (sshHost.StartsWith('-') || sshUser.StartsWith('-') || (sshHost.Length > 0 && host.StartsWith('-'))) throw new ServiceException("A host or user cannot start with -.");
        if (sshHost.Any(char.IsWhiteSpace) || sshUser.Any(char.IsWhiteSpace)) throw new ServiceException("The SSH host and user have no spaces.");
        if (user.Length == 0) user = kind == "postgres" ? "postgres" : "root";
        if (database.Length == 0 && kind == "postgres") database = "postgres";
        string name = Text("name");
        if (name.Length == 0) name = $"{user}@{host}" + (database.Length > 0 ? "/" + database : "") + (sshHost.Length > 0 ? " via " + sshHost : "");
        string id = new string((kind + "-" + host + "-" + port + "-" + user + "-" + database + (sshHost.Length > 0 ? "-via-" + sshHost : "")).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return new DbServer(id, name, kind, host, port, user, database, sslMode, sslCa, sslCert, sslKey, sshHost, sshPort, sshUser, sshKey);
    }

    /// <summary>The password kept for a server (its SSH machine's with <paramref name="ssh"/>), "" when none: a form that changes a server and leaves the password empty keeps it.</summary>
    public static string StoredPassword(string root, string id, bool ssh = false) => Passwords(root).GetValueOrDefault(ssh ? id + ":ssh" : id, "");

    public static void Save(string root, DbServer server, string password, string sshPassword = "")
    {
        var list = Load(root).Where(s => s.Id != server.Id).ToList();
        list.Add(server);
        Directory.CreateDirectory(Path.Combine(root, ".pymagic"));
        File.WriteAllText(SettingsFile(root), JsonSerializer.Serialize(list, Options));
        var passwords = Passwords(root);
        if (password.Length > 0) passwords[server.Id] = password; else passwords.Remove(server.Id);
        if (sshPassword.Length > 0 && server.SshHost.Length > 0) passwords[server.Id + ":ssh"] = sshPassword; else passwords.Remove(server.Id + ":ssh");
        File.WriteAllText(PasswordFile(root), JsonSerializer.Serialize(passwords));
    }

    public static void Remove(string root, string id)
    {
        SshTunnels.Close(id);
        if (!File.Exists(SettingsFile(root))) return;
        File.WriteAllText(SettingsFile(root), JsonSerializer.Serialize(Load(root).Where(s => s.Id != id).ToList(), Options));
        var passwords = Passwords(root);
        bool a = passwords.Remove(id), b = passwords.Remove(id + ":ssh");
        if (a || b) File.WriteAllText(PasswordFile(root), JsonSerializer.Serialize(passwords));
    }
}
