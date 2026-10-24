using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Editable installs made by newer setuptools do not put the project on sys.path with a .pth line; they install an import
/// hook (<c>__editable___name_finder.py</c>) whose <c>MAPPING</c> says where each package lives. Reading that mapping
/// gives the folders to search, so such packages resolve like any other.
/// </summary>
public static class EditableInstalls
{
    static readonly Regex Mapping = new(@"MAPPING[^=\n]*=\s*\{(?<body>.*?)\}", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Pair = new(@"(?:'(?<k>(?:[^'\\]|\\.)*)'|""(?<k>(?:[^""\\]|\\.)*)"")\s*:\s*(?:'(?<v>(?:[^'\\]|\\.)*)'|""(?<v>(?:[^""\\]|\\.)*)"")", RegexOptions.Compiled);

    /// <summary>Folders that make the mapped packages importable (the parent of each mapped package folder, or the folder of a mapped module).</summary>
    public static IEnumerable<string> RootsIn(string sitePackages)
    {
        string[] finders;
        try { finders = Directory.GetFiles(sitePackages, "__editable___*_finder.py"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var finder in finders)
        {
            string text;
            try { text = File.ReadAllText(finder); } catch (IOException) { continue; }
            var m = Mapping.Match(text);
            if (!m.Success) continue;
            foreach (Match pair in Pair.Matches(m.Groups["body"].Value))
            {
                string name = pair.Groups["k"].Value.Replace("\\\\", "\\");
                string target = pair.Groups["v"].Value.Replace("\\\\", "\\");
                string leaf = Path.GetFileName(target.TrimEnd('\\', '/'));
                if (Directory.Exists(target) && string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase))
                    yield return Path.GetDirectoryName(target.TrimEnd('\\', '/'))!;
                else if (File.Exists(target) && string.Equals(Path.GetFileNameWithoutExtension(target), name, StringComparison.OrdinalIgnoreCase))
                    yield return Path.GetDirectoryName(target)!;
            }
        }
    }
}
