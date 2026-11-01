using System.Runtime.InteropServices;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// Spelling (item 240): the words of a text checked by Windows' own spell checker (ISpellChecker, the one Office and Edge use, with the
/// dictionaries of the languages the user has installed), so nothing is downloaded; plus the project's own dictionary,
/// <c>.pymagic/dictionary.txt</c> (one word a line), which stays in version control with the project.
/// </summary>
public static class Spelling
{
    // ---------------------------------------------------------------- the COM interfaces of spellcheck.h, as far as they are used

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        IEnumString get_SupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("E7ED1C71-87F7-4378-A840-C9200DACEE47"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]   // ISpellChecker2, which starts with the methods of ISpellChecker (whose own IID this machine refuses)
    interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError error);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellingError
    {
        uint get_StartIndex();
        uint get_Length();
    }

    [ComImport, Guid("00000101-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumString
    {
        [PreserveSig] int Next(int celt, [MarshalAs(UnmanagedType.LPWStr)] out string value, out int fetched);
    }

    static readonly Guid FactoryClass = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");

    // ---------------------------------------------------------------- the checker

    /// <summary>Why the checker could not be used, for a message.</summary>
    public static string? LastError { get; private set; }

    static readonly object Gate = new();
    static readonly Dictionary<string, bool> Known = new();       // "lang|word" -> spelled right
    static readonly Dictionary<string, string[]> Suggested = new();

    /// <summary>Runs on a thread of its own with a single-threaded apartment, as COM objects of the checker expect; null when it fails.</summary>
    static T? OnApartment<T>(Func<T> work) where T : class
    {
        T? result = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) when (e is COMException or InvalidCastException or PlatformNotSupportedException or TypeLoadException or InvalidOperationException) { result = null; LastError = e.ToString(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(15000);
        return result;
    }

    static ISpellChecker? Checker(string language)
    {
        var type = Type.GetTypeFromCLSID(FactoryClass);
        if (type == null || Activator.CreateInstance(type) is not ISpellCheckerFactory factory) return null;
        return factory.IsSupported(language) ? factory.CreateSpellChecker(language) : null;
    }

    /// <summary>The language the user writes in: the interface language when the machine has a dictionary for it, else English.</summary>
    public static string DefaultLanguage() => OnApartment(() => new[] { System.Globalization.CultureInfo.CurrentUICulture.Name, "en-US" }.FirstOrDefault(l => l.Length > 0 && Checker(l) != null) ?? "") is { Length: > 0 } l ? l : "en-US";

    /// <summary>Whether the machine has a spell checker for the language at all.</summary>
    public static bool Available(string language = "en-US") => OperatingSystem.IsWindows() && (OnApartment(() => Checker(language) != null ? "yes" : null) != null);

    /// <summary>The words (of those given) that are not spelled right and are not in the project's dictionary. Words the checker is not asked about are taken as right.</summary>
    public static IReadOnlyList<string> Misspelled(string root, string language, IEnumerable<string> words)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var mine = ProjectWords(root);
        var wanted = words.Where(w => w.Length > 0 && w.Length <= 60 && !mine.Contains(w.ToLowerInvariant())).Distinct(StringComparer.Ordinal).ToList();
        List<string> unknown;
        lock (Gate) unknown = wanted.Where(w => !Known.ContainsKey(language + "|" + w)).ToList();
        if (unknown.Count > 0)
        {
            var verdicts = OnApartment(() =>
            {
                var checker = Checker(language);
                if (checker == null) return null;
                var result = new Dictionary<string, bool>();
                foreach (var w in unknown)
                {
                    var errors = checker.Check(w);
                    result[w] = errors.Next(out var _) != 0;          // S_FALSE: no error: spelled right
                }
                return result;
            });
            if (verdicts == null) return Array.Empty<string>();
            lock (Gate) foreach (var (w, ok) in verdicts) Known[language + "|" + w] = ok;
        }
        lock (Gate) return wanted.Where(w => Known.TryGetValue(language + "|" + w, out var ok) && !ok).ToList();
    }

    /// <summary>What Windows suggests for a word, the best first (at most 8).</summary>
    public static IReadOnlyList<string> Suggest(string language, string word)
    {
        if (!OperatingSystem.IsWindows() || word.Length == 0 || word.Length > 60) return Array.Empty<string>();
        lock (Gate) if (Suggested.TryGetValue(language + "|" + word, out var hit)) return hit;
        var found = OnApartment(() =>
        {
            var checker = Checker(language);
            if (checker == null) return null;
            var list = new List<string>();
            var e = checker.Suggest(word);
            while (list.Count < 8 && e.Next(1, out var s, out int fetched) == 0 && fetched == 1) list.Add(s);
            return list.ToArray();
        }) ?? Array.Empty<string>();
        lock (Gate) Suggested[language + "|" + word] = found;
        return found;
    }

    // ---------------------------------------------------------------- the project's dictionary

    public static string DictionaryFile(string root) => Path.Combine(root, ".pymagic", "dictionary.txt");

    /// <summary>The words of the project's dictionary, in lower case.</summary>
    public static HashSet<string> ProjectWords(string root)
    {
        try { return File.Exists(DictionaryFile(root)) ? File.ReadAllLines(DictionaryFile(root)).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet() : new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Puts a word in the project's dictionary (once, sorted).</summary>
    public static void AddWord(string root, string word)
    {
        word = word.Trim();
        if (word.Length == 0 || word.Length > 60 || word.Any(c => !char.IsLetter(c) && c is not ('\'' or '-'))) throw new ArgumentException("not a word");
        var words = ProjectWords(root);
        if (!words.Add(word.ToLowerInvariant())) return;
        Directory.CreateDirectory(Path.GetDirectoryName(DictionaryFile(root))!);
        File.WriteAllText(DictionaryFile(root), string.Join("\n", words.OrderBy(w => w, StringComparer.Ordinal)) + "\n", new UTF8Encoding(false));
    }

    public static bool RemoveWord(string root, string word)
    {
        var words = ProjectWords(root);
        if (!words.Remove(word.Trim().ToLowerInvariant())) return false;
        File.WriteAllText(DictionaryFile(root), words.Count == 0 ? "" : string.Join("\n", words.OrderBy(w => w, StringComparer.Ordinal)) + "\n", new UTF8Encoding(false));
        return true;
    }
}
