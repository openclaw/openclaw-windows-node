using System;
using System.Threading;
using System.IO;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

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
internal sealed class TestApp : Application, IXamlMetadataProvider
{
    // The generated product provider includes native WinUI templates and setup controls.
    private readonly OpenClawTray.OpenClaw_Tray_WinUI_XamlTypeInfo.XamlMetaDataProvider _metadata = new();

    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();

    public TestApp()
    {
        var proofTheme = Environment.GetEnvironmentVariable("OPENCLAW_UI_TEST_APP_THEME");
        if (!string.IsNullOrEmpty(proofTheme))
        {
            if (!Enum.TryParse<ApplicationTheme>(proofTheme, out var theme) || !Enum.IsDefined(theme))
                throw new InvalidOperationException("OPENCLAW_UI_TEST_APP_THEME must be Light or Dark.");
            RequestedTheme = theme;
        }
        // Resolve compiled product strings, not the testhost executable's empty PRI.
        var productResources = new ResourceManager(
            Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.pri"));
        ResourceManagerRequested += (_, args) => args.CustomResourceManager = productResources;
        var localization = typeof(OpenClaw.SetupEngine.UI.SetupWindow).Assembly.GetType(
            "OpenClaw.SetupEngine.UI.SetupLocalization", throwOnError: true)!;
        var resourceCache = localization.GetField("s_resourceManager", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(localization.FullName, "s_resourceManager");
        resourceCache.SetValue(null, productResources);
        UnhandledException += (_, args) =>
            Console.Error.WriteLine($"WinUI test host unhandled exception: {args.Exception}");
    }

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
    /// Merge XamlControlsResources + production App.xaml's custom keys so
    /// renderers that look them up resolve a real value. Call this ON THE UI
    /// THREAD after Application.Current is set.
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
            "<Setter Property='Background' Value='{ThemeResource AccentFillColorDefaultBrush}' />" +
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
            if (!resources.ContainsKey(key))
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
            if (!resources.ContainsKey(key))
                resources[key] = new SolidColorBrush(color);
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch
        {
            // best-effort; missing key just means renderers fall back.
        }
    }

}
