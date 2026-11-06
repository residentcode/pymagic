using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Deployment (item 244): servers of a project, the mappings of folders, upload, download, compare and sync over a folder, FTP and SFTP.</summary>
public class DeploymentTests
{
    static string Temp(string kind) { string p = Path.Combine(Path.GetTempPath(), "pm_dep_" + kind + "_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }

    static DeployServer Folder(string remoteRoot, params DeployMapping[] mappings) =>
        new("folder-x", "x", "folder", "", 0, "", remoteRoot, "", false, mappings.Length == 0 ? new() { new DeployMapping("", "") } : mappings.ToList(), new(), "");

    static string Msg(Action a) { try { a(); } catch (ServiceException e) { return e.Message; } return "(no exception)"; }
    static void Put(string root, string rel, string text) { string p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }

    // ------------------------------------------------------------ mappings

    [Fact]
    public void AFileHasItsPlaceOnTheServerByTheLongestMappingAndBack()
    {
        var s = new DeployServer("s", "s", "sftp", "h", 22, "u", "/srv/app", "", false,
            new() { new DeployMapping("", ""), new DeployMapping("web", "public"), new DeployMapping("web/admin", "private/admin") }, new(), "");
        Assert.Equal("srv/app/main.py", Deployment.RemoteOf(s, "main.py"));
        Assert.Equal("srv/app/public/index.html", Deployment.RemoteOf(s, "web/index.html"));
        Assert.Equal("srv/app/private/admin/users.py", Deployment.RemoteOf(s, "web/admin/users.py"));
        Assert.Equal("srv/app/public", Deployment.RemoteOf(s, "web"));
        Assert.Equal("web/index.html", Deployment.LocalOf(s, "/srv/app/public/index.html"));
        Assert.Equal("web/admin/users.py", Deployment.LocalOf(s, "srv/app/private/admin/users.py"));
        Assert.Equal("main.py", Deployment.LocalOf(s, "srv/app/main.py"));
        Assert.True(Deployment.LocalOf(s, "other/main.py") == null, "outside the server's root");
        // a server without a mapping that covers a file
        var narrow = Folder("/x", new DeployMapping("src", ""));
        Assert.True(Deployment.RemoteOf(narrow, "docs/a.md") == null);
        Assert.Equal("a.py", Deployment.RemoteOf(narrow, "src/a.py"));
    }

    [Fact]
    public void WhatNeverGoesToAServerIsLeftOut()
    {
        var s = Folder("/x") with { Excluded = new() { "*.log", "secrets" } };
        Assert.True(Deployment.IsExcluded(s, ".git/config") && Deployment.IsExcluded(s, "a/__pycache__/x.pyc") && Deployment.IsExcluded(s, "m.pyc") && Deployment.IsExcluded(s, ".venv/lib/x.py"));
        Assert.True(Deployment.IsExcluded(s, "logs/today.log") && Deployment.IsExcluded(s, "secrets/key.txt") && Deployment.IsExcluded(s, "app/secrets/key.txt"));
        Assert.True(!Deployment.IsExcluded(s, "src/app.py") && !Deployment.IsExcluded(s, "secrets_notes.py"));
    }

    // ------------------------------------------------------------ the form, and the passwords

    [Fact]
    public void AServerIsCheckedAndItsPasswordKeptApart()
    {
        string root = Temp("store");
        try
        {
            var body = new JsonObject { ["type"] = "sftp", ["host"] = "example.org", ["port"] = "2222", ["user"] = "me", ["root"] = "/srv\\app", ["uploadOnSave"] = true,
                ["mappings"] = new JsonArray(new JsonObject { ["local"] = "web\\", ["remote"] = "/public/" }), ["excluded"] = new JsonArray("*.log", " ") };
            var server = DeployStore.Parse(body);
            Assert.Equal("example.org", server.Host);
            Assert.Equal(2222, server.Port);
            Assert.Equal("/srv/app", server.Root);
            Assert.Equal("me@example.org", server.Name);
            Assert.Equal("web", server.Mappings[0].Local);
            Assert.Equal("public", server.Mappings[0].Remote);
            Assert.Equal("*.log", string.Join(",", server.Excluded));
            DeployStore.Save(root, server, Fake.Password);
            Assert.True(!File.ReadAllText(Path.Combine(root, ".pymagic", "deployment.json")).Contains(Fake.Password));
            Assert.Equal(Fake.Password, DeployStore.PasswordOf(root, server.Id));
            // saved again without a password: the password stays
            DeployStore.Save(root, server with { Name = "renamed" }, null);
            Assert.Equal(Fake.Password, DeployStore.PasswordOf(root, server.Id));
            Assert.Equal("renamed", DeployStore.Find(root, server.Id)!.Name);
            DeployStore.Remove(root, server.Id);
            Assert.Equal(0, DeployStore.Load(root).Count);
            Assert.Equal("", DeployStore.PasswordOf(root, server.Id));
            Assert.Throws<ServiceException>(() => DeployStore.Parse(new JsonObject { ["type"] = "scp", ["host"] = "h" }));
            Assert.Throws<ServiceException>(() => DeployStore.Parse(new JsonObject { ["type"] = "ftp", ["host"] = "" }));
            Assert.Throws<ServiceException>(() => DeployStore.Parse(new JsonObject { ["type"] = "folder", ["root"] = "" }));
            Assert.Throws<ServiceException>(() => DeployStore.Parse(new JsonObject { ["type"] = "ftp", ["host"] = "h", ["mappings"] = new JsonArray(new JsonObject { ["local"] = "../x", ["remote"] = "" }) }));
            Assert.Equal("anonymous", DeployStore.Parse(new JsonObject { ["type"] = "ftp", ["host"] = "h" }).User);
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------ a folder as a server

    [Fact]
    public void UploadDownloadCompareAndSyncOverAFolder()
    {
        string project = Temp("proj"), share = Temp("share");
        try
        {
            Put(project, "app.py", "print(1)\n");
            Put(project, "pkg/mod.py", "x = 1\n");
            Put(project, "pkg/__pycache__/mod.pyc", "junk");
            Put(project, ".git/config", "[core]");
            Put(project, "web/index.html", "<p>hi</p>");
            var server = Folder(share, new DeployMapping("", "app"), new DeployMapping("web", "public"));
            // upload a file, then a folder (what is excluded stays home)
            Assert.True(Deployment.Upload(project, server, new[] { "app.py" }).All(r => r.Ok));
            Assert.Equal("print(1)\n", File.ReadAllText(Path.Combine(share, "app", "app.py")));
            var all = Deployment.Upload(project, server, new[] { "." });
            Assert.Equal("app.py,pkg/mod.py,web/index.html", string.Join(",", all.Select(r => r.Path).OrderBy(x => x)));
            Assert.True(File.Exists(Path.Combine(share, "app", "pkg", "mod.py")) && File.Exists(Path.Combine(share, "public", "index.html")));
            Assert.True(!Directory.Exists(Path.Combine(share, "app", ".git")) && !Directory.Exists(Path.Combine(share, "app", "pkg", "__pycache__")));
            // compare: the same, then changed on the server
            var same = Deployment.Compare(project, server, "app.py");
            Assert.True(same.Same && same.LocalExists && same.RemoteExists);
            File.WriteAllText(Path.Combine(share, "app", "app.py"), "print(2)\n");
            var diff = Deployment.Compare(project, server, "app.py");
            Assert.True(!diff.Same);
            Assert.Equal("print(1)\n", diff.Local);
            Assert.Equal("print(2)\n", diff.Remote);
            Assert.True(!Deployment.Compare(project, server, "nope.py").RemoteExists);
            // sync plan: a change on the server, a new file on each side
            File.WriteAllText(Path.Combine(share, "app", "app.py"), "print(22222)\n");
            File.SetLastWriteTimeUtc(Path.Combine(share, "app", "app.py"), DateTime.UtcNow.AddMinutes(5));
            Put(project, "new_local.py", "a");
            Put(share, "app/only_remote.py", "b");
            var plan = Deployment.SyncPlan(project, server, "").ToDictionary(i => i.Path, i => i.Status);
            Assert.Equal("remote-newer", plan["app.py"]);
            Assert.Equal("local-only", plan["new_local.py"]);
            Assert.Equal("remote-only", plan["only_remote.py"]);
            Assert.Equal("same", plan["pkg/mod.py"]);
            Assert.True(!plan.ContainsKey(".git/config"));
            // download: a file, and a folder of the server (it makes the local files)
            Assert.True(Deployment.Download(project, server, new[] { "app.py", "only_remote.py" }).All(r => r.Ok));
            Assert.Equal("print(22222)\n", File.ReadAllText(Path.Combine(project, "app.py")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(project, "only_remote.py")));
            Directory.Delete(Path.Combine(project, "pkg"), true);
            Assert.True(Deployment.Download(project, server, new[] { "pkg" }).All(r => r.Ok));
            Assert.Equal("x = 1\n", File.ReadAllText(Path.Combine(project, "pkg", "mod.py")));
            // what is not on the server, and a path no mapping covers
            Assert.True(!Deployment.Download(project, server, new[] { "missing.py" })[0].Ok);
        }
        finally { Directory.Delete(project, true); Directory.Delete(share, true); }
    }

    [Fact]
    public void AFolderServerCannotLeaveItsFolder()
    {
        string share = Temp("jail");
        try
        {
            using var remote = new FolderRemote(share);
            Assert.Throws<ServiceException>(() => remote.Write("../escape.txt", new byte[] { 1 }));
            Assert.Throws<ServiceException>(() => remote.Read("../../etc/passwd"));
            remote.Write("a/b.txt", Encoding.UTF8.GetBytes("ok"));
            Assert.Equal("ok", Encoding.UTF8.GetString(remote.Read("a/b.txt")));
            Assert.Equal(1, remote.List("a").Count);
            remote.Delete("a");
            Assert.True(remote.Stat("a") == null);
        }
        finally { Directory.Delete(share, true); }
    }

    // ------------------------------------------------------------ FTP, against a small server of its own

    /// <summary>Just enough of an FTP server for the framework's client: USER, PASS, PWD, CWD, TYPE, PASV, LIST, RETR, STOR, MKD, DELE, RMD, QUIT.</summary>
    sealed class MiniFtp : IDisposable
    {
        readonly TcpListener _control = new(IPAddress.Loopback, 0);
        readonly string _root;
        readonly Thread _thread;
        volatile bool _stop;
        public int Port => ((IPEndPoint)_control.LocalEndpoint).Port;
        readonly System.Security.Cryptography.X509Certificates.X509Certificate2? _cert;
        public int TlsSessions, ProtectedData, StoredFiles;
        readonly bool _implicit;
        /// <param name="implicitTls">The control connection is TLS from its first byte (implicit FTPS), not after AUTH TLS.</param>
        public MiniFtp(string root, System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null, bool implicitTls = false) { _root = root; _cert = cert; _implicit = implicitTls; _control.Start(); _thread = new Thread(Accept) { IsBackground = true }; _thread.Start(); }
        void Accept()
        {
            while (!_stop)
            {
                try { var c = _control.AcceptTcpClient(); new Thread(() => { try { Session(c); } catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or SocketException or ObjectDisposedException) { /* a client that gave up */ } }) { IsBackground = true }.Start(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        string Map(string cwd, string arg) { string p = arg.StartsWith('/') ? arg : (cwd.TrimEnd('/') + "/" + arg); return Path.GetFullPath(Path.Combine(_root, p.TrimStart('/'))); }
        void Session(TcpClient client)
        {
            using var _ = client;
            Stream stream = client.GetStream();
            if (_implicit) { var tls = new System.Net.Security.SslStream(stream, false); tls.AuthenticateAsServer(_cert!); stream = tls; Interlocked.Increment(ref TlsSessions); }
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            string cwd = "/";
            TcpListener? data = null; bool protP = false;
            Stream Data(TcpClient dc) { Stream s = dc.GetStream(); if (!protP) return s; Interlocked.Increment(ref ProtectedData); var ssl = new System.Net.Security.SslStream(s, false); ssl.AuthenticateAsServer(_cert!); return ssl; }
            void Reply(string s) => writer.WriteLine(s);
            Reply("220 mini");
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string cmd = line.Split(' ')[0].ToUpperInvariant(), arg = line.Length > cmd.Length ? line[(cmd.Length + 1)..] : "";
                switch (cmd)
                {
                    case "AUTH" when _cert != null && arg.ToUpperInvariant() is "TLS" or "TLS-C":
                    {
                        Reply("234 go ahead"); Interlocked.Increment(ref TlsSessions);
                        var ssl = new System.Net.Security.SslStream(stream, false); ssl.AuthenticateAsServer(_cert); stream = ssl;
                        reader = new StreamReader(stream, Encoding.UTF8); writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                        break;
                    }
                    case "PBSZ": Reply("200 ok"); break;
                    case "PROT": protP = arg.Trim().ToUpperInvariant() == "P"; Reply("200 ok"); break;
                    case "USER": Reply("331 password"); break;
                    case "PASS": Reply("230 ok"); break;
                    case "OPTS": case "TYPE": case "MODE": case "STRU": Reply("200 ok"); break;
                    case "SYST": Reply("215 UNIX Type: L8"); break;
                    case "PWD": Reply($"257 \"{cwd}\" is the directory"); break;
                    case "CWD": { string d = Map(cwd, arg); if (Directory.Exists(d)) { cwd = arg.StartsWith('/') ? arg : cwd.TrimEnd('/') + "/" + arg; Reply("250 ok"); } else Reply("550 no"); break; }
                    case "PASV":
                        data?.Stop(); data = new TcpListener(IPAddress.Loopback, 0); data.Start();
                        int port = ((IPEndPoint)data.LocalEndpoint).Port;
                        Reply($"227 Entering Passive Mode (127,0,0,1,{port >> 8},{port & 255})");
                        break;
                    case "LIST":
                    {
                        string d = Map(cwd, arg.TrimStart('-').Trim().Length == 0 || arg.StartsWith('-') ? "" : arg);
                        if (!Directory.Exists(d)) { Reply("550 no"); break; }
                        Reply("150 listing");
                        using (var dc = data!.AcceptTcpClient())
                        using (var w = new StreamWriter(Data(dc), new UTF8Encoding(false)) { NewLine = "\r\n" })
                            foreach (var i in new DirectoryInfo(d).EnumerateFileSystemInfos())
                            {
                                string when = i.LastWriteTimeUtc.ToString("MMM dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                                w.WriteLine($"{(i is DirectoryInfo ? "d" : "-")}rw-r--r-- 1 u g {(i is FileInfo f ? f.Length : 4096),12} {when} {i.Name}");
                            }
                        Reply("226 done");
                        break;
                    }
                    case "RETR":
                    {
                        string f = Map(cwd, arg);
                        if (!File.Exists(f)) { Reply("550 no"); break; }
                        Reply("150 sending");
                        using (var dc = data!.AcceptTcpClient()) { var bytes = File.ReadAllBytes(f); using var ds = Data(dc); ds.Write(bytes, 0, bytes.Length); }
                        Reply("226 done");
                        break;
                    }
                    case "STOR":
                    {
                        string f = Map(cwd, arg);
                        Reply("150 receiving");
                        // as vsftpd: a data connection that ends without TLS's own end (reset, or closed with the server's bytes unread) is a failed upload
                        bool failed = false;
                        using (var dc = data!.AcceptTcpClient())
                        {
                            using var ms = new MemoryStream();
                            {
                                using var ds = Data(dc);
                                try
                                {
                                    ds.CopyTo(ms);
                                    if (ds is System.Net.Security.SslStream tls) { tls.ShutdownAsync().Wait(5000); Thread.Sleep(200); dc.Client.Send(Array.Empty<byte>()); }
                                }
                                catch (Exception x) when (x is IOException or AggregateException or SocketException) { failed = true; }
                            }
                            if (!failed) { Interlocked.Increment(ref StoredFiles); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, ms.ToArray()); }
                        }
                        Reply(failed ? "426 Failure reading network stream." : "226 done");
                        break;
                    }
                    case "MKD": { string d = Map(cwd, arg); if (Directory.Exists(d)) Reply("550 exists"); else { Directory.CreateDirectory(d); Reply($"257 \"{arg}\" made"); } break; }
                    case "DELE": { string f = Map(cwd, arg); if (File.Exists(f)) { File.Delete(f); Reply("250 gone"); } else Reply("550 no"); break; }
                    case "RMD": { string d = Map(cwd, arg); if (Directory.Exists(d)) { Directory.Delete(d, true); Reply("250 gone"); } else Reply("550 no"); break; }
                    case "QUIT": Reply("221 bye"); return;
                    default: Reply("502 not implemented"); break;
                }
            }
        }
        public void Dispose() { _stop = true; _control.Stop(); }
    }

    [Fact]
    public void FtpUploadsDownloadsAndListsFolders()
    {
        string share = Temp("ftp");
        try
        {
            using var ftp = new MiniFtp(share);
            using var remote = new FtpRemote("127.0.0.1", ftp.Port, "anonymous", "");
            remote.Write("site/pkg/mod.py", Encoding.UTF8.GetBytes("x = 1\n"));
            remote.Write("site/readme.txt", Encoding.UTF8.GetBytes("hello"));
            Assert.Equal("x = 1\n", File.ReadAllText(Path.Combine(share, "site", "pkg", "mod.py")));
            Assert.Equal("pkg,readme.txt", string.Join(",", remote.List("site").Select(e => e.Name)));
            Assert.True(remote.List("site").First(e => e.Name == "pkg").IsDir);
            Assert.Equal(5, remote.List("site").First(e => e.Name == "readme.txt").Size);
            Assert.Equal("hello", Encoding.UTF8.GetString(remote.Read("site/readme.txt")));
            Assert.True(remote.Stat("site/pkg/mod.py") is { IsDir: false });
            Assert.True(remote.Stat("site/nope") == null);
            Assert.Equal(0, remote.List("missing").Count);
            remote.Delete("site/readme.txt");
            Assert.True(!File.Exists(Path.Combine(share, "site", "readme.txt")));
            // the same operations as a deployment: upload, compare, download through the server record
            string project = Temp("ftpproj");
            try
            {
                Put(project, "a.py", "print('a')\n");
                var server = new DeployServer("ftp-x", "x", "ftp", "127.0.0.1", ftp.Port, "anonymous", "/site", "", false, new() { new DeployMapping("", "") }, new(), "");
                Assert.True(Deployment.Upload(project, server, new[] { "a.py" }).All(r => r.Ok));
                Assert.Equal("print('a')\n", File.ReadAllText(Path.Combine(share, "site", "a.py")));
                Assert.True(Deployment.Compare(project, server, "a.py").Same);
                File.Delete(Path.Combine(project, "a.py"));
                Assert.True(Deployment.Download(project, server, new[] { "a.py" }).All(r => r.Ok));
                Assert.Equal("print('a')\n", File.ReadAllText(Path.Combine(project, "a.py")));
            }
            finally { Directory.Delete(project, true); }
        }
        finally { Directory.Delete(share, true); }
    }

    // ------------------------------------------------------------ SFTP, against a server that PYMAGIC_TEST_SFTP names ("host:port:user:password")

    [Fact]
    public void SftpUploadsDownloadsAndRemembersTheHostKey()
    {
        string? spec = Environment.GetEnvironmentVariable("PYMAGIC_TEST_SFTP");
        if (spec == null) return;
        var p = spec.Split(':');
        string project = Temp("sftpproj");
        try
        {
            Put(project, "pkg/mod.py", "x = 1\n");
            Put(project, "top.txt", "top");
            var server = new DeployServer("sftp-x", "x", "sftp", p[0], int.Parse(p[1]), p[2], "/site", "", false, new() { new DeployMapping("", "") }, new(), "");
            DeployStore.Save(project, server, p[3]);
            Assert.True(Deployment.Upload(project, server, new[] { "." }).All(r => r.Ok), "upload");
            // the key was learned on the first connection and is kept
            var kept = DeployStore.Find(project, "sftp-x")!;
            Assert.True(kept.HostKey.StartsWith("SHA256:") || kept.HostKey.Length > 20, kept.HostKey);
            Assert.True(Deployment.Compare(project, kept, "pkg/mod.py").Same);
            using (var remote = Deployment.Open(project, kept))
            {
                Assert.Equal("pkg,top.txt", string.Join(",", remote.List("site").Select(e => e.Name)));
                Assert.Equal("top", Encoding.UTF8.GetString(remote.Read("site/top.txt")));
                Assert.True(remote.Stat("site/pkg") is { IsDir: true });
            }
            File.Delete(Path.Combine(project, "top.txt"));
            Assert.True(Deployment.Download(project, kept, new[] { "top.txt" }).All(r => r.Ok));
            Assert.Equal("top", File.ReadAllText(Path.Combine(project, "top.txt")));
            // another key than the remembered one is refused
            var forged = kept with { HostKey = "SHA256:forgedforgedforgedforgedforgedforgedforgedforged" };
            string refused = "";
            try { Deployment.Open(project, forged).Dispose(); } catch (ServiceException e) { refused = e.Message; }
            Assert.Contains("not the one remembered", refused);
            // a wrong password says so
            DeployStore.Save(project, kept, "wrong password");
            Assert.Throws<ServiceException>(() => Deployment.Open(project, kept));
        }
        finally { Directory.Delete(project, true); }
    }

    // ------------------------------------------------------------ FTPS (explicit TLS), against MiniFtp with a certificate made here

    static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new System.Security.Cryptography.X509Certificates.X509Certificate2(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
    }

    [Fact]
    public void FtpsEncryptsTheControlAndTheDataConnectionAndChecksTheCertificate()
    {
        string share = Temp("ftps");
        try
        {
            using var cert = SelfSigned();
            using var ftp = new MiniFtp(share, cert);
            // a certificate this computer does not trust is refused (nothing is sent), and its fingerprint is told
            string refused = Msg(() => new FtpsRemote("127.0.0.1", ftp.Port, "anonymous", "", false, "", null).Dispose());
            Assert.Contains("not one this computer trusts", refused);
            Assert.Contains(FtpsRemote.Fingerprint(cert), refused);
            Assert.Equal(0, ftp.StoredFiles);
            // once it is trusted (its fingerprint is the server's), everything works over TLS
            string project = Temp("ftpsproj");
            try
            {
                Put(project, "pkg/mod.py", "x = 2\n");
                var server = new DeployServer("ftps-x", "x", "ftps", "127.0.0.1", ftp.Port, "anonymous", "/site", "", false, new() { new DeployMapping("", "") }, new(), FtpsRemote.Fingerprint(cert));
                var up = Deployment.Upload(project, server, new[] { "pkg" }); Assert.True(up.All(r => r.Ok), "upload " + string.Join(";", up.Select(r => r.Error)));
                Assert.Equal("x = 2\n", File.ReadAllText(Path.Combine(share, "site", "pkg", "mod.py")));
                Assert.True(Deployment.Compare(project, server, "pkg/mod.py").Same);
                File.Delete(Path.Combine(project, "pkg", "mod.py"));
                Assert.True(Deployment.Download(project, server, new[] { "pkg/mod.py" }).All(r => r.Ok));
                Assert.Equal("x = 2\n", File.ReadAllText(Path.Combine(project, "pkg", "mod.py")));
                using (var remote = Deployment.Open(project, server))
                {
                    Assert.Equal("mod.py", string.Join(",", remote.List("site/pkg").Select(e => e.Name)));
                    Assert.True(remote.Stat("site/pkg") is { IsDir: true } && remote.Stat("site/nope") == null);
                    Assert.Equal(0, remote.List("missing").Count);
                }
                Assert.True(Deployment.DeleteRemote(project, server, "pkg/mod.py").Ok);
                Assert.True(!File.Exists(Path.Combine(share, "site", "pkg", "mod.py")));
                // a folder that has files in it goes with them
                Put(share, "site/full/a.txt", "a"); Put(share, "site/full/sub/b.txt", "b");
                Assert.True(Deployment.DeleteRemote(project, server, "full").Ok);
                Assert.True(!Directory.Exists(Path.Combine(share, "site", "full")));
                // the server was asked for TLS on the control connection and for protected data connections
                Assert.True(ftp.TlsSessions >= 1 && ftp.ProtectedData >= 1, $"tls {ftp.TlsSessions}, data {ftp.ProtectedData}");
            }
            finally { Directory.Delete(project, true); }
        }
        finally { Directory.Delete(share, true); }
    }

    [Fact]
    public void ImplicitFtpsAndASelfSignedCertificateTrustedOnceAndRemembered()
    {
        string share = Temp("ftpsi"), project = Temp("ftpsiproj");
        try
        {
            using var cert = SelfSigned();
            using var ftp = new MiniFtp(share, cert, implicitTls: true);
            // the form: implicit TLS is on port 990 unless another is named
            var parsed = DeployStore.Parse(new JsonObject { ["type"] = "ftps", ["host"] = "ftp.example.org", ["implicitTls"] = true });
            Assert.True(parsed.ImplicitTls);
            Assert.Equal(990, parsed.Port);
            Assert.True(!DeployStore.Parse(new JsonObject { ["type"] = "ftp", ["host"] = "h", ["implicitTls"] = true }).ImplicitTls, "only FTPS has it");
            var server = new DeployServer("ftps-i", "x", "ftps", "127.0.0.1", ftp.Port, "anonymous", "", "", false, new() { new DeployMapping("", "") }, new(), "", false, ImplicitTls: true);
            DeployStore.Save(project, server, "");
            // not trusted yet: refused; trusted by the user: taken, and its fingerprint is kept with the server
            Assert.Contains("not one this computer trusts", Msg(() => Deployment.Open(project, server).Dispose()));
            using (var remote = Deployment.Open(project, server, trustCertificate: true)) remote.Write("hello.txt", Encoding.UTF8.GetBytes("hi"));
            Assert.Equal("hi", File.ReadAllText(Path.Combine(share, "hello.txt")));
            var kept = DeployStore.Find(project, server.Id)!;
            Assert.Equal(FtpsRemote.Fingerprint(cert), kept.HostKey);
            Assert.True(kept.ImplicitTls, "kept in deployment.json");
            Put(project, "a.py", "a = 1\n");
            Assert.True(Deployment.Upload(project, kept, new[] { "a.py" }).All(r => r.Ok));
            Assert.Equal("a = 1\n", File.ReadAllText(Path.Combine(share, "a.py")));
            // the server shows another certificate: the remembered one is not it, and it is refused
            using var other = SelfSigned();
            using var ftp2 = new MiniFtp(share, other, implicitTls: true);
            Assert.Contains("not the one trusted before", Msg(() => Deployment.Open(project, kept with { Port = ftp2.Port }).Dispose()));
        }
        finally { Directory.Delete(project, true); Directory.Delete(share, true); }
    }

    [Fact]
    public void APlainFtpClientDoesNotUseTls()
    {
        string share = Temp("ftpplain");
        try
        {
            using var cert = SelfSigned();
            using var ftp = new MiniFtp(share, cert);
            using var plain = new FtpRemote("127.0.0.1", ftp.Port, "anonymous", "");
            plain.Write("a.txt", Encoding.UTF8.GetBytes("a"));
            Assert.Equal(0, ftp.TlsSessions);
        }
        finally { Directory.Delete(share, true); }
    }

    // ------------------------------------------------------------ deleting on a server

    [Fact]
    public void AFileOrFolderOfAServerIsDeletedByItsPathAndTheTopIsNot()
    {
        string share = Temp("del"), project = Temp("delproj");
        try
        {
            Put(share, "site/a.txt", "a"); Put(share, "site/dir/b.txt", "b"); Put(share, "keep.txt", "k");
            var server = Folder(share);
            Assert.True(Deployment.DeleteRemote(project, server, "site/a.txt").Ok);
            Assert.True(!File.Exists(Path.Combine(share, "site", "a.txt")));
            Assert.True(Deployment.DeleteRemote(project, server, "site/dir").Ok);
            Assert.True(!Directory.Exists(Path.Combine(share, "site", "dir")));
            Assert.True(Directory.Exists(Path.Combine(share, "site")) && File.Exists(Path.Combine(share, "keep.txt")));
            // what is not there, the top, a way out of the server
            Assert.True(!Deployment.DeleteRemote(project, server, "site/nope.txt").Ok);
            Assert.True(!Deployment.DeleteRemote(project, server, "").Ok && !Deployment.DeleteRemote(project, server, "/").Ok);
            Assert.True(!Deployment.DeleteRemote(project, server, "../x").Ok && !Deployment.DeleteRemote(project, server, "site/../..").Ok);
            Assert.True(File.Exists(Path.Combine(share, "keep.txt")));
            // a server with a root: the path is below it
            Put(share, "srv/app/x.txt", "x");
            var rooted = new DeployServer("ftp-y", "y", "folder", "", 0, "", share, "", false, new() { new DeployMapping("", "") }, new(), "");
            Assert.True(Deployment.DeleteRemote(project, rooted, "srv/app/x.txt").Ok);
            Assert.True(!File.Exists(Path.Combine(share, "srv", "app", "x.txt")));
        }
        finally { Directory.Delete(project, true); Directory.Delete(share, true); }
    }

    [Fact]
    public void ADeleteInTheProjectReachesTheServerOnlyWhenTheServerWasSetToAndOnlyForFiles()
    {
        string share = Temp("deld"), project = Temp("deldproj");
        try
        {
            Put(share, "a.py", "a"); Put(share, "b.py", "b"); Put(share, "sub/c.py", "c"); Put(share, "x.log", "l"); Put(share, "outside/d.py", "d");
            var off = Folder(share, new DeployMapping("", ""));
            Assert.True(!(off.DeleteOnDelete));
            Assert.Equal(0, Deployment.DeleteForLocal(project, off, new[] { "a.py" }).Count);
            Assert.True(File.Exists(Path.Combine(share, "a.py")));
            var on = off with { DeleteOnDelete = true, Excluded = new() { "*.log" } };
            var results = Deployment.DeleteForLocal(project, on, new[] { "a.py", "sub/c.py", "x.log", "not_there.py", ".git/config", "sub" });
            Assert.Equal("a.py,sub/c.py", string.Join(",", results.Select(r => r.Path)));
            Assert.True(results.All(r => r.Ok));
            Assert.True(!File.Exists(Path.Combine(share, "a.py")) && !File.Exists(Path.Combine(share, "sub", "c.py")));
            // an excluded file, a file only the server had, and a folder name are left alone
            Assert.True(File.Exists(Path.Combine(share, "x.log")) && File.Exists(Path.Combine(share, "b.py")) && Directory.Exists(Path.Combine(share, "sub")));
            // a mapping limits it: only what the project's src folder maps to
            var narrow = on with { Mappings = new() { new DeployMapping("src", "outside") } };
            Assert.Equal(1, Deployment.DeleteForLocal(project, narrow, new[] { "src/d.py", "b.py" }).Count);
            Assert.True(!File.Exists(Path.Combine(share, "outside", "d.py")) && File.Exists(Path.Combine(share, "b.py")));
            // the setting is read from the form, and defaults off
            Assert.True(!(DeployStore.Parse(new JsonObject { ["type"] = "folder", ["root"] = share }).DeleteOnDelete));
            Assert.True(DeployStore.Parse(new JsonObject { ["type"] = "folder", ["root"] = share, ["deleteOnDelete"] = true }).DeleteOnDelete);
        }
        finally { Directory.Delete(project, true); Directory.Delete(share, true); }
    }

    // ------------------------------------------------------------ FTPS in the form, a key file and its passphrase

    [Fact]
    public void AnFtpsServerIsAKindOfItsOwnAndAPassphraseIsKeptWithThePassword()
    {
        string root = Temp("keystore");
        try
        {
            var ftps = DeployStore.Parse(new JsonObject { ["type"] = "FTPS", ["host"] = "ftp.example.org" });
            Assert.Equal("ftps", ftps.Type);
            Assert.Equal(21, ftps.Port);
            Assert.Equal("anonymous", ftps.User);
            var sftp = DeployStore.Parse(new JsonObject { ["type"] = "sftp", ["host"] = "h", ["user"] = "me", ["keyFile"] = " ~/.ssh/id_ed25519 " });
            Assert.Equal("~/.ssh/id_ed25519", sftp.KeyFile);
            DeployStore.Save(root, sftp, "pw", "the passphrase");
            string visible = File.ReadAllText(Path.Combine(root, ".pymagic", "deployment.json"));
            Assert.True(!visible.Contains("the passphrase") && !visible.Contains("pw\""), "the settings file holds neither");
            Assert.Equal("the passphrase", DeployStore.PassphraseOf(root, sftp.Id));
            Assert.Equal("pw", DeployStore.PasswordOf(root, sftp.Id));
            // saved without them: both stay; an empty passphrase takes only the passphrase away
            DeployStore.Save(root, sftp with { Name = "n" }, null, null);
            Assert.Equal("the passphrase", DeployStore.PassphraseOf(root, sftp.Id));
            DeployStore.Save(root, sftp, null, "");
            Assert.Equal("", DeployStore.PassphraseOf(root, sftp.Id));
            Assert.Equal("pw", DeployStore.PasswordOf(root, sftp.Id));
            DeployStore.Save(root, sftp, null, "again");
            DeployStore.Remove(root, sftp.Id);
            Assert.Equal("", DeployStore.PassphraseOf(root, sftp.Id));
            // a key file: ~ is the home folder, a relative path is from the project
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "k"), Deployment.KeyPath(root, "~/.ssh/k"));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "keys", "k")), Deployment.KeyPath(root, "keys/k"));
            Assert.Equal("", Deployment.KeyPath(root, "  "));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ASftpKeyFileIsReadWithItsPassphraseBeforeAnythingIsSent()
    {
        string project = Temp("keyfile");
        try
        {
            var missing = new DeployServer("k1", "k", "sftp", "127.0.0.1", 1, "me", "/", "nokey", false, new() { new DeployMapping("", "") }, new(), "");
            Assert.Contains("not there", Msg(() => Deployment.Open(project, missing)));
            string key = Path.Combine(project, "id_test");
            var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ssh-keygen", $"-q -t ed25519 -N secretphrase -f \"{key}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true });
            if (gen == null) return;
            gen.WaitForExit(20000);
            if (gen.ExitCode != 0 || !File.Exists(key)) return;       // no ssh-keygen here: the rest is not checked
            var server = missing with { KeyFile = "id_test" };          // relative: from the project
            DeployStore.Save(project, server, null, "wrong one");
            Assert.Contains("key file could not be read", Msg(() => Deployment.Open(project, server)));
            // the right passphrase gets as far as connecting (port 1 is closed), so the key was opened
            DeployStore.Save(project, server, null, "secretphrase");
            Assert.Contains("Could not connect", Msg(() => Deployment.Open(project, server)));
        }
        finally { Directory.Delete(project, true); }
    }

    // against a server that PYMAGIC_TEST_SFTP_KEY names ("host|port|user|keyfile|passphrase"; the public key is in the server's authorized_keys)
    [Fact]
    public void SftpLogsInWithAKeyFile()
    {
        string? spec = Environment.GetEnvironmentVariable("PYMAGIC_TEST_SFTP_KEY");
        if (spec == null) return;
        var p = spec.Split('|');
        string project = Temp("sftpkey");
        try
        {
            Put(project, "k.txt", "key login");
            var server = new DeployServer("sftp-key", "x", "sftp", p[0], int.Parse(p[1]), p[2], "/site_key", p[3], false, new() { new DeployMapping("", "") }, new(), "");
            DeployStore.Save(project, server, "", p.Length > 4 ? p[4] : "");
            Assert.True(Deployment.Upload(project, server, new[] { "k.txt" }).All(r => r.Ok), "upload with the key");
            Assert.True(Deployment.Compare(project, DeployStore.Find(project, "sftp-key")!, "k.txt").Same);
            Assert.True(Deployment.DeleteRemote(project, server, "k.txt").Ok);
        }
        finally { Directory.Delete(project, true); }
    }
}
