using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class HotkeyRegistrationStateTests
{
    [Fact]
    public void SettingsChordStaysLiveWhenVoiceRegistrationFails()
    {
        var state = new HotkeyRegistrationState()
            .WithVoice(false)
            .WithSettings(true);

        Assert.True(state.AnyLive);
        Assert.False(state.Voice);
        Assert.True(state.Settings);
    }

    [Fact]
    public void NeitherChord_IsNotLive()
    {
        var state = new HotkeyRegistrationState()
            .WithVoice(false)
            .WithSettings(false);

        Assert.False(state.AnyLive);
    }

    [Fact]
    public void Clear_DropsBothChords()
    {
        var state = new HotkeyRegistrationState(true, true).Cleared();

        Assert.False(state.AnyLive);
        Assert.False(state.Voice);
        Assert.False(state.Settings);
    }

    [Fact]
    public void Service_UnregistersSettingsWhenVoiceDidNotRegister()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "GlobalHotkeyService.cs"));

        Assert.Contains("_hotkeys.AnyLive", source, StringComparison.Ordinal);
        Assert.Contains("if (_hotkeys.Settings)", source, StringComparison.Ordinal);
        Assert.Contains("UnregisterHotKey(hWnd, HOTKEY_ID_SETTINGS)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_registered)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
