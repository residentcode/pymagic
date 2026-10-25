using System.Text;

namespace PyMagic.Server;

/// <summary>Writes files as a tar stream (the POSIX "ustar" format, with GNU long-name entries for names over 100 bytes) that any tar reads.</summary>
public static class TarWriter
{
    const int Block = 512;

    /// <summary>Writes the files (paths relative to <paramref name="root"/>, with forward slashes) to the stream, then the end-of-archive blocks.</summary>
    public static void Write(Stream output, string root, IEnumerable<string> files)
    {
        foreach (var rel in files)
        {
            var info = new FileInfo(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists) continue;
            byte[] name = Encoding.UTF8.GetBytes(rel);
            long size = info.Length;
            long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            using var file = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            bool script = false;
            if (size >= 2)
            {
                var head = new byte[2];
                script = file.Read(head, 0, 2) == 2 && head[0] == '#' && head[1] == '!';
                file.Position = 0;
            }

            if (name.Length > 100)
            {
                // a GNU long name: an entry of its own that carries the name as its data
                output.Write(Header(Encoding.ASCII.GetBytes("././@LongLink"), name.Length + 1, 0, 0, 'L'));
                output.Write(name);
                output.WriteByte(0);
                Pad(output, name.Length + 1);
            }
            output.Write(Header(name.Length > 100 ? name[..100] : name, size, mtime, script ? 0x1ED : 0x1A4, '0'));

            // exactly `size` bytes: a file that changes while it is read must not shift the entries after it
            var buffer = new byte[81920];
            long left = size;
            while (left > 0)
            {
                int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) break;
                output.Write(buffer, 0, n);
                left -= n;
            }
            if (left > 0) output.Write(new byte[left]);
            Pad(output, size);
        }
        output.Write(new byte[Block * 2]);
        output.Flush();
    }

    static void Pad(Stream output, long length)
    {
        int rest = (int)(length % Block);
        if (rest != 0) output.Write(new byte[Block - rest]);
    }

    static byte[] Header(byte[] name, long size, long mtime, int mode, char type)
    {
        var h = new byte[Block];
        name.CopyTo(h, 0);
        Octal(h, 100, 8, mode);
        Octal(h, 108, 8, 0);          // uid
        Octal(h, 116, 8, 0);          // gid
        Octal(h, 124, 12, size);
        Octal(h, 136, 12, mtime);
        for (int i = 148; i < 156; i++) h[i] = (byte)' ';      // the checksum is counted with blanks in its place
        h[156] = (byte)type;
        Encoding.ASCII.GetBytes("ustar").CopyTo(h, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(h, 263);
        long sum = 0;
        foreach (var b in h) sum += b;
        Octal(h, 148, 7, sum);
        h[155] = (byte)' ';
        return h;
    }

    /// <summary>A number as octal digits filling the field but its last byte, which is the terminating NUL.</summary>
    static void Octal(byte[] block, int at, int width, long value)
    {
        string digits = Convert.ToString(value, 8).PadLeft(width - 1, '0');
        Encoding.ASCII.GetBytes(digits.Length > width - 1 ? digits[^(width - 1)..] : digits).CopyTo(block, at);
        block[at + width - 1] = 0;
    }
}
