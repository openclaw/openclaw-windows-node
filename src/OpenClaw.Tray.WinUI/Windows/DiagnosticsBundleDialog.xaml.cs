using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
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
    /// IntPtr.Zero and the picker reports a failure instead of crashing
    /// on a stale handle or writing diagnostics somewhere unexpected.
    /// </summary>
    public void Configure(string bundleText, string headerCaption, string suggestedFileName, Func<IntPtr> hwndProvider)
    {
        _bundleText = bundleText ?? string.Empty;
        _suggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? "openclaw-diagnostics.txt"
            : suggestedFileName;
        _hwndProvider = hwndProvider;
        BundleHeaderText.Text = headerCaption ?? string.Empty;
        SetBundleText(_bundleText, isReady: true);
    }

    public void SetBundleText(string bundleText, bool isReady)
    {
        _bundleText = bundleText ?? string.Empty;
        BundleText.Text = _bundleText;
        IsPrimaryButtonEnabled = isReady;
        IsSecondaryButtonEnabled = isReady;
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

    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open after Save so the user can also Copy
        // (or save again to a different location). Mirrors OnCopyClick.
        // Use a deferral so picker/write failures can update the button
        // instead of vanishing in a fire-and-forget task.
        args.Cancel = true;
        var deferral = args.GetDeferral();
        AsyncEventHandlerGuard.Run(
            async () =>
            {
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
            },
            new OpenClawTray.AppLogger(),
            nameof(OnSaveClick));
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
                Logger.Warn("DiagnosticsBundleDialog save skipped: no host hwnd available for save picker.");
                return new SaveResult(null, "Save failed");
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
            Logger.Error($"DiagnosticsBundleDialog save failed: {ex}");
            return new SaveResult(null, "Save failed");
        }
    }

    private sealed record SaveResult(string? Path, string ButtonText);
}
