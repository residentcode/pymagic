using System.Diagnostics;

namespace PyMagic.Tests;

/// <summary>
/// The temp folder of a test run: the runner points TEMP and TMP at a folder of its own (pymagic-tests-&lt;process id&gt;), so that what every test
/// and every program it starts (git, tar, Python) leaves there is removed when the run ends. Runs used to leave gigabytes in the person's temp
/// folder. The folder of a run that did not end (stopped, crashed) is removed by the next run, once its process is gone.
/// </summary>
public static class TestTemp
{
    const string Prefix = "pymagic-tests-";

    /// <summary>Makes this run's temp folder and points TEMP and TMP at it; removes those of earlier runs that are over. Returns it.</summary>
    public static string Begin()
    {
        string parent = Path.GetTempPath();
        foreach (string old in Directory.EnumerateDirectories(parent, Prefix + "*"))
            if (int.TryParse(Path.GetFileName(old)[Prefix.Length..], out int pid) && !Running(pid))
                RemoveTree(old);
        string dir = Path.Combine(parent, Prefix + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("TEMP", dir);
        Environment.SetEnvironmentVariable("TMP", dir);
        return dir;
    }

    static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Removes a folder: read-only files (git's objects, a virtual environment's) are made writable first, and one a closing process
    /// still holds is tried again; one held for longer is left (the next run removes it).</summary>
    public static void RemoveTree(string dir)
    {
        for (int i = 0; i < 5 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, true); return; }
            catch (UnauthorizedAccessException)
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { Thread.Sleep(300); }
        }
    }
}
