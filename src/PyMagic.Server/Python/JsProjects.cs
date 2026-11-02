using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// The choices of New Project's other kinds, as PyCharm's dialog has them: the Node runtime; the generator's command (shown with its default, such
/// as <c>npx --package express-generator express</c>, and changed as wanted) and its version; then the kind's options: the version of HTML5
/// Boilerplate, Bootstrap or React Native, Vite's template, Next.js's switches (TypeScript, ESLint, Tailwind CSS, the App Router, a src folder),
/// Express's view and stylesheet engines.
/// </summary>
public sealed record JsProjectOptions(string? Version = null, string? Template = null, bool TypeScript = true, bool Eslint = true, bool Tailwind = true,
    bool AppRouter = true, bool SrcDir = false, string? View = null, string? Node = null, string? Command = null, string? GeneratorVersion = null, string? Css = null);

/// <summary>A kind's generator: the npm package it is, and the command New Project shows for it (npx, as PyCharm runs it).</summary>
public sealed record JsGenerator(string Package, string Command);

/// <summary>
/// New Project's kinds that are not Python, as PyCharm's New Project has them under Other: HTML5 Boilerplate, Bootstrap, Vite, React Native, Next.js,
/// Node.js and Express. Each is made the way its own makers make it: Vite, Next.js, React Native and Express by their generators (create-vite,
/// create-next-app, the React Native CLI, express-generator, run with npx), HTML5 Boilerplate and Bootstrap from the package of the chosen version
/// (what PyCharm downloads), Node.js as package.json and index.js. They need Node.js; everything they fetch comes from the npm registry.
/// </summary>
public static class JsProjects
{
    public static readonly string[] Kinds = { "html5boilerplate", "bootstrap", "vite", "reactnative", "nextjs", "nodejs", "express" };

    public static bool IsJs(string kind) => Kinds.Contains(kind);

    /// <summary>create-vite's templates.</summary>
    public static readonly string[] ViteTemplates =
    {
        "vanilla", "vanilla-ts", "vue", "vue-ts", "react", "react-ts", "react-swc", "react-swc-ts", "preact", "preact-ts", "lit", "lit-ts",
        "svelte", "svelte-ts", "solid", "solid-ts", "qwik", "qwik-ts",
    };

    /// <summary>express-generator's view engines ("none": an API without views).</summary>
    public static readonly string[] ExpressViews = { "dust", "ejs", "hbs", "hjs", "pug", "twig", "vash", "none" };

    /// <summary>express-generator's stylesheet engines ("css": plain CSS).</summary>
    public static readonly string[] ExpressCss = { "css", "less", "stylus", "compass", "sass" };

    /// <summary>The generator of a kind, or null for a kind made without one (HTML5 Boilerplate, Bootstrap and Node.js).</summary>
    public static JsGenerator? Generator(string kind) => kind switch
    {
        "express" => new("express-generator", "npx --package express-generator express"),
        "vite" => new("create-vite", "npx create-vite"),
        "nextjs" => new("create-next-app", "npx create-next-app"),
        "reactnative" => new("@react-native-community/cli", "npx @react-native-community/cli"),
        _ => null,
    };

    /// <summary>The npm package whose versions a kind's options offer (the framework's own, not its generator's), or null.</summary>
    public static string? VersionPackage(string kind) => kind switch
    {
        "html5boilerplate" => "html5-boilerplate",
        "bootstrap" => "bootstrap",
        "reactnative" => "react-native",
        _ => null,
    };

    static readonly Regex Package = new(@"^(@[a-z0-9~-][a-z0-9._~-]*/)?[a-z0-9~-][a-z0-9._~-]*$");

    /// <summary>Whether a name is one npm takes for a package (express-generator, @react-native-community/cli).</summary>
    public static bool IsPackageName(string name) => Package.IsMatch(name);

    /// <summary>
    /// The arguments of npx for a generator's command as written in New Project: it must start with npx; the package it names (after --package, or
    /// the first word that is not an option) gets the version chosen when it has none; --yes is added so that npx does not ask.
    /// </summary>
    public static List<string> CommandArgs(string command, string package, string? version)
    {
        var words = Regex.Matches(command ?? "", "\"([^\"]*)\"|'([^']*)'|(\\S+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToList();
        if (words.Count < 2 || !string.Equals(words[0], "npx", StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("The generator's command starts with npx, as in: npx " + package);
        words.RemoveAt(0);
        int at = words.FindIndex(w => w is "--package" or "-p");
        at = at >= 0 && at + 1 < words.Count ? at + 1 : words.FindIndex(w => !w.StartsWith('-'));
        if (at >= 0 && version is { } v && Version.IsMatch(v) && words[at].LastIndexOf('@') <= 0) words[at] += "@" + v;
        if (!words.Contains("--yes") && !words.Contains("-y")) words.Insert(0, "--yes");
        return words;
    }

    static readonly Regex Version = new(@"^\d+\.\d+\.\d+$");
    static readonly Regex NpmName = new(@"^[a-z0-9][a-z0-9._-]*$");

    /// <summary>The name of the project as npm wants a package name: lower case, no spaces.</summary>
    public static string PackageName(string name)
    {
        string s = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        return s.Length == 0 ? "project" : s;
    }

    /// <summary>The name React Native wants for an app: letters and digits, starting with a letter (MySite for "my site").</summary>
    public static string AppName(string name)
    {
        string s = string.Concat(Regex.Split(name, @"[^A-Za-z0-9]+").Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return s.Length == 0 || !char.IsLetter(s[0]) ? "App" + s : s;
    }

    /// <summary>The stable versions among those npm lists, newest first.</summary>
    public static List<string> StableVersions(IEnumerable<string> versions) =>
        versions.Where(v => Version.IsMatch(v)).Select(v => (v, key: new System.Version(v))).OrderByDescending(x => x.key).Select(x => x.v).ToList();

    /// <summary>
    /// The arguments of npx that make a project of a kind: the generator's command (New Project's, or the default) with its version, then what the
    /// options ask of it; run in the project's folder (React Native: in the folder above it). Null for a kind made without a generator.
    /// </summary>
    public static List<string>? GeneratorArgs(string kind, string folder, JsProjectOptions o, bool install)
    {
        if (Generator(kind) is not { } generator) return null;
        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var args = CommandArgs(string.IsNullOrWhiteSpace(o.Command) ? generator.Command : o.Command, generator.Package, o.GeneratorVersion);
        switch (kind)
        {
            case "vite":
                string template = o.Template is { } t && ViteTemplates.Contains(t) ? t : "vanilla";
                args.AddRange(new[] { ".", "--template", template, "--no-interactive" });
                break;
            case "nextjs":
                args.AddRange(new[] { ".", o.TypeScript ? "--ts" : "--js", o.Eslint ? "--eslint" : "--no-eslint", o.Tailwind ? "--tailwind" : "--no-tailwind",
                    o.AppRouter ? "--app" : "--no-app", o.SrcDir ? "--src-dir" : "--no-src-dir", "--import-alias", "@/*", "--use-npm", "--disable-git", "--yes" });
                if (!install) args.Add("--skip-install");
                break;
            case "reactnative":
                args.AddRange(new[] { "init", AppName(name), "--directory", folder, "--skip-git-init", "--pm", "npm" });
                if (o.Version is { } rv && Version.IsMatch(rv)) { args.Add("--version"); args.Add(rv); }
                if (!install) args.Add("--skip-install");
                break;
            case "express":
                string view = o.View is { } w && ExpressViews.Contains(w) ? w : "pug";
                args.Add(view == "none" ? "--no-view" : "--view=" + view);
                if (o.Css is { } css && ExpressCss.Contains(css) && css != "css") args.Add("--css=" + css);
                args.Add(".");
                break;
        }
        return args;
    }

    /// <summary>Whether a kind's generator leaves the packages to `npm install` (Vite and Express do; Next.js and React Native install them themselves).</summary>
    public static bool InstallsAfter(string kind) => kind is "vite" or "express";

    /// <summary>The .gitignore of a project of these kinds, when its generator wrote none.</summary>
    public const string GitIgnore = "node_modules/\ndist/\nbuild/\n.next/\nout/\n.env*.local\nnpm-debug.log*\n.pymagic/\n";

    /// <summary>The files of a Node.js project: package.json and index.js.</summary>
    public static Dictionary<string, string> NodeFiles(string name)
    {
        var package = new JsonObject
        {
            ["name"] = PackageName(name), ["version"] = "1.0.0", ["description"] = "", ["main"] = "index.js",
            ["scripts"] = new JsonObject { ["start"] = "node index.js", ["test"] = "node --test" },
            ["license"] = "ISC",
        };
        return new(StringComparer.Ordinal)
        {
            ["package.json"] = package.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n",
            ["index.js"] = $"console.log('Hello from {name.Replace("'", "")}!');\n",
        };
    }

    /// <summary>Bootstrap's own starter page, for the css and js folders of the version unpacked beside it.</summary>
    public static string BootstrapPage(string name, bool bundle) =>
        "<!doctype html>\n<html lang=\"en\">\n  <head>\n    <meta charset=\"utf-8\">\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
        $"    <title>{System.Net.WebUtility.HtmlEncode(name)}</title>\n    <link href=\"css/bootstrap.min.css\" rel=\"stylesheet\">\n  </head>\n  <body>\n" +
        "    <div class=\"container py-4\">\n      <h1>Hello, world!</h1>\n    </div>\n" +
        (bundle ? "    <script src=\"js/bootstrap.bundle.min.js\"></script>\n" : "    <script src=\"https://code.jquery.com/jquery-1.12.4.min.js\"></script>\n    <script src=\"js/bootstrap.min.js\"></script>\n") +
        "  </body>\n</html>\n";

    // ------------------------------------------------------------ making one

    /// <summary>Makes a project of one of these kinds in the empty folder New Project made.</summary>
    public static NewProjectResult Create(NewProjectRequest r, string folder)
    {
        var o = r.Js ?? new JsProjectOptions();
        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var notes = new List<string>();
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        bool install = r.InstallPackages;
        switch (r.Kind)
        {
            case "nodejs":
                foreach (var (rel, text) in NodeFiles(name)) written[rel] = text;
                break;
            case "html5boilerplate":
            {
                var npm = Npm.Find(o.Node);
                string version = Unpack(npm, "html5-boilerplate", o.Version, folder, "package/dist/");
                notes.Add($"HTML5 Boilerplate {version}.");
                break;
            }
            case "bootstrap":
            {
                var npm = Npm.Find(o.Node);
                string version = Unpack(npm, "bootstrap", o.Version, folder, "package/dist/");
                written["index.html"] = BootstrapPage(name, File.Exists(Path.Combine(folder, "js", "bootstrap.bundle.min.js")));
                notes.Add($"Bootstrap {version}: its css and js folders, and its starter page as index.html.");
                break;
            }
            case "nextjs":
                if (!NpmName.IsMatch(name)) throw new ServiceException($"Next.js wants the folder's name in lower case, without spaces (such as {PackageName(name)}).");
                goto default;
            default:
            {
                var npm = Npm.Find(o.Node);
                var args = GeneratorArgs(r.Kind, folder, o, install) ?? throw new ServiceException($"'{r.Kind}' is not a kind of project.");
                string where = folder;
                if (r.Kind == "reactnative")
                {
                    // the React Native CLI makes the folder itself
                    Directory.Delete(folder);
                    where = Path.GetDirectoryName(folder.TrimEnd('\\', '/'))!;
                }
                npm.Npx(where, args, TimeSpan.FromMinutes(r.Kind == "reactnative" ? 20 : 10));
                if (install && InstallsAfter(r.Kind))
                {
                    try { npm.Run(folder, new[] { "install" }, TimeSpan.FromMinutes(10)); notes.Add("npm install fetched its packages (node_modules)."); }
                    catch (ServiceException e) { notes.Add("Its packages are not installed (" + e.Message + "): run npm install in the terminal."); }
                }
                else if (!install) notes.Add("Its packages are not installed: run npm install in the terminal.");
                break;
            }
        }
        foreach (var (rel, text) in written)
        {
            string full = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
        }
        if (r.Git)
        {
            string ignore = Path.Combine(folder, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, GitIgnore);
            if (!Directory.Exists(Path.Combine(folder, ".git")))
            {
                try { Npm.RunProgram(Environment.GetEnvironmentVariable("PYMAGIC_GIT") is { Length: > 0 } g ? g : "git", folder, new[] { "init" }, TimeSpan.FromMinutes(1)); }
                catch (ServiceException e) { notes.Add("Git: " + e.Message); }
            }
        }
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>()
            .Concat(Directory.EnumerateDirectories(folder).Select(d => Path.GetFileName(d) + "/").Where(d => d is not ("node_modules/" or ".git/")))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new NewProjectResult(folder, files, null, notes);
    }

    /// <summary>Unpacks the files under <paramref name="prefix"/> of an npm package's tarball (the chosen version, or the latest) into the folder; returns the version.</summary>
    static string Unpack(Npm npm, string package, string? version, string folder, string prefix)
    {
        string temp = Path.Combine(Path.GetTempPath(), "pymagic_pack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string spec = package + "@" + (version is { } v && Version.IsMatch(v) ? v : "latest");
            npm.Run(temp, new[] { "pack", spec, "--pack-destination", temp, "--silent" }, TimeSpan.FromMinutes(5));
            string tgz = Directory.GetFiles(temp, "*.tgz").FirstOrDefault() ?? throw new ServiceException($"npm did not fetch {spec}");
            ExtractUnder(tgz, prefix, folder);
            return Regex.Match(Path.GetFileNameWithoutExtension(tgz), @"\d+\.\d+\.\d+.*$").Value;
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Writes the files of a .tgz under a prefix (package/dist/) into a folder, keeping the paths below the prefix; nothing outside the folder.</summary>
    public static int ExtractUnder(string tgz, string prefix, string folder)
    {
        string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        int count = 0;
        using var file = File.OpenRead(tgz);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            string path = entry.Name.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) continue;
            string target = Path.GetFullPath(Path.Combine(folder, path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        if (count == 0) throw new ServiceException("the package has no " + prefix.TrimEnd('/') + " folder");
        return count;
    }

    /// <summary>The versions New Project offers for a kind, newest first (the npm registry is asked), or none when it cannot be reached.</summary>
    public static List<string> Versions(string kind) => VersionPackage(kind) is { } package ? VersionsOf(package) : new();

    /// <summary>The stable versions of an npm package, newest first (a generator's, for the version beside its command); none when npm cannot be reached.</summary>
    public static List<string> VersionsOf(string package)
    {
        if (!IsPackageName(package)) return new();
        try
        {
            string json = Npm.Find().Output(Path.GetTempPath(), new[] { "view", package, "versions", "--json" }, TimeSpan.FromSeconds(60));
            var list = JsonNode.Parse(json) as JsonArray;
            return StableVersions(list?.Select(n => n?.GetValue<string>() ?? "") ?? []).Take(30).ToList();
        }
        catch (Exception e) when (e is ServiceException or System.Text.Json.JsonException or InvalidOperationException) { return new(); }
    }
}

/// <summary>npm and npx, run through node itself (npm-cli.js, npx-cli.js) rather than their .cmd files, so no argument passes through cmd.exe.</summary>
public sealed record Npm(string Node, string NpmCli, string NpxCli)
{
    /// <summary>
    /// The node chosen in New Project (or the one on the PATH) and its npm: the npm command found first on the PATH (a newer npm installed globally
    /// wins over Node's own) when that node is the PATH's, else the npm beside the chosen node.
    /// </summary>
    public static Npm Find(string? chosen = null)
    {
        string? onPath = LockFiles.Find("node");
        string node = !string.IsNullOrWhiteSpace(chosen) ? chosen.Trim() : onPath ?? throw new ServiceException("this kind of project needs Node.js (nodejs.org), which was not found");
        if (!File.Exists(node)) throw new ServiceException($"the Node runtime {node} does not exist");
        bool pathNode = onPath != null && string.Equals(Path.GetFullPath(onPath), Path.GetFullPath(node), StringComparison.OrdinalIgnoreCase);
        string? pathNpm = pathNode && LockFiles.Find("npm") is { } npm ? Path.GetDirectoryName(npm) : null;
        foreach (string dir in new[] { pathNpm, Path.GetDirectoryName(node)! }.OfType<string>())
        {
            string bin = Path.Combine(dir, "node_modules", "npm", "bin");
            if (File.Exists(Path.Combine(bin, "npm-cli.js")) && File.Exists(Path.Combine(bin, "npx-cli.js")))
                return new Npm(node, Path.Combine(bin, "npm-cli.js"), Path.Combine(bin, "npx-cli.js"));
        }
        throw new ServiceException("npm was not found beside Node.js");
    }

    /// <summary>The Node runtimes of this computer, for New Project's Node runtime: each node on the PATH and in Node's usual folders, with its version.</summary>
    public static List<(string Path, string Version)> Runtimes()
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var places = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('"'))
            .Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(p => p.Length > 0).Select(p => Path.Combine(p, "nodejs")));
        foreach (var dir in places)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir, exe)); }
            catch (ArgumentException) { continue; }
            if (!File.Exists(candidate) || !seen.Add(candidate)) continue;
            string version;
            try { version = RunProgram(candidate, Path.GetTempPath(), new[] { "--version" }, TimeSpan.FromSeconds(15)).Trim().TrimStart('v'); }
            catch (ServiceException) { continue; }
            found.Add((candidate, version));
        }
        return found;
    }

    public void Run(string folder, IEnumerable<string> args, TimeSpan limit) => RunProgram(Node, folder, new[] { NpmCli }.Concat(args), limit);

    public void Npx(string folder, IEnumerable<string> args, TimeSpan limit) => RunProgram(Node, folder, new[] { NpxCli }.Concat(args), limit);

    public string Output(string folder, IEnumerable<string> args, TimeSpan limit) => RunProgram(Node, folder, new[] { NpmCli }.Concat(args), limit);

    /// <summary>Runs a program with no input (a generator that would ask stops instead of waiting), and returns what it wrote; a failure says the last line of its errors.</summary>
    public static string RunProgram(string program, string folder, IEnumerable<string> args, TimeSpan limit)
    {
        var psi = new ProcessStartInfo(program) { WorkingDirectory = folder, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // no questions, no adverts, no update check
        psi.Environment["npm_config_yes"] = "true";
        psi.Environment["npm_config_fund"] = "false";
        psi.Environment["npm_config_audit"] = "false";
        psi.Environment["npm_config_update_notifier"] = "false";
        psi.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("cannot start " + program);
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(limit)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException(Path.GetFileName(program) + " took too long"); }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                string last = (err.Result + "\n" + output.Result).Trim().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "exit code " + p.ExitCode;
                string what = string.Join(' ', psi.ArgumentList.Skip(1).Where(a => !a.StartsWith('-')).Take(2));
                throw new ServiceException($"{what} failed: {last}");
            }
            return output.Result;
        }
        catch (System.ComponentModel.Win32Exception e) { throw new ServiceException($"cannot start {program}: {e.Message}"); }
    }
}
