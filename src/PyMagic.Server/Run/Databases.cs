using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>An SQLite database of the project: found in its folders, added by hand, or named by the Django settings.</summary>
/// <param name="Exists">False for the database a Django project's settings name before <c>manage.py migrate</c> has made it.</param>
/// <param name="Django">The settings module whose <c>DATABASES</c> names it.</param>
/// <param name="Manage">The manage.py of that Django project, by its path from the root (`manage.py migrate` makes the database).</param>
/// <param name="Server">"postgres" or "mysql" for a database server (its Path is <c>server:id</c>); null for an SQLite file.</param>
/// <param name="Detail">What the panel shows beside a server: <c>host:port/database</c>.</param>
public sealed record DataSource(string Path, string Name, long Size, bool Added, bool Exists = true, string? Django = null, string? Manage = null, string? Server = null, string? Detail = null);

/// <summary>
/// The Database panel's engine side: the SQLite files of a project, and the requests on them (schema, a page of rows,
/// the data editor's changes, a statement of the console), answered by pm_db.py with the project's Python and its
/// standard sqlite3 module, so nothing has to be installed.
/// </summary>
public static class Databases
{
    static readonly string[] Extensions = { ".sqlite", ".sqlite3", ".db", ".db3" };
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "__pycache__", "node_modules", ".venv", "venv", "env", ".tox", ".mypy_cache", "site-packages", "dist", "build", ".pymagic",
    };

    static string AddedFile(string root) => System.IO.Path.Combine(root, ".pymagic", "datasources.json");

    /// <summary>Whether a file starts as SQLite 3 files do.</summary>
    public static bool IsSqlite(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            var head = new byte[16];
            return f.Read(head, 0, 16) == 16 && Encoding.ASCII.GetString(head) == "SQLite format 3\0";
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The SQLite files of the project (by their extension and their first bytes) and those added by hand, by their path from the root.</summary>
    public static List<DataSource> Find(string root)
    {
        var found = new Dictionary<string, DataSource>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && IsSqlite(f)) Add(f, false);
                if (depth < 6)
                    foreach (var d in Directory.EnumerateDirectories(dir))
                        if (!Skipped.Contains(System.IO.Path.GetFileName(d)) && !ProjectFolders.IsExcluded(d)) stack.Push((d, depth + 1));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var rel in AddedPaths(root))
        {
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
            if (File.Exists(full) && IsSqlite(full)) Add(full, true);
        }
        // the SQLite database of a Django project's settings, also before migrate has made it
        foreach (var (full, settings, manage) in DjangoDatabases(root))
        {
            string rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
            if (found.TryGetValue(rel, out var known)) found[rel] = known with { Django = settings, Manage = manage };
            else if (!File.Exists(full)) found[rel] = new DataSource(rel, System.IO.Path.GetFileName(full), 0, false, Exists: false, Django: settings, Manage: manage);
        }
        var all = found.Values.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
        all.AddRange(DbServers.Load(root).Select(v => new DataSource(DbServers.PathOf(v.Id), v.Name, 0, true, Server: v.Kind, Detail: $"{v.Host}:{v.Port}/{v.Database}")));
        return all;

        void Add(string full, bool added)
        {
            string rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
            found[rel] = new DataSource(rel, System.IO.Path.GetFileName(full), new FileInfo(full).Length, added || found.GetValueOrDefault(rel)?.Added == true);
        }
    }

    // "ENGINE": "django.db.backends.sqlite3" ... "NAME": <expression>, in one entry of DATABASES (either order)
    static readonly System.Text.RegularExpressions.Regex SqliteEntry = new(
        @"\{[^{}]*?['""]ENGINE['""]\s*:\s*['""]django\.db\.backends\.sqlite3['""][^{}]*\}", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex NameValue = new(
        @"['""]NAME['""]\s*:\s*(?<expr>[^,}\n(]*(?:\([^)]*\))?[^,}\n]*)",System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex StringLiteral = new(@"['""](?<s>[^'""]+)['""]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The SQLite files the settings modules of the project's Django name: <c>BASE_DIR / "db.sqlite3"</c>,
    /// <c>os.path.join(BASE_DIR, "db.sqlite3")</c> or a plain name (BASE_DIR is the folder of manage.py, where Django runs);
    /// inside the project only. With the settings module that names each.
    /// </summary>
    public static List<(string Path, string Settings, string Manage)> DjangoDatabases(string root)
    {
        var result = new List<(string, string, string)>();
        var django = DjangoProject.Scan(root);
        if (!django.Found || django.Manage == null) return result;
        string baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, django.Manage)))!;
        foreach (var module in django.SettingsChoices ?? Array.Empty<string>())
        {
            string stem = System.IO.Path.Combine(baseDir, module.Replace('.', System.IO.Path.DirectorySeparatorChar));
            string file = File.Exists(stem + ".py") ? stem + ".py" : System.IO.Path.Combine(stem, "__init__.py");
            string text;
            try { text = File.ReadAllText(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (System.Text.RegularExpressions.Match entry in SqliteEntry.Matches(text))
            {
                if (NameValue.Match(entry.Value) is not { Success: true } name) continue;
                var literals = StringLiteral.Matches(name.Groups["expr"].Value);
                if (literals.Count == 0) continue;
                // BASE_DIR / "data" / "db.sqlite3", os.path.join(BASE_DIR, "data", "db.sqlite3"): the parts after BASE_DIR, in order
                string expr = name.Groups["expr"].Value;
                string last = expr.Contains("BASE_DIR") || expr.Contains("join(")
                    ? string.Join("/", literals.Select(l => l.Groups["s"].Value)) : literals[^1].Groups["s"].Value;
                if (last == ":memory:" || last.StartsWith("file:", StringComparison.Ordinal)) continue;
                string full;
                try { full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(last) ? last : System.IO.Path.Combine(baseDir, last)); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException) { continue; }
                if (!full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.Any(r => string.Equals(r.Item1, full, StringComparison.OrdinalIgnoreCase))) result.Add((full, module, django.Manage.Replace('\\', '/')));
            }
        }
        return result;
    }

    static List<string> AddedPaths(string root)
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AddedFile(root))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Adds a file of the project to the data sources (a new SQLite file is made when it does not exist yet).</summary>
    /// <exception cref="ServiceException">The file is not an SQLite database.</exception>
    public static void AddSource(string root, string full, string python)
    {
        if (File.Exists(full) && !IsSqlite(full)) throw new ServiceException($"{System.IO.Path.GetFileName(full)} is not an SQLite database.");
        if (!File.Exists(full))
        {
            var made = Ask(python, new JsonObject { ["op"] = "create", ["path"] = full });
            if (made["error"] is { } error) throw new ServiceException(error.GetValue<string>());
        }
        string rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
        var list = AddedPaths(root);
        if (!list.Contains(rel, StringComparer.OrdinalIgnoreCase)) list.Add(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AddedFile(root))!);
        File.WriteAllText(AddedFile(root), JsonSerializer.Serialize(list));
    }

    /// <summary>Takes a file added by hand out of the list (the file itself stays).</summary>
    public static void RemoveSource(string root, string rel)
    {
        var list = AddedPaths(root).Where(p => !string.Equals(p, rel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (File.Exists(AddedFile(root))) File.WriteAllText(AddedFile(root), JsonSerializer.Serialize(list));
    }

    /// <summary>A request for pm_db.py; its answer, which has "error" when SQLite refused.</summary>
    /// <exception cref="ServiceException">Python could not be started or did not answer.</exception>
    public static JsonNode Ask(string python, JsonObject request, int timeoutMs = 60000, string helperName = "pm_db.py")
    {
        string helper = RunSession.ExtractHelper(helperName);
        var psi = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-I");   // the helper's own folder only: no file of the project is imported in its place
        psi.ArgumentList.Add(helper);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.StandardInputEncoding = new UTF8Encoding(false); psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false);
        Process process;
        try { process = Process.Start(psi) ?? throw new ServiceException($"'{python}' could not be started."); }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException($"The Python interpreter '{python}' could not be started."); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(request.ToJsonString());
            process.StandardInput.Close();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new ServiceException("The database did not answer in time (a long statement, or a database locked by another program).");
            }
            try { return JsonNode.Parse(output.Result) ?? throw new JsonException(); }
            catch (JsonException) { throw new ServiceException("The database helper failed: " + error.Result.Trim()); }
        }
    }
}
