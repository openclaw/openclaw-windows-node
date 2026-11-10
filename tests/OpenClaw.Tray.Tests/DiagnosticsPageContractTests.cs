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
        Assert.Contains("CopyDiagnosticText(\n            \"Summary debug bundle\"", cs.Replace("\r\n", "\n"));
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
    public void SettingsShellView_PrunesDiagnosticsHistory_WhenDiagnosticsHidden()
    {
        var view = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        var workspace = Read("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs");
        var manager = Read("src", "OpenClaw.Tray.WinUI", "Services", "WindowManager.cs");

        // The view asks the one host history to drop "debug"; it keeps no stack of its own.
        Assert.Contains("PruneRequested?.Invoke(tag => string.Equals(tag, \"debug\", StringComparison.Ordinal), \"settings\")", view);
        Assert.Contains("if (tag == \"debug\" && !DiagnosticsGate.IsVisible)", view);
        Assert.Contains("tag = \"settings\";", view);
        Assert.Contains("RefreshDiagnosticsNavVisibility()", view);
        Assert.DoesNotContain("RemoveBackStackEntries", view);
        Assert.DoesNotContain("BackStack", view);
        Assert.Contains("PruneRequested = PruneSettingsHistory", workspace);
        Assert.Contains("_navigation.PruneSettings(", workspace);
        Assert.Contains("_workspaceWindow?.SettingsView?.RefreshDiagnosticsNavVisibility()", manager);
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
    public void General_KeepsNotificationPreferences_WhileAboutOwnsUpdatesAndAppInfo()
    {
        var settingsXaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml");
        var settingsCs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs");
        var aboutXaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml");
        var aboutCs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml.cs");
        var registry = Read("src", "OpenClaw.Tray.WinUI", "Presentation", "HubPageRegistry.cs");
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.DoesNotContain("SettingsPage_AppInfoExpander", settingsXaml);
        Assert.DoesNotContain("SettingsPage_GatewayInfoExpander", settingsXaml);
        Assert.Contains("SettingsPage_Notifications", settingsXaml);
        Assert.DoesNotContain("RefreshGatewayInfo", settingsCs);
        Assert.DoesNotContain("StoreMigrationCard", settingsXaml);
        Assert.Contains("SettingsPage_AppInfoExpander", aboutXaml);
        Assert.Contains("SettingsPage_GatewayInfoExpander", aboutXaml);
        Assert.Contains("OnCheckUpdates", aboutXaml);
        Assert.Contains("OnDocumentationLink", aboutXaml);
        Assert.Contains("OnGitHubLink", aboutXaml);
        Assert.Contains("OnDashboardLink", aboutXaml);
        Assert.Contains("RefreshGatewayInfo", aboutCs);
        Assert.Contains("SettingsAppInfoProjection.ResolveDisplayVersion(", aboutCs);
        Assert.Contains("PackageHelper.PackageVersion", aboutCs);
        Assert.Contains("Click=\"OnTestNotification\"", settingsXaml);
        Assert.Contains("new ToastContentBuilder()", settingsCs);
        var general = System.Xml.Linq.XDocument.Parse(settingsXaml);
        foreach (var (id, property, binding) in new[]
        {
            ("SettingsPageNotifications", "IsOn", "ShowNotifications"),
            ("SettingsPageSound", "SelectedValue", "NotificationSound"),
            ("SettingsPageNotifyHealth", "IsChecked", "NotifyHealth"),
            ("SettingsPageNotifyUrgent", "IsChecked", "NotifyUrgent"),
            ("SettingsPageNotifyReminder", "IsChecked", "NotifyReminder"),
            ("SettingsPageNotifyEmail", "IsChecked", "NotifyEmail"),
            ("SettingsPageNotifyCalendar", "IsChecked", "NotifyCalendar"),
            ("SettingsPageNotifyBuild", "IsChecked", "NotifyBuild"),
            ("SettingsPageNotifyStock", "IsChecked", "NotifyStock"),
            ("SettingsPageNotifyInfo", "IsChecked", "NotifyInfo"),
        })
        {
            var control = Assert.Single(general.Descendants(),
                element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);
            Assert.Equal($"{{Binding {binding}, Mode=TwoWay}}", (string?)control.Attribute(property));
        }
        Assert.Contains("\"info\" or \"about\" => HubPageKind.About", registry);
        Assert.Contains("HubPageKind.Settings => typeof(SettingsPage)", registry);
        Assert.Contains("HubPageKind.About => typeof(AboutPage)", registry);
        Assert.DoesNotContain("NotificationPreferencesPage", registry);
        Assert.DoesNotContain("NotificationPreferencesPage", app);
        Assert.Contains("[typeof(Pages.SettingsPage)] = typeof(SettingsPageViewModel)", app);
    }

    [Fact]
    public void SettingsShellView_DebugNavItem_RoutesUnchanged_LabelRenamed()
    {
        // The Tag must still be "debug" so command-palette / deep-link
        // aliases keep working, even though the visible label is now
        // "Diagnostics".
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml");
        Assert.Contains("Tag=\"debug\"", xaml);

        var resw = Read("src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");
        Assert.Contains("<data name=\"HubWindow_NavigationViewItem_145.Content\"", resw);
        // The resw entry must now say Diagnostics.
        var navEntryStart = resw.IndexOf("<data name=\"HubWindow_NavigationViewItem_145.Content\"", StringComparison.Ordinal);
        var navEntryEnd = resw.IndexOf("</data>", navEntryStart, StringComparison.Ordinal);
        var entry = resw.Substring(navEntryStart, navEntryEnd - navEntryStart);
        Assert.Contains("Diagnostics", entry);

        // Internal route mapping unchanged.
        var registry = Read("src", "OpenClaw.Tray.WinUI", "Presentation", "HubPageRegistry.cs");
        Assert.Contains("\"debug\" => HubPageKind.Debug", registry);
        Assert.Contains("HubPageKind.Debug => typeof(DebugPage)", registry);
    }

    [Fact]
    public void SettingsShellView_SearchUsesSharedPopupWithReadableResultTitles()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml");
        Assert.Contains("<controls:SearchHost", xaml);
        Assert.DoesNotContain("<AutoSuggestBox", xaml);
        var host = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SearchHost.xaml");
        Assert.Contains("Text=\"{Binding Title}\"", host);
        Assert.Contains("Text=\"{Binding Detail}\"", host);
    }

    [Fact]
    public void SettingsShellView_NavigationToolbar_OwnsPaneAndSearchOnly_WithHistoryInWorkspace()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml");
        var document = System.Xml.Linq.XDocument.Parse(xaml);
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        // The embedded view has no title bar of its own; the Workspace title bar is the only one.
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "TitleBar");
        var header = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SettingsNavigationToolbar");
        var buttons = header.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Equal(new[] { "NavPaneToggleButton", "SettingsSearchButton" },
            buttons.Select(element => (string?)element.Attribute(x + "Name")));
        Assert.All(buttons, button =>
        {
            Assert.Equal("{StaticResource SubtleButtonStyle}", (string?)button.Attribute("Style"));
            Assert.Equal("40", (string?)button.Attribute("Height"));
            Assert.Equal("40", (string?)button.Attribute("Width"));
        });
        var toggle = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NavPaneToggleButton");
        Assert.Equal("SettingsNavigationToolbar", (string?)toggle.Parent!.Attribute(x + "Name"));
        Assert.Contains("IsPaneToggleButtonVisible=\"False\"", xaml);
        Assert.DoesNotContain("NavBackButton", xaml);
        Assert.DoesNotContain("NavForwardButton", xaml);

        var cs = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        Assert.Contains("NavView.IsPaneOpen = !NavView.IsPaneOpen;", cs);
        Assert.Contains("NavView.IsPaneOpen ? SettingsSearchButton : NavPaneToggleButton", cs);
        Assert.Contains("_commandSearchShow.ShowAt(SearchAnchor)", cs);
        Assert.Contains("new FlyoutShowRequest(CommandSearchFlyout, () => (CommandCenterRequested ?? OpenCommandCenter)())", cs);
        Assert.Contains("NavContentClip.Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);", cs);
        Assert.DoesNotContain("PreviewKeyDown=\"", xaml.Replace("PreviewKeyDown=\"OnCommandSearchKeyDown\"", ""));
        foreach (var banned in new[] { "GoBack", "GoForward", "BackStack", "ForwardStack", "CanGoBack", "CanGoForward", "IconSource", "AppTitleBar" })
            Assert.DoesNotContain(banned, cs);
        Assert.DoesNotMatch(@"new\s+ChatPage\b|typeof\(ChatPage\)|\bMountReactorChat\b", cs);

        // The single Back/Forward pair, history, and Alt+Left/Right belong to WorkspaceWindow.
        var workspaceXaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml");
        var workspace = Read("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs");
        foreach (var name in new[] { "BackButton", "ForwardButton", "SettingsBackButton", "SettingsForwardButton" })
            Assert.Contains($"x:Name=\"{name}\"", workspaceXaml);
        Assert.Equal(4, Regex.Matches(workspaceXaml, "Click=\"On(?:Back|Forward)\"").Count);
        Assert.Contains("SettingsBackButton.IsEnabled = back;", workspace);
        Assert.Contains("SettingsForwardButton.IsEnabled = forward;", workspace);
        AssertInOrderIn(workspace,
            "alt && e.Key == global::Windows.System.VirtualKey.Left", "NavigateBack();",
            "alt && e.Key == global::Windows.System.VirtualKey.Right", "NavigateForward();");
    }

    private static void AssertInOrderIn(string source, params string[] fragments)
    {
        var cursor = 0;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected '{fragment}' after position {cursor}.");
            cursor = index + fragment.Length;
        }
    }

    [Fact]
    public void WorkspaceAndSettings_ShareOnePaneFooter_AndDiagnosticsLivesInSystem()
    {
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var settings = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml"));
        var workspace = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml"));
        foreach (var (document, prefix) in new[] { (settings, "Settings"), (workspace, "Workspace") })
        {
            var paneFooter = document.Descendants().Single(element => element.Name.LocalName == "NavigationView.PaneFooter");
            var footer = Assert.Single(paneFooter.Elements());
            Assert.Equal("WorkspacePaneFooter", footer.Name.LocalName);
            Assert.Equal(prefix, (string?)footer.Attribute("AutomationIdPrefix"));
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "NavigationView.FooterMenuItems");
            foreach (var name in new[] { "OwnerButton", "NotificationsButton", "DashboardButton", "StatusPillButton" })
                Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
        }

        var menu = settings.Descendants().Single(element => element.Name.LocalName == "NavigationView.MenuItems");
        var thisComputer = menu.Elements()
            .SkipWhile(element => (string?)element.Attribute("Content") != "This Computer")
            .Skip(1)
            .TakeWhile(element => element.Name.LocalName is not ("NavigationViewItemHeader" or "NavigationViewItemSeparator"))
            .ToList();
        var tags = thisComputer.Select(element => (string?)element.Attribute("Tag")).ToArray();
        Assert.Equal(new[] { "voice", "permissions", "sandbox" }, tags);

        var system = menu.Elements()
            .SkipWhile(element => (string?)element.Attribute("Content") != "System")
            .Skip(1)
            .TakeWhile(element => element.Name.LocalName is not ("NavigationViewItemHeader" or "NavigationViewItemSeparator"))
            .ToList();
        var systemTags = system.Select(element => (string?)element.Attribute("Tag")).ToArray();
        Assert.Equal(new[] { "settings", "debug", "about" }, systemTags);
        Assert.Equal("SettingsNavDiagnostics", (string?)system[1].Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("SettingsNavGeneral", (string?)system[0].Attribute("AutomationProperties.AutomationId"));

        var connection = menu.Elements().Single(element => (string?)element.Attribute("Tag") == "connection");
        Assert.Equal("SettingsNavConnection", (string?)connection.Attribute("AutomationProperties.AutomationId"));
        Assert.Null(connection.Attribute("Visibility"));
        Assert.Null(connection.Attribute("IsEnabled"));
        Assert.DoesNotContain(settings.Descendants(), element =>
            (string?)element.Attribute("Tag") == "notification-preferences");

        // Moving Connection retains its artwork; About reuses the former Info page icon.
        var iconSources = settings.Descendants()
            .Where(element => element.Name.LocalName == "SvgImageSource")
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => (string?)element.Attribute("UriSource"));
        foreach (var (tag, key, file) in new[]
        {
            ("connection", "Connection_Icon", "Connection.svg"),
            ("about", "Info_Icon", "Info.svg"),
        })
        {
            var item = menu.Elements().Single(element => (string?)element.Attribute("Tag") == tag);
            var icon = Assert.Single(item.Descendants(), element => element.Name.LocalName == "ImageIcon");
            Assert.Equal($"{{StaticResource {key}}}", (string?)icon.Attribute("Source"));
            Assert.Equal($"ms-appx:///Assets/SidebarIcons/{file}", iconSources[key]);
            Assert.True(File.Exists(Path.Combine(
                TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Assets", "SidebarIcons", file)));
        }

        var hub = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        Assert.Contains("NavDiagnostics.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;", hub);
        Assert.DoesNotContain("StatusPill", hub);
        Assert.DoesNotContain("ConnectionStatusPresenter", hub);
    }

    [Fact]
    public void SettingsNavigation_ExposesGatewayWorkAsDirectEntries_WithItsExistingVisibilityGate()
    {
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var settings = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml"));
        var menu = settings.Descendants().Single(element => element.Name.LocalName == "NavigationView.MenuItems");
        var gateway = menu.Elements()
            .SkipWhile(element => (string?)element.Attribute("Content") != "Gateway")
            .Skip(1)
            .TakeWhile(element => element.Name.LocalName is not ("NavigationViewItemHeader" or "NavigationViewItemSeparator"))
            .ToArray();

        Assert.Equal(new[] { "connection", "systems", "cron", "usage", "config" },
            gateway.Select(element => (string?)element.Attribute("Tag")).OfType<string>());
        var tools = menu.Elements()
            .SkipWhile(element => (string?)element.Attribute("Content") != "Agents and Tools")
            .Skip(1)
            .TakeWhile(element => element.Name.LocalName is not ("NavigationViewItemHeader" or "NavigationViewItemSeparator"))
            .ToArray();
        Assert.Equal(new[] { "AgentsNavItem", "sessions", "model", "local-ai" },
            tools.Select(element => (string?)element.Attribute("Tag") ?? (string?)element.Attribute(x + "Name")));
        var agents = Assert.Single(tools, element => (string?)element.Attribute(x + "Name") == "AgentsNavItem");
        Assert.Contains(agents.Descendants(), element => (string?)element.Attribute("Tag") == "agent:main");
        Assert.DoesNotContain(settings.Descendants(), element => (string?)element.Attribute(x + "Name") == "NavAdvanced");
        Assert.DoesNotContain(settings.Descendants(), element => (string?)element.Attribute(x + "Uid") == "HubWindow_Advanced");

        // Relocated entries must retain the disconnect gate formerly inherited from the Advanced group,
        // including staying visible while Config is the current page.
        var hub = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        Assert.Contains("var gatewayToolVisibility = keepCurrentGatewayPageVisible ? Visibility.Visible : vis;", hub);
        Assert.Contains("AgentsNavItem.Visibility = gatewayToolVisibility;", hub);
        Assert.Contains("NavSessions.Visibility = vis;", hub);
        Assert.Contains("NavUsage.Visibility = gatewayToolVisibility;", hub);
        Assert.Contains("NavConfig.Visibility = gatewayToolVisibility;", hub);
        Assert.DoesNotContain("NavGatewayHeader.Visibility =", hub);
        Assert.Null(settings.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "NavGatewayHeader")
            .Attribute("Visibility"));
    }

    [Fact]
    public void SettingsNavigation_SeparatesSectionsWithHeadersOnly()
    {
        var settings = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml"));
        var menu = settings.Descendants().Single(element => element.Name.LocalName == "NavigationView.MenuItems");
        Assert.DoesNotContain(settings.Descendants(), element => element.Name.LocalName == "NavigationViewItemSeparator");
        Assert.Equal(new[] { "Gateway", "Agents and Tools", "This Computer", "System" },
            menu.Elements().Where(element => element.Name.LocalName == "NavigationViewItemHeader")
                .Select(element => (string?)element.Attribute("Content")));
    }

    [Fact]
    public void InboxApprovalRows_LabelTheGatewayHostAndShowTheWholeCommand()
    {
        var row = Read("src", "OpenClaw.Tray.WinUI", "ViewModels", "InboxItemViewModel.cs");
        // "gateway" execution is the Gateway's machine, which may be remote.
        Assert.Contains("\"gateway\" => LocalizationHelper.GetString(\"Inbox_ApprovalHostGateway\")", row);
        Assert.DoesNotContain("Inbox_ApprovalHostThisComputer", row);
        Assert.Contains("InboxGatewayItems.DisplayCommand(approval.Command)", row);

        var xaml = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Controls", "InboxView.xaml"));
        var command = xaml.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" && (string?)element.Attribute("Text") == "{Binding CommandText}");
        Assert.Null(command.Attribute("MaxLines"));
        Assert.Null(command.Attribute("TextTrimming"));
        Assert.Equal("True", (string?)command.Attribute("IsTextSelectionEnabled"));
        Assert.Equal("ScrollViewer", command.Parent!.Name.LocalName);

        // Non-exec approval titles are repeated in Details because State is ellipsized.
        // Details must remain selectable plain text with no hidden tail or line cap.
        var details = xaml.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" && (string?)element.Attribute("Text") == "{Binding Details}");
        Assert.Null(details.Attribute("MaxLines"));
        Assert.Null(details.Attribute("TextTrimming"));
        Assert.Equal("Wrap", (string?)details.Attribute("TextWrapping"));
        Assert.Equal("True", (string?)details.Attribute("IsTextSelectionEnabled"));

        // Consent metadata is full selectable plain text, including unbroken scope commands.
        foreach (var field in new[] { "ScopeText", "WarningText", "Metadata" })
        {
            var text = xaml.Descendants().Single(element => element.Name.LocalName == "TextBlock" &&
                (string?)element.Attribute("Text") == "{Binding " + field + "}");
            Assert.Null(text.Attribute("MaxLines"));
            Assert.Null(text.Attribute("TextTrimming"));
            Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
            Assert.Equal("True", (string?)text.Attribute("IsTextSelectionEnabled"));
        }

        var metadata = xaml.Descendants().Single(element => element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{Binding Metadata}");
        Assert.Equal("Grid", metadata.Parent!.Name.LocalName);
        Assert.Equal("*", (string?)metadata.Parent.Descendants().First(element =>
            element.Name.LocalName == "ColumnDefinition").Attribute("Width"));

        // The App owns the single Inbox feed; a window-created fallback would leak its subscription.
        Assert.DoesNotContain("new InboxFeed(", Read("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
    }

    [Fact]
    public void ShellFlyoutContent_HasFocusedOwners()
    {
        var hub = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        var workspace = Read("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs");
        var footer = Read("src", "OpenClaw.Tray.WinUI", "Controls", "WorkspacePaneFooter.xaml.cs");
        foreach (var shell in new[] { hub, workspace, footer })
        {
            Assert.DoesNotContain("SyncInboxItems(", shell);
            Assert.DoesNotContain("GatewayRowDetail.Text", shell);
            Assert.DoesNotContain("CountEnabledCapabilities(", shell);
        }
        Assert.Contains("SyncInboxItems(", Read("src", "OpenClaw.Tray.WinUI", "Controls", "InboxView.xaml.cs"));
        // Hosts own the Inbox binding: a late Unloaded from a quick close/reopen must not unbind the open flyout.
        Assert.DoesNotContain("Unloaded +=", Read("src", "OpenClaw.Tray.WinUI", "Controls", "InboxView.xaml.cs"));
        Assert.Contains("Unloaded += (_, _) => Inbox.Unbind();", Read("src", "OpenClaw.Tray.WinUI", "Pages", "NotificationsPage.xaml.cs"));
        // Rows, tab counts and badge share InboxFeed; no host reads either source on its own.
        Assert.DoesNotContain("AppNotificationService", Read("src", "OpenClaw.Tray.WinUI", "Controls", "InboxView.xaml.cs"));
        Assert.DoesNotContain("InboxProjection.Build(", workspace);
        Assert.Contains("ConnectionStatusPresenter.NodeRow(", Read("src", "OpenClaw.Tray.WinUI", "Controls", "GatewayStatusContent.xaml.cs"));
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Controls", "WorkspacePaneFooter.xaml");
        Assert.Contains("Opening=\"OnNotificationsOpening\"", xaml);
        Assert.DoesNotContain("Click=\"OnNotifications\"", xaml);
        Assert.Contains("_inbox.Changed -= OnNotificationsChanged;", workspace);
        Assert.Contains("footer.Close();", workspace);
        Assert.Contains("NotificationContent.Unbind();", footer);
    }

    [Fact]
    public void InboxClock_RefreshesRowsOnlyWhileTheHostIsBound()
    {
        // WinUI-only lifecycle guard. Retire when mounted Inbox clock tests cover
        // ticking, empty tabs and close/reopen through both hosting surfaces.
        var view = Read("src", "OpenClaw.Tray.WinUI", "Controls", "InboxView.xaml.cs");
        var row = Read("src", "OpenClaw.Tray.WinUI", "ViewModels", "InboxItemViewModel.cs");
        Assert.Contains("_clock.IsRepeating = true;", view);
        Assert.Contains("_clock.Tick += (_, _) => RefreshClock();", view);
        Assert.Matches(@"internal void Unbind\(\)\s*\{\s*_clock.Stop\(\);", view);
        Assert.Matches(@"if \((?:_service|_feed) is null \|\| _items.Count == 0\)\s*\{\s*_clock.Stop\(\);", view);
        Assert.Matches(@"CountText.Text = [^;]+;\s*UpdateClock\(\);", view);
        Assert.Contains("if (!_clock.IsRunning)", view);
        Assert.Contains("_clock.Start();", view);
        Assert.Contains("item.RefreshClock(now);", view);
        Assert.Contains("OnPropertyChanged(nameof(AgeText));", row);
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
        var hub = Read("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
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
        var windowManager = Read("src", "OpenClaw.Tray.WinUI", "Services", "WindowManager.cs");

        Assert.Contains("public IntPtr GetHubWindowHandle()", app);
        Assert.Contains("_windowManager?.GetHubWindowHandle() ?? IntPtr.Zero", app);
        Assert.Contains("_workspaceWindow is { IsClosed: false }", windowManager);
        Assert.Contains("ActiveHubWindow is { } window", windowManager);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(window)", windowManager);
    }

    [Fact]
    public void App_SettingsChanged_DispatchesToUiThread()
    {
        var settingsCoordinator = Read("src", "OpenClaw.Tray.WinUI", "App.SettingsChangeCoordinator.cs");

        Assert.Contains("_dispatcherQueue.HasThreadAccess", settingsCoordinator);
        Assert.Contains("void ApplyUiSettingsAndNotify()", settingsCoordinator);
        Assert.Contains("_dispatcherQueue.TryEnqueue(ApplyUiSettingsAndNotify)", settingsCoordinator);
    }
}
