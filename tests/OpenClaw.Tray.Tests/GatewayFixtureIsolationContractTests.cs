using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Guard only the WinUI/OS adapter entrypoints not compiled by Tray.Tests.
/// Retirement condition: replace each source guard when that adapter has an
/// injected runtime and can be executed without reaching the real host.
/// </summary>
public sealed class GatewayFixtureIsolationContractTests
{
    [Fact]
    public void TrayBuild_AdvertisesUnconditionalFixtureIsolationVersion()
    {
        var project = XDocument.Parse(ReadTraySource("OpenClaw.Tray.WinUI.csproj"));
        var marker = Assert.Single(project.Descendants("RuntimeHostConfigurationOption"),
            option => (string?)option.Attribute("Include") == "OpenClaw.GatewayFixtureIsolationVersion");

        Assert.Equal("1", (string?)marker.Attribute("Value"));
        Assert.All(marker.AncestorsAndSelf(), element => Assert.Null(element.Attribute("Condition")));
    }

    [Fact]
    public void StartupAutostartReconciliationSkipsFixtureHostAccess()
    {
        var body = BodyAfter(ReadTraySource("App.xaml.cs"), "private async Task ReconcileAutoStartOnStartupAsync()");
        var guard = body.IndexOf("GatewayFixtureIsolation.IsEnabled", StringComparison.Ordinal);
        var hostCall = body.IndexOf("AutoStartManager.ReconcileAutoStartAsync", StringComparison.Ordinal);
        Assert.True(guard >= 0 && hostCall > guard);
        Assert.Contains("return;", body[guard..hostCall]);
    }

    [Fact]
    public void App_ValidatesFixtureBeforeStartupSideEffects()
    {
        var body = BodyAfter(ReadTraySource("App.xaml.cs"), "public App()");

        Assert.StartsWith("_ = GatewayFixtureIsolation.Get();", body);
        Assert.True(body.IndexOf("GatewayFixtureIsolation.Get()", StringComparison.Ordinal) <
            body.IndexOf("WaitForRestartSourceIfRequested(", StringComparison.Ordinal));
        Assert.True(body.IndexOf("GatewayFixtureIsolation.Get()", StringComparison.Ordinal) <
            body.IndexOf("s_runMarker.MarkStarted()", StringComparison.Ordinal));
    }

    [Fact]
    public void WslKeepalive_GuardsBothStartAndStaleCleanupBeforeSettingsOrHostAccess()
    {
        var body = BodyAfter(ReadTraySource("Services", "WslGatewayKeepAliveService.cs"),
            "public async Task TryEnsureAsync()");

        Assert.StartsWith("if (GatewayFixtureIsolation.IsEnabled)", body);
        var guardedPrefix = body[..body.IndexOf("try", StringComparison.Ordinal)];
        Assert.Contains("return;", guardedPrefix);
        Assert.DoesNotContain("_getSettings()", guardedPrefix);
        Assert.DoesNotContain("_getRegistry()", guardedPrefix);
        Assert.DoesNotContain("StopStaleLocalGatewayKeepAliveAsync()", guardedPrefix);
        Assert.DoesNotContain("Process.", guardedPrefix);
        Assert.Contains("await StopStaleLocalGatewayKeepAliveAsync();", body);
    }

    [Theory]
    [InlineData("public static void SetAutoStart(bool enable)")]
    [InlineData("public static Task SetAutoStartAsync(bool enable)")]
    [InlineData("public static Task<bool> ReconcileAutoStartAsync(bool configured)")]
    [InlineData("private static void SetUnpackagedAutoStart(bool enable)")]
    [InlineData("private static async Task SetPackagedAutoStartAsync(bool enable)")]
    public void AutoStart_MutationEntrypointsRejectFixtureBeforeWindowsAccess(string signature)
    {
        var body = BodyAfter(ReadTraySource("Services", "AutoStartManager.cs"), signature);

        // In particular this must be outside the unpackaged best-effort catch:
        // swallowing the refusal would make an installed-registration no-op look successful.
        Assert.StartsWith("ThrowIfFixtureMutation();", body);
    }

    [Fact]
    public void AutoStart_RefusalIsLoggedAndRethrown_NotConvertedToSuccess()
    {
        var body = BodyAfter(ReadTraySource("Services", "AutoStartManager.cs"),
            "private static void ThrowIfFixtureMutation()");
        body = body[..body.IndexOf("private static void SetUnpackagedAutoStart", StringComparison.Ordinal)];

        Assert.Contains("AutoStartReconciliation.ThrowIfFixtureMutation();", body);
        Assert.Contains("catch (AutoStartRefusedException ex)", body);
        Assert.Contains("Logger.Warn(ex.Message);", body);
        Assert.Contains("throw;", body);
    }

    [Fact]
    public void AutoStart_FixtureRollbackAvoidsInstalledRegistrationReads()
    {
        var body = BodyAfter(ReadTraySource("Services", "AutoStartManager.cs"),
            "public static Task<bool> ResolveAutoStartAfterFailedChangeAsync(bool requested, Exception failure)");

        Assert.StartsWith("if (GatewayFixtureIsolation.IsEnabled)", body);
        Assert.Contains("return Task.FromResult(false);",
            body[..body.IndexOf("PackageHelper.IsPackaged", StringComparison.Ordinal)]);
    }

    [Fact]
    public void ToastBoundaries_DoNotInitializeInstalledRegistrationInFixtureMode()
    {
        Assert.Matches(
            @"if \(!GatewayFixtureIsolation\.IsEnabled\)\s+ToastNotificationManagerCompat\.OnActivated \+= OnToastActivated;",
            ReadTraySource("App.xaml.cs"));
        Assert.Matches(
            @"if \(!GatewayFixtureIsolation\.IsEnabled\)\s+ToastNotificationManagerCompat\.OnActivated -= OnToastActivated;",
            ReadTraySource("App.AppShutdownCoordinator.cs"));
        var body = BodyAfter(ReadTraySource("Services", "ToastService.cs"),
            "public void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null)");
        Assert.StartsWith("if (GatewayFixtureIsolation.IsEnabled)", body);
        Assert.Contains("return;", body[..body.IndexOf("ShouldShowToast(", StringComparison.Ordinal)]);
    }

    private static string ReadTraySource(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            [TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", .. parts]));

    private static string BodyAfter(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing guarded adapter: {signature}");
        var body = source[(source.IndexOf('{', start) + 1)..];
        return Regex.Replace(body, @"//[^\r\n]*", string.Empty).TrimStart();
    }
}
