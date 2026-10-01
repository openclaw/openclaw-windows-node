using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Runtime.InteropServices;

namespace OpenClawTray.Pages;

public sealed partial class ClawHubPage : Page
{
    private static readonly Uri ClawHubUri = new("https://clawhub.ai/");
    private bool _initialized;
    private bool _eventsAttached;
    private string? _installBridgeScript;
    private readonly HashSet<ulong> _interceptedNavigationIds = [];

    public ClawHubPage()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            InitializeAsync,
            new AppLogger(),
            nameof(OnLoaded));

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachEvents();
    }

    private async Task InitializeAsync()
    {
        ShowLoading();
        try
        {
            if (!_initialized)
            {
                await ClawHubWebView.EnsureCoreWebView2Async();
                ConfigureWebView(ClawHubWebView.CoreWebView2);
                var scriptPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets",
                    "ClawHub",
                    "ClawHubInstallBridge.js");
                _installBridgeScript = await File.ReadAllTextAsync(scriptPath);
                await ClawHubWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    _installBridgeScript);
                _initialized = true;
            }

            AttachEvents();
            ClawHubWebView.CoreWebView2.Navigate(ClawHubUri.AbsoluteUri);
        }
        catch (FileNotFoundException exception)
        {
            ShowError($"The ClawHub integration script is missing. {exception.Message}");
        }
        catch (COMException exception)
        {
            ShowError(
                $"The Microsoft Edge WebView2 Runtime is required to browse ClawHub. {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            ShowError($"ClawHub could not start. {exception.Message}");
        }
    }

    private static void ConfigureWebView(CoreWebView2 core)
    {
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
    }

    private void AttachEvents()
    {
        if (_eventsAttached)
            return;

        ClawHubWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
        ClawHubWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        ClawHubWebView.CoreWebView2.ProcessFailed += OnProcessFailed;
        _eventsAttached = true;
    }

    private void DetachEvents()
    {
        if (!_eventsAttached || ClawHubWebView.CoreWebView2 is null)
            return;

        ClawHubWebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
        ClawHubWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        ClawHubWebView.CoreWebView2.ProcessFailed -= OnProcessFailed;
        _eventsAttached = false;
    }

    private void OnNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => HandleNavigationStartingAsync(args),
            new AppLogger(),
            nameof(OnNavigationStarting));

    private async Task HandleNavigationStartingAsync(
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, AppIdentity.ProtocolScheme, StringComparison.OrdinalIgnoreCase))
        {
            ShowLoading();
            return;
        }

        args.Cancel = true;
        _interceptedNavigationIds.Add(args.NavigationId);
        try
        {
            var handled = await ((App)Application.Current).DispatchProtocolUriAsync(args.Uri);
            if (!handled)
                ShowError("Windows Hub could not understand the ClawHub install link.");
        }
        catch (Exception exception)
        {
            Logger.Error($"ClawHub deep link dispatch failed: {exception.Message}");
            ShowError($"Windows Hub could not open the ClawHub install link. {exception.Message}");
        }
    }

    private void OnNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => HandleNavigationCompletedAsync(args),
            new AppLogger(),
            nameof(OnNavigationCompleted));

    private async Task HandleNavigationCompletedAsync(
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_interceptedNavigationIds.Remove(args.NavigationId))
            return;

        if (args.IsSuccess)
        {
            if (!string.IsNullOrEmpty(_installBridgeScript))
            {
                try
                {
                    await ClawHubWebView.CoreWebView2.ExecuteScriptAsync(_installBridgeScript);
                }
                catch (COMException exception)
                {
                    Logger.Error($"ClawHub install bridge injection failed: {exception.Message}");
                    ShowError(
                        "ClawHub loaded, but the Windows install integration could not start. " +
                        "Try again to reload it.");
                    return;
                }
            }

            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowError(args.WebErrorStatus switch
        {
            CoreWebView2WebErrorStatus.CannotConnect or
            CoreWebView2WebErrorStatus.ConnectionReset or
            CoreWebView2WebErrorStatus.ServerUnreachable or
            CoreWebView2WebErrorStatus.Timeout =>
                "ClawHub could not be reached. Check your network connection and try again.",
            _ => $"ClawHub failed to load ({args.WebErrorStatus})."
        });
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        Logger.Warn($"ClawHub WebView2 process failed: {args.ProcessFailedKind}");
        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            RecreateWebView();
        ShowError("The embedded ClawHub browser stopped unexpectedly. Try again to restart it.");
    }

    private void RecreateWebView()
    {
        DetachEvents();
        ClawHubWebView.Close();
        WebViewHost.Children.Clear();
        ClawHubWebView = new WebView2();
        AutomationProperties.SetAutomationId(ClawHubWebView, "ClawHubWebView");
        WebViewHost.Children.Add(ClawHubWebView);
        _initialized = false;
        _interceptedNavigationIds.Clear();
    }

    private void OnRetry(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            InitializeAsync,
            new AppLogger(),
            nameof(OnRetry));

    private void ShowLoading()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
    }

    private void ShowError(string message)
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
