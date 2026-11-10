using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// The project's own .proto files read for the HTTP client's GRPC requests, when the server offers no reflection: their packages, messages
/// (nested ones, oneofs, maps), enums and services, as the descriptors a server would give (Protobuf.Pool). Options, reserved names,
/// extensions and proto2's groups are passed over; google.protobuf's well-known types need no file (they are written in their own JSON forms).
/// </summary>
public static class ProtoFiles
{
    static readonly string[] SkipDirs = { "node_modules", ".git", "vendor", ".venv", "venv", "__pycache__", "bin", "obj", ".pymagic", "dist", "build" };

    static readonly Dictionary<string, int> Scalars = new(StringComparer.Ordinal)
    {
        ["double"] = 1, ["float"] = 2, ["int64"] = 3, ["uint64"] = 4, ["int32"] = 5, ["fixed64"] = 6, ["fixed32"] = 7, ["bool"] = 8, ["string"] = 9,
        ["bytes"] = 12, ["uint32"] = 13, ["sfixed32"] = 15, ["sfixed64"] = 16, ["sint32"] = 17, ["sint64"] = 18,
    };
    const int TMessage = 11, TEnum = 14;

    /// <summary>The .proto files of the project (not those of node_modules, vendor, virtual environments, build folders).</summary>
    public static List<string> Find(string root)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 12) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.proto")) found.Add(f);
                foreach (var d in Directory.GetDirectories(dir))
                    if (!SkipDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) Walk(d, depth + 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (Directory.Exists(root)) Walk(root, 0);
        return found;
    }

    /// <summary>The descriptors of the project's .proto files; a file that cannot be read is left out (its error in <paramref name="errors"/>).</summary>
    public static Protobuf.Pool Pool(string root, List<string>? errors = null)
    {
        var pool = new Protobuf.Pool();
        var parsed = new List<Parsed>();
        foreach (var file in Find(root))
        {
            try { parsed.Add(Parse(File.ReadAllText(file))); }
            catch (Exception e) when (e is FormatException or IOException) { errors?.Add($"{Path.GetRelativePath(root, file)}: {e.Message}"); }
        }
        Fill(pool, parsed);
        return pool;
    }

    /// <summary>The descriptors of .proto texts (for tests, and files given by hand).</summary>
    public static Protobuf.Pool PoolOf(params string[] texts)
    {
        var pool = new Protobuf.Pool();
        Fill(pool, texts.Select(Parse).ToList());
        return pool;
    }

    sealed record RawField(string Name, string Type, int Number, bool Repeated, string? JsonName, string? MapKey, string? MapValue);
    sealed class RawMessage { public string FullName = ""; public List<RawField> Fields = new(); }
    sealed class Parsed
    {
        public string Package = "";
        public List<RawMessage> Messages = new();
        public List<Protobuf.Enum> Enums = new();
        public List<(string FullName, List<(string Name, string Input, string Output, bool Cs, bool Ss)> Methods)> Services = new();
    }

    // ------------------------------------------------------------ reading a file

    static List<string> Tokens(string text)
    {
        // comments out; strings kept whole; names with their dots; numbers; one-character symbols
        text = Regex.Replace(text, @"//[^\n]*|/\*[\s\S]*?\*/", " ");
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(text, @"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\.?[A-Za-z_][\w.]*|-?(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|[{}\[\]()<>;=,:.\-+]"))
            tokens.Add(m.Value);
        return tokens;
    }

    static Parsed Parse(string text)
    {
        var t = Tokens(text);
        var parsed = new Parsed();
        int i = 0;
        string Next() => i < t.Count ? t[i++] : throw new FormatException("the file ends early");
        string Peek() => i < t.Count ? t[i] : "";
        void Expect(string s) { var got = Next(); if (got != s) throw new FormatException($"{s} expected, not {got}"); }
        void SkipStatement()
        {
            int depth = 0;
            while (i < t.Count)
            {
                string x = t[i++];
                if (x is "{" or "[" or "(") depth++;
                else if (x is "}" or "]" or ")") { if (--depth <= 0 && x == "}") return; }
                else if (x == ";" && depth == 0) return;
            }
        }
        string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') ? s[1..^1] : s;
        // a field's options: [json_name = "x", deprecated = true]
        string? Options()
        {
            string? json = null;
            if (Peek() != "[") return null;
            Next();
            while (i < t.Count && Peek() != "]")
            {
                string x = Next();
                if (x == "json_name" && Peek() == "=") { Next(); json = Unquote(Next()); }
            }
            Next();
            return json;
        }

        void Enum(string scope)
        {
            var e = new Protobuf.Enum { FullName = Join(scope, Next()) };
            Expect("{");
            while (Peek() != "}")
            {
                string x = Next();
                if (x is "option" or "reserved") { SkipStatement(); continue; }
                if (x == ";") continue;
                Expect("=");
                string num = Next();
                if (num == "-") num = "-" + Next();
                int n = num.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(num[2..], 16) : int.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
                Options();
                Expect(";");
                e.Numbers[x] = n;
                e.Names.TryAdd(n, x);
            }
            Next();
            parsed.Enums.Add(e);
        }

        void Message(string scope)
        {
            var m = new RawMessage { FullName = Join(scope, Next()) };
            parsed.Messages.Add(m);
            Expect("{");
            void Body(bool inOneof)
            {
                while (Peek() != "}")
                {
                    string x = Next();
                    switch (x)
                    {
                        case ";": continue;
                        case "message" when !inOneof: Message(m.FullName); continue;
                        case "enum" when !inOneof: Enum(m.FullName); continue;
                        case "option": case "reserved": case "extensions": SkipStatement(); continue;
                        case "extend": SkipStatement(); continue;
                        case "oneof": Next(); Expect("{"); Body(true); Next(); continue;
                    }
                    bool repeated = false;
                    if (x is "repeated" or "optional" or "required") { repeated = x == "repeated"; x = Next(); }
                    if (x == "group") { SkipStatement(); continue; }   // proto2's groups
                    if (x == "map")
                    {
                        Expect("<"); string key = Next(); Expect(","); string value = Next(); Expect(">");
                        string mapName = Next(); Expect("=");
                        int mapNumber = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                        string? mapJson = Options();
                        Expect(";");
                        m.Fields.Add(new RawField(mapName, "", mapNumber, true, mapJson, key, value));
                        continue;
                    }
                    string name = Next();
                    Expect("=");
                    int number = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    string? json = Options();
                    Expect(";");
                    m.Fields.Add(new RawField(name, x, number, repeated, json, null, null));
                }
            }
            Body(false);
            Next();
        }

        void Service(string scope)
        {
            string full = Join(scope, Next());
            var methods = new List<(string, string, string, bool, bool)>();
            Expect("{");
            while (Peek() != "}")
            {
                string x = Next();
                if (x is "option") { SkipStatement(); continue; }
                if (x == ";") continue;
                if (x != "rpc") throw new FormatException($"rpc expected in service {full}, not {x}");
                string name = Next();
                Expect("(");
                bool cs = Peek() == "stream" && t[i + 1] != ")"; if (cs) Next();
                string input = Next(); Expect(")");
                Expect("returns");
                Expect("(");
                bool ss = Peek() == "stream" && t[i + 1] != ")"; if (ss) Next();
                string output = Next(); Expect(")");
                if (Peek() == "{") SkipStatement(); else Expect(";");
                methods.Add((name, input, output, cs, ss));
            }
            Next();
            parsed.Services.Add((full, methods));
        }

        while (i < t.Count)
        {
            string x = Next();
            switch (x)
            {
                case "syntax": case "edition": case "import": case "option": SkipStatement(); break;
                case "package": parsed.Package = Next(); Expect(";"); break;
                case "message": Message(parsed.Package); break;
                case "enum": Enum(parsed.Package); break;
                case "service": Service(parsed.Package); break;
                case "extend": SkipStatement(); break;
                case ";": break;
                default: throw new FormatException($"what is {x} here?");
            }
        }
        return parsed;
    }

    static string Join(string scope, string name) => scope.Length == 0 ? name : scope + "." + name;

    // ------------------------------------------------------------ names to types

    static void Fill(Protobuf.Pool pool, List<Parsed> files)
    {
        var messages = files.SelectMany(f => f.Messages).GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.Last());
        var enums = files.SelectMany(f => f.Enums).GroupBy(e => e.FullName).ToDictionary(g => g.Key, g => g.Last());
        foreach (var e in enums.Values) pool.Enums[e.FullName] = e;

        // a type's full name: from the scope it is named in outwards (Outer.Inner.T, Outer.T, package.T, T), or absolute with a leading dot
        (int Type, string? Name) Resolve(string scope, string name)
        {
            if (Scalars.TryGetValue(name, out int scalar)) return (scalar, null);
            if (name.StartsWith('.')) { string abs = name[1..]; return (enums.ContainsKey(abs) ? TEnum : TMessage, abs); }
            for (string s = scope; ; s = s.Contains('.') ? s[..s.LastIndexOf('.')] : "")
            {
                string candidate = Join(s, name);
                if (messages.ContainsKey(candidate)) return (TMessage, candidate);
                if (enums.ContainsKey(candidate)) return (TEnum, candidate);
                if (s.Length == 0) break;
            }
            // google.protobuf's types (their own JSON forms), or a message of a file not in the project
            return (TMessage, name);
        }

        foreach (var raw in messages.Values)
        {
            var m = new Protobuf.Message { FullName = raw.FullName };
            foreach (var f in raw.Fields)
            {
                var field = new Protobuf.Field { Name = f.Name, Number = f.Number, Label = f.Repeated ? 3 : 1, JsonName = f.JsonName ?? CamelCase(f.Name) };
                if (f.MapKey != null)
                {
                    // map<K, V> is a repeated message of its own: NameEntry { K key = 1; V value = 2; }
                    string entryName = raw.FullName + "." + Pascal(f.Name) + "Entry";
                    var entry = new Protobuf.Message { FullName = entryName, MapEntry = true };
                    var (kt, kn) = Resolve(raw.FullName, f.MapKey);
                    var (vt, vn) = Resolve(raw.FullName, f.MapValue!);
                    entry.Fields.Add(new Protobuf.Field { Name = "key", JsonName = "key", Number = 1, Label = 1, Type = kt, TypeName = kn });
                    entry.Fields.Add(new Protobuf.Field { Name = "value", JsonName = "value", Number = 2, Label = 1, Type = vt, TypeName = vn });
                    pool.Messages[entryName] = entry;
                    field.Type = TMessage;
                    field.TypeName = entryName;
                }
                else
                {
                    var (type, typeName) = Resolve(raw.FullName, f.Type);
                    field.Type = type;
                    field.TypeName = typeName;
                }
                m.Fields.Add(field);
            }
            pool.Messages[m.FullName] = m;
        }
        foreach (var file in files)
            foreach (var (full, methods) in file.Services)
            {
                string scope = full.Contains('.') ? full[..full.LastIndexOf('.')] : "";
                var s = new Protobuf.Service { FullName = full };
                foreach (var (name, input, output, cs, ss) in methods)
                    s.Methods.Add(new Protobuf.Method(name, Resolve(scope, input).Name ?? input, Resolve(scope, output).Name ?? output, cs, ss));
                pool.Services[full] = s;
            }
    }

    static string CamelCase(string name)
    {
        var sb = new StringBuilder();
        bool up = false;
        foreach (char c in name)
        {
            if (c == '_') { up = true; continue; }
            sb.Append(up ? char.ToUpperInvariant(c) : c);
            up = false;
        }
        return sb.ToString();
    }

    static string Pascal(string name) { string c = CamelCase(name); return c.Length == 0 ? c : char.ToUpperInvariant(c[0]) + c[1..]; }
}
