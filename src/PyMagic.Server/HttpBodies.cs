using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// The HTTP client's bodies and saved responses (the engine sends the requests of .http files): a body made of its parts, the text the
/// page filled in and the files its <c>&lt; path</c> lines name, sent as their bytes (a multipart body's file part, an image as a body);
/// and the response written to the file <c>&gt;&gt; file</c> names (a new name when it is taken, unless <c>&gt;&gt;!</c>).
/// </summary>
public static class HttpBodies
{
    /// <summary>The bytes of a body: <c>[{text}, {file}]</c>, a text as UTF-8, a file as it is on disk. <paramref name="resolve"/> gives a file's full path.</summary>
    /// <exception cref="ServiceException">A file is not there.</exception>
    public static byte[] Assemble(JsonArray parts, Func<string, string> resolve)
    {
        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            if (part?["text"]?.GetValue<string>() is { } text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                ms.Write(bytes);
            }
            else if (part?["file"]?.GetValue<string>() is { } file)
            {
                string full = resolve(file);
                if (!File.Exists(full)) throw new ServiceException($"The file {file} of the body is not there.");
                ms.Write(File.ReadAllBytes(full));
            }
        }
        return ms.ToArray();
    }

    /// <summary>A path that is not taken: the path itself, else name-1.ext, name-2.ext, as PyCharm names the responses it keeps.</summary>
    public static string FreeName(string full)
    {
        if (!File.Exists(full)) return full;
        string dir = Path.GetDirectoryName(full)!, stem = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full);
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(dir, $"{stem}-{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Writes a response's body to a file (its folder made), a new name unless <paramref name="overwrite"/>; the file written.</summary>
    public static string Save(string full, byte[] body, bool overwrite)
    {
        string target = overwrite ? full : FreeName(full);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, body);
        return target;
    }
}
