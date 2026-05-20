using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClawTray.Chat.Explorations;

/// <summary>
/// FunctionalUI 좌측 패널: ChatExplorationState 의 모든 토글을 슬라이더/체크박스/콤보박스/
/// ColorPicker 로 묶어서 보여준다. 변경하면 ChatExplorationState 가 즉시 업데이트되고
/// 옆 라이브 챗이 다시 그려진다 (각 챗 컴포넌트가 Changed 를 구독 중).
/// </summary>
public class ChatExplorationsPanel : Component
{
    public override Element Render()
    {
        var rev = UseState(0, threadSafe: true);
        var revRef = UseRef(0);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler h = (_, _) =>
            {
                revRef.Current++;
                rev.Set(revRef.Current);
            };
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        var presetNameState = UseState("", threadSafe: true);
        var presetsState = UseState<List<ChatExplorationPreset>>(ChatExplorationPresetStore.LoadAll(), threadSafe: true);
        var selectedPresetIdx = UseState(0, threadSafe: true);

        // ── A. Surface / Backdrop ────────────────────────────────────
        var backdropSection = Section("A. Surface",
            EnumCombo("Backdrop", ChatExplorationState.BackdropMode,
                v => ChatExplorationState.BackdropMode = v,
                ChatBackdropMode.Mica, ChatBackdropMode.MicaAlt, ChatBackdropMode.Acrylic, ChatBackdropMode.Solid),
            Toggle("Uses host backdrop", ChatExplorationState.UsesHostBackdrop,
                v => ChatExplorationState.UsesHostBackdrop = v),
            EnumCombo("Preview theme", ChatExplorationState.PreviewTheme,
                v => ChatExplorationState.PreviewTheme = v,
                ChatPreviewTheme.System, ChatPreviewTheme.Light, ChatPreviewTheme.Dark)
        );

        // ── B. Variation 프리셋 + 커스텀 프리셋 ─────────────────────
        var presets = presetsState.Value;
        var presetNames = presets.Count == 0
            ? new[] { "(no custom presets)" }
            : presets.Select(p => p.IsDefault ? $"★ {p.Name}" : p.Name).ToArray();
        var safeIdx = presets.Count == 0 ? 0 : Math.Clamp(selectedPresetIdx.Value, 0, presets.Count - 1);
        var selectedPreset = presets.Count == 0 ? null : presets[safeIdx];

        var variationSection = Section("B. Variation preset",
            EnumCombo("Variation", ChatExplorationState.Variation,
                v => { ChatExplorationState.Variation = v; ChatVariationPresets.Apply(v); },
                ChatVariation.Calm, ChatVariation.Compact, ChatVariation.Plain),
            TextBlock("Custom presets").Set(t => { t.FontSize = 12; t.Opacity = 0.85; }),
            (FlexRow(
                TextField(presetNameState.Value, v => presetNameState.Set(v))
                    .Set(tb => { tb.PlaceholderText = "name"; tb.MinWidth = 130; tb.Height = 28; }),
                Button("💾 Save", () =>
                {
                    var name = (presetNameState.Value ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) return;
                    var existingDefault = presetsState.Value.Any(p => p.Name == name && p.IsDefault);
                    var list = presetsState.Value.Where(p => p.Name != name).ToList();
                    list.Add(ChatExplorationPresetStore.Capture(name) with { IsDefault = existingDefault });
                    ChatExplorationPresetStore.SaveAll(list);
                    presetsState.Set(list);
                    selectedPresetIdx.Set(list.FindIndex(p => p.Name == name));
                    presetNameState.Set("");
                }).Set(b => b.Height = 28)
            ) with { ColumnGap = 6 }),
            (FlexRow(
                ComboBox(presetNames, safeIdx, i => selectedPresetIdx.Set(i))
                    .Set(cb => { cb.MinWidth = 130; cb.Height = 28; }),
                Button("📂 Load", () =>
                {
                    if (presets.Count == 0) return;
                    ChatExplorationPresetStore.Apply(presets[safeIdx]);
                }).Set(b => b.Height = 28),
                Button(selectedPreset?.IsDefault == true ? "★ Unset default" : "☆ Set default", () =>
                {
                    if (selectedPreset is null) return;
                    var newName = selectedPreset.IsDefault ? null : selectedPreset.Name;
                    var updated = ChatExplorationPresetStore.SetDefault(newName);
                    presetsState.Set(updated);
                }).Set(b => b.Height = 28),
                Button("🗑", () =>
                {
                    if (presets.Count == 0) return;
                    var list = presets.Where((_, i) => i != safeIdx).ToList();
                    ChatExplorationPresetStore.SaveAll(list);
                    presetsState.Set(list);
                    selectedPresetIdx.Set(0);
                }).Set(b => b.Height = 28)
            ) with { ColumnGap = 6 })
        );

        // ── C. Bubble / Layout ───────────────────────────────────────
        var bubbleSection = Section("C. Bubble / Layout",
            SliderRow("Bubble corner radius", ChatExplorationState.BubbleCornerRadius, 0, 30,
                v => ChatExplorationState.BubbleCornerRadius = v),
            SliderRow("Gutter", ChatExplorationState.Gutter, 0, 160,
                v => ChatExplorationState.Gutter = v),
            SliderRow("Message gap", ChatExplorationState.MessageGap, 0, 40,
                v => ChatExplorationState.MessageGap = v),
            SliderRow("Bubble max width", ChatExplorationState.BubbleMaxWidth, 200, 900,
                v => ChatExplorationState.BubbleMaxWidth = v),
            SliderRow("Bubble side margin", ChatExplorationState.BubbleSideMargin, 0, 40,
                v => ChatExplorationState.BubbleSideMargin = v),
            EnumCombo("Padding density", ChatExplorationState.PaddingDensity,
                v => ChatExplorationState.PaddingDensity = v,
                ChatPaddingDensity.Cozy, ChatPaddingDensity.Comfortable, ChatPaddingDensity.Compact)
        );

        // ── C.1. Bubble visibility & footer ──────────────────────────
        var visibilitySection = Section("C.1. Visibility & footer",
            Toggle("Show assistant bubbles", ChatExplorationState.ShowAssistantBubbles,
                v => ChatExplorationState.ShowAssistantBubbles = v),
            Toggle("Show tool calls", ChatExplorationState.ShowToolCalls,
                v => ChatExplorationState.ShowToolCalls = v),
            Toggle("Show timestamps", ChatExplorationState.ShowTimestamps,
                v => ChatExplorationState.ShowTimestamps = v),
            Toggle("  Show sender name", ChatExplorationState.ShowSenderName,
                v => ChatExplorationState.ShowSenderName = v),
            Toggle("  Show model name", ChatExplorationState.ShowModelName,
                v => ChatExplorationState.ShowModelName = v),
            Toggle("  Show tokens", ChatExplorationState.ShowTokens,
                v => ChatExplorationState.ShowTokens = v),
            Toggle("  Show context %", ChatExplorationState.ShowContextPercent,
                v => ChatExplorationState.ShowContextPercent = v)
        );

        // ── D. Avatar ────────────────────────────────────────────────
        var avatarSection = Section("D. Avatar",
            Toggle("Show avatars", ChatExplorationState.ShowAvatars,
                v => ChatExplorationState.ShowAvatars = v),
            EnumCombo("Avatar mode", ChatExplorationState.AvatarMode,
                v => ChatExplorationState.AvatarMode = v,
                ChatAvatarMode.Both, ChatAvatarMode.AgentOnly, ChatAvatarMode.None)
        );

        // ── E. Composer ──────────────────────────────────────────────
        var composerSection = Section("E. Composer",
            EnumCombo("Composer layout", ChatExplorationState.ComposerLayout,
                v => ChatExplorationState.ComposerLayout = v,
                ChatComposerLayout.ThreeRow, ChatComposerLayout.InlinePill, ChatComposerLayout.Minimal),
            SliderRow("Composer corner radius", ChatExplorationState.ComposerCornerRadius, 0, 24,
                v => ChatExplorationState.ComposerCornerRadius = v),
            SliderRow("Composer icon size", ChatExplorationState.ComposerIconSize, 8, 32,
                v => ChatExplorationState.ComposerIconSize = v),
            SliderRow("Send button size", ChatExplorationState.SendButtonSize, 16, 64,
                v => ChatExplorationState.SendButtonSize = v)
        );

        // ── E.1. Composer icons ──────────────────────────────────────
        var iconsSection = Section("E.1. Composer icons (Segoe MDL2)",
            IconRow("Send",
                () => ChatExplorationState.SendIconShow,   v => ChatExplorationState.SendIconShow = v,
                () => ChatExplorationState.SendIconGlyph,  v => ChatExplorationState.SendIconGlyph = v),
            IconRow("Stop",
                () => ChatExplorationState.StopIconShow,   v => ChatExplorationState.StopIconShow = v,
                () => ChatExplorationState.StopIconGlyph,  v => ChatExplorationState.StopIconGlyph = v),
            IconRow("Attach",
                () => ChatExplorationState.AttachIconShow, v => ChatExplorationState.AttachIconShow = v,
                () => ChatExplorationState.AttachIconGlyph, v => ChatExplorationState.AttachIconGlyph = v),
            IconRow("Voice",
                () => ChatExplorationState.VoiceIconShow,  v => ChatExplorationState.VoiceIconShow = v,
                () => ChatExplorationState.VoiceIconGlyph, v => ChatExplorationState.VoiceIconGlyph = v),
            IconRow("More",
                () => ChatExplorationState.MoreIconShow,   v => ChatExplorationState.MoreIconShow = v,
                () => ChatExplorationState.MoreIconGlyph,  v => ChatExplorationState.MoreIconGlyph = v)
        );

        // ── F. Brush overrides (ColorPicker) ─────────────────────────
        var brushSection = Section("F. Brush overrides (color picker)",
            ColorRow("Accent",
                ChatExplorationState.AccentBrushOverride,
                b => ChatExplorationState.AccentBrushOverride = b),
            ColorRow("User bubble",
                ChatExplorationState.UserBubbleBrushOverride,
                b => ChatExplorationState.UserBubbleBrushOverride = b),
            ColorRow("Assistant bubble",
                ChatExplorationState.AssistantBubbleBrushOverride,
                b => ChatExplorationState.AssistantBubbleBrushOverride = b),
            ColorRow("Send button",
                ChatExplorationState.SendButtonBrushOverride,
                b => ChatExplorationState.SendButtonBrushOverride = b)
        );

        // ── G. Preview state ─────────────────────────────────────────
        // Lets designers preview each chat lifecycle state (loading,
        // empty/zero, empty thread, thinking indicator, pending
        // permission) without having to reproduce the real backend
        // conditions. Live = no override.
        var previewStateSection = Section("G. Preview state",
            EnumCombo("Force chat state", ChatExplorationState.PreviewState,
                v => ChatExplorationState.PreviewState = v,
                ChatPreviewState.Live,
                ChatPreviewState.Loading,
                ChatPreviewState.Empty,
                ChatPreviewState.Thinking,
                ChatPreviewState.PendingPermission)
        );

        // ── H. Tool burst (multi-step task framing) ──────────────────
        // Variants explored here mirror competitor patterns:
        //   Plain          — current Cursor-lite (no task framing)
        //   TaskHeader     — Cursor's "Tool calls (N steps)" + per-row list
        //   CompactSummary — single collapsed "Task · 3 steps" row, expands
        //   FooterReframe  — plain rows, just reframe the footer text
        var toolBurstSection = Section("H. Tool burst style",
            EnumCombo("Burst style", ChatExplorationState.ToolBurstStyle,
                v => ChatExplorationState.ToolBurstStyle = v,
                ToolBurstStyle.Plain,
                ToolBurstStyle.TaskHeader,
                ToolBurstStyle.CompactSummary,
                ToolBurstStyle.FooterReframe,
                ToolBurstStyle.TaskList),
            Toggle("Show step numbers (1./2./3.)", ChatExplorationState.ShowStepNumbers,
                v => ChatExplorationState.ShowStepNumbers = v)
        );

        var resetBtn = Button("Reset all", () =>
        {
            ChatVariationPresets.Apply(ChatVariation.Calm);
            ChatExplorationState.AccentBrushOverride = null;
            ChatExplorationState.UserBubbleBrushOverride = null;
            ChatExplorationState.AssistantBubbleBrushOverride = null;
            ChatExplorationState.SendButtonBrushOverride = null;
            ChatExplorationState.PreviewState = ChatPreviewState.Live;
        });

        var pinBtn = Button(
            ChatWindowPinState.IsPinned ? "📌 Unpin tray chat popup" : "📌 Pin tray chat popup",
            () =>
            {
                try
                {
                    if (ChatWindowPinState.IsPinned)
                    {
                        ChatWindowPinState.IsPinned = false;
                        rev.Set(rev.Value + 1); // refresh button label
                        return;
                    }
                    ChatWindowPinState.IsPinned = true;
                    (App.Current as App)?.ShowChatWindow();
                    rev.Set(rev.Value + 1);
                }
                catch { /* ignore */ }
            });

        var content = VStack(12,
            TextBlock("Chat explorations").Set(t => { t.FontSize = 18; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; }),
            backdropSection, variationSection,
            bubbleSection, visibilitySection,
            avatarSection,
            composerSection, iconsSection,
            brushSection,
            previewStateSection,
            toolBurstSection,
            (FlexRow(resetBtn, pinBtn) with { ColumnGap = 8 })
        ).Padding(16, 16, 16, 16);

        return ScrollView(content);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    static Element Section(string title, params Element[] rows)
    {
        var children = new Element[rows.Length + 1];
        children[0] = TextBlock(title).Set(t => { t.FontSize = 13; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; t.Opacity = 0.85; });
        Array.Copy(rows, 0, children, 1, rows.Length);
        return Border(VStack(8, children))
            .Padding(12, 10, 12, 10)
            .Set(b =>
            {
                b.CornerRadius = new CornerRadius(6);
                b.BorderThickness = new Thickness(1);
                b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ControlStrokeColorDefaultBrush"];
            });
    }

    static Element SliderRow(string label, double value, double min, double max, Action<double> set)
        => VStack(2,
            TextBlock($"{label}: {value:0.#}").Set(t => t.FontSize = 12),
            Slider(value, min, max, set)
        );

    static Element Toggle(string label, bool isChecked, Action<bool> set)
        => CheckBox(isChecked, set, label);

    static Element EnumCombo<T>(string label, T current, Action<T> set, params T[] options) where T : struct, Enum
    {
        var names = new string[options.Length];
        for (int i = 0; i < options.Length; i++) names[i] = options[i].ToString();
        var idx = Array.IndexOf(options, current);
        if (idx < 0) idx = 0;
        return VStack(2,
            TextBlock(label).Set(t => t.FontSize = 12),
            ComboBox(names, idx, i => { if (i >= 0 && i < options.Length) set(options[i]); })
                .Set(cb => { cb.Height = 28; cb.MinWidth = 200; })
        );
    }

    static Element IconRow(string label, Func<bool> getShow, Action<bool> setShow,
                                          Func<string> getGlyph, Action<string> setGlyph)
    {
        var glyph = getGlyph();
        var preview = string.IsNullOrEmpty(glyph) ? "·" : glyph;
        return (FlexRow(
            CheckBox(getShow(), v => setShow(v), label).Set(c => c.MinWidth = 70),
            TextField(glyph, v => setGlyph(v))
                .Set(tb => { tb.PlaceholderText = "\\uE724"; tb.MinWidth = 90; tb.Height = 28; tb.FontFamily = new FontFamily("Consolas, monospace"); }),
            Border(
                TextBlock(preview).Set(t =>
                {
                    t.FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons");
                    t.FontSize = 16;
                })
            ).Padding(6, 2, 6, 2).VAlign(VerticalAlignment.Center)
        ) with { ColumnGap = 6 });
    }

    static Element ColorRow(string label, Brush? current, Action<Brush?> set)
    {
        var hex = ChatExplorationPresetStore.BrushToHex(current) ?? "(default)";
        var swatchColor = current is SolidColorBrush scb ? scb.Color : Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC);

        var swatch = Border(Empty())
            .Set(b =>
            {
                b.Width = 24; b.Height = 24;
                b.CornerRadius = new CornerRadius(4);
                b.Background = current ?? new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
                b.BorderThickness = new Thickness(1);
                b.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ControlStrokeColorDefaultBrush"];
            }).VAlign(VerticalAlignment.Center);

        var pickerButton = Button(
            (FlexRow(
                swatch,
                Caption(hex).VAlign(VerticalAlignment.Center)
            ) with { ColumnGap = 8 }),
            () => { /* opens via attached flyout */ })
            .Set(b => { b.Height = 32; b.MinWidth = 160; b.HorizontalContentAlignment = HorizontalAlignment.Left; })
            .WithFlyout(ContentFlyout(
                VStack(8,
                    ColorPicker(swatchColor, c =>
                        set(new SolidColorBrush(Color.FromArgb(0xFF, c.R, c.G, c.B))))
                ).Padding(8, 8, 8, 8),
                FlyoutPlacementMode.Right));

        var clearBtn = Button("✕", () => set(null))
            .Set(b => { b.Height = 32; b.MinWidth = 32; });

        return VStack(2,
            TextBlock(label).Set(t => t.FontSize = 12),
            (FlexRow(pickerButton, clearBtn) with { ColumnGap = 6 })
        );
    }
}
