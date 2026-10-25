using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>
/// The built-in formatter against the real black and ruff. They need a Python with both installed: set PYMAGIC_FORMATTER_PYTHON to its
/// interpreter (python -m venv v; v\Scripts\pip install black ruff); without it the tests have nothing to compare with and pass.
/// </summary>
public class RealFormatterTests
{
    static string? Python => Environment.GetEnvironmentVariable("PYMAGIC_FORMATTER_PYTHON") is { Length: > 0 } p && File.Exists(p) ? p : null;

    const string Sample = """
        '''Module docstring.'''
        import sys
        import os
        from b import c


        class A:

            x = 'it\'s'
            y = br"\xAB" + b'\\xAB' + U'u' + F'{x}'

            def f(self):

                # a comment
                a = self.x[i+1:j+1]
                b = self.x[1:2, ::3]
                c = self.x[len(a):len(b)], self.x[-1:], self.x[:-i]
                t = 1,
                u, = a
                for v, in a: pass
                if a:

                    def g(): pass
                else:

                    return (1)
                return a.b[x:y if z else w]

            def h(self):
                '''doc'''

                del (a)
        """;

    [Fact]
    public void TheBuiltInFormatterWritesWhatBlackWrites()
    {
        if (Python is not { } python) return;
        string source = Sample.Replace("\r\n", "\n") + "\n";
        string black = ExternalFormatter.Format(python, "black", source, "sample.py");
        string ours = PythonFormatter.Format(source) ?? throw new AssertionException("the formatter declined: " + PythonFormatter.LastMismatch);
        Assert.Equal(black, ours);
    }

    [Fact]
    public void RuffSortsTheImportsAndOnlyThem()
    {
        if (Python is not { } python) return;
        string sorted = ExternalFormatter.SortImports(python, "import sys\nimport os\nfrom b import c\nx=1\n", "sample.py");
        Assert.Equal("import os\nimport sys\n\nfrom b import c\n\nx=1\n", sorted);
    }
}
