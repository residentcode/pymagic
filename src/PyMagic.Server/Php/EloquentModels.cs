using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Laravel's Eloquent models as completion: after <c>$user-></c> the columns of the model's table (read from the project's migrations:
/// Schema::create and Schema::table, $table->string('email'), id(), timestamps(), softDeletes(), rememberToken(), foreignId(), morphs(), a
/// column dropped or renamed later) and its relations as properties (a method that returns $this->hasMany(…) and its kin). Phpactor knows the
/// methods of a model but not its columns: they are added to its answer when the receiver is one of the project's models.
/// </summary>
public static class EloquentModels
{
    static readonly HashSet<string> ModelBases = new(StringComparer.Ordinal) { "Model", "Authenticatable", "Pivot", "MorphPivot", "User" };
    static readonly string[] RelationCalls = { "hasOne", "hasMany", "belongsTo", "belongsToMany", "morphTo", "morphOne", "morphMany", "morphToMany", "morphedByMany", "hasOneThrough", "hasManyThrough" };

    /// <summary>The file of a class of the project by composer.json's PSR-4 map (App\Models\User → app/Models/User.php), or null.</summary>
    public static string? FileOf(string root, string fqcn)
    {
        fqcn = fqcn.TrimStart('\\');
        var map = new List<(string Prefix, string Dir)>();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(root, "composer.json")))?["autoload"]?["psr-4"] is JsonObject psr4)
                foreach (var (prefix, dir) in psr4)
                    foreach (var d in dir is JsonArray a ? a.Select(x => x?.GetValue<string>()) : new[] { dir?.GetValue<string>() })
                        if (d != null) map.Add((prefix, d));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
        if (map.Count == 0) map.Add(("App\\", "app/"));
        foreach (var (prefix, dir) in map.OrderByDescending(m => m.Prefix.Length))
        {
            if (!fqcn.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string file = Path.GetFullPath(Path.Combine(root, dir, fqcn[prefix.Length..].Replace('\\', Path.DirectorySeparatorChar) + ".php"));
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>Whether a class's text is an Eloquent model: it extends Model (or Authenticatable, Pivot), here or through a class of the project.</summary>
    public static bool IsModel(string root, string text, int depth = 0)
    {
        var m = Regex.Match(text, @"class\s+\w+\s+extends\s+\\?([\w\\]+)");
        if (!m.Success) return false;
        string parent = m.Groups[1].Value;
        string shortName = parent[(parent.LastIndexOf('\\') + 1)..];
        if (parent.StartsWith("Illuminate\\", StringComparison.Ordinal) || (ModelBases.Contains(shortName) && Regex.IsMatch(text, @"use\s+Illuminate\\(Database\\Eloquent|Foundation\\Auth)\\")))
            return true;
        if (depth > 4) return false;
        // a base model of the project: its file by the use statement (or the same namespace)
        string? fq = Regex.Match(text, @"use\s+([\w\\]+\\" + Regex.Escape(shortName) + @")\s*;") is { Success: true } u ? u.Groups[1].Value
            : Regex.Match(text, @"namespace\s+([\w\\]+)\s*;") is { Success: true } ns ? ns.Groups[1].Value + "\\" + shortName : null;
        return fq != null && FileOf(root, fq) is { } baseFile && IsModel(root, File.ReadAllText(baseFile), depth + 1);
    }

    /// <summary>The table of a model: its $table, else its class name in snake case and plural (User → users, Category → categories).</summary>
    public static string TableOf(string text)
    {
        if (Regex.Match(text, @"protected\s+(?:\??string\s+)?\$table\s*=\s*['""]([^'""]+)['""]") is { Success: true } t) return t.Groups[1].Value;
        string cls = Regex.Match(text, @"class\s+(\w+)").Groups[1].Value;
        string snake = Regex.Replace(cls, @"(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
        if (Regex.IsMatch(snake, @"[^aeiou]y$")) return snake[..^1] + "ies";
        if (Regex.IsMatch(snake, @"(s|x|z|ch|sh)$")) return snake + "es";
        return snake + "s";
    }

    /// <summary>The columns of a table by the project's migrations, in order: (name, type).</summary>
    public static List<(string Name, string Type)> Columns(string root, string table)
    {
        var columns = new List<(string Name, string Type)>();
        string dir = Path.Combine(root, "database", "migrations");
        if (!Directory.Exists(dir)) return columns;
        foreach (var file in Directory.GetFiles(dir, "*.php").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            string code = LaravelLinks.CodeOnly(text);
            // the up() of the migration: down() undoes it
            int down = Regex.Match(code, @"function\s+down\s*\(") is { Success: true } d ? d.Index : code.Length;
            foreach (Match s in Regex.Matches(text[..down], @"Schema::(create|table)\(\s*['""]" + Regex.Escape(table) + @"['""]\s*,\s*function\s*\([^)]*\$(\w+)"))
            {
                if (code[s.Index] != 'S') continue;
                int open = code.IndexOf('{', s.Index);
                if (open < 0) continue;
                int close = LaravelLinks.ClosingBrace(code, open);
                string body = text[open..close];
                string v = Regex.Escape(s.Groups[2].Value);
                foreach (Match c in Regex.Matches(body, @"\$" + v + @"\s*->\s*(\w+)\s*\(\s*(?:['""]([^'""]+)['""]|\[([^\]]*)\])?"))
                {
                    string type = c.Groups[1].Value, name = c.Groups[2].Value;
                    void Add(string n, string t) { columns.RemoveAll(x => x.Name == n); columns.Add((n, t)); }
                    switch (type)
                    {
                        case "id": Add(name.Length > 0 ? name : "id", "id"); break;
                        case "timestamps": case "timestampsTz": case "nullableTimestamps": Add("created_at", "timestamp"); Add("updated_at", "timestamp"); break;
                        case "softDeletes": case "softDeletesTz": Add(name.Length > 0 ? name : "deleted_at", "timestamp"); break;
                        case "rememberToken": Add("remember_token", "string"); break;
                        case "morphs": case "nullableMorphs": case "uuidMorphs": case "ulidMorphs": case "nullableUuidMorphs": case "nullableUlidMorphs":
                            if (name.Length > 0) { Add(name + "_type", "string"); Add(name + "_id", type.Contains("uuid", StringComparison.OrdinalIgnoreCase) ? "uuid" : type.Contains("ulid", StringComparison.OrdinalIgnoreCase) ? "ulid" : "unsignedBigInteger"); }
                            break;
                        case "dropColumn": case "dropColumns":
                            foreach (var n in (name.Length > 0 ? new[] { name } : Regex.Matches(c.Groups[3].Value, @"['""]([^'""]+)['""]").Select(x => x.Groups[1].Value))) columns.RemoveAll(x => x.Name == n);
                            break;
                        case "dropTimestamps": columns.RemoveAll(x => x.Name is "created_at" or "updated_at"); break;
                        case "dropSoftDeletes": columns.RemoveAll(x => x.Name == "deleted_at"); break;
                        case "renameColumn":
                            if (Regex.Match(body[c.Index..], @"renameColumn\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]+)['""]") is { Success: true } r)
                            {
                                int at = columns.FindIndex(x => x.Name == r.Groups[1].Value);
                                if (at >= 0) columns[at] = (r.Groups[2].Value, columns[at].Type);
                            }
                            break;
                        default:
                            // a column: a type's method with the column's name first ($table->string('email'), foreignId('user_id'))
                            if (name.Length > 0 && !type.StartsWith("drop", StringComparison.Ordinal) && !type.StartsWith("rename", StringComparison.Ordinal)
                                && type is not ("index" or "unique" or "primary" or "foreign" or "fullText" or "spatialIndex" or "comment" or "engine" or "charset" or "collation"))
                                Add(name, type);
                            break;
                    }
                }
            }
        }
        return columns;
    }

    /// <summary>
    /// Where a column of a table is made: the migration (its full path) and the line (0-based) of the last Schema::create or Schema::table of the
    /// table that names it; timestamps() for created_at and updated_at, id() for id. Null when no migration names it.
    /// </summary>
    public static (string File, int Line)? MigrationOf(string root, string table, string column)
    {
        string dir = Path.Combine(root, "database", "migrations");
        if (!Directory.Exists(dir) || column.Length == 0) return null;
        (string, int)? found = null;
        string named = column switch { "created_at" or "updated_at" => @"->\s*(?:timestamps|timestampsTz|nullableTimestamps)\s*\(", "id" => @"->\s*id\s*\(\s*\)", "deleted_at" => @"->\s*softDeletes(?:Tz)?\s*\(", "remember_token" => @"->\s*rememberToken\s*\(", _ => "" };
        foreach (var file in Directory.GetFiles(dir, "*.php").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            foreach (Match s in Regex.Matches(text, @"Schema::(?:create|table)\(\s*['""]" + Regex.Escape(table) + @"['""]"))
            {
                int open = text.IndexOf('{', s.Index);
                if (open < 0) continue;
                int close = LaravelLinks.ClosingBrace(LaravelLinks.CodeOnly(text), open);
                string body = text[open..Math.Max(open, close)];
                var m = Regex.Match(body, @"\(\s*['""]" + Regex.Escape(column) + @"['""]");
                if (!m.Success && named.Length > 0) m = Regex.Match(body, named);
                if (m.Success) found = (file, text[..(open + m.Index)].Count(c => c == '\n'));
            }
        }
        return found;
    }

    /// <summary>The relations of a model's text: (method name, kind of relation, related class's short name or null).</summary>
    public static List<(string Name, string Kind, string? Related)> Relations(string text)
    {
        var relations = new List<(string, string, string?)>();
        string code = LaravelLinks.CodeOnly(text);
        foreach (Match m in Regex.Matches(code, @"public\s+function\s+(\w+)\s*\([^)]*\)\s*(?::\s*[\w\\]+)?\s*\{"))
        {
            int open = m.Index + m.Length - 1, close = LaravelLinks.ClosingBrace(code, open);
            var r = Regex.Match(text[open..close], @"return\s+\$this\s*->\s*(" + string.Join("|", RelationCalls) + @")\s*\(\s*(?:\\?([\w\\]+)::class)?");
            if (r.Success) relations.Add((m.Groups[1].Value, r.Groups[1].Value, r.Groups[2].Success ? r.Groups[2].Value[(r.Groups[2].Value.LastIndexOf('\\') + 1)..] : null));
        }
        return relations;
    }

    /// <summary>
    /// The completion items a model adds after <c>-></c>: its columns and its relations as properties, for the model class Phpactor names
    /// (null when it is not one of the project's models).
    /// </summary>
    public static JsonArray? Items(string root, string fqcn)
    {
        if (Model(root, fqcn) is not { } model) return null;
        var items = new JsonArray();
        int n = 0;
        foreach (var a in model.Attributes)
            items.Add(new JsonObject { ["label"] = a.Name, ["kind"] = 10, ["detail"] = DetailOf(a, model), ["insertText"] = a.Name, ["sortText"] = "!" + (n++).ToString("D3") });
        foreach (var (name, kind, related) in Relations(model.Text))
            items.Add(new JsonObject { ["label"] = name, ["kind"] = 10, ["detail"] = $"{kind}{(related != null ? " " + related : "")} relation", ["insertText"] = name, ["sortText"] = "!" + (n++).ToString("D3") });
        return items;
    }

    /// <summary>What an attribute is, as completion says it: its PHP type (by its cast, or its column's type) and where it comes from.</summary>
    public static string DetailOf(ModelAttribute a, ModelInfo model) =>
        $"{a.PhpType}{(a.Nullable && a.PhpType != "mixed" ? "|null" : "")} · " + (a.Accessor ? $"accessor of {model.ShortName}" : $"{a.Type} column of {model.Table}") + (a.Cast is { } c && !a.Accessor ? $" (cast {c})" : "");

    /// <summary>An attribute of a model: a column of its table or an accessor; its database type, its cast, the PHP type it reads as.</summary>
    public sealed record ModelAttribute(string Name, string Type, string? Cast, string PhpType, bool Nullable, bool Accessor);

    /// <summary>A model of the project: its text, table, attributes, and whether they were read from the database (Laravel's model:show) or the code.</summary>
    public sealed record ModelInfo(string Fqcn, string Text, string Table, List<ModelAttribute> Attributes, bool FromDatabase)
    {
        public string ShortName => Fqcn[(Fqcn.LastIndexOf('\\') + 1)..];
    }

    /// <summary>
    /// Runs the project's artisan (php artisan …) with the PHP of Settings > PHP (this computer's, Sail's container's, the SSH machine's) and gives its
    /// exit code and output, or null when there is no PHP to run it with; set by the host. Without it the attributes are the code's only.
    /// </summary>
    public static Func<string, IReadOnlyList<string>, int, (int Code, string Out)?>? Artisan { get; set; }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, Task<JsonObject?> Shown)> Shown = new(StringComparer.Ordinal);

    /// <summary>
    /// A model of the project by its class, or null when the class is not one: its attributes as the database has them (artisan model:show --json:
    /// the table's real columns, their casts, the accessors), else as the code says (the migrations' columns, the model's $casts or casts(), its
    /// accessors). The database's answer is kept until the model, the migrations, the database file or .env change; the first one is waited for
    /// <paramref name="waitMs"/> at most (the code's answer meanwhile).
    /// </summary>
    public static ModelInfo? Model(string root, string fqcn, int waitMs = 4000)
    {
        fqcn = fqcn.TrimStart('\\');
        if (FileOf(root, fqcn) is not { } file) return null;
        string text;
        try { text = File.ReadAllText(file); } catch (IOException) { return null; }
        if (!IsModel(root, text)) return null;
        if (Artisan != null && File.Exists(Path.Combine(root, "artisan")))
        {
            string stamp = StampOf(root, file);
            string key = root + "\0" + fqcn;
            var entry = Shown.AddOrUpdate(key, _ => (stamp, ShowModel(root, fqcn)), (_, old) => old.Stamp == stamp ? old : (stamp, ShowModel(root, fqcn)));
            // (a table the database has not got yet, migrations not run, has no columns there: the code's answer then)
            if (entry.Shown.Wait(waitMs) && entry.Shown.Result is { } shown && FromModelShow(shown, text) is { } fromDb && fromDb.Any(a => !a.Accessor))
                return new ModelInfo(fqcn, text, shown["table"]?.GetValue<string>() ?? TableOf(text), fromDb, true);
        }
        string table = TableOf(text);
        var casts = Casts(text);
        var attributes = Columns(root, table).Select(c =>
        {
            // the timestamps are dates without a cast of their own (Laravel's), deleted_at too with SoftDeletes
            string? cast = casts.TryGetValue(c.Name, out var k) ? k
                : c.Name is "created_at" or "updated_at" || (c.Name == "deleted_at" && text.Contains("SoftDeletes")) ? "datetime" : null;
            return new ModelAttribute(c.Name, c.Type, cast, PhpTypeOf(cast, c.Type, text), false, false);
        }).ToList();
        foreach (var (name, type) in Accessors(text))
            if (!attributes.Any(a => a.Name == name)) attributes.Add(new ModelAttribute(name, "accessor", null, type ?? "mixed", false, true));
        return new ModelInfo(fqcn, text, table, attributes, false);
    }

    static Task<JsonObject?> ShowModel(string root, string fqcn) => Task.Run(() =>
    {
        try
        {
            if (Artisan?.Invoke(root, new[] { "model:show", fqcn, "--json" }, 60) is not { Code: 0 } r) return null;
            // the JSON is the output's last line (a deprecation notice can come first)
            string? line = r.Out.Replace("\r", "").Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
            return line != null ? JsonNode.Parse(line) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or ServiceException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    });

    /// <summary>What says the database's answer may have changed: the model's file, the migrations, the SQLite files of database/, .env.</summary>
    static string StampOf(string root, string modelFile)
    {
        long Time(string p) { try { return File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p).Ticks : 0; } catch (IOException) { return 0; } }
        var parts = new List<long> { Time(modelFile), Time(Path.Combine(root, ".env")), Time(Path.Combine(root, "database", "migrations")) };
        try
        {
            string db = Path.Combine(root, "database");
            if (Directory.Exists(db)) parts.AddRange(Directory.GetFiles(db, "*.sqlite").Select(Time));
            string migrations = Path.Combine(root, "database", "migrations");
            if (Directory.Exists(migrations)) parts.AddRange(Directory.GetFiles(migrations, "*.php").Select(Time));
        }
        catch (IOException) { }
        return string.Join(",", parts);
    }

    /// <summary>model:show's attributes as the model's: columns with their database type, nullability and cast; accessors (cast "accessor" or "attribute").</summary>
    public static List<ModelAttribute>? FromModelShow(JsonObject shown, string modelText)
    {
        if (shown["attributes"] is not JsonArray list) return null;
        var accessors = Accessors(modelText).ToDictionary(a => a.Name, a => a.Type);
        var attributes = new List<ModelAttribute>();
        foreach (var a in list.OfType<JsonObject>())
        {
            if (a["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            string? cast = a["cast"]?.GetValue<string>();
            string? type = a["type"]?.GetValue<string>();
            bool accessor = cast is "accessor" or "attribute" || type == null;
            bool nullable = a["nullable"]?.GetValue<bool>() == true;
            attributes.Add(accessor
                ? new ModelAttribute(name, "accessor", null, accessors.TryGetValue(name, out var t) && t != null ? t : "mixed", false, true)
                : new ModelAttribute(name, type!, cast, PhpTypeOf(cast, type!, modelText), nullable, false));
        }
        return attributes;
    }

    /// <summary>The PHP type an attribute reads as: by its cast (int, bool, float, array, Carbon, an enum or class's short name), else by its column's type.</summary>
    public static string PhpTypeOf(string? cast, string columnType, string modelText = "")
    {
        if (!string.IsNullOrEmpty(cast))
        {
            string c = cast.TrimStart('\\');
            string head = c.Split(':')[0].ToLowerInvariant();
            switch (head)
            {
                case "int": case "integer": return "int";
                case "bool": case "boolean": return "bool";
                case "float": case "double": case "real": return "float";
                case "decimal": case "string": case "hashed": case "encrypted": return "string";
                case "array": case "json": case "encrypted:array": return "array";
                case "object": case "encrypted:object": return "object";
                case "collection": case "encrypted:collection": return "Collection";
                case "date": case "datetime": case "custom_datetime": case "timestamp": return "Carbon";
                case "immutable_date": case "immutable_datetime": case "immutable_custom_datetime": return "CarbonImmutable";
            }
            if (c.StartsWith("encrypted:", StringComparison.Ordinal)) return PhpTypeOf(c["encrypted:".Length..], columnType, modelText);
            // a class: an enum, AsCollection, AsArrayObject, a cast of the project's (its short name)
            string cls = c.Split(':')[0];
            string shortName = cls[(cls.LastIndexOf('\\') + 1)..];
            return shortName switch { "AsCollection" or "AsEncryptedCollection" => "Collection", "AsArrayObject" or "AsEncryptedArrayObject" => "ArrayObject", "AsStringable" => "Stringable", _ => shortName };
        }
        string t = columnType.ToLowerInvariant();
        if (t is "id" or "increments" or "bigincrements" or "foreignid" || Regex.IsMatch(t, @"^(unsigned)?(tiny|small|medium|big)?int(eger)?\b|^(big)?serial|^year$")) return "int";
        if (Regex.IsMatch(t, @"^(boolean|bool)\b")) return "bool";
        if (Regex.IsMatch(t, @"^(float|double|real|numeric)")) return "float";
        if (Regex.IsMatch(t, @"^json")) return "string";
        return "string";
    }

    /// <summary>The model's casts: its $casts array, or its casts() method's array (attribute name → cast, a class's ::class as its name).</summary>
    public static Dictionary<string, string> Casts(string text)
    {
        var casts = new Dictionary<string, string>(StringComparer.Ordinal);
        string code = LaravelLinks.CodeOnly(text);
        var where = Regex.Match(code, @"\$casts\s*=\s*\[") is { Success: true } p ? p.Index + p.Length - 1
            : Regex.Match(code, @"function\s+casts\s*\(\s*\)[^{]*\{[^\[]*return\s*\[") is { Success: true } f ? f.Index + f.Length - 1 : -1;
        if (where < 0) return casts;
        int depth = 0, end = where;
        for (; end < code.Length; end++) { if (code[end] == '[') depth++; else if (code[end] == ']' && --depth == 0) break; }
        string body = text[where..Math.Min(end + 1, text.Length)];
        foreach (Match m in Regex.Matches(body, @"['""](\w+)['""]\s*=>\s*(?:['""]([^'""]+)['""]|\\?([\w\\]+)::class(?:\s*\.\s*['""]([^'""]*)['""])?)"))
            casts[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value + (m.Groups[4].Success ? m.Groups[4].Value : "");
        return casts;
    }

    /// <summary>
    /// The model's accessors: an Attribute method (protected function fullName(): Attribute → full_name) and the older get…Attribute methods
    /// (getFullNameAttribute → full_name); the type is the get closure's or the method's declared return type, when it says one.
    /// </summary>
    public static List<(string Name, string? Type)> Accessors(string text)
    {
        var found = new List<(string, string?)>();
        string code = LaravelLinks.CodeOnly(text);
        static string Snake(string s) => Regex.Replace(s, @"(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
        static string? Short(string? t) => t == null ? null : Regex.Replace(t.Trim(), @"\\?(?:[\w]+\\)+(\w+)", "$1");
        foreach (Match m in Regex.Matches(code, @"function\s+(\w+)\s*\(\s*\)\s*:\s*\\?(?:Illuminate\\Database\\Eloquent\\Casts\\)?Attribute\b"))
        {
            int open = code.IndexOf('{', m.Index + m.Length);
            int close = open < 0 ? -1 : LaravelLinks.ClosingBrace(code, open);
            string body = open < 0 || close < 0 ? "" : text[open..close];
            // get: fn (…): string => …, or get: function (…): string { … }
            string? type = Regex.Match(body, @"get\s*:\s*(?:static\s+)?(?:fn|function)\s*\([^)]*\)\s*:\s*(\??[\w\\|]+)") is { Success: true } g ? g.Groups[1].Value : null;
            found.Add((Snake(m.Groups[1].Value), Short(type)));
        }
        foreach (Match m in Regex.Matches(code, @"function\s+get(\w+)Attribute\s*\([^)]*\)\s*(?::\s*(\??[\w\\|]+))?"))
            found.Add((Snake(m.Groups[1].Value), Short(m.Groups[2].Success ? m.Groups[2].Value : null)));
        return found;
    }

    /// <summary>
    /// The class names in Phpactor's hover text of a value, the first one first: full names (App\Models\User, \App\Models\User|null) as they are,
    /// a short one (its hover says "variable user: `Ⓒ User`") by the document's use statements, else its namespace, else App\Models.
    /// </summary>
    public static List<string> ClassesIn(string? hover, string? document = null)
    {
        if (hover == null) return new();
        var names = new List<string>();
        foreach (Match m in Regex.Matches(hover, @"\\?([A-Z][A-Za-z0-9_]*(?:\\[A-Z][A-Za-z0-9_]*)*)"))
        {
            string name = m.Groups[1].Value;
            if (name.Contains('\\')) { names.Add(name); continue; }
            if (document != null && Regex.Match(document, @"^\s*use\s+\\?([\w\\]+\\" + Regex.Escape(name) + @")\s*;", RegexOptions.Multiline) is { Success: true } use) names.Add(use.Groups[1].Value);
            else if (document != null && Regex.Match(document, @"^\s*use\s+\\?([\w\\]+)\s+as\s+" + Regex.Escape(name) + @"\s*;", RegexOptions.Multiline) is { Success: true } alias) names.Add(alias.Groups[1].Value);
            else if (document != null && Regex.Match(document, @"^\s*namespace\s+([\w\\]+)\s*;", RegexOptions.Multiline) is { Success: true } ns) names.Add(ns.Groups[1].Value + "\\" + name);
            else names.Add("App\\Models\\" + name);
        }
        return names.Distinct().ToList();
    }
}
