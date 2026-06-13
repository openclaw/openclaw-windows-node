using OpenClaw.Chat;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Chat.Explorations;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class ChatWindow : WindowEx
{
    private string _gatewayUrl;
    private string _token;
    private string? _chatUrl;
    private MountedFunctionalChat? _functionalHost;
    private IChatDataProvider? _mountedProvider;
    private bool _webViewInitialized;
    private bool _webViewMode;
    public bool IsClosed { get; private set; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int val, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT2 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT2 rcMonitor;
        public RECT2 rcWork;
        public int dwFlags;
    }

    public ChatWindow(string gatewayUrl, string token)
    {
        _gatewayUrl = gatewayUrl;
        _token = token;
        _chatUrl = ChatSurfaceResolver.BuildChatUrl(gatewayUrl, token);
        InitializeComponent();

        this.SetWindowSize(DefaultChatWidth, DefaultChatHeight);
        this.SetIcon("Assets\\openclaw.ico");

        // Set as tool window (hidden from taskbar) + remove system caption.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        var wStyle = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, wStyle & ~WS_CAPTION & ~WS_THICKFRAME);

        // Rounded corners (Windows 11)
        var cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // Auto-hide when clicking outside the panel
        Activated += OnWindowActivated;

        // Hide instead of close — preserves native chat state for instant reopen
        Closed += OnWindowClosing;

        // a11y: Esc to hide the popup + try to focus composer on first show.
        // KeyboardAccelerator on the root content gets first-class keyboard
        // handling without needing a focus host.
        if (this.Content is FrameworkElement contentRoot)
        {
            var escAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = global::Windows.System.VirtualKey.Escape,
            };
            escAccel.Invoked += (_, args) =>
            {
                args.Handled = true;
                this.Hide();
            };
            contentRoot.KeyboardAccelerators.Add(escAccel);
            // Suppress the default "Esc" tooltip that WinUI shows for
            // keyboard accelerators — it lingers as a floating orphan
            // when the user scrolls the chat timeline.
            contentRoot.KeyboardAcceleratorPlacementMode =
                Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        }

        // Subscribe to global SettingsChanged so the surface swaps when the
        // user toggles "Use standard Gateway Chat interface" while the
        // pre-warmed window is alive.
        if (App.Current is App app)
        {
            app.SettingsChanged += OnAppSettingsChanged;
            app.ChatProviderChanged += OnAppChatProviderChanged;
            app.SpeakerMuteChanged += OnSpeakerMuteChanged;
        }

        // Per-surface debug override (DebugPage > "Debug Overrides").
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed += OnDebugOverrideChanged;

        // ChatExploration backdrop / theme — re-apply when toggled live.
        ChatExplorationState.Changed -= OnExplorationChanged;
        ChatExplorationState.Changed += OnExplorationChanged;

        ApplyChatSurface();
        ApplySystemBackdrop();
        ApplyPreviewTheme();
    }

    private const int DefaultChatWidth = 480;
    private const int DefaultChatHeight = 640;

    private void OnAppSettingsChanged(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnSpeakerMuteChanged(bool muted)
    {
        DispatcherQueue?.TryEnqueue(() => _functionalHost?.SetSpeakerMuted(muted));
    }

    private void OnAppChatProviderChanged(object? sender, EventArgs e)
    {
        if (IsClosed) return;

        var dispatcher = DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            ApplyChatSurface();
            return;
        }

        _ = dispatcher.TryEnqueue(ApplyChatSurface);
    }

    private void OnDebugOverrideChanged(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnExplorationChanged(object? sender, EventArgs e)
    {
        // Marshal back to UI thread — Changed may fire from anywhere.
        DispatcherQueue?.TryEnqueue(() =>
        {
            ApplySystemBackdrop();
            ApplyPreviewTheme();
        });
    }

    /// <summary>
    /// Swap <see cref="Window.SystemBackdrop"/> based on
    /// <see cref="ChatExplorationState.BackdropMode"/>. When
    /// <see cref="ChatExplorationState.UsesHostBackdrop"/> is true (embedded
    /// chat scenarios), leave whatever the host already set in place.
    /// </summary>
    private void ApplySystemBackdrop()
    {
        if (ChatExplorationState.UsesHostBackdrop) return;

        SystemBackdrop = ChatExplorationState.BackdropMode switch
        {
            ChatBackdropMode.Mica     => new MicaBackdrop { Kind = MicaKind.Base },
            ChatBackdropMode.MicaAlt  => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            ChatBackdropMode.Acrylic  => new DesktopAcrylicBackdrop(),
            _ /* Solid */             => null!,
        };
    }

    private void ApplyPreviewTheme()
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = ChatVisualResolver.ResolvePreviewTheme();
    }

    private void ApplyChatSurface()
    {
        var setting = (App.Current as App)?.Settings?.UseLegacyWebChat ?? false;
        var decision = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.TrayChat,
            setting,
            _chatUrl,
            ChatSurfaceResolver.BuildChatUrl(_gatewayUrl, _token));

        _chatUrl = decision.ChatUrl;

        if (decision.UseLegacyWebChat)
            ShowWebViewSurface();
        else
            ShowFunctionalSurface();
    }

    private void ShowFunctionalSurface()
    {
        _webViewMode = false;
        StopWebViewNavigation();
        WebView.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        TryMountFunctionalChat();
    }

    private void ShowWebViewSurface()
    {
        _webViewMode = true;

        // Tear down native chat so the WebView2 owns the row.
        DisposeFunctionalHost();

        ChatHost.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;

        if (_webViewInitialized)
        {
            if (!NavigateWebViewToCurrentChatUrl())
                ShowMissingChatCredentialError();
            return;
        }

        if (string.IsNullOrEmpty(_chatUrl))
        {
            ShowMissingChatCredentialError();
            return;
        }

        _ = InitializeWebViewAsync();
    }

    private bool NavigateWebViewToCurrentChatUrl()
    {
        if (string.IsNullOrEmpty(_chatUrl) || WebView.CoreWebView2 is null)
            return false;

        ErrorPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;
        WebView.CoreWebView2.Navigate(_chatUrl);
        return true;
    }

    private void ShowMissingChatCredentialError()
    {
        StopWebViewNavigation();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = LocalizationHelper.GetString("ChatWindow_UnableToLoadChatCredentials");
    }

    private void StopWebViewNavigation()
    {
        try
        {
            WebView.CoreWebView2?.Stop();
            WebView.CoreWebView2?.Navigate("about:blank");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ChatWindow WebView stop failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-resolve the gateway URL and token, and reload the WebView2 if either changed.
    /// Bug 2 fix: ChatWindow caches credentials at construction. When the pre-warmed window
    /// is created before pairing completes, its cached token is empty/stale. App calls this
    /// before re-activating the cached window so the freshest credentials are used.
    /// </summary>
    public void RefreshCredentials(string gatewayUrl, string token)
    {
        gatewayUrl ??= string.Empty;
        token ??= string.Empty;

        _gatewayUrl = gatewayUrl;
        _token = token;
        _chatUrl = ChatSurfaceResolver.BuildChatUrl(_gatewayUrl, _token);

        // HIGH 4: never log the full chat URL — its query string contains the
        // auth token. Strip the query before logging.
        Logger.Info($"[ChatWindow] Refreshing to {SafeLogUrl(_chatUrl)}");

        // If WebView2 is already up, navigate it to the refreshed URL so the user gets a
        // working chat instead of the pre-warmed (auth-failed) view.
        // BUT only when we're actively in webview mode — otherwise this would
        // un-hide the WebView on top of the active native surface (e.g. when
        // the Debug Overrides force the Companion Chat UI on the Tray popup).
        if (_webViewMode && _webViewInitialized && WebView?.CoreWebView2 != null)
        {
            if (string.IsNullOrEmpty(_chatUrl))
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = LocalizationHelper.GetString("ChatWindow_UnableToLoadChatCredentials");
                return;
            }

            try
            {
                ErrorPanel.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                WebView.CoreWebView2.Navigate(_chatUrl);
            }
            catch (Exception ex)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = LocalizationHelper.Format("ChatWindow_UnableToLoadChatRetryFormat", ex.Message);
                Logger.Warn($"ChatWindow.RefreshCredentials navigate failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Exposes a script executor wrapping CoreWebView2.ExecuteScriptAsync without
    /// leaking the WebView2 control field. Returns null if CoreWebView2 is not ready.
    /// </summary>
    public Func<string, Task<string>>? TryGetScriptExecutor()
    {
        if (!_webViewInitialized || WebView?.CoreWebView2 == null)
        {
            return null;
        }
        var core = WebView.CoreWebView2;
        return script => core.ExecuteScriptAsync(script).AsTask();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            await GatewayChatHelper.InitializeWebView2Async(WebView);
            _webViewInitialized = true;

            WebView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess)
                {
                    WebView.Visibility = Visibility.Collapsed;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorText.Text = e.WebErrorStatus switch
                    {
                        CoreWebView2WebErrorStatus.CannotConnect or
                        CoreWebView2WebErrorStatus.ConnectionReset or
                        CoreWebView2WebErrorStatus.ServerUnreachable or
                        CoreWebView2WebErrorStatus.Timeout =>
                            "The gateway is not reachable. Check that it is running and try again.",
                        _ => $"Unable to load chat. Please try again. ({e.WebErrorStatus})"
                    };
                }
                else
                {
                    ErrorPanel.Visibility = Visibility.Collapsed;
                    WebView.Visibility = Visibility.Visible;
                    RequestChatInputFocus();
                }
            };

            WebView.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(_chatUrl))
                WebView.CoreWebView2.Navigate(_chatUrl);
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = LocalizationHelper.Format("ChatWindow_WebViewFailedFormat", ex.Message);
        }
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized && !string.IsNullOrEmpty(_chatUrl))
            WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized) WebView.CoreWebView2?.Reload();
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (!_webViewInitialized || string.IsNullOrEmpty(_chatUrl)) return;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Visible;
        WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void TryMountFunctionalChat()
    {
        var app = App.Current as App;
        var provider = app?.ChatProvider;
        Func<string, Task>? readAloud = app is null ? null : app.SpeakChatTextAsync;

        if (_functionalHost is not null && ReferenceEquals(_mountedProvider, provider))
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ChatHost.Visibility = Visibility.Visible;
            return;
        }

        DisposeFunctionalHost();

        if (provider is null)
        {
            PlaceholderPanel.Visibility = Visibility.Visible;
            ChatHost.Visibility = Visibility.Collapsed;
            return;
        }

        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ChatHost.Visibility = Visibility.Visible;
        var appInstance = App.Current as App;
        _functionalHost = ((Window)this).MountFunctionalChat(
            ChatHost,
            provider,
            onReadAloud: readAloud,
            onStopSpeaking: () => appInstance?.StopChatSpeaking(),
            onVoiceRequest: VoiceTranscribeAsync,
            onAttachClick: OnAttachClicked,
            onSettingsClick: () => appInstance?.ShowHub("voice"),
            onSpeakerMuteChanged: muted => appInstance?.SetChatSpeakerMuted(muted),
            initialMuted: appInstance?.Settings?.VoiceTtsEnabled == false,
            isCompact: true);
        _mountedProvider = provider;
    }

    private void DisposeFunctionalHost()
    {
        var host = _functionalHost;
        _functionalHost = null;
        _mountedProvider = null;
        try { host?.Dispose(); }
        catch (Exception ex) { Logger.Debug($"ChatWindow: functional host dispose tear-down race: {ex.Message}"); }
    }

    private void EagerlyLoadChatHistory()
    {
        var provider = (App.Current as App)?.ChatProvider;
        if (provider is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // LoadAsync seeds from cached sessions; the important thing is
                // it triggers the Changed event with the current snapshot.
                var snap = await provider.LoadAsync();
                // If there's a default thread, load its history so the
                // timeline has entries to render on the very first show.
                if (snap.DefaultThreadId is { } threadId)
                    await provider.LoadHistoryAsync(threadId);
            }
            catch (Exception ex) { Logger.Debug($"ChatWindow: eager chat history load failed (mount path will retry): {ex.Message}"); }
        });
    }

    private void OnAttachClicked()
    {
        _ = PickAndAttachFileAsync();
    }

    private async Task<string?> VoiceTranscribeAsync(CancellationToken cancellationToken, Action? onRecordingStarted)
    {
        var app = App.Current as App;
        if (app is null || app.Settings?.NodeSttEnabled != true)
        {
            await ShowVoiceSettingsDialogAsync(
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffTitle"),
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffMessage"),
                () => app?.ShowHub("voice"));
            return null;
        }

        var voiceService = app.VoiceServiceInstance;
        var host = _functionalHost;
        if (voiceService is null)
        {
            await ShowVoiceSettingsDialogAsync(
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffTitle"),
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffMessage"),
                () => app.ShowHub("voice"));
            return null;
        }

        // If the STT model isn't downloaded yet, prompt the user and open voice settings.
        if (!voiceService.IsModelDownloaded)
        {
            await ShowVoiceSettingsDialogAsync(
                LocalizationHelper.GetString("ChatVoiceDialog_ModelRequiredTitle"),
                LocalizationHelper.GetString("ChatVoiceDialog_ModelRequiredMessage"),
                () => app.ShowHub("voice"));
            return null;
        }

        void OnTranscription(string text) => host?.SetVoiceTranscript(text);
        void OnAudioLevel(float level) => host?.SetVoiceAudioLevel(level);

        voiceService.TranscriptionReceived += OnTranscription;
        voiceService.AudioLevelChanged += OnAudioLevel;
        onRecordingStarted?.Invoke();
        try
        {
            var args = new SttListenArgs
            {
                TimeoutMs = 10_000,
                Language = ""
            };
            var result = await voiceService.ListenOnceAsync(args, cancellationToken);
            return result?.Text;
        }
        finally
        {
            voiceService.TranscriptionReceived -= OnTranscription;
            voiceService.AudioLevelChanged -= OnAudioLevel;
            host?.SetVoiceTranscript(null);
            host?.SetVoiceAudioLevel(0f);
        }
    }

    private async Task ShowVoiceSettingsDialogAsync(string title, string message, Action openVoiceSettings)
    {
        var tcs = new TaskCompletionSource();
        if (DispatcherQueue is null || !DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = LocalizationHelper.GetString("ChatVoiceDialog_OpenVoiceSettings"),
                    CloseButtonText = LocalizationHelper.GetString("ChatVoiceDialog_Dismiss"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content?.XamlRoot
                };
                dialog.Opened += (s, _) =>
                {
                    if (s is ContentDialog d)
                    {
                        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(d.XamlRoot))
                        {
                            if (popup.Child is UIElement overlay && overlay != d)
                            {
                                overlay.Tapped += (_, _) => d.Hide();
                                break;
                            }
                        }
                    }
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    openVoiceSettings();
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warn($"Voice settings dialog could not be shown: {ex.Message}");
            }
            finally
            {
                tcs.TrySetResult();
            }
        }))
        {
            return;
        }

        await tcs.Task;
    }

    private async Task PickAndAttachFileAsync()
    {
        var wasPinned = ChatWindowPinState.IsPinned;
        try
        {
            // Pin the window so the light-dismiss handler doesn't hide it
            // when the file picker dialog takes focus.
            ChatWindowPinState.IsPinned = true;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle((Window)this);
            var path = await Win32FilePickerHelper.PickSingleFileAsync(hwnd, "Attach file");
            if (path is null) return;
            Logger.Info($"[ChatWindow] File selected: {path}");

            Logger.Info($"[ChatWindow] File selected: {path}");
            var attachment = await ChatAttachment.FromFileAsync(path);
            _functionalHost?.AttachFile(attachment);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"[ChatWindow] Attachment rejected: {ex.Message}");
            await ShowAttachmentErrorAsync(ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ChatWindow] File picker error: {ex}");
        }
        finally
        {
            ChatWindowPinState.IsPinned = wasPinned;
        }
    }

    private async Task ShowAttachmentErrorAsync(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Cannot attach file",
                Content = message,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content?.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Logger.Debug($"ChatWindow: dialog display failed (already logged upstream): {ex.Message}"); }
    }

    private bool _backdropAppliedOnce;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            // First time the window actually becomes visible/active — re-apply the
            // system backdrop. Setting SystemBackdrop on a pre-warmed (never shown)
            // window doesn't always attach the controller, which is why acrylic
            // appeared blank until the user toggled it from the exploration panel.
            if (!_backdropAppliedOnce)
            {
                _backdropAppliedOnce = true;
                ApplySystemBackdrop();
            }

            // a11y: place keyboard focus on the composer text box so the user
            // can start typing immediately. Defer to next dispatcher pass so
            // FunctionalUI has finished mounting the composer.
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (this.Content is FrameworkElement root && FindFirstFocusableTextBox(root) is { } tb)
                    tb.Focus(FocusState.Programmatic);
            });
            return;
        }

        // Pinned via Chat exploration panel — keep open so the user can
        // preview backdrop/composer changes side-by-side.
        if (ChatWindowPinState.IsPinned) return;
        this.Hide();
    }

    private static Microsoft.UI.Xaml.Controls.TextBox? FindFirstFocusableTextBox(DependencyObject root)
    {
        if (root is Microsoft.UI.Xaml.Controls.TextBox tb && tb.IsEnabled && tb.Visibility == Visibility.Visible)
            return tb;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (FindFirstFocusableTextBox(child) is { } found) return found;
        }
        return null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }

    /// <summary>Position near the system tray and show with animation.</summary>
    public void ShowNearTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        GetCursorPos(out POINT pt);

        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;

        int panelWPx = (int)(DefaultChatWidth * scale);
        int panelHPx = (int)(DefaultChatHeight * scale);

        int margin = 8;
        int x = work.Right - panelWPx - margin;
        int y = work.Bottom - panelHPx - margin;

        // Position and size atomically while still hidden to avoid flicker.
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, panelWPx, panelHPx, SWP_NOZORDER | SWP_NOACTIVATE);

        // Provider may have arrived after construction — re-apply surface so
        // a native-mode window swaps placeholder → live tree on first show.
        ApplyChatSurface();

        // Eagerly load chat history so the tray popup renders messages
        // immediately instead of showing the zero-state while history loads.
        EagerlyLoadChatHistory();

        this.Show();
        SetForegroundWindow(hwnd);
        RequestChatInputFocus();
    }

    /// <summary>Show near tray. Native chat renders synchronously so no animation gating needed.</summary>
    public void ShowNearTrayAnimated() => ShowNearTray();

    private void OnWindowClosing(object sender, WindowEventArgs args)
    {
        // Intercept close → hide instead (keeps native chat state warm).
        args.Handled = true;
        this.Hide();
    }

    /// <summary>Actually close and dispose (called on app shutdown).</summary>
    public void ForceClose()
    {
        Closed -= OnWindowClosing;
        if (App.Current is App app)
        {
            app.SettingsChanged -= OnAppSettingsChanged;
            app.ChatProviderChanged -= OnAppChatProviderChanged;
            app.SpeakerMuteChanged -= OnSpeakerMuteChanged;
        }
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
        ChatExplorationState.Changed -= OnExplorationChanged;
        IsClosed = true;
        DisposeFunctionalHost();
        Close();
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        // Open in Companion app — route to the Hub window's chat tab so the
        // full companion experience is available, then dismiss the tray popup.
        try
        {
            (App.Current as App)?.ShowHub("chat");
            this.Hide();
        }
        catch (Exception ex)
        {
            Logger.Warn($"ChatWindow: Failed to pop out chat to hub: {ex.Message}");
        }
    }

    private void RequestChatInputFocus()
    {
        WebView.Focus(FocusState.Programmatic);

        if (!_webViewInitialized || WebView.CoreWebView2 == null)
        {
            return;
        }

        _ = FocusChatInputAsync();
    }

    private async Task FocusChatInputAsync()
    {
        try
        {
            await WebView.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    const selectors = [
                        'textarea:not([disabled])',
                        'input[type="text"]:not([disabled])',
                        'input:not([type]):not([disabled])',
                        '[contenteditable="true"]',
                        '[role="textbox"]'
                    ];
                    const isVisible = element =>
                        !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                    const target = selectors
                        .flatMap(selector => Array.from(document.querySelectorAll(selector)))
                        .find(isVisible);
                    if (!target) {
                        return false;
                    }
                    target.focus({ preventScroll: true });
                    return document.activeElement === target || target.contains(document.activeElement);
                })();
                """);
        }
        catch (ObjectDisposedException ex)
        {
            Logger.Warn($"Failed to focus chat input: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"Failed to focus chat input: {ex.Message}");
        }
        catch (COMException ex)
        {
            Logger.Warn($"Failed to focus chat input: {ex.Message}");
        }
    }

    /// <summary>
    /// Strip the query string (which carries <c>?token=…</c>) from a chat URL
    /// before logging. Returns the bare scheme + authority + path so the host
    /// is still recognisable for diagnostics.
    /// </summary>
    private static string SafeLogUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(empty)";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.GetLeftPart(UriPartial.Path);
        return "(unparseable)";
    }
}
