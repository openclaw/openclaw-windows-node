using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

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
    /// <summary>
    /// Merge XamlControlsResources + the production App.xaml's custom keys
    /// (LobsterAccentBrush, AccentButtonStyle) so renderers that look them up
    /// resolve a real value. Call this ON THE UI THREAD after Application.Current
    /// is set.
    /// </summary>
    public void MergeStandardResources()
    {
        try
        {
            Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        }
        catch
        {
            // If XamlControlsResources can't load (rare; missing assembly), keep
            // going — the renderers degrade gracefully without theme styles.
        }

        TryAddResource("LobsterAccentBrush",
            "<SolidColorBrush xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Color='#E74C3C' />");

        TryAddResource("AccentButtonStyle",
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "TargetType='Button'>" +
            "<Setter Property='Foreground' Value='White' />" +
            "<Setter Property='CornerRadius' Value='4' />" +
            "</Style>");
    }

    private void TryAddResource(string key, string xaml)
    {
        try
        {
            Resources[key] = XamlReader.Load(xaml);
        }
        catch
        {
            // best-effort; missing key just means renderers fall back.
        }
    }
}
