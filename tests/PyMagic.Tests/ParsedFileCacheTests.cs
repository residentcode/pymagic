using System.Collections;
using System.Text;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The syntax trees of library files kept on disk between starts are the trees the parser makes.</summary>
public class ParsedFileCacheTests
{
    /// <summary>Every field of every node, nested, with its place in the text.</summary>
    static string Dump(object? value)
    {
        var sb = new StringBuilder();
        void Walk(object? v)
        {
            switch (v)
            {
                case null: sb.Append('~'); break;
                case string s: sb.Append('"').Append(s).Append('"'); break;
                case Node n:
                    sb.Append(n.GetType().Name).Append('@').Append(n.Start).Append('-').Append(n.End).Append('(');
                    foreach (var p in n.GetType().GetProperties().Where(p => p.Name is not ("Start" or "End" or "EqualityContract")).OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        sb.Append(p.Name).Append('=');
                        Walk(p.GetValue(n));
                        sb.Append(';');
                    }
                    sb.Append(')');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    foreach (var x in list) { Walk(x); sb.Append(','); }
                    sb.Append(']');
                    break;
                default: sb.Append(v); break;
            }
        }
        Walk(value);
        return sb.ToString();
    }

    static string Fresh() => Path.Combine(Path.GetTempPath(), "pm_parsed_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ATreeIsReadBackAsTheParserMadeItAndAChangedTextIsParsedAgain()
    {
        string folder = Fresh();
        var before = ParsedFileCache.Folder;
        try
        {
            ParsedFileCache.Folder = folder;
            const string text = "from typing import overload\n\n\n@overload\ndef f(x: int, /, *a: str, k: 'T' = ..., **kw) -> int: ...\n"
                + "class C[T: (int, str)](Base, metaclass=M):\n    x: list[T] = [i async for i in y if i]\n    type A = dict[str, T]\n"
                + "match v:\n    case [1, *rest] | {'k': _, **others} | P(a, b=c) as z if z:\n        pass\n"
                + "s = f\"{a!r:>{w}}\" b'x' -1j\ntry:\n    del a[1:2:3], b\nexcept* (E, F) as e:\n    raise X from e\nfinally:\n    global g\n"
                + "lambda q=1, *, r: (yield from q)\nwith (a as b, c):\n    print(not x, a if b else c, (n := 1), await z, {**d}, {1, 2}, a < b <= c)\n"
                + "def broken(:\n";
            int written = ParsedFileCache.Written, read = ParsedFileCache.Read;
            var first = ParsedFileCache.Parse(text);
            Assert.Equal(written + 1, ParsedFileCache.Written);
            var second = ParsedFileCache.Parse(text);
            Assert.Equal(read + 1, ParsedFileCache.Read);
            var parsed = Parser.Parse(text);
            Assert.Equal(Dump(parsed.Module), Dump(second.Module));
            Assert.Equal(Dump(parsed.Diagnostics), Dump(second.Diagnostics));
            Assert.True(parsed.Diagnostics.Count > 0, "the syntax error is kept too");

            ParsedFileCache.Parse(text + "x = 1\n");
            Assert.Equal(written + 2, ParsedFileCache.Written);   // another text: parsed again

            // a file that is not one of the cache's (cut short, or written by something else) is parsed instead
            foreach (var file in Directory.EnumerateFiles(folder, "*.ast", SearchOption.AllDirectories)) File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            Assert.Equal(Dump(parsed.Module), Dump(ParsedFileCache.Parse(text).Module));
        }
        finally
        {
            ParsedFileCache.Folder = before;
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void EveryTypeshedStubComesBackTheSame()
    {
        if (Workspace.DefaultTypeshedPath is not { } typeshed || !Directory.Exists(Path.Combine(typeshed, "stdlib"))) return;
        string folder = Fresh();
        var before = ParsedFileCache.Folder;
        try
        {
            ParsedFileCache.Folder = folder;
            int files = 0;
            foreach (var path in Directory.EnumerateFiles(typeshed, "*.pyi", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                ParsedFileCache.Parse(text);
                var back = ParsedFileCache.Parse(text);
                var parsed = Parser.Parse(text);
                if (Dump(parsed.Module) != Dump(back.Module) || Dump(parsed.Diagnostics) != Dump(back.Diagnostics)) Assert.Equal(path, "the same tree");
                files++;
            }
            Assert.True(files > 1000, files + " stubs");
        }
        finally
        {
            ParsedFileCache.Folder = before;
            Directory.Delete(folder, true);
        }
    }
}
