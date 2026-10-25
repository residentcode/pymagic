using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The tar stream the project is sent in: read back by an independent tar (Windows' own), every file must come out byte for byte.</summary>
public class TarWriterTests
{
    static readonly string Tar = Path.Combine(Environment.SystemDirectory, "tar.exe");

    static string Make(Dictionary<string, byte[]> files)
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_tarw_src_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, bytes) in files)
        {
            string full = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return src;
    }

    static string RoundTrip(string src, IEnumerable<string> names)
    {
        string archive = Path.Combine(Path.GetTempPath(), "pm_tarw_" + Guid.NewGuid().ToString("N") + ".tar");
        using (var f = File.Create(archive)) TarWriter.Write(f, src, names);
        string dest = Path.Combine(Path.GetTempPath(), "pm_tarw_dst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        var psi = new ProcessStartInfo(Tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-x", "-f", archive, "-C", dest }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "tar failed: " + err);
        File.Delete(archive);
        return dest;
    }

    static byte[] Pseudo(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void EveryFileComesBackByteForByte()
    {
        if (!File.Exists(Tar)) return;
        string longName = "deep/" + string.Join("/", Enumerable.Range(0, 12).Select(i => "folder_number_" + i)) + "/a_rather_long_file_name_to_pass_one_hundred_bytes.py";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(longName) > 100);
        var files = new Dictionary<string, byte[]>
        {
            ["main.py"] = System.Text.Encoding.UTF8.GetBytes("print('héllo')\r\n"),
            ["empty.txt"] = new byte[0],
            ["exact_block.bin"] = Pseudo(512, 1),
            ["one_over.bin"] = Pseudo(513, 2),
            ["big.bin"] = Pseudo(300_000, 3),
            ["dir with space/plain name.txt"] = System.Text.Encoding.UTF8.GetBytes("odd name\n"),
            [longName] = Pseudo(1000, 4),
        };
        string src = Make(files);
        string dest = RoundTrip(src, files.Keys);
        foreach (var (rel, bytes) in files)
        {
            string got = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(got), $"'{rel}' did not come out");
            Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(got)), $"'{rel}' differs");
        }
    }

    [Fact]
    public void NamesAreWrittenAsUtf8BytesForTheTarOfTheOtherMachine()
    {
        string src = Make(new() { ["ü ñ 日本.txt"] = new byte[] { 1 } });
        var stream = new MemoryStream();
        TarWriter.Write(stream, src, new[] { "ü ñ 日本.txt" });
        var bytes = stream.ToArray();
        var name = System.Text.Encoding.UTF8.GetBytes("ü ñ 日本.txt");
        Assert.True(bytes.AsSpan(0, name.Length).SequenceEqual(name), "the first header does not start with the UTF-8 name");
        Assert.Equal(0, bytes.Length % 512);
    }

    [Fact]
    public void ModificationTimesSurvive()
    {
        if (!File.Exists(Tar)) return;
        string src = Make(new() { ["a.txt"] = new byte[] { 1, 2, 3 } });
        var when = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), when);
        string dest = RoundTrip(src, new[] { "a.txt" });
        Assert.Equal(when, File.GetLastWriteTimeUtc(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void AFileThatIsNotThereIsSkippedAndTheRestStillComes()
    {
        if (!File.Exists(Tar)) return;
        string src = Make(new() { ["a.txt"] = new byte[] { 7 }, ["b.txt"] = new byte[] { 8 } });
        string dest = RoundTrip(src, new[] { "a.txt", "gone.txt", "b.txt" });
        Assert.True(new byte[] { 7 }.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dest, "a.txt"))));
        Assert.True(new byte[] { 8 }.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dest, "b.txt"))));
        Assert.True(!File.Exists(Path.Combine(dest, "gone.txt")));
    }

    [Fact]
    public void NothingToSendIsAnEmptyButValidArchive()
    {
        if (!File.Exists(Tar)) return;
        string src = Make(new() { ["a.txt"] = new byte[] { 1 } });
        string dest = RoundTrip(src, new string[0]);
        Assert.Equal(0, Directory.GetFileSystemEntries(dest).Length);
    }
}
