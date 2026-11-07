namespace OpenClaw.Tray.Tests;

public sealed class SettingsCaptureConsentContractTests
{
    [Fact]
    public void PermissionsPage_SeparatesAndPersistsAppAccessConsents()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "PermissionsPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Presentation",
            "PermissionsPageViewModel.cs"));

        Assert.Contains("PermissionsPage_AppAccess", xaml);
        Assert.Contains("PermissionsPage_AppAccessDescription", xaml);
        Assert.Contains("Text=\"App access\"", xaml);
        Assert.Contains("x:Name=\"ScreenRecordingToggle\"", xaml);
        Assert.Contains("x:Name=\"CameraRecordingToggle\"", xaml);
        Assert.Contains("x:Name=\"LocationConsentToggle\"", xaml);
        Assert.Contains("IsOn=\"{Binding ScreenRecordingConsentGiven, Mode=TwoWay}\"", xaml);
        Assert.Contains("IsOn=\"{Binding CameraRecordingConsentGiven, Mode=TwoWay}\"", xaml);
        Assert.Contains("IsOn=\"{Binding LocationConsentGiven, Mode=TwoWay}\"", xaml);
        Assert.Contains("PersistSetting(edit => edit.LocationConsentGiven = value)", viewModel);
    }
}
