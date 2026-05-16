using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.GatewayWizard;
using OpenClawTray.Services;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Hosting;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using WinUIEx;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Host window for the functional UI onboarding wizard.
/// Supports visual test capture via OPENCLAW_VISUAL_TEST env var.
/// </summary>
public sealed class OnboardingWindow : WindowEx
{
    private OpenClawTray.Onboarding.V2.OnboardingV2State? _v2State;
    private OpenClawTray.Onboarding.V2.OnboardingV2Bridge? _v2Bridge;

    public event EventHandler? OnboardingCompleted;
    public bool Completed { get; private set; }

    private readonly SettingsManager _settings;
    private readonly FunctionalHostControl _host;
    private readonly string? _visualTestDir;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly string? _identityDataPath;
    private int _captureIndex;

    private readonly Grid _rootGrid;
    private readonly GatewayWizardState _gatewayWizardState;
    private bool _gatewayWizardStateDisposed;
    private EventHandler? _v2StateCaptureHandler;
    // Single-fire guard so the X button (Closed) and the Finish button don't both
    // dispatch completion. Both paths
    // route through TryCompleteOnboarding which no-ops after the first call.
    private bool _completionDispatched;
    private bool _incompleteSetupDialogOpen;

    public OnboardingWindow(SettingsManager settings, string? identityDataPath = null)
    {
        _settings = settings;
        _identityDataPath = identityDataPath;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _visualTestDir = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1"
            ? ValidateTestDir(Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
              ?? Path.Combine(Path.GetTempPath(), "openclaw-visual-test"))
            : null;

        // Optional override for visual tests: render the onboarding UI in a specific locale
        // (e.g. "fr-FR", "zh-CN") regardless of system language. Must be set BEFORE the first
        // LocalizationHelper.GetString call so the resource context picks it up.
        var testLocale = Environment.GetEnvironmentVariable("OPENCLAW_TEST_LOCALE");
        if (!string.IsNullOrWhiteSpace(testLocale))
        {
            LocalizationHelper.SetLanguageOverride(testLocale);
        }

        Title = LocalizationHelper.GetString("Onboarding_Title");
        ExtendsContentIntoTitleBar = true;
        this.SetWindowSize(720, 900);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        _gatewayWizardState = new GatewayWizardState(settings);

        if (identityDataPath != null)
        {
            _gatewayWizardState.ExistingConfigGuard = new OnboardingExistingConfigGuard(settings, identityDataPath);
        }

        _host = new FunctionalHostControl();
        // Mount the V2 onboarding component tree. The bridge below wires
        // engine + permission-checker + settings into the V2 state object
        // so the new UI renders against real data without touching any
        // service code.
        _v2State = new OpenClawTray.Onboarding.V2.OnboardingV2State();
        _v2State.GatewayWizardChildFactory = () =>
            Factories.Component<GatewayWizardPage, GatewayWizardState>(_gatewayWizardState);

        // Optional override for visual tests / engineering: jump straight to a V2 route.
        var startRoute = Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_ROUTE");
        if (!string.IsNullOrWhiteSpace(startRoute)
            && Enum.TryParse<OpenClawTray.Onboarding.V2.V2Route>(startRoute, ignoreCase: true, out var parsedRoute))
        {
            _v2State.CurrentRoute = parsedRoute;
        }

        if (_gatewayWizardState.ExistingConfigGuard is { } guard)
        {
            var summary = guard.GetSummary();
            _v2State.ExistingConfig = new OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingConfigSnapshot(
                HasAny: summary.HasAny,
                HasToken: summary.HasToken,
                HasBootstrapToken: summary.HasBootstrapToken,
                HasOperatorDeviceToken: summary.HasOperatorDeviceToken,
                HasNodeDeviceToken: summary.HasNodeDeviceToken,
                HasNonDefaultGatewayUrl: summary.HasNonDefaultGatewayUrl);
        }

        SeedExistingGatewayClassification(_v2State);

        // Route V2Strings through the existing LocalizationHelper so V2
        // text comes from the same .resw resources as legacy strings.
        // Falls back to V2Strings.DefaultEnUs when a key is missing or
        // the resource resolver returns the key itself (treated as miss).
        OpenClawTray.Onboarding.V2.V2Strings.Resolver = LocalizationHelper.GetString;

        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(_v2State);
            return Factories.Component<
                OpenClawTray.Onboarding.V2.OnboardingV2App,
                OpenClawTray.Onboarding.V2.OnboardingV2State>(s);
        });

        CreateAndStartV2Bridge(settings);

        // Root grid: titlebar row + content area
        _rootGrid = new Grid
        {
            Background = GetThemeBrush("SolidBackgroundFillColorBaseBrush")
        };
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Custom title bar — matches HubWindow treatment
        var titleBar = new Grid { Padding = new Thickness(16, 0, 140, 0) };
        var titleIcon = new TextBlock
        {
            Text = "🦞",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var titleText = new TextBlock
        {
            Text = LocalizationHelper.GetString("Onboarding_Title"),
            FontSize = 13,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(titleIcon);
        titleStack.Children.Add(titleText);
        titleBar.Children.Add(titleStack);
        Grid.SetRow(titleBar, 0);
        _rootGrid.Children.Add(titleBar);
        SetTitleBar(titleBar);

        // Content area
        var contentGrid = new Grid();
        contentGrid.Children.Add(_host);
        Grid.SetRow(contentGrid, 1);
        _rootGrid.Children.Add(contentGrid);
        Content = _rootGrid;
        Closed += OnClosed;

        // Auto-capture in visual test mode
        if (_visualTestDir != null)
        {
            Directory.CreateDirectory(_visualTestDir);

            _host.Loaded += (_, _) =>
            {
                DispatcherQueue.GetForCurrentThread().TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () => _ = CaptureCurrentPageAsync());
            };

            Task.Delay(1500).ContinueWith(_ =>
                _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                TaskScheduler.Default);
            Task.Delay(5000).ContinueWith(_ =>
                _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                TaskScheduler.Default);

            _v2StateCaptureHandler = (_, _) =>
            {
                Task.Delay(500).ContinueWith(_ =>
                    _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                    TaskScheduler.Default);
            };
            _v2State.StateChanged += _v2StateCaptureHandler;
        }
    }

    private static Brush GetThemeBrush(string resourceKey)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var resource) == true &&
            resource is Brush brush)
        {
            return brush;
        }

        throw new InvalidOperationException($"Brush resource '{resourceKey}' was not found.");
    }

    /// <summary>
    /// Build a fresh <see cref="OpenClawTray.Onboarding.V2.OnboardingV2Bridge"/>
    /// against the current <see cref="_v2State"/>, wire its host-facing
    /// events, and start it. Idempotent: disposes a prior bridge first.
    /// </summary>
    private void CreateAndStartV2Bridge(SettingsManager settings)
    {
        if (_v2State is null) return;

        try { _v2Bridge?.Dispose(); } catch { /* ignore */ }

        _v2Bridge = new OpenClawTray.Onboarding.V2.OnboardingV2Bridge(
            state: _v2State,
            settings: settings,
            dispatcher: _dispatcherQueue,
            engineFactory: replaceConfirmed =>
                ((App)Application.Current).CreateLocalGatewaySetupEngine(replaceConfirmed),
            hasExistingConfiguration: () => _gatewayWizardState.ExistingConfigGuard?.HasExistingConfiguration() == true,
            seedGatewayWizardClient: client => _gatewayWizardState.GatewayClient = client,
            freshLocalGatewayUninstall: RunFreshLocalGatewayUninstallAsync);
        _v2Bridge.PrimarySetupRequested += (_, _) => _ = ConfirmAndStartV2SetupAsync();
        _v2Bridge.AdvancedSetupRequested += (_, _) => OpenConnectionsFromAdvancedSetup();
        _v2Bridge.Finished += (_, _) =>
        {
            if (TryCompleteOnboarding())
            {
                Close();
            }
        };
        _v2Bridge.Dismissed += (_, _) =>
        {
            // V2 Welcome's "Keep my setup" — user has existing configuration
            // and wants to keep it. Close the window without firing the
            // completion pipeline so existing settings + gateway connection
            // are preserved untouched.
            Logger.Info("[OnboardingWindow] V2 Dismissed — closing without completing");
            _dismissedWithoutCompletion = true;
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                _dismissedWithoutCompletion = false;
                Logger.Warn($"[OnboardingWindow] V2 Dismissed Close() failed: {ex.Message}");
            }
        };
        _v2Bridge.Start();
    }

    /// <summary>
    /// Captures the current window content to a PNG file.
    /// Called automatically on page navigation when OPENCLAW_VISUAL_TEST=1.
    /// </summary>
    public async Task CaptureCurrentPageAsync()
    {
        if (_visualTestDir == null) return;
        try
        {
            await Task.Delay(300);

            var fileName = $"page-{_captureIndex:D2}.png";
            var filePath = Path.Combine(_visualTestDir, fileName);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_rootGrid);
            var pixels = await rtb.GetPixelsAsync();
            var pixelBytes = pixels.ToArray();

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight,
                96, 96, pixelBytes);
            await encoder.FlushAsync();

            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(filePath, bytes);

            Logger.Info($"[VisualTest] Captured {fileName} ({rtb.PixelWidth}x{rtb.PixelHeight})");
            _captureIndex++;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VisualTest] Capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Set when the user explicitly exits setup without completing it.
    /// <see cref="OnClosed"/> consults this to skip the completion pipeline
    /// so existing settings and gateway connection are preserved untouched.
    /// </summary>
    private bool _dismissedWithoutCompletion;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // X button path: also runs TryCompleteOnboarding (idempotent via _completionDispatched)
        // so a user who clicks the title-bar X on the Ready page still gets the chat-window
        // launch when a model has been configured, matching the Finish-button behavior.
        //
        // Skipped entirely when the user explicitly dismissed via "Keep my setup" — that
        // path must NOT mark onboarding complete, must NOT fire OnboardingCompleted, and
        // must NOT touch settings/AutoStart so the prior gateway connection is preserved.
        if (!_dismissedWithoutCompletion)
        {
            _ = TryCompleteOnboarding();
        }

        try { _v2Bridge?.Dispose(); } catch { /* ignore */ }
        _v2Bridge = null;

        if (_v2State is not null && _v2StateCaptureHandler is not null)
        {
            _v2State.StateChanged -= _v2StateCaptureHandler;
            _v2StateCaptureHandler = null;
        }

        if (_gatewayWizardStateDisposed) return;
        _gatewayWizardStateDisposed = true;
        if (Completed)
        {
            _gatewayWizardState.GatewayClient = null;
        }
        _gatewayWizardState.Dispose();
    }

    private void SeedExistingGatewayClassification(OpenClawTray.Onboarding.V2.OnboardingV2State v2State)
    {
        var dataPath = _identityDataPath ?? SettingsManager.SettingsDirectoryPath;
        var app = Application.Current as App;
        var registry = app?.Registry;
        v2State.ExistingGateway = ToV2ExistingGatewayKind(
            ApplyVisualExistingGatewayOverride(
                SetupExistingGatewayClassifier.ClassifyWithoutWslProbe(registry, _settings, dataPath)));

        _ = Task.Run(async () =>
        {
            var classified = await SetupExistingGatewayClassifier
                .ClassifyAsync(registry, _settings, dataPath)
                .ConfigureAwait(false);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_v2State is null) return;
                _v2State.ExistingGateway = ToV2ExistingGatewayKind(ApplyVisualExistingGatewayOverride(classified));
            });
        });
    }

    private static SetupExistingGatewayKind ApplyVisualExistingGatewayOverride(SetupExistingGatewayKind detected)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1"
            && Enum.TryParse<SetupExistingGatewayKind>(
                Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_EXISTING_GATEWAY_KIND"),
                ignoreCase: true,
                out var overrideKind))
        {
            return overrideKind;
        }

        return detected;
    }

    private static OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind ToV2ExistingGatewayKind(
        SetupExistingGatewayKind kind)
    {
        return kind switch
        {
            SetupExistingGatewayKind.AppOwnedLocalWsl => OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl,
            SetupExistingGatewayKind.ExternalOnly => OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind.ExternalOnly,
            _ => OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind.None,
        };
    }

    private async Task ConfirmAndStartV2SetupAsync()
    {
        if (_v2State is null) return;

        var kind = await RefreshExistingGatewayClassificationAsync();
        if (kind == OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind.None)
        {
            _v2State.RequestAdvance();
            return;
        }

        var isLocalReplacement = kind == OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl;
        var titleKey = isLocalReplacement
            ? "V2_Welcome_LocalReplaceDialog_Title"
            : "V2_Welcome_ExternalDialog_Title";
        var bodyKey = isLocalReplacement
            ? "V2_Welcome_LocalReplaceDialog_Body"
            : "V2_Welcome_ExternalDialog_Body";

        if (_rootGrid.XamlRoot is null)
        {
            Logger.Warn("[OnboardingWindow] Cannot show setup warning dialog; XamlRoot is unavailable");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = OpenClawTray.Onboarding.V2.V2Strings.Get(titleKey),
            Content = new TextBlock
            {
                Text = OpenClawTray.Onboarding.V2.V2Strings.Get(bodyKey),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = OpenClawTray.Onboarding.V2.V2Strings.Get("V2_Welcome_SetupWarning_Confirm"),
            CloseButtonText = OpenClawTray.Onboarding.V2.V2Strings.Get("V2_Welcome_SetupWarning_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _rootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _v2State.ReplaceExistingConfigurationConfirmed = true;
        _v2State.RequestAdvance();
    }

    private Task<LocalGatewayUninstallResult> RunFreshLocalGatewayUninstallAsync(CancellationToken cancellationToken)
    {
        var app = (App)Application.Current;
        var uninstall = LocalGatewayUninstall.Build(
            _settings,
            logger: new AppLogger(),
            identityDataPath: _identityDataPath ?? SettingsManager.SettingsDirectoryPath,
            registry: app.Registry);

        return uninstall.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveExecPolicy = true,
            PreserveLogs = true,
            PreserveRootDeviceTokensWhenExternalGatewaysExist = true
        }, cancellationToken);
    }

    private async Task<OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind> RefreshExistingGatewayClassificationAsync()
    {
        if (_v2State is null)
        {
            return OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingGatewayKind.None;
        }

        var dataPath = _identityDataPath ?? SettingsManager.SettingsDirectoryPath;
        var registry = (Application.Current as App)?.Registry;
        var classified = await SetupExistingGatewayClassifier
            .ClassifyAsync(registry, _settings, dataPath)
            .ConfigureAwait(true);

        var kind = ToV2ExistingGatewayKind(ApplyVisualExistingGatewayOverride(classified));
        _v2State.ExistingGateway = kind;
        return kind;
    }

    /// <summary>
    /// Called when V2 Welcome's Advanced setup link fires. Setup is now only
    /// for installing a new local WSL gateway, so Advanced exits setup and
    /// opens the tray app's Connections page for existing/remote gateways.
    /// </summary>
    private void OpenConnectionsFromAdvancedSetup()
    {
        Logger.Info("[OnboardingWindow] V2 Advanced requested — closing setup and opening Connections");
        _dismissedWithoutCompletion = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _dismissedWithoutCompletion = false;
            Logger.Warn($"[OnboardingWindow] Failed to close setup for Advanced: {ex.Message}");
        }

        try
        {
            if (Application.Current is App app)
            {
                app.ShowHub("connection");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[OnboardingWindow] Failed to open Connections for Advanced setup: {ex.Message}");
        }
    }

    /// <summary>
    /// Unified completion handler invoked from both the Finish button and the
    /// title-bar X button. Idempotent — guarded by <see cref="_completionDispatched"/>.
    ///
    /// If the user is closing from the All Set page and setup no longer requires
    /// credentials, launches the main tray hub window on the chat tab.
    /// This intentionally does not depend on WizardLifecycleState == "complete": the
    /// gateway wizard can stop on a later channel step even after credentials/model
    /// setup succeeded, but Finish on All Set still runs this handler.
    /// </summary>
    private bool TryCompleteOnboarding()
    {
        if (_completionDispatched) return true;
        var finishedFromTerminalPage = _v2State?.CurrentRoute == OpenClawTray.Onboarding.V2.V2Route.AllSet;
        var dataPath = _identityDataPath ?? SettingsManager.SettingsDirectoryPath;
        var setupStillRequired = StartupSetupState.RequiresSetup(
            _settings,
            dataPath,
            (Application.Current as App)?.Registry);
        if (OnboardingCompletionPolicy.Decide(finishedFromTerminalPage, setupStillRequired) == OnboardingCompletionOutcome.BlockIncompleteReady)
        {
            Logger.Warn("[OnboardingWindow] Finish blocked because setup is still required; keeping onboarding open");
            return false;
        }

        _completionDispatched = true;

        _settings.Save();
        Completed = true;
        _gatewayWizardState.GatewayClient = null;

        // Materialize the persisted AutoStart preference into the OS-level Run-key.
        // ReadyPage applies the toggle on each change, but a user who never touches
        // it should still get the default (true) registered. Idempotent.
        try
        {
            AutoStartManager.SetAutoStart(_settings.AutoStart);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Onboarding] Failed to apply AutoStart={_settings.AutoStart}: {ex.Message}");
        }

        OnboardingCompleted?.Invoke(this, EventArgs.Empty);

        if (finishedFromTerminalPage && !setupStillRequired)
        {
            Logger.Info("[OnboardingWindow] TryCompleteOnboarding launching HubWindow on chat tab");
            ShowHubChatAfterWizardClose();
        }
        else
        {
            Logger.Info($"[OnboardingWindow] TryCompleteOnboarding skipping chat launch; route={_v2State?.CurrentRoute}, setupStillRequired={setupStillRequired}");
        }

        return true;
    }

    private async Task ShowIncompleteSetupDialogAsync()
    {
        if (_incompleteSetupDialogOpen) return;
        _incompleteSetupDialogOpen = true;
        try
        {
            if (_rootGrid.XamlRoot is null)
            {
                Logger.Warn("[OnboardingWindow] Cannot show incomplete setup dialog; XamlRoot is unavailable");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("Onboarding_IncompleteSetup_Title"),
                Content = new TextBlock
                {
                    Text = LocalizationHelper.GetString("Onboarding_IncompleteSetup_Body"),
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = LocalizationHelper.GetString("Onboarding_IncompleteSetup_Close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _rootGrid.XamlRoot
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[OnboardingWindow] Failed to show incomplete setup dialog: {ex.Message}");
        }
        finally
        {
            _incompleteSetupDialogOpen = false;
        }
    }

    private void ShowHubChatAfterWizardClose()
    {
        void ShowHubChat()
        {
            try
            {
                var app = Microsoft.UI.Xaml.Application.Current as App;
                if (app == null)
                {
                    Logger.Warn("[OnboardingWindow] ShowHub chat after Finish failed: App unavailable");
                    return;
                }

                app.ShowHub("chat");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OnboardingWindow] ShowHub chat after Finish failed: {ex.Message}");
            }
        }

        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowHubChat))
        {
            ShowHubChat();
        }
    }

    /// <summary>
    /// SECURITY: Validate visual test directory path to prevent directory traversal.
    /// Returns null if the path is suspicious.
    /// </summary>
    private static string? ValidateTestDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.Contains('\0')) return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            // Ensure it doesn't escape via .. traversal to unexpected locations
            if (fullPath.Contains("..")) return null;
            return fullPath;
        }
        catch
        {
            return null;
        }
    }
}
