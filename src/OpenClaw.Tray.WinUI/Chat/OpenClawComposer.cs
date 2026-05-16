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
using OpenClawTray.Chat.Explorations;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClawTray.Chat;

/// <summary>
/// Three-row composer surface that mirrors Kenny Hong's <c>ChatShell</c> XAML
/// design (kenehong/native-chat-v2):
///
/// <list type="number">
///   <item><description>Row 1 — three compact <see cref="Microsoft.UI.Xaml.Controls.ComboBox"/>es:
///     <c>Channel</c> (agent identity), <c>Model</c>, and <c>Reasoning</c> mode.</description></item>
///   <item><description>Row 2 — multi-line message <see cref="Microsoft.UI.Xaml.Controls.TextBox"/>
///     with <c>Message Assistant (Enter to send)</c> placeholder.</description></item>
///   <item><description>Row 3 — four right-aligned action buttons (transparent attach / mic / more,
///     plus a filled accent <c>Send</c> button).</description></item>
/// </list>
///
/// Replaces the original <c>InputBar</c> + <c>StatusBar</c> pair from the
/// previous native chat prototype so our chat surface no longer carries two
/// separate footer rows. The status, working indicator, and permission
/// banner that <c>InputBar</c> used to render are preserved here above the
/// composer.
/// </summary>
public record OpenClawComposerProps(
    string ConnectionState,
    bool TurnActive,
    ChatPermissionRequest? PendingPermission,
    string ChannelLabel,
    string[] AvailableChannels,
    string[] AvailableModels,
    string? CurrentModel,
    string? CurrentThinkingLevel,
    Action<string, ChatAttachment?> OnSend,
    Action OnStop,
    Action<string, bool> OnPermissionResponse,
    Action<string> OnChannelChanged,
    Action<string> OnModelChanged,
    Action<string> OnThinkingLevelChanged,
    Action<bool> OnPermissionsChanged,
    Func<CancellationToken, Task<string?>>? OnVoiceRequest = null,
    Action? OnAttachClick = null,
    ChatAttachment? PendingAttachment = null,
    Action? OnAttachmentRemoved = null,
    bool IsSpeakerMuted = false,
    Action? OnSpeakerToggle = null,
    Action? OnSettingsClick = null,
    string? VoiceTranscript = null,
    float VoiceAudioLevel = 0f);

public sealed class OpenClawComposer : Component<OpenClawComposerProps>
{
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
        var hasTextState = UseState(false, threadSafe: true);

        // Subscribe to ChatExplorationState so toggles re-render the composer.
        // Inline because UseState/UseEffect are protected on Component (can't
        // be called from an extension method). Same pattern in
        // OpenClawChatTimeline + OpenClawChatRoot.
        var explorationRev = UseState(0, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler h = (_, _) => explorationRev.Set(explorationRev.Value + 1);
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        // Live values from ChatExplorationState (composer group E + brushes F).
        var composerCornerRadius = ChatVisualResolver.ComposerCornerRadius();
        var composerIconSize     = ChatVisualResolver.ComposerIconSize();
        var sendButtonSize       = ChatVisualResolver.SendButtonSize();
        var composerLayout       = ChatExplorationState.ComposerLayout;

        // Version bump triggers a re-render on send so the cleared ref value
        // is pushed to the TextBox control.
        var sendVersion = UseState(0, threadSafe: true);

        // Track whether the mic is actively recording for visual indicator.
        var isRecording = UseState(false, threadSafe: true);
        var voiceCtsRef = UseRef<CancellationTokenSource?>(null);

        var sendAction = () =>
        {
            var msg = inputRef.Current?.Trim();
            var attachment = Props.PendingAttachment;
            if (string.IsNullOrEmpty(msg) && attachment is null) return;
            Props.OnSend(msg ?? "", attachment);
            inputRef.Current = "";
            hasTextState.Set(false);
            sendVersion.Set(sendVersion.Value + 1);
        };
        var sendActionRef = UseRef<Action>(sendAction);
        sendActionRef.Current = sendAction;

        var isConnected = Props.ConnectionState == "connected";
        var placeholder = Props.ConnectionState switch
        {
            "connected" => LocalizationHelper.GetString("Chat_Composer_Placeholder_Connected"),
            "connecting" => LocalizationHelper.GetString("Chat_Composer_Placeholder_Connecting"),
            _ => LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected")
        };

        // ── Row 1: three compact dropdowns ─────────────────────────────
        var channelOptions = Props.AvailableChannels is { Length: > 0 }
            ? Props.AvailableChannels
            : new[] { Props.ChannelLabel ?? "main" };
        var channelIndex = Array.IndexOf(channelOptions, Props.ChannelLabel ?? "");
        if (channelIndex < 0) channelIndex = 0;
        var channelCombo = ComboBox(channelOptions, channelIndex, idx =>
            {
                if (idx >= 0 && idx < channelOptions.Length)
                    Props.OnChannelChanged(channelOptions[idx]);
            })
            .Set(cb =>
            {
                cb.MinWidth = 0;
                cb.MaxWidth = 150;
                cb.Height = 28;
                cb.FontSize = 11;
                cb.Padding = new Thickness(8, 0, 4, 0);
                cb.CornerRadius = composerCornerRadius;
            }).VAlign(VerticalAlignment.Center);

        var models = Props.AvailableModels;
        var modelIndex = models is { Length: > 0 } && Props.CurrentModel is { } cur
            ? Array.IndexOf(models, cur) : -1;
        if (modelIndex < 0 && models is { Length: > 0 }) modelIndex = 0;
        var modelDisplay = models is { Length: > 0 } ? models : new[] { Props.CurrentModel ?? "model" };

        var modelCombo = ComboBox(modelDisplay, Math.Max(modelIndex, 0), idx =>
        {
            if (models is { Length: > 0 } && idx >= 0 && idx < models.Length)
                Props.OnModelChanged(models[idx]);
        }).Set(cb =>
        {
            cb.MinWidth = 0;
            cb.MaxWidth = 180;
            cb.Height = 28;
            cb.FontSize = 11;
            cb.Padding = new Thickness(8, 0, 4, 0);
            cb.CornerRadius = composerCornerRadius;
        }).VAlign(VerticalAlignment.Center);

        var thinkingLevel = Props.CurrentThinkingLevel ?? "medium";
        var thinkingIndex = Array.IndexOf(ThinkingLevelIds, thinkingLevel);
        if (thinkingIndex < 0) thinkingIndex = 3; // default to "medium (default)"

        var reasoningCombo = ComboBox(ThinkingLevelLabels, thinkingIndex, idx =>
        {
            if (idx >= 0 && idx < ThinkingLevelIds.Length)
                Props.OnThinkingLevelChanged(ThinkingLevelIds[idx]);
        })
            .Set(cb =>
            {
                cb.MinWidth = 0;
                cb.MaxWidth = 150;
                cb.Height = 28;
                cb.FontSize = 11;
                cb.Padding = new Thickness(8, 0, 4, 0);
                cb.CornerRadius = composerCornerRadius;
            }).VAlign(VerticalAlignment.Center);

        // ComposerLayout 분기: ThreeRow = 3개 다 보임, InlinePill = 모델만, Minimal = 숨김.
        Element dropdownsRow = composerLayout switch
        {
            ChatComposerLayout.Minimal    => Empty(),
            ChatComposerLayout.InlinePill => (FlexRow(modelCombo) with { ColumnGap = 4 }),
            _                             => (FlexRow(channelCombo, modelCombo, reasoningCombo) with { ColumnGap = 4 }),
        };

        // ── Row 2: multi-line composer textbox ─────────────────────────
        var recording = isRecording.Value;
        var recTranscript = recording ? Props.VoiceTranscript : null;

        // When recording, show the streaming transcript in the textbox.
        // The user can still type to edit after recording stops.
        var displayText = recording && !string.IsNullOrEmpty(recTranscript)
            ? recTranscript
            : inputRef.Current;

        var textbox = TextField(displayText, v =>
            {
                inputRef.Current = v;
                hasTextState.Set(!string.IsNullOrWhiteSpace(v));
            })
            .Set(tb =>
            {
                tb.PlaceholderText = recording
                    ? LocalizationHelper.GetString("Chat_Voice_ListeningPrompt")
                    : placeholder;
                tb.AcceptsReturn = false;
                tb.TextWrapping = TextWrapping.Wrap;
                tb.MinHeight = 56;
                tb.IsEnabled = isConnected;
                tb.CornerRadius = composerCornerRadius;
                if (recording)
                {
                    // Accent border to indicate active recording
                    tb.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
                    tb.BorderThickness = new Thickness(2);
                }
            })
            .OnKeyDown((_, e) =>
            {
                if (e.Key == global::Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    sendActionRef.Current();
                }
            });

        // ── Row 3: action icons (right-aligned) ────────────────────────

        // ── Attachment chip (shown between textbox and actions when a file is pending) ──
        Element attachmentChip = Empty();
        if (Props.PendingAttachment is { } att)
        {
            var isImage = att.Type == "image";
            var icon = isImage ? "\uEB9F" : "\uE8A5"; // Photo / Page glyph
            attachmentChip = Border(
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(icon)
                        .Set(t =>
                        {
                            t.FontFamily = new FontFamily("Segoe Fluent Icons");
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
                    Button(
                        TextBlock("\uE711") // Cancel glyph
                            .Set(t =>
                            {
                                t.FontFamily = new FontFamily("Segoe Fluent Icons");
                                t.FontSize = 10;
                            }),
                        () => Props.OnAttachmentRemoved?.Invoke())
                        .Set(b =>
                        {
                            b.Padding = new Thickness(4, 2, 4, 2);
                            b.MinWidth = 0; b.MinHeight = 0;
                            b.CornerRadius = new CornerRadius(4);
                        })
                        .Resources(r => r
                            .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                            .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                            .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                            .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                            .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                            .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
                        .AutomationName("Remove attachment")
                        .Grid(row: 0, column: 2)
                )
            ).Padding(8, 4, 8, 4)
             .CornerRadius(6)
             .Set(b =>
             {
                 b.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
                 b.BorderThickness = new Thickness(1);
                 b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"];
             })
             .Margin(0, 0, 0, 4);
        }

        // ── Voice recording indicator row (shown during active recording) ──
        Element voiceIndicator = Empty();
        if (recording)
        {
            var audioLevel = Props.VoiceAudioLevel;

            // Pulsing red dot — uses audio level to vary opacity (0.4–1.0)
            var redDot = Border(Empty())
                .Set(b =>
                {
                    b.Width = 8;
                    b.Height = 8;
                    b.CornerRadius = new CornerRadius(4);
                    b.Background = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    b.Opacity = 0.4 + Math.Min(audioLevel, 1f) * 0.6;
                    b.VerticalAlignment = VerticalAlignment.Center;
                });

            // Level bars — 16 thin rectangles driven by audio level
            var barCount = 16;
            var barElements = new Element[barCount];
            for (int bi = 0; bi < barCount; bi++)
            {
                // Stagger bar heights for a natural waveform look
                var barPhase = (bi % 3 == 0) ? 0.8 : (bi % 3 == 1) ? 1.0 : 0.6;
                var barHeight = 2.0 + Math.Min(audioLevel * barPhase, 1.0) * 14.0;
                barElements[bi] = Border(Empty())
                    .Set(b =>
                    {
                        b.Width = 3;
                        b.Height = barHeight;
                        b.CornerRadius = new CornerRadius(1.5);
                        b.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
                        b.Opacity = 0.5 + Math.Min(audioLevel, 1f) * 0.5;
                        b.VerticalAlignment = VerticalAlignment.Center;
                    });
            }
            var levelBars = (FlexRow(barElements) with { ColumnGap = 2 })
                .VAlign(VerticalAlignment.Center)
                .Set(sp => sp.Height = 18);

            voiceIndicator = (FlexRow(
                redDot,
                levelBars.Flex(grow: 1)
            ) with { ColumnGap = 8 })
            .Margin(4, 0, 4, 0);
        }

        Element IconButton(string glyph, string tip, Action onClick, Brush? foreground = null)
            => Button(
                TextBlock(glyph)
                    .Set(t =>
                    {
                        t.FontFamily = new FontFamily("Segoe Fluent Icons");
                        t.FontSize = composerIconSize;
                        // Always set foreground explicitly so element diffing resets it.
                        t.Foreground = foreground
                            ?? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
                    }),
                onClick)
            .Set(b =>
            {
                b.Padding = new Thickness(8, 4, 8, 4);
                b.MinWidth = 32; b.MinHeight = 28;
                b.CornerRadius = composerCornerRadius;
            })
            .Resources(r => r
                .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
            .AutomationName(tip);

        // 5 icons (Send/Stop/Attach/Voice/More) honor ChatExplorationState
        // Show + Glyph overrides set from the explorations panel.
        var attachBtn = ChatExplorationState.AttachIconShow
            ? IconButton(NonEmptyGlyph(ChatExplorationState.AttachIconGlyph, "\uE723"), LocalizationHelper.GetString("Chat_Composer_Tooltip_Attach"), () =>
            {
                Props.OnAttachClick?.Invoke();
            })
            : Empty();
        var voiceBtn  = ChatExplorationState.VoiceIconShow
            ? IconButton(
                isRecording.Value
                    ? "\uE71A"  // Stop glyph — click to cancel recording
                    : NonEmptyGlyph(ChatExplorationState.VoiceIconGlyph, "\uE720"),
                LocalizationHelper.GetString("Chat_Composer_Tooltip_Voice"), () =>
            {
                if (Props.OnVoiceRequest is null) return;
                if (isRecording.Value)
                {
                    // Cancel the active recording
                    voiceCtsRef.Current?.Cancel();
                    return;
                }
                var cts = new CancellationTokenSource();
                voiceCtsRef.Current = cts;
                isRecording.Set(true);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var text = await Props.OnVoiceRequest(cts.Token);
                        if (!string.IsNullOrEmpty(text) && !cts.IsCancellationRequested)
                        {
                            inputRef.Current = text;
                            hasTextState.Set(true);
                            sendVersion.Set(sendVersion.Value + 1);
                        }
                    }
                    catch { /* voice cancelled or failed — silently ignore */ }
                    finally
                    {
                        voiceCtsRef.Current = null;
                        cts.Dispose();
                        isRecording.Set(false);
                    }
                });
            }, foreground: isRecording.Value ? new SolidColorBrush(Microsoft.UI.Colors.Red) : null)
            : Empty();
        var speakerBtn = Props.OnSpeakerToggle is not null
            ? IconButton(
                Props.IsSpeakerMuted ? "\uE74F" : "\uE767",  // SpeakerMute : Speaker
                Props.IsSpeakerMuted ? "Unmute" : "Mute",
                () => Props.OnSpeakerToggle())
            : Empty();
        var settingsBtn = Props.OnSettingsClick is not null
            ? IconButton("\uE713", "Settings", () => Props.OnSettingsClick())
            : Empty();

        // Send button — always present so the user can queue follow-up messages
        // even while the assistant is responding.
        var defaultSendBrush = ChatVisualResolver.UserBubbleBrush(
            (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"]);
        var sendBrush = ChatVisualResolver.SendButtonBrush(defaultSendBrush);

        var sendGlyph = NonEmptyGlyph(ChatExplorationState.SendIconGlyph, "\uE724");
        var stopGlyph = NonEmptyGlyph(ChatExplorationState.StopIconGlyph, "\uE71A");

        Element actionBtn;
        if (ChatExplorationState.SendIconShow)
        {
            var hasText = hasTextState.Value || Props.PendingAttachment is not null;
            var glyphBrush = hasText
                ? (Brush)new SolidColorBrush(Colors.White)
                : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
            actionBtn = Button(
                TextBlock(sendGlyph)
                    .Set(t =>
                    {
                        t.FontFamily = new FontFamily("Segoe Fluent Icons");
                        t.FontSize = composerIconSize;
                    })
                    .Foreground(glyphBrush),
                sendAction
            ).Set(b =>
            {
                b.Padding = new Thickness(10, 4, 10, 4);
                b.MinWidth = sendButtonSize + 4; b.MinHeight = sendButtonSize - 4;
                b.CornerRadius = composerCornerRadius;
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
            .AutomationName(LocalizationHelper.GetString("Chat_Composer_Tooltip_Send"));
        }
        else
        {
            actionBtn = Empty();
        }

        // Stop button — shown inline NEXT TO the send button (to its right)
        // when the assistant is responding, matching the gateway web UI pattern.
        Element stopBtn = Empty();
        if (Props.TurnActive && ChatExplorationState.StopIconShow)
        {
            stopBtn = Button(
                TextBlock(stopGlyph)
                    .Set(t =>
                    {
                        t.FontFamily = new FontFamily("Segoe Fluent Icons");
                        t.FontSize = composerIconSize;
                    })
                    .Foreground(new SolidColorBrush(Colors.White)),
                Props.OnStop
            ).Set(b =>
            {
                b.Padding = new Thickness(10, 4, 10, 4);
                b.MinWidth = sendButtonSize + 4; b.MinHeight = sendButtonSize - 4;
                b.CornerRadius = composerCornerRadius;
                b.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"];
            })
            .Resources(r =>
            {
                r.Set("ButtonBackgroundPointerOver", Ref("SystemFillColorCriticalBrush"));
                r.Set("ButtonBackgroundPressed", Ref("SystemFillColorCriticalBrush"));
                r.Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent));
                r.Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent));
            })
            .AutomationName(LocalizationHelper.GetString("Chat_Composer_Tooltip_Stop"));
        }

        Element workingBanner = Empty();

        Element permissionBanner = Props.PendingPermission is { } perm
            ? Border(
                Grid([GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
                    TextBlock($"⚠ {perm.ToolName}: {perm.Detail}")
                        .Set(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.CharacterEllipsis; })
                        .HAlign(HorizontalAlignment.Stretch)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    Button(LocalizationHelper.GetString("Chat_Permission_Allow"), () => Props.OnPermissionResponse(perm.RequestId, true))
                        .Set(b => { b.CornerRadius = new CornerRadius(4); b.Padding = new Thickness(12, 4, 12, 4); b.MinWidth = 0; b.MinHeight = 0; })
                        .VAlign(VerticalAlignment.Center)
                        .Margin(8, 0, 0, 0)
                        .Grid(row: 0, column: 1),
                    Button(LocalizationHelper.GetString("Chat_Permission_Deny"), () => Props.OnPermissionResponse(perm.RequestId, false))
                        .Set(b => { b.CornerRadius = new CornerRadius(4); b.Padding = new Thickness(12, 4, 12, 4); b.MinWidth = 0; b.MinHeight = 0; })
                        .VAlign(VerticalAlignment.Center)
                        .Margin(8, 0, 0, 0)
                        .Grid(row: 0, column: 2)
                ).Padding(12, 16, 12, 16)
              ).CornerRadius(8).Margin(24, 16, 24, 16)
               .Set(b => { b.MaxWidth = 720; b.HorizontalAlignment = HorizontalAlignment.Stretch; })
            : Empty();

        // ── ComposerLayout 분기 ───────────────────────────────────────
        // ThreeRow:    [3 dropdowns] [textbox] [attach/voice/more ... send]
        // Minimal:     [textbox] [send]
        // InlinePill:  [textbox] then BELOW: [chevron pill] ... [attach/voice/more] [send]
        if (composerLayout == ChatComposerLayout.InlinePill)
        {
            // Borderless "{Channel} · {Model} ⌄" pill that opens a single
            // MenuFlyout with three sections (Channel / Model / Thinking),
            // each using RadioMenuItem so the active selection is obvious.
            var channelLabel = Props.ChannelLabel ?? "main";
            var modelLabel = Props.CurrentModel ?? "model";
            double pillTextSize = Math.Max(10, composerIconSize - 2);
            double chevronSize = Math.Max(8, composerIconSize - 4);

            // Build three groups of RadioMenuItem entries. Section headers are
            // disabled, semibold, indented further toward the menu's left edge
            // so they read as labels rather than rows.
            var menuItems = new List<OpenClawTray.FunctionalUI.Core.MenuFlyoutItemBase>();
            // Header items: shift LEFT toward the dot column by zeroing the outer
            // padding (default ≈11px). Combined with SemiBold + slightly smaller
            // size they read as section labels rather than rows.
            var headerPad = new Thickness(0, 6, 8, 2);
            var headerWeight = Microsoft.UI.Text.FontWeights.SemiBold;

            menuItems.Add(MenuItem("Channel") with { IsEnabled = false, Padding = headerPad, FontWeight = headerWeight });
            foreach (var ch in channelOptions)
            {
                var name = ch;
                menuItems.Add(RadioMenuItem(
                    name,
                    "channel",
                    isChecked: name == channelLabel,
                    onClick: () => Props.OnChannelChanged(name)));
            }

            menuItems.Add(MenuSeparator());
            menuItems.Add(MenuItem("Model") with { IsEnabled = false, Padding = headerPad, FontWeight = headerWeight });
            foreach (var m in modelDisplay)
            {
                var name = m;
                menuItems.Add(RadioMenuItem(
                    name,
                    "model",
                    isChecked: name == modelLabel,
                    onClick: () => { if (models is { Length: > 0 } && Array.IndexOf(models, name) >= 0) Props.OnModelChanged(name); }));
            }

            menuItems.Add(MenuSeparator());
            menuItems.Add(MenuItem("Thinking") with { IsEnabled = false, Padding = headerPad, FontWeight = headerWeight });
            for (int i = 0; i < ThinkingLevelIds.Length; i++)
            {
                var id = ThinkingLevelIds[i];
                var label = ThinkingLevelLabels[i];
                menuItems.Add(RadioMenuItem(
                    label,
                    "reasoning",
                    isChecked: id == thinkingLevel,
                    onClick: () => Props.OnThinkingLevelChanged(id)));
            }

            var combinedPill = Button(
                (FlexRow(
                    TextBlock($"{channelLabel} · {modelLabel}")
                        .Set(t =>
                        {
                            t.FontSize = pillTextSize;
                            t.VerticalAlignment = VerticalAlignment.Center;
                        }),
                    TextBlock("\uE70D") // chevron down
                        .Set(t =>
                        {
                            t.FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons");
                            t.FontSize = chevronSize;
                            t.VerticalAlignment = VerticalAlignment.Center;
                            t.Margin = new Thickness(0, 1, 0, 0);
                        })
                ) with { ColumnGap = 6 }),
                () => { /* opens via attached flyout */ })
                .Set(b =>
                {
                    b.Padding = new Thickness(8, 4, 8, 4);
                    b.MinHeight = 28;
                    // WinUI Button default MinWidth is 120 (from ButtonStyle).
                    // Reset to 0 so hover background only paints the chevron-pill width.
                    b.MinWidth = 0;
                    b.CornerRadius = composerCornerRadius;
                    b.HorizontalAlignment = HorizontalAlignment.Left;
                })
                .Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                    .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
                .WithFlyout(MenuItems(FlyoutPlacementMode.Top, menuItems.ToArray()));

            // Put combinedPill directly into the Grid cell with HAlign(Left).
            // Wrapping in FlexRow caused the Button to stretch to the Star column width.
            var bottomRow = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                combinedPill.HAlign(HorizontalAlignment.Left).Grid(row: 0, column: 0),
                (FlexRow(attachBtn, voiceBtn, speakerBtn, settingsBtn, actionBtn, stopBtn) with { ColumnGap = 4 })                    .HAlign(HorizontalAlignment.Right).Grid(row: 0, column: 1)
            );

            return VStack(0,
                workingBanner,
                permissionBanner,
                Border(
                    VStack(8, textbox, attachmentChip, bottomRow)
                ).Padding(14, 12, 8, 12)
                 .Set(b =>
                 {
                     b.BorderThickness = new Thickness(0, 1, 0, 0);
                     b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SurfaceStrokeColorDefaultBrush"];
                 })
            );
        }

        var actionsRow = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            (FlexRow(attachBtn, voiceBtn, speakerBtn, settingsBtn, actionBtn, stopBtn)
                with { ColumnGap = 4 })
            .HAlign(HorizontalAlignment.Right)
            .Grid(row: 0, column: 1)
        );

        // ── Optional working / permission banners above the composer ──
        Element workingBanner2 = workingBanner;
        Element permissionBanner2 = permissionBanner;

        return VStack(0,
            workingBanner2,
            permissionBanner2,
            Border(
                VStack(8, dropdownsRow, textbox, voiceIndicator, attachmentChip, actionsRow)
            ).Padding(14, 12, 8, 12)
             .Set(b =>
             {
                 // Top divider only — mirrors Kenny's ChatShell ComposerBorder.
                 b.BorderThickness = new Thickness(0, 1, 0, 0);
                 b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SurfaceStrokeColorDefaultBrush"];
             })
        );
    }

    private static string NonEmptyGlyph(string? glyph, string fallback)
        => string.IsNullOrEmpty(glyph) ? fallback : glyph!;
}
