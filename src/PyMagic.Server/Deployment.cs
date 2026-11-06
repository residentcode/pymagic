using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>A folder of the project and the folder of a server it is deployed to (both relative: "" is the project root, and the server's root).</summary>
public sealed record DeployMapping(string Local, string Remote);

/// <summary>
/// A server the project is deployed to (item 244): a folder (a share, a USB stick), an FTP server (<c>ftp</c>, or <c>ftps</c>: explicit TLS, or
/// implicit TLS when <c>ImplicitTls</c>), or an SFTP server. <c>HostKey</c> is the SFTP server's key, or the fingerprint of the FTPS server's certificate
/// that was trusted (a self-signed one). <c>DeleteOnDelete</c> (off unless chosen) takes a file off the server when it is deleted in the project. <c>Root</c> is the
/// folder on it everything below is relative to (for a folder server, the folder itself).
/// </summary>
public sealed record DeployServer(string Id, string Name, string Type, string Host, int Port, string User, string Root, string KeyFile, bool UploadOnSave,
    List<DeployMapping> Mappings, List<string> Excluded, string HostKey, bool DeleteOnDelete = false, bool ImplicitTls = false);

public sealed record RemoteEntry(string Name, bool IsDir, long Size, DateTime Modified);

/// <summary>One file's state for a sync: where it is, and which side has the newer one.</summary>
public sealed record SyncItem(string Path, string Status, long LocalSize, long RemoteSize, DateTime? LocalTime, DateTime? RemoteTime);

public sealed record TransferResult(string Path, bool Ok, string? Error);

/// <summary>A connection to a server: what a deployment needs of one.</summary>
public interface IRemote : IDisposable
{
    IReadOnlyList<RemoteEntry> List(string dir);
    byte[] Read(string path);
    void Write(string path, byte[] data);
    void Delete(string path);
    RemoteEntry? Stat(string path);
}

/// <summary>The servers of a project (<c>.pymagic/deployment.json</c>) and their passwords (<c>.pymagic/deployment.private.json</c>, kept out of version control).</summary>
public static class DeployStore
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static string File_(string root) => System.IO.Path.Combine(root, ".pymagic", "deployment.json");
    static string Private(string root) => System.IO.Path.Combine(root, ".pymagic", "deployment.private.json");

    public static List<DeployServer> Load(string root)
    {
        try { return JsonSerializer.Deserialize<List<DeployServer>>(File.ReadAllText(File_(root)), Options) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    const string PassphraseSuffix = "#passphrase";

    static Dictionary<string, string> Passwords(string root)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Private(root))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static string PasswordOf(string root, string id) => Passwords(root).GetValueOrDefault(id, "");

    /// <summary>The passphrase of the private key file, kept in the same private file as the password.</summary>
    public static string PassphraseOf(string root, string id) => Passwords(root).GetValueOrDefault(id + PassphraseSuffix, "");

    public static DeployServer? Find(string root, string id) => Load(root).FirstOrDefault(s => s.Id == id);

    /// <summary>Makes a server from what the form sent, checked. <exception cref="ServiceException">A field is not usable.</exception></summary>
    public static DeployServer Parse(System.Text.Json.Nodes.JsonObject body, string? existingId = null)
    {
        string type = (body["type"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (type is not ("folder" or "ftp" or "ftps" or "sftp")) throw new ServiceException("A server is a folder, an FTP server, an FTPS server or an SFTP server.");
        string host = (body["host"]?.GetValue<string>() ?? "").Trim();
        string root = (body["root"]?.GetValue<string>() ?? "").Trim().Replace('\\', '/');
        if (type == "folder")
        {
            if (root.Length == 0) throw new ServiceException("Name the folder.");
            host = "";
        }
        else if (host.Length == 0) throw new ServiceException("Name the host.");
        bool implicitTls = type == "ftps" && body["implicitTls"]?.GetValue<bool>() == true;
        int port = body["port"] is { } p && int.TryParse(p.ToString(), out var n) ? n : implicitTls ? 990 : (type is "ftp" or "ftps" ? 21 : 22);
        if (port is < 1 or > 65535) throw new ServiceException("The port is a number from 1 to 65535.");
        string user = (body["user"]?.GetValue<string>() ?? "").Trim();
        foreach (var field in new[] { host, user, root }) if (field.Any(char.IsControl)) throw new ServiceException("A name has a control character in it.");
        if (type != "folder" && user.Length == 0) user = type is "ftp" or "ftps" ? "anonymous" : "";
        string name = (body["name"]?.GetValue<string>() ?? "").Trim();
        if (name.Length == 0) name = type == "folder" ? root : $"{user}@{host}";
        string id = existingId ?? new string((type + "-" + (type == "folder" ? root : host + "-" + port + "-" + user)).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        var mappings = (body["mappings"] as System.Text.Json.Nodes.JsonArray)?.Select(m => new DeployMapping(Clean(m?["local"]?.GetValue<string>()), Clean(m?["remote"]?.GetValue<string>()))).ToList();
        if (mappings == null || mappings.Count == 0) mappings = new() { new DeployMapping("", "") };
        if (mappings.Any(m => m.Local.Split('/').Contains("..") || m.Remote.Split('/').Contains(".."))) throw new ServiceException("A mapping cannot climb out of its folder.");
        var excluded = (body["excluded"] as System.Text.Json.Nodes.JsonArray)?.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToList() ?? new();
        return new DeployServer(id, name, type, host, port, user, root, (body["keyFile"]?.GetValue<string>() ?? "").Trim(), body["uploadOnSave"]?.GetValue<bool>() ?? false, mappings, excluded, body["hostKey"]?.GetValue<string>() ?? "", body["deleteOnDelete"]?.GetValue<bool>() ?? false, implicitTls);
    }

    static string Clean(string? path) => (path ?? "").Trim().Replace('\\', '/').Trim('/');

    public static void Save(string root, DeployServer server, string? password, string? passphrase = null)
    {
        var list = Load(root).Where(s => s.Id != server.Id).ToList();
        list.Add(server);
        Directory.CreateDirectory(System.IO.Path.Combine(root, ".pymagic"));
        System.IO.File.WriteAllText(File_(root), JsonSerializer.Serialize(list, Options));
        // a password or passphrase that was not sent stays as it was
        if (password != null || passphrase != null)
        {
            var passwords = Passwords(root);
            if (password != null) { if (password.Length > 0) passwords[server.Id] = password; else passwords.Remove(server.Id); }
            if (passphrase != null) { if (passphrase.Length > 0) passwords[server.Id + PassphraseSuffix] = passphrase; else passwords.Remove(server.Id + PassphraseSuffix); }
            System.IO.File.WriteAllText(Private(root), JsonSerializer.Serialize(passwords));
        }
        PymagicFolder.IgnoreInGit(System.IO.Path.Combine(root, ".pymagic"), "deployment.private.json");
    }

    public static void Remove(string root, string id)
    {
        if (!System.IO.File.Exists(File_(root))) return;
        System.IO.File.WriteAllText(File_(root), JsonSerializer.Serialize(Load(root).Where(s => s.Id != id).ToList(), Options));
        var passwords = Passwords(root);
        bool had = passwords.Remove(id) | passwords.Remove(id + PassphraseSuffix);
        if (had) System.IO.File.WriteAllText(Private(root), JsonSerializer.Serialize(passwords));
    }
}

public sealed class FolderRemote : IRemote
{
    readonly string _root;
    public FolderRemote(string root) { _root = System.IO.Path.GetFullPath(root); }

    string Full(string path)
    {
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, path.TrimStart('/')));
        if (full != _root && !full.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ServiceException("That path is outside the server's folder.");
        return full;
    }

    public IReadOnlyList<RemoteEntry> List(string dir)
    {
        string full = Full(dir);
        if (!Directory.Exists(full)) return Array.Empty<RemoteEntry>();
        return new DirectoryInfo(full).EnumerateFileSystemInfos().Select(i => new RemoteEntry(i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0, i.LastWriteTimeUtc))
            .OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public byte[] Read(string path) => File.ReadAllBytes(Full(path));
    public void Write(string path, byte[] data) { string full = Full(path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!); File.WriteAllBytes(full, data); }
    public void Delete(string path) { string full = Full(path); if (File.Exists(full)) File.Delete(full); else if (Directory.Exists(full)) Directory.Delete(full, true); }
    public RemoteEntry? Stat(string path)
    {
        string full = Full(path);
        if (File.Exists(full)) { var f = new FileInfo(full); return new RemoteEntry(f.Name, false, f.Length, f.LastWriteTimeUtc); }
        if (Directory.Exists(full)) { var d = new DirectoryInfo(full); return new RemoteEntry(d.Name, true, 0, d.LastWriteTimeUtc); }
        return null;
    }
    public void Dispose() { }
}

/// <summary>FTP through the framework's own client: passive mode, binary, LIST read in the Unix and the DOS formats.</summary>
#pragma warning disable SYSLIB0014   // FtpWebRequest is the client that needs no download
public sealed class FtpRemote : IRemote
{
    readonly string _host; readonly int _port; readonly NetworkCredential _credential;
    /// <summary>Plain FTP; FTPS is <see cref="FtpsRemote"/>.</summary>
    public FtpRemote(string host, int port, string user, string password) { _host = host; _port = port; _credential = new NetworkCredential(user, password); }

    FtpWebRequest Request(string path, string method)
    {
        var uri = new UriBuilder("ftp", _host, _port) { Path = "/" + string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) };
        var r = (FtpWebRequest)WebRequest.Create(uri.Uri);
        r.Method = method; r.Credentials = _credential; r.UsePassive = true; r.UseBinary = true; r.KeepAlive = false; r.Timeout = 20000; r.ReadWriteTimeout = 30000;
        return r;
    }

    static WebResponse Respond(FtpWebRequest request) => request.GetResponse();

    static readonly Regex Unix =new(@"^(?<kind>[-dlDL])[\w-]{9}[+@.]?\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\w{3}\s+\d{1,2}\s+(?:\d{4}|\d{1,2}:\d{2}))\s+(?<name>.+)$", RegexOptions.Compiled);
    static readonly Regex Dos = new(@"^(?<date>\d{2}-\d{2}-\d{2,4}\s+\d{1,2}:\d{2}[AP]M)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$", RegexOptions.Compiled);

    public IReadOnlyList<RemoteEntry> List(string dir)
    {
        var lines = new List<string>();
        try
        {
            using var res = (FtpWebResponse)Respond(Request(dir, WebRequestMethods.Ftp.ListDirectoryDetails));
            using var reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
        }
        catch (WebException e) when (e.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable }) { return Array.Empty<RemoteEntry>(); }
        return ParseListing(lines);
    }

    /// <summary>The lines of a LIST answer, in the Unix and the DOS formats, as entries: folders first, then by name.</summary>
    internal static List<RemoteEntry> ParseListing(IEnumerable<string> lines)
    {
        var entries = new List<RemoteEntry>();
        foreach (var line in lines)
        {
            if (Unix.Match(line) is { Success: true } u && u.Groups["name"].Value is var n && n is not ("." or ".."))
                entries.Add(new RemoteEntry(n.Contains(" -> ") ? n[..n.IndexOf(" -> ", StringComparison.Ordinal)] : n, u.Groups["kind"].Value is "d" or "D", long.Parse(u.Groups["size"].Value), ParseUnixDate(u.Groups["date"].Value)));
            else if (Dos.Match(line) is { Success: true } d)
                entries.Add(new RemoteEntry(d.Groups["name"].Value, d.Groups["size"].Value == "<DIR>", d.Groups["size"].Value == "<DIR>" ? 0 : long.Parse(d.Groups["size"].Value),
                    DateTime.TryParseExact(d.Groups["date"].Value, new[] { "MM-dd-yy hh:mmtt", "MM-dd-yyyy hh:mmtt", "MM-dd-yy h:mmtt" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTime.MinValue));
        }
        return entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static DateTime ParseUnixDate(string text)
    {
        var parts = Regex.Replace(text, @"\s+", " ").Split(' ');
        string year = parts[2].Contains(':') ? DateTime.UtcNow.Year.ToString() : parts[2];
        string time = parts[2].Contains(':') ? parts[2] : "00:00";
        return DateTime.TryParseExact($"{parts[0]} {parts[1]} {year} {time}", new[] { "MMM d yyyy HH:mm", "MMM dd yyyy HH:mm" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTime.MinValue;
    }

    public byte[] Read(string path)
    {
        using var res = (FtpWebResponse)Respond(Request(path, WebRequestMethods.Ftp.DownloadFile));
        using var ms = new MemoryStream();
        res.GetResponseStream().CopyTo(ms);
        return ms.ToArray();
    }

    public void Write(string path, byte[] data)
    {
        // the folders above are made first (one that exists is refused, which is fine)
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
        {
            try { using var _ = (FtpWebResponse)Respond(Request(string.Join("/", parts.Take(i)), WebRequestMethods.Ftp.MakeDirectory)); }
            catch (WebException) { /* it exists */ }
        }
        var req = Request(path, WebRequestMethods.Ftp.UploadFile);
        req.ContentLength = data.Length;
        using (var stream = req.GetRequestStream()) stream.Write(data, 0, data.Length);
        using var res = (FtpWebResponse)Respond(req);
    }

    public void Delete(string path)
    {
        try { using var _ = (FtpWebResponse)Respond(Request(path, WebRequestMethods.Ftp.DeleteFile)); }
        catch (WebException) { using var _ = (FtpWebResponse)Respond(Request(path, WebRequestMethods.Ftp.RemoveDirectory)); }
    }

    public RemoteEntry? Stat(string path)
    {
        string name = path.TrimEnd('/').Split('/').Last();
        string parent = path.TrimEnd('/').Contains('/') ? path.TrimEnd('/')[..path.TrimEnd('/').LastIndexOf('/')] : "";
        return List(parent).FirstOrDefault(e => e.Name == name);
    }
    public void Dispose() { }
}
#pragma warning restore SYSLIB0014

/// <summary>
/// FTPS through a client of its own (the framework's FtpWebRequest knows neither implicit TLS nor a certificate trusted for one server):
/// explicit TLS (AUTH TLS on the usual port, 21) or implicit TLS (the connection is TLS from its first byte, port 990), with the data
/// connections protected too (PBSZ 0, PROT P) and passive (EPSV, else PASV to the control connection's own address). A certificate this
/// computer trusts is taken; another one (a self-signed one) only when its SHA-256 fingerprint is the one remembered for the server, or
/// when the user chose to trust it, and then it is remembered. One control connection serves all the requests of an instance.
/// </summary>
public sealed class FtpsRemote : IRemote
{
    readonly string _host;
    readonly TcpClient _control;
    Stream _stream;
    readonly System.Net.Security.RemoteCertificateValidationCallback _check;
    string? _refused;

    /// <param name="knownCert">The fingerprint ("AB:CD:…") of the certificate trusted before, or "".</param>
    /// <param name="learn">Not null when a certificate this computer does not trust is to be trusted now: it is told the fingerprint.</param>
    public FtpsRemote(string host, int port, string user, string password, bool implicitTls, string knownCert, Action<string>? learn)
    {
        _host = host;
        _check = (_, cert, _, errors) =>
        {
            if (cert == null) { _refused = "The server showed no certificate."; return false; }
            if (errors == System.Net.Security.SslPolicyErrors.None) return true;
            string seen = Fingerprint(cert);
            if (knownCert.Length > 0)
            {
                if (string.Equals(knownCert, seen, StringComparison.OrdinalIgnoreCase)) return true;
                _refused = $"The server's certificate is not the one trusted before ({knownCert}, now {seen}). If the server was given a new certificate on purpose, edit the server and tick Trust its certificate.";
                return false;
            }
            if (learn != null) { learn(seen); knownCert = seen; return true; }
            _refused = $"The server's certificate is not one this computer trusts ({cert.Subject}, SHA-256 {seen}). If it is the server's own certificate (a self-signed one), edit the server and tick Trust its certificate.";
            return false;
        };
        _control = new TcpClient { ReceiveTimeout = 20000, SendTimeout = 20000 };
        try
        {
            if (!_control.ConnectAsync(host, port).Wait(20000)) throw new TimeoutException("no answer in 20 seconds");
            _stream = _control.GetStream();
            if (implicitTls) _stream = Secure(_stream);
            Expect(Reply(), 2);
            if (!implicitTls)
            {
                Expect(Command("AUTH TLS"), 2, "The server does not offer TLS (AUTH TLS)");
                _stream = Secure(_stream);
            }
            var (code, text) = Command("USER " + user);
            if (code == 331) (code, text) = Command("PASS " + password);
            if (code is < 200 or >= 300) throw new ServiceException("The server did not let this user in: " + text);
            Command("PBSZ 0");
            Expect(Command("PROT P"), 2, "The server does not protect the data connections (PROT P)");
            Expect(Command("TYPE I"), 2);
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or AggregateException or System.Security.Authentication.AuthenticationException)
        {
            _control.Dispose();
            throw new ServiceException(_refused ?? "Could not connect: " + (e is AggregateException a ? a.InnerException?.Message ?? a.Message : e.Message));
        }
    }

    /// <summary>A certificate's SHA-256 fingerprint as it is usually shown: pairs of hex digits with colons.</summary>
    public static string Fingerprint(System.Security.Cryptography.X509Certificates.X509Certificate cert) =>
        string.Join(":", System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData()).Select(b => b.ToString("X2")));

    Stream Secure(Stream inner)
    {
        var ssl = new System.Net.Security.SslStream(inner, false, _check);
        ssl.AuthenticateAsClient(_host);
        return ssl;
    }

    // ------------------------------------------------------------ the control connection

    (int Code, string Text) Reply()
    {
        // a reply of several lines starts "123-" and ends with a line "123 "
        string first = ReadLine() ?? throw new IOException("The server closed the connection.");
        var text = new StringBuilder(first);
        if (first.Length > 3 && first[3] == '-')
            for (string? line; (line = ReadLine()) != null;) { text.Append('\n').Append(line); if (line.Length >= 4 && line.StartsWith(first[..3], StringComparison.Ordinal) && line[3] == ' ') break; }
        return (first.Length >= 3 && int.TryParse(first[..3], out int code) ? code : 0, text.ToString());
    }

    string? ReadLine()
    {
        // byte by byte: a reader with a buffer would swallow what a TLS handshake that follows needs
        var bytes = new List<byte>();
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add((byte)b);
        }
    }

    (int Code, string Text) Command(string line)
    {
        if (line.Any(c => c is '\r' or '\n')) throw new ServiceException("A name has a line break in it.");
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
        return Reply();
    }

    static void Expect((int Code, string Text) reply, int kind, string? what = null)
    {
        if (reply.Code / 100 != kind) throw new ServiceException((what != null ? what + ": " : "") + reply.Text.Trim());
    }

    // ------------------------------------------------------------ data connections

    TcpClient Passive()
    {
        var address = ((IPEndPoint)_control.Client.RemoteEndPoint!).Address;
        int port;
        var (code, text) = Command("EPSV");
        if (code == 229 && Regex.Match(text, @"\(\|\|\|(\d+)\|\)") is { Success: true } e) port = int.Parse(e.Groups[1].Value);
        else
        {
            (code, text) = Command("PASV");
            var m = Regex.Match(text, @"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)");
            if (code != 227 || !m.Success) throw new ServiceException("The server offers no passive data connection: " + text.Trim());
            // the address in the answer is not used (behind a router it is often one that cannot be reached): the server's own is
            port = int.Parse(m.Groups[5].Value) * 256 + int.Parse(m.Groups[6].Value);
        }
        var data = new TcpClient { ReceiveTimeout = 30000, SendTimeout = 30000 };
        if (!data.ConnectAsync(address, port).Wait(20000)) { data.Dispose(); throw new ServiceException("The data connection could not be made."); }
        return data;
    }

    /// <summary>Runs a command that sends or takes data; null when the server refused it with a 5xx (a file or folder that is not there).</summary>
    T? Transfer<T>(string command, Func<Stream, T> work) where T : class
    {
        using var data = Passive();
        var (code, text) = Command(command);
        if (code / 100 == 5) return null;
        if (code / 100 != 1) throw new ServiceException(text.Trim());
        T result;
        using (var ssl = new System.Net.Security.SslStream(data.GetStream(), false, _check))
        {
            ssl.AuthenticateAsClient(_host);
            result = work(ssl);
            try { ssl.ShutdownAsync().Wait(5000); } catch (Exception x) when (x is IOException or AggregateException or ObjectDisposedException) { /* the server closed it first */ }
            // then the server's end of TLS: what it still sends (TLS 1.3's session tickets) is read, since Windows resets a connection closed
            // with bytes not read, and a real server (vsftpd) takes that reset as a failed upload ("426 Failure reading network stream")
            try
            {
                data.ReceiveTimeout = 5000;
                var rest = new byte[4096];
                while (ssl.Read(rest, 0, rest.Length) > 0) { }
            }
            catch (Exception x) when (x is IOException or ObjectDisposedException or InvalidOperationException or System.Security.Authentication.AuthenticationException) { /* gone already */ }
        }
        data.Close();
        Expect(Reply(), 2);
        return result;
    }

    static string P(string path) => string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries));

    public IReadOnlyList<RemoteEntry> List(string dir)
    {
        string p = P(dir);
        var lines = Transfer(p.Length == 0 ? "LIST" : "LIST " + p, s =>
        {
            var found = new List<string>();
            using var reader = new StreamReader(s, Encoding.UTF8, false, 4096, leaveOpen: true);
            for (string? line; (line = reader.ReadLine()) != null;) found.Add(line);
            return found;
        });
        return lines == null ? Array.Empty<RemoteEntry>() : FtpRemote.ParseListing(lines);
    }

    public byte[] Read(string path)
    {
        return Transfer("RETR " + P(path), s => { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); })
            ?? throw new ServiceException("It is not on the server: " + P(path));
    }

    public void Write(string path, byte[] data)
    {
        // the folders above are made first (one that exists is refused, which is fine)
        var parts = P(path).Split('/');
        for (int i = 1; i < parts.Length; i++) Command("MKD " + string.Join("/", parts.Take(i)));
        if (Transfer("STOR " + P(path), s => { s.Write(data, 0, data.Length); s.Flush(); return ""; }) == null) throw new ServiceException("The server did not take " + P(path));
    }

    public void Delete(string path)
    {
        string p = P(path);
        if (Command("DELE " + p).Code / 100 == 2) return;
        // a folder: what is in it first
        foreach (var e in List(p)) Delete(p + "/" + e.Name);
        Expect(Command("RMD " + p), 2);
    }

    public RemoteEntry? Stat(string path)
    {
        string p = P(path);
        string name = p.Split('/').Last();
        return List(p.Contains('/') ? p[..p.LastIndexOf('/')] : "").FirstOrDefault(e => e.Name == name);
    }

    public void Dispose()
    {
        try { Command("QUIT"); } catch (Exception e) when (e is IOException or ServiceException or ObjectDisposedException or SocketException) { /* gone already */ }
        _stream.Dispose();
        _control.Dispose();
    }
}

public sealed class SftpRemote : IRemote
{
    readonly Renci.SshNet.SftpClient _client;
    public SftpRemote(string host, int port, string user, string password, string keyFile, string knownKey, Action<string> learn, string passphrase = "")
    {
        var methods = new List<Renci.SshNet.AuthenticationMethod>();
        if (keyFile.Length > 0)
        {
            if (!File.Exists(keyFile)) throw new ServiceException("The key file is not there: " + keyFile);
            try { methods.Add(new Renci.SshNet.PrivateKeyAuthenticationMethod(user, new Renci.SshNet.PrivateKeyFile(keyFile, passphrase.Length > 0 ? passphrase : password.Length > 0 ? password : null))); }
            catch (Exception e) when (e is IOException or Renci.SshNet.Common.SshException or ArgumentException) { throw new ServiceException("The key file could not be read: " + e.Message); }
        }
        else methods.Add(new Renci.SshNet.PasswordAuthenticationMethod(user, password));
        _client = new Renci.SshNet.SftpClient(new Renci.SshNet.ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) });
        string? refused = null;
        _client.HostKeyReceived += (_, e) =>
        {
            string seen = e.FingerPrintSHA256;
            if (knownKey.Length == 0) learn(seen);                                   // the first time: trusted, and remembered
            else if (knownKey != seen) { e.CanTrust = false; refused = $"The host's key is not the one remembered ({knownKey}, now {seen}). If the server was changed on purpose, edit the server to forget the old key."; }
        };
        try { _client.Connect(); }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or TimeoutException or IOException)
        { throw new ServiceException(refused ?? "Could not connect: " + e.Message); }
    }

    public IReadOnlyList<RemoteEntry> List(string dir)
    {
        string path = dir.Length == 0 ? "." : "/" + dir.TrimStart('/');
        try { return _client.ListDirectory(path).Where(f => f.Name is not ("." or "..")).Select(f => new RemoteEntry(f.Name, f.IsDirectory, f.Length, f.LastWriteTimeUtc)).OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { return Array.Empty<RemoteEntry>(); }
    }
    static string P(string path) => "/" + path.TrimStart('/');
    public byte[] Read(string path) { using var ms = new MemoryStream(); _client.DownloadFile(P(path), ms); return ms.ToArray(); }
    public void Write(string path, byte[] data)
    {
        var parts = P(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++) { string dir = "/" + string.Join("/", parts.Take(i)); if (!_client.Exists(dir)) _client.CreateDirectory(dir); }
        using var ms = new MemoryStream(data);
        _client.UploadFile(ms, P(path), true);
    }
    public void Delete(string path)
    {
        string p = P(path);
        if (!_client.Exists(p)) return;
        if (_client.Get(p).IsDirectory) { foreach (var e in List(path)) Delete(path.TrimEnd('/') + "/" + e.Name); _client.DeleteDirectory(p); }
        else _client.DeleteFile(p);
    }
    public RemoteEntry? Stat(string path) { try { var f = _client.Get(P(path)); return new RemoteEntry(f.Name, f.IsDirectory, f.Length, f.LastWriteTimeUtc); } catch (Renci.SshNet.Common.SftpPathNotFoundException) { return null; } }
    public void Dispose() { try { _client.Disconnect(); } catch (Exception) { /* gone already */ } _client.Dispose(); }
}

/// <summary>What deploying does: where a file goes on a server, and upload, download, compare and sync.</summary>
public static class Deployment
{
    public const long MaxFile = 50L * 1024 * 1024;

    static readonly string[] AlwaysExcluded = { ".git", ".pymagic", "__pycache__", ".venv", "venv", ".conda", "node_modules", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".idea", ".vs" };

    /// <param name="trustCertificate">FTPS: a certificate this computer does not trust is taken this time, and its fingerprint remembered (the user chose to trust it).</param>
    public static IRemote Open(string root, DeployServer server, bool trustCertificate = false)
    {
        string password = DeployStore.PasswordOf(root, server.Id);
        return server.Type switch
        {
            "folder" => new FolderRemote(server.Root),
            "ftp" => new FtpRemote(server.Host, server.Port, server.User, password),
            "ftps" => new FtpsRemote(server.Host, server.Port, server.User, password, server.ImplicitTls, server.HostKey, trustCertificate ? key => RememberKey(root, server.Id, key) : null),
            _ => new SftpRemote(server.Host, server.Port, server.User, password, KeyPath(root, server.KeyFile), server.HostKey, key => RememberKey(root, server.Id, key), DeployStore.PassphraseOf(root, server.Id)),
        };
    }

    /// <summary>The key file as a full path: "~" is the home folder, a relative path is from the project.</summary>
    public static string KeyPath(string root, string keyFile)
    {
        keyFile = keyFile.Trim();
        if (keyFile.Length == 0) return "";
        if (keyFile == "~" || keyFile.StartsWith("~/") || keyFile.StartsWith("~\\")) keyFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), keyFile.Length > 2 ? keyFile[2..] : "");
        return System.IO.Path.GetFullPath(keyFile, root);
    }

    static void RememberKey(string root, string id, string key)
    {
        if (DeployStore.Find(root, id) is { } s && s.HostKey.Length == 0) DeployStore.Save(root, s with { HostKey = key }, null);
    }

    /// <summary>The path of a file of the project on the server: the longest mapping that contains it, under the server's root; null when no mapping does.</summary>
    public static string? RemoteOf(DeployServer server, string rel)
    {
        rel = rel.Replace('\\', '/').Trim('/');
        DeployMapping? best = null;
        foreach (var m in server.Mappings)
            if ((m.Local.Length == 0 || rel == m.Local || rel.StartsWith(m.Local + "/", StringComparison.Ordinal)) && (best == null || m.Local.Length > best.Local.Length)) best = m;
        if (best == null) return null;
        string below = best.Local.Length == 0 ? rel : rel[Math.Min(rel.Length, best.Local.Length)..].TrimStart('/');
        string root = server.Type == "folder" ? "" : server.Root.Trim('/');
        return string.Join("/", new[] { root, best.Remote, below }.Where(p => p.Length > 0));
    }

    /// <summary>The file of the project a path on the server is, or null when no mapping covers it.</summary>
    public static string? LocalOf(DeployServer server, string remote)
    {
        remote = remote.Replace('\\', '/').Trim('/');
        string root = server.Type == "folder" ? "" : server.Root.Trim('/');
        if (root.Length > 0) { if (remote != root && !remote.StartsWith(root + "/", StringComparison.Ordinal)) return null; remote = remote[root.Length..].TrimStart('/'); }
        DeployMapping? best = null;
        foreach (var m in server.Mappings)
            if ((m.Remote.Length == 0 || remote == m.Remote || remote.StartsWith(m.Remote + "/", StringComparison.Ordinal)) && (best == null || m.Remote.Length > best.Remote.Length)) best = m;
        if (best == null) return null;
        string below = best.Remote.Length == 0 ? remote : remote[Math.Min(remote.Length, best.Remote.Length)..].TrimStart('/');
        return string.Join("/", new[] { best.Local, below }.Where(p => p.Length > 0));
    }

    public static bool IsExcluded(DeployServer server, string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => AlwaysExcluded.Contains(p, StringComparer.OrdinalIgnoreCase)) || parts.LastOrDefault()?.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) == true) return true;
        foreach (var pattern in server.Excluded)
        {
            string glob = "^" + Regex.Escape(pattern.Trim('/')).Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
            if (parts.Select((_, i) => string.Join("/", parts.Take(i + 1))).Any(prefix => Regex.IsMatch(prefix, glob, RegexOptions.IgnoreCase)) || parts.Any(p => Regex.IsMatch(p, glob, RegexOptions.IgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>The files of the project to deploy for a list of paths: a file is itself, a folder is what is in it.</summary>
    public static IEnumerable<string> LocalFiles(string root, DeployServer server, IEnumerable<string> rels)
    {
        foreach (var rel0 in rels)
        {
            string rel = rel0.Replace('\\', '/').Trim('/');
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
            if (full != System.IO.Path.GetFullPath(root) && !full.StartsWith(System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(full)) { if (!IsExcluded(server, rel)) yield return rel; }
            else if (Directory.Exists(full))
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    string r = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (!IsExcluded(server, r)) yield return r;
                }
        }
    }

    public static List<TransferResult> Upload(string root, DeployServer server, IEnumerable<string> rels)
    {
        var results = new List<TransferResult>();
        using var remote = Open(root, server);
        foreach (var rel in LocalFiles(root, server, rels))
        {
            string? to = RemoteOf(server, rel);
            if (to == null) { results.Add(new TransferResult(rel, false, "No mapping covers this file.")); continue; }
            try
            {
                var info = new FileInfo(System.IO.Path.Combine(root, rel));
                if (info.Length > MaxFile) { results.Add(new TransferResult(rel, false, "The file is larger than 50 MB.")); continue; }
                remote.Write(to, File.ReadAllBytes(info.FullName));
                results.Add(new TransferResult(rel, true, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or WebException or Renci.SshNet.Common.SshException or ServiceException) { results.Add(new TransferResult(rel, false, e.Message)); }
        }
        return results;
    }

    /// <summary>
    /// Deletes a file or folder of the server by its path under the server's root (what the browser of the server shows). The root itself,
    /// an empty path and a path with ".." are refused. A folder goes with all that is in it.
    /// </summary>
    public static TransferResult DeleteRemote(string root, DeployServer server, string path)
    {
        string clean = path.Replace('\\', '/').Trim('/');
        if (clean.Length == 0) return new TransferResult(path, false, "The server's top folder is not deleted from here.");
        if (clean.Split('/').Contains("..") || clean.Any(char.IsControl)) return new TransferResult(path, false, "That path is not usable.");
        string full = server.Type == "folder" ? clean : string.Join("/", new[] { server.Root.Trim('/').Replace('\\', '/'), clean }.Where(x => x.Length > 0));
        try
        {
            using var remote = Open(root, server);
            if (remote.Stat(full) == null) return new TransferResult(clean, false, "It is not on the server.");
            remote.Delete(full);
            return new TransferResult(clean, true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WebException or Renci.SshNet.Common.SshException or ServiceException) { return new TransferResult(clean, false, e.Message); }
    }

    /// <summary>
    /// For a server that was set to delete what is deleted in the project: takes the server's copies of these deleted files off. Only files
    /// (never a folder of the server), only those a mapping covers and that are not excluded; a file that is not there is left alone.
    /// </summary>
    public static List<TransferResult> DeleteForLocal(string root, DeployServer server, IEnumerable<string> rels)
    {
        var results = new List<TransferResult>();
        if (!server.DeleteOnDelete) return results;
        IRemote? remote = null;
        try
        {
            foreach (var rel0 in rels)
            {
                string rel = rel0.Replace('\\', '/').Trim('/');
                string? to = RemoteOf(server, rel);
                if (rel.Length == 0 || to == null || IsExcluded(server, rel)) continue;
                try
                {
                    remote ??= Open(root, server);
                    if (remote.Stat(to) is not { IsDir: false }) continue;
                    remote.Delete(to);
                    results.Add(new TransferResult(rel, true, null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or WebException or Renci.SshNet.Common.SshException or ServiceException) { results.Add(new TransferResult(rel, false, e.Message)); }
            }
        }
        finally { remote?.Dispose(); }
        return results;
    }

    /// <summary>Brings the files named (paths of the project) from the server; a name that is a folder takes all that is under it there.</summary>
    public static List<TransferResult> Download(string root, DeployServer server, IEnumerable<string> rels)
    {
        var results = new List<TransferResult>();
        using var remote = Open(root, server);
        foreach (var rel0 in rels)
        {
            string rel = rel0.Replace('\\', '/').Trim('/');
            string? from = RemoteOf(server, rel);
            if (from == null) { results.Add(new TransferResult(rel, false, "No mapping covers this path.")); continue; }
            try
            {
                var stat = remote.Stat(from);
                if (stat == null) { results.Add(new TransferResult(rel, false, "It is not on the server.")); continue; }
                var files = stat.IsDir ? Walk(remote, from).Select(f => LocalOf(server, f)).Where(x => x != null).Select(x => x!).ToList() : new List<string> { rel };
                foreach (var file in files)
                {
                    if (IsExcluded(server, file)) continue;
                    string local = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, file));
                    if (!local.StartsWith(System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { results.Add(new TransferResult(file, false, "Outside the project.")); continue; }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
                    File.WriteAllBytes(local, remote.Read(RemoteOf(server, file)!));
                    results.Add(new TransferResult(file, true, null));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or WebException or Renci.SshNet.Common.SshException or ServiceException) { results.Add(new TransferResult(rel, false, e.Message)); }
        }
        return results;
    }

    /// <summary>Every file under a folder of the server (paths on the server), up to a limit.</summary>
    public static List<string> Walk(IRemote remote, string dir, int limit = 5000)
    {
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0 && found.Count < limit)
        {
            string d = stack.Pop();
            foreach (var e in remote.List(d))
            {
                string path = d.Length == 0 ? e.Name : d.TrimEnd('/') + "/" + e.Name;
                if (e.IsDir) stack.Push(path); else found.Add(path);
            }
        }
        return found;
    }

    static bool LooksLikeText(byte[] bytes) => bytes.Length <= 1024 * 1024 && Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) < 0;

    /// <summary>One file as it is here and as it is on the server, for the comparison window; the text of a side that is a text file.</summary>
    public static (bool LocalExists, bool RemoteExists, string? Local, string? Remote, bool Same) Compare(string root, DeployServer server, string rel)
    {
        string? to = RemoteOf(server, rel);
        string local = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
        byte[]? l = File.Exists(local) ? File.ReadAllBytes(local) : null;
        byte[]? r = null;
        if (to != null)
        {
            using var remote = Open(root, server);
            if (remote.Stat(to) is { IsDir: false } s && s.Size <= MaxFile) r = remote.Read(to);
        }
        return (l != null, r != null, l != null && LooksLikeText(l) ? Encoding.UTF8.GetString(l) : null, r != null && LooksLikeText(r) ? Encoding.UTF8.GetString(r) : null, l != null && r != null && l.AsSpan().SequenceEqual(r));
    }

    /// <summary>
    /// What differs between a folder of the project and its place on the server: size first, and the newer side when the sizes differ. Files
    /// whose sizes are equal are taken as the same (the server's own times are not the files').
    /// </summary>
    public static List<SyncItem> SyncPlan(string root, DeployServer server, string dirRel)
    {
        dirRel = dirRel.Replace('\\', '/').Trim('/');
        string? remoteDir = RemoteOf(server, dirRel);
        if (remoteDir == null) throw new ServiceException("No mapping covers this folder.");
        var local = LocalFiles(root, server, new[] { dirRel }).ToDictionary(f => f, f => new FileInfo(System.IO.Path.Combine(root, f)), StringComparer.Ordinal);
        using var remote = Open(root, server);
        var theirs = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(remoteDir);
        while (stack.Count > 0 && theirs.Count < 5000)
        {
            string d = stack.Pop();
            foreach (var e in remote.List(d))
            {
                string p = d.Length == 0 ? e.Name : d.TrimEnd('/') + "/" + e.Name;
                if (e.IsDir) { stack.Push(p); continue; }
                if (LocalOf(server, p) is { } rel && !IsExcluded(server, rel)) theirs[rel] = e;
            }
        }
        var items = new List<SyncItem>();
        foreach (var rel in local.Keys.Union(theirs.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            local.TryGetValue(rel, out var lf);
            theirs.TryGetValue(rel, out var rf);
            string status = lf == null ? "remote-only" : rf == null ? "local-only" : lf.Length == rf.Size ? "same" : lf.LastWriteTimeUtc >= rf.Modified ? "local-newer" : "remote-newer";
            items.Add(new SyncItem(rel, status, lf?.Length ?? 0, rf?.Size ?? 0, lf?.LastWriteTimeUtc, rf?.Modified));
        }
        return items;
    }
}
