using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using WinUIEx;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Mounted production-window proof with in-memory operations only, not migration/package proof.
/// Set OPENCLAW_VISUAL_TEST=1 and OPENCLAW_VISUAL_TEST_DIR to capture rendered states.
/// Never invokes Installed apps or constructs the production migration operations.
/// </summary>
[Collection(UICollection.Name)]
public sealed class StoreMigrationWindowProofTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Fact]
    public Task Consent_AssignsNativeIconsForTaskbarAndWindowSwitcher()
    {
        return WithWindowAsync(new Operations(), async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Consent);
            await ui.RunOnUIAsync(() =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                const uint wmGetIcon = 0x007f;
                foreach (var iconType in new nuint[] { 0, 1 })
                {
                    var icon = SendMessage(hwnd, wmGetIcon, iconType, 0);
                    Assert.NotEqual(nint.Zero, icon);
                    output.WriteLine($"Native window icon type {iconType}: nonzero handle.");
                }
            });
            await InvokeAsync(window, "Dismiss");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [Fact]
    public Task Consent_DefaultsToNotNow_AndDismissesWithoutMigration()
    {
        var operations = new Operations();
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Consent);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Consent", consent: true);
                var root = Root(window);
                Assert.Same(Control<Button>(window, "Dismiss"), FocusManager.GetFocusedElement(root.XamlRoot));
                Assert.Equal(new[] { "inspect", "consent?" }, operations.Calls);
            });
            await CaptureAsync(window, "Consent");
            await InvokeAsync(window, "Dismiss");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() =>
                Assert.Equal(new[] { "inspect", "consent?" }, operations.Calls));
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light, 720, 820, false)]
    [InlineData(ElementTheme.Dark, 720, 820, false)]
    [InlineData(ElementTheme.Light, 520, 520, true)]
    public Task Consent_UsesWizardVisuals_AndKeepsActionsOutsideScrollingContent(
        ElementTheme theme, int width, int height, bool largeText)
    {
        var operations = new Operations();
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Consent);
            await ui.RunOnUIAsync(() =>
            {
                Root(window).RequestedTheme = theme;
                window.SetWindowSize(width, height);
                if (largeText)
                {
                    Control<TextBlock>(window, "Status").FontSize = 28;
                    Control<TextBlock>(window, "Heading").FontSize = 40;
                }
            });
            await WaitUntilAsync(() => Control<Image>(window, "MascotHero").Source
                is BitmapImage { PixelWidth: > 0, PixelHeight: > 0 }, "wizard mascot to load");
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Consent", consent: true);
                Assert.True(window.ExtendsContentIntoTitleBar);
                Assert.IsType<MicaBackdrop>(window.SystemBackdrop);
                Assert.Equal(Localized("Migration2_Title"), Control<TextBlock>(window, "TitleBarText").Text);
                Assert.Equal(AutomationHeadingLevel.Level1,
                    AutomationProperties.GetHeadingLevel(Control<TextBlock>(window, "Heading")));
                var mascot = Control<Image>(window, "MascotHero");
                Assert.Equal(96, mascot.ActualWidth);
                Assert.Equal(96, mascot.ActualHeight);
                Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(mascot));

                var viewport = Control<ScrollViewer>(window, "MigrationContent");
                Assert.InRange(viewport.ActualWidth, 1, 560);
                Assert.Equal(ScrollBarVisibility.Disabled, viewport.HorizontalScrollBarVisibility);
                var layout = Assert.IsType<Grid>(viewport.Parent);
                Assert.Equal(new Thickness(48, 28, 48, 24), layout.Padding);
                Assert.Equal(16, layout.RowSpacing);
                var actions = Control<Grid>(window, "Actions");
                Assert.True(actions.ColumnDefinitions[1].Width.IsAuto);
                Assert.Equal(HorizontalAlignment.Left, Control<Button>(window, "Dismiss").HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Right, Control<Button>(window, "Primary").HorizontalAlignment);
                Assert.Null(Control<Button>(window, "Dismiss").Style);
                Assert.NotNull(Control<Button>(window, "Primary").Style);
                var viewportBottom = viewport.TransformToVisual(Root(window))
                    .TransformPoint(new Windows.Foundation.Point(0, viewport.ActualHeight)).Y;
                var actionsTop = actions.TransformToVisual(Root(window))
                    .TransformPoint(new Windows.Foundation.Point()).Y;
                Assert.True(viewportBottom <= actionsTop);
                Assert.Same(Control<Button>(window, "Dismiss"),
                    FocusManager.GetFocusedElement(Root(window).XamlRoot));
                Assert.Equal(new[] { "inspect", "consent?" }, operations.Calls);
                if (largeText)
                    Assert.True(viewport.ScrollableHeight > 0, "Large text must scroll, not push actions offscreen.");
                else
                    AssertSafetyWithinViewport(window);
            });
            await CaptureAsync(window, $"Wizard-{theme}-{width}x{height}-LargeText{largeText}");
            if (largeText)
            {
                await ui.RunOnUIAsync(() =>
                {
                    var viewport = Control<ScrollViewer>(window, "MigrationContent");
                    viewport.ChangeView(null, viewport.ScrollableHeight, null, disableAnimation: true);
                });
                await WaitUntilAsync(() =>
                {
                    var viewport = Control<ScrollViewer>(window, "MigrationContent");
                    return Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) < 1;
                }, "consent warning to scroll into view");
                await ui.RunOnUIAsync(() => AssertSafetyWithinViewport(window));
                await CaptureAsync(window, "Consent-520x520-WarningScrolled");
            }
            await InvokeAsync(window, "Dismiss");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Fact]
    public Task AwaitingRemoval_LargeText_KeepsAllActionsVisible()
    {
        var operations = new Operations { Admission = new(StoreMigrationStartupState.AwaitingInnoRemoval) };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.AwaitingRemoval);
            await ui.RunOnUIAsync(() =>
            {
                window.SetWindowSize(520, 520);
                Control<TextBlock>(window, "Status").FontSize = 28;
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "AwaitingRemoval", removal: true);
                var actions = Control<Grid>(window, "Actions");
                Assert.Same(actions, Control<Button>(window, "InstalledApps").Parent);
                Assert.Same(actions, Control<Button>(window, "Primary").Parent);
                Assert.Same(actions, Control<Button>(window, "Dismiss").Parent);
                Assert.True(Control<ScrollViewer>(window, "MigrationContent").ScrollableHeight > 0);
                Assert.Equal(new[] { "inspect" }, operations.Calls);
            });
            await CaptureAsync(window, "AwaitingRemoval-520x520-LargeText");
            await InvokeAsync(window, "Dismiss");
            // A completion receipt exists here, so dismissal must not resume normal startup.
            Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Fact]
    public Task ExplicitConsent_BusyClose_ManualRetry_OffersRemovalOnlyAfterCompletion()
    {
        var operations = new Operations
        {
            CloseGate = NewGate<bool>(),
            CompleteGate = NewGate<StoreMigrationCompletionState>(),
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Consent);
            await InvokeAsync(window, "Primary");
            await WaitForStageAsync(workflow, StoreMigrationStage.ClosingSource, busy: true);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Closing", busy: true);
                Assert.Equal(new[] { "inspect", "consent?", "inspect", "grant", "close" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "ClosingSource");

            await ui.RunOnUIAsync(() => operations.CloseGate!.SetResult(false));
            await WaitForStageAsync(workflow, StoreMigrationStage.CloseSource);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "CloseInno");
                Assert.DoesNotContain("prepare", operations.Calls);
                Assert.DoesNotContain("complete", operations.Calls);
                Assert.Same(Control<Button>(window, "Primary"),
                    FocusManager.GetFocusedElement(Root(window).XamlRoot));
                operations.CloseGate = null;
            });
            await CaptureAsync(window, "CloseSource");

            await InvokeAsync(window, "Primary");
            await WaitForStageAsync(workflow, StoreMigrationStage.Completing, busy: true);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Completing", busy: true);
                Assert.Equal(1, operations.Calls.Count(call => call == "grant"));
                Assert.Equal(2, operations.Calls.Count(call => call == "close"));
                Assert.Equal(1, operations.Calls.Count(call => call == "prepare"));
                Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
                Assert.False(completion.IsCompleted);
                operations.CompleteGate!.SetResult(StoreMigrationCompletionState.Completed);
            });
            await WaitForStageAsync(workflow, StoreMigrationStage.AwaitingRemoval);
            await ui.RunOnUIAsync(() => AssertState(window, "AwaitingRemoval", removal: true));
            await CaptureAsync(window, "AwaitingRemoval");

            await ui.RunOnUIAsync(() =>
                operations.Admission = new(StoreMigrationStartupState.FinalizationRequired));
            await InvokeAsync(window, "Primary");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(StoreMigrationStage.Ready, workflow.Stage);
                Assert.Equal(1, operations.Calls.Count(call => call == "finalize"));
                Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
            });
        });
    }

    [Fact]
    public Task FailedPreparation_ShowsRecoveryCopy_AndRetryCanComplete()
    {
        var operations = new Operations
        {
            Consent = true,
            Prepared = StoreMigrationPreparationState.ValidationFailed,
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.ValidationFailed);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "ValidationFailed");
                Assert.DoesNotContain("complete", operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "ValidationFailed");
            await ui.RunOnUIAsync(() => operations.Prepared = StoreMigrationPreparationState.Prepared);
            await InvokeAsync(window, "Primary");
            await WaitForStageAsync(workflow, StoreMigrationStage.AwaitingRemoval);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "AwaitingRemoval", removal: true);
                Assert.Equal(2, operations.Calls.Count(call => call == "prepare"));
                Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
                Assert.DoesNotContain("grant", operations.Calls);
                Assert.False(completion.IsCompleted);
            });
        });
    }

    [Fact]
    public Task FinalizationFailed_RetriesWithoutRepeatingAdoption()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.RecordCleanupFailed,
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.FinalizationFailed);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "FinalizationFailed");
                Assert.Equal(new[] { "inspect", "finalize" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "FinalizationFailed");
            await ui.RunOnUIAsync(() => operations.Finalized = StoreMigrationFinalizationState.Finalized);
            await InvokeAsync(window, "Primary");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() =>
                Assert.Equal(new[] { "inspect", "finalize", "inspect", "finalize" }, operations.Calls));
        });
    }

    /// <summary>
    /// A durable startup refusal is the one finalization outcome that succeeds and still has to be
    /// read: only Windows can re-enable the startup task. The window must say so, must not offer a
    /// Retry that cannot work, and must still let the app launch when the user dismisses it.
    /// </summary>
    [Fact]
    public Task DurableStartupRefusal_ShowsTheNoticeWithoutRetryAndStillLaunches()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.StartupPreferenceRefused,
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.StartupRefused);
            await ui.RunOnUIAsync(() =>
            {
                var root = Root(window);
                root.UpdateLayout();
                Assert.True(root.ActualWidth > 0 && root.ActualHeight > 0);
                var status = Control<TextBlock>(window, "Status");
                Assert.Equal(Localized("Migration_StoreStartupRefused"), status.Text);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                Assert.Equal(Visibility.Collapsed, Control<Button>(window, "Primary").Visibility);
                Assert.True(Control<Button>(window, "Dismiss").FocusState != FocusState.Unfocused);
                Assert.Equal(new[] { "inspect", "finalize" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "StartupRefused");
            await InvokeAsync(window, "Dismiss");
            // Finalization succeeded, so no receipt blocks launch: dismissal must resume startup.
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.InspectionFailed, "InspectionFailed")]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation, "Unsupported")]
    [InlineData(StoreMigrationStartupState.UpdateInno, "UpdateRequired")]
    public Task BlockedAdmission_HasAccessibleRetry_WithoutRemovalOrMutation(
        StoreMigrationStartupState admission, string status)
    {
        var operations = new Operations { Admission = new(admission) };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitUntilAsync(() => !workflow.IsBusy && operations.Calls.Count == 1,
                "initial admission to render");
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, status);
                Assert.Equal(new[] { "inspect" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await InvokeAsync(window, "Primary");
            await WaitUntilAsync(() => !workflow.IsBusy && operations.Calls.Count == 2,
                "retry to inspect admission again");
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, status);
                Assert.Equal(new[] { "inspect", "inspect" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
        });
    }

    [Fact]
    public Task Recovery_OffersRetryAndRemovalWithoutMutatingRecords()
    {
        var operations = new Operations { Admission = new(StoreMigrationStartupState.RecoveryRequired) };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Recovery);
            await ui.RunOnUIAsync(() =>
            {
                // The remedy is removing the previous app, so recovery shows that shortcut and a
                // retry that notices it. Discard is withheld: these records still decode.
                AssertState(window, "Recovery", removal: true);
                Assert.Same(Control<Button>(window, "Dismiss"),
                    FocusManager.GetFocusedElement(Root(window).XamlRoot));
                Assert.Equal(new[] { "inspect" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "Recovery");
            await InvokeAsync(window, "Dismiss");
            // Recovery holds no completion receipt, so dismissal returns the user to the app.
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() => Assert.Equal(new[] { "inspect" }, operations.Calls));
        });
    }

    [Fact]
    public Task Recovery_DiscardsUnreadableRecordsAndReleasesTheApp()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.RecoveryRequired),
            Unreadable = true
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Recovery);
            await ui.RunOnUIAsync(() => AssertState(window, "Recovery", removal: true, discard: true));
            await CaptureAsync(window, "RecoveryDiscard");

            await InvokeAsync(window, "DiscardRecords");

            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() => Assert.Equal(new[] { "inspect", "discard", "inspect" }, operations.Calls));
        });
    }

    /// <summary>
    /// The previous app holds the migration lease for its whole lifetime, so a contended discard
    /// is the most likely first outcome. The window has to say so, or the button reads as dead.
    /// </summary>
    [Fact]
    public Task Recovery_ReportsADiscardThatCouldNotRun()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.RecoveryRequired),
            Unreadable = true,
            Discarded = StoreMigrationDiscardState.Busy
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Recovery);

            await InvokeAsync(window, "DiscardRecords");
            for (var attempt = 0; attempt < 100 && workflow.LastDiscard is null; attempt++)
                await Task.Delay(50);
            Assert.Equal(StoreMigrationDiscardState.Busy, workflow.LastDiscard);
            Assert.Equal(StoreMigrationStage.Recovery, workflow.Stage);

            await ui.RunOnUIAsync(() =>
            {
                var root = Root(window);
                var safety = Control<InfoBar>(window, "Safety");
                var error = Assert.Single(TestSupport.FindLogical<InfoBar>(root), bar => bar != safety);
                Assert.True(error.IsOpen);
                Assert.Equal(Localized("Migration2_DiscardFailed"), error.Message);
                // The offer stays, because a contended discard is worth retrying.
                Assert.Equal(Visibility.Visible, Control<Button>(window, "DiscardRecords").Visibility);
            });
            await CaptureAsync(window, "RecoveryDiscardBusy");
        });
    }

    private async Task WithWindowAsync(
        Operations operations,
        Func<StoreMigrationWindow, StoreMigrationWorkflow, Task<bool>, Task> test)
    {
        StoreMigrationWindow? window = null;
        StoreMigrationWorkflow? workflow = null;
        Task<bool>? completion = null;
        var closed = false;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                workflow = new StoreMigrationWorkflow(operations, NullLogger.Instance);
                window = new StoreMigrationWindow(workflow);
                window.Closed += (_, _) => closed = true;
                completion = window.ShowAsync();
            });
            await test(window!, workflow!, completion!);
        }
        finally
        {
            if (window is not null)
            {
                // Release only fake work so the real busy-close guard cannot leak a window.
                await ui.RunOnUIAsync(() =>
                {
                    operations.CloseGate?.TrySetResult(false);
                    operations.CompleteGate?.TrySetResult(StoreMigrationCompletionState.Completed);
                });
                await WaitUntilAsync(() => !workflow!.IsBusy, "fake operations to settle for cleanup");
                await ui.RunOnUIAsync(() =>
                {
                    if (!closed)
                        window.Close();
                });
            }
        }
    }

    private Task WaitForStageAsync(StoreMigrationWorkflow workflow, StoreMigrationStage stage, bool busy = false) =>
        WaitUntilAsync(() => workflow.Stage == stage && workflow.IsBusy == busy, $"{stage}, busy={busy}");

    private async Task WaitUntilAsync(Func<bool> predicate, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await ui.YieldToRenderAsync();
            if (await ui.RunOnUIAsync(() => Task.FromResult(predicate())))
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for {description}.");
    }

    private Task InvokeAsync(StoreMigrationWindow window, string name) => ui.RunOnUIAsync(() =>
    {
        var button = Control<Button>(window, name);
        Assert.True(button.IsEnabled);
        Assert.Equal(Visibility.Visible, button.Visibility);
        var peer = new ButtonAutomationPeer(button);
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
    });

    private static void AssertState(
        StoreMigrationWindow window, string status, bool consent = false, bool busy = false,
        bool removal = false, bool retry = true, bool discard = false)
    {
        var root = Root(window);
        root.UpdateLayout();
        Assert.NotNull(root.XamlRoot);
        Assert.True(root.ActualWidth > 0 && root.ActualHeight > 0);
        var statusText = Control<TextBlock>(window, "Status");
        Assert.Equal(Localized("Migration2_" + status), statusText.Text);
        Assert.True(statusText.IsTextSelectionEnabled);
        Assert.Equal("MigrationStatus", AutomationProperties.GetAutomationId(statusText));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(statusText));
        Assert.Equal(Localized("Migration2_Title"), window.Title);
        Assert.Equal(Localized("Migration2_Title"), Control<TextBlock>(window, "TitleBarText").Text);
        Assert.Equal(Localized(status switch
        {
            "CloseInno" => "Migration2_CloseInnoTitle",
            "AwaitingRemoval" => "Migration2_RemovalTitle",
            _ => "Migration2_Title"
        }), Control<TextBlock>(window, "Heading").Text);
        Assert.Equal(consent ? Visibility.Visible : Visibility.Collapsed,
            Control<StackPanel>(window, "ConsentSteps").Visibility);
        Assert.Equal(consent ? Visibility.Visible : Visibility.Collapsed,
            Control<TextBlock>(window, "ConsentHint").Visibility);
        if (consent)
        {
            foreach (var step in new[] { "Prepare", "Remove", "Finish" })
            {
                var title = Control<TextBlock>(window, step + "StepTitle");
                Assert.Equal(Localized($"Migration2_{step}StepTitle"), title.Text);
                Assert.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(title));
                Assert.Equal(Localized($"Migration2_{step}StepBody"), Control<TextBlock>(window, step + "StepBody").Text);
            }
            Assert.Equal(Localized("Migration2_ConsentHint"), Control<TextBlock>(window, "ConsentHint").Text);
        }
        var warningKey = status switch
        {
            "Consent" => "Consent",
            "CloseInno" => "CloseInno",
            "AwaitingRemoval" => "Removal",
            _ => null
        };
        var safety = Control<InfoBar>(window, "Safety");
        Assert.Equal(warningKey is not null, safety.IsOpen);
        Assert.Equal(warningKey is null ? Visibility.Collapsed : Visibility.Visible, safety.Visibility);
        Assert.False(safety.IsClosable);
        Assert.Equal(InfoBarSeverity.Warning, safety.Severity);
        Assert.Equal("MigrationSafety", AutomationProperties.GetAutomationId(safety));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(safety));
        Assert.Equal(warningKey is null ? string.Empty : Localized($"Migration2_{warningKey}WarningTitle"), safety.Title);
        Assert.Equal(warningKey is null ? string.Empty : Localized($"Migration2_{warningKey}Warning"), safety.Message);
        AssertButton(window, "Primary", "MigrationPrimary",
            consent ? "Migration_StoreMigrate" : "Migration_StoreRetry", enabled: !busy, visible: retry);
        AssertButton(window, "Dismiss", "MigrationDismiss",
            consent ? "Migration_StoreNotNow" : "Migration2_Close", enabled: !busy);
        AssertButton(window, "InstalledApps", "MigrationInstalledApps", "Migration2_InstalledApps",
            enabled: !busy, visible: removal);
        AssertButton(window, "DiscardRecords", "MigrationDiscardRecords", "Migration2_DiscardRecords",
            enabled: !busy, visible: discard);
        var progress = Control<ProgressRing>(window, "Progress");
        Assert.Equal(busy, progress.IsActive);
        Assert.Equal(busy ? Visibility.Visible : Visibility.Collapsed, progress.Visibility);
        Assert.Equal(Localized("Migration2_Busy"), AutomationProperties.GetName(progress));
        Assert.False(Assert.Single(TestSupport.FindLogical<InfoBar>(root), bar => bar != safety).IsOpen);
    }

    private static void AssertSafetyWithinViewport(StoreMigrationWindow window)
    {
        var safety = Control<InfoBar>(window, "Safety");
        var viewport = Control<ScrollViewer>(window, "MigrationContent");
        var top = safety.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point()).Y;
        Assert.True(safety.ActualHeight > 0);
        Assert.InRange(top, -1, viewport.ActualHeight);
        Assert.InRange(top + safety.ActualHeight, 1, viewport.ActualHeight + 1);
    }

    private static void AssertButton(
        StoreMigrationWindow window, string name, string automationId, string resourceKey,
        bool enabled, bool visible = true)
    {
        var button = Control<Button>(window, name);
        Assert.Equal(automationId, AutomationProperties.GetAutomationId(button));
        Assert.Equal(Localized(resourceKey), new ButtonAutomationPeer(button).GetName());
        Assert.Equal(enabled, button.IsEnabled);
        Assert.Equal(100, button.MinWidth);
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
        if (visible)
        {
            Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
            var position = button.TransformToVisual(Root(window)).TransformPoint(new Windows.Foundation.Point());
            Assert.InRange(position.X, 0, Root(window).ActualWidth);
            Assert.InRange(position.X + button.ActualWidth, 1, Root(window).ActualWidth + 1);
            Assert.InRange(position.Y + button.ActualHeight, 1, Root(window).ActualHeight + 1);
        }
    }

    private static string Localized(string key)
    {
        var value = LocalizationHelper.GetString(key);
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual(key, value);
        return value;
    }

    private async Task CaptureAsync(StoreMigrationWindow window, string state)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1")
            return;

        var directory = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Screenshot proof requires OPENCLAW_VISUAL_TEST_DIR.");
        var surface = $"StoreMigrationWindow-FakeOperations-{state}-{Guid.NewGuid():N}";
        var surfaceDirectory = Path.Combine(Path.GetFullPath(directory!), surface);
        await ui.YieldToRenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var root = Assert.IsType<Grid>(Root(window));
            var background = root.Background;
            try
            {
                // RenderTargetBitmap omits Mica. Avoid the capture helper's white fill over dark-theme text.
                if (root.ActualTheme == ElementTheme.Dark)
                    root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                await VisualTestCapture.CaptureAsync(root, surface);
            }
            finally
            {
                root.Background = background;
            }
        });
        Assert.True(Directory.Exists(surfaceDirectory), "The screenshot helper did not create the proof directory.");
        var screenshot = Assert.Single(Directory.GetFiles(surfaceDirectory, "*.png"));
        Assert.True(new FileInfo(screenshot).Length > 0, "The screenshot helper produced an empty artifact.");
        output.WriteLine($"XAML-only proof with solid capture background and fake operations, not Mica/package proof: {state}; screenshot={screenshot}");
    }

    private static FrameworkElement Root(StoreMigrationWindow window) =>
        Assert.IsAssignableFrom<FrameworkElement>(window.Content);

    private static T Control<T>(StoreMigrationWindow window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(Root(window).FindName(name));

    private static TaskCompletionSource<T> NewGate<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Operations : IStoreMigrationOperations
    {
        public List<string> Calls { get; } = [];
        public StoreMigrationStartupDecision Admission { get; set; } = new(
            StoreMigrationStartupState.ConsentRequired,
            new InnoInstallation(@"C:\ui-proof-only", @"C:\ui-proof-only\app.exe",
                @"C:\ui-proof-only\unins000.exe", "x64", new Version(2026, 9, 5)));
        public bool Consent { get; set; }
        public TaskCompletionSource<bool>? CloseGate { get; set; }
        public TaskCompletionSource<StoreMigrationCompletionState>? CompleteGate { get; set; }
        public StoreMigrationPreparationState Prepared { get; set; } = StoreMigrationPreparationState.Prepared;
        public StoreMigrationFinalizationState Finalized { get; set; } = StoreMigrationFinalizationState.Finalized;

        public StoreMigrationStartupDecision Inspect()
        {
            Calls.Add("inspect");
            return Admission;
        }

        public bool HasConsent(InnoInstallation installation)
        {
            Calls.Add("consent?");
            return Consent;
        }

        public bool HoldsCompletionReceipt()
        {
            Calls.Add("receipt?");
            return false;
        }

        public bool Unreadable { get; set; }

        public StoreMigrationDiscardState Discarded { get; set; } = StoreMigrationDiscardState.Discarded;

        public bool CanDiscardRecords() => Unreadable;

        public StoreMigrationDiscardState DiscardUnreadableRecords()
        {
            Calls.Add("discard");
            if (Discarded != StoreMigrationDiscardState.Discarded)
                return Discarded;
            Unreadable = false;
            Admission = new(StoreMigrationStartupState.NotRequired);
            return StoreMigrationDiscardState.Discarded;
        }

        public void GrantConsent(InnoInstallation installation)
        {
            Calls.Add("grant");
            Consent = true;
        }

        public Task<bool> CloseSourceAsync(StoreMigrationStartupDecision admission, CancellationToken cancellationToken)
        {
            Calls.Add("close");
            return CloseGate?.Task ?? Task.FromResult(true);
        }

        public Task<StoreMigrationPreparationState> PrepareAsync(InnoInstallation installation)
        {
            Calls.Add("prepare");
            return Task.FromResult(Prepared);
        }

        public Task<StoreMigrationCompletionState> CompleteAsync(InnoInstallation installation)
        {
            Calls.Add("complete");
            return CompleteGate?.Task ?? Task.FromResult(StoreMigrationCompletionState.Completed);
        }

        public Task<StoreMigrationFinalizationDecision> FinalizeAsync()
        {
            Calls.Add("finalize");
            return Task.FromResult(new StoreMigrationFinalizationDecision(Finalized));
        }
    }
}
