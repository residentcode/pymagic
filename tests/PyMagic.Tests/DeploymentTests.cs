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
            DeployStore.Save(root, server, "s3cret");
            Assert.True(!File.ReadAllText(Path.Combine(root, ".pymagic", "deployment.json")).Contains("s3cret"));
            Assert.Equal("s3cret", DeployStore.PasswordOf(root, server.Id));
            // saved again without a password: the password stays
            DeployStore.Save(root, server with { Name = "renamed" }, null);
            Assert.Equal("s3cret", DeployStore.PasswordOf(root, server.Id));
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
        public MiniFtp(string root) { _root = root; _control.Start(); _thread = new Thread(Accept) { IsBackground = true }; _thread.Start(); }
        void Accept()
        {
            while (!_stop)
            {
                try { var c = _control.AcceptTcpClient(); new Thread(() => Session(c)) { IsBackground = true }.Start(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        string Map(string cwd, string arg) { string p = arg.StartsWith('/') ? arg : (cwd.TrimEnd('/') + "/" + arg); return Path.GetFullPath(Path.Combine(_root, p.TrimStart('/'))); }
        void Session(TcpClient client)
        {
            using var _ = client;
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            string cwd = "/";
            TcpListener? data = null;
            void Reply(string s) => writer.WriteLine(s);
            Reply("220 mini");
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string cmd = line.Split(' ')[0].ToUpperInvariant(), arg = line.Length > cmd.Length ? line[(cmd.Length + 1)..] : "";
                switch (cmd)
                {
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
                        using (var w = new StreamWriter(dc.GetStream(), new UTF8Encoding(false)) { NewLine = "\r\n" })
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
                        using (var dc = data!.AcceptTcpClient()) { var bytes = File.ReadAllBytes(f); dc.GetStream().Write(bytes, 0, bytes.Length); }
                        Reply("226 done");
                        break;
                    }
                    case "STOR":
                    {
                        string f = Map(cwd, arg);
                        Reply("150 receiving");
                        using (var dc = data!.AcceptTcpClient()) { using var ms = new MemoryStream(); dc.GetStream().CopyTo(ms); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, ms.ToArray()); }
                        Reply("226 done");
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
}
