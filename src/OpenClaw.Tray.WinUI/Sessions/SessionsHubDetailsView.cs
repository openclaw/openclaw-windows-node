using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

/// <summary>Session details: identity, primary actions, per-session overrides, ownership, grouping, and facts.</summary>
internal static class SessionsHubDetailsView
{
    private static string T(string key) => SessionsHubPresentation.T(key);
    private static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);
    private static string W(string key) => OpenClawTray.Windows.WorkspaceWindow.Text(key);

    private static readonly string[] FastValues = ["", "auto", "on", "off"];
    private static readonly string[] VerboseValues = ["", "off", "on", "full"];
    private static readonly string[] ReasoningValues = ["", "off", "on", "stream"];
    private static readonly string[] DefaultThinkingValues = ["off", "minimal", "low", "medium", "high"];
    private const double OrganizationActionWidth = 150;

    public static Element Render(SessionsHubViewContext context, SessionInfo row, IReadOnlyDictionary<string, string> titles)
    {
        var state = context.State;
        var controller = context.Controller;
        var title = titles.TryGetValue(row.Key, out var known) ? known : row.Key;
        var canWrite = state.Permissions.CanWrite && state.CanMutateRows;

        var customize = Expander(T("Appearance"),
                VStack(16,
                    Overrides(context, row, title, canWrite),
                    Appearance(context, row, title, canWrite),
                    Organization(context, row, title, canWrite))
                    .HAlign(HorizontalAlignment.Stretch),
                true, _ => { })
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId("SessionsHubCustomize");
        var technical = Expander("",
                TechnicalFacts(context, row), false, _ => { })
            .HeaderTemplate(HStack(8,
                SessionsHubView.Glyph("\uE9D9", 14),
                TextBlock(LocalizationHelper.GetString("ConnectionPage_NodeTechnicalDetails.Text"))))
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId("SessionsHubTechnicalDetails");
        var information = new List<Element> { About(context, row) };
        if (SessionsHubProjection.IsCloudWorker(row))
            information.Add(CloudWorker(row));
        var about = VStack(16, information.ToArray());
        var editing = VStack(10, customize, technical)
            .HAlign(HorizontalAlignment.Stretch);
        Element body = context.IsWide
            ? Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                about.Margin(0, 0, 20, 0).Grid(column: 0), editing.Margin(20, 0, 0, 0).Grid(column: 1))
            : VStack(20, about, editing);
        var sections = new List<Element> { CommandBar(context, row, title), body };
        if (!string.IsNullOrWhiteSpace(row.LastRunError))
            sections.Insert(1, (InfoBar(T("LastRunError"), row.LastRunError) with { IsOpen = true })
                .Severity(InfoBarSeverity.Error).IsClosable(false));
        else if (controller.IsMain(row))
            sections.Insert(1, (InfoBar(T("MainSessionTitle"), T("MainSessionBody")) with { IsOpen = true })
                .Severity(InfoBarSeverity.Informational).IsClosable(false));
        return Border(VStack(16, sections.ToArray())).Padding(4, 4, 4, 8)
            .AutomationName(F("DetailsFor", title))
            .Landmark(Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType.Custom)
            .AutomationId("SessionsHubDetails");
    }

    private static Element CommandBar(SessionsHubViewContext context, SessionInfo row, string title)
    {
        var state = context.State;
        var controller = context.Controller;
        var canWrite = state.Permissions.CanWrite && state.CanMutateRows;
        var canArchive = canWrite && !controller.IsMain(row);
        Element Command(string glyph, string label, Action action, bool enabled, string automationId, string? tooltip = null) =>
            SessionsHubView.WithMutationHelp(Button(HStack(6,
                    SessionsHubView.Glyph(glyph, 13),
                    TextBlock(label).FontSize(12)),
                    action)
                .SubtleButton()
                .Padding(8, 5)
                .IsEnabled(enabled)
                .AutomationName(label)
                .AutomationId(automationId), state, tooltip ?? label);
        var menu = MenuItems(SessionsHubMenus.SessionMenu(context, row, title));
        return VStack(6,
            (FlexRow(
                Command(FluentIconCatalog.Rename, W("SessionMenu_Rename"), () => controller.SetLabelDraft(row.Key, row.Label ?? ""), canWrite,
                    "SessionsHubCommandRename"),
                Command(row.Pinned ? FluentIconCatalog.Unpin : FluentIconCatalog.Pin,
                    W(row.Pinned ? "SessionMenu_Unpin" : "SessionMenu_Pin"),
                    () => _ = controller.RunSharedActionAsync(WorkspaceSessionMenuAction.TogglePin, row, title), canWrite,
                    "SessionsHubCommandPin"),
                Command(row.Unread ? FluentIconCatalog.MarkRead : FluentIconCatalog.MarkUnread,
                    W(row.Unread ? "SessionMenu_MarkRead" : "SessionMenu_MarkUnread"),
                    () => _ = controller.RunSharedActionAsync(WorkspaceSessionMenuAction.ToggleUnread, row, title), canWrite,
                    "SessionsHubCommandUnread"),
                Command(row.Archived ? FluentIconCatalog.Unarchive : FluentIconCatalog.Archive,
                    W(row.Archived ? "SessionMenu_Unarchive" : "SessionMenu_Archive"),
                    () => _ = row.Archived ? controller.UnarchiveAsync(row, title) : controller.ArchiveAsync(row, title),
                    canArchive || row.Archived && canWrite, "SessionsHubCommandArchive",
                    controller.IsMain(row) ? T("MainCannotArchive")
                        : W(row.Archived ? "SessionMenu_Unarchive" : "SessionMenu_Archive")),
                Border(Empty()).Flex(grow: 1, basis: 0),
                SessionsHubView.IconButton(FluentIconCatalog.MoreOverflow, F("MoreNamed", title), () => { },
                        automationId: "SessionsHubCommandMore")
                    .WithFlyout(menu)) with
                { Wrap = FlexWrap.Wrap, ColumnGap = 4, RowGap = 4, AlignItems = FlexAlign.Center }),
            Border(Empty()).Height(1).Background(Theme.Ref("DividerStrokeColorDefaultBrush")));
    }

    private static Element Section(string title, params Element[] children) =>
        VStack(8, [TextBlock(title).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(Theme.SecondaryText)
            .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3), .. children])
            .HAlign(HorizontalAlignment.Stretch);

    private static Element Overrides(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var controller = context.Controller;
        var state = context.State;
        var labelHelp = state.CanMutateRows ? T("Label") : T("RosterNotCurrent");
        var saveHelp = state.CanMutateRows ? T("Save") : T("RosterNotCurrent");
        var draft = context.State.LabelDraft.Key == row.Key ? context.State.LabelDraft.Text : row.Label ?? "";
        var dirty = draft.Trim() != (row.Label ?? "").Trim();
        var thinkingValues = ThinkingValues(row);
        return Section(T("Overrides"),
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBox(draft, text => controller.SetLabelDraft(row.Key, text), T("LabelPlaceholder"), T("Label"))
                    .MaxLength(120)
                    .AutomationName(T("Label"))
                    .IsEnabled(canWrite)
                    .ToolTip(labelHelp)
                    .HelpText(labelHelp)
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
                    .IsEnabled(canWrite && dirty).ToolTip(saveHelp).HelpText(saveHelp)
                    .VAlign(VerticalAlignment.Bottom).Margin(8, 0, 0, 0)
                    .AutomationId("SessionsHubLabelSave").Grid(column: 1)),
            Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                Choice(T("Thinking"), thinkingValues, row.ThinkingLevel, value =>
                        _ = controller.PatchAsync(row, new SessionPatch { ThinkingLevel = value is null ? SessionPatch.Clear : value }),
                        canWrite, "SessionsHubThinking", ThinkingLabel,
                        state.CanMutateRows ? T("Thinking") : T("RosterNotCurrent"))
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
                        canWrite, "SessionsHubFast", LevelLabel,
                        state.CanMutateRows ? T("Fast") : T("RosterNotCurrent"))
                    .Margin(6, 0, 0, 8).Grid(row: 0, column: 1),
                Choice(T("Verbose"), VerboseValues, row.VerboseLevel, value =>
                        _ = controller.PatchAsync(row, new SessionPatch { VerboseLevel = value is null ? SessionPatch.Clear : value }),
                        canWrite, "SessionsHubVerbose", LevelLabel,
                        state.CanMutateRows ? T("Verbose") : T("RosterNotCurrent"))
                    .Margin(0, 0, 6, 0).Grid(row: 1, column: 0),
                Choice(T("Reasoning"), ReasoningValues, row.ReasoningLevel, value =>
                        _ = controller.PatchAsync(row, new SessionPatch { ReasoningLevel = value is null ? SessionPatch.Clear : value }),
                        canWrite, "SessionsHubReasoning", LevelLabel,
                        state.CanMutateRows ? T("Reasoning") : T("RosterNotCurrent"))
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
        string automationId, Func<string, string> label, string helpText)
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
            .ToolTip(helpText)
            .HelpText(helpText)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId(automationId);
    }

    /// <summary>Session color and icon, the same tags the Workspace sidebar and web client show.</summary>
    private static Element Appearance(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var controller = context.Controller;
        var state = context.State;
        var unavailableReason = !state.CanMutateRows ? T("RosterNotCurrent") : null;
        var colorId = SessionAppearance.NormalizeColor(row.Color);
        var iconId = SessionAppearance.NormalizeIcon(row.Icon);
        var swatches = new List<Element>
        {
            Swatch(null, colorId is null, canWrite, () => _ = controller.PatchAsync(row, new SessionPatch { Color = SessionPatch.Clear }),
                unavailableReason),
        };
        foreach (var id in SessionAppearance.ColorIds)
        {
            var value = id;
            swatches.Add(Swatch(value, colorId == value, canWrite,
                () => _ = controller.PatchAsync(row, new SessionPatch { Color = value }), unavailableReason));
        }
        var icons = new List<Element>
        {
            IconChoice(null, colorId, string.IsNullOrWhiteSpace(row.Icon), canWrite,
                () => _ = controller.PatchAsync(row, new SessionPatch { Icon = SessionPatch.Clear }), unavailableReason),
        };
        foreach (var id in SessionAppearance.IconIds)
        {
            var value = id;
            icons.Add(IconChoice(value, colorId, iconId == value, canWrite,
                () => _ = controller.PatchAsync(row, new SessionPatch { Icon = value }), unavailableReason));
        }
        return Section(T("Appearance"),
            TextBlock(T("ColorHeader")).FontSize(12),
            HStack(2, swatches.ToArray()).AutomationName(F("ColorFor", title)).AutomationId("SessionsHubColorPicker"),
            TextBlock(T("IconHeader")).FontSize(12).Margin(0, 4, 0, 0),
            HStack(2, icons.ToArray()).AutomationName(F("IconFor", title)).AutomationId("SessionsHubIconPicker"));
    }

    private static Element Swatch(string? colorId, bool selected, bool enabled, Action apply, string? unavailableReason)
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
            .ToolTip(unavailableReason ?? label)
            .HelpText(unavailableReason ?? label)
            .AutomationId("SessionsHubColor:" + (colorId ?? "none"));
    }

    private static Element IconChoice(
        string? iconId, string? colorId, bool selected, bool enabled, Action apply, string? unavailableReason)
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
            .ToolTip(unavailableReason ?? label)
            .HelpText(unavailableReason ?? label)
            .AutomationId("SessionsHubIcon:" + (iconId ?? "default"));
    }

    private static Element Organization(SessionsHubViewContext context, SessionInfo row, string title, bool canWrite)
    {
        var state = context.State;
        var owner = row.Owner is { } value ? SessionsHubPresentation.OwnerLabel(value, state.Agents) : T("Unassigned");
        var group = row.Category ?? T("Ungrouped");
        var move = SessionsHubView.WithMutationHelp(DropDownButton(T("Move"), MenuItems(SessionsHubMenus.GroupItems(context, row)))
            .Width(OrganizationActionWidth)
            .HAlign(HorizontalAlignment.Right)
            .IsEnabled(canWrite)
            .AutomationName(F("MoveToGroupNamed", title))
            .AutomationId("SessionsHubMoveGroup"), state, T("Move"));
        return Section(T("Organization"),
            Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                TextBlock(T("Owner")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
                TextBlock(owner).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center).Margin(12, 0)
                    .AutomationId("SessionsHubOwner").Grid(row: 0, column: 1),
                TextBlock(T("Group")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 1, column: 0),
                (row.Category is { } category
                        ? SessionsHubView.Pill(category, SessionAppearance.PaletteColor(category), FluentIconCatalog.Folder)
                            .HAlign(HorizontalAlignment.Left)
                        : TextBlock(group).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center))
                    .Margin(12, 0).AutomationName(group).AutomationId("SessionsHubGroupValue").Grid(row: 1, column: 1),
                move.Grid(row: 1, column: 2)));
    }

    private static Element About(SessionsHubViewContext context, SessionInfo row)
    {
        var display = SessionDisplayResolver.Resolve(row);
        var agent = display.AgentId is { } agentId
            ? context.State.Agents.FirstOrDefault(entry => entry.Id == agentId)
            : null;
        var owner = row.Owner is { } value
            ? SessionsHubPresentation.OwnerLabel(value, context.State.Agents)
            : T("Unassigned");
        var facts = new List<(string Label, string Value)>
        {
            (T("FactAgent"), agent?.Name ?? display.AgentId ?? T("NotAvailable")),
            (T("Owner"), owner),
            (T("FactModel"), row.Model ?? T("NotAvailable")),
            (T("FactTokens"), SessionsHubPresentation.Tokens(row)),
            (T("FactChannel"), display.Channel is { } channel ? SessionsHubPresentation.ChannelLabel(channel) : T("NotAvailable")),
            (T("FactUpdated"), SessionsHubPresentation.AbsoluteTime(row.UpdatedAt)),
        };
        var children = new List<Element> { FactGrid(facts, "SessionsHubAbout") };
        if (row.Worktree is { } worktree)
        {
            facts.Add((T("FactWorktree"), $"{worktree.Branch} · {WorktreesProjection.RepoName(worktree.RepoRoot ?? "")}"));
            children[0] = FactGrid(facts, "SessionsHubAbout");
            if (!string.IsNullOrWhiteSpace(worktree.Id))
            {
                var worktreeId = worktree.Id;
                children.Add(Button(T("ShowInWorktrees"), () => context.Controller.OpenWorktree(worktreeId))
                    .SubtleButton().AutomationName(T("ShowInWorktrees"))
                    .AutomationId("SessionsHubShowWorktree"));
            }
        }
        return Section(LocalizationHelper.GetString("SettingsPage_About.Text"), children.ToArray());
    }

    private static Element TechnicalFacts(SessionsHubViewContext context, SessionInfo row)
    {
        var facts = new List<(string Label, string Value)>
        {
            (T("FactKey"), row.Key),
        };
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add((label, value));
        }
        Add(T("FactProvider"), row.Provider);
        Add(T("FactSessionId"), row.SessionId);
        Add(T("FactCreated"), row.CreatedAt is { } created
            ? SessionsHubPresentation.AbsoluteTime(DateTimeOffset.FromUnixTimeMilliseconds(created).UtcDateTime) : null);
        Add(T("FactWorktree"), row.Worktree is { } worktree ? $"{worktree.Id} · {worktree.RepoRoot}" : null);
        Add(T("FactArchiveReason"), row.Archived ? SessionsHubPresentation.ArchiveReason(row.ArchiveReason) : null);
        Add(T("FactArchivedAt"), row.ArchivedAt is { } archivedAt
            ? SessionsHubPresentation.AbsoluteTime(DateTimeOffset.FromUnixTimeMilliseconds(archivedAt).UtcDateTime) : null);
        Add(T("FactPinned"), row.Pinned ? T("Yes") : null);
        return FactGrid(facts, "SessionsHubFacts");
    }

    private static Element FactGrid(IReadOnlyList<(string Label, string Value)> facts, string automationId)
    {
        var cells = new List<Element>();
        for (var i = 0; i < facts.Count; i++)
        {
            cells.Add(TextBlock(facts[i].Label).FontSize(12).Foreground(Theme.SecondaryText).Margin(0, 0, 12, 6)
                .Grid(row: i, column: 0));
            cells.Add(TextBlock(facts[i].Value).FontSize(12).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled()
                .AutomationName($"{facts[i].Label}: {facts[i].Value}")
                .Margin(0, 0, 0, 6).Grid(row: i, column: 1));
        }
        return Grid([GridSize.Auto, GridSize.Star()], facts.Select(_ => GridSize.Auto).ToArray(), cells.ToArray())
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId(automationId);
    }

    private static Element CloudWorker(SessionInfo row) =>
        Section(T("CloudWorker"),
            TextBlock(SessionsHubPresentation.Placement(row.PlacementState)).AutomationId("SessionsHubPlacement"));
}
