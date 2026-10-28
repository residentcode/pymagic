using PyMagic.Core.Syntax;

namespace PyMagic.Tests;

/// <summary>Prints one hash per file for the shape of its syntax tree (node types and the offsets they span), to compare two builds of the parser (usage: asthash &lt;dir&gt;).</summary>
public static class AstHash
{
    public static int Run(string[] args)
    {
        long total = 17;
        int files = 0;
        foreach (var f in Directory.EnumerateFiles(args[1], "*.py", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var r = Parser.Parse(File.ReadAllText(f));
            long h = 17;
            void Walk(Node n)
            {
                h = h * 31 + StableHash(n.GetType().Name);
                h = h * 31 + n.Start;
                h = h * 31 + n.End;
                if (n is NameExpr ne) h = h * 31 + StableHash(ne.Name);
                if (n is AttributeExpr ae) h = h * 31 + StableHash(ae.Attr);
                if (n is BinaryExpr be) h = h * 31 + StableHash(be.Op);
                foreach (var c in AstChildren.Of(n)) Walk(c);
            }
            Walk(r.Module);
            h = h * 31 + r.Diagnostics.Count;
            total = total * 31 + h;
            files++;
            if (args.Length > 2) Console.WriteLine($"{h:X}  {f}");
        }
        Console.WriteLine($"{files} files, tree hash {total:X}");
        return 0;
    }

    static long StableHash(string s)
    {
        long h = 7;
        foreach (char c in s) h = h * 131 + c;
        return h;
    }
}
