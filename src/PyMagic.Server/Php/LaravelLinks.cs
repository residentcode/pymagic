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
    /// <param name="Receiver">For a column of a query (User::where('…')): the model class the query starts from, as the file names it.</param>
    public sealed record Place(string Kind, string Name, int Start, int End, string? Receiver = null);

    // a column of a query: the first argument of where(), orderBy(), pluck() and their kin, a name of select([…]) or whereIn's
    static readonly Regex ColumnCall = new(@"(?:->|::)(?:where|orWhere|whereNot|orWhereNot|whereIn|whereNotIn|orWhereIn|orWhereNotIn|whereNull|whereNotNull|orWhereNull|orWhereNotNull|whereBetween|whereNotBetween|whereDate|whereMonth|whereDay|whereYear|whereTime|whereColumn|whereLike|whereNotLike|firstWhere|orderBy|orderByDesc|latest|oldest|groupBy|having|pluck|value|increment|decrement|sum|avg|average|max|min|count|distinct|select|addSelect|get|only|except|latestOfMany|oldestOfMany)\s*\(\s*(?:\[(?:[^\[\]]*,)?\s*)?$", RegexOptions.Compiled);

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
            // what is before the quote, the lines before too: a call's argument on a line of its own (route(\n 'home'), as in a Blade @php block)
            string before = text[Math.Max(0, lineStart - 400)..(lineStart + m.Index)];
            foreach (var (kind, call) in Calls)
                if (call.IsMatch(before)) return new Place(kind, m.Groups[2].Value, lineStart + nameStart, lineStart + nameEnd);
            if (ColumnCall.IsMatch(before) && QueryModel(before) is { } model && EloquentModels.ClassesIn(model, text).FirstOrDefault() is { } fqcn) return new Place("column", m.Groups[2].Value, lineStart + nameStart, lineStart + nameEnd, fqcn);
            return null;
        }
        // a Blade component's tag: <x-alert …> or <x-forms.input>
        foreach (Match m in Regex.Matches(line, @"</?x-([\w.\-:]*)"))
            if (at >= m.Index && at <= m.Index + m.Length) return new Place("component", m.Groups[1].Value, lineStart + m.Groups[1].Index, lineStart + m.Groups[1].Index + m.Groups[1].Length);
        return null;
    }

    /// <summary>
    /// The class a query starts from, for the text before a call's quote: its statement (back to a ; { or } outside the brackets) begins with
    /// Class:: (User::where(…)->orderBy(, User::query()->…); a variable's query is not known here.
    /// </summary>
    static string? QueryModel(string before)
    {
        int depth = 0, start = 0;
        for (int i = before.Length - 1; i >= 0; i--)
        {
            char c = before[i];
            if (c is ')' or ']') depth++;
            else if (c is '(' or '[') { if (depth == 0) continue; depth--; }
            else if (depth == 0 && c is ';' or '{' or '}') { start = i + 1; break; }
        }
        var m = Regex.Match(before[start..], @"^\s*(?:return\s+|\$\w+\s*=\s*|=>\s*)?\\?([A-Z][\w]*(?:\\[A-Z]\w*)*)::");
        return m.Success && m.Groups[1].Value is not ("DB" or "Schema" or "Route" or "Config" or "Cache" or "Str" or "Arr") ? m.Groups[1].Value : null;
    }

    // ------------------------------------------------------------ links

    /// <summary>Where a name leads: a file of the project and its line (0-based), or null when the project has none.</summary>
    public static (string File, int Line)? Target(string root, Place place)
    {
        switch (place.Kind)
        {
            case "column":
            {
                // a column of a query leads to the migration that makes it (the last one naming it)
                if (place.Receiver == null || EloquentModels.Model(root, place.Receiver, 0) is not { } model) return null;
                return EloquentModels.MigrationOf(root, model.Table, place.Name);
            }
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

    /// <summary>
    /// The project's named routes: the name ->name('…') gives in routes/*.php, under the name prefixes of the groups around it however a group is
    /// written (Route::name('admin.')->group(…), a chain over several lines, ->as('admin.'), Route::group(['as' => 'admin.'], …), groups in
    /// groups); Route::resource and Route::apiResource name their actions (with ->only, ->except and ->names).
    /// </summary>
    public static List<(string Name, string File, int Line)> Routes(string root)
    {
        var result = new List<(string, string, int)>();
        string dir = Path.Combine(root, "routes");
        if (!Directory.Exists(dir)) return result;
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(dir, "*.php", SearchOption.AllDirectories))
            try { texts[Path.GetFullPath(file)] = File.ReadAllText(file); } catch (IOException) { }

        // a routes file a group includes (->name('admin.')->group(base_path('routes/admin.php'))) has the group's prefix: groups of routes files,
        // of bootstrap/app.php (withRouting's then:) and of the service providers, a file included by an included one adding up
        var includers = new Dictionary<string, string>(texts, StringComparer.OrdinalIgnoreCase);
        foreach (var extra in new[] { Path.Combine(root, "bootstrap", "app.php") }.Concat(Directory.Exists(Path.Combine(root, "app", "Providers")) ? Directory.GetFiles(Path.Combine(root, "app", "Providers"), "*.php") : Array.Empty<string>()))
            if (File.Exists(extra)) try { includers[Path.GetFullPath(extra)] = File.ReadAllText(extra); } catch (IOException) { }
        var included = new List<(string From, string Target, string Prefix)>();
        foreach (var (file, text) in includers)
            foreach (var (target, prefix) in IncludesOf(text, Path.GetDirectoryName(file)!, root)) included.Add((file, target, prefix));
        var prefixOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int pass = 0; pass < 4; pass++)
            foreach (var (from, target, prefix) in included)
                if (prefix.Length > 0 || prefixOf.ContainsKey(from))
                    prefixOf[target] = (prefixOf.TryGetValue(from, out var outer) ? outer : "") + prefix;

        foreach (var (file, text) in texts)
        {
            string outerPrefix = prefixOf.TryGetValue(file, out var p) ? p : "";
            foreach (var (name, line) in RoutesOf(text)) result.Add((outerPrefix + name, file, line));
        }
        return result;
    }

    /// <summary>The routes files a file's groups include (by base_path('…'), __DIR__.'…', or a require inside a group), with the prefix they give.</summary>
    public static List<(string Target, string Prefix)> IncludesOf(string text, string fileDir, string root)
    {
        var result = new List<(string, string)>();
        string code = CodeOnly(text);
        var (groups, _, fileGroups) = GroupsOf(text, code);
        string PrefixAt(int at) => string.Concat(groups.Where(g => g.Open < at && at < g.Close).OrderBy(g => g.Open).Select(g => g.Prefix));
        string? Target(string expression)
        {
            string? path = Regex.Match(expression, @"base_path\(\s*['""]([^'""]+)['""]\s*\)") is { Success: true } b ? Path.Combine(root, b.Groups[1].Value)
                : Regex.Match(expression, @"__DIR__\s*\.\s*['""]([^'""]+)['""]") is { Success: true } d ? fileDir + d.Groups[1].Value
                : null;
            return path == null ? null : Path.GetFullPath(path);
        }
        foreach (var (at, args, prefix) in fileGroups)
            if (Target(args) is { } target) result.Add((target, PrefixAt(at) + prefix));
        foreach (Match r in Regex.Matches(text, @"(?<![\w$>:])(?:require|include)(?:_once)?\b\s*\(?([^;]+);"))
            if (code[r.Index] is 'r' or 'i' && Target(r.Groups[1].Value) is { } target) result.Add((target, PrefixAt(r.Index)));
        return result;
    }

    /// <summary>
    /// The groups of a routes file that give a name prefix: the braces of their closure; the offsets of the ->name( of their heads (which name no
    /// route); and the groups of a file (group(base_path(…))): where they are, their arguments' text and their own prefix.
    /// </summary>
    static (List<(int Open, int Close, string Prefix)> Groups, HashSet<int> Heads, List<(int At, string Args, string Prefix)> FileGroups) GroupsOf(string text, string code)
    {
        var groups = new List<(int Open, int Close, string Prefix)>();
        var heads = new HashSet<int>();
        var fileGroups = new List<(int, string, string)>();
        foreach (Match g in Regex.Matches(code, @"(?:->|::)group\s*\("))
        {
            int start = StatementStart(code, g.Index);
            string? prefix = null;
            foreach (Match n in Regex.Matches(text[start..g.Index], @"(?:Route::|->)(?:name|as)\(\s*(['""])([^'""]*)\1\s*\)"))
            {
                prefix = (prefix ?? "") + n.Groups[2].Value;
                heads.Add(start + n.Index);
            }
            // the closure: the first brace after group( (a group of a file, group(base_path(…)), has none before its ;)
            int brace = code.IndexOf('{', g.Index), semi = code.IndexOf(';', g.Index);
            if (brace < 0 || (semi >= 0 && semi < brace))
            {
                if (semi > g.Index) fileGroups.Add((g.Index, text[(g.Index + g.Length)..semi], prefix ?? ""));
                continue;
            }
            // Route::group(['as' => 'admin.', 'prefix' => …], function () {
            if (Regex.Match(text[g.Index..brace], @"['""]as['""]\s*=>\s*(['""])([^'""]*)\1") is { Success: true } a) prefix = (prefix ?? "") + a.Groups[2].Value;
            if (string.IsNullOrEmpty(prefix)) continue;
            groups.Add((brace, ClosingBrace(code, brace), prefix));
        }
        return (groups, heads, fileGroups);
    }

    static readonly string[] ResourceActions = { "index", "create", "store", "show", "edit", "update", "destroy" };

    /// <summary>The named routes of a routes file's text: (name, 0-based line).</summary>
    public static List<(string Name, int Line)> RoutesOf(string text)
    {
        var result = new List<(string, int)>();
        string code = CodeOnly(text);
        int LineAt(int at) { int n = 0; for (int i = 0; i < at && i < text.Length; i++) if (text[i] == '\n') n++; return n; }
        var (groups, heads, _) = GroupsOf(text, code);
        string PrefixAt(int at) => string.Concat(groups.Where(g => g.Open < at && at < g.Close).OrderBy(g => g.Open).Select(g => g.Prefix));

        foreach (Match m in Regex.Matches(text, @"->name\(\s*(['""])([^'""]+)\1\s*\)"))
            if (code[m.Index] == '-' && !heads.Contains(m.Index)) result.Add((PrefixAt(m.Index) + m.Groups[2].Value, LineAt(m.Index)));

        foreach (Match m in Regex.Matches(text, @"Route::(resource|apiResource)\(\s*(['""])([^'""]+)\2"))
        {
            if (code[m.Index] != 'R') continue;
            int end = code.IndexOf(';', m.Index);
            string chain = text[m.Index..(end < 0 ? text.Length : end)];
            var actions = ResourceActions.Where(a => m.Groups[1].Value == "resource" || (a != "create" && a != "edit")).ToList();
            List<string> Quoted(string s) => Regex.Matches(s, @"['""]([^'""]+)['""]").Select(q => q.Groups[1].Value).ToList();
            if (Regex.Match(chain, @"->only\(([^)]*)\)") is { Success: true } only) { var keep = Quoted(only.Groups[1].Value); actions = actions.Where(keep.Contains).ToList(); }
            if (Regex.Match(chain, @"->except\(([^)]*)\)") is { Success: true } except) { var drop = Quoted(except.Groups[1].Value); actions = actions.Where(a => !drop.Contains(a)).ToList(); }
            string name = m.Groups[3].Value.Replace('/', '.');
            var renamed = new Dictionary<string, string>();
            if (Regex.Match(chain, @"->names\(\s*(['""])([^'""]+)\1\s*\)") is { Success: true } names) name = names.Groups[2].Value;
            else if (Regex.Match(chain, @"->names\(\s*\[([^\]]*)\]") is { Success: true } map)
                foreach (Match kv in Regex.Matches(map.Groups[1].Value, @"['""](\w+)['""]\s*=>\s*['""]([^'""]+)['""]")) renamed[kv.Groups[1].Value] = kv.Groups[2].Value;
            string prefix = PrefixAt(m.Index);
            int line = LineAt(m.Index);
            foreach (var action in actions) result.Add((prefix + (renamed.TryGetValue(action, out var r) ? r : name + "." + action), line));
        }
        return result.OrderBy(r => r.Item2).ToList();
    }

    /// <summary>PHP's text with its comments and the insides of its strings blanked (same length, same lines): brackets there are not code.</summary>
    internal static string CodeOnly(string text)
    {
        var c = text.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            if (ch == '/' && i + 1 < c.Length && c[i + 1] == '/' || ch == '#' && (i + 1 >= c.Length || c[i + 1] != '['))
            {
                while (i < c.Length && c[i] != '\n') c[i++] = ' ';
                i--;
            }
            else if (ch == '/' && i + 1 < c.Length && c[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? c.Length : end + 2;
                for (; i < end; i++) if (c[i] != '\n') c[i] = ' ';
                i--;
            }
            else if (ch == '\'' || ch == '"')
            {
                for (i++; i < c.Length && text[i] != ch; i++)
                {
                    if (text[i] == '\\' && i + 1 < c.Length) { c[i] = ' '; i++; }
                    if (c[i] != '\n') c[i] = ' ';
                }
            }
        }
        return new string(c);
    }

    /// <summary>Where the statement that has an offset starts: after the ; or brace before it that is outside brackets.</summary>
    static int StatementStart(string code, int at)
    {
        int depth = 0;
        for (int i = at - 1; i >= 0; i--)
        {
            char ch = code[i];
            if (ch == ')' || ch == ']') depth++;
            else if (ch == '(' || ch == '[') { if (depth == 0) return i + 1; depth--; }
            else if (depth == 0 && (ch == ';' || ch == '{' || ch == '}')) return i + 1;
        }
        return 0;
    }

    internal static int ClosingBrace(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return i;
        }
        return code.Length;
    }

    // ------------------------------------------------------------ completion

    /// <summary>What can go in the quotes of a place: names with what they are (a view's file, a route's file, a config value's line).</summary>
    public static JsonArray Complete(string root, Place place)
    {
        var items = new JsonArray();
        void Add(string label, string detail) { if (items.Count < 500) items.Add(new JsonObject { ["label"] = label, ["detail"] = detail }); }
        switch (place.Kind)
        {
            case "column":
            {
                // the model's attributes that are columns (an accessor is no column of a query)
                if (place.Receiver == null || EloquentModels.Model(root, place.Receiver) is not { } model) break;
                foreach (var a in model.Attributes.Where(a => !a.Accessor)) Add(a.Name, EloquentModels.DetailOf(a, model));
                break;
            }
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
