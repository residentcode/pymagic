using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// Just enough of Protocol Buffers for the HTTP client's GRPC requests, with no .proto file and no package: the descriptors a server gives
/// through gRPC reflection (FileDescriptorProto, read here field by field) and a message written from JSON and read back to JSON, by the
/// proto3 JSON mapping (int64 as text, bytes as base64, enums by name, maps as objects). Well-known types (Timestamp, wrappers, Struct) are
/// written as the plain messages they are, not in their special JSON forms.
/// </summary>
public static class Protobuf
{
    public sealed class Field
    {
        public string Name = "", JsonName = "";
        public int Number, Label, Type;
        public string? TypeName;
        public bool Repeated => Label == 3;
    }

    public sealed class Message
    {
        public string FullName = "";
        public List<Field> Fields = new();
        public bool MapEntry;
    }

    public sealed class Enum
    {
        public string FullName = "";
        public Dictionary<int, string> Names = new();
        public Dictionary<string, int> Numbers = new();
    }

    public sealed record Method(string Name, string Input, string Output, bool ClientStreaming, bool ServerStreaming);

    public sealed class Service
    {
        public string FullName = "";
        public List<Method> Methods = new();
    }

    /// <summary>The messages, enums and services of the files read, by full name (no leading dot).</summary>
    public sealed class Pool
    {
        public readonly Dictionary<string, Message> Messages = new();
        public readonly Dictionary<string, Enum> Enums = new();
        public readonly Dictionary<string, Service> Services = new();
        public readonly HashSet<string> Files = new();
        /// <summary>The files the files read import, which are not read yet.</summary>
        public readonly HashSet<string> Missing = new();

        /// <summary>Reads a FileDescriptorProto.</summary>
        public void AddFile(byte[] bytes)
        {
            string name = "", package = "";
            var deps = new List<string>();
            var messages = new List<byte[]>();
            var enums = new List<byte[]>();
            var services = new List<byte[]>();
            foreach (var (number, _, value, data) in Fields(bytes))
                switch (number)
                {
                    case 1: name = Text(data); break;
                    case 2: package = Text(data); break;
                    case 3: deps.Add(Text(data)); break;
                    case 4: messages.Add(data!); break;
                    case 5: enums.Add(data!); break;
                    case 6: services.Add(data!); break;
                }
            Files.Add(name);
            Missing.Remove(name);
            foreach (var d in deps) if (!Files.Contains(d)) Missing.Add(d);
            string prefix = package.Length > 0 ? package + "." : "";
            foreach (var m in messages) AddMessage(m, prefix);
            foreach (var e in enums) AddEnum(e, prefix);
            foreach (var s in services) AddService(s, prefix);
        }

        void AddMessage(byte[] bytes, string prefix)
        {
            var m = new Message();
            var nested = new List<byte[]>();
            var nestedEnums = new List<byte[]>();
            foreach (var (number, _, _, data) in Fields(bytes))
                switch (number)
                {
                    case 1: m.FullName = prefix + Text(data); break;
                    case 2: m.Fields.Add(ReadField(data!)); break;
                    case 3: nested.Add(data!); break;
                    case 4: nestedEnums.Add(data!); break;
                    case 7: m.MapEntry = Fields(data!).Any(f => f.Number == 7 && f.Value == 1); break;   // MessageOptions.map_entry
                }
            Messages[m.FullName] = m;
            foreach (var n in nested) AddMessage(n, m.FullName + ".");
            foreach (var e in nestedEnums) AddEnum(e, m.FullName + ".");
        }

        static Field ReadField(byte[] bytes)
        {
            var f = new Field();
            foreach (var (number, _, value, data) in Fields(bytes))
                switch (number)
                {
                    case 1: f.Name = Text(data); break;
                    case 3: f.Number = (int)value; break;
                    case 4: f.Label = (int)value; break;
                    case 5: f.Type = (int)value; break;
                    case 6: f.TypeName = Text(data).TrimStart('.'); break;
                    case 10: f.JsonName = Text(data); break;
                }
            if (f.JsonName.Length == 0) f.JsonName = CamelCase(f.Name);
            return f;
        }

        void AddEnum(byte[] bytes, string prefix)
        {
            var e = new Enum();
            foreach (var (number, _, _, data) in Fields(bytes))
            {
                if (number == 1) e.FullName = prefix + Text(data);
                else if (number == 2)
                {
                    string vName = "";
                    int vNumber = 0;
                    foreach (var (n, _, v, d) in Fields(data!))
                    {
                        if (n == 1) vName = Text(d);
                        else if (n == 2) vNumber = (int)v;
                    }
                    e.Names.TryAdd(vNumber, vName);
                    e.Numbers[vName] = vNumber;
                }
            }
            Enums[e.FullName] = e;
        }

        void AddService(byte[] bytes, string prefix)
        {
            var s = new Service();
            foreach (var (number, _, _, data) in Fields(bytes))
            {
                if (number == 1) s.FullName = prefix + Text(data);
                else if (number == 2)
                {
                    string name = "", input = "", output = "";
                    bool cs = false, ss = false;
                    foreach (var (n, _, v, d) in Fields(data!))
                        switch (n)
                        {
                            case 1: name = Text(d); break;
                            case 2: input = Text(d).TrimStart('.'); break;
                            case 3: output = Text(d).TrimStart('.'); break;
                            case 5: cs = v != 0; break;
                            case 6: ss = v != 0; break;
                        }
                    s.Methods.Add(new Method(name, input, output, cs, ss));
                }
            }
            Services[s.FullName] = s;
        }
    }

    static string Text(byte[]? data) => data == null ? "" : Encoding.UTF8.GetString(data);

    /// <summary>protoc's json_name: lower_case to lowerCase.</summary>
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

    // ------------------------------------------------------------ the wire

    /// <summary>The fields of a message as they are on the wire: its number, wire type, the value of a varint or fixed field, the bytes of a length-delimited one.</summary>
    public static IEnumerable<(int Number, int Wire, ulong Value, byte[]? Data)> Fields(byte[] bytes)
    {
        int at = 0;
        while (at < bytes.Length)
        {
            ulong key = Varint(bytes, ref at);
            int number = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0: yield return (number, wire, Varint(bytes, ref at), null); break;
                case 1: Need(bytes, at, 8); yield return (number, wire, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8)), null); at += 8; break;
                case 5: Need(bytes, at, 4); yield return (number, wire, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4)), null); at += 4; break;
                case 2:
                    int len = checked((int)Varint(bytes, ref at));
                    Need(bytes, at, len);
                    yield return (number, wire, 0, bytes.AsSpan(at, len).ToArray());
                    at += len;
                    break;
                default: throw new FormatException($"wire type {wire} of field {number} is not one of proto3");
            }
        }
    }

    static void Need(byte[] bytes, int at, int n) { if (at + n > bytes.Length) throw new FormatException("the message ends early"); }

    static ulong Varint(byte[] bytes, ref int at)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (at >= bytes.Length) throw new FormatException("the message ends early");
            byte b = bytes[at++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("a varint longer than 10 bytes");
    }

    static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    static void WriteKey(Stream s, int number, int wire) => WriteVarint(s, ((ulong)number << 3) | (uint)wire);

    static void WriteBytes(Stream s, int number, byte[] data)
    {
        WriteKey(s, number, 2);
        WriteVarint(s, (ulong)data.Length);
        s.Write(data);
    }

    // field types of FieldDescriptorProto.Type
    const int TDouble = 1, TFloat = 2, TInt64 = 3, TUint64 = 4, TInt32 = 5, TFixed64 = 6, TFixed32 = 7, TBool = 8, TString = 9, TGroup = 10,
        TMessage = 11, TBytes = 12, TUint32 = 13, TEnum = 14, TSfixed32 = 15, TSfixed64 = 16, TSint32 = 17, TSint64 = 18;

    static int WireOf(int type) => type switch
    {
        TDouble or TFixed64 or TSfixed64 => 1,
        TFloat or TFixed32 or TSfixed32 => 5,
        TString or TBytes or TMessage => 2,
        _ => 0,
    };

    // ------------------------------------------------------------ JSON to a message

    /// <summary>A message written from its JSON (an object); a field the message does not have is an error, as are values of the wrong kind.</summary>
    /// <exception cref="FormatException">The JSON does not fit the message.</exception>
    public static byte[] Encode(Pool pool, string messageName, JsonNode? json)
    {
        if (!pool.Messages.TryGetValue(messageName, out var message)) throw new FormatException($"the server did not describe the message {messageName}");
        using var ms = new MemoryStream();
        if (json is null) return Array.Empty<byte>();
        if (json is not JsonObject obj) throw new FormatException($"{Short(messageName)} is written as a JSON object");
        foreach (var (key, value) in obj)
        {
            var field = message.Fields.FirstOrDefault(f => f.JsonName == key) ?? message.Fields.FirstOrDefault(f => f.Name == key)
                ?? throw new FormatException($"{Short(messageName)} has no field {key}" + (message.Fields.Count > 0 ? $" (it has {string.Join(", ", message.Fields.Select(f => f.JsonName))})" : ""));
            if (value is null) continue;
            WriteField(ms, pool, field, value, messageName);
        }
        return ms.ToArray();
    }

    static string Short(string full) => full.Contains('.') ? full[(full.LastIndexOf('.') + 1)..] : full;

    static void WriteField(Stream s, Pool pool, Field f, JsonNode value, string owner)
    {
        string where = $"{Short(owner)}.{f.JsonName}";
        if (f.Type == TMessage && f.TypeName != null && pool.Messages.TryGetValue(f.TypeName, out var entry) && entry.MapEntry)
        {
            if (value is not JsonObject map) throw new FormatException($"{where} is a map: a JSON object");
            var keyField = entry.Fields.First(x => x.Number == 1);
            var valueField = entry.Fields.First(x => x.Number == 2);
            foreach (var (k, v) in map)
            {
                using var e = new MemoryStream();
                WriteField(e, pool, keyField, keyField.Type == TBool ? JsonValue.Create(k == "true") : keyField.Type == TString ? JsonValue.Create(k) : JsonValue.Create(k), entry.FullName);
                if (v != null) WriteField(e, pool, valueField, v, entry.FullName);
                WriteBytes(s, f.Number, e.ToArray());
            }
            return;
        }
        if (f.Repeated)
        {
            if (value is not JsonArray items) throw new FormatException($"{where} is repeated: a JSON array");
            int wire = WireOf(f.Type);
            if (wire != 2)
            {
                // packed, as proto3 writes repeated numbers
                using var packed = new MemoryStream();
                foreach (var item in items) if (item != null) WriteScalar(packed, pool, f, item, where, withKey: false);
                WriteBytes(s, f.Number, packed.ToArray());
            }
            else foreach (var item in items) if (item != null) WriteOne(s, pool, f, item, where);
            return;
        }
        WriteOne(s, pool, f, value, where);
    }

    static void WriteOne(Stream s, Pool pool, Field f, JsonNode value, string where)
    {
        switch (f.Type)
        {
            case TMessage: WriteBytes(s, f.Number, Encode(pool, f.TypeName ?? "", value)); break;
            case TString: WriteBytes(s, f.Number, Encoding.UTF8.GetBytes(StringOf(value, where))); break;
            case TBytes:
                try { WriteBytes(s, f.Number, Convert.FromBase64String(StringOf(value, where))); }
                catch (FormatException) { throw new FormatException($"{where} is bytes: base64 text"); }
                break;
            case TGroup: throw new FormatException($"{where} is a group, which proto3 does not have");
            default: WriteScalar(s, pool, f, value, where, withKey: true); break;
        }
    }

    static string StringOf(JsonNode value, string where) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new FormatException($"{where} is text: a JSON string");

    static void WriteScalar(Stream s, Pool pool, Field f, JsonNode value, string where, bool withKey)
    {
        if (withKey) WriteKey(s, f.Number, WireOf(f.Type));
        switch (f.Type)
        {
            case TBool:
                if (value is not JsonValue bv || !bv.TryGetValue<bool>(out var b)) throw new FormatException($"{where} is true or false");
                WriteVarint(s, b ? 1UL : 0UL);
                break;
            case TEnum:
                int number;
                if (value is JsonValue ev && ev.TryGetValue<string>(out var name))
                {
                    if (f.TypeName == null || !pool.Enums.TryGetValue(f.TypeName, out var en) || !en.Numbers.TryGetValue(name, out number))
                        throw new FormatException($"{where} has no value {name}" + (f.TypeName != null && pool.Enums.TryGetValue(f.TypeName, out var en2) ? $" (it has {string.Join(", ", en2.Numbers.Keys)})" : ""));
                }
                else number = (int)Integer(value, where);
                WriteVarint(s, (ulong)(long)number);
                break;
            case TDouble: Span<byte> d = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(d, Real(value, where)); s.Write(d); break;
            case TFloat: Span<byte> fl = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(fl, (float)Real(value, where)); s.Write(fl); break;
            case TFixed64: Span<byte> f64 = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(f64, (ulong)Integer(value, where, unsigned: true)); s.Write(f64); break;
            case TSfixed64: Span<byte> s64 = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(s64, (long)Integer(value, where)); s.Write(s64); break;
            case TFixed32: Span<byte> f32 = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(f32, (uint)Integer(value, where, unsigned: true)); s.Write(f32); break;
            case TSfixed32: Span<byte> s32 = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s32, (int)Integer(value, where)); s.Write(s32); break;
            case TSint32: { long v = (int)Integer(value, where); WriteVarint(s, (ulong)((v << 1) ^ (v >> 63))); break; }
            case TSint64: { long v = (long)Integer(value, where); WriteVarint(s, (ulong)((v << 1) ^ (v >> 63))); break; }
            case TUint32: case TUint64: WriteVarint(s, (ulong)Integer(value, where, unsigned: true)); break;
            case TInt32: WriteVarint(s, (ulong)(long)(int)Integer(value, where)); break;
            default: WriteVarint(s, (ulong)(long)Integer(value, where)); break;   // int64
        }
    }

    /// <summary>A whole number from JSON: a number, or text (as proto3 writes 64-bit numbers).</summary>
    static decimal Integer(JsonNode value, string where, bool unsigned = false)
    {
        decimal d;
        if (value is JsonValue v && v.TryGetValue<decimal>(out var n)) d = n;
        else if (value is JsonValue t && t.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) d = p;
        else throw new FormatException($"{where} is a whole number");
        if (d != decimal.Truncate(d) || (unsigned && d < 0)) throw new FormatException($"{where} is a whole number{(unsigned ? " not below 0" : "")}");
        return d;
    }

    static double Real(JsonNode value, string where)
    {
        if (value is JsonValue v && v.TryGetValue<double>(out var n)) return n;
        if (value is JsonValue t && t.TryGetValue<string>(out var text))
            return text switch { "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity, _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : throw new FormatException($"{where} is a number") };
        throw new FormatException($"{where} is a number");
    }

    // ------------------------------------------------------------ a message to JSON

    /// <summary>A message read to JSON; fields it does not know are left out, fields not set are not written (proto3's defaults).</summary>
    public static JsonObject Decode(Pool pool, string messageName, byte[] bytes)
    {
        var obj = new JsonObject();
        pool.Messages.TryGetValue(messageName, out var message);
        foreach (var (number, wire, value, data) in Fields(bytes))
        {
            var f = message?.Fields.FirstOrDefault(x => x.Number == number);
            if (f == null) continue;
            if (f.Type == TMessage && f.TypeName != null && pool.Messages.TryGetValue(f.TypeName, out var entry) && entry.MapEntry)
            {
                var map = obj[f.JsonName] as JsonObject ?? new JsonObject();
                obj[f.JsonName] = map;
                var kv = Decode(pool, entry.FullName, data!);
                var keyField = entry.Fields.First(x => x.Number == 1);
                var valueField = entry.Fields.First(x => x.Number == 2);
                string key = kv[keyField.JsonName]?.ToString() ?? (keyField.Type == TBool ? "false" : keyField.Type == TString ? "" : "0");
                map[key] = kv[valueField.JsonName]?.DeepClone() ?? DefaultOf(pool, valueField);
                continue;
            }
            if (f.Repeated)
            {
                var list = obj[f.JsonName] as JsonArray ?? new JsonArray();
                obj[f.JsonName] = list;
                if (wire == 2 && WireOf(f.Type) != 2)
                {
                    // packed numbers
                    int at = 0;
                    while (at < data!.Length) list.Add(ReadPacked(pool, f, data, ref at));
                }
                else list.Add(ValueOf(pool, f, wire, value, data));
                continue;
            }
            obj[f.JsonName] = ValueOf(pool, f, wire, value, data);
        }
        return obj;
    }

    static JsonNode DefaultOf(Pool pool, Field f) => f.Type switch
    {
        TString or TBytes => JsonValue.Create(""),
        TBool => JsonValue.Create(false),
        TMessage => new JsonObject(),
        TEnum => JsonValue.Create(f.TypeName != null && pool.Enums.TryGetValue(f.TypeName, out var e) && e.Names.TryGetValue(0, out var n) ? n : "0"),
        TInt64 or TUint64 or TFixed64 or TSfixed64 or TSint64 => JsonValue.Create("0"),
        _ => JsonValue.Create(0),
    };

    static JsonNode ReadPacked(Pool pool, Field f, byte[] data, ref int at)
    {
        switch (WireOf(f.Type))
        {
            case 1: Need(data, at, 8); { var v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at, 8)); at += 8; return ValueOf(pool, f, 1, v, null)!; }
            case 5: Need(data, at, 4); { var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4)); at += 4; return ValueOf(pool, f, 5, v, null)!; }
            default: return ValueOf(pool, f, 0, Varint(data, ref at), null)!;
        }
    }

    static JsonNode? ValueOf(Pool pool, Field f, int wire, ulong value, byte[]? data)
    {
        switch (f.Type)
        {
            case TMessage: return Decode(pool, f.TypeName ?? "", data ?? Array.Empty<byte>());
            case TString: return JsonValue.Create(Text(data));
            case TBytes: return JsonValue.Create(Convert.ToBase64String(data ?? Array.Empty<byte>()));
            case TBool: return JsonValue.Create(value != 0);
            case TEnum:
                int n = (int)(long)value;
                return f.TypeName != null && pool.Enums.TryGetValue(f.TypeName, out var e) && e.Names.TryGetValue(n, out var name) ? JsonValue.Create(name) : JsonValue.Create(n);
            case TDouble: return Real(BitConverter.Int64BitsToDouble((long)value));
            case TFloat: return Real(BitConverter.Int32BitsToSingle((int)(uint)value));
            case TInt32: return JsonValue.Create((int)(long)value);
            case TSfixed32: return JsonValue.Create((int)(uint)value);
            case TUint32: case TFixed32: return JsonValue.Create((uint)value);
            case TSint32: return JsonValue.Create((int)((uint)value >> 1) ^ -(int)((uint)value & 1));
            case TSint64: return JsonValue.Create((((long)(value >> 1)) ^ -(long)(value & 1)).ToString(CultureInfo.InvariantCulture));
            case TUint64: case TFixed64: return JsonValue.Create(value.ToString(CultureInfo.InvariantCulture));
            default: return JsonValue.Create(((long)value).ToString(CultureInfo.InvariantCulture));   // int64, sfixed64
        }
    }

    static JsonNode Real(double d) => double.IsNaN(d) ? JsonValue.Create("NaN") : double.IsPositiveInfinity(d) ? JsonValue.Create("Infinity") : double.IsNegativeInfinity(d) ? JsonValue.Create("-Infinity") : JsonValue.Create(d);
}
