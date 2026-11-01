using System.Diagnostics;
using System.Text;

namespace PyMagic.Server;

/// <summary>A profiler run kept: its name (the file names without extension), what ran, and when.</summary>
public sealed record ProfileSnapshot(string Name, string Label, DateTime When, bool HasStats);

/// <summary>
/// The profiler runs kept in <c>.pymagic/profiles</c> (the last 20): each run's JSON and raw pstats, so an earlier run can
/// be looked at again and its .pstats opened by other tools; and .pstats files of anywhere in the project read into the panel.
/// </summary>
public static class ProfileSnapshots
{
    public const int MaxKept = 20;

    public static string Folder(string root) => Path.Combine(root, ".pymagic", "profiles");

    /// <summary>Copies the last run (profile.json and profile.pstats) into the snapshots, named by the time and what ran.</summary>
    public static string? Keep(string root, string label)
    {
        string json = RunSession.ProfilePath(root);
        if (!File.Exists(json)) return null;
        Directory.CreateDirectory(Folder(root));
        string safe = new string(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (safe.Length > 40 ? safe[..40] : safe);
        for (int n = 2; File.Exists(Path.Combine(Folder(root), name + ".json")); n++) name = name.Split('~')[0] + "~" + n;
        File.Copy(json, Path.Combine(Folder(root), name + ".json"));
        if (File.Exists(RunSession.ProfileStatsPath(root))) File.Copy(RunSession.ProfileStatsPath(root), Path.Combine(Folder(root), name + ".pstats"));
        foreach (var old in List(root).Skip(MaxKept))
            foreach (var ext in new[] { ".json", ".pstats" })
                try { File.Delete(Path.Combine(Folder(root), old.Name + ext)); } catch (IOException) { }
        return name;
    }

    /// <summary>The snapshots, newest first.</summary>
    public static List<ProfileSnapshot> List(string root)
    {
        if (!Directory.Exists(Folder(root))) return new();
        return Directory.EnumerateFiles(Folder(root), "*.json")
            .Select(f =>
            {
                string name = Path.GetFileNameWithoutExtension(f);
                int dash = name.IndexOf('-', 9);   // after yyyyMMdd-HHmmss
                return new ProfileSnapshot(name, dash > 0 ? name[(dash + 1)..].Split('~')[0] : name, File.GetLastWriteTime(f), File.Exists(Path.ChangeExtension(f, ".pstats")));
            })
            .OrderByDescending(s => s.When).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();   // when the run ended (two in a second differ by it)
    }

    /// <summary>The JSON of a snapshot by its name, or null (a name with a path in it is not one).</summary>
    public static string? PathOf(string root, string name) =>
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && name != ".." && File.Exists(Path.Combine(Folder(root), name + ".json")) ? Path.Combine(Folder(root), name + ".json") : null;

    /// <summary>
    /// Reads a .pstats file (written by cProfile, `python -m cProfile -o`, yappi or PyCharm) with pm_profile.py into the
    /// last run's place, and keeps it as a snapshot named after the file.
    /// </summary>
    /// <exception cref="ServiceException">Python could not read it.</exception>
    public static string Open(string root, string python, string pstatsFile)
    {
        string out_ = RunSession.ProfilePath(root);
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(RunSession.ExtractHelper("pm_profile.py"));
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(out_);
        psi.ArgumentList.Add("--load"); psi.ArgumentList.Add(pstatsFile);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        Process process;
        try { process = Process.Start(psi) ?? throw new ServiceException($"'{python}' could not be started."); }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException($"The Python interpreter '{python}' could not be started."); }
        using (process)
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(60000);
            if (process.ExitCode != 0) throw new ServiceException(error.Trim().Length > 0 ? error.Trim() : "The file could not be read.");
        }
        if (!string.Equals(Path.GetFullPath(pstatsFile), Path.GetFullPath(RunSession.ProfileStatsPath(root)), StringComparison.OrdinalIgnoreCase))
            File.Copy(pstatsFile, RunSession.ProfileStatsPath(root), overwrite: true);
        return Keep(root, Path.GetFileNameWithoutExtension(pstatsFile)) ?? "";
    }
}
