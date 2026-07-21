using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.Core.Theme;
using CoreMenuFlyoutItemBase = OpenClawTray.FunctionalUI.Core.MenuFlyoutItemBase;

namespace OpenClawTray.Chat;

/// <summary>
/// Unified chat composer surface matching the seeded design-system
/// <c>ChatComposer</c> pattern (see <c>.agents/design/components.jsx</c>).
///
/// A single rounded (Fluent OverlayCornerRadius / 8px), 1px-line-bordered card
/// holds a transparent auto-growing message
/// <see cref="Microsoft.UI.Xaml.Controls.TextBox"/> ABOVE a space-between
/// toolbar row:
///
/// <list type="bullet">
///   <item><description>LEFT cluster — an Add (+) subtle icon button followed by
///     subtle text+chevron pickers for the session/channel, model, and reasoning
///     effort (menu-flyout dropdowns, not ComboBoxes).</description></item>
///   <item><description>RIGHT cluster — Dictate and speaker subtle icon buttons
///     plus one primary action slot that switches between accent <c>Send</c> and
///     neutral <c>Stop</c>.</description></item>
/// </list>
///
/// Only <c>Send</c> carries the accent; every other control is subtle and uses
/// Fluent theme resources (<c>SubtleFillColorSecondaryBrush</c> on hover,
/// <c>SubtleFillColorTertiaryBrush</c> on press) so light/dark/high-contrast
/// stay correct with no hard-coded colors. The surface border turns accent while
/// recording. Tool-call/usage visibility now lives in the Settings page "Chat"
/// section; speaker mute remains available inline and is mirrored by the
/// read-aloud setting. The working indicator and permission banner render above
/// the composer / inline in the timeline respectively.
/// </summary>
public record OpenClawComposerProps(
    string ConnectionState,
    bool TurnActive,
    string ChannelLabel,
    string? ChannelId,
    ChannelGroup[] AvailableChannels,
    string[] AvailableModels,
    string? CurrentModel,
    string? CurrentModelProvider,
    string? CurrentThinkingLevel,
    bool MessageOptionsDisabled,
    Func<string, IReadOnlyList<ChatAttachment>, Task<bool>> OnSend,
    Action OnStop,
    Action<string> OnChannelChanged,
    Action<string> OnModelChanged,
    Action<string> OnThinkingLevelChanged,
    Action<bool> OnPermissionsChanged,
    Func<CancellationToken, Action?, Task<string?>>? OnVoiceRequest = null,
    Action? OnAttachClick = null,
    IReadOnlyList<ChatAttachment>? PendingAttachments = null,
    IReadOnlyList<ChatQueuedMessage>? QueuedMessages = null,
    Action<string>? OnQueuedMessageCancel = null,
    Action<ChatAttachment>? OnAttachmentRemoved = null,
    bool IsSpeakerMuted = false,
    Action? OnSpeakerToggle = null,
    Action? OnSettingsClick = null,
    string? VoiceTranscript = null,
    float VoiceAudioLevel = 0f,
    Action<Action>? RegisterVoiceStarter = null,
    Action<ChatAttachment>? OnAttachmentPasted = null,
    bool IsCompact = false,
    IReadOnlyList<ChatModelChoice>? ModelChoices = null,
    Action? OnModelCleared = null,
    IReadOnlyList<GatewayCommand>? AvailableCommands = null,
    bool CommandsSupported = true,
    Action? OnCommandsRequested = null,
    double? AvailableHeight = null);

public sealed class OpenClawComposer : Component<OpenClawComposerProps>
{
    private const double CompactQueuedMessagesMaxHeight = 144;
    private const double ExpandedQueuedMessagesFallbackMaxHeight = 220;
    private const double ExpandedQueuedMessagesMinHeight = 144;
    private const double ExpandedQueuedMessagesMaxHeight = 280;
    private const double ExpandedQueuedMessagesHeightRatio = 0.28;

    // Distinct reference-equality sentinel used as the ComboBoxItem.Tag for the
    // "Default" (clear model override) row, so it can never collide with a real
    // model id string. Selecting it routes to OnModelCleared (tri-state clear)
    // rather than OnModelChanged.
    private static readonly object ClearModelTag = new();

    // Thinking levels matching the gateway's sessions.patch thinkingLevel values.
    // "medium" is the default when the session has no explicit thinkingLevel set.
    private static readonly string[] ThinkingLevelIds    = { "off", "minimal", "low", "medium", "high" };
    private static readonly string[] ThinkingLevelLabels = { "off", "minimal", "low", "medium (default)", "high" };

    public override Element Render()
    {
        // UseRef for input text — avoids full-tree re-render on every keypress.
        // A separate hasTextState tracks the empty↔non-empty transition so the
        // send button accent styling updates correctly (at most 2 re-renders
        // per compose cycle instead of one per keypress).
        var inputRef = UseRef("");
        var inputRevisionRef = UseRef(0L);
        var hasTextState = UseState(false, threadSafe: true);
        // Slash-command menu state. Active when the composer holds a "/token"
        // (a leading slash with no whitespace yet); Query is the text after the
        // slash; Index is the highlighted row. ArgsMode flips the same popup into
        // the argument-choice picker after a command with fixed choices is chosen
        // (mirrors Mac's slashMenuMode "command" | "args"). Drives the inline
        // command menu that mirrors the gateway/web "type / to open" UX.
        var slashMenuState = UseState<(bool Active, string Query, int Index, bool ArgsMode)>((false, "", 0, false), threadSafe: true);

        // Surface uses the Fluent OverlayCornerRadius (8); the small controls
        // inside the toolbar (icon buttons + inline pickers) use the tighter
        // ControlCornerRadius (4) so they read as quiet Fluent controls.
        var composerCornerRadius = new CornerRadius(8);
        var controlCornerRadius = new CornerRadius(4);
        const double composerIconSize = 16;

        // Version bump triggers a re-render on send so the cleared ref value
        // is pushed to the TextBox control.
        var sendVersion = UseState(0, threadSafe: true);

        // Track whether the mic is actively recording for visual indicator.
        var isRecording = UseState(false, threadSafe: true);
        var voiceCtsRef = UseRef<CancellationTokenSource?>(null);
        // When true, a stop (not cancel) was requested — keep the transcript.
        var voiceStoppedRef = UseRef(false);
        // TextBox reference for focusing after recording completes
        var textBoxRef = UseRef<TextBox?>(null);
        // Slash-menu popup overlay (floats above the composer; does not push the
        // input controls). Created once on first render, driven each render.
        var slashPopupRef = UseRef<Microsoft.UI.Xaml.Controls.Primitives.Popup?>(null);
        // Tear the popup down when the composer unmounts so it isn't left rooted
        // to the XamlRoot (which would keep its row-button closures alive).
        UseEffect((Func<Action>)(() => () =>
        {
            var p = slashPopupRef.Current;
            if (p is not null)
            {
                p.IsOpen = false;
                p.Child = null;
                p.PlacementTarget = null;
                slashPopupRef.Current = null;
            }
        }));
        // One-time hook flag for the TextBox Paste event so we don't re-attach
        // the handler on every re-render (Set() runs each render).
        var pasteHookedRef = UseRef(false);
        // Cache BitmapImages built for current attachments so we rebuild them
        // only when an attachment is added or removed (not on every render).
        var attachmentImagesRef = UseRef<Dictionary<ChatAttachment, Microsoft.UI.Xaml.Media.Imaging.BitmapImage?>>(new());
        var pendingAttachments = Props.PendingAttachments ?? Array.Empty<ChatAttachment>();
        var queuedMessages = Props.QueuedMessages ?? Array.Empty<ChatQueuedMessage>();
        var imageCache = attachmentImagesRef.Current;
        foreach (var cachedAttachment in imageCache.Keys.ToArray())
        {
            if (!pendingAttachments.Contains(cachedAttachment))
                imageCache.Remove(cachedAttachment);
        }

        // Extracted voice-start action so it can be triggered programmatically (e.g. hotkey)
        Action startVoiceRecording = () =>
        {
            if (Props.OnVoiceRequest is null || isRecording.Value) return;
            var cts = new CancellationTokenSource();
            voiceCtsRef.Current = cts;
            voiceStoppedRef.Current = false;
            // Don't set isRecording yet — the request may show a dialog
            // (e.g. STT model not installed) and return null immediately.
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await Props.OnVoiceRequest(cts.Token, () => isRecording.Set(true));
                    // Keep transcript if we got text (either natural completion
                    // or user pressed stop). Discard only on explicit cancel.
                    if (!string.IsNullOrEmpty(text)
                        && (voiceStoppedRef.Current || !cts.IsCancellationRequested))
                    {
                        // Append to existing text (supports multiple recording passes)
                        var existing = inputRef.Current?.TrimEnd();
                        inputRef.Current = string.IsNullOrEmpty(existing)
                            ? text
                            : existing + " " + text;
                        inputRevisionRef.Current++;
                        hasTextState.Set(true);
                        sendVersion.Set(sendVersion.Value + 1);
                    }
                }
                catch (Exception ex)
                {
                    // Voice recording cancelled mid-transcription or pipeline
                    // unavailable. The UI already reflects the cancel; surface
                    // the cause at Debug for diagnostics.
                    OpenClawTray.Services.Logger.Debug($"OpenClawComposer: voice transcription failed/cancelled: {ex.Message}");
                }
                finally
                {
                    voiceCtsRef.Current = null;
                    voiceStoppedRef.Current = false;
                    cts.Dispose();
                    isRecording.Set(false);
                    // Move focus to the textbox so Enter sends the transcribed text
                    var tb = textBoxRef.Current;
                    if (tb != null)
                    {
                        tb.DispatcherQueue?.TryEnqueue(() =>
                        {
                            tb.Focus(FocusState.Programmatic);
                            // Place cursor at end of transcribed text
                            tb.SelectionStart = tb.Text?.Length ?? 0;
                            tb.SelectionLength = 0;
                        });
                    }
                }
            });
        };

        // Register the voice starter so external callers (e.g. hotkey) can trigger recording
        Props.RegisterVoiceStarter?.Invoke(startVoiceRecording);

        var sendInFlightRef = UseRef(false);
        Action sendAction = async () =>
        {
            if (sendInFlightRef.Current)
                return;

            var submittedInput = inputRef.Current ?? "";
            var submittedRevision = inputRevisionRef.Current;
            var msg = submittedInput.Trim();
            if (string.IsNullOrEmpty(msg) && pendingAttachments.Count == 0) return;
            var submittedAttachments = pendingAttachments.ToArray();
            sendInFlightRef.Current = true;
            try
            {
                if (!await Props.OnSend(msg, submittedAttachments))
                    return;

                if (!ChatComposerSubmissionPolicy.ShouldClearInput(
                        submittedRevision,
                        inputRevisionRef.Current))
                    return;

                inputRef.Current = "";
                inputRevisionRef.Current++;
                hasTextState.Set(false);
                // Clear any open slash menu so it doesn't re-open over the now-empty
                // composer (programmatic text reset doesn't fire TextChanged).
                slashMenuState.Set((false, "", 0, false));
                sendVersion.Set(sendVersion.Value + 1);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[chat] composer send failed: {ex}");
            }
            finally
            {
                sendInFlightRef.Current = false;
            }
        };
        var sendActionRef = UseRef<Action>(sendAction);
        sendActionRef.Current = sendAction;
        var isConnected = Props.ConnectionState == "connected";
        var messageOptionControlsEnabled = !Props.MessageOptionsDisabled;
        var placeholder = Props.ConnectionState switch
        {
            "connected" => LocalizationHelper.GetString("Chat_Composer_Placeholder_Connected"),
            "connecting" => LocalizationHelper.GetString("Chat_Composer_Placeholder_Connecting"),
            "incompatible-gateway" => LocalizationHelper.GetString("Chat_Composer_Placeholder_IncompatibleGateway"),
            _ => LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected")
        };

        // ── Toolbar pickers: session / model / reasoning ───────────────
        // These replace the old ComboBoxes with quiet Fluent "text + chevron"
        // pickers. The model and reasoning pickers are MenuFlyouts whose current
        // selection is marked with a native checkmark (ToggleMenuItem); a
        // disabled MenuItem renders unavailable rows. The session picker is a
        // richer content flyout (built below, once model choices are resolved) so
        // each session can show its associated model as a second-line subtext.
        // All pickers carry the same SubtleButton hover/press treatment as the
        // icon buttons — no accent, no hard-coded colors — so light/dark/
        // high-contrast all stay correct. Menus open upward (Top) since the
        // composer sits at the bottom of the chat surface.

        // ── Model picker (provider-rich) ─────────────────────────────────
        IReadOnlyList<ChatModelChoice> modelChoices = Props.ModelChoices is { Count: > 0 } mc
            ? mc
            : (Props.AvailableModels is { Length: > 0 } am
                ? am.Select(id => new ChatModelChoice(id, id)).ToList()
                : Array.Empty<ChatModelChoice>());

        var currentModelId = Props.CurrentModel;
        var currentSelectionId = ChatModelChoice.ResolveSelectionId(currentModelId, Props.CurrentModelProvider, modelChoices);
        var trackingDefault = ChatModelLabels.IsTrackingDefault(currentModelId);
        ChatModelChoice? currentChoice = null;
        ChatModelChoice? defaultChoice = null;
        foreach (var c in modelChoices)
        {
            if (defaultChoice is null && c.IsDefault) defaultChoice = c;
            if (currentChoice is null && !trackingDefault
                && string.Equals(c.SelectionId, currentSelectionId, StringComparison.Ordinal))
                currentChoice = c;
        }

        // Keep stale/custom current models visible even if models.list omits them.
        var effectiveChoices = modelChoices;
        if (!trackingDefault && currentChoice is null && !string.IsNullOrWhiteSpace(currentModelId))
        {
            var synthetic = new ChatModelChoice(currentModelId!, currentModelId!, Provider: Props.CurrentModelProvider);
            currentChoice = synthetic;
            var augmented = new List<ChatModelChoice>(modelChoices.Count + 1);
            augmented.AddRange(modelChoices);
            augmented.Add(synthetic);
            effectiveChoices = augmented;
            currentSelectionId = synthetic.SelectionId;
        }

        var modelEntries = new List<(string Label, object Tag, bool Selectable, bool IsCurrent)>();
        if (effectiveChoices.Count > 0)
            modelEntries.Add((ChatModelLabels.BuildDefaultEntryLabel(defaultChoice), ClearModelTag, true, trackingDefault));
        foreach (var c in effectiveChoices)
        {
            var isCur = !trackingDefault && string.Equals(c.SelectionId, currentSelectionId, StringComparison.Ordinal);
            modelEntries.Add((ChatModelLabels.BuildMenuLabel(c), c.SelectionId, c.IsSelectable, isCur));
        }
        if (modelEntries.Count == 0)
        {
            modelEntries.Add((Props.CurrentModel ?? "model", Props.CurrentModel ?? "", false, true));
        }

        // Model menu: selectable rows become ToggleMenuItems (the current model
        // is checked); explicitly unavailable rows render as disabled MenuItems
        // so they stay visible but can't be chosen. The "Default" entry clears
        // the session's explicit override (tri-state clear).
        var modelMenuItems = new List<CoreMenuFlyoutItemBase>(modelEntries.Count);
        foreach (var entry in modelEntries)
        {
            if (entry.Selectable)
            {
                var tag = entry.Tag;
                modelMenuItems.Add(ToggleMenuItem(
                    entry.Label,
                    isChecked: entry.IsCurrent,
                    onClick: () =>
                    {
                        if (ReferenceEquals(tag, ClearModelTag))
                            Props.OnModelCleared?.Invoke();
                        else if (tag is string id && !string.IsNullOrEmpty(id))
                            Props.OnModelChanged(id);
                    }));
            }
            else
            {
                modelMenuItems.Add(MenuItem(entry.Label) with { IsEnabled = false });
            }
        }
        var modelMenu = MenuItems(FlyoutPlacementMode.Top, modelMenuItems.ToArray());

        // Compact label for the model picker button — just the model's display
        // name (menu rows carry the provider/context/state detail).
        string modelPickerLabel = trackingDefault
            ? (defaultChoice?.DisplayName ?? "Default")
            : (currentChoice?.DisplayName ?? Props.CurrentModel ?? "Model");

        var thinkingLevel = Props.CurrentThinkingLevel ?? "medium";
        var thinkingIndex = Array.IndexOf(ThinkingLevelIds, thinkingLevel);
        if (thinkingIndex < 0) thinkingIndex = 3; // default to "medium (default)"

        var reasoningMenuItems = new CoreMenuFlyoutItemBase[ThinkingLevelLabels.Length];
        for (int i = 0; i < ThinkingLevelLabels.Length; i++)
        {
            var levelIndex = i;
            reasoningMenuItems[i] = ToggleMenuItem(
                ThinkingLevelLabels[i],
                isChecked: i == thinkingIndex,
                onClick: () =>
                {
                    if (levelIndex >= 0 && levelIndex < ThinkingLevelIds.Length)
                        Props.OnThinkingLevelChanged(ThinkingLevelIds[levelIndex]);
                });
        }
        var reasoningMenu = MenuItems(FlyoutPlacementMode.Top, reasoningMenuItems);

        // ── Session picker content (two-line rows) ─────────────────────────
        // Each session row shows its title on the first line and the model it is
        // configured to use as a muted second-line subtext, so switching model in
        // the model picker (which patches the current session) is reflected here.
        // A leading checkmark column marks the active session. Rows use the same
        // subtle hover/press theme resources as the other pickers.
        var sessionPrimaryBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
        var sessionMutedBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];

        string ResolveSessionModelCaption(string? modelId, string? provider)
        {
            // Mirror ChatModelLabels.BuildDefaultEntryLabel's un-localized
            // "Default" convention so the session subtext matches the model
            // picker without introducing new (translated) resource keys.
            if (ChatModelLabels.IsTrackingDefault(modelId))
                return "Default";
            var selId = ChatModelChoice.ResolveSelectionId(modelId, provider, modelChoices);
            foreach (var c in modelChoices)
                if (string.Equals(c.SelectionId, selId, StringComparison.Ordinal))
                    return ChatModelLabels.BuildMenuLabel(c);
            return modelId!;
        }

        Element SessionRow((string Id, string Title, string? Model, string? ModelProvider) session, bool isCurrent)
        {
            var sessionId = session.Id;
            var check = TextBlock(isCurrent ? "\uE73E" : "") // CheckMark
                .Set(t =>
                {
                    t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                    t.FontSize = 12;
                    t.Width = 16;
                    t.Foreground = sessionPrimaryBrush;
                    t.VerticalAlignment = VerticalAlignment.Center;
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                });
            var titleBlock = TextBlock(session.Title)
                .Set(t =>
                {
                    t.FontSize = 14;
                    t.Foreground = sessionPrimaryBrush;
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    t.TextWrapping = TextWrapping.NoWrap;
                });
            var modelBlock = TextBlock(ResolveSessionModelCaption(session.Model, session.ModelProvider))
                .Set(t =>
                {
                    t.FontSize = 12;
                    t.Foreground = sessionMutedBrush;
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    t.TextWrapping = TextWrapping.NoWrap;
                });
            return Button(
                    HStack(8, check, VStack(2, titleBlock, modelBlock)),
                    () => Props.OnChannelChanged(sessionId))
                .Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Left;
                    b.Padding = new Thickness(8, 6, 8, 6);
                    b.MinWidth = 240;
                    b.CornerRadius = controlCornerRadius;
                    ToolTipService.SetToolTip(b, session.Title);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
                        b,
                        isCurrent
                            ? LocalizationHelper.GetString("Chat_Composer_Accessibility_CurrentSession")
                            : string.Empty);
                    // Close the flyout after selecting (content flyouts do not
                    // auto-dismiss on inner button clicks like a MenuFlyout does).
                    // Use a named static handler with -= then += so re-renders
                    // that reuse the button don't stack duplicate handlers.
                    b.Click -= DismissSessionFlyoutOnClick;
                    b.Click += DismissSessionFlyoutOnClick;
                })
                .Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                    .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)));
        }

        var channelGroupsForPicker = Props.AvailableChannels;
        var sessionRows = new List<Element?>();
        foreach (var group in channelGroupsForPicker)
        {
            if (channelGroupsForPicker.Length > 1)
                sessionRows.Add(TextBlock(group.AgentLabel)
                    .Set(t =>
                    {
                        t.FontSize = 12;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        t.Foreground = sessionMutedBrush;
                        t.Margin = new Thickness(8, 6, 8, 2);
                    }));
            foreach (var session in group.Sessions)
                sessionRows.Add(SessionRow(session, session.Id == (Props.ChannelId ?? "")));
        }
        var channelFlyout = ContentFlyout(
            Border(ScrollView(VStack(2, sessionRows.ToArray()))
                    .Set(sv =>
                    {
                        sv.MaxHeight = 320;
                        sv.HorizontalScrollMode = ScrollMode.Disabled;
                        sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    }))
                .Set(b =>
                {
                    b.MinWidth = 240;
                    b.MaxWidth = 360;
                }),
            FlyoutPlacementMode.Top);


        // Title-case the level id for the picker button (menu rows show the
        // fuller "medium (default)" style labels).
        var reasoningId = ThinkingLevelIds[thinkingIndex];
        var reasoningPickerLabel = reasoningId.Length == 0
            ? reasoningId
            : char.ToUpperInvariant(reasoningId[0]) + reasoningId.Substring(1);

        // ── Row 2: multi-line composer textbox ─────────────────────────
        var recording = isRecording.Value;
        var recTranscript = recording ? Props.VoiceTranscript : null;

        // When recording, show the streaming transcript in the textbox.
        // The user can still type to edit after recording stops.
        var displayText = recording && !string.IsNullOrEmpty(recTranscript)
            ? recTranscript
            : inputRef.Current;

        // ── Slash command menu (type "/" to discover gateway commands inline) ──
        // The composer text box doubles as the menu's search field: when the
        // input is a bare "/token" the menu opens and filters as the user types,
        // matching the gateway/web Control UI's primary command surface.
        var slash = slashMenuState.Value;
        // The menu only engages on a connected gateway that actually advertises a
        // command catalog. Gating on CommandsSupported here keeps the whole
        // feature inert on gateways without commands.list: the popup stays hidden
        // AND the slash key-handling branches below are skipped, so "/foo" behaves
        // as ordinary text (Tab/Esc/Enter all keep their normal meaning).
        var slashActive = isConnected && slash.Active && !recording && Props.CommandsSupported;

        // Lazily fetch the catalog when the menu opens without one. Keyed via
        // UseEffect on the (open + missing-catalog) transition so the request
        // fires once on that edge — not as a render side effect, and not on every
        // keystroke while loading. If a fetch fails and the catalog stays null the
        // deps don't change, so it won't retry until the menu is reopened.
        // EnsureCommandCatalogAsync is itself cached + in-flight guarded.
        var needsCatalog = slashActive && Props.AvailableCommands is null;
        UseEffect(() =>
        {
            if (needsCatalog) Props.OnCommandsRequested?.Invoke();
        }, needsCatalog);

        IReadOnlyList<GatewayCommand> slashResults = Array.Empty<GatewayCommand>();
        IReadOnlyList<CommandCategoryGroup> slashGroups = Array.Empty<CommandCategoryGroup>();
        // Index (within the flattened group order) of the GLOBAL best search match
        // — the default keyboard selection, so display grouping never demotes a
        // strong later-bucket match behind a weak earlier-bucket one for Enter/Tab.
        var slashDefaultIndex = 0;
        if (slashActive && !slash.ArgsMode && Props.AvailableCommands is { } slashCmds)
        {
            // Category-grouped palette (Mac/web parity): commands render under
            // their Mac display bucket (Session, Model, Tools, Agents). Within a
            // bucket the relevance order from Search is preserved; Flattened drives
            // keyboard navigation, and DefaultSelectionIndex pins the global top
            // match as the default Enter/Tab target.
            var palette = new ChatCommandCatalogView(slashCmds)
                .GroupForPalette(CommandCategories.Bucket, slash.Query, CommandCategories.DisplayOrder);
            slashGroups = palette.Groups;
            slashResults = palette.Flattened;
            slashDefaultIndex = palette.DefaultSelectionIndex;
        }

        // Args-mode: the command (parsed from the composer text) plus its static
        // choices filtered by what the user has typed after "/name ".
        GatewayCommand? slashArgCmd = null;
        IReadOnlyList<GatewayCommandArgChoice> slashArgResults = Array.Empty<GatewayCommandArgChoice>();
        if (slashActive && slash.ArgsMode && Props.CommandsSupported && Props.AvailableCommands is { } argCmds)
        {
            var (argName, _, _) = SplitSlashArgText(displayText);
            slashArgCmd = argCmds.FirstOrDefault(c => c.MatchesName(argName));
            if (slashArgCmd is not null)
                slashArgResults = slashArgCmd.FirstArgChoices()
                    .Where(ch => ChoiceMatches(ch, slash.Query))
                    .Take(SlashMenuMaxItems)
                    .ToList();
        }

        var inArgsMode = slash.ArgsMode && slashArgCmd is not null && slashArgResults.Count > 0;
        var slashSelectableCount = inArgsMode ? slashArgResults.Count : slashResults.Count;
        // slash.Index < 0 means "not navigated yet": default to the global best
        // command match (slashDefaultIndex) in command mode, or the first arg
        // choice in args mode. Up/Down then make it a concrete index.
        var slashDefault = inArgsMode ? 0 : slashDefaultIndex;
        var slashIndex = slashSelectableCount == 0
            ? 0
            : Math.Clamp(slash.Index < 0 ? slashDefault : slash.Index, 0, slashSelectableCount - 1);

        // Pushes a new composer value into the textbox and restores the caret to
        // the end (shared by command insertion and arg-choice insertion).
        Action<string> commitSlashText = insert =>
        {
            inputRef.Current = insert;
            inputRevisionRef.Current++;
            hasTextState.Set(!string.IsNullOrWhiteSpace(insert));
            sendVersion.Set(sendVersion.Value + 1);
            var tbox = textBoxRef.Current;
            tbox?.DispatcherQueue?.TryEnqueue(() =>
            {
                tbox.Focus(FocusState.Programmatic);
                var c = tbox.Text?.Length ?? 0;
                tbox.SelectionStart = c;
                tbox.SelectionLength = 0;
            });
        };

        // Inserts the chosen command, replacing the "/token" the user was typing.
        // When the command has fixed argument choices, the popup transitions into
        // the arg-choice picker (Mac parity) instead of dismissing; otherwise the
        // command text is inserted (with a trailing space when it takes args).
        Action<GatewayCommand> insertSlashCommand = cmd =>
        {
            if (cmd.FirstArgChoices().Count > 0)
            {
                commitSlashText(cmd.DisplayName() + " ");
                slashMenuState.Set((true, "", 0, true));
                return;
            }
            commitSlashText(cmd.BuildInsertionText());
            slashMenuState.Set((false, "", 0, false));
        };

        // Picks a static argument choice, filling "/name value" and closing.
        Action<GatewayCommand, GatewayCommandArgChoice> insertSlashArg = (cmd, choice) =>
        {
            commitSlashText(cmd.BuildArgInsertionText(choice.Value));
            slashMenuState.Set((false, "", 0, false));
        };

        var textbox = TextField(displayText, v =>
            {
                inputRef.Current = v;
                inputRevisionRef.Current++;
                hasTextState.Set(!string.IsNullOrWhiteSpace(v));
                var (active, query, argsMode) = ComputeSlashState(v, Props.AvailableCommands);
                var cur = slashMenuState.Value;
                if (active != cur.Active || query != cur.Query || argsMode != cur.ArgsMode)
                    // -1 = "not navigated": selection resolves to the global best
                    // match (see slashIndex) until the user presses Up/Down.
                    slashMenuState.Set((active, query, -1, argsMode));
            })
            .Set(tb =>
            {
                textBoxRef.Current = tb;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    tb,
                    "ChatComposerInput");
                tb.PlaceholderText = recording
                    ? LocalizationHelper.GetString("Chat_Voice_ListeningPrompt")
                    : placeholder;
                // Keep AcceptsReturn=false: this lets us intercept *every*
                // Enter key in OnKeyDown reliably. When the user holds Shift,
                // we manually insert a newline at the caret below. This avoids
                // the routed-event ordering problem where the TextBox's class
                // handler can swallow Enter before our handler runs.
                tb.AcceptsReturn = false;
                tb.TextWrapping = TextWrapping.Wrap;
                tb.MinHeight = 56;
                tb.MaxHeight = 200;
                tb.IsEnabled = isConnected;
                // Strip the TextBox's own chrome — the wrapper Border below
                // (composerInput) provides the unified border + corner radius
                // so the optional attachment preview visually sits inside the
                // same input "card" as the typed text.
                tb.BorderThickness = new Thickness(0);
                tb.BorderBrush = new SolidColorBrush(Colors.Transparent);
                tb.Background = new SolidColorBrush(Colors.Transparent);
                tb.CornerRadius = new CornerRadius(0);
                // The TextBox template draws an additional "focus underline"
                // using TextControlBorderThemeThicknessFocused (default 0,0,0,2)
                // and a static top/side line via TextControlBorderThemeThickness
                // even when our BorderThickness=0 (template binds its inner
                // BorderElement to those theme thicknesses directly). Zero them
                // out plus force every TextControl BorderBrush variant to
                // transparent so the wrapper Border (composerInput) is the
                // only chrome visible.
                tb.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
                tb.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
                tb.Resources["TextControlBackground"] = new SolidColorBrush(Colors.Transparent);
                tb.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
                tb.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
                tb.Resources["TextControlBorderBrush"] = new SolidColorBrush(Colors.Transparent);
                tb.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
                tb.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);

                if (!pasteHookedRef.Current)
                {
                    pasteHookedRef.Current = true;
                    tb.Paste += async (s, e) =>
                    {
                        try
                        {
                            var att = await TryReadImageFromClipboardAsync();
                            if (att is not null)
                            {
                                e.Handled = true;
                                Props.OnAttachmentPasted?.Invoke(att);
                            }
                        }
                        catch (Exception ex)
                        {
                            // If anything goes wrong reading the clipboard,
                            // fall through to the default text paste behavior.
                            OpenClawTray.Services.Logger.Debug($"OpenClawComposer: clipboard image paste failed, falling back to text: {ex.Message}");
                        }
                    };
                }
            })
            .OnKeyDown((sender, e) =>
            {
                var key = e.Key;

                // While the slash menu is open with results, the arrow keys
                // navigate it and Enter/Tab autocompletes — instead of moving
                // the caret or sending the message. Works for both the command
                // list and the argument-choice picker (slashSelectableCount and
                // the Enter/Tab branch dispatch on the active mode).
                if (slashActive && slashSelectableCount > 0)
                {
                    if (key == global::Windows.System.VirtualKey.Down)
                    {
                        e.Handled = true;
                        slashMenuState.Set((slash.Active, slash.Query, Math.Min(slashIndex + 1, slashSelectableCount - 1), slash.ArgsMode));
                        return;
                    }
                    if (key == global::Windows.System.VirtualKey.Up)
                    {
                        e.Handled = true;
                        slashMenuState.Set((slash.Active, slash.Query, Math.Max(slashIndex - 1, 0), slash.ArgsMode));
                        return;
                    }
                    if (key == global::Windows.System.VirtualKey.Enter
                        || key == global::Windows.System.VirtualKey.Tab)
                    {
                        e.Handled = true;
                        if (inArgsMode)
                            insertSlashArg(slashArgCmd!, slashArgResults[slashIndex]);
                        else
                            insertSlashCommand(slashResults[slashIndex]);
                        return;
                    }
                    if (key == global::Windows.System.VirtualKey.Escape)
                    {
                        e.Handled = true;
                        slashMenuState.Set((false, "", 0, false));
                        return;
                    }
                }
                else if (slashActive && Props.AvailableCommands is null)
                {
                    // The loading popup is visible but nothing is selectable yet.
                    // No-match input hides the popup, so it must fall through as
                    // ordinary composer text instead of trapping Tab/Escape/arrows.
                    if (key == global::Windows.System.VirtualKey.Escape
                        || key == global::Windows.System.VirtualKey.Tab)
                    {
                        // Dismiss (Tab must not silently move focus while the popup
                        // overlays the composer).
                        e.Handled = true;
                        slashMenuState.Set((false, "", 0, false));
                        return;
                    }
                    if (key == global::Windows.System.VirtualKey.Up
                        || key == global::Windows.System.VirtualKey.Down)
                    {
                        // Nothing to move through — swallow so the caret doesn't jump.
                        e.Handled = true;
                        return;
                    }
                    if (key == global::Windows.System.VirtualKey.Enter)
                    {
                        // We don't yet know whether "/token" is a real command, so
                        // don't let Enter send it as raw text and race the fetch.
                        e.Handled = true;
                        return;
                    }
                }

                if (key == global::Windows.System.VirtualKey.Enter)
                {
                    var shift = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift);
                    var shiftDown = shift.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
                    e.Handled = true;
                    if (shiftDown && sender is TextBox tb)
                    {
                        // Insert a newline at the caret position. AcceptsReturn
                        // is false, so we do this manually instead of letting
                        // the TextBox handle it (which would race with the
                        // routed-event order and could either fail to insert
                        // or also trigger send).
                        var pos = tb.SelectionStart;
                        var len = tb.SelectionLength;
                        var text = tb.Text ?? string.Empty;
                        var safePos = Math.Min(Math.Max(pos, 0), text.Length);
                        var safeEnd = Math.Min(safePos + Math.Max(len, 0), text.Length);
                        tb.Text = text.Substring(0, safePos) + "\n" + text.Substring(safeEnd);
                        tb.SelectionStart = safePos + 1;
                        tb.SelectionLength = 0;
                        inputRef.Current = tb.Text;
                        inputRevisionRef.Current++;
                        hasTextState.Set(!string.IsNullOrWhiteSpace(tb.Text));
                    }
                    else
                    {
                        sendActionRef.Current();
                    }
                }
            });

        // ── Row 3: action icons (right-aligned) ────────────────────────

        // ── Attachment preview (rendered INSIDE the composer input card) ──
        // For images, a real thumbnail is shown so the user can confirm what
        // they pasted/picked. For other files a compact icon+name chip is
        // shown. The preview sits inside the same Border as the textbox so it
        // visually reads as part of the chat input.
        Element attachmentPreview = Empty();
        if (pendingAttachments.Count > 0)
        {
            Element BuildRemoveButton(ChatAttachment attachment, bool floating = false) => Button(
                    TextBlock("\uE711") // Cancel glyph
                        .Set(t =>
                        {
                            t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                            t.FontSize = 10;
                            if (floating)
                            {
                                t.HorizontalAlignment = HorizontalAlignment.Center;
                                t.VerticalAlignment = VerticalAlignment.Center;
                            }
                        }),
                    () => Props.OnAttachmentRemoved?.Invoke(attachment))
                .Set(b =>
                {
                    if (floating)
                    {
                        b.Width = 22;
                        b.Height = 22;
                        b.Padding = new Thickness(0);
                        b.CornerRadius = new CornerRadius(11);
                        b.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        b.Padding = new Thickness(4, 2, 4, 2);
                        b.CornerRadius = new CornerRadius(4);
                    }
                    b.MinWidth = 0; b.MinHeight = 0;
                })
                .Resources(r =>
                {
                    if (floating)
                    {
                        r.Set("ButtonBackground", Ref("SolidBackgroundFillColorBaseBrush"));
                        r.Set("ButtonBackgroundPointerOver", Ref("SolidBackgroundFillColorTertiaryBrush"));
                        r.Set("ButtonBackgroundPressed", Ref("SolidBackgroundFillColorQuarternaryBrush"));
                        r.Set("ButtonForeground", Ref("TextFillColorPrimaryBrush"));
                        r.Set("ButtonForegroundPointerOver", Ref("TextFillColorPrimaryBrush"));
                        r.Set("ButtonForegroundPressed", Ref("TextFillColorPrimaryBrush"));
                        r.Set("ButtonBorderBrush", Ref("CardStrokeColorDefaultBrush"));
                        r.Set("ButtonBorderBrushPointerOver", Ref("CardStrokeColorDefaultBrush"));
                        r.Set("ButtonBorderBrushPressed", Ref("CardStrokeColorDefaultBrush"));
                    }
                    else
                    {
                        r.Set("ButtonBackground", new SolidColorBrush(Colors.Transparent));
                        r.Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"));
                        r.Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"));
                        r.Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent));
                        r.Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent));
                        r.Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent));
                    }

                })
                .AutomationName("Remove attachment");

            Element BuildAttachmentPreview(ChatAttachment att)
            {
                var isImage = att.Type == "image";

            if (isImage)
            {
                // Build (and cache) a BitmapImage from the base64 content.
                // Rebuild only when the attachment instance changes; base64
                // decode + stream copy is non-trivial work to repeat per
                // keystroke re-render.
                if (!imageCache.TryGetValue(att, out var bmp))
                {
                    bmp = TryCreateBitmapFromBase64(att.Content);
                    imageCache[att] = bmp;
                }

                Element thumb;
                if (bmp is not null)
                {
                    // Fit the thumbnail inside a 160×96 box while preserving
                    // aspect ratio (downscale only, never upscale tiny pastes).
                    const double maxW = 160;
                    const double maxH = 96;
                    var pw = bmp.PixelWidth > 0 ? bmp.PixelWidth : (int)maxW;
                    var ph = bmp.PixelHeight > 0 ? bmp.PixelHeight : (int)maxH;
                    var scale = Math.Min(Math.Min(maxW / pw, maxH / ph), 1.0);
                    var thumbW = pw * scale;
                    var thumbH = ph * scale;

                    thumb = Border(Empty())
                        .CornerRadius(4)
                        .Set(b =>
                        {
                            b.Width = thumbW;
                            b.Height = thumbH;
                            b.Background = new Microsoft.UI.Xaml.Media.ImageBrush
                            {
                                ImageSource = bmp,
                                Stretch = Stretch.UniformToFill,
                            };
                            b.BorderThickness = new Thickness(1);
                            b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"];
                        });
                }
                else
                {
                    thumb = TextBlock("\uEB9F")
                        .Set(t =>
                        {
                            t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                            t.FontSize = 16;
                            t.VerticalAlignment = VerticalAlignment.Center;
                        });
                }

                // Circular close button that floats in the top-right corner
                // of the thumbnail. Distinct from the chip's flat removeBtn
                // because we need an opaque background (so the × is readable
                // over any image) and a contrast-friendly hover.
                var floatingRemove = BuildRemoveButton(att, floating: true)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Top)
                    .Margin(0, -8, -8, 0);

                // Stack the close button on top of the thumbnail in the same
                // Grid cell. Auto sizing means the chip is exactly as wide as
                // the thumbnail.
                var thumbWithClose = Grid(
                    [GridSize.Auto], [GridSize.Auto],
                    thumb.Grid(row: 0, column: 0),
                    floatingRemove.Grid(row: 0, column: 0)
                ).HAlign(HorizontalAlignment.Left);

                return Border(thumbWithClose)
                    .Padding(8, 12, 8, 4);
            }
            else
            {
                return Border(
                    Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock("\uE8A5") // Page glyph
                            .Set(t =>
                            {
                                t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                                t.FontSize = 12;
                                t.VerticalAlignment = VerticalAlignment.Center;
                            })
                            .Grid(row: 0, column: 0),
                        TextBlock($"{att.FileName}  ({att.FormatSize()})")
                            .Set(t =>
                            {
                                t.FontSize = 12;
                                t.TextTrimming = TextTrimming.CharacterEllipsis;
                                t.VerticalAlignment = VerticalAlignment.Center;
                                t.Margin = new Thickness(6, 0, 0, 0);
                            })
                            .Grid(row: 0, column: 1),
                        BuildRemoveButton(att).Grid(row: 0, column: 2)
                    )
                ).Padding(4, 4, 4, 0);
            }
            }

            attachmentPreview = VStack(6, pendingAttachments.Select(BuildAttachmentPreview).ToArray());
        }

        // Composer "card" — wraps the attachment preview (if any) and the
        // textbox in a single bordered container so the preview reads as
        // content inside the chat input rather than a separate row.
        Element RenderQueuedMessages()
        {
            if (queuedMessages.Count == 0)
                return Empty();

            var cardBg = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            var cardBorder = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ControlStrokeColorDefaultBrush"];
            var labelFg = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
            var textFg = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
            var failureFg = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"];
            var queuedCountText = string.Format(
                CultureInfo.CurrentCulture,
                LocalizationHelper.GetString("Chat_Composer_QueuedCountFormat"),
                queuedMessages.Count);
            Element RenderQueueCancelButton(ChatQueuedMessage message, int index)
            {
                if (message.SendState == ChatQueuedMessageSendState.Sending || Props.OnQueuedMessageCancel is null)
                    return Empty();

                var failed = message.SendState == ChatQueuedMessageSendState.Failed;
                var cancelTooltip = LocalizationHelper.GetString(failed
                    ? "Chat_Composer_QueuedMessageRemoveFailed"
                    : "Chat_Composer_QueuedMessageCancel");
                var buttonName = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationHelper.GetString(failed
                        ? "Chat_Composer_QueuedMessageRemoveFailedAutomationFormat"
                        : "Chat_Composer_QueuedMessageCancelAutomationFormat"),
                    index + 1,
                    FormatQueuedMessageAutomationSnippet(message.Text));
                return Button(
                        TextBlock("\uE711")
                            .Set(t =>
                            {
                                t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                                t.FontSize = 11;
                                t.HorizontalAlignment = HorizontalAlignment.Center;
                                t.VerticalAlignment = VerticalAlignment.Center;
                            }),
                        () => Props.OnQueuedMessageCancel?.Invoke(message.Id))
                    .Set(b =>
                    {
                        b.Width = 28;
                        b.Height = 28;
                        b.MinWidth = 0;
                        b.MinHeight = 0;
                        b.Padding = new Thickness(0);
                        b.CornerRadius = new CornerRadius(4);
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                            b,
                            $"{(failed ? "ChatQueuedMessageRemoveFailed" : "ChatQueuedMessageCancel")}_{message.Id}");
                    })
                    .Resources(r =>
                    {
                        r.Set("ButtonBackground", new SolidColorBrush(Colors.Transparent));
                        r.Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"));
                        r.Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"));
                        r.Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent));
                        r.Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent));
                        r.Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent));
                    })
                    .AutomationName(buttonName)
                    .SetToolTip(cancelTooltip);
            }

            Element RenderQueuedCard(ChatQueuedMessage message, int index)
            {
                var failed = message.SendState == ChatQueuedMessageSendState.Failed;
                var automationName = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationHelper.GetString(failed
                        ? "Chat_Composer_QueuedMessageFailedAutomationFormat"
                        : "Chat_Composer_QueuedMessageAutomationFormat"),
                    message.Text);

                Element stateLabel = Empty();
                Element details = Empty();
                if (failed)
                {
                    stateLabel = TextBlock(LocalizationHelper.GetString("Chat_Composer_QueuedMessageFailed"))
                        .Set(t =>
                            {
                                t.FontSize = 12;
                                t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                                t.Foreground = failureFg;
                            });

                    if (!string.IsNullOrWhiteSpace(message.ErrorText))
                    {
                        details = TextBlock(message.ErrorText!)
                            .Set(t =>
                            {
                                t.TextWrapping = TextWrapping.Wrap;
                                t.FontSize = 12;
                                t.Foreground = failureFg;
                            });
                    }
                }

                var messageBody = VStack(6,
                    stateLabel,
                    TextBlock(message.Text)
                        .Set(t =>
                        {
                            t.TextWrapping = TextWrapping.Wrap;
                            t.IsTextSelectionEnabled = true;
                            t.Foreground = textFg;
                        }),
                    details
                );

                return Border(
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        messageBody.Grid(row: 0, column: 0),
                        RenderQueueCancelButton(message, index)
                            .Grid(row: 0, column: 1)
                            .VAlign(VerticalAlignment.Top)
                    )
                )
                .Background(cardBg)
                .CornerRadius(8)
                .Padding(10, 8, 10, 8)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationName(automationName)
                .WithKey($"composer-queued:{Props.ChannelId ?? "none"}:{message.Id}")
                .Set(b =>
                {
                    b.BorderBrush = cardBorder;
                    b.BorderThickness = new Thickness(1);
                });
            }

            var queuedList = ScrollView(VStack(8, queuedMessages.Select(RenderQueuedCard).ToArray()))
                .Set(sv =>
                {
                    sv.MaxHeight = ComputeQueuedMessagesMaxHeight(Props.IsCompact, Props.AvailableHeight);
                    sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    sv.HorizontalScrollMode = ScrollMode.Disabled;
                    sv.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                });

            return Border(
                    VStack(8,
                        TextBlock(queuedCountText)
                            .Set(t =>
                            {
                                t.FontSize = 13;
                                t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                                t.Foreground = labelFg;
                            }),
                        queuedList
                    )
                )
                .Padding(0, 0, 0, 0)
                .AutomationName(queuedCountText)
                .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite)
                .WithKey($"composer-queued-section:{Props.ChannelId ?? "none"}");
        }

        var queuedPanel = RenderQueuedMessages();

        // Inner text content — the border + fill now live on the unified
        // composerSurface below, so this stays transparent/chromeless. Kept as
        // its own element (name preserved) so the attachment preview and
        // textbox read as one input region.
        var composerInput = Border(
            VStack(0, attachmentPreview, textbox)
        ).Set(b =>
        {
            b.Background = new SolidColorBrush(Colors.Transparent);
            b.BorderThickness = new Thickness(0);
        });

        // ── Voice recording indicator: compact pill with dot, label, and mini waveform ──
        // Only shown while actively recording (isRecording state).
        // Uses a unique Key so FunctionalUI doesn't reuse the same Border and leave
        // stale styling when switching between pill and empty placeholder.
        Element voiceIndicator;
        if (recording)
        {
            var audioLevel = Props.VoiceAudioLevel;
            var accentBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];

            // Red recording dot
            var recDot = Border(Empty())
                .Set(b =>
                {
                    b.Width = 6;
                    b.Height = 6;
                    b.CornerRadius = new CornerRadius(3);
                    b.Background = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    b.Opacity = 0.5 + Math.Min(audioLevel, 1f) * 0.5;
                    b.VerticalAlignment = VerticalAlignment.Center;
                });

            // "Recording" label
            var recLabel = TextBlock("Recording")
                .Set(t =>
                {
                    t.FontSize = 11;
                    t.Foreground = accentBrush;
                    t.VerticalAlignment = VerticalAlignment.Center;
                });

            // Mini waveform bars (16 bars for a fuller waveform)
            var miniBarCount = 16;
            var miniBarElements = new Element[miniBarCount];
            for (int bi = 0; bi < miniBarCount; bi++)
            {
                var barPhase = (bi % 3 == 0) ? 0.7 : (bi % 3 == 1) ? 1.0 : 0.5;
                var barHeight = 2.0 + Math.Min(audioLevel * barPhase, 1.0) * 8.0;
                miniBarElements[bi] = Border(Empty())
                    .Set(b =>
                    {
                        b.Width = 2;
                        b.Height = barHeight;
                        b.CornerRadius = new CornerRadius(1);
                        b.Background = accentBrush;
                        b.Opacity = 0.5 + Math.Min(audioLevel, 1f) * 0.5;
                        b.VerticalAlignment = VerticalAlignment.Center;
                    });
            }
            var miniWave = (FlexRow(miniBarElements) with { ColumnGap = 1.5 })
                .VAlign(VerticalAlignment.Center);

            // Pill container with accent tint background and border
            voiceIndicator = Border(
                (FlexRow(recDot, recLabel, miniWave) with { ColumnGap = 8 })
                    .VAlign(VerticalAlignment.Center)
            ).Set(b =>
            {
                b.Padding = new Thickness(10, 5, 12, 5);
                b.CornerRadius = new CornerRadius(14);
                b.Background = accentBrush;
                b.Opacity = 1.0;
                // Use a low-opacity accent background
                if (accentBrush is SolidColorBrush scb)
                {
                    b.Background = new SolidColorBrush(scb.Color) { Opacity = 0.1 };
                    b.BorderBrush = new SolidColorBrush(scb.Color) { Opacity = 0.3 };
                }
                b.BorderThickness = new Thickness(1);
            }).Margin(4, 0, 4, 0)
              .HAlign(HorizontalAlignment.Left);
            voiceIndicator.Key = "voice-pill";
        }
        else
        {
            voiceIndicator = Border(Empty()).Set(b =>
            {
                b.Padding = new Thickness(0);
                b.Margin = new Thickness(0);
                b.Height = 0;
                b.Opacity = 0;
            });
            voiceIndicator.Key = "voice-pill-hidden";
        }

        // Subtle 32×32 icon button — transparent at rest, Fluent
        // SubtleFillColorSecondary on hover / Tertiary on press (theme
        // resources, so light/dark/high-contrast stay correct). Radius uses the
        // tighter ControlCornerRadius. Mirrors the design-system
        // ComposerIconButton.
        Element IconButton(string glyph, string tip, Action onClick, Brush? foreground = null)
            => Button(
                TextBlock(glyph)
                    .Set(t =>
                    {
                        t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        t.FontSize = composerIconSize;
                        // Always set foreground explicitly so element diffing resets it.
                        t.Foreground = foreground
                            ?? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
                    }),
                onClick)
            .Set(b =>
            {
                b.Padding = new Thickness(0);
                b.MinWidth = 32; b.Width = 32;
                b.MinHeight = 32; b.Height = 32;
                b.CornerRadius = controlCornerRadius;
            })
            .Resources(r => r
                .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
            .AutomationName(tip)
            .SetToolTip(tip);

        // Subtle inline picker — text label + chevron with the same
        // SubtleButton hover/press treatment as the icon buttons. Reads as a
        // quiet dropdown (no border/fill until hover), never an accent. The
        // MenuFlyout opens upward from the toolbar. Mirrors the design-system
        // ComposerPicker.
        Element PickerButton(string label, string automationName, double? maxLabelWidth, FlyoutElement menu, bool enabled = true)
        {
            var mutedBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
            var labelBlock = TextBlock(label)
                .Set(t =>
                {
                    t.FontSize = 13;
                    t.Foreground = mutedBrush;
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    t.TextWrapping = TextWrapping.NoWrap;
                    t.VerticalAlignment = VerticalAlignment.Center;
                    if (maxLabelWidth is { } mw) t.MaxWidth = mw;
                });
            var chevron = TextBlock("\uE70D") // ChevronDown
                .Set(t =>
                {
                    t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                    t.FontSize = 10;
                    t.Foreground = mutedBrush;
                    t.VerticalAlignment = VerticalAlignment.Center;
                });
            // Fold the current selection into the accessible name so assistive
            // tech announces "<field>: <value>" (e.g. "Session: <title>"), the
            // way the legacy ComboBox surfaced its selected item. The visible
            // chevron button only shows the value, so without this the current
            // selection would be silent to screen readers.
            var accessibleName = string.IsNullOrWhiteSpace(label)
                ? automationName
                : $"{automationName}: {label}";
            return Button(HStack(4, labelBlock, chevron))
                .Set(b =>
                {
                    b.Padding = new Thickness(8, 0, 8, 0);
                    b.MinWidth = 0;
                    b.MinHeight = 32; b.Height = 32;
                    b.CornerRadius = controlCornerRadius;
                    b.IsEnabled = enabled;
                })
                .Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                    .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
                .WithFlyout(menu)
                .AutomationName(accessibleName)
                .SetToolTip(automationName);
        }

        var channelPicker = PickerButton(
            Props.ChannelLabel,
            LocalizationHelper.GetString("Chat_Composer_Accessibility_Session"),
            160,
            channelFlyout);
        var modelPicker = PickerButton(
            modelPickerLabel,
            LocalizationHelper.GetString("Chat_Composer_Accessibility_Model"),
            180,
            modelMenu,
            messageOptionControlsEnabled);
        var reasoningPicker = PickerButton(
            reasoningPickerLabel,
            LocalizationHelper.GetString("Chat_Composer_Accessibility_Reasoning"),
            null,
            reasoningMenu,
            messageOptionControlsEnabled);

        var attachBtn = IconButton("\uE723", LocalizationHelper.GetString("Chat_Composer_Tooltip_Attach"), () =>
        {
            Props.OnAttachClick?.Invoke();
        });

        // Voice recording: three-button model
        // - Not recording: mic button starts recording
        // - Recording: mic button becomes stop (■, keeps transcript),
        //   plus a cancel (✕) button that discards
        Element voiceBtn = Empty();
        Element voiceCancelBtn = Empty();
        if (isRecording.Value)
        {
            // Stop button — ends recording and keeps the transcript
            voiceBtn = IconButton("\uE15B", "Stop recording", () =>
            {
                voiceStoppedRef.Current = true;
                voiceCtsRef.Current?.Cancel();
            }, foreground: new SolidColorBrush(Microsoft.UI.Colors.Red));

            // Cancel button — discards recording entirely
            voiceCancelBtn = IconButton("\uE711", "Cancel recording", () =>
            {
                voiceStoppedRef.Current = false;
                voiceCtsRef.Current?.Cancel();
            });
        }
        else
        {
            voiceBtn = IconButton(
                "\uE720",
                LocalizationHelper.GetString("Chat_Composer_Tooltip_Voice"),
                startVoiceRecording);
        }

        // Speaker mute — subtle icon button (never accent). Reflects TTS mute
        // state via the glyph and toggles it through the host. Only rendered
        // when the host wires OnSpeakerToggle (chat surfaces with TTS).
        Element speakerBtn = Props.OnSpeakerToggle is not null
            ? IconButton(
                Props.IsSpeakerMuted ? "\uE74F" : "\uE767",  // SpeakerMute : Speaker
                Props.IsSpeakerMuted ? "Unmute" : "Mute",
                () => Props.OnSpeakerToggle())
            : Empty();

        // ── Slash command menu (gateway commands.list discovery) ──
        // Hosted in a floating Popup above the composer so the input controls
        // never move; it overlays content like standard command menus. The
        // textbox stays focused (light-dismiss off) so typing keeps filtering
        // and ↑/↓/Enter/Tab/Esc drive the menu.
        FrameworkElement? slashPopupContent = null;
        var slashMenuVisible = false;
        if (slashActive)
        {
            // slashActive already implies a connected, command-supporting gateway.
            if (Props.AvailableCommands is null)
            {
                slashPopupContent = BuildSlashHintPopup("Loading commands…");
                slashMenuVisible = true;
            }
            else if (slash.ArgsMode)
            {
                // Arg-choice picker for the selected command (Mac parity). When
                // nothing matches, ComputeSlashState has already cleared Active,
                // so we only reach here with results to show.
                if (inArgsMode)
                {
                    slashPopupContent = BuildSlashArgPopup(slashArgCmd!, slashArgResults, slashIndex, choice => insertSlashArg(slashArgCmd!, choice));
                    slashMenuVisible = true;
                }
            }
            else if (slashResults.Count == 0)
            {
                // No command matches the typed text — hide the palette entirely
                // (no "no matches" hint) so the composer reads as normal.
                slashMenuVisible = false;
            }
            else
            {
                slashPopupContent = BuildSlashPopup(slashGroups, slashIndex, slash.Query, insertSlashCommand);
                slashMenuVisible = true;
            }
        }

        // Primary action button — a single slot that shows Send when idle and
        // the Stop button while the assistant is responding. Keeping one slot
        // (identical geometry) means the toolbar never reflows between states.
        // Follow-up messages can still be queued mid-turn via Enter.
        var sendBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
        const string sendGlyph = "\uE724";
        const string stopGlyph = "\uE71A";

        var hasText = hasTextState.Value || pendingAttachments.Count > 0;
        var sendTooltip = LocalizationHelper.GetString("Chat_Composer_Tooltip_Send");
        // Send is the ONE accent affordance in the composer. Its glyph sits on
        // the accent fill, so use the Fluent "text on accent" brush (white in
        // both themes) rather than a hard-coded color. When empty it drops to a
        // subtle transparent button with muted glyph.
        var glyphBrush = hasText
            ? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
            : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
        var actionBtn = Button(
            TextBlock(sendGlyph)
                .Set(t =>
                {
                    t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                    t.FontSize = composerIconSize;
                })
                .Foreground(glyphBrush),
            sendAction
        ).Set(b =>
        {
            b.Padding = new Thickness(0);
            b.MinWidth = 40; b.MinHeight = 32; b.Height = 32;
            b.CornerRadius = controlCornerRadius;
            b.IsEnabled = isConnected;
            b.Background = hasText ? sendBrush : new SolidColorBrush(Colors.Transparent);
        })
        .Resources(r =>
        {
            if (hasText)
            {
                r.Set("ButtonBackgroundPointerOver", Ref("AccentFillColorSecondaryBrush"));
                r.Set("ButtonBackgroundPressed",    Ref("AccentFillColorTertiaryBrush"));
                r.Set("ButtonBorderBrush",            new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPressed",     new SolidColorBrush(Colors.Transparent));
            }
            else
            {
                r.Set("ButtonBackground",             new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBackgroundPointerOver",  Ref("SubtleFillColorSecondaryBrush"));
                r.Set("ButtonBackgroundPressed",      Ref("SubtleFillColorTertiaryBrush"));
                r.Set("ButtonBorderBrush",            new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPressed",     new SolidColorBrush(Colors.Transparent));
            }
        })
        .AutomationName(sendTooltip)
        .SetToolTip(sendTooltip);

        // Stop button — occupies the SAME action slot as Send (identical size)
        // while the assistant is responding, so nothing in the toolbar shifts.
        // Uses a neutral text-primary fill (theme-adaptive black/white); red is
        // reserved for genuine error states. The glyph uses the base surface
        // brush so it stays legible against the inverted fill in both themes.
        Element stopBtn = Empty();
        if (Props.TurnActive)
        {
            var stopTooltip = LocalizationHelper.GetString("Chat_Composer_Tooltip_Stop");
            stopBtn = Button(
                TextBlock(stopGlyph)
                    .Set(t =>
                    {
                        t.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        t.FontSize = composerIconSize;
                    })
                    .Foreground((Brush)Microsoft.UI.Xaml.Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]),
                Props.OnStop
            ).Set(b =>
            {
                b.Padding = new Thickness(0);
                b.MinWidth = 40; b.MinHeight = 32; b.Height = 32;
                b.CornerRadius = controlCornerRadius;
                b.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
            })
            .Resources(r =>
            {
                r.Set("ButtonBackgroundPointerOver", Ref("TextFillColorSecondaryBrush"));
                r.Set("ButtonBackgroundPressed", Ref("TextFillColorTertiaryBrush"));
                r.Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent));
            })
            .AutomationName(stopTooltip)
            .SetToolTip(stopTooltip);
        }

        Element workingBanner = Empty();

        // Permission/exec-approval banner used to live here, pinned above
        // the composer. It now renders inline in the timeline as a
        // ChatTimelineItemKind.PermissionRequest entry so the conversation
        // history records every approval (and its decided/expired badge)
        // in chronological order. See OpenClawChatTimeline.RenderPermissionEntry.

        // ── Toolbar row (space-between) ────────────────────────────────
        // LEFT cluster: Add(+) attach, then the session / model / reasoning
        // pickers. RIGHT cluster: dictation (+ cancel while recording) and a
        // single primary-action slot that is the accent Send when idle and the
        // accent Stop while a turn is active. Mirrors the design-system
        // ChatComposer toolbar.
        var leftCluster = (FlexRow(attachBtn, channelPicker, modelPicker, reasoningPicker)
                with { ColumnGap = 4 })
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);

        // Send and Stop share one slot (same 40x32 geometry) so the toolbar
        // never reflows when a turn starts or ends.
        Element primaryActionBtn = Props.TurnActive ? stopBtn : actionBtn;
        var rightCluster = (FlexRow(voiceCancelBtn, speakerBtn, voiceBtn, primaryActionBtn)
                with { ColumnGap = 4 })
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        var bottomToolbar = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            leftCluster.Grid(row: 0, column: 0),
            rightCluster.Grid(row: 0, column: 1)
        );

        // ── Optional working banner above the composer ──
        Element workingBanner2 = workingBanner;

        // Unified composer surface: one rounded (Fluent OverlayCornerRadius),
        // 1px-line-bordered card holding the text region above the toolbar. The
        // border turns accent while recording. Matches the design-system
        // ChatComposer surface (radius md / 1px line / surface fill / 8 padding).
        var composerSurface = Border(
            VStack(8, composerInput, voiceIndicator, bottomToolbar)
        ).Set(b =>
        {
            b.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextControlBackground"];
            if (recording)
            {
                b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
                b.BorderThickness = new Thickness(2);
            }
            else
            {
                b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextControlBorderBrush"];
                b.BorderThickness = new Thickness(1);
            }
            b.CornerRadius = composerCornerRadius;
            b.Padding = new Thickness(8);
        });

        // Queued messages sit above the surface (outside the input card).
        var composerCore = VStack(8, queuedPanel, composerSurface);

        // Drive the floating slash-menu popup after the tree builds so it anchors
        // above the (already mounted) textbox without shifting any controls.
        var tbForPopup = textBoxRef.Current;
        tbForPopup?.DispatcherQueue?.TryEnqueue(() =>
            DriveSlashPopup(slashPopupRef, tbForPopup, slashPopupContent, slashMenuVisible));

        return VStack(0,
            workingBanner2,
            Border(composerCore).Padding(16, 12, 16, 12)
             .Set(b =>
             {
                 // Top divider separates the composer region from the timeline.
                 b.BorderThickness = new Thickness(0, 1, 0, 0);
                 b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SurfaceStrokeColorDefaultBrush"];
             })
        );
    }

    private static void DismissSessionFlyoutOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            DismissOpenPopups(fe.XamlRoot);
    }

    private static void DismissOpenPopups(Microsoft.UI.Xaml.XamlRoot? root)
    {
        if (root is null)
            return;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            popup.IsOpen = false;
    }

    private static double ComputeQueuedMessagesMaxHeight(bool isCompact, double? availableHeight)
    {
        if (isCompact)
            return CompactQueuedMessagesMaxHeight;

        if (availableHeight is not { } height || double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            return ExpandedQueuedMessagesFallbackMaxHeight;

        return Math.Clamp(
            Math.Round(height * ExpandedQueuedMessagesHeightRatio),
            ExpandedQueuedMessagesMinHeight,
            ExpandedQueuedMessagesMaxHeight);
    }

    private static string FormatQueuedMessageAutomationSnippet(string text)
    {
        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int maxLength = 80;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "…";
    }

    private const int SlashMenuMaxItems = 8;

    /// <summary>
    /// Decides whether the composer text should open the inline slash menu and
    /// in which mode. Command mode: a single leading "/token" with no whitespace.
    /// Args mode: "/name &lt;filter&gt;" where <paramref name="commands"/> contains a
    /// command named <c>name</c> that declares static argument choices and at
    /// least one choice matches the filter (mirrors Mac's slash-commands.ts).
    /// </summary>
    private static (bool Active, string Query, bool ArgsMode) ComputeSlashState(
        string? text, IReadOnlyList<GatewayCommand>? commands)
    {
        var t = text ?? string.Empty;
        if (t.Length == 0 || t[0] != '/')
            return (false, string.Empty, false);

        var (name, rest, hasSpace) = SplitSlashArgText(t);
        if (!hasSpace)
            return (true, t.Substring(1), false); // command mode: still typing the name

        // The arg-choice picker filters on a single token. Once the user types
        // whitespace within that token (e.g. completed "/model gpt-5 " and kept
        // typing), they've moved past the picker — fall back to plain text so the
        // menu doesn't keep trapping Enter/Tab on a value they've finished.
        if (rest.Any(char.IsWhiteSpace))
            return (false, string.Empty, false);

        var cmd = commands?.FirstOrDefault(c => c.MatchesName(name));
        if (cmd is not null)
        {
            var choices = cmd.FirstArgChoices();
            if (choices.Count > 0 && choices.Any(ch => ChoiceMatches(ch, rest)))
                return (true, rest, true);
        }
        return (false, string.Empty, false);
    }

    /// <summary>Splits "/name rest" into ("name", "rest", hasSpace). Without a space, remainder is "".</summary>
    private static (string Name, string Remainder, bool HasSpace) SplitSlashArgText(string? text)
    {
        var t = text ?? string.Empty;
        if (t.Length == 0 || t[0] != '/') return (string.Empty, string.Empty, false);
        for (int i = 1; i < t.Length; i++)
        {
            if (char.IsWhiteSpace(t[i]))
                return (t.Substring(1, i - 1), t.Substring(i + 1), true);
        }
        return (t.Substring(1), string.Empty, false);
    }

    /// <summary>Prefix match of a choice (value or label) against the typed filter, case-insensitive.</summary>
    private static bool ChoiceMatches(GatewayCommandArgChoice choice, string? filter)
    {
        var f = (filter ?? string.Empty).Trim();
        if (f.Length == 0) return true;
        return (choice.Value?.StartsWith(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (choice.Label?.StartsWith(f, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ── Native slash-menu popup builders ──
    // The menu is hosted in a WinUI Popup (overlay) so it floats above the
    // composer without shifting controls. Content is built as native controls
    // (not FunctionalUI elements) because it lives outside the functional tree.

    private static double SlashPopupWidth(double anchorWidth) =>
        Math.Max(280, anchorWidth);

    private static Border BuildSlashHintPopup(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(8, 6, 8, 6),
        };
        return SlashShell(label);
    }

    private static Border BuildSlashPopup(
        IReadOnlyList<CommandCategoryGroup> groups, int selectedIndex, string query, Action<GatewayCommand> onPick)
    {
        var primary = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
        var secondary = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
        var selectedBg = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        var headerBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorTertiaryBrush"];

        var list = new StackPanel { Orientation = Orientation.Vertical };
        // Each group leads with a non-interactive category subheading; command
        // rows follow. A single running index across all groups keeps the
        // rendered rows aligned with the flattened keyboard-navigation list.
        var idx = 0;
        foreach (var group in groups)
        {
            list.Children.Add(SlashCategoryHeader(CommandCategories.Label(group.Category), headerBrush));
            foreach (var cmd in group.Commands)
            {
                list.Children.Add(SlashRow(cmd, idx == selectedIndex, query, primary, secondary, selectedBg, onPick));
                idx++;
            }
        }
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            MaxHeight = 280,
            Content = list,
        };
        return SlashShell(scroll);
    }

    /// <summary>
    /// Non-interactive category subheading for the slash palette: uppercase and
    /// letter-spaced like Mac's .slash-menu-group__label, rendered in a muted
    /// tone (Mac tints its label with the accent color; we keep it subdued to
    /// match the reference design).
    /// </summary>
    private static TextBlock SlashCategoryHeader(string text, Brush foreground) => new()
    {
        Text = (text ?? "").ToUpperInvariant(),
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        CharacterSpacing = 60,   // ~0.06em (units are 1/1000 em)
        Foreground = foreground,
        Margin = new Thickness(8, 8, 8, 2),
    };

    private static Border BuildSlashArgPopup(
        GatewayCommand cmd, IReadOnlyList<GatewayCommandArgChoice> choices,
        int selectedIndex, Action<GatewayCommandArgChoice> onPick)
    {
        var primary = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
        var secondary = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
        var selectedBg = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"];

        var list = new StackPanel { Orientation = Orientation.Vertical };
        // Header echoes the chosen command + its argument description so the user
        // keeps context while choosing a value. Falls back to the command's own
        // description, then just the command name.
        var argDesc = cmd.Args?.FirstOrDefault()?.Description;
        var headerText = !string.IsNullOrWhiteSpace(argDesc)
            ? $"{cmd.DisplayName()}  {argDesc}"
            : !string.IsNullOrWhiteSpace(cmd.Description)
                ? $"{cmd.DisplayName()}  {cmd.Description}"
                : cmd.DisplayName();
        list.Children.Add(new TextBlock
        {
            Text = headerText,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Margin = new Thickness(8, 6, 8, 2),
        });
        for (int i = 0; i < choices.Count; i++)
            list.Children.Add(SlashArgRow(cmd, choices[i], i == selectedIndex, primary, secondary, selectedBg, onPick));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            MaxHeight = 280,
            Content = list,
        };
        return SlashShell(scroll);
    }

    private static Button SlashArgRow(
        GatewayCommand cmd, GatewayCommandArgChoice choice, bool selected,
        Brush primary, Brush secondary, Brush selectedBg, Action<GatewayCommandArgChoice> onPick)
    {
        var label = string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{cmd.DisplayName()} {choice.Value}",
            FontSize = 12,
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });

        var btn = new Button
        {
            Content = row,
            Padding = new Thickness(8, 7, 8, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(6),
            Background = selected ? selectedBg : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        btn.Click += (_, _) => onPick(choice);
        return btn;
    }

    private static Border SlashShell(UIElement child)
    {
        // Floating, opaque, elevated container for the slash menu. Uses the same
        // 8px corner radius + default surface stroke as the composer card, with a
        // soft shadow so the menu reads as a distinct layer over the chat content.
        var res = Microsoft.UI.Xaml.Application.Current.Resources;

        // Match the composer input's background, but OPAQUE. TextControlBackground
        // is a semi-transparent overlay (fine over the solid composer card, but it
        // would show chat content through this floating popup), so composite it
        // over the base surface into a solid color.
        Brush background;
        if (res["TextControlBackground"] as SolidColorBrush is { } overlay
            && res["SolidBackgroundFillColorBaseBrush"] as SolidColorBrush is { } baseBrush)
        {
            var a = overlay.Color.A / 255.0;
            byte Mix(byte b, byte o) => (byte)Math.Round(b * (1 - a) + o * a);
            var o = overlay.Color;
            var b = baseBrush.Color;
            background = new SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, Mix(b.R, o.R), Mix(b.G, o.G), Mix(b.B, o.B)));
        }
        else
        {
            background = (Brush)res["SolidBackgroundFillColorBaseBrush"];
        }

        var shell = new Border
        {
            Background = background,
            BorderBrush = (Brush)res["SurfaceStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = child,
        };
        shell.Translation = new System.Numerics.Vector3(0, 0, 32);
        shell.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
        return shell;
    }

    // Highlights every case-insensitive occurrence of the typed query inside a
    // row TextBlock (command name / description) with a soft accent tint, so it's
    // clear what the filter matched. Uses TextHighlighter (rectangular) because it
    // is the only WinUI text-highlight that doesn't disturb the line layout —
    // inline rounded "chip" elements (InlineUIContainer) render as superscript and
    // break the baseline. No-op when the query is empty or shorter than the text.
    private static void ApplyQueryHighlight(TextBlock tb, string? query)
    {
        tb.TextHighlighters.Clear();
        var text = tb.Text ?? "";
        var q = (query ?? "").Trim().TrimStart('/').Trim();
        if (q.Length == 0 || text.Length < q.Length) return;

        var accent = Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush
            ?? new SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
        // IMPORTANT: TextHighlighter ignores SolidColorBrush.Opacity (unlike a
        // normal element background), so the alpha must be baked into the Color
        // itself — otherwise it renders the full, vivid accent. ~12% alpha gives
        // the same soft, muted blue-grey the old padded chip had.
        var ac = accent.Color;
        var tint = global::Windows.UI.Color.FromArgb(31, ac.R, ac.G, ac.B);
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new SolidColorBrush(tint),
            // Make the matched text pop over the tint (white in dark theme, near-
            // black in light) — theme-aware via the primary text brush.
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"],
        };

        var i = 0;
        while (i <= text.Length - q.Length)
        {
            var found = text.IndexOf(q, i, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = found, Length = q.Length });
            i = found + q.Length;
        }

        if (highlighter.Ranges.Count > 0) tb.TextHighlighters.Add(highlighter);
    }

    private static Button SlashRow(
        GatewayCommand cmd, bool selected, string query, Brush primary, Brush secondary, Brush selectedBg, Action<GatewayCommand> onPick)
    {
        // Row layout mirrors Mac's .slash-menu-item: icon · /name · [args] on the
        // left; description + "N options" badge pushed to the right edge (the
        // description column is star-sized and right-aligned). Args use the mono
        // font; the name keeps the default font (bold) for readability.
        var mono = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");

        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto }); // icon
        grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto }); // name
        grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto }); // args
        grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) }); // desc
        grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto }); // badge

        var icon = new FontIcon
        {
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["SymbolThemeFontFamily"],
            Glyph = SlashGlyph(cmd),
            FontSize = 14,
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = cmd.DisplayName(),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        ApplyQueryHighlight(name, query);

        var args = cmd.ArgTemplate();
        if (!string.IsNullOrWhiteSpace(args))
        {
            var argBlock = new TextBlock
            {
                Text = args,
                FontSize = 12,
                FontFamily = mono,
                Foreground = secondary,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(argBlock, 2);
            grid.Children.Add(argBlock);
        }

        if (!string.IsNullOrWhiteSpace(cmd.Description))
        {
            var desc = new TextBlock
            {
                Text = cmd.Description!,
                FontSize = 12,
                Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(desc, 3);
            grid.Children.Add(desc);
            ApplyQueryHighlight(desc, query);
        }

        var opts = cmd.OptionCount();
        if (opts > 0)
        {
            var badge = SlashBadge($"{opts} options");
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(badge, 4);
            grid.Children.Add(badge);
        }

        var btn = new Button
        {
            Content = grid,
            Padding = new Thickness(8, 7, 8, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Stretch the content so the star-sized description column fills the
            // row and its right-alignment actually pushes desc/badge to the edge.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
            Background = selected ? selectedBg : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        btn.Click += (_, _) => onPick(cmd);
        // Auto-scroll: when this is the keyboard-selected row, bring it into view
        // once it mounts so arrow navigation past the visible fold follows the
        // selection (the popup content is rebuilt each render, so Loaded fires per
        // navigation step). No animation to keep keypress scrolling snappy.
        if (selected)
        {
            btn.Loaded += (_, _) =>
                btn.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions { AnimationDesired = false });
        }
        return btn;
    }

    // Mirrors Mac's COMMAND_ICON_OVERRIDES (slash-commands.ts): icon keyed by
    // normalized command name, defaulting to the command-prompt glyph. Lucide
    // names are mapped to their nearest Segoe Fluent equivalents.
    private static string SlashGlyph(GatewayCommand cmd)
    {
        var name = (cmd.NativeName ?? cmd.DisplayName()).Trim().TrimStart('/').ToLowerInvariant();
        name = name.Replace(':', '_').Replace('.', '_').Replace('-', '_');
        return name switch
        {
            "help" or "commands" => "\uE82D",        // book
            "status" or "usage" => "\uE9D9",          // bar chart
            "export" or "export_session" => "\uE896", // download
            "skill" or "fast" => "\uE945",            // lightning (zap)
            "model" or "models" or "think" => "\uE713", // model/options (brain→settings)
            "new" => "\uE710",                         // plus
            "reset" or "redirect" => "\uE72C",         // refresh
            "compact" => "\uE9F3",                     // loader
            "stop" => "\uE71A",                        // stop
            "clear" => "\uE74D",                       // trash
            "agents" => "\uE7F4",                      // monitor
            "subagents" => "\uE8B7",                   // folder
            "steer" => "\uE724",                       // send
            "tts" => "\uE767",                         // volume
            _ => "\uE756",                              // command prompt (terminal default)
        };
    }

    // Accent-tinted pill mirroring Mac's .slash-menu-badge (accent text on a
    // ~14% accent fill).
    private static Border SlashBadge(string text)
    {
        var accent = Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush
            ?? new SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
        return new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(accent.Color) { Opacity = 0.14 },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = accent,
            },
        };
    }

    /// <summary>
    /// Creates (once) and drives the floating slash-menu Popup so it overlays
    /// just above the composer without affecting layout. Closed by clearing the
    /// content or when the trigger is no longer active.
    /// </summary>
    private static void DriveSlashPopup(
        Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef,
        TextBox anchor,
        FrameworkElement? content,
        bool visible)
    {
        var popup = popupRef.Current;
        if (popup is null)
        {
            popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup
            {
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true,
            };
            popupRef.Current = popup;
        }

        if (!visible || content is null || anchor.XamlRoot is null)
        {
            popup.IsOpen = false;
            popup.Child = null;
            popup.PlacementTarget = null;
            return;
        }

        var width = SlashPopupWidth(anchor.ActualWidth > 0 ? anchor.ActualWidth : 360);
        if (content is FrameworkElement fe) fe.Width = width;

        popup.XamlRoot = anchor.XamlRoot;
        popup.PlacementTarget = anchor;
        popup.DesiredPlacement = Microsoft.UI.Xaml.Controls.Primitives.PopupPlacementMode.Top;
        popup.Child = content;
        popup.IsOpen = true;
    }

    /// <summary>
    /// Synchronously builds a <see cref="Microsoft.UI.Xaml.Media.Imaging.BitmapImage"/>
    /// from a base64-encoded image payload (PNG/JPEG/etc.). Returns
    /// <c>null</c> if the base64 string can't be decoded or the bitmap can't
    /// be initialized — callers should fall back to a glyph in that case.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? TryCreateBitmapFromBase64(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.SetSource(stream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// If the clipboard contains a bitmap, reads it, re-encodes as PNG, and
    /// returns a <see cref="ChatAttachment"/>. Returns <c>null</c> if no
    /// bitmap is present or the bitmap exceeds <see cref="ChatAttachment.MaxSizeBytes"/>.
    /// </summary>
    private static async Task<ChatAttachment?> TryReadImageFromClipboardAsync()
    {
        var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (content is null) return null;
        if (!content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            return null;

        var streamRef = await content.GetBitmapAsync();
        using var inStream = await streamRef.OpenReadAsync();

        // Decode then re-encode as PNG so the gateway always receives a
        // self-describing image (clipboard bitmaps on Windows are often raw
        // CF_DIB and lack a recognizable container).
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var outStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();

        var size = (long)outStream.Size;
        if (size > ChatAttachment.MaxSizeBytes)
            return null;

        outStream.Seek(0);
        var buffer = new byte[size];
        using (var reader = new global::Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(buffer);
        }

        // Use a timestamp filename — clipboard bitmaps have no original name.
        var fileName = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        return new ChatAttachment
        {
            Type = "image",
            MimeType = "image/png",
            FileName = fileName,
            Content = Convert.ToBase64String(buffer),
            SizeBytes = size
        };
    }
}
