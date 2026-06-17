using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class DiagnosticsPageContractTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void DebugPage_UsesToolkitSettingsCard_NotRawExpander()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // Toolkit namespace must be declared.
        Assert.Contains("xmlns:toolkit=\"using:CommunityToolkit.WinUI.Controls\"", xaml);
        // At least the primary card uses SettingsCard.
        Assert.Contains("toolkit:SettingsCard", xaml);
        // The page must not have the chaotic flat list of <Expander> cards
        // it had before the redesign. Stock <Expander> is still allowed
        // elsewhere if needed, but we assert the page now uses Toolkit
        // SettingsExpander as the grouping primitive for sub-items.
        Assert.Contains("toolkit:SettingsExpander", xaml);
    }

    [Fact]
    public void DebugPage_HasThreeTaskOrientedSections()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("Share diagnostics with support", xaml);
        Assert.Contains("Inspect local diagnostics", xaml);
        Assert.Contains("Developer tools", xaml);
    }

    [Fact]
    public void DebugPage_GatewayDoctorCard_IsGatedOnWslControlAndRunsDoctor()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        // Whole-row clickable card that runs the doctor handler and uses the
        // catalog Doctor glyph (not a literal or chevron).
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Uid=""DiagnosticsPage_Card_Doctor""[\s\S]{0,500}IsClickEnabled=""True""[\s\S]{0,200}Click=""OnRunGatewayDoctor"""),
            xaml);
        Assert.Contains("FluentIconCatalog.Doctor", xaml);

        // Section is collapsed by default; visibility is driven by the
        // app-managed-WSL control gate (CanControlWslGateway), and the handler
        // launches a terminal via OpenGatewayDoctor rather than capturing output.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Name=""GatewayDoctorSection""[\s\S]{0,200}Visibility=""Collapsed"""),
            xaml);
        Assert.Contains("CanControlWslGateway", cs);
        Assert.Contains("OpenGatewayDoctor", cs);
    }

    [Fact]
    public void DebugPage_SurfacesAllExistingDiagnosticCommands()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // The four diagnostic-text commands that already existed in
        // App.xaml.cs but were invisible on the page before the redesign
        // must now have UI entry points.
        Assert.Contains("OnCopySupportContext", xaml);
        Assert.Contains("OnCopyDebugBundle", xaml);
        Assert.Contains("OnCopyBrowserSetup", xaml);
        Assert.Contains("OnCopyPortDiagnostics", xaml);
        Assert.Contains("OnCopyCapabilityDiagnostics", xaml);
        // Primary bundle action opens the preview dialog instead of
        // copying silently.
        Assert.Contains("OnCreateDiagnosticsBundle", xaml);
    }

    [Fact]
    public void DebugPage_LeadsWithStatusInfoBar_NotIdentity()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        var statusIndex = xaml.IndexOf("StatusInfoBar", StringComparison.Ordinal);
        var identityIndex = xaml.IndexOf("DeviceIdText", StringComparison.Ordinal);
        Assert.True(statusIndex > 0, "Status InfoBar must be present");
        Assert.True(identityIndex > 0, "Device identity must be present");
        // Rubber-duck fix v2 #4: identity is not the lead of the page.
        Assert.True(statusIndex < identityIndex,
            "Status InfoBar must appear before Device identity in the XAML.");
    }

    [Fact]
    public void DebugPage_TimelineOpensConnectionStatusWindow_AsPopup()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        // The "Connection event timeline" SettingsCard button wires to
        // OnOpenEventTimeline, and that handler pops up the standalone
        // ConnectionStatusWindow rather than swapping the in-page
        // DetailView. Mirrors how "Open chat explorations" launches
        // ChatExplorationsWindow, per the user's "bring it back as a
        // popup" feedback on the redesign. The OnOpen* name matches
        // the popup-launching convention used elsewhere on the page
        // (OnOpenChatExplorations, OnOpenDiagnosticsFolder), while
        // OnShow* is reserved for entering the in-page DetailView
        // (OnShowRecentLog).
        Assert.Contains("Click=\"OnOpenEventTimeline\"", xaml);
        // The handler delegates to IAppCommands.ShowConnectionStatus,
        // reusing App.ShowConnectionStatusWindow() (which already
        // owns reuse-if-not-closed lifetime + Activate() behavior).
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"OnOpenEventTimeline[\s\S]{0,200}IAppCommands[\s\S]{0,80}ShowConnectionStatus"),
            cs);
        // The redundant "View event timeline" hyperlink that used to
        // live inside the top Status InfoBar (alongside "Manage on
        // Connection page") is gone — the user removed it as a dupe
        // of the SettingsCard right below.
        Assert.DoesNotContain("DiagnosticsPage_OpenEventTimeline\"", xaml);
        Assert.DoesNotContain("DiagnosticsViewEventTimeline", xaml);
        // The old handler name must not creep back; OnShow* implies
        // in-page DetailView (the very thing the user rejected for
        // the connection timeline).
        Assert.DoesNotContain("OnShowEventTimeline", cs);
        Assert.DoesNotContain("OnShowEventTimeline", xaml);
        // Belt-and-suspenders: the dead in-page timeline plumbing
        // must not creep back. These names belonged to the old
        // DetailMode.Timeline path and would re-introduce the
        // in-page render the user explicitly rejected.
        Assert.DoesNotContain("DetailMode.Timeline", cs);
        Assert.DoesNotContain("LoadTimelineEvents", cs);
        Assert.DoesNotContain("OnTimelineEventRecorded", cs);
        Assert.DoesNotContain("SubscribeTimeline", cs);
    }

    [Fact]
    public void DebugPage_RecentLogCard_IsClickableWholeRow_NotButton()
    {
        // Per user feedback: the Recent log card just uses the
        // standard SettingsCard chevron (whole row is the affordance);
        // there's no separate "Open" Button. The Connection event
        // timeline card keeps its Button because clicking it opens a
        // popup window (heavier action that benefits from a distinct
        // hit target). Pin both shapes here so they don't drift.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");

        // Recent log: card-level IsClickEnabled + Click, NO inner Button.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Uid=""DiagnosticsPage_Card_RecentLog""[\s\S]{0,400}IsClickEnabled=""True""[\s\S]{0,200}Click=""OnShowRecentLog"""),
            xaml);
        Assert.DoesNotContain("DiagnosticsPage_OpenRecentLogButton", xaml);

        // Open chat explorations: inner Button shape — the row is no
        // longer the click target. Mirrors the popup-launching shape
        // of the Connection event timeline card.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Uid=""DiagnosticsPage_Card_ChatExplorations""[\s\S]{0,600}<Button[\s\S]{0,200}Click=""OnOpenChatExplorations"""),
            xaml);
    }

    [Fact]
    public void DebugPage_CopySpecificCards_HaveCopyGlyph_NotChevron_AndFeedback()
    {
        // Per user feedback: the cards under "Copy specific diagnostic
        // text" should not display the standard right-chevron
        // ActionIcon — clicking them copies to the clipboard, so a
        // Copy glyph telegraphs the action better. And there must be
        // a visible "Copied to clipboard" feedback notice on success.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");

        // Each Copy* SettingsCard must override ActionIcon with the
        // FluentIconCatalog.Copy glyph. Match the structural pattern
        // (Card → ActionIcon → FontIcon → FluentIconCatalog.Copy)
        // rather than counting occurrences, since the SettingsExpander
        // header itself uses the same glyph.
        foreach (var cardUid in new[]
        {
            "DiagnosticsPage_Card_CopySupport",
            "DiagnosticsPage_Card_CopyDebugBundle",
            "DiagnosticsPage_Card_CopyBrowserSetup",
            "DiagnosticsPage_Card_CopyPortDiagnostics",
            "DiagnosticsPage_Card_CopyCapabilityDiagnostics",
        })
        {
            Assert.Matches(
                new System.Text.RegularExpressions.Regex(
                    $@"x:Uid=""{cardUid}""[\s\S]{{0,600}}SettingsCard\.ActionIcon[\s\S]{{0,200}}FluentIconCatalog\.Copy"),
                xaml);
        }

        // The transient "Copied to clipboard" feedback InfoBar must
        // be on the page and start collapsed (IsOpen=False).
        Assert.Contains("x:Name=\"CopyFeedbackInfoBar\"", xaml);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Name=""CopyFeedbackInfoBar""[\s\S]{0,400}IsOpen=""False"""),
            xaml);

        // The C# side opens the InfoBar after a successful copy and
        // schedules an auto-dismiss via DispatcherTimer so it doesn't
        // linger once the user has moved on.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("ShowCopyFeedback", cs);
        Assert.Contains("CopyFeedbackInfoBar.IsOpen = true", cs);
        Assert.Contains("CopyFeedbackInfoBar.IsOpen = false", cs);
        Assert.Contains("DispatcherTimer", cs);
        // Each copy handler must pass a human-readable label that
        // shows up in the feedback message.
        Assert.Contains("CopyDiagnosticText(\"Support context\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Debug bundle\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Browser setup guidance\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Port diagnostics\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Capability diagnostics\"", cs);
    }

    [Fact]
    public void DebugPage_CopyFeedbackTimer_IsStoppedOnTeardown()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        Assert.Contains("private void StopCopyFeedbackTimer()", cs);
        Assert.Matches(
            new Regex(
                @"StopCopyFeedbackTimer\(\)[\s\S]{0,400}_copyFeedbackTimer\.Stop\(\)[\s\S]{0,200}_copyFeedbackTimer = null"),
            cs);
        Assert.Matches(new Regex(@"Unloaded \+=[\s\S]{0,400}StopCopyFeedbackTimer\(\)"), cs);
        Assert.Matches(new Regex(@"OnNavigatedFrom[\s\S]{0,400}StopCopyFeedbackTimer\(\)"), cs);
        Assert.Contains("_copyFeedbackTimer?.Stop()", cs);
        Assert.Contains("CopyFeedbackInfoBar.IsLoaded", cs);
        Assert.DoesNotContain("_copyFeedbackTimer!.Stop()", cs);
    }

    [Fact]
    public void DebugPage_DetailView_UsesGenerationCounterForRaceSafety()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        Assert.Contains("_detailGeneration", cs);
        Assert.Contains("LoadLogFileAsync(int generation)", cs);
        Assert.Contains("_detailMode != DetailMode.Log || _detailGeneration != generation", cs);
        Assert.Matches(
            new Regex(
                @"OnDetailRefresh[\s\S]{0,200}_detailGeneration\+\+[\s\S]{0,120}LoadLogFileAsync\(_detailGeneration\)"),
            cs);
    }

    [Fact]
    public void CommandCenterTextHelper_SupportContext_AdvertisesRedaction()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");

        Assert.Contains("Excluded:", helper);
        Assert.Contains("tokens", helper);
        Assert.Contains("bootstrap tokens", helper);
    }

    [Fact]
    public void CommandCenterTextHelper_DebugBundle_IncludesSanitizedTrayLogTail()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");

        Assert.Contains("Recent Tray Log", helper);
        Assert.Contains("BuildRecentTrayLogTail(Logger.LogFilePath)", helper);
        Assert.Contains("TokenSanitizer.SanitizeLogMessage(line)", helper);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", helper);
    }

    [Fact]
    public void CommandCenterTextHelper_NodeInventoryIncludesTrustDiagnostics()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");

        Assert.Contains("BuildNodeInventorySummary", helper);
        Assert.Contains("OpenClaw node inventory", helper);
        Assert.Contains("Approved/effective capabilities", helper);
        Assert.Contains("Approved/effective commands", helper);
        Assert.Contains("Pending declared capabilities", helper);
        Assert.Contains("Pending declared commands", helper);
        Assert.Contains("Local declared/unverified capabilities", helper);
        Assert.Contains("Local declared/unverified commands", helper);
        Assert.Contains("Approval command", helper);
        Assert.Contains("Pending request discovery command", helper);
        Assert.Contains("TryBuildNodeApprovalCommand", helper);
        Assert.Contains("Safe approved commands", helper);
        Assert.Contains("Privacy-sensitive approved commands", helper);
        Assert.Contains("Browser proxy approved commands", helper);
        Assert.Contains("Missing browser proxy allowlist", helper);
        Assert.Contains("Disabled in Settings", helper);
        Assert.Contains("Missing Mac parity", helper);
        Assert.DoesNotContain("NodePairApproveAsync", helper);
    }

    [Fact]
    public void TrayLogWriters_SanitizeSensitiveValuesBeforeWriting()
    {
        var logger = Read("src", "OpenClaw.Tray.WinUI", "Services", "Logger.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage(message)", logger);

        var diagnosticsJsonl = Read("src", "OpenClaw.Tray.WinUI", "Services", "DiagnosticsJsonlService.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage(JsonSerializer.Serialize(record))", diagnosticsJsonl);

        var crashLogger = Read("src", "OpenClaw.Tray.WinUI", "Services", "AppCrashLogger.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage", crashLogger);
    }

    [Fact]
    public void App_GetHubWindowHandle_GuardsAgainstClosedWindow()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("public IntPtr GetHubWindowHandle()", app);
        Assert.Contains("_hubWindow != null && !_hubWindow.IsClosed", app);
    }

    [Fact]
    public void App_SettingsChanged_DispatchesToUiThread()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("_dispatcherQueue.HasThreadAccess", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue(() => SettingsChanged?.Invoke", app);
    }
}
