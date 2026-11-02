using System.Diagnostics;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

public class ImportTests
{
    internal static readonly string? RealPython = FindPython();

    static string? FindPython()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("import sys; print(sys.executable)");
            using var p = Process.Start(psi);
            string path = p!.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(20000) && p.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    static bool HaveTypeshed => Workspace.DefaultTypeshedPath != null && Directory.Exists(Path.Combine(Workspace.DefaultTypeshedPath, "stdlib"));

    /// <summary>A project folder with an editor attached to it, using the given interpreter.</summary>
    sealed class Project : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_imp_" + Guid.NewGuid().ToString("N"));
        public readonly LanguageService Service = new();
        public string Uri { get; private set; } = "";

        public Project(string? interpreter = null, bool configure = true)
        {
            Directory.CreateDirectory(Root);
            Service.SourceRoots.Add(Root);
            if (configure) Service.ConfigureInterpreter(InterpreterInspector.Inspect(interpreter ?? RealPython!, "system", "test").Interpreter!);
        }

        public void File_(string rel, string text)
        {
            string p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        /// <summary>Opens `main.py` with this text and returns its import-related diagnostics as "message" strings.</summary>
        public List<string> Check(string code, string file = "main.py")
        {
            Uri = LanguageService.UriOfPath(Path.Combine(Root, file));
            if (Service.OpenUris.Contains(Uri)) Service.Change(Uri, code, 2); else Service.Open(Uri, code, 1);
            return Service.Diagnostics(Uri).Where(d => d.Code is "unresolved-import" or "unresolved-import-name" or "package-not-installed").Select(d => d.Message).ToList();
        }

        public List<CodeAction> Actions()
        {
            var lm = new LineMap(Service.Diagnostics(Uri).Count > 0 ? "" : "");
            return Service.CodeActions(Uri, new PyMagic.Server.Range(new PyMagic.Server.Position(0, 0), new PyMagic.Server.Position(1000, 0))).ToList();
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ------------------------------------------------------------ what is and is not reported

    [Fact]
    public void ImportsAreOnlyCheckedWhenAnInterpreterIsKnown()
    {
        using var p = new Project(configure: false);
        Assert.Equal(0, p.Check("import definitely_not_here\nfrom nowhere import thing\n").Count);
    }

    [Fact]
    public void MissingModulesAreReported()
    {
        if (RealPython == null) return;
        using var p = new Project();
        var found = p.Check("import definitely_not_here\nimport os\nfrom nowhere.deep import thing\nimport collections.abc, json\n");
        Assert.Equal("No module named 'definitely_not_here',No module named 'nowhere'", string.Join(",", found));

        var diag = p.Service.Diagnostics(p.Uri).First(d => d.Code == "unresolved-import");
        Assert.Equal(Severity.Warning, diag.Severity);
        Assert.Equal(0, diag.Range.Start.Line);
        Assert.Equal(7, diag.Range.Start.Character);                 // on the module name
        Assert.Equal(7 + "definitely_not_here".Length, diag.Range.End.Character);
    }

    [Fact]
    public void TheStandardLibraryCountsEvenWithoutStubsForIt()
    {
        if (RealPython == null) return;
        using var p = new Project();
        Assert.Equal(0, p.Check("import os, sys, re, json, math, sqlite3, xml.dom.minidom, ctypes, _thread\nfrom os import path, getcwd\nfrom collections import OrderedDict\nfrom __future__ import annotations\n").Count);
    }

    [Fact]
    public void ProjectModulesAndPackagesAreResolved()
    {
        if (RealPython == null) return;
        using var p = new Project();
        p.File_("helper.py", "def work(): ...\n");
        p.File_("pkg/__init__.py", "");
        p.File_("pkg/mod.py", "VALUE = 1\n");
        p.File_("pkg/sibling.py", "from . import mod\nfrom .mod import VALUE\nfrom .missing import x\n");

        Assert.Equal(0, p.Check("import helper\nfrom helper import work\nfrom helper import work as w\nimport pkg.mod\nfrom pkg import mod\nfrom pkg.mod import VALUE\n").Count);
        Assert.Equal("Cannot import name 'nope' from 'helper'", string.Join(",", p.Check("from helper import nope\n")));
        Assert.Equal("No module named 'helper.sub'", string.Join(",", p.Check("import helper.sub\n")));
        Assert.Equal("No module named 'pkg.nope'", string.Join(",", p.Check("import pkg.nope\n")));
        Assert.Equal("Cannot import name 'gone' from 'pkg'", string.Join(",", p.Check("from pkg import gone\n")));
        Assert.Equal("No module named 'pkg.missing'", string.Join(",", p.Check(File.ReadAllText(Path.Combine(p.Root, "pkg", "sibling.py")), "pkg/sibling.py")));
    }

    [Fact]
    public void GuardedAndDeadImportsAreLeftAlone()
    {
        if (RealPython == null) return;
        using var p = new Project();
        string code =
            "import sys\n" +
            "try:\n    import optional_one\nexcept ImportError:\n    optional_one = None\n" +
            "try:\n    from optional_two import x\nexcept (ValueError, ModuleNotFoundError):\n    x = None\n" +
            "try:\n    import optional_three\nexcept:\n    pass\n" +
            "if sys.version_info < (3, 0):\n    import python_two_only\n" +
            "if sys.platform == 'plan9':\n    import plan9_only\n";
        Assert.Equal(0, p.Check(code).Count);

        // a handler for something else does not make the import optional; the type-checking block is still checked
        Assert.Equal("No module named 'needed'", string.Join(",", p.Check("try:\n    import needed\nexcept ValueError:\n    pass\n")));
        Assert.Equal("No module named 'typed_only'", string.Join(",", p.Check("from typing import TYPE_CHECKING\nif TYPE_CHECKING:\n    import typed_only\n")));
    }

    [Fact]
    public void CompiledModulesAreNotMissing()
    {
        if (RealPython == null) return;
        using var p = new Project();
        File.WriteAllText(Path.Combine(p.Root, "speedups.cp313-win_amd64.pyd"), "x");
        File.WriteAllText(Path.Combine(p.Root, "fast.so"), "x");
        Directory.CreateDirectory(Path.Combine(p.Root, "native"));
        File.WriteAllText(Path.Combine(p.Root, "native", "core.pyd"), "x");
        Assert.Equal(0, p.Check("import speedups\nimport fast\nfrom speedups import anything\nimport native.core\nfrom native import core\n").Count);
    }

    [Fact]
    public void ImportsNextToAScriptOutsideAnyRootAreFound()
    {
        if (RealPython == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_loose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "neighbour.py"), "X = 1\n");
            var svc = new LanguageService();
            svc.ConfigureInterpreter(InterpreterInspector.Inspect(RealPython, "system", "test").Interpreter!);
            string uri = LanguageService.UriOfPath(Path.Combine(dir, "script.py"));
            svc.Open(uri, "import neighbour\nfrom neighbour import X\nimport absent\n", 1);
            Assert.Equal("No module named 'absent'", string.Join(",", svc.Diagnostics(uri).Where(d => d.Code == "unresolved-import").Select(d => d.Message)));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------ packages that are not installed

    [Fact]
    public void ModulesWithOnlyStubsAreReportedAsNotInstalled()
    {
        if (RealPython == null || !HaveTypeshed || !Directory.Exists(Path.Combine(Workspace.DefaultTypeshedPath!, "stubs", "PyYAML"))) return;
        using var root = new Project(configure: false);
        // a clean environment: nothing is installed in it, so only typeshed knows `yaml`
        string python = new InterpreterManager(root.Root).CreateVenv(RealPython, ".venv", withPip: false);
        using var p = new Project(python);
        Assert.Equal("Package 'yaml' is not installed", string.Join(",", p.Check("import yaml\n")));
        Assert.Equal("Package 'yaml' is not installed", string.Join(",", p.Check("from yaml import safe_load\n")));
        Assert.Equal(0, p.Check("from typing import TYPE_CHECKING\nif TYPE_CHECKING:\n    import yaml\n").Count);   // stubs are all a type-only import needs
    }

    [Fact]
    public void EditableInstallsAreFoundThroughTheirFinder()
    {
        string site = Path.Combine(Path.GetTempPath(), "pm_edit_" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(site, "src");
        Directory.CreateDirectory(Path.Combine(src, "edpkg"));
        Directory.CreateDirectory(Path.Combine(src, "other"));
        try
        {
            File.WriteAllText(Path.Combine(src, "edpkg", "__init__.py"), "");
            File.WriteAllText(Path.Combine(src, "single.py"), "");
            string escaped = src.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(site, "__editable___proj_1_0_finder.py"),
                "from __future__ import annotations\nimport sys\n" +
                "MAPPING: dict[str, str] = {'edpkg': '" + escaped + "\\\\edpkg', 'single': '" + escaped + "\\\\single.py', 'ghost': '" + escaped + "\\\\ghost', 'other': '" + escaped + "\\\\elsewhere'}\n" +
                "NAMESPACES: dict[str, list[str]] = {}\n");
            var roots = EditableInstalls.RootsIn(site).Select(Path.GetFullPath).Distinct().ToList();
            Assert.Equal(Path.GetFullPath(src), string.Join(",", roots));   // edpkg and single both live in src; the others do not exist or do not match
        }
        finally { Directory.Delete(site, true); }
    }

    // ------------------------------------------------------------ quick fixes

    static CodeAction? Fix(Project p, string titleStart) => p.Actions().FirstOrDefault(a => a.Title.StartsWith(titleStart));

    [Fact]
    public void MissingModulesOfferToInstallThePackage()
    {
        if (RealPython == null) return;
        using var p = new Project();
        p.Check("import requests_toolbelt\n");
        var fix = Fix(p, "Install package")!;
        Assert.Equal("Install package 'requests_toolbelt'", fix.Title);
        Assert.Equal("quickfix", fix.Kind);
        Assert.True(fix.IsPreferred);
        Assert.True(fix.Edit == null, "it is a command, not a text edit");
        Assert.Equal("pymagic.installPackage", fix.Command!.Command);
        Assert.Equal("requests_toolbelt", (string)fix.Command.Arguments![0]);
    }

    [Fact]
    public void ImportNamesThatDifferFromThePackageNameAreMapped()
    {
        if (RealPython == null) return;
        using var p = new Project();
        foreach (var (import, package) in new[] { ("PIL", "Pillow"), ("cv2", "opencv-python"), ("sklearn", "scikit-learn"), ("bs4", "beautifulsoup4"), ("dateutil", "python-dateutil") })
        {
            p.Check($"import {import}\n");
            Assert.Equal($"Install package '{package}'", Fix(p, "Install package")!.Title);
        }
        p.Check("from PIL import Image\n");
        Assert.Equal("Install package 'Pillow'", Fix(p, "Install package")!.Title);
    }

    [Fact]
    public void ADeclaredRequirementIsPreferred()
    {
        if (RealPython == null) return;
        using var p = new Project();
        p.File_("requirements.txt", "Pillow>=10\nrequests\n");
        p.Check("import PIL\n");
        Assert.Equal("Install package 'Pillow' (listed in the project's requirements)", Fix(p, "Install package")!.Title);

        p.File_("pyproject.toml", "[project]\ndependencies = [\"python-dateutil>=2\"]\n");
        p.Check("import dateutil\n");
        Assert.Contains("python-dateutil", Fix(p, "Install package")!.Title);
        Assert.Contains("listed in", Fix(p, "Install package")!.Title);
    }

    [Fact]
    public void SubmodulesOfInstalledPackagesAreNotInstallable()
    {
        if (RealPython == null) return;
        using var p = new Project();
        p.File_("pkg/__init__.py", "");
        p.Check("import pkg.nope\n");
        Assert.True(Fix(p, "Install package") == null, "pkg exists; only its submodule is missing");
    }

    [Fact]
    public void TyposAreOfferedAsChanges()
    {
        if (RealPython == null) return;
        using var p = new Project();
        p.File_("helper.py", "def work(): ...\ndef rest(): ...\n");

        static string Apply(string text, CodeAction a, string uri)
        {
            var lm = new LineMap(text);
            foreach (var e in a.Edit!.Changes[uri].OrderByDescending(e => lm.OffsetOf(e.Range.Start)))
                text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
            return text;
        }

        p.Check("import colections\n");
        Assert.Equal("import collections\n", Apply("import colections\n", Fix(p, "Change to 'collections'")!, p.Uri));
        p.Check("import helpr\n");
        Assert.Equal("import helper\n", Apply("import helpr\n", Fix(p, "Change to 'helper'")!, p.Uri));
        p.Check("from helper import wrk\n");
        Assert.Equal("from helper import work\n", Apply("from helper import wrk\n", Fix(p, "Change to 'work'")!, p.Uri));
        p.Check("from helper import nothing_like_it\n");
        Assert.True(Fix(p, "Change to") == null && Fix(p, "Install") == null, "no suggestion when nothing is close");
    }

    // ------------------------------------------------------------ the server

    [Fact]
    public void SwitchingTheInterpreterTurnsTheCheckOnAndOff()
    {
        if (RealPython == null) return;
        var sent = new List<System.Text.Json.Nodes.JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), sent.Add);
        const string uri = "file:///c:/proj/main.py";
        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri +
            "\",\"languageId\":\"python\",\"version\":1,\"text\":\"import definitely_not_here\\nprint(definitely_not_here)\\n\"}}}")!);
        int Problems() => sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray().Count;
        Assert.Equal(0, Problems());

        string json = System.Text.Json.Nodes.JsonValue.Create(RealPython)!.ToJsonString();
        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeConfiguration\",\"params\":{\"settings\":{\"pymagic\":{\"pythonPath\":" + json + "}}}}")!);
        Assert.Equal(1, Problems());
        var d = sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!["diagnostics"]![0]!;
        Assert.Equal("unresolved-import", d["code"]!.GetValue<string>());

        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/codeAction\",\"params\":{\"textDocument\":{\"uri\":\"" + uri +
            "\"},\"range\":{\"start\":{\"line\":0,\"character\":7},\"end\":{\"line\":0,\"character\":8}},\"context\":{\"diagnostics\":[]}}}")!);
        var actions = sent.Last(m => m["id"]?.GetValue<int>() == 3)["result"]!.AsArray();
        var install = actions.First(a => a!["title"]!.GetValue<string>().StartsWith("Install package"))!;
        Assert.Equal("pymagic.installPackage", install["command"]!["command"]!.GetValue<string>());
        Assert.Equal("definitely_not_here", install["command"]!["arguments"]![0]!.GetValue<string>());
        Assert.True(install["edit"] == null);
    }

    // ------------------------------------------------------------ item 215: PyPI, ignoring an import, the commands for any client

    [Fact]
    public void AGuessedPackageIsOfferedOnlyWhenPyPIHasIt()
    {
        if (RealPython == null) return;
        using var p = new Project();
        var asked = new List<string>();
        p.Service.PackageIndexLookup = name => { asked.Add(name); return name == "requests_toolbelt"; };
        p.Check("import requests_toolbelt\n");
        Assert.True(Fix(p, "Install package") != null);
        p.Check("import surely_no_such_package_here\n");
        Assert.True(Fix(p, "Install package") == null, "PyPI has no such package");
        Assert.True(Fix(p, "Ignore unresolved imports of 'surely_no_such_package_here'") != null);
        // PyPI that cannot be asked (offline) changes nothing
        p.Service.PackageIndexLookup = _ => null;
        p.Check("import another_unknown_one\n");
        Assert.True(Fix(p, "Install package") != null);
        // each name is asked once
        p.Service.PackageIndexLookup = name => { asked.Add(name); return name == "requests_toolbelt"; };
        p.Check("import requests_toolbelt\n");
        Fix(p, "Install package");
        Assert.Equal(1, asked.Count(n => n == "requests_toolbelt"));
    }

    [Fact]
    public void AnIgnoredImportIsNoLongerReported()
    {
        if (RealPython == null) return;
        using var p = new Project();
        Assert.Equal(2, p.Check("import vendored_tool\nimport vendored_tool.part\nimport other_missing\n").Count(m => m.Contains("vendored_tool")));
        var fix = Fix(p, "Ignore unresolved imports of 'vendored_tool'")!;
        Assert.Equal("pymagic.ignoreImport", fix.Command!.Command);
        p.Service.ExecuteCommand("pymagic.ignoreImport", new[] { "vendored_tool" });
        var left = p.Check("import vendored_tool\nimport vendored_tool.part\nimport other_missing\n");
        Assert.True(left.All(m => !m.Contains("vendored_tool")), "the module and its submodules");
        Assert.True(left.Any(m => m.Contains("other_missing")));
        Assert.Equal("[\n  \"vendored_tool\"\n]\n", File.ReadAllText(Path.Combine(p.Root, ".pymagic", "ignored-imports.json")).Replace("\r\n", "\n"));

        // taken off the list again (the Settings window's Remove): reported again, and the file goes with the last one
        p.Service.ExecuteCommand("pymagic.ignoreImport", new[] { "other_missing" });
        p.Service.ExecuteCommand("pymagic.unignoreImport", new[] { "vendored_tool" });
        Assert.Equal("other_missing", string.Join(",", p.Service.IgnoredImports()));
        Assert.Equal(2, p.Check("import vendored_tool\nimport vendored_tool.part\nimport other_missing\n").Count(m => m.Contains("vendored_tool")));
        p.Service.ExecuteCommand("pymagic.unignoreImport", new[] { "other_missing" });
        Assert.True(!File.Exists(Path.Combine(p.Root, ".pymagic", "ignored-imports.json")));
        p.Service.ExecuteCommand("pymagic.unignoreImport", new[] { "never_ignored" });   // nothing to take off: no harm
    }

    [Fact]
    public void TheCommandsOfTheFixesComeBackThroughExecuteCommand()
    {
        if (RealPython == null) return;
        using var p = new Project();
        var sent = new List<System.Text.Json.Nodes.JsonNode>();
        var server = new PyMagic.Server.Server(p.Service, sent.Add);
        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{}}}")!);
        var commands = sent.Last(m => m["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!["executeCommandProvider"]!["commands"]!.AsArray().Select(c => c!.GetValue<string>());
        Assert.True(commands.SequenceEqual(new[] { "pymagic.ignoreImport", "pymagic.unignoreImport", "pymagic.installPackage" }));
        string uri = LanguageService.UriOfPath(Path.Combine(p.Root, "main.py"));
        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":" + System.Text.Json.Nodes.JsonValue.Create(uri)!.ToJsonString() +
            ",\"languageId\":\"python\",\"version\":1,\"text\":\"import not_here_at_all\\n\"}}}")!);
        int Problems() => sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray().Count(d => d!["code"]?.GetValue<string>() == "unresolved-import");
        Assert.Equal(1, Problems());
        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"workspace/executeCommand\",\"params\":{\"command\":\"pymagic.ignoreImport\",\"arguments\":[\"not_here_at_all\"]}}")!);
        Assert.Equal(0, Problems());
        Assert.True(sent.Any(m => m["method"]?.GetValue<string>() == "window/showMessage"));
        // an unknown command is an error answer
        server.Handle(System.Text.Json.Nodes.JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"workspace/executeCommand\",\"params\":{\"command\":\"nope\",\"arguments\":[]}}")!);
        Assert.True(sent.Last(m => m["id"]?.GetValue<int>() == 3)["error"] != null);
    }
}
