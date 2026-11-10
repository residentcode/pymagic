using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Protocol Buffers' well-known types in their own JSON forms (proto3's JSON mapping), whether the server described them or not:
/// Timestamp as "2024-05-01T10:00:00.5Z", Duration as "1.5s", the wrappers (Int32Value, StringValue, ...) as their bare value, Struct, Value and
/// ListValue as any JSON, FieldMask as "a.b,fooBar", Empty as {}, and Any as an object with its "@type" (and "value" for a well-known one).
/// </summary>
public static partial class Protobuf
{
    const string WellKnownPrefix = "google.protobuf.";

    static readonly Dictionary<string, int> Wrappers = new(StringComparer.Ordinal)
    {
        ["DoubleValue"] = TDouble, ["FloatValue"] = TFloat, ["Int64Value"] = TInt64, ["UInt64Value"] = TUint64, ["Int32Value"] = TInt32,
        ["UInt32Value"] = TUint32, ["BoolValue"] = TBool, ["StringValue"] = TString, ["BytesValue"] = TBytes,
    };

    /// <summary>Whether a message is a well-known type written in a JSON form of its own.</summary>
    public static bool IsWellKnown(string name) =>
        name.StartsWith(WellKnownPrefix, StringComparison.Ordinal) && name[WellKnownPrefix.Length..] is var n
        && (Wrappers.ContainsKey(n) || n is "Timestamp" or "Duration" or "Struct" or "Value" or "ListValue" or "FieldMask" or "Empty" or "Any");

    static Field Synthetic(int number, int type, string? typeName = null, int label = 1) => new() { Number = number, Type = type, TypeName = typeName, Label = label, Name = "value", JsonName = "value" };

    static byte[] EncodeWellKnown(Pool pool, string name, JsonNode? json)
    {
        string n = name[WellKnownPrefix.Length..];
        using var ms = new MemoryStream();
        if (Wrappers.TryGetValue(n, out int type))
        {
            if (json != null) WriteOne(ms, pool, Synthetic(1, type), json, n);
            return ms.ToArray();
        }
        switch (n)
        {
            case "Timestamp":
            {
                if (json is not JsonValue tv || !tv.TryGetValue<string>(out var text)) throw new FormatException("a Timestamp is written as text: \"2024-05-01T10:00:00Z\"");
                var (seconds, nanos) = ParseTimestamp(text);
                if (seconds != 0) { WriteKey(ms, 1, 0); WriteVarint(ms, (ulong)seconds); }
                if (nanos != 0) { WriteKey(ms, 2, 0); WriteVarint(ms, (ulong)(long)nanos); }
                return ms.ToArray();
            }
            case "Duration":
            {
                if (json is not JsonValue dv || !dv.TryGetValue<string>(out var text)) throw new FormatException("a Duration is written as text: \"1.5s\"");
                var m = Regex.Match(text.Trim(), @"^(-)?(\d+)(?:\.(\d{1,9}))?s$");
                if (!m.Success) throw new FormatException($"a Duration is written as seconds: \"1.5s\", not {text}");
                long seconds = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int nanos = m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0;
                if (m.Groups[1].Success) { seconds = -seconds; nanos = -nanos; }
                if (seconds != 0) { WriteKey(ms, 1, 0); WriteVarint(ms, (ulong)seconds); }
                if (nanos != 0) { WriteKey(ms, 2, 0); WriteVarint(ms, (ulong)(long)nanos); }
                return ms.ToArray();
            }
            case "Empty":
                if (json is not (null or JsonObject { Count: 0 })) throw new FormatException("an Empty is written as {}");
                return ms.ToArray();
            case "Struct":
            {
                if (json is not JsonObject obj) throw new FormatException("a Struct is written as a JSON object");
                foreach (var (k, v) in obj)
                {
                    using var entry = new MemoryStream();
                    WriteBytes(entry, 1, Encoding.UTF8.GetBytes(k));
                    WriteBytes(entry, 2, EncodeWellKnown(pool, WellKnownPrefix + "Value", v));
                    WriteBytes(ms, 1, entry.ToArray());
                }
                return ms.ToArray();
            }
            case "ListValue":
            {
                if (json is not JsonArray list) throw new FormatException("a ListValue is written as a JSON array");
                foreach (var v in list) WriteBytes(ms, 1, EncodeWellKnown(pool, WellKnownPrefix + "Value", v));
                return ms.ToArray();
            }
            case "Value":
                switch (json)
                {
                    case null: WriteKey(ms, 1, 0); WriteVarint(ms, 0); break;
                    case JsonObject: WriteBytes(ms, 5, EncodeWellKnown(pool, WellKnownPrefix + "Struct", json)); break;
                    case JsonArray: WriteBytes(ms, 6, EncodeWellKnown(pool, WellKnownPrefix + "ListValue", json)); break;
                    case JsonValue v when v.TryGetValue<string>(out var s): WriteBytes(ms, 3, Encoding.UTF8.GetBytes(s)); break;
                    case JsonValue v when v.TryGetValue<bool>(out var b): WriteKey(ms, 4, 0); WriteVarint(ms, b ? 1UL : 0UL); break;
                    default:
                        Span<byte> d = stackalloc byte[8];
                        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(d, Real(json, "Value"));
                        WriteKey(ms, 2, 1); ms.Write(d);
                        break;
                }
                return ms.ToArray();
            case "FieldMask":
            {
                if (json is not JsonValue fv || !fv.TryGetValue<string>(out var text)) throw new FormatException("a FieldMask is written as text: \"name,address.city\"");
                foreach (var path in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    WriteBytes(ms, 1, Encoding.UTF8.GetBytes(Regex.Replace(path, "([A-Z])", m => "_" + m.Value.ToLowerInvariant())));
                return ms.ToArray();
            }
            case "Any":
            {
                if (json is not JsonObject any || any["@type"]?.GetValue<string>() is not { Length: > 0 } typeUrl) throw new FormatException("an Any is written as an object with its \"@type\": \"type.googleapis.com/package.Message\"");
                string inner = typeUrl[(typeUrl.LastIndexOf('/') + 1)..];
                JsonNode? content;
                if (IsWellKnown(inner)) content = any["value"];
                else { var rest = new JsonObject(); foreach (var (k, v) in any) if (k != "@type") rest[k] = v?.DeepClone(); content = rest; }
                WriteBytes(ms, 1, Encoding.UTF8.GetBytes(typeUrl));
                WriteBytes(ms, 2, Encode(pool, inner, content));
                return ms.ToArray();
            }
        }
        throw new FormatException($"{name} is not known");
    }

    static JsonNode? DecodeWellKnown(Pool pool, string name, byte[] bytes)
    {
        string n = name[WellKnownPrefix.Length..];
        var fields = Fields(bytes).ToList();
        if (Wrappers.TryGetValue(n, out int type))
        {
            var field = Synthetic(1, type);
            var f = fields.LastOrDefault(x => x.Number == 1);
            return f.Number == 1 ? ValueOf(pool, field, f.Wire, f.Value, f.Data) : DefaultOf(pool, field);
        }
        long Long(int number) => (long)(fields.LastOrDefault(x => x.Number == number).Value);
        switch (n)
        {
            case "Timestamp": return JsonValue.Create(FormatTimestamp(Long(1), (int)Long(2)));
            case "Duration":
            {
                long seconds = Long(1);
                int nanos = (int)Long(2);
                bool negative = seconds < 0 || nanos < 0;
                string text = Math.Abs(seconds).ToString(CultureInfo.InvariantCulture) + Fraction(Math.Abs(nanos));
                return JsonValue.Create((negative ? "-" : "") + text + "s");
            }
            case "Empty": return new JsonObject();
            case "Struct":
            {
                var obj = new JsonObject();
                foreach (var entry in fields.Where(x => x.Number == 1 && x.Data != null))
                {
                    var kv = Fields(entry.Data!).ToList();
                    string key = kv.FirstOrDefault(x => x.Number == 1).Data is { } k ? Encoding.UTF8.GetString(k) : "";
                    obj[key] = kv.FirstOrDefault(x => x.Number == 2).Data is { } v ? DecodeWellKnown(pool, WellKnownPrefix + "Value", v) : null;
                }
                return obj;
            }
            case "ListValue": return new JsonArray(fields.Where(x => x.Number == 1).Select(x => DecodeWellKnown(pool, WellKnownPrefix + "Value", x.Data ?? Array.Empty<byte>())).ToArray());
            case "Value":
            {
                if (fields.Count == 0) return null;
                var f = fields[^1];
                return f.Number switch
                {
                    1 => null,
                    2 => Real(BitConverter.Int64BitsToDouble((long)f.Value)),
                    3 => JsonValue.Create(Encoding.UTF8.GetString(f.Data ?? Array.Empty<byte>())),
                    4 => JsonValue.Create(f.Value != 0),
                    5 => DecodeWellKnown(pool, WellKnownPrefix + "Struct", f.Data ?? Array.Empty<byte>()),
                    6 => DecodeWellKnown(pool, WellKnownPrefix + "ListValue", f.Data ?? Array.Empty<byte>()),
                    _ => null,
                };
            }
            case "FieldMask":
                return JsonValue.Create(string.Join(",", fields.Where(x => x.Number == 1).Select(x => CamelCase(Encoding.UTF8.GetString(x.Data ?? Array.Empty<byte>())).Replace("_", ""))));
            case "Any":
            {
                string typeUrl = fields.FirstOrDefault(x => x.Number == 1).Data is { } t ? Encoding.UTF8.GetString(t) : "";
                byte[] value = fields.FirstOrDefault(x => x.Number == 2).Data ?? Array.Empty<byte>();
                string inner = typeUrl[(typeUrl.LastIndexOf('/') + 1)..];
                var any = new JsonObject { ["@type"] = typeUrl };
                if (IsWellKnown(inner)) any["value"] = DecodeWellKnown(pool, inner, value);
                else if (pool.Messages.ContainsKey(inner) && Decode(pool, inner, value) is JsonObject decoded)
                    foreach (var (k, v) in decoded) any[k] = v?.DeepClone();
                else any["value"] = Convert.ToBase64String(value);   // a message the server did not describe
                return any;
            }
        }
        return null;
    }

    /// <summary>RFC 3339 text (Z or an offset, up to nine digits of the second) as seconds since 1970 and nanoseconds.</summary>
    static (long Seconds, int Nanos) ParseTimestamp(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(\d{4}-\d\d-\d\d[Tt ]\d\d:\d\d:\d\d)(?:\.(\d{1,9}))?([Zz]|[+-]\d\d:\d\d)$");
        if (!m.Success || !DateTimeOffset.TryParse(m.Groups[1].Value.Replace(' ', 'T') + (m.Groups[3].Value is "z" or "Z" ? "+00:00" : m.Groups[3].Value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            throw new FormatException($"a Timestamp is written as \"2024-05-01T10:00:00Z\" (RFC 3339), not {text}");
        int nanos = m.Groups[2].Success ? int.Parse(m.Groups[2].Value.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0;
        return (at.ToUnixTimeSeconds(), nanos);
    }

    static string FormatTimestamp(long seconds, int nanos) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(nanos) + "Z";

    /// <summary>The nanoseconds as JSON writes them: none, or 3, 6 or 9 digits.</summary>
    static string Fraction(int nanos)
    {
        if (nanos == 0) return "";
        string nine = nanos.ToString("D9", CultureInfo.InvariantCulture);
        return "." + (nine.EndsWith("000000") ? nine[..3] : nine.EndsWith("000") ? nine[..6] : nine);
    }
}
