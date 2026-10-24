using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>One dependency a project declares. <paramref name="Spec"/> is what pip would be given.</summary>
public sealed record Requirement(string Name, string Spec);

/// <summary>A file that declares dependencies, relative to the project folder.</summary>
public sealed record RequirementFile(string File, IReadOnlyList<Requirement> Requirements);

/// <summary>
/// Finds the dependencies a project declares: <c>requirements*.txt</c> (also in a <c>requirements/</c> folder) and the
/// <c>[project] dependencies</c> and <c>[project.optional-dependencies]</c> lists of <c>pyproject.toml</c>. Only the
/// declared names and specs are read; nothing is resolved.
/// </summary>
public static class RequirementsScanner
{
    public static List<RequirementFile> Scan(string root)
    {
        var result = new List<RequirementFile>();
        try
        {
            var candidates = Directory.EnumerateFiles(root, "requirements*.txt").ToList();
            string folder = Path.Combine(root, "requirements");
            if (Directory.Exists(folder)) candidates.AddRange(Directory.EnumerateFiles(folder, "*.txt"));
            foreach (var f in candidates.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var reqs = ParseRequirementsTxt(File.ReadAllText(f));
                if (reqs.Count > 0) result.Add(new RequirementFile(Path.GetRelativePath(root, f).Replace('\\', '/'), reqs));
            }
            string pyproject = Path.Combine(root, "pyproject.toml");
            if (File.Exists(pyproject))
            {
                var reqs = ParsePyproject(File.ReadAllText(pyproject));
                if (reqs.Count > 0) result.Add(new RequirementFile("pyproject.toml", reqs));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>Lines of a requirements file; comments, options (-r, -e, --index-url ...) and URLs are skipped.</summary>
    public static List<Requirement> ParseRequirementsTxt(string text)
    {
        var list = new List<Requirement>();
        foreach (var raw in text.Split('\n'))
        {
            string line = Regex.Replace(raw.Trim(), @"\s+#.*$", "");
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('-')) continue;
            if (line.Contains("://") || line.StartsWith('.') || line.StartsWith('/')) continue;
            if (FromSpec(line) is { } r) list.Add(r);
        }
        return list;
    }

    /// <summary>`name[extra]>=1.0; python_version &lt; "3.12"` -> name "name", spec without the environment marker.</summary>
    static Requirement? FromSpec(string line)
    {
        string spec = line.Split(';')[0].Trim();
        var m = Regex.Match(spec, @"^([A-Za-z0-9][A-Za-z0-9._\-]*)");
        if (!m.Success) return null;
        spec = Regex.Replace(spec, @"\s+", "");
        return PackageManager.IsSafeSpec(spec) ? new Requirement(m.Groups[1].Value, spec) : new Requirement(m.Groups[1].Value, m.Groups[1].Value);
    }

    /// <summary>The string items of `dependencies = [...]` under [project] and of every list under [project.optional-dependencies].</summary>
    public static List<Requirement> ParsePyproject(string text)
    {
        var list = new List<Requirement>();
        string? section = null;
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            var header = Regex.Match(line, @"^\[([^\]]+)\]\s*$");
            if (header.Success) { section = header.Groups[1].Value.Trim(); continue; }

            bool deps = section == "project" && Regex.IsMatch(line, @"^dependencies\s*=");
            bool optional = section == "project.optional-dependencies" && Regex.IsMatch(line, @"^[\w\-]+\s*=\s*\[");
            if (!deps && !optional) continue;

            // collect from the first "[" to the matching "]" (the array may span lines)
            var sb = new System.Text.StringBuilder(line[(line.IndexOf('=') + 1)..]);
            int j = i;
            while (!sb.ToString().Contains(']') && j + 1 < lines.Length) sb.Append(' ').Append(lines[++j].Trim());
            i = j;
            string body = sb.ToString();
            int start = body.IndexOf('['), end = body.IndexOf(']');
            if (start < 0 || end < start) continue;
            foreach (Match s in Regex.Matches(body[start..end], "\"([^\"]+)\"|'([^']+)'"))
                if (FromSpec(s.Groups[1].Success ? s.Groups[1].Value : s.Groups[2].Value) is { } r) list.Add(r);
        }
        return list;
    }
}
