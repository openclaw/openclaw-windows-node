using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

/// <summary>Session details: identity, primary actions, per-session overrides, ownership, grouping, and facts.</summary>
internal static class SessionsHubDetailsView
{
    private static string T(string key) => SessionsHubPresentation.T(key);
    private static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);

    private static readonly string[] FastValues = ["", "auto", "on", "off"];
    private static readonly string[] VerboseValues = ["", "off", "on", "full"];
    private static readonly string[] ReasoningValues = ["", "off", "on", "stream"];
    private static readonly string[] DefaultThinkingValues = ["off", "minimal", "low", "medium", "high"];

    public static Element Render(SessionsHubViewContext context, SessionInfo row, IReadOnlyDictionary<string, string> titles)
    {
        var state = context.State;
        var controller = context.Controller;
        var title = titles.TryGetValue(row.Key, out var known) ? known : row.Key;
        var (statusLabel, tone) = SessionsHubPresentation.Status(row);
        var kind = SessionsHubProjection.DisplayKind(row);
        var canWrite = state.Permissions.CanWrite && !state.IsBusy;
        var mainState = controller.MainState(row);

        var display = SessionDisplayResolver.Resolve(row);
        var agent = display.AgentId is { } agentId ? state.Agents.FirstOrDefault(entry => entry.Id == agentId) : null;
        var colorId = SessionAppearance.NormalizeColor(row.Color);
        var heroColor = colorId ?? SessionAppearance.PaletteColor(agent?.Id ?? display.AgentId ?? row.Key);
        var chips = new List<Element>
        {
            tone switch
            {
                SessionsHubPresentation.StatusTone.Working => SessionsHubView.Pill(statusLabel, "green", FluentIconCatalog.Sync),
                SessionsHubPresentation.StatusTone.Attention => SessionsHubView.Pill(statusLabel, "red", FluentIconCatalog.StatusWarn),
                _ => SessionsHubView.NeutralBadge(statusLabel),
            },
            SessionsHubPresentation.KindColor(kind) is { } kindColor
                ? SessionsHubView.Pill(SessionsHubPresentation.KindLabel(kind), kindColor)
                : SessionsHubView.NeutralBadge(SessionsHubPresentation.KindLabel(kind)),
        };
        if (row.Archived) chips.Add(SessionsHubView.NeutralBadge(T("Archived")));
        if (row.Unread) chips.Add(SessionsHubView.Badge(T("Unread"), Theme.Accent, Theme.Ref("TextOnAccentFillColorPrimaryBrush")));

        var header = Border(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                    SessionsHubView.AgentAvatar(agent, display.AgentId ?? row.Key, 52)
                        .VAlign(VerticalAlignment.Top).Grid(row: 0, column: 0, rowSpan: 2),
                    TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.Wrap)
                        .Margin(14, 0, 8, 0)
                        .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2)
                        .AutomationId("SessionsHubDetailsTitle").Grid(row: 0, column: 1),
                    SessionsHubView.IconButton(FluentIconCatalog.Exit, T("CloseDetails"), () => controller.CloseDetails(),
                        automationId: "SessionsHubCloseDetails").VAlign(VerticalAlignment.Top).Grid(row: 0, column: 2),
                    HStack(6, chips.ToArray()).Margin(14, 6, 0, 0).Grid(row: 1, column: 1, columnSpan: 2)))
            .Background(SessionsHubView.Tint(heroColor))
            .CornerRadius(8)
            .Padding(14);

        var primaryActions = new List<Element>
        {
            SessionsHubView.CommandButton(FluentIconCatalog.Chat, T("OpenInChat"), () => controller.OpenInChat(row.Key),
                row.Kind != "global", "SessionsHubDetailsOpen", accent: true),
        };
        if (row.Archived)
            primaryActions.Add(SessionsHubView.CommandButton(FluentIconCatalog.Unarchive, T("Unarchive"),
                () => _ = controller.UnarchiveAsync(row, title), canWrite, "SessionsHubDetailsUnarchive"));
        else
            primaryActions.Add(SessionsHubView.CommandButton(FluentIconCatalog.Archive, T("Archive"),
                () => _ = controller.ArchiveAsync(row, title),
                canWrite && WorkspaceSessionMenuCanArchive(mainState), "SessionsHubDetailsArchive"));
        primaryActions.Add(SessionsHubView.IconButton(FluentIconCatalog.MoreOverflow, F("MoreNamed", title), () => { },
                automationId: "SessionsHubDetailsMore")
            .WithFlyout(MenuItems(SessionsHubMenus.SessionMenu(context, row, title))));

        var sections = new List<Element>
        {
            header,
            HStack(8, primaryActions.ToArray()),
            Overrides(context, row, title, canWrite),
            Appearance(context, row, title, canWrite),
            Organization(context, row, title, canWrite),
            Facts(context, row),
        };
        if (!string.IsNullOrWhiteSpace(row.LastRunError))
            sections.Insert(2, (InfoBar(T("LastRunError"), row.LastRunError) with { IsOpen = true })
                .Severity(InfoBarSeverity.Error).IsClosable(false));
        if (SessionsHubProjection.IsCloudWorker(row))
            sections.Add(CloudWorker(context, row, title, canWrite));

        return SessionsHubView.Card(ScrollView(VStack(16, sections.ToArray()).Padding(12)))
            .WithTransitions(new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromHorizontalOffset = 48 })
            .AutomationName(F("DetailsFor", title))
            .Landmark(Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType.Custom)
            .AutomationId("SessionsHubDetails");
    }

    private static bool WorkspaceSessionMenuCanArchive(SessionMainState mainState) =>
        OpenClawTray.Presentation.WorkspaceSessionMenu.CanArchiveOrDelete(mainState);

    private static Element Section(string title, params Element[] children) =>
        VStack(8, [TextBlock(title).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(Theme.SecondaryText)
            .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3), .. children]);

    private static Element Overrides(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var controller = context.Controller;
        var draft = context.State.LabelDraft.Key == row.Key ? context.State.LabelDraft.Text : row.Label ?? "";
        var dirty = draft.Trim() != (row.Label ?? "").Trim();
        var thinkingValues = ThinkingValues(row);
        return Section(T("Overrides"),
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBox(draft, text => controller.SetLabelDraft(row.Key, text), T("LabelPlaceholder"), T("Label"))
                    .MaxLength(120)
                    .AutomationName(T("Label"))
                    .IsEnabled(canWrite)
                    .OnKeyDown((_, args) =>
                    {
                        if (args.Key == global::Windows.System.VirtualKey.Enter && dirty)
                        {
                            args.Handled = true;
                            _ = controller.SaveLabelAsync(row, draft);
                        }
                    })
                    .AutomationId("SessionsHubLabel")
                    .Grid(column: 0),
                Button(T("Save"), () => _ = controller.SaveLabelAsync(row, draft)).AutomationName(T("Save"))
                    .IsEnabled(canWrite && dirty).VAlign(VerticalAlignment.Bottom).Margin(8, 0, 0, 0)
                    .AutomationId("SessionsHubLabelSave").Grid(column: 1)),
            Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                Choice(T("Thinking"), thinkingValues, row.ThinkingLevel, value =>
                        _ = controller.PatchAsync(row, new SessionPatch { ThinkingLevel = value is null ? SessionPatch.Clear : value }),
                        canWrite, "SessionsHubThinking", ThinkingLabel)
                    .Margin(0, 0, 6, 8).Grid(row: 0, column: 0),
                Choice(T("Fast"), FastValues, row.FastMode, value =>
                        _ = controller.PatchAsync(row, new SessionPatch
                        {
                            FastMode = value switch
                            {
                                null => SessionPatch.Clear,
                                "on" => SessionFastMode.On,
                                "off" => SessionFastMode.Off,
                                _ => SessionFastMode.Auto,
                            },
                        }),
                        canWrite, "SessionsHubFast", LevelLabel)
                    .Margin(6, 0, 0, 8).Grid(row: 0, column: 1),
                Choice(T("Verbose"), VerboseValues, row.VerboseLevel, value =>
                        _ = controller.PatchAsync(row, new SessionPatch { VerboseLevel = value is null ? SessionPatch.Clear : value }),
                        canWrite, "SessionsHubVerbose", LevelLabel)
                    .Margin(0, 0, 6, 0).Grid(row: 1, column: 0),
                Choice(T("Reasoning"), ReasoningValues, row.ReasoningLevel, value =>
                        _ = controller.PatchAsync(row, new SessionPatch { ReasoningLevel = value is null ? SessionPatch.Clear : value }),
                        canWrite, "SessionsHubReasoning", LevelLabel)
                    .Margin(6, 0, 0, 0).Grid(row: 1, column: 1)));
    }

    private static string[] ThinkingValues(SessionInfo row)
    {
        var advertised = (row.ThinkingContext?.Profile?.Levels ?? row.ThinkingDefaults?.Profile?.Levels) is { IsDefault: false } levels
            ? levels.Select(level => level.Id).ToArray()
            : DefaultThinkingValues;
        return ["", .. advertised];
    }

    private static string ThinkingLabel(string value) => value switch
    {
        "" => T("Inherit"),
        "off" => T("LevelOff"),
        "minimal" => T("ThinkingMinimal"),
        "low" => T("ThinkingLow"),
        "medium" => T("ThinkingMedium"),
        "high" => T("ThinkingHigh"),
        "xhigh" => T("ThinkingXHigh"),
        _ => value,
    };

    private static string LevelLabel(string value) => value switch
    {
        "" => T("Inherit"),
        "off" => T("LevelOff"),
        "on" => T("LevelOn"),
        "auto" => T("LevelAuto"),
        "full" => T("LevelFull"),
        "stream" => T("LevelStream"),
        "ultrafast" => T("LevelUltrafast"),
        _ => value,
    };

    /// <summary>A current value outside the known set stays visible rather than being misreported as Inherit.</summary>
    private static Element Choice(string header, string[] values, string? current, Action<string?> apply, bool enabled,
        string automationId, Func<string, string> label)
    {
        var options = values.ToList();
        var value = current ?? "";
        if (!options.Contains(value))
            options.Add(value);
        var index = options.IndexOf(value);
        return ComboBox(options.Select(label).ToArray(), index, selected =>
            {
                if (selected < 0 || selected >= options.Count || selected == index) return;
                apply(options[selected].Length == 0 ? null : options[selected]);
            })
            .Header(header)
            .IsEnabled(enabled)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId(automationId);
    }

    /// <summary>Session color and icon, the same tags the Workspace sidebar and web client show.</summary>
    private static Element Appearance(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var controller = context.Controller;
        var colorId = SessionAppearance.NormalizeColor(row.Color);
        var iconId = SessionAppearance.NormalizeIcon(row.Icon);
        var swatches = new List<Element>
        {
            Swatch(null, colorId is null, canWrite, () => _ = controller.PatchAsync(row, new SessionPatch { Color = SessionPatch.Clear })),
        };
        foreach (var id in SessionAppearance.ColorIds)
        {
            var value = id;
            swatches.Add(Swatch(value, colorId == value, canWrite,
                () => _ = controller.PatchAsync(row, new SessionPatch { Color = value })));
        }
        var icons = new List<Element>
        {
            IconChoice(null, colorId, string.IsNullOrWhiteSpace(row.Icon), canWrite,
                () => _ = controller.PatchAsync(row, new SessionPatch { Icon = SessionPatch.Clear })),
        };
        foreach (var id in SessionAppearance.IconIds)
        {
            var value = id;
            icons.Add(IconChoice(value, colorId, iconId == value, canWrite,
                () => _ = controller.PatchAsync(row, new SessionPatch { Icon = value })));
        }
        return Section(T("Appearance"),
            TextBlock(T("ColorHeader")).FontSize(12),
            HStack(2, swatches.ToArray()).AutomationName(F("ColorFor", title)).AutomationId("SessionsHubColorPicker"),
            TextBlock(T("IconHeader")).FontSize(12).Margin(0, 4, 0, 0),
            HStack(2, icons.ToArray()).AutomationName(F("IconFor", title)).AutomationId("SessionsHubIconPicker"));
    }

    private static Element Swatch(string? colorId, bool selected, bool enabled, Action apply)
    {
        var label = colorId is null ? T("ColorNone") : SessionsHubPresentation.ColorLabel(colorId);
        Element dot = colorId is null
            ? Border(SessionsHubView.Glyph("\uE711", 10, Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                .Size(20, 20).CornerRadius(10).WithBorder(Theme.Ref("ControlStrongStrokeColorDefaultBrush"), 1)
            : SessionsHubView.ColorDot(colorId, 20);
        return Button(Border(dot)
                    .Padding(2)
                    .CornerRadius(14)
                    .WithBorder(selected ? (colorId is null ? Theme.Accent : SessionsHubView.Strong(colorId)) : Theme.Ref("SubtleFillColorTransparentBrush"), 2),
                selected ? () => { } : apply)
            .SubtleButton()
            .Padding(2)
            .IsEnabled(enabled)
            .AutomationName(label)
            .ItemStatus(selected ? T("Selected") : "")
            .ToolTip(label)
            .AutomationId("SessionsHubColor:" + (colorId ?? "none"));
    }

    private static Element IconChoice(string? iconId, string? colorId, bool selected, bool enabled, Action apply)
    {
        var label = iconId is null ? T("IconDefault") : SessionsHubPresentation.IconLabel(iconId);
        var tint = colorId ?? "blue";
        return Button(Border(SessionsHubView.Glyph(iconId is null ? FluentIconCatalog.Chat : SessionsHubPresentation.IconGlyph(iconId), 16,
                            selected ? SessionsHubView.Strong(tint) : Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                    .Size(32, 32)
                    .CornerRadius(6)
                    .Background(selected ? SessionsHubView.Tint(tint) : Theme.Ref("SubtleFillColorTransparentBrush")),
                selected ? () => { } : apply)
            .SubtleButton()
            .Padding(0)
            .IsEnabled(enabled)
            .AutomationName(label)
            .ItemStatus(selected ? T("Selected") : "")
            .ToolTip(label)
            .AutomationId("SessionsHubIcon:" + (iconId ?? "default"));
    }

    private static Element Organization(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var state = context.State;
        var owner = row.Owner is { } value ? SessionsHubPresentation.OwnerLabel(value, state.Agents) : T("Unassigned");
        var group = row.Category ?? T("Ungrouped");
        return Section(T("Organization"),
            Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                TextBlock(T("Owner")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
                TextBlock(owner).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center).Margin(12, 0)
                    .AutomationId("SessionsHubOwner").Grid(row: 0, column: 1),
                DropDownButton(T("Assign"), MenuItems(SessionsHubMenus.OwnerItems(context, row)))
                    .IsEnabled(canWrite && state.Permissions.Supports("sessions.assignOwner"))
                    .AutomationName(F("AssignOwnerNamed", title))
                    .AutomationId("SessionsHubAssignOwner").Margin(0, 0, 0, 6).Grid(row: 0, column: 2),
                TextBlock(T("Group")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 1, column: 0),
                (row.Category is { } category
                        ? SessionsHubView.Pill(category, SessionAppearance.PaletteColor(category), FluentIconCatalog.Folder)
                            .HAlign(HorizontalAlignment.Left)
                        : TextBlock(group).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center))
                    .Margin(12, 0).AutomationName(group).AutomationId("SessionsHubGroupValue").Grid(row: 1, column: 1),
                DropDownButton(T("Move"), MenuItems(SessionsHubMenus.GroupItems(context, row)))
                    .IsEnabled(canWrite)
                    .AutomationName(F("MoveToGroupNamed", title))
                    .AutomationId("SessionsHubMoveGroup").Grid(row: 1, column: 2)));
    }

    private static Element Facts(SessionsHubViewContext context, SessionInfo row)
    {
        var facts = new List<(string Label, string Value)>
        {
            (T("FactKey"), row.Key),
            (T("FactUpdated"), SessionsHubPresentation.AbsoluteTime(row.UpdatedAt)),
            (T("FactTokens"), SessionsHubPresentation.Tokens(row)),
        };
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add((label, value));
        }
        var display = SessionDisplayResolver.Resolve(row);
        Add(T("FactAgent"), display.AgentId is { } agentId
            ? context.State.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? agentId : null);
        Add(T("FactChannel"), display.Channel is { } channel ? SessionsHubPresentation.ChannelLabel(channel) : null);
        Add(T("FactModel"), row.Model);
        Add(T("FactProvider"), row.Provider);
        Add(T("FactSessionId"), row.SessionId);
        Add(T("FactCreated"), row.CreatedAt is { } created
            ? SessionsHubPresentation.AbsoluteTime(DateTimeOffset.FromUnixTimeMilliseconds(created).UtcDateTime) : null);
        Add(T("FactWorktree"), row.Worktree is { } worktree ? $"{worktree.Branch} ({WorktreesProjection.RepoName(worktree.RepoRoot ?? "")})" : null);
        Add(T("FactArchiveReason"), row.Archived ? SessionsHubPresentation.ArchiveReason(row.ArchiveReason) : null);
        Add(T("FactArchivedAt"), row.ArchivedAt is { } archivedAt
            ? SessionsHubPresentation.AbsoluteTime(DateTimeOffset.FromUnixTimeMilliseconds(archivedAt).UtcDateTime) : null);
        Add(T("FactPinned"), row.Pinned ? T("Yes") : null);

        var cells = new List<Element>();
        for (var i = 0; i < facts.Count; i++)
        {
            cells.Add(TextBlock(facts[i].Label).FontSize(12).Foreground(Theme.SecondaryText).Margin(0, 0, 12, 6)
                .Grid(row: i, column: 0));
            cells.Add(TextBlock(facts[i].Value).FontSize(12).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled()
                .AutomationName($"{facts[i].Label}: {facts[i].Value}")
                .Margin(0, 0, 0, 6).Grid(row: i, column: 1));
        }
        return Section(T("Details"),
            Grid([GridSize.Auto, GridSize.Star()], facts.Select(_ => GridSize.Auto).ToArray(), cells.ToArray())
                .AutomationId("SessionsHubFacts"));
    }

    private static Element CloudWorker(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var controller = context.Controller;
        var canStop = SessionsHubProjection.CanStopCloudWorker(row);
        var help = canStop ? T("StopWorkerHelp") : row.HasActiveRun == true ? T("StopWorkerBusy") : T("StopWorkerUnavailable");
        return Section(T("CloudWorker"),
            TextBlock(SessionsHubPresentation.Placement(row.PlacementState)).AutomationId("SessionsHubPlacement"),
            TextBlock(help).FontSize(12).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap),
            SessionsHubView.CommandButton(FluentIconCatalog.Stop, T("StopWorker"), () => _ = controller.StopCloudWorkerAsync(row, title),
                canWrite && canStop && context.State.Permissions.Supports("sessions.reclaim"), "SessionsHubStopWorker"));
    }
}
