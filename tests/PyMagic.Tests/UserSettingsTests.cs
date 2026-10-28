using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The person's own settings: kept in one file, never half written, never anything but one JSON object.</summary>
public class UserSettingsTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "pm_settings_" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        string file = Temp();
        var s = new UserSettings(file);
        Assert.Equal("{}", s.Read());                                  // nothing yet
        s.Write("{ \"theme\": \"light\", \"fontSize\": 15 }");
        Assert.Equal("{\"theme\":\"light\",\"fontSize\":15}", new UserSettings(file).Read());
        s.Write("{\"theme\":\"dark\"}");
        Assert.Equal("{\"theme\":\"dark\"}", s.Read());                // replaced, not merged
        Assert.True(!File.Exists(file + ".tmp"), "no half file left");
    }

    [Fact]
    public void OnlyAJsonObjectOfSensibleSizeIsAccepted()
    {
        var s = new UserSettings(Temp());
        Assert.Throws<ServiceException>(() => s.Write("[1, 2]"));
        Assert.Throws<ServiceException>(() => s.Write("not json"));
        Assert.Throws<ServiceException>(() => s.Write("\"text\""));
        Assert.Throws<ServiceException>(() => s.Write("{\"x\":\"" + new string('a', UserSettings.MaxBytes) + "\"}"));
        Assert.Equal("{}", s.Read());
    }

    [Fact]
    public void ADamagedFileReadsAsNoSettings()
    {
        string file = Temp();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ broken");
        Assert.Equal("{}", new UserSettings(file).Read());
        File.WriteAllText(file, "[1]");
        Assert.Equal("{}", new UserSettings(file).Read());
    }
}
