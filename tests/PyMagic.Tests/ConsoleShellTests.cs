using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The shells the Terminal panel can start (item 139): the list in Run/shells.json, each found where it says, chosen by id from the settings.
/// The lookup is checked on made-up programs; the shipped list is read as it is.
/// </summary>
public class ConsoleShellTests
{
    static Func<string, string?> Vars(Dictionary<string, string> v) => name => v.TryGetValue(name, out var x) ? x : null;

    static readonly Dictionary<string, string> Pc = new() { ["Base"] = @"C:\Base", ["PATH"] = @"C:\Bin;D:\Kit\front" };

    static readonly List<ShellProfile> Made = new()
    {
        new("one", "One", new[] { "path:one.exe", @"%Base%\one\one.exe" }, "-q"),
        new("two", "Two Shell", new[] { @"%Base%\two app\two.exe" }),
        new("three", "Three", new[] { @"path:front.exe>..\back\three.exe" }, null, new() { ["STAY"] = "1" }),
        new("four", "Four", new[] { @"%Missing%\four.exe" }),
    };

    [Fact]
    public void EachPlaceKindIsLookedUpInOrder()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Base\one\one.exe", @"D:\Kit\front\one.exe", @"C:\Base\two app\two.exe", @"D:\Kit\front\front.exe", @"D:\Kit\back\three.exe", @"\four.exe",
        };
        var found = ConsoleShells.Found(Made, files.Contains, Vars(Pc));
        Assert.Equal("one,two,three", string.Join(",", found.Select(s => s.Id)));
        // the PATH comes first when the list says so, the arguments follow, a path with spaces is quoted
        Assert.Equal(@"D:\Kit\front\one.exe -q", found[0].CommandLine);
        Assert.Equal("\"C:\\Base\\two app\\two.exe\"", found[1].CommandLine);
        // a file beside one found on the PATH, with the variables the entry asks for
        Assert.Equal(@"D:\Kit\back\three.exe", found[2].CommandLine);
        Assert.Equal("1", found[2].Environment!["STAY"]);
    }

    [Fact]
    public void WhatIsNotThereIsLeftOut()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Base\one\one.exe", @"D:\Kit\front\front.exe" };
        var found = ConsoleShells.Found(Made, files.Contains, Vars(Pc));
        Assert.Equal("one", string.Join(",", found.Select(s => s.Id)));
        Assert.Equal(@"C:\Base\one\one.exe -q", found[0].CommandLine);
        Assert.Equal(0, ConsoleShells.Found(Made, _ => false, Vars(Pc)).Count);
    }

    [Fact]
    public void TheShippedListIsReadInItsOrder()
    {
        var list = ConsoleShells.Catalogue();
        Assert.Equal("pwsh,powershell,cmd,gitbash,wsl", string.Join(",", list.Select(p => p.Id)));
        Assert.True(list.All(p => p.Name.Length > 0 && p.Find.Length > 0));
        // the same ids as the settings may keep (termtabs.js keeps ids of letters, digits, _ and -)
        Assert.True(list.All(p => System.Text.RegularExpressions.Regex.IsMatch(p.Id, @"^[\w-]{1,40}$")));
    }

    [Fact]
    public void TheSettingChoosesByIdAndAutoOrAMissingOneTakesTheFirst()
    {
        var found = new List<ConsoleShell> { new("one", "One", "one.exe"), new("two", "Two", "two.exe") };
        var none = Vars(new());
        Assert.Equal("two", ConsoleShells.Choose("two", found, none).Id);
        Assert.Equal("one", ConsoleShells.Choose("auto", found, none).Id);
        Assert.Equal("one", ConsoleShells.Choose("three", found, none).Id);
        Assert.Equal("one", ConsoleShells.Choose(null, found, none).Id);
        // a command line sent as the id is not run
        Assert.Equal("one", ConsoleShells.Choose("two.exe -x", found, none).Id);
        // PYMAGIC_SHELL stands for auto; a shell chosen by id still wins
        var own = Vars(new() { ["PYMAGIC_SHELL"] = "mine.exe" });
        Assert.Equal("mine.exe", ConsoleShells.Choose("auto", found, own).CommandLine);
        Assert.Equal("two", ConsoleShells.Choose("two", found, own).Id);
    }

    [Fact]
    public void ThisPcHasAShellFromTheList()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(ConsoleShells.Found().Count > 0);
    }

    [Fact]
    public void AnActiveVenvShowsInThePromptOfPowerShellAndCmd()
    {
        Assert.Equal(".venv", ConsoleShells.VenvPrompt(@"C:\proj\.venv", _ => null));
        Assert.Equal("shop", ConsoleShells.VenvPrompt(@"C:\proj\.venv", _ => "home = C:\\Python\nprompt = 'shop'\n"));
        Assert.Equal("bad name", ConsoleShells.VenvPrompt(@"C:\proj\.venv", _ => "prompt = bad\"; name&\n"));
        Assert.True(ConsoleShells.VenvPrompt(null) == null);

        var list = ConsoleShells.Catalogue();
        var pwsh = list.Single(p => p.Id == "powershell");
        var cmd = list.Single(p => p.Id == "cmd");
        var shell = new ConsoleShell("powershell", "Windows PowerShell", "powershell.exe -NoLogo", null, pwsh.VenvArgs, pwsh.VenvEnv, pwsh.CommandArg, pwsh.StartCommand, pwsh.VenvCommand);
        var (line, env) = shell.WithVenv(".venv");
        // the uv function first (uv init as an application), then the prompt
        Assert.True(line.StartsWith("powershell.exe -NoLogo -NoExit -Command \"function global:uv {"), line);
        Assert.True(line.EndsWith("; function global:prompt { '(.venv) PS ' + $executionContext.SessionState.Path.CurrentLocation + '> ' }\""), line);
        Assert.True(line.Count(c => c == '"') == 2, "one argument: no double quote inside it");
        Assert.Equal(".venv", env["VIRTUAL_ENV_PROMPT"]);
        Assert.True(!shell.WithVenv(null).CommandLine.Contains("prompt"), "no prompt without an environment");
        var (cmdLine, cmdEnv) = new ConsoleShell("cmd", "Command Prompt", "cmd.exe", null, cmd.VenvArgs, cmd.VenvEnv, cmd.CommandArg, cmd.StartCommand, cmd.VenvCommand).WithVenv("shop");
        Assert.True(cmdLine.StartsWith("cmd.exe /k \"doskey uv="), cmdLine);
        Assert.Equal("(shop) $P$G", cmdEnv["PROMPT"]);
    }
}
