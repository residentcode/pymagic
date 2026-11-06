using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// A database server's connection settings: TLS passed to the drivers, an SSH tunnel opened by ssh (here a stand-in written in Python that
/// forwards the port as ssh -L does, as no SSH server can be assumed), and another database of the same server.
/// </summary>
public class DbConnectionTests
{
    static readonly bool HavePython = Probe();

    static bool Probe()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // a helper of the engine, as it ships in the assembly, written to a folder of its own
    static string Helper(string name)
    {
        using var s = typeof(DbServers).Assembly.GetManifestResourceStream(name) ?? throw new AssertionException(name + " is not in the assembly");
        string dir = Path.Combine(Path.GetTempPath(), "pm_dbc_helpers_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        using (var f = File.Create(path)) s.CopyTo(f);
        return path;
    }

    static string Root() { string r = Path.Combine(Path.GetTempPath(), "pm_dbc_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(r); return r; }

    // ssh's stand-in: what it was started with goes to a file; -L 127.0.0.1:port:host:port is forwarded; a host named refused.example is refused
    const string FakeSsh = """
import json, os, socket, sys, threading
args = sys.argv[1:]
with open(os.environ["PM_FAKE_SSH_LOG"], "a", encoding="utf-8") as f:
    f.write(json.dumps({"args": args, "askpass": os.environ.get("SSH_ASKPASS", ""), "require": os.environ.get("SSH_ASKPASS_REQUIRE", ""),
                        "password": os.environ.get("PYMAGIC_SSH_PASSWORD", "")}) + "\n")
if args[-1] == "refused.example":
    sys.stderr.write("me@refused.example: Permission denied (publickey).\n")
    sys.exit(255)
_, lport, host, port = args[args.index("-L") + 1].split(":")
ls = socket.socket()
ls.bind(("127.0.0.1", int(lport)))
ls.listen(5)
def pipe(a, b):
    try:
        while True:
            data = a.recv(65536)
            if not data:
                break
            b.sendall(data)
    except OSError:
        pass
    for s in (a, b):
        try:
            s.close()
        except OSError:
            pass
while True:
    c, _ = ls.accept()
    u = socket.create_connection((host, int(port)))
    threading.Thread(target=pipe, args=(c, u), daemon=True).start()
    threading.Thread(target=pipe, args=(u, c), daemon=True).start()
""";

    static JsonObject Form(string sshHost = "jump.example", int port = 5432) => new()
    {
        ["kind"] = "postgres", ["host"] = "127.0.0.1", ["port"] = port.ToString(), ["user"] = "me", ["password"] = "db-secret", ["database"] = "shop",
        ["sshHost"] = sshHost, ["sshPort"] = "2222", ["sshUser"] = "admin", ["sshKey"] = "keys/id_ed25519",
    };

    [Fact]
    public void TheSshArgumentsForwardALocalPortToTheServerAsTheSshMachineSeesIt()
    {
        var s = DbServers.Parse(Form()) with { SshKey = @"C:\keys\id" };
        var args = SshTunnels.Arguments(s, 50123, withPassword: false);
        string line = string.Join(" ", args);
        Assert.Contains("-N -T", line);
        Assert.Contains("-L 127.0.0.1:50123:127.0.0.1:5432", line);
        Assert.Contains("-p 2222", line);
        Assert.Contains(@"-i C:\keys\id", line);
        Assert.Contains("-l admin", line);
        Assert.Contains("BatchMode=yes", line);        // never waits for a question nobody sees
        Assert.Contains("ExitOnForwardFailure=yes", line);
        Assert.Equal("jump.example", args[^1]);
        Assert.Contains("NumberOfPasswordPrompts=1", string.Join(" ", SshTunnels.Arguments(s, 1, withPassword: true)));
        Assert.True(!string.Join(" ", SshTunnels.Arguments(s, 1, withPassword: true)).Contains("BatchMode"), "a password is asked for by askpass");
        // a host or user that ssh would read as an option is refused
        Assert.Throws<ServiceException>(() => DbServers.Parse(new JsonObject { ["kind"] = "postgres", ["sshHost"] = "-oProxyCommand=x" }));
        Assert.Throws<ServiceException>(() => DbServers.Parse(new JsonObject { ["kind"] = "postgres", ["sshHost"] = "a b" }));
        Assert.Throws<ServiceException>(() => DbServers.Parse(new JsonObject { ["kind"] = "postgres", ["sshPort"] = "0" }));
    }

    [Fact]
    public void TlsSettingsAreKeptCheckedAndPassedToTheHelper()
    {
        string root = Root();
        try
        {
            var body = new JsonObject { ["kind"] = "postgres", ["host"] = "db.example", ["sslMode"] = "verify-full", ["sslCa"] = "certs/ca.pem", ["sslCert"] = @"C:\abs\client.crt", ["sslKey"] = "~/client.key" };
            var s = DbServers.Parse(body);
            Assert.Equal("verify-full", s.SslMode);
            DbServers.Save(root, s, "pw");
            var c = DbServers.Connection(root, DbServers.PathOf(s.Id))!;
            Assert.Equal("verify-full", c["ssl"]!["mode"]!.GetValue<string>());
            Assert.Equal(Path.Combine(root, "certs", "ca.pem"), c["ssl"]!["ca"]!.GetValue<string>());   // from the project's folder
            Assert.Equal(@"C:\abs\client.crt", c["ssl"]!["cert"]!.GetValue<string>());
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "client.key"), c["ssl"]!["key"]!.GetValue<string>());
            Assert.Contains("TLS", Databases.Find(root).Single(x => x.Server != null).Detail!);
            Assert.True(DbServers.Describe(DbServers.Parse(new JsonObject { ["kind"] = "postgres" }), "")["ssl"] == null, "no TLS settings, none sent");
            Assert.Throws<ServiceException>(() => DbServers.Parse(new JsonObject { ["kind"] = "postgres", ["sslMode"] = "sometimes" }));
            Assert.Throws<ServiceException>(() => DbServers.Parse(new JsonObject { ["kind"] = "mysql", ["sslMode"] = "prefer" }));
            Assert.Throws<ServiceException>(() => DbServers.Parse(new JsonObject { ["kind"] = "mysql", ["sslMode"] = "verify-ca" }));   // MySQL's check needs the CA file
            // the settings of an older file (no TLS, no SSH) still read
            File.WriteAllText(Path.Combine(root, ".pymagic", "db-servers.json"), """[{"id": "old", "name": "old", "kind": "mysql", "host": "h", "port": 3306, "user": "u", "database": "d"}]""");
            var old = DbServers.Load(root).Single();
            Assert.Equal("", old.SslMode);
            Assert.Equal(22, old.SshPort);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheHelperTurnsTheSettingsIntoTheDriversArguments()
    {
        if (!HavePython) return;
        string helper = Path.GetDirectoryName(Helper("pm_dbserver.py"))!;
        string script = """
import json, sys
sys.path.insert(0, sys.argv[1])
import pm_dbserver as m
s = {"kind": "postgres", "host": "db.example", "port": 5432, "user": "me", "password": "pw", "database": "shop",
     "via": {"host": "127.0.0.1", "port": 50123}, "ssl": {"mode": "verify-full", "ca": "ca.pem", "cert": "", "key": ""}}
pg = m.postgres_args(s)
my = m.mysql_args(dict(s, kind="mysql", ssl={"mode": "require"}))
off = m.mysql_args(dict(s, kind="mysql", ssl={"mode": "disable"}))
print(json.dumps({"pg": pg, "myHost": my["host"], "myPort": my["port"], "myVerify": int(my["ssl"].verify_mode), "off": off.get("ssl_disabled")}))
""";
        var psi = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script); psi.ArgumentList.Add(helper);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var o = JsonNode.Parse(output) ?? throw new AssertionException(error);
        // libpq goes to the tunnel (hostaddr) and checks the certificate against the server's own name (host)
        Assert.Equal("db.example", o["pg"]!["host"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", o["pg"]!["hostaddr"]!.GetValue<string>());
        Assert.Equal(50123, o["pg"]!["port"]!.GetValue<int>());
        Assert.Equal("verify-full", o["pg"]!["sslmode"]!.GetValue<string>());
        Assert.Equal("ca.pem", o["pg"]!["sslrootcert"]!.GetValue<string>());
        Assert.True(o["pg"]!["sslcert"] == null, "an empty file is not passed");
        // text comes as text from a database made as SQL_ASCII too (psycopg gives bytes for that encoding otherwise)
        Assert.Equal("UTF8", o["pg"]!["client_encoding"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", o["myHost"]!.GetValue<string>());
        Assert.Equal(50123, o["myPort"]!.GetValue<int>());
        Assert.Equal(0, o["myVerify"]!.GetValue<int>());   // require: encrypted, the certificate not checked
        Assert.True(o["off"]!.GetValue<bool>());
    }

    [Fact]
    public void ATunnelIsOpenedReusedAndClosedAndAPasswordGoesOnlyToAskpass()
    {
        if (!HavePython) return;
        string root = Root();
        string fake = Path.Combine(root, "fake_ssh.py");
        File.WriteAllText(fake, FakeSsh);
        string log = Path.Combine(root, "ssh.log");
        Environment.SetEnvironmentVariable("PM_FAKE_SSH_LOG", log);
        // the "database server": an echo on this computer
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await echo.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () => { using (c) { var s = c.GetStream(); var b = new byte[64]; int n; while ((n = await s.ReadAsync(b)) > 0) await s.WriteAsync(b.AsMemory(0, n)); } });
            }
        });
        SshTunnels.Launcher = ("python", new[] { fake });
        try
        {
            var server = DbServers.Parse(Form(port: echoPort));
            DbServers.Save(root, server, "db-secret", "ssh-secret");
            var c = DbServers.Connection(root, DbServers.PathOf(server.Id))!;
            int port = c["via"]!["port"]!.GetValue<int>();
            Assert.Equal("127.0.0.1", c["via"]!["host"]!.GetValue<string>());
            Assert.Equal(echoPort, c["port"]!.GetValue<int>());   // the server as the SSH machine sees it
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                var s = client.GetStream();
                s.Write(Encoding.ASCII.GetBytes("ping"));
                var b = new byte[4];
                int got = 0;
                while (got < 4) got += s.Read(b, got, 4 - got);
                Assert.Equal("ping", Encoding.ASCII.GetString(b));
            }
            // the next request uses the same tunnel; another database of the server too
            var other = DbServers.Connection(root, DbServers.PathOf(server.Id, "archive"))!;
            Assert.Equal(port, other["via"]!["port"]!.GetValue<int>());
            Assert.Equal("archive", other["database"]!.GetValue<string>());
            Assert.Equal(1, File.ReadAllLines(log).Length);
            var started = JsonNode.Parse(File.ReadAllLines(log)[0])!;
            string args = string.Join(" ", started["args"]!.AsArray().Select(a => a!.GetValue<string>()));
            Assert.True(!args.Contains("ssh-secret"), "the password is no argument");
            Assert.Equal("ssh-secret", started["password"]!.GetValue<string>());
            Assert.Contains("pm_askpass.cmd", started["askpass"]!.GetValue<string>());
            Assert.Equal("force", started["require"]!.GetValue<string>());
            Assert.Contains("-i " + Path.Combine(root, "keys", "id_ed25519"), args);   // the key file from the project's folder
            Assert.True(SshTunnels.IsOpen(server.Id));
            // disconnecting the server closes its tunnel
            DbServers.Remove(root, server.Id);
            Assert.True(!SshTunnels.IsOpen(server.Id), "closed with its server");

            // ssh refused: what it said is the error
            var refused = DbServers.Parse(Form(sshHost: "refused.example", port: echoPort));
            DbServers.Save(root, refused, "");
            try { DbServers.Connection(root, DbServers.PathOf(refused.Id)); throw new AssertionException("expected a refusal"); }
            catch (ServiceException e) { Assert.Contains("Permission denied (publickey)", e.Message); }
            Assert.Contains("BatchMode=yes", File.ReadAllLines(log)[^1]);   // no password: ssh never asks
            Assert.Throws<ServiceException>(() => DbServers.Connection(root, DbServers.PathOf(refused.Id, "a\nb")));
        }
        finally
        {
            SshTunnels.Launcher = null;
            SshTunnels.CloseAll();
            echo.Stop();
            Environment.SetEnvironmentVariable("PM_FAKE_SSH_LOG", null);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheRealSshSaysWhyATunnelCannotBeOpened()
    {
        if (SshTunnels.FindSsh() == null) return;   // no OpenSSH client on this machine
        // nothing listens on port 1 of this computer: ssh ends at once, and what it said is the error
        var s = new DbServer("pm-ssh-test", "t", "postgres", "localhost", 5432, "me", "db", SshHost: "127.0.0.1", SshPort: 1, SshUser: "nobody");
        try { SshTunnels.Reach(s, ""); throw new AssertionException("expected a refusal"); }
        catch (ServiceException e)
        {
            Assert.Contains("could not be opened", e.Message);
            Assert.True(e.Message.Contains("refused", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("port 1", StringComparison.OrdinalIgnoreCase), e.Message);
        }
        Assert.True(!SshTunnels.IsOpen("pm-ssh-test"), "no tunnel kept");
    }

    [Fact]
    public void TheAskpassHelperPrintsThePasswordAsItIs()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(Helper("pm_askpass.cmd"));
        psi.ArgumentList.Add("me@host's password:");
        psi.Environment["PYMAGIC_SSH_PASSWORD"] = "a&b!c|d\"e%PATH%<f>";
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal("a&b!c|d\"e%PATH%<f>", output.TrimEnd('\r', '\n'));
    }
}
