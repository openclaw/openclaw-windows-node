using System;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Minimal Application used by the test process.
///
/// Why no resource merge in the ctor: in WinAppSDK 1.8, accessing
/// <see cref="Application.Resources"/> during ctor throws COMException — the
/// underlying COM object isn't fully wired until after construction returns.
/// Resources are set up by <see cref="MergeStandardResources"/>, called from the
/// fixture once the dispatcher confirms the app is alive.
///
/// Renderers that look up theme keys (e.g. <c>BodyTextBlockStyle</c>) wrap each
/// lookup in try/catch and tolerate missing keys, so tests still get a live
/// visual tree even before the merge happens — assertions on text content,
/// hierarchy, and click handlers don't depend on theme styles.
/// </summary>
internal sealed class TestApp : Application
{
    private static readonly (string Key, Windows.UI.Color Color)[] FluentBrushFallbacks =
    [
        ("SolidBackgroundFillColorBaseBrush", Colors.White),
        ("LayerFillColorDefaultBrush", Colors.White),
        ("CardBackgroundFillColorDefaultBrush", Colors.White),
        ("CardStrokeColorDefaultBrush", ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00)),
        ("ControlFillColorTertiaryBrush", ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00)),
        ("ControlStrokeColorDefaultBrush", ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00)),
        ("SubtleFillColorSecondaryBrush", ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00)),
        ("SubtleFillColorTertiaryBrush", ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00)),
        ("AccentFillColorDefaultBrush", ColorHelper.FromArgb(0xFF, 0x00, 0x66, 0xCC)),
        ("AccentFillColorSecondaryBrush", ColorHelper.FromArgb(0xCC, 0x00, 0x66, 0xCC)),
        ("TextFillColorPrimaryBrush", Colors.Black),
        ("TextFillColorSecondaryBrush", ColorHelper.FromArgb(0xE3, 0x00, 0x00, 0x00)),
        ("TextFillColorTertiaryBrush", ColorHelper.FromArgb(0x99, 0x00, 0x00, 0x00)),
        ("TextOnAccentFillColorPrimaryBrush", Colors.White),
        ("SystemFillColorSuccessBrush", ColorHelper.FromArgb(0xFF, 0x0F, 0x7B, 0x0F)),
        ("SystemFillColorCautionBrush", ColorHelper.FromArgb(0xFF, 0x9D, 0x5D, 0x00)),
        ("SystemFillColorCriticalBrush", ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
    ];

    /// <summary>
    /// Merge XamlControlsResources + the production App.xaml's custom keys
    /// (LobsterAccentBrush, AccentButtonStyle) so renderers that look them up
    /// resolve a real value. Call this ON THE UI THREAD after Application.Current
    /// is set.
    /// </summary>
    public void MergeStandardResources()
    {
        if (!TryGetResources(out var resources))
        {
            return;
        }

        try
        {
            resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch
        {
            // If XamlControlsResources can't load (rare; missing assembly), keep
            // going — the renderers degrade gracefully without theme styles.
        }

        TryAddResource(resources, "LobsterAccentBrush",
            "<SolidColorBrush xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Color='#E74C3C' />");

        TryAddResource(resources, "AccentButtonStyle",
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "TargetType='Button'>" +
            "<Setter Property='Foreground' Value='White' />" +
            "<Setter Property='CornerRadius' Value='4' />" +
            "</Style>");

        EnsureFluentBrushFallbacks(resources);
    }

    internal static void EnsureFluentBrushFallbacks(ResourceDictionary resources)
    {
        foreach (var (key, color) in FluentBrushFallbacks)
        {
            TryAddBrushResource(resources, key, color);
        }
    }

    private bool TryGetResources(out ResourceDictionary resources)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                resources = Resources;
                return true;
            }
            // slopwatch-ignore: SW003 Test fixture resource setup is best-effort and must not hide the test outcome.
            catch
            {
                Thread.Sleep(10);
            }
        }

        resources = null!;
        return false;
    }

    private static void TryAddResource(ResourceDictionary resources, string key, string xaml)
    {
        try
        {
            resources[key] = XamlReader.Load(xaml);
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch
        {
            // best-effort; missing key just means renderers fall back.
        }
    }

    private static void TryAddBrushResource(ResourceDictionary resources, string key, Windows.UI.Color color)
    {
        try
        {
            resources[key] = new SolidColorBrush(color);
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch
        {
            // best-effort; missing key just means renderers fall back.
        }
    }
}
