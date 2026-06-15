using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using System;
using System.Linq;
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
public record ChannelGroup(string AgentLabel, (string Id, string Title)[] Sessions);

public record OpenClawComposerProps(
    string ConnectionState,
    bool TurnActive,
    string ChannelLabel,
    string? ChannelId,
    ChannelGroup[] AvailableChannels,
    string[] AvailableModels,
    string? CurrentModel,
    string? CurrentThinkingLevel,
    Action<string, ChatAttachment?> OnSend,
    Action OnStop,
    Action<string> OnChannelChanged,
    Action<string> OnModelChanged,
    Action<string> OnThinkingLevelChanged,
    Action<bool> OnPermissionsChanged,
    Func<CancellationToken, Action?, Task<string?>>? OnVoiceRequest = null,
    Action? OnAttachClick = null,
    ChatAttachment? PendingAttachment = null,
    Action? OnAttachmentRemoved = null,
    bool IsSpeakerMuted = false,
    Action? OnSpeakerToggle = null,
    Action? OnSettingsClick = null,
    string? VoiceTranscript = null,
    float VoiceAudioLevel = 0f,
    Action<Action>? RegisterVoiceStarter = null,
    Action<ChatAttachment>? OnAttachmentPasted = null,
    bool ShowToolCalls = true,
    Action<bool>? OnShowToolCallsChanged = null,
    bool IsCompact = false);

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

        var composerCornerRadius = new CornerRadius(8);
        const double composerIconSize = 16;
        const double sendButtonSize = 40;

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
        // One-time hook flag for the TextBox Paste event so we don't re-attach
        // the handler on every re-render (Set() runs each render).
        var pasteHookedRef = UseRef(false);
        // Cache the BitmapImage built for the current attachment so we rebuild
        // it only when the attachment instance changes (not on every render).
        var attachmentImageRef = UseRef<(ChatAttachment? Att, Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Bmp)>((null, null));

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
            "incompatible-gateway" => LocalizationHelper.GetString("Chat_Composer_Placeholder_IncompatibleGateway"),
            _ => LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected")
        };

        // ── Row 1: three compact dropdowns ─────────────────────────────
        // Build grouped session ComboBox directly (bypassing the FunctionalUI
        // ComboBox helper which only supports flat string[] items).
        var groups = Props.AvailableChannels;
        var channelCombo = Border()
            .Set(border =>
            {
                var cb = new ComboBox
                {
                    MinWidth = 0,
                    Width = double.NaN,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 4, 0),
                    CornerRadius = composerCornerRadius,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                ComboBoxItem? selectedItem = null;
                foreach (var group in groups)
                {
                    if (groups.Length > 1)
                    {
                        cb.Items.Add(new ComboBoxItem
                        {
                            Content = group.AgentLabel,
                            IsEnabled = false,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            FontSize = 10,
                            Padding = new Thickness(4, 2, 4, 2),
                            IsHitTestVisible = false,
                        });
                    }
                    foreach (var session in group.Sessions)
                    {
                        var item = new ComboBoxItem
                        {
                            Content = session.Title,
                            Tag = session.Id,
                            Padding = groups.Length > 1
                                ? new Thickness(16, 4, 4, 4)
                                : new Thickness(8, 4, 4, 4),
                        };
                        cb.Items.Add(item);
                        if (session.Id == (Props.ChannelId ?? ""))
                            selectedItem = item;
                    }
                }

                if (selectedItem != null)
                    cb.SelectedItem = selectedItem;

                var onChanged = Props.OnChannelChanged;
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is ComboBoxItem { Tag: string id })
                        onChanged(id);
                };

                border.Child = cb;
            });

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
            cb.Width = double.NaN;
            cb.Height = 28;
            cb.FontSize = 11;
            cb.Padding = new Thickness(8, 0, 4, 0);
            cb.CornerRadius = composerCornerRadius;
            cb.HorizontalAlignment = HorizontalAlignment.Stretch;
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
                cb.Width = double.NaN;
                cb.Height = 28;
                cb.FontSize = 11;
                cb.Padding = new Thickness(8, 0, 4, 0);
                cb.CornerRadius = composerCornerRadius;
                cb.HorizontalAlignment = HorizontalAlignment.Stretch;
            }).VAlign(VerticalAlignment.Center);

        Element dropdownsRow = Grid([GridSize.Star(1.2), GridSize.Star(), GridSize.Star(0.62)], [GridSize.Auto],
            channelCombo.Margin(0, 0, 6, 0).HAlign(HorizontalAlignment.Stretch).Grid(row: 0, column: 0),
            modelCombo.Margin(0, 0, 6, 0).HAlign(HorizontalAlignment.Stretch).Grid(row: 0, column: 1),
            reasoningCombo.HAlign(HorizontalAlignment.Stretch).Grid(row: 0, column: 2)
        ).HAlign(HorizontalAlignment.Stretch);

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
                textBoxRef.Current = tb;
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
                if (e.Key == global::Windows.System.VirtualKey.Enter)
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
        if (Props.PendingAttachment is { } att)
        {
            var isImage = att.Type == "image";

            Element removeBtn = Button(
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
                .AutomationName("Remove attachment");

            if (isImage)
            {
                // Build (and cache) a BitmapImage from the base64 content.
                // Rebuild only when the attachment instance changes — base64
                // decode + stream copy is non-trivial work to repeat per
                // keystroke re-render.
                var cached = attachmentImageRef.Current;
                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? bmp = cached.Bmp;
                if (!ReferenceEquals(cached.Att, att) || bmp is null)
                {
                    bmp = TryCreateBitmapFromBase64(att.Content);
                    attachmentImageRef.Current = (att, bmp);
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
                            t.FontFamily = new FontFamily("Segoe Fluent Icons");
                            t.FontSize = 16;
                            t.VerticalAlignment = VerticalAlignment.Center;
                        });
                }

                // Circular close button that floats in the top-right corner
                // of the thumbnail. Distinct from the chip's flat removeBtn
                // because we need an opaque background (so the × is readable
                // over any image) and a contrast-friendly hover.
                var floatingRemove = Button(
                        TextBlock("\uE711")
                            .Set(t =>
                            {
                                t.FontFamily = new FontFamily("Segoe Fluent Icons");
                                t.FontSize = 10;
                                t.HorizontalAlignment = HorizontalAlignment.Center;
                                t.VerticalAlignment = VerticalAlignment.Center;
                            }),
                        () => Props.OnAttachmentRemoved?.Invoke())
                    .Set(b =>
                    {
                        b.Width = 22;
                        b.Height = 22;
                        b.MinWidth = 0; b.MinHeight = 0;
                        b.Padding = new Thickness(0);
                        b.CornerRadius = new CornerRadius(11);
                        b.BorderThickness = new Thickness(1);
                    })
                    .Resources(r => r
                        .Set("ButtonBackground", Ref("SolidBackgroundFillColorBaseBrush"))
                        .Set("ButtonBackgroundPointerOver", Ref("SolidBackgroundFillColorTertiaryBrush"))
                        .Set("ButtonBackgroundPressed", Ref("SolidBackgroundFillColorQuarternaryBrush"))
                        .Set("ButtonForeground", Ref("TextFillColorPrimaryBrush"))
                        .Set("ButtonForegroundPointerOver", Ref("TextFillColorPrimaryBrush"))
                        .Set("ButtonForegroundPressed", Ref("TextFillColorPrimaryBrush"))
                        .Set("ButtonBorderBrush", Ref("CardStrokeColorDefaultBrush"))
                        .Set("ButtonBorderBrushPointerOver", Ref("CardStrokeColorDefaultBrush"))
                        .Set("ButtonBorderBrushPressed", Ref("CardStrokeColorDefaultBrush")))
                    .AutomationName("Remove attachment")
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

                attachmentPreview = Border(thumbWithClose)
                    .Padding(8, 12, 8, 4);
            }
            else
            {
                attachmentPreview = Border(
                    Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock("\uE8A5") // Page glyph
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
                        removeBtn.Grid(row: 0, column: 2)
                    )
                ).Padding(4, 4, 4, 0);
            }
        }

        // Composer "card" — wraps the attachment preview (if any) and the
        // textbox in a single bordered container so the preview reads as
        // content inside the chat input rather than a separate row.
        var composerInput = Border(
            VStack(0, attachmentPreview, textbox)
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
            .AutomationName(tip)
            .SetToolTip(tip);

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
        var speakerBtn = Props.OnSpeakerToggle is not null
            ? IconButton(
                Props.IsSpeakerMuted ? "\uE74F" : "\uE767",  // SpeakerMute : Speaker
                Props.IsSpeakerMuted ? "Unmute" : "Mute",
                () => Props.OnSpeakerToggle())
            : Empty();
        // Toggle tool-call visibility. Same wrench icon in both states;
        // reduced opacity when tool calls are hidden to indicate "off"
        // without looking disabled. Tooltip clarifies the action.
        var showTools = Props.ShowToolCalls;
        var toolToggleBtn = IconButton(
            "\uE90F",  // Wrench
            showTools ? "Hide tool calls & usage" : "Show tool calls & usage",
            () => Props.OnShowToolCallsChanged?.Invoke(!Props.ShowToolCalls))
            .Set(b => b.Opacity = showTools ? 1.0 : 0.55);

        // Send button — always present so the user can queue follow-up messages
        // even while the assistant is responding.
        var sendBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
        const string sendGlyph = "\uE724";
        const string stopGlyph = "\uE71A";

        var hasText = hasTextState.Value || Props.PendingAttachment is not null;
        var sendTooltip = LocalizationHelper.GetString("Chat_Composer_Tooltip_Send");
        var glyphBrush = hasText
            ? (Brush)new SolidColorBrush(Colors.White)
            : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
        var actionBtn = Button(
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
        .AutomationName(sendTooltip)
        .SetToolTip(sendTooltip);

        // Stop button — shown inline NEXT TO the send button (to its right)
        // when the assistant is responding, matching the gateway web UI pattern.
        Element stopBtn = Empty();
        if (Props.TurnActive)
        {
            var stopTooltip = LocalizationHelper.GetString("Chat_Composer_Tooltip_Stop");
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
            .AutomationName(stopTooltip)
            .SetToolTip(stopTooltip);
        }

        Element workingBanner = Empty();

        // Permission/exec-approval banner used to live here, pinned above
        // the composer. It now renders inline in the timeline as a
        // ChatTimelineItemKind.PermissionRequest entry so the conversation
        // history records every approval (and its decided/expired badge)
        // in chronological order. See OpenClawChatTimeline.RenderPermissionEntry.

        var actionsRow = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            Empty().Grid(row: 0, column: 0),
            (FlexRow(attachBtn, voiceCancelBtn, voiceBtn, speakerBtn, toolToggleBtn, actionBtn, stopBtn)
                with { ColumnGap = 4 })
            .HAlign(HorizontalAlignment.Right)
            .Grid(row: 0, column: 1)
        );

        // ── Optional working banner above the composer ──
        Element workingBanner2 = workingBanner;

        return VStack(0,
            workingBanner2,
            Border(
                VStack(8, dropdownsRow, composerInput, voiceIndicator, actionsRow.Margin(0, -8, 0, -4))
            ).Padding(16, 12, 16, 12)
             .Set(b =>
             {
                 // Top divider only — mirrors Kenny's ChatShell ComposerBorder.
                 b.BorderThickness = new Thickness(0, 1, 0, 0);
                 b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SurfaceStrokeColorDefaultBrush"];
             })
        );
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
