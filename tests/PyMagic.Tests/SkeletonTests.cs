using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Stubs of compiled modules (Skeletons, Run/pm_skeletons.py): a .pyd in an environment's site-packages with no .pyi or .py of its own is described
/// from the interpreter, kept apart from the environment, found as a library, and made again only when it changes.
/// </summary>
public class SkeletonTests
{
    [Fact]
    public async Task ACompiledModuleWithoutStubsIsDescribedAndFound()
    {
        if (ImportTests.RealPython is not { } python) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_skel_" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Skeletons.Root;
        try
        {
            // an environment with one compiled module of its own: the standard library's _bz2, copied into its site-packages
            var made = Process.Start(new ProcessStartInfo(python, $"-m venv --without-pip \"{Path.Combine(dir, "env")}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            Assert.True(made.WaitForExit(120000) && made.ExitCode == 0);
            string envPython = Path.Combine(dir, "env", "Scripts", "python.exe");
            var interpreter = InterpreterInspector.Inspect(envPython, "venv", "test").Interpreter!;
            string site = interpreter.SitePackages.First(Directory.Exists);
            string? bz2 = Directory.GetFiles(Path.Combine(interpreter.BasePrefix, "DLLs"), "_bz2*.pyd").FirstOrDefault();
            if (bz2 == null) return;
            File.Copy(bz2, Path.Combine(site, Path.GetFileName(bz2)));

            Skeletons.Root = Path.Combine(dir, "skeletons");
            Assert.True(await Skeletons.RefreshAsync(interpreter));
            string stub = Path.Combine(Skeletons.DirFor(envPython), "_bz2.pyi");
            Assert.True(File.Exists(stub));
            string text = File.ReadAllText(stub);
            Assert.Contains("class BZ2Compressor", text);
            Assert.Contains("def compress(self", text);
            // nothing changed: nothing made again
            Assert.True(!await Skeletons.RefreshAsync(interpreter));
            // the stubs are the interpreter's last library root
            Assert.Equal(Path.GetFullPath(Skeletons.DirFor(envPython)), interpreter.LibraryRoots(includeStdlib: false)[^1]);

            // and the analysis reads them: what the compiled module has is known (no typeshed, so nothing else describes _bz2)
            var svc = new LanguageService();
            svc.SourceRoots.Add(Path.Combine(dir, "proj"));
            svc.ConfigureInterpreter(interpreter);
            const string code = "import _bz2\n\nV = _bz2.BZ2Compressor()\nW = V.compress\n";
            string uri = LanguageService.UriOfPath(Path.Combine(dir, "proj", "main.py"));
            svc.Open(uri, code, 1);
            Assert.Contains("V: BZ2Compressor", svc.Hover(uri, new Position(2, 0))!.Contents.Value);
            Assert.Contains("def W(", svc.Hover(uri, new Position(3, 0))!.Contents.Value);
        }
        finally
        {
            Skeletons.Root = previousRoot;
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
