namespace PyMagic.Server;

/// <summary>What the project tree's and the tabs' menus do to files beyond moving them: <i>Clean Python Compiled Files</i> and <i>Add to .gitignore</i>.</summary>
public static class ProjectFiles
{
    /// <summary>Deletes the <c>__pycache__</c> folders and the <c>.pyc</c> / <c>.pyo</c> files under <paramref name="folder"/>; virtual environments, <c>.git</c>
    /// and <c>node_modules</c> are left alone (their compiled files are the interpreter's). Returns how many folders and files went.</summary>
    public static int CleanCompiled(string folder)
    {
        int removed = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            IEnumerable<string> subdirs, files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string f in files)
            {
                if (!f.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); removed++; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            foreach (string d in subdirs)
            {
                string name = Path.GetFileName(d);
                if (name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(d, true); removed++; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    continue;
                }
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(d, "pyvenv.cfg")))
                    continue;
                pending.Push(d);
            }
        }
        return removed;
    }

    /// <summary>The line of the project's <c>.gitignore</c> that leaves out <paramref name="rel"/> (a path from the project folder, with <c>/</c>):
    /// anchored with a leading <c>/</c>, and a trailing <c>/</c> for a folder.</summary>
    public static string IgnoreLine(string rel, bool isDir) => "/" + rel.Trim('/') + (isDir ? "/" : "");

    /// <summary>The text of the <c>.gitignore</c> with <paramref name="line"/> added at the end, or null when it is there already.</summary>
    public static string? WithIgnoreLine(string text, string line)
    {
        var have = text.Split('\n').Select(l => l.Trim()).ToHashSet();
        // "/a/b" and "a/b" mean the same file when it is written with a slash inside
        if (have.Contains(line) || line.TrimStart('/').Contains('/') && have.Contains(line.TrimStart('/'))) return null;
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.Length == 0 || text.EndsWith('\n') ? text + line + newline : text + newline + line + newline;
    }
}
