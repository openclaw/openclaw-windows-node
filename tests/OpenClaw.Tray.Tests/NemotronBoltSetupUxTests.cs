using OpenClaw.TestSupport;
using OpenClaw.SetupEngine.UI.Pages;

namespace OpenClaw.Tray.Tests;

public class NemotronBoltSetupUxTests
{
    [Fact]
    public void ReceiptRead_CannotApplyAfterThePageStartsAnotherProbe()
    {
        var source = ReadControl();
        int start = source.IndexOf("var installed = await new LocalAiManifestStore", StringComparison.Ordinal);
        int mutation = source.IndexOf("_localAiPinnedPlan =", start, StringComparison.Ordinal);
        Assert.Contains("if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))",
            source[start..mutation]);
        Assert.Contains("return;", source[start..mutation]);
    }

    [Fact]
    public void AvailableDevice_CanRecheckModelCapacityWithoutDisablingOtherChoices()
    {
        var coordinator = new LocalAiSetupAvailabilityCoordinator();
        var checking = coordinator.StartProbe();
        Assert.True(coordinator.TryApplyAvailable(checking.Generation, out var available, isRecoverable: true));
        Assert.True(available.IsAvailable);
        Assert.True(available.CanRecheck);
        Assert.True(coordinator.TryStartRecheck(out var rechecking));
        Assert.True(rechecking.IsChecking);
    }

    private static string ReadControl() => File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
        "src", "OpenClaw.SetupEngine.UI", "Controls", "LocalAiSetupControl.xaml.cs"));
}
