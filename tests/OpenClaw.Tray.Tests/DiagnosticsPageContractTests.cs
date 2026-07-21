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
    public void DebugPage_PutsOpenTelemetryEndpointInDiagnosticsDeveloperTools()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        Assert.Contains("DiagnosticsPage_Expander_OpenTelemetry", xaml);
        Assert.Contains("OpenTelemetry connection", xaml);
        Assert.Contains("FluentIconCatalog.Telemetry", xaml);
        Assert.Contains("DiagnosticsOpenTelemetryEndpoint", xaml);
        Assert.Contains("DiagnosticsOpenTelemetryProtocolGrpc", xaml);
        Assert.Contains("DiagnosticsOpenTelemetryProtocolHttp", xaml);
        Assert.Contains("OTLP/gRPC", xaml);
        Assert.Contains("OTLP/HTTP", xaml);
        Assert.Contains("SettingsExpander.ItemsHeader", xaml);
        Assert.DoesNotContain("DiagnosticsPage_Card_OpenTelemetryEndpoint", xaml);
        Assert.Contains("Sends a small diagnostic probe only to the OTLP collector endpoint you configure.", xaml);
        Assert.Contains("OpenTelemetryEndpoint", cs);
        Assert.Contains("OpenTelemetryProtocol", cs);
        Assert.Contains("TryNormalizeOpenTelemetryEndpoint", cs);
        Assert.Contains("OpenTelemetryEndpointOptions.TryCreateEndpointUri", cs);
        Assert.Contains("CurrentApp.Settings.OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Grpc", cs);
        Assert.Contains("NotifySettingsSaved", cs);
        Assert.Contains("DiagnosticsResendOpenTelemetryProbe", xaml);
        Assert.Contains("Send probe again", xaml);
        Assert.Contains("OnResendOpenTelemetryProbe", xaml);
        Assert.Contains("ResendOpenTelemetryProbeAsync", cs);
        Assert.Contains("probeFlushed.Value ? DimTextBrush : WarnTextBrush", cs);
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

        // Connection event timeline: inner Button shape because it opens
        // a popup window rather than swapping the in-page detail view.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Uid=""DiagnosticsPage_Card_EventTimeline""[\s\S]{0,600}<Button[\s\S]{0,200}Click=""OnOpenEventTimeline"""),
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
        Assert.Contains("CopyDiagnosticText(\r\n            \"Summary debug bundle\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Browser setup guidance\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Port diagnostics\"", cs);
        Assert.Contains("CopyDiagnosticText(\"Capability diagnostics\"", cs);
    }

    [Fact]
    public void DiagnosticsGate_DefaultsVisible_ForMissingSettingsCompatibility()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "DiagnosticsGate.cs");

        Assert.Contains("public static bool BuildDefault =>", source);
        Assert.Contains("true;", source);
        Assert.DoesNotContain("PackageHelper.IsPackaged", source);
    }

    [Fact]
    public void HubWindow_RemovesDiagnosticsBackStack_WhenDiagnosticsHidden()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");

        Assert.Contains("RemoveBackStackEntries(\"debug\")", source);
        Assert.Contains("NavigateInternal(\"settings\")", source);
        Assert.Contains("RefreshDiagnosticsNavVisibility()", source);
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
    public void DebugPage_MainAndDetailViews_UseBoundedStretchLayout()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");

        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Name=""MainView""[\s\S]{0,500}<Grid HorizontalAlignment=""Stretch"">[\s\S]{0,300}<StackPanel HorizontalAlignment=""Stretch""[\s\S]{0,160}MaxWidth=""900""[\s\S]{0,160}Padding=""24,24,24,24"""),
            xaml);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Name=""DetailView""[\s\S]{0,400}HorizontalAlignment=""Stretch""[\s\S]{0,300}<Grid MaxWidth=""900""[\s\S]{0,160}HorizontalAlignment=""Stretch""[\s\S]{0,160}Padding=""24,24,24,24"""),
            xaml);
    }

    [Fact]
    public void DebugPage_UsesFluentIconCatalog_NotLiteralGlyphs()
    {
        // Per docs/design/iconography.md and AGENT_HANDOFF.md "drift
        // candidates", WinUI surfaces must route through
        // FluentIconCatalog. Page should declare the helpers xmlns
        // and bind glyphs from the catalog.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("xmlns:helpers=\"using:OpenClawTray.Helpers\"", xaml);
        Assert.Contains("x:Bind helpers:FluentIconCatalog.", xaml);

        // No literal PUA glyph hex entities in the body. We allow
        // catalog references; we forbid raw "Glyph=&#xE..."
        // declarations because that's exactly the drift the design
        // reference calls out.
        Assert.DoesNotContain("Glyph=\"&#x", xaml);
    }

    [Fact]
    public void DebugPage_UsesSystemFillBrushes_NotLiteralColors()
    {
        // Per docs/design/tokens.md status colors must use
        // SystemFillColor* tokens. Hard-coded ARGB / Color.FromArgb
        // is the drift the handoff calls out for status dots.
        // (Success/Attention tokens were used by the in-page timeline
        // coloring; the timeline now lives in ConnectionStatusWindow,
        // so only the Critical/Caution/Secondary tokens are still
        // referenced from this page — which is what we assert.)
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("SystemFillColorCriticalBrush", cs);
        Assert.Contains("SystemFillColorCautionBrush", cs);
        Assert.Contains("TextFillColorSecondaryBrush", cs);
        Assert.DoesNotContain("ColorHelper.FromArgb", cs);
    }

    [Fact]
    public void DebugPage_DoesNotDuplicateSettingsSetupEntryPoint()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.DoesNotContain("Reconfigure", xaml);
        Assert.DoesNotContain("OnRelaunchOnboarding", xaml);
        Assert.DoesNotContain("Relaunch first-run setup", xaml);

        var settings = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml");
        Assert.Contains("OnOpenLocalGatewaySetup", settings);
        Assert.Contains("Open setup", settings);
    }

    [Fact]
    public void DebugPage_DetailView_HasLogMode()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("enum DetailMode", cs);
        Assert.Contains("DetailMode.Log", cs);
        // Both entry points present. OnOpenEventTimeline opens the
        // ConnectionStatusWindow popup; OnShowRecentLog enters the
        // in-page DetailView. OnOpen* vs OnShow* mirrors the rest of
        // the page (popup vs in-page detail).
        Assert.Contains("OnOpenEventTimeline", cs);
        Assert.Contains("OnShowRecentLog", cs);
        // Back navigation present.
        Assert.Contains("OnBackToMain", cs);
    }

    [Fact]
    public void DebugPage_SharesLogColoringWithConnectionStatusWindow()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        // The severity brushes used for log line coloring mirror
        // ConnectionStatusWindow:33-40 so both surfaces speak the same
        // visual language. (AuthTextBrush was timeline-only and is
        // gone now that the timeline lives in ConnectionStatusWindow.)
        Assert.Contains("ErrorTextBrush", cs);
        Assert.Contains("WarnTextBrush", cs);
        Assert.Contains("DimTextBrush", cs);

        // Log lines must be parsed for severity so they get colored too.
        Assert.Contains("LogSeverityPattern", cs);
        Assert.Contains("CreateLogParagraph", cs);
    }

    [Fact]
    public void DiagnosticsBundleDialog_Exists_And_ExposesCopyAndSaveAndCancel()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "DiagnosticsBundleDialog.xaml");
        Assert.Contains("ContentDialog", xaml);
        Assert.Contains("PrimaryButtonText=\"Copy to clipboard\"", xaml);
        Assert.Contains("SecondaryButtonText=\"Save to file\"", xaml);
        Assert.Contains("CloseButtonText=\"Close\"", xaml);
        Assert.Contains("Height=\"420\"", xaml);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml);
        Assert.DoesNotContain("MinWidth=\"720\"", xaml);
        Assert.DoesNotContain("Width=\"720\"", xaml);

        var cs = Read("src", "OpenClaw.Tray.WinUI", "Windows", "DiagnosticsBundleDialog.xaml.cs");
        // The dialog must expose a Configure() that takes a HWND-provider
        // delegate (not a captured IntPtr) so we can resolve the host
        // window handle JUST-IN-TIME when Save is clicked, instead of
        // trusting a possibly-stale handle captured at dialog open
        // (Hanselman v2 review #4).
        Assert.Contains("public void Configure(", cs);
        Assert.Contains("Func<IntPtr>", cs);
        Assert.Contains("hwndProvider", cs);
        Assert.DoesNotContain("SaveToDesktopAsync", cs);
        Assert.Contains("DiagnosticsBundleDialog save skipped: no host hwnd", cs);
        // It must NOT auto-close on Copy — the user may want to also save.
        Assert.Contains("args.Cancel = true", cs);
    }

    [Fact]
    public void AppLogger_LogsFullExceptionDetails()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "AppLogger.cs");
        Assert.Contains(@"{message}: {ex}", cs);
        Assert.DoesNotContain(@"{message}: {ex.Message}", cs);
    }

    [Fact]
    public void SettingsPage_HostsAboutAndGatewayInfoAfterAboutPageRemoval()
    {
        var settingsXaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml");
        var settingsCs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs");
        var hub = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");

        Assert.Contains("SettingsPage_AppInfoExpander", settingsXaml);
        Assert.Contains("SettingsPage_GatewayInfoExpander", settingsXaml);
        Assert.Contains("OnCheckUpdates", settingsXaml);
        Assert.Contains("OnDocumentationLink", settingsXaml);
        Assert.Contains("OnGitHubLink", settingsXaml);
        Assert.Contains("OnDashboardLink", settingsXaml);
        Assert.Contains("RefreshGatewayInfo", settingsCs);
        Assert.Contains("\"info\" => typeof(SettingsPage)", hub);
        Assert.Contains("\"about\" => typeof(SettingsPage)", hub);
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml")));
        Assert.False(File.Exists(Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml.cs")));
    }

    [Fact]
    public void HubWindow_DebugNavItem_RoutesUnchanged_LabelRenamed()
    {
        // The Tag must still be "debug" so command-palette / deep-link
        // aliases keep working, even though the visible label is now
        // "Diagnostics".
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml");
        Assert.Contains("Tag=\"debug\"", xaml);

        var resw = Read("src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");
        Assert.Contains("<data name=\"HubWindow_NavigationViewItem_145.Content\"", resw);
        // The resw entry must now say Diagnostics.
        var navEntryStart = resw.IndexOf("<data name=\"HubWindow_NavigationViewItem_145.Content\"", StringComparison.Ordinal);
        var navEntryEnd = resw.IndexOf("</data>", navEntryStart, StringComparison.Ordinal);
        var entry = resw.Substring(navEntryStart, navEntryEnd - navEntryStart);
        Assert.Contains("Diagnostics", entry);

        // Internal route mapping unchanged.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        Assert.Contains("\"debug\" => typeof(DebugPage)", cs);
    }

    [Fact]
    public void HubWindow_NavPaneToggle_LivesInTitleBarAndHidesBuiltInToggle()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml");
        Assert.Contains("x:Uid=\"NavPaneToggleButton\"", xaml);
        Assert.Contains("x:Name=\"NavPaneToggleButton\"", xaml);
        Assert.Contains("Click=\"OnNavPaneToggleButtonClick\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Toggle navigation pane\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"Toggle navigation pane\"", xaml);
        Assert.Contains("MinWidth=\"32\" MinHeight=\"32\"", xaml);
        Assert.Contains("Padding=\"9,0,140,0\"", xaml);
        Assert.Contains("Background=\"Transparent\"", xaml);
        Assert.Contains("BorderBrush=\"Transparent\"", xaml);
        Assert.Contains("BorderThickness=\"0\"", xaml);
        Assert.Contains("FontSize=\"16\"", xaml);
        Assert.Contains("xmlns:controls=\"using:OpenClawTray.Controls\"", xaml);
        // The brand mark is merged into the pane toggle: hovering swaps the lobster for the toggle glyph.
        Assert.Contains("<controls:BrandMark x:Name=\"BrandToggleMark\"", xaml);
        Assert.Contains("MarkSize=\"18\"", xaml);
        Assert.Contains("x:Name=\"BrandToggleGlyph\"", xaml);
        // The reveal glyph is the Fluent DockLeft (panel-left) icon, not the hamburger.
        Assert.Contains("Glyph=\"&#xE90C;\"", xaml);
        Assert.Contains("PointerEntered=\"OnBrandTogglePointerEntered\"", xaml);
        Assert.Contains("PointerExited=\"OnBrandTogglePointerExited\"", xaml);
        Assert.DoesNotContain("Translation=\"0,-1,0\"", xaml);
        Assert.Contains("IsPaneToggleButtonVisible=\"False\"", xaml);
        Assert.Contains("x:Name=\"NavContentHost\"", xaml);
        Assert.Contains("x:Name=\"NavContentClip\"", xaml);
        Assert.Contains("SizeChanged=\"OnNavContentHostSizeChanged\"", xaml);
        Assert.DoesNotContain("x:Name=\"TitleContentDivider\"", xaml);

        var titleBarIndex = xaml.IndexOf("x:Name=\"AppTitleBar\"", StringComparison.Ordinal);
        var toggleIndex = xaml.IndexOf("x:Name=\"NavPaneToggleButton\"", StringComparison.Ordinal);
        var iconIndex = xaml.IndexOf("<controls:BrandMark x:Name=\"BrandToggleMark\"", StringComparison.Ordinal);
        var navViewIndex = xaml.IndexOf("x:Name=\"NavView\"", StringComparison.Ordinal);
        Assert.True(titleBarIndex >= 0, "The hub title bar must exist.");
        Assert.True(toggleIndex > titleBarIndex, "The nav pane toggle must live inside the title bar block.");
        Assert.True(toggleIndex < iconIndex, "The brand mark must live inside the nav pane toggle button.");
        Assert.True(toggleIndex < navViewIndex, "The nav pane toggle must be outside the NavigationView pane.");

        // The back button now sits directly left of the search box (Teams style),
        // after the title and before the search field.
        // The back button uses the Fluent ChevronLeft glyph and shares the search's centered group.
        Assert.Contains("Glyph=\"&#xE76B;\"", xaml);
        // Both title-bar icon buttons carry a subtle hover/pressed affordance.
        Assert.Contains("<SolidColorBrush x:Key=\"ButtonBackgroundPointerOver\" Color=\"{ThemeResource SubtleFillColorSecondary}\"/>", xaml);
        Assert.Contains("<SolidColorBrush x:Key=\"ButtonBackgroundPressed\" Color=\"{ThemeResource SubtleFillColorTertiary}\"/>", xaml);

        var backIndex = xaml.IndexOf("x:Name=\"NavBackButton\"", StringComparison.Ordinal);
        var titleTextIndex = xaml.IndexOf("x:Name=\"TitleText\"", StringComparison.Ordinal);
        var searchIndex = xaml.IndexOf("x:Name=\"TitleSearchBox\"", StringComparison.Ordinal);
        Assert.True(backIndex > titleTextIndex, "The back button must appear after the title.");
        Assert.True(backIndex < searchIndex, "The back button must appear directly before the search box.");

        var cs = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        Assert.Contains("private void OnNavPaneToggleButtonClick", cs);
        Assert.Contains("NavView.IsPaneOpen = !NavView.IsPaneOpen;", cs);
        Assert.Contains("private void OnBrandTogglePointerEntered", cs);
        Assert.Contains("private void OnBrandTogglePointerExited", cs);
        Assert.Contains("private void OnNavContentHostSizeChanged", cs);
        Assert.Contains("NavContentClip.Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);", cs);
    }

    [Fact]
    public void DebugPage_ObservesAppState_NotHubWindow()
    {
        // After Ranjesh's single-app-model rebase, the page must
        // observe AppState directly per
        // docs/DATA_FLOW_ARCHITECTURE.md and not depend on HubWindow.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("private static App CurrentApp", cs);
        Assert.Contains("AppState? _appState", cs);
        Assert.Contains("_appState.PropertyChanged", cs);
        // App provides BuildCommandCenterState() so the bundle preview
        // dialog can render text without going through HubWindow.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("internal GatewayCommandCenterState BuildCommandCenterState", app);
        // HubWindow no longer plumbs a state-action callback for pages.
        var hub = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        Assert.DoesNotContain("GetCommandCenterStateAction", hub);
    }

    [Fact]
    public void DebugPage_RefreshesOnSettingsChanged()
    {
        // The Status InfoBar shows the effective Gateway URL from
        // SettingsManager. Settings-saved events must update the page
        // immediately rather than waiting for the next Status flip
        // (reactive-by-default ethos per docs/DATA_FLOW_ARCHITECTURE.md).
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("CurrentApp.SettingsChanged += OnSettingsChanged", cs);
        Assert.Contains("CurrentApp.SettingsChanged -= OnSettingsChanged", cs);
        Assert.Contains("OnSettingsChanged", cs);
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
    public void CommandCenterTextHelper_DebugBundle_IsSummaryOnlyWithoutLogTail()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        var resources = Read("src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

        Assert.DoesNotContain("Recent Tray Log", helper);
        Assert.DoesNotContain("DiagnosticsLogTailReader.BuildSection", helper);
        Assert.DoesNotContain("DiagnosticsTailOptions", helper);
        Assert.DoesNotContain("RecentTrayLogTailLines", helper);
        Assert.DoesNotContain("RecentTrayLogMaxChars", helper);
        Assert.DoesNotContain("BuildRecentTrayLogTail", helper);
        Assert.DoesNotContain("builder.AppendLine(line)", helper);
        Assert.Contains("Generated summaries only; excludes log tails.", xaml);
        Assert.Contains("Generated summaries only; excludes log tails.", resources);
        Assert.DoesNotContain("sanitized recent tray log", xaml);
        Assert.DoesNotContain("sanitized recent tray log", resources);
    }

    [Fact]
    public void DiagnosticsCopyAndExport_UseSideEffectFreeExportSanitizer()
    {
        var page = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        var service = Read("src", "OpenClaw.Tray.WinUI", "Services", "DiagnosticsClipboardService.cs");
        var builder = Read("src", "OpenClaw.Tray.WinUI", "Services", "DiagnosticsBundleBuilder.cs");

        Assert.Contains("DiagnosticsExportSanitizer.SanitizeTextBlock", page);
        Assert.Contains("DiagnosticsExportSanitizer.SanitizeTextBlock", service);
        Assert.Contains(
            "DiagnosticsExportSanitizer.SanitizeTextBlock(_plainBuffer.ToString())",
            Read("src", "OpenClaw.Tray.WinUI", "Windows", "ConnectionStatusWindow.xaml.cs"));
        Assert.Contains("ReadSanitizedTail", page);
        Assert.DoesNotContain("ReadLogTail(", page);
        Assert.Contains("CommandCenterTextHelper.BuildDebugBundle", page);
        Assert.Contains("CommandCenterTextHelper.BuildDebugBundle", service);
        Assert.DoesNotContain("DiagnosticsBundleBuilder.Build(state)", service);
        Assert.DoesNotContain("NormalizePersistedDiagnosticsLogs", builder);
        Assert.DoesNotContain("File.WriteAllLines", builder);
        Assert.Contains("Export is read-only", builder);
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
        Assert.Contains("FormatRecordLine(eventName, metadata)", diagnosticsJsonl);
        Assert.Contains("metadata = SanitizeMetadata(metadata)", diagnosticsJsonl);
        Assert.Contains("IsSensitiveMetadataKey(propertyName)", diagnosticsJsonl);
        Assert.Contains("TokenSanitizer.IsSensitiveMetadataKeyName(key)", diagnosticsJsonl);
        Assert.DoesNotContain("TokenSanitizer.SanitizeLogMessage(JsonSerializer.Serialize(record, JsonOptions))", diagnosticsJsonl);

        var crashLogger = Read("src", "OpenClaw.Tray.WinUI", "Services", "AppCrashLogger.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage", crashLogger);
    }

    [Fact]
    public void FullDiagnosticsBundle_IsUsedForPreviewOnly()
    {
        var page = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("OnCreateDiagnosticsBundle", page);
        Assert.Contains("DiagnosticsBundleBuilder.Build", page);
        Assert.Contains("ShowBundlePreviewAsync", page);

        var handlerStart = page.IndexOf("private void OnCopyDebugBundle", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "OnCopyDebugBundle must exist.");
        var handlerBody = page.Substring(handlerStart, Math.Min(260, page.Length - handlerStart));

        Assert.Contains("CommandCenterTextHelper.BuildDebugBundle", handlerBody);
        Assert.DoesNotContain("DiagnosticsBundleBuilder.BuildCached", handlerBody);
    }

    [Fact]
    public void DebugPage_DetailView_UsesGenerationCounterForRaceSafety()
    {
        // Hanselman v2 review #5/#6: long log reads must check a
        // generation counter after their async continuation so a
        // page navigation mid-flight can't clobber the active view
        // (or, post-popup, write into a no-longer-current generation).
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("_detailGeneration", cs);
        // LoadLogFileAsync takes the generation as a parameter.
        Assert.Contains("LoadLogFileAsync(int generation)", cs);
        // Log mode re-checks both mode AND generation after the
        // background sanitized tail read returns.
        Assert.Contains("_detailMode != DetailMode.Log || _detailGeneration != generation", cs);
        // Manual refresh must also invalidate any in-flight read, otherwise
        // rapid refresh clicks can let multiple reads append duplicate rows
        // into the same detail view generation.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"OnDetailRefresh[\s\S]{0,200}_detailGeneration\+\+[\s\S]{0,120}LoadLogFileAsync\(_detailGeneration\)"),
            cs);
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
        Assert.Contains("void ApplyUiSettingsAndNotify()", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue(ApplyUiSettingsAndNotify)", app);
    }
}
