using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Laravel's names in a .php file or a Blade template, as links and as completion: a view's name (view('users.index'), View::make, Route::view,
/// @extends, @include and its kin, @component, @each, &lt;x-alert&gt;) to its template under resources/views; a route's name (route('login'),
/// to_route, redirect()->route, URL::route, Route::has) to the ->name('…') that gives it in routes/; a config key (config('app.name'),
/// Config::get) to its line in config/; an environment variable (env('APP_KEY')) to its line in .env; a translation (__('auth.failed'),
/// trans, @lang) to its line in lang/; an asset (asset('css/app.css'), Vite's @vite) to its file. Completion in the same quotes gives the
/// project's views, routes, config keys, variables, translations and assets.
/// </summary>
public static class LaravelLinks
{
    /// <summary>What a quoted name is: the kind of name and the text of the quotes (start, end: offsets of the name in the text).</summary>
    public sealed record Place(string Kind, string Name, int Start, int End);

    // the call or directive before the quote that names a kind of thing; the name is its first argument (a view: Route::view's second)
    static readonly (string Kind, Regex Before)[] Calls =
    {
        ("view", new Regex(@"(?:\bview|View::(?:make|first|exists)|->view|Route::view\s*\([^,()]*,|\bmarkdown|->markdown)\s*\(\s*$", RegexOptions.Compiled)),
        ("view", new Regex(@"@(?:extends|include|includeIf|includeFirst|component|each|livewire)\s*\(\s*\[?\s*$", RegexOptions.Compiled)),
        ("view", new Regex(@"@includeWhen\s*\([^,()]*,\s*$|@includeUnless\s*\([^,()]*,\s*$", RegexOptions.Compiled)),
        ("route", new Regex(@"(?:\broute|\bto_route|->route|URL::route|Route::has|->routeIs|Route::is|request\(\)->routeIs)\s*\(\s*$", RegexOptions.Compiled)),
        ("config", new Regex(@"(?:\bconfig|Config::(?:get|set|has|string|integer|boolean|array))\s*\(\s*\[?\s*$", RegexOptions.Compiled)),
        ("env", new Regex(@"\benv\s*\(\s*$", RegexOptions.Compiled)),
        ("lang", new Regex(@"(?:\b__|\btrans|\btrans_choice|Lang::get|@lang|@choice)\s*\(\s*$", RegexOptions.Compiled)),
        ("asset", new Regex(@"(?:\basset|\bsecure_asset|\bmix|Vite::asset|@vite\s*\(\s*\[(?:[^\]]*,)?)\s*\(?\s*$", RegexOptions.Compiled)),
    };

    /// <summary>The quoted name at an offset of a file's text (in its quotes, the quotes included), or null.</summary>
    public static Place? At(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;
        // the quotes around the offset, on its line
        int lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        string line = text[lineStart..lineEnd];
        int at = offset - lineStart;
        foreach (Match m in Regex.Matches(line, @"(['""])([^'""\\\n]*)(\1|$)"))
        {
            int nameStart = m.Groups[2].Index, nameEnd = nameStart + m.Groups[2].Length;
            if (at < m.Index || at > (m.Groups[3].Length > 0 ? m.Index + m.Length - 1 : line.Length)) continue;
            string before = line[..m.Index];
            foreach (var (kind, call) in Calls)
                if (call.IsMatch(before)) return new Place(kind, m.Groups[2].Value, lineStart + nameStart, lineStart + nameEnd);
            return null;
        }
        // a Blade component's tag: <x-alert …> or <x-forms.input>
        foreach (Match m in Regex.Matches(line, @"</?x-([\w.\-:]*)"))
            if (at >= m.Index && at <= m.Index + m.Length) return new Place("component", m.Groups[1].Value, lineStart + m.Groups[1].Index, lineStart + m.Groups[1].Index + m.Groups[1].Length);
        return null;
    }

    // ------------------------------------------------------------ links

    /// <summary>Where a name leads: a file of the project and its line (0-based), or null when the project has none.</summary>
    public static (string File, int Line)? Target(string root, Place place)
    {
        switch (place.Kind)
        {
            case "view":
                return ViewFile(root, place.Name) is { } view ? (view, 0) : null;
            case "component":
            {
                string rel = place.Name.Replace("::", "/").Replace('.', Path.DirectorySeparatorChar);
                foreach (var candidate in new[] { Path.Combine(root, "resources", "views", "components", rel + ".blade.php"), Path.Combine(root, "resources", "views", "components", rel, "index.blade.php") })
                    if (File.Exists(candidate)) return (candidate, 0);
                // a class component: app/View/Components/Forms/Input.php
                string cls = Path.Combine(root, "app", "View", "Components", string.Join(Path.DirectorySeparatorChar, place.Name.Split('.').Select(Studly)) + ".php");
                return File.Exists(cls) ? (cls, LineOf(cls, new Regex(@"^\s*(?:final\s+)?class\s"))) : null;
            }
            case "route":
                foreach (var (name, file, line) in Routes(root)) if (name == place.Name) return (file, line);
                return null;
            case "config":
            {
                var parts = place.Name.Split('.');
                string file = Path.Combine(root, "config", parts[0] + ".php");
                if (!File.Exists(file)) return null;
                return (file, parts.Length > 1 ? KeyLine(file, parts.Skip(1).ToArray()) : 0);
            }
            case "env":
            {
                string file = Path.Combine(root, ".env");
                if (!File.Exists(file)) file = Path.Combine(root, ".env.example");
                if (!File.Exists(file)) return null;
                int line = LineOf(file, new Regex(@"^\s*(?:export\s+)?" + Regex.Escape(place.Name) + @"\s*="));
                return line >= 0 ? (file, line) : null;
            }
            case "lang":
            {
                var parts = place.Name.Split('.');
                foreach (var dir in LangDirs(root))
                {
                    string file = Path.Combine(dir, parts[0] + ".php");
                    if (parts.Length > 1 && File.Exists(file)) return (file, Math.Max(0, KeyLine(file, parts.Skip(1).ToArray())));
                    string json = dir + ".json";
                    if (File.Exists(json)) { int l = LineOf(json, new Regex("^\\s*\"" + Regex.Escape(place.Name) + "\"\\s*:")); if (l >= 0) return (json, l); }
                }
                return null;
            }
            case "asset":
            {
                foreach (var candidate in new[] { Path.Combine(root, "public", place.Name.TrimStart('/')), Path.Combine(root, place.Name.TrimStart('/')) })
                    if (File.Exists(candidate) && IsInside(candidate, root)) return (candidate, 0);
                return null;
            }
        }
        return null;
    }

    static bool IsInside(string path, string root)
    {
        string f = Path.GetFullPath(path), r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return f.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    static string Studly(string s) => string.Concat(s.Split('-', '_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>A view's template: users.index → resources/views/users/index.blade.php (or .php); a package's (mail::x) is not the project's.</summary>
    public static string? ViewFile(string root, string name)
    {
        if (name.Length == 0 || name.Contains("::") || name.Contains("..")) return null;
        string rel = name.Replace('.', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var ext in new[] { ".blade.php", ".php" })
        {
            string file = Path.Combine(root, "resources", "views", rel + ext);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    static int LineOf(string file, Regex pattern)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++) if (pattern.IsMatch(lines[i])) return i;
        }
        catch (IOException) { }
        return -1;
    }

    /// <summary>The line of a key of a config or translation file's array ('a' => [ 'b' => …): each key looked for after the one before; 0 when not found.</summary>
    static int KeyLine(string file, string[] keys)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); } catch (IOException) { return 0; }
        int from = 0, found = 0;
        foreach (var key in keys)
        {
            var pattern = new Regex(@"^\s*['""]" + Regex.Escape(key) + @"['""]\s*=>");
            int hit = -1;
            for (int i = from; i < lines.Length; i++) if (pattern.IsMatch(lines[i])) { hit = i; break; }
            if (hit < 0) return found;
            found = hit; from = hit + 1;
        }
        return found;
    }

    static IEnumerable<string> LangDirs(string root)
    {
        foreach (var lang in new[] { Path.Combine(root, "lang"), Path.Combine(root, "resources", "lang") })
        {
            if (!Directory.Exists(lang)) continue;
            // the app's locale first (config/app.php's 'locale', usually en), then the others
            string locale = "en";
            try { if (Regex.Match(File.ReadAllText(Path.Combine(root, "config", "app.php")), @"'locale'\s*=>\s*env\(\s*'APP_LOCALE'\s*,\s*'(\w+)'|'locale'\s*=>\s*'(\w+)'") is { Success: true } m) locale = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value; }
            catch (IOException) { }
            yield return Path.Combine(lang, locale);
            foreach (var d in Directory.GetDirectories(lang).Where(d => !string.Equals(Path.GetFileName(d), locale, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(d) != "vendor")) yield return d;
        }
    }

    /// <summary>The project's named routes: the name given by ->name('…') in routes/*.php, with a group's name prefix (Route::name('admin.')->group).</summary>
    public static List<(string Name, string File, int Line)> Routes(string root)
    {
        var result = new List<(string, string, int)>();
        string dir = Path.Combine(root, "routes");
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.GetFiles(dir, "*.php", SearchOption.AllDirectories))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }
            // name prefixes of the groups open at each line: Route::name('admin.')->group(function () { … }) / ->name('admin.')->group(
            var prefixes = new Stack<(string Prefix, int Depth)>();
            int depth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                var group = Regex.Match(l, @"(?:Route::|->)(?:name|as)\(\s*['""]([^'""]+)['""]\s*\)(?:->\w+\([^)]*\))*->group\(");
                string prefix = string.Concat(prefixes.Reverse().Select(p => p.Prefix));
                foreach (Match m in Regex.Matches(l, @"->name\(\s*['""]([^'""]+)['""]\s*\)"))
                    if (!(group.Success && group.Groups[1].Index == m.Groups[1].Index)) result.Add((prefix + m.Groups[1].Value, file, i));
                foreach (Match m in Regex.Matches(l, @"Route::resource\(\s*['""]([^'""]+)['""]"))
                    foreach (var action in new[] { "index", "create", "store", "show", "edit", "update", "destroy" }) result.Add((prefix + m.Groups[1].Value.Replace('/', '.') + "." + action, file, i));
                int opened = l.Count(c => c == '{'), closed = l.Count(c => c == '}');
                if (group.Success) prefixes.Push((group.Groups[1].Value, depth));
                depth += opened - closed;
                while (prefixes.Count > 0 && depth <= prefixes.Peek().Depth && !group.Success) prefixes.Pop();
                if (group.Success && opened == closed) { /* a group on one line */ prefixes.Pop(); }
            }
        }
        return result;
    }

    // ------------------------------------------------------------ completion

    /// <summary>What can go in the quotes of a place: names with what they are (a view's file, a route's file, a config value's line).</summary>
    public static JsonArray Complete(string root, Place place)
    {
        var items = new JsonArray();
        void Add(string label, string detail) { if (items.Count < 500) items.Add(new JsonObject { ["label"] = label, ["detail"] = detail }); }
        switch (place.Kind)
        {
            case "view":
            {
                string views = Path.Combine(root, "resources", "views");
                if (!Directory.Exists(views)) break;
                foreach (var f in Directory.GetFiles(views, "*.php", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = Path.GetRelativePath(views, f).Replace('\\', '/');
                    string name = (rel.EndsWith(".blade.php") ? rel[..^10] : rel[..^4]).Replace('/', '.');
                    Add(name, "resources/views/" + rel);
                }
                break;
            }
            case "route":
                foreach (var (name, file, line) in Routes(root).DistinctBy(r => r.Name)) Add(name, $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line + 1}");
                break;
            case "config":
            {
                string config = Path.Combine(root, "config");
                if (!Directory.Exists(config)) break;
                foreach (var f in Directory.GetFiles(config, "*.php").OrderBy(f => f))
                {
                    string file = Path.GetFileNameWithoutExtension(f);
                    Add(file, "config/" + Path.GetFileName(f));
                    foreach (var key in TopKeys(f)) Add(file + "." + key, "config/" + Path.GetFileName(f));
                }
                break;
            }
            case "env":
                foreach (var f in new[] { ".env", ".env.example" }.Select(n => Path.Combine(root, n)).Where(File.Exists).Take(1))
                    foreach (var l in File.ReadAllLines(f))
                        if (Regex.Match(l, @"^\s*([A-Z][A-Z0-9_]*)\s*=(.*)$") is { Success: true } m) Add(m.Groups[1].Value, m.Groups[2].Value.Trim());
                break;
            case "lang":
                foreach (var dir in LangDirs(root).Take(1))
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "*.php").OrderBy(f => f))
                            foreach (var key in TopKeys(f)) Add(Path.GetFileNameWithoutExtension(f) + "." + key, Path.GetRelativePath(root, f).Replace('\\', '/'));
                break;
            case "asset":
            {
                string pub = Path.Combine(root, "public");
                if (Directory.Exists(pub))
                    foreach (var f in Directory.GetFiles(pub, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".php") && !f.Contains(Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar)).Take(300))
                        Add(Path.GetRelativePath(pub, f).Replace('\\', '/'), "public");
                foreach (var f in new[] { "resources/css/app.css", "resources/js/app.js" }) if (File.Exists(Path.Combine(root, f))) Add(f, "Vite");
                break;
            }
            case "component":
            {
                string comps = Path.Combine(root, "resources", "views", "components");
                if (Directory.Exists(comps))
                    foreach (var f in Directory.GetFiles(comps, "*.blade.php", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(comps, f).Replace('\\', '/')[..^10];
                        Add(rel.EndsWith("/index") ? rel[..^6].Replace('/', '.') : rel.Replace('/', '.'), "resources/views/components/" + rel + ".blade.php");
                    }
                break;
            }
        }
        return items;
    }

    /// <summary>The keys of the array a config or translation file returns, one level down too ('mail' => ['from' => …] gives mail and mail.from).</summary>
    static IEnumerable<string> TopKeys(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); } catch (IOException) { yield break; }
        int depth = 0;
        string? parent = null;
        foreach (var l in lines)
        {
            string code = Regex.Replace(l, @"//.*$|#.*$", "");
            var key = Regex.Match(code, @"^\s*['""]([\w.\-]+)['""]\s*=>");
            if (key.Success && depth == 1) { yield return key.Groups[1].Value; parent = key.Groups[1].Value; }
            else if (key.Success && depth == 2 && parent != null) yield return parent + "." + key.Groups[1].Value;
            foreach (char c in code) { if (c == '[') depth++; else if (c == ']') depth--; }
            if (depth <= 1) parent = depth == 1 ? parent : null;
        }
    }
}
