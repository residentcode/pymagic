using System.Collections.Concurrent;

namespace PyMagic.Core.Syntax;

/// <summary>
/// One string for each name, keyword and operator the parser reads: every `self` or `return` in a project is the same string, found from the
/// characters in the source without making a string first. The table is shared by all parsers (files are read on several threads) and stops
/// growing at a limit, after which new words are simply made as before.
/// </summary>
internal static class WordCache
{
    const int Limit = 400_000;
    static readonly ConcurrentDictionary<string, string> Words = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Lookup = Words.GetAlternateLookup<ReadOnlySpan<char>>();

    public static string Get(ReadOnlySpan<char> word)
    {
        if (Lookup.TryGetValue(word, out var known)) return known;
        string made = word.ToString();
        return Words.Count >= Limit ? made : Words.GetOrAdd(made, made);
    }
}
