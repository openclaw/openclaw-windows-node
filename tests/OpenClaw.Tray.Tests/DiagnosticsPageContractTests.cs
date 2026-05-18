using System.Reflection;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Static-source contract tests for the Diagnostics page redesign. We assert
/// the structure rather than spin up WinUI so the tests stay in the pure
/// net10.0 Tray.Tests project and run on Linux build agents too. The intent
/// is to catch regressions that would silently undo the design:
///   - replacing Toolkit SettingsCard back with raw Expander
///   - dropping the bundle-preview dialog
///   - re-introducing duplicated "Open Log File" / "Open Config Folder"
///     surfaces that already live on AboutPage
///   - the AboutPage Copy-Support-Context handler diverging from the
///     unified CommandCenterTextHelper.BuildSupportContext path
///   - the diagnostics bundle text omitting the redaction exclusion line
/// </summary>
public sealed class DiagnosticsPageContractTests
{
    private static string RepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

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
        // Hanselman dual-model review (Opus + Codex consensus, MEDIUM):
        // the _copyFeedbackTimer must be stopped + nulled on both
        // Unloaded and OnNavigatedFrom so it can't tick on a detached
        // visual tree. Without this, a copy followed by quick navigate-
        // away leaks a Tick handler that touches IsOpen on a torn-down
        // FrameworkElement. Mirrors the codebase pattern in
        // ConnectionPage._reconnectMaskTimer and
        // PermissionsPage._execSavedHintTimer.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        // The shared teardown helper exists.
        Assert.Contains("private void StopCopyFeedbackTimer()", cs);
        // Helper stops AND nulls the timer (both halves are required —
        // Stop() alone leaves the field non-null which prevents the
        // lazy-init path from re-creating a fresh timer; nulling alone
        // leaks a running timer).
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"StopCopyFeedbackTimer\(\)[\s\S]{0,400}_copyFeedbackTimer\.Stop\(\)[\s\S]{0,200}_copyFeedbackTimer = null"),
            cs);

        // Both teardown sites call the helper.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"Unloaded \+=[\s\S]{0,400}StopCopyFeedbackTimer\(\)"),
            cs);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"OnNavigatedFrom[\s\S]{0,400}StopCopyFeedbackTimer\(\)"),
            cs);

        // The Tick lambda must use ?.Stop() (not !.Stop()) because
        // teardown can null the field between tick-queue and tick-run.
        // And it must guard the InfoBar access with IsLoaded because
        // DispatcherTimer.Stop() does not cancel ticks already queued
        // on the DispatcherQueue.
        Assert.Contains("_copyFeedbackTimer?.Stop()", cs);
        Assert.Contains("CopyFeedbackInfoBar.IsLoaded", cs);
        // The old null-forgiving Stop() must not creep back; it would
        // NRE under the teardown race described above.
        Assert.DoesNotContain("_copyFeedbackTimer!.Stop()", cs);
    }

    [Fact]
    public void App_PreservesConnectionStatusDeepLinkAndCommandRoute()
    {
        // The Diagnostics redesign commit promises that the deep-link
        // / command-palette path through "connectionstatus" keeps
        // working. The page now also depends on this wiring for its
        // popup behavior. Pin the App-side route here so neither side
        // can be accidentally renamed without a test failing.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("case \"connectionstatus\": ShowConnectionStatusWindow();", app);
        Assert.Contains("void IAppCommands.ShowConnectionStatus() => ShowConnectionStatusWindow();", app);

        var iface = Read("src", "OpenClaw.Tray.WinUI", "Services", "IAppCommands.cs");
        Assert.Contains("void ShowConnectionStatus();", iface);
    }

    [Fact]
    public void DebugPage_HasInPageDetailView_ForLogOnly()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // We swap a MainView/DetailView pair via Visibility for the
        // recent-log reader, mirroring ConnectionPage.AddGatewayPanel.
        Assert.Contains("x:Name=\"MainView\"", xaml);
        Assert.Contains("x:Name=\"DetailView\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        // The detail surface uses a RichTextBlock so we can color
        // individual log lines.
        Assert.Contains("x:Name=\"DetailRichText\"", xaml);

        // The old "open separate ConnectionStatusWindow" handler name
        // must not return. The new wiring goes through
        // IAppCommands.ShowConnectionStatus inside OnOpenEventTimeline.
        Assert.DoesNotContain("OnOpenConnectionDiagnostics", xaml);

        // The Clear toolbar button was timeline-only — Log mode has
        // nothing to clear (it's a re-read from disk). The button +
        // its handler must be gone now that timeline lives in its
        // own window.
        Assert.DoesNotContain("DetailClearButton", xaml);
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.DoesNotContain("OnDetailClear", cs);
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
    public void DebugPage_UsesCanonicalReconfigureLabel()
    {
        // Per docs/design/naming.md, "Reconfigure…" (with ellipsis) is
        // the canonical verb for "walk the user back through the
        // onboarding wizard". The old "Relaunch first-run setup"
        // label is prohibited.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("Reconfigure", xaml);
        Assert.Contains("\u2026", xaml); // U+2026 HORIZONTAL ELLIPSIS
        Assert.DoesNotContain("Relaunch first-run setup", xaml);
    }

    [Fact]
    public void DebugPage_PageDimensionsMatchPermissions()
    {
        // Page uses MaxWidth=900 + HorizontalAlignment=Center on the
        // content container, wrapped in a HorizontalAlignment=Stretch
        // outer container. The Stretch wrapper is REQUIRED — without it,
        // a content element placed directly inside a ScrollViewer ignores
        // MaxWidth on wide windows (Center collapses to natural content
        // width). This has regressed across several sessions; the contract
        // pins all three pieces of the pattern.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("MaxWidth=\"900\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml);
        // " Width=" (space-prefixed) distinguishes the fixed-width regression
        // from the valid MaxWidth="900" attribute we just asserted above.
        Assert.DoesNotContain(" Width=\"900\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"1064\"", xaml);
    }

    [Fact]
    public void DebugPage_WrapsCenteredContentInStretchingContainer()
    {
        // WinUI 3 quirk: a MaxWidth + HorizontalAlignment=Center element
        // placed directly inside a ScrollViewer is given the ScrollViewer's
        // full width but then collapses to its natural content size
        // (which on wide settings pages is "however wide the cards
        // measure to be" — often filling the entire ScrollViewer).
        // The fix is to wrap the centered element in a Grid with
        // HorizontalAlignment=Stretch so the centered child has a known
        // parent rect to center within. Mirrors PermissionsPage.xaml.
        // This is the SPECIFIC regression the user has hit multiple
        // times across sessions — guard it explicitly.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // The string "HorizontalAlignment=\"Stretch\"" must appear on
        // BOTH the MainView wrapper Grid AND the DetailView outer Grid.
        // Easiest: count occurrences; we expect at least 2 (one per view).
        var stretchCount = System.Text.RegularExpressions.Regex.Matches(
            xaml, "HorizontalAlignment=\"Stretch\"").Count;
        Assert.True(stretchCount >= 2,
            $"DebugPage.xaml must wrap MainView and DetailView centered " +
            $"content in a HorizontalAlignment=Stretch container so MaxWidth=900 " +
            $"is honored on wide windows. Found {stretchCount} Stretch declarations; " +
            $"expected at least 2 (one per view).");
    }

    [Fact]
    public void DebugPage_DetailViewMatchesMainViewPadding()
    {
        // The DetailView Padding must match the MainView ScrollViewer
        // Padding so the centered 900-wide column doesn't visually
        // shift (jump up or change horizontal position) when the user
        // enters or leaves the detail view. Both sides must be
        // "24,24,24,24" — earlier "24,12,24,24" on the DetailView
        // caused a visible 12px vertical jolt that the user described
        // as the views having "different widths".
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // MainView ScrollViewer.
        Assert.Contains("x:Name=\"MainView\"", xaml);
        // DetailView Grid with matching uniform 24,24,24,24 padding.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"x:Name=""DetailView""[\s\S]{0,200}Padding=""24,24,24,24"""),
            xaml);
        Assert.DoesNotContain("Padding=\"24,12,24,24\"", xaml);
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

        var cs = Read("src", "OpenClaw.Tray.WinUI", "Windows", "DiagnosticsBundleDialog.xaml.cs");
        // The dialog must expose a Configure() that takes a HWND-provider
        // delegate (not a captured IntPtr) so we can resolve the host
        // window handle JUST-IN-TIME when Save is clicked, instead of
        // trusting a possibly-stale handle captured at dialog open
        // (Hanselman v2 review #4).
        Assert.Contains("public void Configure(", cs);
        Assert.Contains("Func<IntPtr>", cs);
        Assert.Contains("hwndProvider", cs);
        // It must NOT auto-close on Copy — the user may want to also save.
        Assert.Contains("args.Cancel = true", cs);
    }

    [Fact]
    public void AboutPage_CopySupportContext_UsesUnifiedHelper()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml.cs");
        // Plan §4 / rubber-duck v2 #7: AboutPage's Copy Support Context
        // must call the same CommandCenterTextHelper.BuildSupportContext
        // that Diagnostics uses, not its old hand-rolled local string.
        Assert.Contains("CommandCenterTextHelper.BuildSupportContext", cs);
        // And there must be a hyperlink that takes the user from About
        // to the richer Diagnostics surface.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml");
        Assert.Contains("OnMoreDiagnosticsClick", xaml);
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
        // Rubber-duck v2 risk #3: the bundle output must continue to
        // explicitly advertise the redaction promise, since the new
        // preview dialog surfaces this text to users.
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");
        Assert.Contains("Excluded:", helper);
        Assert.Contains("tokens", helper);
        Assert.Contains("bootstrap tokens", helper);
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
        // background ReadLogTail call returns.
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
        // Hanselman v2 review #4: every other _hubWindow call site
        // pairs the null check with !IsClosed; this one should too,
        // otherwise the file picker can land a stale HWND during a
        // shutdown race.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("public IntPtr GetHubWindowHandle()", app);
        Assert.Contains("_hubWindow != null && !_hubWindow.IsClosed", app);
    }

    [Fact]
    public void App_SettingsChanged_DispatchesToUiThread()
    {
        // Hanselman v2 review #7: IAppCommands.NotifySettingsSaved is a
        // public entry point; future BG callers must not be able to
        // race the InfoBar refresh. OnSettingsSaved must marshal the
        // SettingsChanged?.Invoke onto the UI dispatcher when called
        // off-thread.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        // Check the marshalling pattern explicitly.
        Assert.Contains("_dispatcherQueue.HasThreadAccess", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue(() => SettingsChanged?.Invoke", app);
    }
}
