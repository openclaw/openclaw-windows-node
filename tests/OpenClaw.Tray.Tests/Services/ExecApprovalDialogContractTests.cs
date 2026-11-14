using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

// Retire these source guards when the WinUI dialog has direct behavioral coverage.
public sealed class ExecApprovalDialogContractTests
{
    private static string ReadDialog() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src", "OpenClaw.Tray.WinUI", "Dialogs", "ExecApprovalDialog.cs"));

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing marker: {start}");
        Assert.True(to > from, $"Missing or misordered marker: {end}");
        return source[from..to];
    }

    [Fact]
    public void DecisionActions_ArmTogetherAfterDelayWithoutScrollOrLayoutGate()
    {
        var source = ReadDialog();
        var guard = Slice(source, "private void ArmDecisionGuard()", "private static Border BuildConfusableWarning()");

        Assert.Contains("TimeSpan.FromMilliseconds(1500)", source);
        Assert.Contains("_denyButton.IsEnabled = false;", guard);
        Assert.Contains("_allowOnceButton.IsEnabled = false;", guard);
        Assert.Contains("_allowAlwaysButton.IsEnabled = false;", guard);
        Assert.Contains("_armTimer.Tick +=", guard);
        Assert.Contains("if (IsClosed) return;", guard);
        Assert.Contains("_decisionsArmed = true;", guard);
        Assert.Contains("_denyButton.IsEnabled = true;", guard);
        Assert.Contains("_allowOnceButton.IsEnabled = true;", guard);
        Assert.Contains("_allowAlwaysButton.IsEnabled = _allowAlwaysAvailable;", guard);
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
    public void ArmDelay_StartsFromFirstRenderAndIsReleasedOnClose()
    {
        var source = ReadDialog();
        var guard = Slice(source, "private void ArmDecisionGuard()", "private static Border BuildConfusableWarning()");
        var show = Slice(source, "public Task<ExecApprovalPromptOutcome> ShowAsync()", "private void Decide(");
        var firstFrame = Slice(source, "private void OnFirstFrameRendering(", "private void StartArmDelay()");
        var start = Slice(source, "private void StartArmDelay()", "private void ArmDecisionGuard()");
        var closed = Slice(source, "private void OnWindowClosed(", "private static Brush ResolveBrush(");

        // The delay must not run while the prompt is still being constructed or is not yet drawn.
        Assert.DoesNotContain("_armTimer.Start();", guard);
        Assert.Contains("CompositionTarget.Rendering += OnFirstFrameRendering;", show);
        Assert.Contains("if (_firstFrameFallbackTimer is null)", show);
        Assert.Contains("if (!_windowContent.IsLoaded) return;", firstFrame);
        Assert.Contains("StartArmDelay();", firstFrame);

        // A missed first frame must not leave the prompt undecidable.
        Assert.Contains("_firstFrameFallbackTimer.Tick += (_, _) => StartArmDelay();", show);
        Assert.Contains("_firstFrameFallbackTimer.Start();", show);

        // Arming starts once and releases both triggers.
        Assert.Contains("CompositionTarget.Rendering -= OnFirstFrameRendering;", start);
        Assert.Contains("_firstFrameFallbackTimer?.Stop();", start);
        Assert.Contains("if (_armDelayStarted || IsClosed) return;", start);
        Assert.Contains("_armTimer?.Start();", start);

        Assert.Contains("CompositionTarget.Rendering -= OnFirstFrameRendering;", closed);
        Assert.Contains("Activated -= OnWindowActivated;", closed);
        Assert.Contains("_firstFrameFallbackTimer?.Stop();", closed);
        Assert.Contains("_armTimer?.Stop();", closed);
    }

    [Fact]
    public void ShowAsync_PresentsWithoutTakingForegroundOrKeyboardFocus()
    {
        var source = ReadDialog();
        var show = Slice(source, "public Task<ExecApprovalPromptOutcome> ShowAsync()", "private void Decide(");

        Assert.Contains("AppWindow.Show(activateWindow: false);", show);
        Assert.Contains("FlashWindowEx(ref flash);", show);
        Assert.DoesNotContain("Activate();", source);
        Assert.DoesNotContain("SetForegroundWindow", source);

        // Deny focus is applied only after user activation and arming, never on open.
        Assert.DoesNotContain(".Focus(", show);
        var focusCalls = source.Split('\n').Count(line => line.Contains(".Focus(", StringComparison.Ordinal));
        Assert.Equal(1, focusCalls);
        var focusDeny = Slice(source, "private void FocusDenyAfterUserActivation()", "private void PlaceCenteredWithoutActivating()");
        Assert.Contains("if (IsClosed || _denyFocusApplied || !_decisionsArmed || !_windowActive) return;", focusDeny);
        Assert.Contains("_denyButton.Focus(FocusState.Programmatic);", focusDeny);
        Assert.Contains("_windowActive = args.WindowActivationState != WindowActivationState.Deactivated;", source);
        Assert.Contains("FocusDenyAfterUserActivation();", Slice(source, "private void ArmDecisionGuard()", "private static Border BuildConfusableWarning()"));

        // WinUIEx sizing and centering helpers call SetWindowPos without SWP_NOACTIVATE.
        Assert.DoesNotContain("SetWindowSize(", source);
        Assert.DoesNotContain("CenterOnScreen(", source);
        Assert.Contains("AppWindow.MoveAndResize(", source);

        // Without programmatic focus, "Enter never approves" rests on tab order: Deny is
        // added to the button row before either allow button, and none sets TabIndex.
        var denyAdded = source.IndexOf("buttonGrid.Children.Add(_denyButton);", StringComparison.Ordinal);
        Assert.True(denyAdded >= 0);
        Assert.True(denyAdded < source.IndexOf("buttonGrid.Children.Add(_allowAlwaysButton);", StringComparison.Ordinal));
        Assert.True(denyAdded < source.IndexOf("buttonGrid.Children.Add(_allowOnceButton);", StringComparison.Ordinal));
        Assert.DoesNotContain("TabIndex", source);

        var setWindowPosCalls = source.Split('\n')
            .Where(line => line.Contains("SetWindowPos(hwnd,", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(setWindowPosCalls);
        Assert.All(setWindowPosCalls, line => Assert.Contains("SWP_NOACTIVATE", line));
    }

    [Fact]
    public void EarlyDecisions_AreIgnoredWhileWindowCloseStillDenies()
    {
        var source = ReadDialog();
        var decide = Slice(source, "private void Decide(", "private void OnWindowActivated(");

        Assert.Contains("if (IsClosed || !_decisionsArmed) return;", decide);
        Assert.Contains("Decide(ExecApprovalPromptOutcome.Deny);", source);
        Assert.Contains("_outcome = ExecApprovalPromptOutcome.Deny;", source);
        Assert.Contains("_tcs.TrySetResult(_outcome);", source);
    }

    [Fact]
    public void ApprovalSafeguards_PreservePolicyVisibilityAndCloseCleanup()
    {
        var source = ReadDialog();

        Assert.Contains("_allowAlwaysAvailable = view.AllowAlwaysAvailable;", source);
        Assert.Contains("if (!_allowAlwaysAvailable)", source);
        Assert.Contains("_allowAlwaysButton.Visibility = Visibility.Collapsed;", source);
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
