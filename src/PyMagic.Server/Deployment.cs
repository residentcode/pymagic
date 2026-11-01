using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>A folder of the project and the folder of a server it is deployed to (both relative: "" is the project root, and the server's root).</summary>
public sealed record DeployMapping(string Local, string Remote);

/// <summary>
/// A server the project is deployed to (item 244): a folder (a share, a USB stick), an FTP server, or an SFTP server. <c>Root</c> is the
/// folder on it everything below is relative to (for a folder server, the folder itself).
/// </summary>
public sealed record DeployServer(string Id, string Name, string Type, string Host, int Port, string User, string Root, string KeyFile, bool UploadOnSave,
    List<DeployMapping> Mappings, List<string> Excluded, string HostKey);

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

    static Dictionary<string, string> Passwords(string root)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Private(root))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static string PasswordOf(string root, string id) => Passwords(root).GetValueOrDefault(id, "");

    public static DeployServer? Find(string root, string id) => Load(root).FirstOrDefault(s => s.Id == id);

    /// <summary>Makes a server from what the form sent, checked. <exception cref="ServiceException">A field is not usable.</exception></summary>
    public static DeployServer Parse(System.Text.Json.Nodes.JsonObject body, string? existingId = null)
    {
        string type = (body["type"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (type is not ("folder" or "ftp" or "sftp")) throw new ServiceException("A server is a folder, an FTP server or an SFTP server.");
        string host = (body["host"]?.GetValue<string>() ?? "").Trim();
        string root = (body["root"]?.GetValue<string>() ?? "").Trim().Replace('\\', '/');
        if (type == "folder")
        {
            if (root.Length == 0) throw new ServiceException("Name the folder.");
            host = "";
        }
        else if (host.Length == 0) throw new ServiceException("Name the host.");
        int port = body["port"] is { } p && int.TryParse(p.ToString(), out var n) ? n : (type == "ftp" ? 21 : 22);
        if (port is < 1 or > 65535) throw new ServiceException("The port is a number from 1 to 65535.");
        string user = (body["user"]?.GetValue<string>() ?? "").Trim();
        foreach (var field in new[] { host, user, root }) if (field.Any(char.IsControl)) throw new ServiceException("A name has a control character in it.");
        if (type != "folder" && user.Length == 0) user = type == "ftp" ? "anonymous" : "";
        string name = (body["name"]?.GetValue<string>() ?? "").Trim();
        if (name.Length == 0) name = type == "folder" ? root : $"{user}@{host}";
        string id = existingId ?? new string((type + "-" + (type == "folder" ? root : host + "-" + port + "-" + user)).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        var mappings = (body["mappings"] as System.Text.Json.Nodes.JsonArray)?.Select(m => new DeployMapping(Clean(m?["local"]?.GetValue<string>()), Clean(m?["remote"]?.GetValue<string>()))).ToList();
        if (mappings == null || mappings.Count == 0) mappings = new() { new DeployMapping("", "") };
        if (mappings.Any(m => m.Local.Split('/').Contains("..") || m.Remote.Split('/').Contains(".."))) throw new ServiceException("A mapping cannot climb out of its folder.");
        var excluded = (body["excluded"] as System.Text.Json.Nodes.JsonArray)?.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToList() ?? new();
        return new DeployServer(id, name, type, host, port, user, root, (body["keyFile"]?.GetValue<string>() ?? "").Trim(), body["uploadOnSave"]?.GetValue<bool>() ?? false, mappings, excluded, body["hostKey"]?.GetValue<string>() ?? "");
    }

    static string Clean(string? path) => (path ?? "").Trim().Replace('\\', '/').Trim('/');

    public static void Save(string root, DeployServer server, string? password)
    {
        var list = Load(root).Where(s => s.Id != server.Id).ToList();
        list.Add(server);
        Directory.CreateDirectory(System.IO.Path.Combine(root, ".pymagic"));
        System.IO.File.WriteAllText(File_(root), JsonSerializer.Serialize(list, Options));
        // a password that was not sent stays as it was
        if (password != null)
        {
            var passwords = Passwords(root);
            if (password.Length > 0) passwords[server.Id] = password; else passwords.Remove(server.Id);
            System.IO.File.WriteAllText(Private(root), JsonSerializer.Serialize(passwords));
        }
        PymagicFolder.IgnoreInGit(System.IO.Path.Combine(root, ".pymagic"), "deployment.private.json");
    }

    public static void Remove(string root, string id)
    {
        if (!System.IO.File.Exists(File_(root))) return;
        System.IO.File.WriteAllText(File_(root), JsonSerializer.Serialize(Load(root).Where(s => s.Id != id).ToList(), Options));
        var passwords = Passwords(root);
        if (passwords.Remove(id)) System.IO.File.WriteAllText(Private(root), JsonSerializer.Serialize(passwords));
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
    public FtpRemote(string host, int port, string user, string password) { _host = host; _port = port; _credential = new NetworkCredential(user, password); }

    FtpWebRequest Request(string path, string method)
    {
        var uri = new UriBuilder("ftp", _host, _port) { Path = "/" + string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) };
        var r = (FtpWebRequest)WebRequest.Create(uri.Uri);
        r.Method = method; r.Credentials = _credential; r.UsePassive = true; r.UseBinary = true; r.KeepAlive = false; r.Timeout = 20000; r.ReadWriteTimeout = 30000;
        return r;
    }

    static readonly Regex Unix = new(@"^(?<kind>[-dlDL])[\w-]{9}[+@.]?\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\w{3}\s+\d{1,2}\s+(?:\d{4}|\d{1,2}:\d{2}))\s+(?<name>.+)$", RegexOptions.Compiled);
    static readonly Regex Dos = new(@"^(?<date>\d{2}-\d{2}-\d{2,4}\s+\d{1,2}:\d{2}[AP]M)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$", RegexOptions.Compiled);

    public IReadOnlyList<RemoteEntry> List(string dir)
    {
        var entries = new List<RemoteEntry>();
        try
        {
            using var res = (FtpWebResponse)Request(dir, WebRequestMethods.Ftp.ListDirectoryDetails).GetResponse();
            using var reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (Unix.Match(line) is { Success: true } u && u.Groups["name"].Value is var n && n is not ("." or ".."))
                    entries.Add(new RemoteEntry(n.Contains(" -> ") ? n[..n.IndexOf(" -> ", StringComparison.Ordinal)] : n, u.Groups["kind"].Value is "d" or "D", long.Parse(u.Groups["size"].Value), ParseUnixDate(u.Groups["date"].Value)));
                else if (Dos.Match(line) is { Success: true } d)
                    entries.Add(new RemoteEntry(d.Groups["name"].Value, d.Groups["size"].Value == "<DIR>", d.Groups["size"].Value == "<DIR>" ? 0 : long.Parse(d.Groups["size"].Value),
                        DateTime.TryParseExact(d.Groups["date"].Value, new[] { "MM-dd-yy hh:mmtt", "MM-dd-yyyy hh:mmtt", "MM-dd-yy h:mmtt" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTime.MinValue));
            }
        }
        catch (WebException e) when (e.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable }) { return Array.Empty<RemoteEntry>(); }
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
        using var res = (FtpWebResponse)Request(path, WebRequestMethods.Ftp.DownloadFile).GetResponse();
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
            try { using var _ = (FtpWebResponse)Request(string.Join("/", parts.Take(i)), WebRequestMethods.Ftp.MakeDirectory).GetResponse(); }
            catch (WebException) { /* it exists */ }
        }
        var req = Request(path, WebRequestMethods.Ftp.UploadFile);
        req.ContentLength = data.Length;
        using (var stream = req.GetRequestStream()) stream.Write(data, 0, data.Length);
        using var res = (FtpWebResponse)req.GetResponse();
    }

    public void Delete(string path)
    {
        try { using var _ = (FtpWebResponse)Request(path, WebRequestMethods.Ftp.DeleteFile).GetResponse(); }
        catch (WebException) { using var _ = (FtpWebResponse)Request(path, WebRequestMethods.Ftp.RemoveDirectory).GetResponse(); }
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

public sealed class SftpRemote : IRemote
{
    readonly Renci.SshNet.SftpClient _client;
    public SftpRemote(string host, int port, string user, string password, string keyFile, string knownKey, Action<string> learn)
    {
        var methods = new List<Renci.SshNet.AuthenticationMethod>();
        if (keyFile.Length > 0)
        {
            try { methods.Add(new Renci.SshNet.PrivateKeyAuthenticationMethod(user, new Renci.SshNet.PrivateKeyFile(keyFile, password.Length > 0 ? password : null))); }
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

    static readonly string[] AlwaysExcluded = { ".git", ".pymagic", "__pycache__", ".venv", "venv", "node_modules", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".idea", ".vs" };

    public static IRemote Open(string root, DeployServer server)
    {
        string password = DeployStore.PasswordOf(root, server.Id);
        return server.Type switch
        {
            "folder" => new FolderRemote(server.Root),
            "ftp" => new FtpRemote(server.Host, server.Port, server.User, password),
            _ => new SftpRemote(server.Host, server.Port, server.User, password, server.KeyFile, server.HostKey, key => RememberKey(root, server.Id, key)),
        };
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
