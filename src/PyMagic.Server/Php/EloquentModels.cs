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
        if (FileOf(root, fqcn) is not { } file) return null;
        string text;
        try { text = File.ReadAllText(file); } catch (IOException) { return null; }
        if (!IsModel(root, text)) return null;
        string table = TableOf(text);
        var items = new JsonArray();
        int n = 0;
        foreach (var (name, type) in Columns(root, table))
            items.Add(new JsonObject { ["label"] = name, ["kind"] = 10, ["detail"] = $"{type} column of {table}", ["insertText"] = name, ["sortText"] = "!" + (n++).ToString("D3") });
        foreach (var (name, kind, related) in Relations(text))
            items.Add(new JsonObject { ["label"] = name, ["kind"] = 10, ["detail"] = $"{kind}{(related != null ? " " + related : "")} relation", ["insertText"] = name, ["sortText"] = "!" + (n++).ToString("D3") });
        return items;
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
