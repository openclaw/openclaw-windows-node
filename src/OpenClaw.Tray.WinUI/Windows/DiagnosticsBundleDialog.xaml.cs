using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open after Save so the user can also Copy
        // (or save again to a different location). Mirrors OnCopyClick.
        // Previously this method used GetDeferral + ContinueWith but
        // because we unconditionally cancel the close, the deferral was
        // dead code (Hanselman review finding #6).
        args.Cancel = true;
        _ = SaveToFileAsync();
    }

    private async Task SaveToFileAsync()
    {
        try
        {
            // Resolve HWND just-in-time so a closed/recreated host
            // window never lands a stale handle in
            // InitializeWithWindow.Initialize (Hanselman v2 #4).
            var hwnd = _hwndProvider?.Invoke() ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("DiagnosticsBundleDialog save: no host hwnd; skipping picker.");
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                SuggestedFileName = Path.GetFileNameWithoutExtension(_suggestedFileName),
            };
            picker.FileTypeChoices.Add("Text file", new[] { ".txt" });
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await FileIO.WriteTextAsync(file, _bundleText);
                SecondaryButtonText = "Saved";
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.Tick += (_, _) =>
                {
                    SecondaryButtonText = "Save to file";
                    timer.Stop();
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiagnosticsBundleDialog save failed: {ex.Message}");
        }
    }
}
