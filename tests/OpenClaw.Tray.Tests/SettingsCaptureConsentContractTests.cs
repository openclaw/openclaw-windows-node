namespace OpenClaw.Tray.Tests;

public sealed class SettingsCaptureConsentContractTests
{
    [Fact]
    public void PrivacySettings_ExposeAndPersistAllCaptureConsentTypes()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SettingsPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Presentation",
            "SettingsPageViewModel.cs"));

        Assert.Contains("x:Name=\"ScreenRecordingToggle\"", xaml);
        Assert.Contains("x:Name=\"CameraRecordingToggle\"", xaml);
        Assert.Contains("x:Name=\"LocationConsentToggle\"", xaml);
        Assert.Contains("IsOn=\"{Binding ScreenRecordingConsentGiven, Mode=TwoWay}\"", xaml);
        Assert.Contains("IsOn=\"{Binding CameraRecordingConsentGiven, Mode=TwoWay}\"", xaml);
        Assert.Contains("IsOn=\"{Binding LocationConsentGiven, Mode=TwoWay}\"", xaml);
        Assert.Contains("Persist(e => e.LocationConsentGiven = value)", viewModel);
    }
}
