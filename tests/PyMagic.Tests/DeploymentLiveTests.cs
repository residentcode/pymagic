using System.Text;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Deployment to a real FTPS server on another machine (the Open work's "against a real FTPS server" of item 324): vsftpd with implicit TLS on
/// port 990, protected data connections in passive mode, and a self-signed certificate trusted once and remembered. Runs only when
/// PYMAGIC_CT_HOST names the machine and PYMAGIC_CT_SECRET is the password of its user ftptest (whose home holds a folder site/); skipped without.
/// </summary>
public class DeploymentLiveTests
{
    static string Temp(string name) { string d = Path.Combine(Path.GetTempPath(), "pm_ftpslive_" + name + "_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    static string Msg(Action a)
    {
        try { a(); return ""; }
        catch (Exception e) { return e.Message; }
    }

    [Fact]
    public void ImplicitFtpsToARealServerWithASelfSignedCertificate()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_CT_HOST") is not { Length: > 0 } host || Environment.GetEnvironmentVariable("PYMAGIC_CT_SECRET") is not { Length: > 0 } secret) return;
        string project = Temp("proj");
        string folder = "/site/pm_" + Guid.NewGuid().ToString("N")[..8];
        string step = "start";
        try
        {
            var parsed = DeployStore.Parse(new System.Text.Json.Nodes.JsonObject { ["type"] = "ftps", ["host"] = host, ["user"] = "ftptest", ["root"] = folder, ["implicitTls"] = true });
            Assert.Equal(990, parsed.Port);
            var server = parsed with { Mappings = new() { new DeployMapping("", "") } };
            DeployStore.Save(project, server, secret);

            // the certificate is the server's own, made on it: refused with its fingerprint until the user trusts it
            string refused = Msg(() => Deployment.Open(project, server).Dispose());
            Assert.Contains("not one this computer trusts", refused);
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(refused, "([0-9A-F]{2}:){31}[0-9A-F]{2}"), "the fingerprint is shown: " + refused);

            // a wrong password is the server's refusal, told as it is
            var wrong = server with { Id = server.Id + "-w" };
            DeployStore.Save(project, wrong, "not-the-password");
            Assert.True(Msg(() => Deployment.Open(project, wrong, trustCertificate: true).Dispose()).Length > 0, "a wrong password is refused");

            // the connection's own paths are from the login folder; the server's root is put before them by Upload and the others
            string f = folder.Trim('/');
            using (var remote = Deployment.Open(project, server, trustCertificate: true))
            {
                step = "write"; remote.Write(f + "/hello.txt", Encoding.UTF8.GetBytes("hi from PyMagic"));
                step = "read"; Assert.Equal("hi from PyMagic", Encoding.UTF8.GetString(remote.Read(f + "/hello.txt")));
                step = "stat"; Assert.Equal(15, remote.Stat(f + "/hello.txt")!.Size);
                Assert.True(remote.Stat(f + "/nothing-here.txt") == null, "a file that is not there");
            }
            var kept = DeployStore.Find(project, server.Id)!;
            Assert.True(kept.HostKey.Length > 0, "the trusted certificate's fingerprint is kept");
            Assert.True(refused.Contains(kept.HostKey), "the one shown is the one kept");

            // an upload of the project: folders made on the way, then listed and compared; one connection for it all
            foreach (var (rel, text) in new[] { ("app.py", "print('hi')\n"), ("pkg/__init__.py", ""), ("pkg/deep/mod.py", "X = 1\n"), ("static/é ü.css", "body { }\n") })
            {
                string full = Path.Combine(project, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            step = "upload"; var results = Deployment.Upload(project, kept, new[] { "app.py", "pkg/__init__.py", "pkg/deep/mod.py", "static/é ü.css" });
            Assert.True(results.All(r => r.Ok), string.Join("; ", results.Where(r => !r.Ok).Select(r => r.Path + ": " + r.Error)));
            using (var remote = Deployment.Open(project, kept))
            {
                step = "list"; var top = remote.List(f).Select(e => (e.IsDir ? "d:" : "f:") + e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Assert.Equal("d:pkg,d:static,f:app.py,f:hello.txt", string.Join(",", top));
                Assert.Equal("X = 1\n", Encoding.UTF8.GetString(remote.Read(f + "/pkg/deep/mod.py")));
                Assert.Equal("body { }\n", Encoding.UTF8.GetString(remote.Read(f + "/static/é ü.css")));   // a name that is not ASCII
                // a folder deleted on the server goes with what is in it
                step = "delete"; remote.Delete(f + "/pkg");
                Assert.True(remote.Stat(f + "/pkg/deep/mod.py") == null && remote.Stat(f + "/pkg") == null, "the folder and its files are gone");
            }

            // the server shows another certificate later (here: the kept fingerprint is not its): refused
            var changed = kept with { HostKey = string.Join(":", Enumerable.Repeat("AB", 32)) };
            Assert.Contains("not the one trusted before", Msg(() => Deployment.Open(project, changed).Dispose()));
        }
        catch (Exception e) when (e is not AssertionException) { throw new AssertionException("at " + step + ": " + e.GetType().Name + ": " + e.Message); }
        finally
        {
            try
            {
                // what the test put on the server goes
                if (DeployStore.Load(project).FirstOrDefault(x => x.HostKey.Length > 0) is { } s)
                    using (var remote = Deployment.Open(project, s)) remote.Delete(folder.Trim('/'));
            }
            catch (Exception) { }
            Directory.Delete(project, true);
        }
    }
}
