using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;

namespace OpenClawTray.Windows;

/// <summary>
/// Preview dialog for a diagnostics-bundle string. The user can review the
/// exact text the app would send before deciding to copy it to the clipboard
/// or save it to a file. Addresses the rubber-duck concern that clipboard-only
/// bundle commands are opaque/trust-hostile.
/// </summary>
public sealed partial class DiagnosticsBundleDialog : ContentDialog
{
    private string _bundleText = string.Empty;
    private string _suggestedFileName = "openclaw-diagnostics.txt";
    private Func<IntPtr>? _hwndProvider;

    public DiagnosticsBundleDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnCopyClick;
        SecondaryButtonClick += OnSaveClick;
    }

    /// <summary>
    /// Populate the dialog. <paramref name="hwndProvider"/> is invoked
    /// when "Save to file" is clicked, so we resolve the host HWND
    /// just-in-time (Hanselman v2 #4 + #7). If the host window has
    /// closed between Configure and Save, the provider returns
    /// IntPtr.Zero and the picker silently no-ops instead of crashing
    /// on a stale handle.
    /// </summary>
    public void Configure(string bundleText, string headerCaption, string suggestedFileName, Func<IntPtr> hwndProvider)
    {
        _bundleText = bundleText ?? string.Empty;
        _suggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? "openclaw-diagnostics.txt"
            : suggestedFileName;
        _hwndProvider = hwndProvider;
        BundleText.Text = _bundleText;
        BundleHeaderText.Text = headerCaption ?? string.Empty;
    }

    private void OnCopyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ClipboardHelper.CopyText(_bundleText);
        // Do NOT close on copy — the user may want to also save the file
        // before dismissing. Cancel the implicit close.
        args.Cancel = true;
        PrimaryButtonText = "Copied";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) =>
        {
            PrimaryButtonText = "Copy to clipboard";
            timer.Stop();
        };
        timer.Start();
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open after Save so the user can also Copy
        // (or save again to a different location). Mirrors OnCopyClick.
        // Use a deferral so picker/write failures can update the button
        // instead of vanishing in a fire-and-forget task.
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            SecondaryButtonText = "Saving...";
            var result = await SaveToFileAsync();
            SecondaryButtonText = result.ButtonText;
        }
        finally
        {
            deferral.Complete();
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) =>
        {
            SecondaryButtonText = "Save to file";
            timer.Stop();
        };
        timer.Start();
    }

    private async Task<SaveResult> SaveToFileAsync()
    {
        try
        {
            // Resolve HWND just-in-time so a closed/recreated host
            // window never lands a stale handle in the native save dialog.
            var hwnd = _hwndProvider?.Invoke() ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("DiagnosticsBundleDialog save: no host hwnd; saving to Desktop instead.");
                var fallback = await SaveToDesktopAsync();
                return new SaveResult(fallback, "Saved to Desktop");
            }

            var selectedPath = await Win32FilePickerHelper.PickSaveFileAsync(
                hwnd,
                title: "Save diagnostics bundle",
                suggestedFileName: Path.GetFileName(_suggestedFileName),
                defaultExtension: "txt");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                await File.WriteAllTextAsync(selectedPath, _bundleText);
                return new SaveResult(selectedPath, "Saved");
            }

            return new SaveResult(null, "Save cancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiagnosticsBundleDialog save failed: {ex.Message}");
            return new SaveResult(null, "Save failed");
        }
    }

    private async Task<string> SaveToDesktopAsync()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(desktop);

        var baseName = Path.GetFileNameWithoutExtension(_suggestedFileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "openclaw-diagnostics";

        var path = Path.Combine(desktop, baseName + ".txt");
        var suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(desktop, $"{baseName}-{suffix++}.txt");
        }

        await File.WriteAllTextAsync(path, _bundleText);
        System.Diagnostics.Debug.WriteLine($"DiagnosticsBundleDialog saved fallback file: {path}");
        return path;
    }

    private sealed record SaveResult(string? Path, string ButtonText);
}
