using System.Text;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The HTTP client's bodies of parts (files sent as their bytes) and responses kept in files (`>> file`, `>>! file`).</summary>
public class HttpBodiesTests
{
    [Fact]
    public void ABodyIsItsTextAndTheBytesOfItsFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_httpb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE };
            File.WriteAllBytes(Path.Combine(dir, "cat.png"), png);
            var parts = new JsonArray
            {
                new JsonObject { ["text"] = "--B\r\nContent-Type: image/png\r\n\r\n" },
                new JsonObject { ["file"] = "cat.png" },
                new JsonObject { ["text"] = "\r\n--B--é\r\n" },
            };
            var bytes = HttpBodies.Assemble(parts, f => Path.Combine(dir, f));
            var expected = Encoding.UTF8.GetBytes("--B\r\nContent-Type: image/png\r\n\r\n").Concat(png).Concat(Encoding.UTF8.GetBytes("\r\n--B--é\r\n")).ToArray();
            Assert.True(bytes.SequenceEqual(expected), "the image's bytes as they are, between the texts as UTF-8");
            Assert.Throws<ServiceException>(() => HttpBodies.Assemble(new JsonArray { new JsonObject { ["file"] = "missing.bin" } }, f => Path.Combine(dir, f)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AResponseIsKeptInANewFileUnlessItMayBeOverwritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_httpb_" + Guid.NewGuid().ToString("N"));
        try
        {
            string target = Path.Combine(dir, "out", "item.json");
            Assert.Equal(target, HttpBodies.Save(target, Encoding.UTF8.GetBytes("1"), overwrite: false));   // the folder is made
            Assert.Equal(Path.Combine(dir, "out", "item-1.json"), HttpBodies.Save(target, Encoding.UTF8.GetBytes("2"), overwrite: false));
            Assert.Equal(Path.Combine(dir, "out", "item-2.json"), HttpBodies.Save(target, Encoding.UTF8.GetBytes("3"), overwrite: false));
            Assert.Equal(target, HttpBodies.Save(target, Encoding.UTF8.GetBytes("4"), overwrite: true));
            Assert.Equal("4", File.ReadAllText(target));
            Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "out", "item-1.json")));
            File.WriteAllText(Path.Combine(dir, "README"), "");
            Assert.Equal(Path.Combine(dir, "README-1"), HttpBodies.FreeName(Path.Combine(dir, "README")));   // a name without an extension
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
