using System.Runtime.CompilerServices;

namespace PyMagic.Tests;

/// <summary>
/// The folders a test looks in for the repository's files (typeshed, web/node_modules, desktop/test, the engine's scripts): the test program's
/// folder and the ones above it, then the repository these sources were built from. A build into another folder (dotnet build -o %TEMP%\…)
/// still finds them.
/// </summary>
static class TestRepo
{
    public static IEnumerable<DirectoryInfo> Ancestors()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) yield return d;
        // this file is tests/PyMagic.Tests/TestRepo.cs: the repository is two folders up
        if (Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Source()))) is { } repo && Directory.Exists(repo)) yield return new DirectoryInfo(repo);
    }

    static string Source([CallerFilePath] string path = "") => path;
}
