using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The database tools against a real PostgreSQL and MySQL on another machine (the Open work's "not checked against real servers" of
/// item 312): a server's other databases, its routines and triggers, each TLS mode with the server's CA and with a CA that is not its,
/// and an SSH tunnel through Windows' own ssh.exe. They run only when these name a test machine (and are skipped without):
/// PYMAGIC_CT_HOST (the machine, running both servers and sshd), PYMAGIC_CT_SECRET (the test password of both servers' user pmtest,
/// with databases pmtest and pmtest_other), PYMAGIC_CT_CA (the CA file of their certificates), PYMAGIC_CT_KEY (an SSH key for root
/// there), PYMAGIC_CT_KNOWN_HOSTS (a known_hosts file for it, so the user's own is not written) and PYMAGIC_PYTHON (a Python with
/// psycopg and PyMySQL). What the machine needs is made by a setup script kept with those settings, outside the repository.
/// </summary>
public class DbServerLiveTests
{
    sealed record Machine(string Host, string Secret, string Ca, string Key, string KnownHosts, string Python);

    static Machine? Find()
    {
        string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var (host, secret, ca, key, known, python) = (V("PYMAGIC_CT_HOST"), V("PYMAGIC_CT_SECRET"), V("PYMAGIC_CT_CA"), V("PYMAGIC_CT_KEY"), V("PYMAGIC_CT_KNOWN_HOSTS"), V("PYMAGIC_PYTHON"));
        return host == null || secret == null || ca == null || key == null || known == null || python == null ? null : new Machine(host, secret, ca, key, known, python);
    }

    static DbServer Server(Machine m, string kind, string database = "pmtest", string sslMode = "", string sslCa = "", bool tunnel = false, string sshKey = "") =>
        DbServers.Parse(new JsonObject
        {
            ["kind"] = kind, ["host"] = tunnel ? "127.0.0.1" : m.Host, ["port"] = kind == "postgres" ? "5432" : "3306", ["user"] = "pmtest", ["database"] = database,
            ["sslMode"] = sslMode, ["sslCa"] = sslCa,
            ["sshHost"] = tunnel ? m.Host : "", ["sshUser"] = tunnel ? "root" : "", ["sshKey"] = tunnel ? (sshKey.Length > 0 ? sshKey : m.Key) : "",
        });

    static JsonNode Ask(Machine m, DbServer server, string op, JsonObject? request = null)
    {
        request ??= new JsonObject();
        request["op"] = op;
        request["server"] = DbServers.Reach("", server, m.Secret, "");
        return Databases.Ask(m.Python, request, 60000, "pm_dbserver.py");
    }

    static string[] Names(JsonNode answer, string list) => answer[list]!.AsArray().Select(o => o!["name"]!.GetValue<string>()).ToArray();
    static void Has(string name, string[] list) => Assert.True(list.Contains(name), $"'{name}' not in: " + string.Join(", ", list));
    static void Tls(string version, string what) => Assert.True(version.StartsWith("TLS"), what + ": " + version);
    static void Says(string word, string text) => Assert.True(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"'{word}' not in: {text}");

    [Fact]
    public void AServersOtherDatabasesItsRoutinesAndItsTriggersAreRead()
    {
        if (Find() is not { } m) return;
        foreach (var kind in new[] { "postgres", "mysql" })
        {
            var server = Server(m, kind);
            Assert.True(Ask(m, server, "test")["error"] == null, kind);
            var dbs = Ask(m, server, "databases");
            var names = dbs["databases"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.True(names.Contains("pmtest") && names.Contains("pmtest_other"), kind + ": " + string.Join(",", names));
            Assert.True(!names.Contains("information_schema") && !names.Contains("template0"), kind + ": not the server's own");
            Assert.Equal("pmtest", dbs["current"]!.GetValue<string>());

            var schema = Ask(m, server, "schema");
            Assert.True(schema["error"] == null, kind + ": " + schema["error"]);
            Has("customers", Names(schema, "objects"));
            var routines = schema["routines"]!.AsArray();
            var add = routines.Single(r => r!["name"]!.GetValue<string>() == "add_numbers")!;
            Assert.Equal("function", add["kind"]!.GetValue<string>().ToLowerInvariant());
            Assert.Contains("a", add["args"]!.GetValue<string>());
            Assert.Equal("procedure", routines.Single(r => r!["name"]!.GetValue<string>() == "rename_customer")!["kind"]!.GetValue<string>().ToLowerInvariant());
            var trigger = schema["triggers"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "customers_email")!;
            Assert.Equal("customers", trigger["table"]!.GetValue<string>());
            Assert.Equal("BEFORE", trigger["timing"]!.GetValue<string>());
            Assert.Equal("INSERT", trigger["event"]!.GetValue<string>());
            Says("lower", trigger["sql"]!.GetValue<string>());

            // the other database, opened on its own
            var other = Ask(m, Server(m, kind, "pmtest_other"), "schema");
            Assert.Equal("orders", string.Join(",", Names(other, "objects")));
            Assert.Equal(7, Ask(m, server, "run", new JsonObject { ["sql"] = "SELECT add_numbers(3, 4)" })["rows"]![0]![0]!.GetValue<int>());
        }
    }

    // a CA that signed nothing of the server's
    static string StrangerCa()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Not the server's CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        string file = Path.Combine(Path.GetTempPath(), "pm_ca_" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(file, cert.ExportCertificatePem());
        return file;
    }

    static string TlsInUse(Machine m, DbServer server) => server.Kind == "postgres"
        ? Ask(m, server, "run", new JsonObject { ["sql"] = "SELECT CASE WHEN ssl THEN version ELSE 'none' END FROM pg_stat_ssl WHERE pid = pg_backend_pid()" })["rows"]![0]![0]!.GetValue<string>()
        : Ask(m, server, "run", new JsonObject { ["sql"] = "SHOW STATUS LIKE 'Ssl_version'" })["rows"]![0]![1]!.GetValue<string>();

    [Fact]
    public void EachTlsModeAgainstTheServersOwnCertificate()
    {
        if (Find() is not { } m) return;
        string stranger = StrangerCa();
        try
        {
            foreach (var kind in new[] { "postgres", "mysql" })
            {
                Assert.True(TlsInUse(m, Server(m, kind, sslMode: "disable")) is "none" or "", kind + ": disable is plain");
                foreach (var mode in new[] { "require", "verify-ca", "verify-full" })
                    Tls(TlsInUse(m, Server(m, kind, sslMode: mode, sslCa: m.Ca)), kind);
                if (kind == "postgres")
                {
                    // libpq's own: allow tries a plain connection first (this server takes it), prefer tries TLS first
                    Assert.Equal("none", TlsInUse(m, Server(m, kind, sslMode: "allow")));
                    Tls(TlsInUse(m, Server(m, kind, sslMode: "prefer")), kind + " prefer");
                }
                // the certificate checked: another CA is refused, with what the driver said; require does not check it
                foreach (var mode in new[] { "verify-ca", "verify-full" })
                {
                    var refused = Ask(m, Server(m, kind, sslMode: mode, sslCa: stranger), "test");
                    Assert.True(refused["error"] != null, kind + " " + mode + " with another CA");
                    Says("certificate", refused["error"]!.GetValue<string>());
                }
                Tls(TlsInUse(m, Server(m, kind, sslMode: "require", sslCa: "")), kind);
            }
        }
        finally { File.Delete(stranger); }
    }

    [Fact]
    public void AnSshTunnelThroughWindowsOwnSsh()
    {
        if (Find() is not { } m || SshTunnels.FindSsh() is not { } ssh) return;
        var before = SshTunnels.Launcher;
        SshTunnels.Launcher = (ssh, new[] { "-o", "UserKnownHostsFile=" + m.KnownHosts });
        try
        {
            foreach (var kind in new[] { "postgres", "mysql" })
            {
                var server = Server(m, kind, tunnel: true);
                // the server sees the connection come from its own machine: through the tunnel
                string from = kind == "postgres"
                    ? Ask(m, server, "run", new JsonObject { ["sql"] = "SELECT host(inet_client_addr())" })["rows"]![0]![0]!.GetValue<string>()
                    : Ask(m, server, "run", new JsonObject { ["sql"] = "SELECT SUBSTRING_INDEX(USER(), '@', -1)" })["rows"]![0]![0]!.GetValue<string>();
                Assert.True(from is "127.0.0.1" or "localhost", kind + " came from " + from);
                Assert.True(SshTunnels.IsOpen(server.Id), "the tunnel is kept for the next request");
                Has("customers", Names(Ask(m, server, "schema"), "objects"));
                // TLS through the tunnel: the certificate is checked against the server's own name
                Tls(TlsInUse(m, Server(m, kind, sslMode: "verify-full", sslCa: m.Ca, tunnel: true)), kind);
                SshTunnels.Close(server.Id);
                Assert.True(!SshTunnels.IsOpen(server.Id), "closed with its server");
            }
            // a key the machine does not take: ssh's own refusal is told
            string stray = Path.Combine(Path.GetTempPath(), "pm_key_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(stray, "not a key\n");
            try
            {
                string told = "";
                try { Ask(m, Server(m, "postgres", tunnel: true, sshKey: stray), "test"); }
                catch (ServiceException e) { told = e.Message; }
                Assert.True(told.Length > 0, "a refused key is told");
                Says("ssh", told);
            }
            finally { File.Delete(stray); }
        }
        finally { SshTunnels.CloseAll(); SshTunnels.Launcher = before; }
    }
}
