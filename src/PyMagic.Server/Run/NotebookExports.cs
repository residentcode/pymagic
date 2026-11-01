using System.Security.Cryptography;

namespace PyMagic.Server;

/// <summary>
/// The pages made by "Open in browser" of a notebook: the page of the notebook view builds the HTML, the host keeps it in memory for a
/// while under a random name, and the web browser asks for it there. Nothing is written to disk. Only the most recent few are kept.
/// </summary>
public static class NotebookExports
{
    const int Keep = 12;
    const int MaxBytes = 64 * 1024 * 1024;
    static readonly object Gate = new();
    static readonly List<(string Id, string Name, string Html)> Pages = new();

    /// <summary>Keeps a page; returns its name (an unguessable string), or null when it is too big.</summary>
    public static string? Put(string name, string html)
    {
        if (html.Length > MaxBytes) return null;
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        lock (Gate)
        {
            Pages.Add((id, name, html));
            while (Pages.Count > Keep) Pages.RemoveAt(0);
        }
        return id;
    }

    public static string? Get(string id)
    {
        lock (Gate) return Pages.FirstOrDefault(p => p.Id == id).Html;
    }

    /// <summary>
    /// What the page is served with: no script can run in it and nothing can be submitted, whatever the notebook's outputs hold (the page
    /// comes from the same address as the IDE itself, so a script in a notebook must never run there).
    /// </summary>
    public const string Policy = "sandbox; script-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
}
