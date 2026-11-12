using System.IO;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

// Retire these source guards when the WinUI dialog has direct behavioral coverage.
public sealed class ExecApprovalDialogContractTests
{
    private static string ReadDialog() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src", "OpenClaw.Tray.WinUI", "Dialogs", "ExecApprovalDialog.cs"));

    [Fact]
    public void AllowActions_ArmAfterDelayWithoutScrollOrLayoutGate()
    {
        var source = ReadDialog();
        var guard = source[source.IndexOf("private void ArmAllowGuard()")..
            source.IndexOf("private static Border BuildConfusableWarning()")];

        Assert.Contains("TimeSpan.FromMilliseconds(1500)", source);
        Assert.Contains("_allowOnceButton.IsEnabled = false;", guard);
        Assert.Contains("_allowAlwaysButton.IsEnabled = false;", guard);
        Assert.Contains("_armTimer.Tick +=", guard);
        Assert.Contains("if (IsClosed) return;", guard);
        Assert.Contains("_allowOnceButton.IsEnabled = true;", guard);
        Assert.Contains("_allowAlwaysButton.IsEnabled = _allowAlwaysAvailable;", guard);
        Assert.Contains("_armTimer.Start();", guard);
        Assert.DoesNotContain("if (", guard.Replace("if (IsClosed) return;", "")
            .Replace("if (_allowAlwaysAvailable)", ""));
        Assert.DoesNotContain("ViewChanged +=", source);
        Assert.DoesNotContain("SizeChanged +=", source);
        Assert.DoesNotContain("Loaded +=", source);
        Assert.DoesNotContain("ScrollableHeight", source);
        Assert.DoesNotContain("VerticalOffset", source);
        Assert.DoesNotContain("ViewportHeight", source);
        Assert.DoesNotContain("ExtentHeight", source);
        Assert.DoesNotContain("ExecApproval_ScrollHint", source);
        Assert.Contains("new ScrollViewer", source);
    }

    [Fact]
    public void ApprovalSafeguards_PreservePolicyVisibilityDenyFocusAndCloseCleanup()
    {
        var source = ReadDialog();

        Assert.Contains("_allowAlwaysAvailable = view.AllowAlwaysAvailable;", source);
        Assert.Contains("if (!_allowAlwaysAvailable)", source);
        Assert.Contains("_allowAlwaysButton.Visibility = Visibility.Collapsed;", source);
        Assert.Contains("_denyButton.Focus(FocusState.Programmatic);", source);
        Assert.Contains("_outcome = ExecApprovalPromptOutcome.Deny;", source);
        Assert.Contains("Key = global::Windows.System.VirtualKey.Escape", source);
        Assert.Contains("_armTimer?.Stop();", source);
        Assert.Contains("_tcs.TrySetResult(_outcome);", source);
    }

    [Fact]
    public void LocalizedResources_DoNotRequireScrollingToApprove()
    {
        var stringsRoot = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings");
        var resources = Directory.GetFiles(stringsRoot, "Resources.resw", SearchOption.AllDirectories);
        Assert.NotEmpty(resources);
        foreach (var path in resources)
            Assert.DoesNotContain("ExecApproval_ScrollHint", File.ReadAllText(path));
    }
}
