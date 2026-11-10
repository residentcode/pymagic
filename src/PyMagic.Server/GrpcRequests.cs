using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// The HTTP client's GRPC requests (PyCharm's format, <c>GRPC localhost:50051/package.Service/Method</c> with the request message as JSON):
/// what the service's messages are is asked of the server itself through gRPC reflection (grpc.reflection.v1, else v1alpha), so no .proto file
/// is needed; the JSON is written as protobuf, sent over HTTP/2 (plain text, or TLS for grpcs://), and the answer read back to JSON with the
/// call's status, headers and trailers. Unary and server-streaming calls; a client-streaming one takes a JSON array, one message each.
/// Answers compressed with gzip or deflate are read (PyMagic says it takes them); a request is sent compressed when its grpc-encoding
/// header asks for gzip or deflate. A server without reflection is described by the project's own .proto files (ProtoFiles).
/// </summary>
public static class GrpcRequests
{
    public sealed record Result(int Status, string StatusName, string? Message, string Body, Dictionary<string, string> Headers, long Ms);

    static readonly string[] Codes = { "OK", "CANCELLED", "UNKNOWN", "INVALID_ARGUMENT", "DEADLINE_EXCEEDED", "NOT_FOUND", "ALREADY_EXISTS", "PERMISSION_DENIED",
        "RESOURCE_EXHAUSTED", "FAILED_PRECONDITION", "ABORTED", "OUT_OF_RANGE", "UNIMPLEMENTED", "INTERNAL", "UNAVAILABLE", "DATA_LOSS", "UNAUTHENTICATED" };

    public static string CodeName(int code) => code >= 0 && code < Codes.Length ? Codes[code] : code.ToString();

    static HttpClient NewClient() => new(new SocketsHttpHandler { EnableMultipleHttp2Connections = true, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>One call. <paramref name="address"/>: http://host:port or https://host:port.</summary>
    /// <exception cref="ServiceException">The server cannot be reached, offers no reflection, has no such method, or the JSON does not fit.</exception>
    /// <param name="root">The project, whose .proto files describe the service when the server offers no reflection; null for none.</param>
    public static async Task<Result> Call(string address, string service, string method, string jsonBody, IEnumerable<KeyValuePair<string, string>> metadata, string? root = null)
    {
        using var client = NewClient();
        var watch = Stopwatch.StartNew();
        var pool = await Describe(client, address, service, root);
        if (!pool.Services.TryGetValue(service, out var svc))
            throw new ServiceException($"the server has no service {service}" + (pool.Services.Count > 0 ? $" (it has {string.Join(", ", pool.Services.Keys)})" : ""));
        var m = svc.Methods.FirstOrDefault(x => x.Name == method)
            ?? throw new ServiceException($"{service} has no method {method} (it has {string.Join(", ", svc.Methods.Select(x => x.Name))})");

        // the request: one message, or for a client-streaming method a JSON array of them
        JsonNode? json;
        try { json = string.IsNullOrWhiteSpace(jsonBody) ? new JsonObject() : JsonNode.Parse(jsonBody); }
        catch (JsonException e) { throw new ServiceException("the body is not JSON: " + e.Message); }
        var messages = m.ClientStreaming && json is JsonArray many ? many.ToList() : new List<JsonNode?> { json };
        byte[] frames;
        // the request compressed as its grpc-encoding header says (gzip, deflate)
        string? encoding = metadata.FirstOrDefault(h => h.Key.Equals("grpc-encoding", StringComparison.OrdinalIgnoreCase)).Value?.Trim().ToLowerInvariant();
        if (encoding is not (null or "identity" or "gzip" or "deflate")) throw new ServiceException($"grpc-encoding {encoding}: PyMagic compresses with gzip or deflate");
        try { frames = Frames(messages.Select(x => Protobuf.Encode(pool, m.Input, x)), encoding is "gzip" or "deflate" ? encoding : null); }
        catch (FormatException e) { throw new ServiceException(e.Message); }

        var (status, message, replies, headers) = await Send(client, address, $"/{service}/{method}", frames, metadata);
        var bodies = new JsonArray();
        foreach (var r in replies)
        {
            try { bodies.Add(Protobuf.DecodeMessage(pool, m.Output, r)); }
            catch (FormatException e) { throw new ServiceException($"the answer could not be read as {m.Output}: {e.Message}"); }
        }
        var options = new JsonSerializerOptions { WriteIndented = true };
        string body = bodies.Count == 0 ? "" : m.ServerStreaming ? bodies.ToJsonString(options) : bodies[0]?.ToJsonString(options) ?? "null";
        return new Result(status, CodeName(status), message, body, headers, watch.ElapsedMilliseconds);
    }

    /// <summary>The frames of gRPC's HTTP/2 body: each message after a byte (1 when it is compressed with <paramref name="encoding"/>) and its length.</summary>
    static byte[] Frames(IEnumerable<byte[]> messages, string? encoding = null)
    {
        using var ms = new MemoryStream();
        foreach (var plain in messages)
        {
            var m = encoding == null ? plain : Compress(plain, encoding);
            ms.WriteByte(encoding == null ? (byte)0 : (byte)1);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)m.Length);
            ms.Write(len);
            ms.Write(m);
        }
        return ms.ToArray();
    }

    static byte[] Compress(byte[] data, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream z = encoding == "gzip" ? new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true)
            : new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
            z.Write(data);
        return output.ToArray();
    }

    static byte[] Decompress(byte[] data, string? encoding)
    {
        using var input = new MemoryStream(data);
        using Stream z = encoding switch
        {
            "gzip" => new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress),
            "deflate" => new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress),
            _ => throw new ServiceException($"the answer is compressed with {encoding ?? "nothing it names"}, which PyMagic does not read (gzip and deflate it does)"),
        };
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The messages of a body; a compressed one (its flag 1) read by the answer's grpc-encoding.</summary>
    static List<byte[]> Unframe(byte[] body, string? encoding = null)
    {
        var list = new List<byte[]>();
        int at = 0;
        while (at + 5 <= body.Length)
        {
            bool compressed = body[at] == 1;
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(at + 1, 4));
            if (at + 5 + len > body.Length) break;
            var data = body.AsSpan(at + 5, len).ToArray();
            list.Add(compressed ? Decompress(data, encoding) : data);
            at += 5 + len;
        }
        return list;
    }

    static async Task<(int Status, string? Message, List<byte[]> Replies, Dictionary<string, string> Headers)> Send(HttpClient client, string address, string path,
        byte[] frames, IEnumerable<KeyValuePair<string, string>> metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, address.TrimEnd('/') + path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,   // plain-text HTTP/2 (h2c) as gRPC speaks it
            Content = new ByteArrayContent(frames),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", "gzip, deflate, identity");
        foreach (var (k, v) in metadata) request.Headers.TryAddWithoutValidation(k, v);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead); }
        catch (HttpRequestException e) { throw new ServiceException($"{address}: {e.Message} (is the gRPC server running, and does it speak plain-text HTTP/2? grpcs:// for TLS)"); }
        catch (TaskCanceledException) { throw new ServiceException("no answer in 30 seconds"); }
        using (response)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            var headers = new Dictionary<string, string>();
            foreach (var h in response.Headers.Concat(response.Content.Headers)) headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.TrailingHeaders) headers[h.Key] = string.Join(", ", h.Value);
            // the status is in the trailers, or in the headers of an answer that is only trailers
            string? statusText = response.TrailingHeaders.TryGetValues("grpc-status", out var t) ? t.First() : response.Headers.TryGetValues("grpc-status", out var hs) ? hs.First() : null;
            string? message = response.TrailingHeaders.TryGetValues("grpc-message", out var tm) ? tm.First() : response.Headers.TryGetValues("grpc-message", out var hm) ? hm.First() : null;
            if (statusText == null)
            {
                if (!response.IsSuccessStatusCode) throw new ServiceException($"{address} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}: not a gRPC server here?");
                statusText = "2";   // no status: UNKNOWN, as gRPC's clients take it
            }
            string? encoding = response.Headers.TryGetValues("grpc-encoding", out var enc) ? enc.First().Trim().ToLowerInvariant() : null;
            return (int.TryParse(statusText, out var code) ? code : 2, message == null ? null : Uri.UnescapeDataString(message), Unframe(body, encoding), headers);
        }
    }

    /// <summary>What the server says of a service and of everything its messages use, through reflection.</summary>
    public static async Task<Protobuf.Pool> Describe(HttpClient client, string address, string service, string? root = null)
    {
        var pool = new Protobuf.Pool();
        string? api = null;
        foreach (var candidate in new[] { "grpc.reflection.v1.ServerReflection", "grpc.reflection.v1alpha.ServerReflection" })
        {
            var (status, message, files) = await Reflect(client, address, candidate, symbol: service, file: null);
            if (status == 12) continue;   // UNIMPLEMENTED: the other version
            if (status != 0) throw new ServiceException($"gRPC reflection answered {CodeName(status)}{(message != null ? ": " + message : "")}");
            foreach (var f in files) pool.AddFile(f);
            api = candidate;
            break;
        }
        if (api == null && root != null)
        {
            // no reflection: the project's .proto files, when one of them has the service
            var errors = new List<string>();
            var fromFiles = ProtoFiles.Pool(root, errors);
            if (fromFiles.Services.ContainsKey(service)) return fromFiles;
            throw new ServiceException($"the server offers no gRPC reflection, and no .proto file of the project has the service {service}"
                + (fromFiles.Services.Count > 0 ? $" (they have {string.Join(", ", fromFiles.Services.Keys)})" : "") + (errors.Count > 0 ? "; " + string.Join("; ", errors) : "")
                + ": add its .proto file to the project, or switch reflection on in the server (grpcio-reflection, AddGrpcReflection, ...)");
        }
        if (api == null)
            throw new ServiceException("the server offers no gRPC reflection (grpc.reflection), which PyMagic asks what the service's messages are: switch it on in the server (grpcio-reflection, AddGrpcReflection, ...)");
        // the files they import, which a server may leave out of its first answer
        for (int round = 0; round < 20 && pool.Missing.Count > 0; round++)
            foreach (var name in pool.Missing.ToList())
            {
                var (status, _, files) = await Reflect(client, address, api, symbol: null, file: name);
                pool.Missing.Remove(name);
                if (status == 0) foreach (var f in files) pool.AddFile(f);
            }
        return pool;
    }

    static async Task<(int Status, string? Message, List<byte[]> Files)> Reflect(HttpClient client, string address, string api, string? symbol, string? file)
    {
        // ServerReflectionRequest: host = 1, file_by_filename = 3, file_containing_symbol = 4
        using var ms = new MemoryStream();
        void Text(int number, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            ms.WriteByte((byte)((number << 3) | 2));
            var len = bytes.Length;
            while (len >= 0x80) { ms.WriteByte((byte)(len | 0x80)); len >>= 7; }
            ms.WriteByte((byte)len);
            ms.Write(bytes);
        }
        if (symbol != null) Text(4, symbol);
        if (file != null) Text(3, file);
        var (status, message, replies, _) = await Send(client, address, $"/{api}/ServerReflectionInfo", Frames(new[] { ms.ToArray() }), Array.Empty<KeyValuePair<string, string>>());
        var files = new List<byte[]>();
        foreach (var reply in replies)
            foreach (var (number, _, _, data) in Protobuf.Fields(reply))
            {
                if (number == 4)   // file_descriptor_response: repeated bytes file_descriptor_proto = 1
                    files.AddRange(Protobuf.Fields(data!).Where(x => x.Number == 1).Select(x => x.Data!));
                else if (number == 7)   // error_response: error_code = 1, error_message = 2
                {
                    var err = Protobuf.Fields(data!).ToList();
                    int code = (int)(err.FirstOrDefault(x => x.Number == 1).Value);
                    string text = err.FirstOrDefault(x => x.Number == 2).Data is { } d ? System.Text.Encoding.UTF8.GetString(d) : "";
                    return (code == 0 ? 5 : code, text.Length > 0 ? text : $"the server does not know {symbol ?? file}", files);
                }
            }
        return (status, message, files);
    }
}
