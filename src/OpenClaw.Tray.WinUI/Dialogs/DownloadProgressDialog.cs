using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using Updatum;

namespace OpenClawTray.Dialogs;

public sealed class DownloadProgressDialog
{
    private Window? _window;
    private readonly UpdatumManager? _updater;

    public DownloadProgressDialog(UpdatumManager updater)
    {
        _updater = updater;
    }

    public void ShowAsync()
    {
        _window = new Window { Title = LocalizationHelper.GetString("WindowTitle_Downloading") };
        _window.SystemBackdrop = new MicaBackdrop();
        
        var panel = new StackPanel { Padding = new Thickness(20) };
        var progressText = new TextBlock { Text = LocalizationHelper.GetString("Download_ProgressText"), Margin = new Thickness(0, 0, 0, 10) };
        var progressBar = new ProgressBar { IsIndeterminate = true };
        
        panel.Children.Add(progressText);
        panel.Children.Add(progressBar);
        _window.Content = panel;
        
        // Size and center the window
        _window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(400, 200));
        var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
        var centerX = (displayArea.WorkArea.Width - 400) / 2;
        var centerY = (displayArea.WorkArea.Height - 200) / 2;
        _window.AppWindow.Move(new global::Windows.Graphics.PointInt32(centerX, centerY));
        
        _window.Activate();
    }

    public void Close() => _window?.Close();
}
