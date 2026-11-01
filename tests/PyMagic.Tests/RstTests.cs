using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>reStructuredText through docutils (item 241): HTML when the Python has docutils, a clear answer when it has not.</summary>
public class RstTests
{
    static bool Has(string module)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("python", $"-c \"import {module}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(20000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public void TheHelperAnswersWithHtmlOrSaysDocutilsIsMissing()
    {
        var answer = Databases.Ask("python", new JsonObject { ["text"] = "Title\n=====\n\nA *word* and ``code``.\n\n.. raw:: html\n\n   <script>alert(1)</script>\n" }, 30000, "pm_rst.py");
        if (Has("docutils"))
        {
            string html = answer["html"]!.GetValue<string>();
            Assert.Contains("<h1", html);
            Assert.Contains("<em>word</em>", html);
            Assert.True(!html.Contains("<script"), "a raw directive is not run");
        }
        else
        {
            Assert.True(answer["missing"]!.GetValue<bool>());
            Assert.Contains("pip install docutils", answer["error"]!.GetValue<string>());
        }
    }
}
