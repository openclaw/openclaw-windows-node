using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Services;

/// <summary>
/// Windows Node service - manages node connection and capabilities
/// </summary>
public sealed class NodeService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<FrameworkElement?> _rootProvider;
    private readonly SettingsManager? _settings;
    private WindowsNodeClient? _nodeClient;
    private CanvasWindow? _canvasWindow;
    // Invariant: _a2uiCanvasWindow is only read/written from the UI dispatcher
    // (DispatcherQueue.TryEnqueue callbacks). Today's WinUI dispatcher serializes,
    // so no memory barrier is needed — but introducing a non-dispatcher caller
    // would silently see stale references. Stay on-thread or marshal.
    private A2UICanvasWindow? _a2uiCanvasWindow;
    private MediaResolver? _mediaResolver;
    private ActionDispatcher? _actionDispatcher;
    private ScreenCaptureService? _screenCaptureService;
    private ScreenRecordingService? _screenRecordingService;
    private CameraCaptureService? _cameraCaptureService;
    private DateTime _lastScreenCaptureNotification = DateTime.MinValue;
    // Concurrent navigates from rapid-fire agent requests can race on the
    // bucket structure of a HashSet. Use ConcurrentDictionary as a thread-safe
    // set; value byte is unused.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _allowedNavigationHosts =
        new(StringComparer.OrdinalIgnoreCase);

    // Navigation-prompt rate-limit / coalescing state. A token-holding agent
    // looping canvas.navigate could otherwise stack arbitrarily many topmost
    // MessageBoxW prompts.
    //   _navigationPromptGate: only one prompt visible at a time across all hosts.
    //   _pendingNavigationPrompts: in-flight prompt per HostKey — a second
    //     request for the same host inherits the user's decision instead of
    //     queueing a duplicate prompt.
    //   _navigationDenyCooldown: HostKey → expiresAt. After a Deny, repeated
    //     requests for the same host auto-deny silently for the cooldown
    //     window so a hostile loop can't keep nagging the user.
    private readonly SemaphoreSlim _navigationPromptGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<UrlNavigationApprovalDecision>> _pendingNavigationPrompts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _navigationDenyCooldown =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NavigationDenyCooldownDuration = TimeSpan.FromSeconds(30);
    
    // Capabilities
    private SystemCapability? _systemCapability;
    private CanvasCapability? _canvasCapability;
    private ScreenCapability? _screenCapability;
    private CameraCapability? _cameraCapability;
    private LocationCapability? _locationCapability;
    private DeviceCapability? _deviceCapability;
    private BrowserProxyCapability? _browserProxyCapability;
    private TtsCapability? _ttsCapability;
    private TextToSpeechService? _textToSpeechService;
    private readonly string _dataPath;
    private string? _token;

    // Authoritative capability list — populated by RegisterCapabilities and
    // shared with both the gateway client (when present) and the MCP bridge.
    // Holding it here lets MCP-only mode skip the gateway client entirely.
    //
    // Mutated on the UI thread (Connect / StartLocalOnly / Disconnect rebuild
    // it); read by the MCP bridge on threadpool threads (every tools/list and
    // tools/call). Every read/write goes through _capabilitiesLock so a
    // bridge snapshot can't race a re-register.
    private readonly List<INodeCapability> _capabilities = new();
    private readonly object _capabilitiesLock = new();

    // Local MCP server — exposes the same capabilities to local MCP clients.
    // TODO: when the port becomes user-configurable (see docs/MCP_MODE.md
    // "Deferred"), McpServerUrl needs to read the live port off the running
    // server, not the constant. Settings UI is the only consumer today.
    public const int McpDefaultPort = 8765;
    // OPENCLAW_MCP_PORT lets test instances bind a free port instead of fighting
    // over the default. Falls back to McpDefaultPort when unset or unparseable.
    private static readonly int McpPort =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MCP_PORT"), out var p) && p > 0
            ? p
            : McpDefaultPort;
    private static readonly bool SuppressExternalBrowserLaunches =
        string.Equals(
            Environment.GetEnvironmentVariable("OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"),
            "1",
            StringComparison.Ordinal);
    public static string McpServerUrl => $"http://127.0.0.1:{McpPort}/";
    /// <summary>
    /// Path of the MCP bearer-token file. The file is created on first MCP server
    /// start and persists across restarts; the same string is sent on every POST
    /// as <c>Authorization: Bearer &lt;contents&gt;</c>. Surfaced by Settings UI so
    /// users can hand the value off to local agents/CLIs without spelunking.
    /// </summary>
    public static string McpTokenPath =>
        System.IO.Path.Combine(SettingsManager.SettingsDirectoryPath, "mcp-token.txt");
    private readonly bool _enableMcpServer;
    private McpHttpServer? _mcpServer;
    private string? _mcpStartupError;
    public bool IsMcpRunning => _mcpServer != null;
    public string McpEndpoint => McpServerUrl;
    /// <summary>Last MCP server startup error, or null if it started cleanly. Surfaced by Settings UI.</summary>
    public string? McpStartupError => _mcpStartupError;
    
    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SystemNotifyArgs>? NotificationRequested;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    public event EventHandler<NodeInvokeCompletedEventArgs>? InvokeCompleted;
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    
    public bool IsConnected => _nodeClient?.IsConnected ?? false;
    public string? NodeId => _nodeClient?.NodeId;
    public bool IsPendingApproval => _nodeClient?.IsPendingApproval ?? false;
    public bool IsPaired => _nodeClient?.IsPaired ?? false;
    public string? ShortDeviceId => _nodeClient?.ShortDeviceId;
    public string? FullDeviceId => _nodeClient?.FullDeviceId;
    public string? GatewayUrl => _nodeClient?.GatewayUrl;
    
    public NodeService(
        IOpenClawLogger logger,
        DispatcherQueue dispatcherQueue,
        string dataPath,
        Func<FrameworkElement?>? rootProvider = null,
        SettingsManager? settings = null,
        bool enableMcpServer = false)
    {
        _logger = logger;
        _dispatcherQueue = dispatcherQueue;
        _dataPath = dataPath;
        _rootProvider = rootProvider ?? (() => null);
        _settings = settings;
        _enableMcpServer = enableMcpServer;
        _screenCaptureService = new ScreenCaptureService(logger);
        _screenRecordingService = new ScreenRecordingService(logger);
        _cameraCaptureService = new CameraCaptureService(logger);
    }
    
    /// <summary>
    /// Initialize and connect the node
    /// </summary>
    public async Task ConnectAsync(string gatewayUrl, string token, string? bootstrapToken = null)
    {
        if (_nodeClient != null)
        {
            await DisconnectAsync();
        }

        _logger.Info($"Starting Windows Node connection to {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");
        _token = token;

        _nodeClient = new WindowsNodeClient(gatewayUrl, token, _dataPath, _logger, bootstrapToken);
        _nodeClient.StatusChanged += OnNodeStatusChanged;
        _nodeClient.PairingStatusChanged += OnPairingStatusChanged;
        _nodeClient.HealthReceived += OnNodeHealthReceived;
        _nodeClient.GatewaySelfUpdated += OnGatewaySelfUpdated;
        _nodeClient.InvokeCompleted += OnNodeInvokeCompleted;

        // Register capabilities (also pushes them to _nodeClient and sets permissions)
        RegisterCapabilities();

        await _nodeClient.ConnectAsync();
    }

    /// <summary>
    /// Bring up node capabilities and the local MCP server without opening a
    /// WebSocket to the gateway. Used for MCP-only mode where the tray app
    /// hosts capabilities for local MCP clients only.
    /// </summary>
    public Task StartLocalOnlyAsync()
    {
        // No gateway client at all — WebSocketClientBase requires non-empty
        // url/token, and we don't need it. Capabilities live on NodeService
        // and are consumed by the MCP bridge directly.
        _logger.Info("Starting Windows Node in MCP-only mode (no gateway)");
        _token = null;

        RegisterCapabilities();

        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Disconnect the node
    /// </summary>
    public async Task DisconnectAsync()
    {
        StopMcpServer();

        if (_nodeClient != null)
        {
            await _nodeClient.DisconnectAsync();
            _nodeClient.Dispose();
            _nodeClient = null;
        }

        lock (_capabilitiesLock) { _capabilities.Clear(); }

        // Close canvas window
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            _dispatcherQueue.TryEnqueue(() => _canvasWindow.Close());
            _canvasWindow = null;
        }

        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
        {
            _dispatcherQueue.TryEnqueue(() => _a2uiCanvasWindow.Close());
            _a2uiCanvasWindow = null;
        }
    }
    
    private void RegisterCapabilities()
    {
        // Hold the lock across the entire rebuild. The body is sync construction
        // (no awaits), so the lock is held briefly and an MCP tools/list arriving
        // mid-rebuild waits for a consistent snapshot rather than seeing a half-
        // populated list.
        lock (_capabilitiesLock)
        {
        _capabilities.Clear();

        // System capability (notifications + command execution)
        _systemCapability = new SystemCapability(_logger);
        _systemCapability.NotifyRequested += OnSystemNotify;
        _systemCapability.SetCommandRunner(new LocalCommandRunner(_logger));
        _systemCapability.SetApprovalPolicy(new ExecApprovalPolicy(_dataPath, _logger));
        _systemCapability.SetPromptHandler(new ExecApprovalPromptService(_dispatcherQueue, _rootProvider, _logger));
        Register(_systemCapability);

        if (_settings?.NodeCanvasEnabled != false)
        {
            _canvasCapability = new CanvasCapability(_logger);
            _canvasCapability.PresentRequested += OnCanvasPresent;
            _canvasCapability.HideRequested += OnCanvasHide;
            _canvasCapability.NavigateRequested += OnCanvasNavigate;
            _canvasCapability.EvalRequested += OnCanvasEval;
            _canvasCapability.SnapshotRequested += OnCanvasSnapshot;
            _canvasCapability.A2UIPushRequested += OnCanvasA2UIPush;
            _canvasCapability.A2UIResetRequested += OnCanvasA2UIReset;
            _canvasCapability.A2UIDumpRequested += OnCanvasA2UIDumpAsync;
            _canvasCapability.CapsRequested += OnCanvasCapsAsync;
            Register(_canvasCapability);
        }

        if (_settings?.NodeScreenEnabled != false)
        {
            _screenCapability = new ScreenCapability(_logger);
            _screenCapability.CaptureRequested += OnScreenCapture;
            _screenCapability.RecordRequested += OnScreenRecord;
            Register(_screenCapability);
        }

        if (_settings?.NodeCameraEnabled != false)
        {
            _cameraCapability = new CameraCapability(_logger);
            _cameraCapability.ListRequested += OnCameraList;
            _cameraCapability.SnapRequested += OnCameraSnap;
            _cameraCapability.ClipRequested += OnCameraClip;
            Register(_cameraCapability);
        }

        if (_settings?.NodeLocationEnabled != false)
        {
            _locationCapability = new LocationCapability(_logger);
            _locationCapability.GetRequested += async (args) => await GetLocationAsync(args);
            Register(_locationCapability);
        }

        if (_settings?.NodeTtsEnabled == true)
        {
            _textToSpeechService ??= new TextToSpeechService(_logger, _settings);
            _ttsCapability = new TtsCapability(_logger);
            _ttsCapability.SpeakRequested += OnTtsSpeakAsync;
            Register(_ttsCapability);
        }

        // Device metadata/status capability
        _deviceCapability = new DeviceCapability(_logger);
        Register(_deviceCapability);

        // BrowserProxy needs a live gateway connection — only register when gateway is up.
        if (_nodeClient != null && _settings?.NodeBrowserProxyEnabled != false)
        {
            _browserProxyCapability = new BrowserProxyCapability(
                _logger,
                _nodeClient.GatewayUrl,
                _token,
                sshRemoteGatewayPort: _settings?.UseSshTunnel == true
                    ? _settings.SshTunnelRemotePort
                    : null);
            Register(_browserProxyCapability);
        }

        if (_nodeClient != null)
        {
            if (_settings?.NodeCameraEnabled != false)
                _nodeClient.SetPermission("camera.capture", true);
            if (_settings?.NodeScreenEnabled != false)
                _nodeClient.SetPermission("screen.record", true);
        }

        _logger.Info($"Capabilities registered: {string.Join(", ", _capabilities.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase))} ({_capabilities.Count} caps)");
        } // end lock

        StartMcpServer();
    }

    /// <summary>
    /// Register one capability with both NodeService and (when present) the
    /// gateway client. Single seam so adding a new capability touches one
    /// site and is exposed by every transport (gateway + MCP) automatically.
    /// </summary>
    private void Register(INodeCapability capability)
    {
        _capabilities.Add(capability);
        _nodeClient?.RegisterCapability(capability);
    }

    private void StartMcpServer()
    {
        if (!_enableMcpServer) return;
        if (_mcpServer != null) return;
        McpHttpServer? attempt = null;
        try
        {
            // Bridge reads the live _capabilities list every tools/list, so any
            // future Register(...) call is exposed via MCP automatically.
            // The snapshot takes the same lock RegisterCapabilities holds,
            // so a tools/list arriving mid-rebuild observes either the old
            // or the new set — never a partially-cleared list.
            var bridge = new McpToolBridge(
                () => { lock (_capabilitiesLock) return _capabilities.ToArray(); },
                _logger,
                serverName: "openclaw-tray-mcp",
                serverVersion: typeof(NodeService).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            // Bearer-token auth. Token is created on first start and persists
            // alongside other OpenClawTray data (so OPENCLAW_TRAY_DATA_DIR
            // isolation in tests scopes the token too); CLI/agent registration
            // reads from the same path. Loopback bind + Origin/Host checks
            // remain in front; this layer rejects untrusted local processes
            // that could otherwise reach the predictable 127.0.0.1:port endpoint.
            var authToken = OpenClaw.Shared.Mcp.McpAuthToken.LoadOrCreate(McpTokenPath);
            // ACL hygiene check: warn if the token file is owned by someone
            // else, or if the DACL grants read to anyone outside
            // {current user, SYSTEM, Administrators}. Warning-only — restricting
            // ACLs is best-effort and a malicious local user can already do
            // worse than read this file. The point is operator visibility.
            var aclWarning = OpenClaw.Shared.Mcp.McpAuthToken.VerifyAcl(McpTokenPath);
            if (aclWarning != null) _logger.Warn($"[MCP] {aclWarning}");
            attempt = new McpHttpServer(bridge, McpPort, _logger, authToken);
            attempt.Start();
            _mcpServer = attempt;
            _mcpStartupError = null;
        }
        catch (Exception ex)
        {
            // Categorize so Settings can show something actionable instead of
            // raw HRESULT text. HttpListener errors on Windows fall into a
            // small set of recurring causes on developer machines.
            _mcpStartupError = DescribeMcpStartupFailure(ex, McpPort);
            _logger.Error($"[MCP] Failed to start HTTP server on port {McpPort}: {_mcpStartupError}", ex);
            // Avoid leaking the half-constructed listener / CTS.
            try { attempt?.Dispose(); } catch { /* ignore */ }
            _mcpServer = null;
        }
    }

    /// <summary>
    /// Translate a startup exception into a short, actionable message for the
    /// Settings UI. HttpListener wraps Win32 errors; a couple of NativeErrorCodes
    /// dominate and are worth calling out by name.
    /// </summary>
    internal static string DescribeMcpStartupFailure(Exception ex, int port) => ex switch
    {
        System.Net.HttpListenerException hle => hle.ErrorCode switch
        {
            5 => $"Access denied registering URL ACL on port {port}. Run `netsh http add urlacl url=http://127.0.0.1:{port}/ user={Environment.UserName}` from an elevated prompt.",
            32 or 183 => $"Port {port} is already in use. Stop the other process or change the MCP port.",
            _ => $"HTTP listener error {hle.ErrorCode}: {hle.Message}",
        },
        _ => ex.Message,
    };

    private void StopMcpServer()
    {
        // McpHttpServer.Dispose drains in-flight handler tasks (CR-005) before
        // returning, so by the time we tear down capability-backing services
        // (camera/screen/canvas) on the next lines of Dispose/Disconnect there
        // is no live handler still using them.
        try { _mcpServer?.Dispose(); } catch (Exception ex) { _logger.Warn($"[MCP] Dispose error: {ex.Message}"); }
        _mcpServer = null;
        _mcpStartupError = null;
    }

    public string ResetMcpToken()
    {
        var token = McpAuthToken.Reset(McpTokenPath);
        _mcpServer?.UpdateAuthToken(token);
        _logger.Info("[MCP] Bearer token rotated");
        return token;
    }

    public GatewayNodeInfo? GetLocalNodeInfo()
    {
        if (_nodeClient == null)
            return null;

        var capabilities = _nodeClient.Capabilities.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var commands = _nodeClient.Capabilities.SelectMany(c => c.Commands).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new GatewayNodeInfo
        {
            NodeId = _nodeClient.NodeId ?? _nodeClient.FullDeviceId ?? "",
            DisplayName = $"Windows Node ({Environment.MachineName})",
            Mode = "node",
            Status = IsConnected ? "connected" : "disconnected",
            Platform = "windows",
            LastSeen = DateTime.UtcNow,
            IsOnline = IsConnected,
            Capabilities = capabilities,
            Commands = commands,
            DisabledCommands = BuildDisabledCommands(),
            CapabilityCount = capabilities.Count,
            CommandCount = commands.Count,
            Permissions = BuildLocalPermissions()
        };
    }

    private List<string> BuildDisabledCommands()
    {
        var disabled = new List<string>();
        if (_settings?.NodeCanvasEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.SafeCompanionCommands.Where(command => command.StartsWith("canvas.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeScreenEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.MacNodeParityCommands.Where(command => command.StartsWith("screen.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeCameraEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.MacNodeParityCommands.Where(command => command.StartsWith("camera.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeLocationEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.SafeCompanionCommands.Where(command => command.StartsWith("location.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeBrowserProxyEnabled == false)
            disabled.Add("browser.proxy");
        if (_settings?.NodeTtsEnabled != true)
            disabled.AddRange(CommandCenterCommandGroups.DangerousCommands.Where(command => command.StartsWith("tts.", StringComparison.OrdinalIgnoreCase)));
        return disabled;
    }

    private Dictionary<string, bool> BuildLocalPermissions()
    {
        var permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (_settings?.NodeCameraEnabled != false)
            permissions["camera.capture"] = true;
        if (_settings?.NodeScreenEnabled != false)
            permissions["screen.record"] = true;
        return permissions;
    }
    
    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _logger.Info($"Node status changed: {status}");
        StatusChanged?.Invoke(this, status);
    }
    
    private void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
    {
        // Guard the slice — a malformed/missing/short device id would otherwise
        // throw out of the event handler and suppress PairingStatusChanged,
        // hiding the very pairing problem the listener is trying to diagnose.
        var displayId = string.IsNullOrEmpty(args.DeviceId)
            ? "unknown"
            : args.DeviceId[..Math.Min(16, args.DeviceId.Length)];
        _logger.Info($"Pairing status changed: {args.Status} (device: {displayId}...)");
        PairingStatusChanged?.Invoke(this, args);
    }

    private void OnNodeHealthReceived(object? sender, JsonElement payload)
    {
        if (payload.TryGetProperty("channels", out var channels))
        {
            var parsed = ChannelHealthParser.Parse(channels);
            _logger.Info(parsed.Length > 0
                ? $"Node health channels: {string.Join(", ", parsed.Select(c => $"{c.Name}={c.Status}"))}"
                : "Node health channels: none");
            ChannelHealthUpdated?.Invoke(this, parsed);
        }
    }

    private void OnGatewaySelfUpdated(object? sender, GatewaySelfInfo info)
    {
        GatewaySelfUpdated?.Invoke(this, info);
    }

    private void OnNodeInvokeCompleted(object? sender, NodeInvokeCompletedEventArgs args)
    {
        InvokeCompleted?.Invoke(this, args);
    }
    
    #region System Capability Handlers
    
    private void OnSystemNotify(object? sender, SystemNotifyArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            NotificationRequested?.Invoke(this, args);
        });
    }
    
    #endregion
    
    #region Canvas Capability Handlers
    
    private void OnCanvasPresent(object? sender, CanvasPresentArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Web canvas is taking the foreground — close the native A2UI window so
                // the user only sees the most-recently-targeted surface (last-write-wins).
                CloseA2UICanvasWindow();

                // Create or reuse canvas window
                if (_canvasWindow == null || _canvasWindow.IsClosed)
                {
                    _canvasWindow = new CanvasWindow();
                    _canvasWindow.SetTrustedGatewayOrigin(GatewayUrl, _token);
                }

                // Configure window
                _canvasWindow.Title = args.Title;
                _canvasWindow.SetSize(args.Width, args.Height);
                _canvasWindow.SetPosition(args.X, args.Y);
                _canvasWindow.SetAlwaysOnTop(args.AlwaysOnTop);

                // Load content
                if (!string.IsNullOrEmpty(args.Url))
                {
                    _canvasWindow.Navigate(args.Url);
                }
                else if (!string.IsNullOrEmpty(args.Html))
                {
                    _canvasWindow.LoadHtml(args.Html);
                }

                // Show window
                _canvasWindow.Activate();
                _canvasWindow.BringToFront(args.AlwaysOnTop);

                _logger.Info($"Canvas presented: {args.Width}x{args.Height}");
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas present failed", ex);
            }
        });
    }

    private void CloseWebCanvasWindow()
    {
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            try { _canvasWindow.Close(); } catch { /* ignore */ }
        }
        _canvasWindow = null;
    }

    private void CloseA2UICanvasWindow()
    {
        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
        {
            try { _a2uiCanvasWindow.Close(); } catch { /* ignore */ }
        }
        _a2uiCanvasWindow = null;
    }
    
    private void OnCanvasHide(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    _canvasWindow.Close();
                    _canvasWindow = null;
                    _logger.Info("Canvas hidden");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas hide failed", ex);
            }
        });
    }
    
    /// <summary>
    /// Service a <c>canvas.navigate</c> request by launching the URL in the
    /// OS default browser. Always — even if a WebView2 canvas window is open.
    /// Rationale: "open this link" on Windows means the default browser, and
    /// the embedded WebView2 canvas runs URL-rewriting (gateway-origin pinning,
    /// CSP, etc.) that mangles arbitrary external URLs. Agents that want to
    /// load a page inside an embedded surface should use <c>canvas.present</c>.
    ///
    /// Open canvas windows are NOT closed after navigate. A2UI surfaces are
    /// control panels / dashboards / launchers, not browser frames; clicking a
    /// link inside one shouldn't dismiss it any more than clicking a link in
    /// the Start Menu would. Agents that want explicit teardown should call
    /// <c>canvas.hide</c> or emit <c>deleteSurface</c>.
    ///
    /// CanvasCapability has already validated the URL with HttpUrlValidator;
    /// we re-validate here as defense-in-depth so the OS-level shell-execute
    /// can never see an unvetted string.
    /// </summary>
    private Task<string> OnCanvasNavigate(string url)
    {
        if (!HttpUrlValidator.TryParse(url, out var canonical, out var validationError))
        {
            _logger.Warn($"OnCanvasNavigate rejected (validator): {validationError}");
            throw new InvalidOperationException($"Invalid url: {validationError}");
        }

        var initialRisk = HttpUrlRiskEvaluator.Evaluate(canonical!);

        // Move the entire decision off the request thread so the agent's
        // response latency carries no signal about the user's decision (see
        // long comment retained below). DNS resolution + prompt + launch all
        // run from the worker.
        _ = Task.Run(async () =>
        {
            try
            {
                // Best-effort triage: resolve DNS now so a hostname pointing at
                // an internal IP raises the prompt. This is NOT a pin on the
                // launched request — the OS browser performs its own DNS
                // resolution when handed the URL, so the actual trust boundary
                // is the user's browser zone/proxy config. A second resolve
                // immediately before ShellExecute would not change that.
                var pinnedRisk = await EnrichWithDnsRiskAsync(initialRisk).ConfigureAwait(false);
                if (await ShouldLaunchAfterPromptAsync(pinnedRisk).ConfigureAwait(false))
                    LaunchInDefaultBrowser(canonical!);
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas navigate (deferred) failed", ex);
            }
        });

        // The agent gets the same response shape and the same response time
        // whether or not a confirmation prompt is needed. If we awaited the
        // prompt here, response latency would leak the user's decision time
        // (or even the existence of a prompt).
        return Task.FromResult("browser");
    }

    /// <summary>
    /// Decide whether to launch given an enriched risk profile, prompting the
    /// user when required while bounding prompt frequency:
    ///   - HostKey in the deny cooldown → silently refuse (recent denial).
    ///   - HostKey already in the session allowlist → launch.
    ///   - Concurrent request for the same HostKey → await the existing prompt
    ///     and inherit its decision rather than stacking a duplicate prompt.
    ///   - Otherwise: hold the global single-prompt gate, show the prompt,
    ///     record cooldown on Deny.
    /// </summary>
    private async Task<bool> ShouldLaunchAfterPromptAsync(HttpUrlRiskProfile pinnedRisk)
    {
        if (!pinnedRisk.RequiresConfirmation || _allowedNavigationHosts.ContainsKey(pinnedRisk.HostKey))
            return true;

        if (_navigationDenyCooldown.TryGetValue(pinnedRisk.HostKey, out var expiresAt))
        {
            if (DateTimeOffset.UtcNow < expiresAt)
            {
                _logger.Warn($"Canvas navigate auto-denied (cooldown): {OpenClaw.Shared.UrlLogSanitizer.Sanitize(pinnedRisk.CanonicalOrigin)}");
                return false;
            }
            // Stale entry — drop it. A racing concurrent request can re-add via
            // the deny path below; this is just opportunistic cleanup.
            _navigationDenyCooldown.TryRemove(pinnedRisk.HostKey, out _);
        }

        // Coalesce: if a prompt is already pending for this host, await its
        // outcome instead of showing a second prompt for the same destination.
        // Use a TaskCompletionSource so all waiters resolve atomically.
        var tcs = new TaskCompletionSource<UrlNavigationApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _pendingNavigationPrompts.GetOrAdd(pinnedRisk.HostKey, tcs.Task);
        if (!ReferenceEquals(existing, tcs.Task))
        {
            var inherited = await existing.ConfigureAwait(false);
            return inherited.Kind != UrlNavigationApprovalDecisionKind.Deny;
        }

        try
        {
            // Serialize prompt display globally — multiple HostKeys racing must
            // not stack overlapping topmost MessageBoxes either.
            await _navigationPromptGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check cooldown / allowlist now that we hold the gate — a
                // prior prompt's Deny may have populated the cooldown while we
                // were queued.
                if (_allowedNavigationHosts.ContainsKey(pinnedRisk.HostKey))
                {
                    var allowDecision = UrlNavigationApprovalDecision.AllowOnce();
                    tcs.TrySetResult(allowDecision);
                    return true;
                }
                if (_navigationDenyCooldown.TryGetValue(pinnedRisk.HostKey, out var nowExpires)
                    && DateTimeOffset.UtcNow < nowExpires)
                {
                    var denyDecision = UrlNavigationApprovalDecision.Deny("cooldown");
                    tcs.TrySetResult(denyDecision);
                    _logger.Warn($"Canvas navigate auto-denied (cooldown): {OpenClaw.Shared.UrlLogSanitizer.Sanitize(pinnedRisk.CanonicalOrigin)}");
                    return false;
                }

                var decision = await new UrlNavigationApprovalService(_logger)
                    .RequestAsync(pinnedRisk, BuildNavigationAgentIdentity())
                    .ConfigureAwait(false);
                tcs.TrySetResult(decision);

                if (decision.Kind == UrlNavigationApprovalDecisionKind.Deny)
                {
                    _navigationDenyCooldown[pinnedRisk.HostKey] = DateTimeOffset.UtcNow + NavigationDenyCooldownDuration;
                    _logger.Warn($"Canvas navigate denied: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(pinnedRisk.CanonicalOrigin)} ({decision.Reason ?? "user denied"}); already reported success to agent");
                    return false;
                }
                // AllowHost (session-allowlist) is currently unreachable from
                // the Win32 prompt — Yes maps to AllowOnce only. The session
                // allowlist remains as scaffolding for a future Fluent
                // ContentDialog prompt (worklist T2-43).
                return true;
            }
            finally
            {
                _navigationPromptGate.Release();
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _pendingNavigationPrompts.TryRemove(pinnedRisk.HostKey, out _);
        }
    }

    /// <summary>
    /// If the URL's host is a DNS name, resolve it and treat any non-public
    /// answer as a Reason. Returns the input profile unchanged for IP literals
    /// or when DNS resolution fails (a failed DNS lookup is its own
    /// confirmation trigger).
    /// </summary>
    private static async Task<HttpUrlRiskProfile> EnrichWithDnsRiskAsync(HttpUrlRiskProfile risk)
    {
        if (!Uri.TryCreate(risk.CanonicalUrl, UriKind.Absolute, out var uri)) return risk;
        if (System.Net.IPAddress.TryParse(uri.Host, out _)) return risk;

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, cts.Token).ConfigureAwait(false);
            var extra = new List<string>(risk.Reasons);
            bool anyNonPublic = false;
            foreach (var ip in addresses)
            {
                if (!HttpUrlRiskEvaluator.IsPublicAddress(ip))
                {
                    anyNonPublic = true;
                    extra.Add($"DNS resolved '{uri.Host}' to non-public address {ip}");
                }
            }
            if (!anyNonPublic) return risk;
            var merged = extra.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return risk with { RequiresConfirmation = true, Reasons = merged };
        }
        catch (Exception ex)
        {
            // Failed lookup → require prompt: better to ask than to ship the
            // user to an unverifiable destination.
            var extra = new List<string>(risk.Reasons) { $"DNS resolution failed for '{uri.Host}': {ex.Message}" };
            return risk with { RequiresConfirmation = true, Reasons = extra.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
    }

    /// <summary>
    /// Hand off to the OS default browser via ShellExecuteEx, then close any
    /// open in-app canvas surfaces (web or A2UI) on the dispatcher. Failures
    /// are logged but never thrown — callers expect a fire-and-forget shape.
    /// </summary>
    private void LaunchInDefaultBrowser(string canonical)
    {
        if (SuppressExternalBrowserLaunches)
        {
            _logger.Info($"Canvas navigate suppressed external browser launch: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(canonical)}");
            return;
        }

        // Process.Start with UseShellExecute=true wraps ShellExecuteEx, which
        // routes the URL to the user's registered http/https handler — never
        // to a script host or file association — given the validator already
        // restricted the scheme.
        //
        // Note: this used to close any open canvas windows after launch. That
        // made sense when canvas == WebView2 and navigate implied "you don't
        // need this frame anymore." With native A2UI the canvas is a control
        // surface (dashboard / launcher), not a browser frame — clicking a
        // link in a dashboard shouldn't nuke the dashboard. Lifecycle is now
        // explicit: agents that want the canvas dismissed after a navigate
        // should follow up with canvas.hide or deleteSurface.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = canonical,
                UseShellExecute = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            _logger.Info($"Canvas navigate → default browser: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(canonical)}");
        }
        catch (Exception ex)
        {
            _logger.Error("Canvas navigate failed", ex);
        }
    }

    private string BuildNavigationAgentIdentity()
    {
        var device = _nodeClient?.ShortDeviceId ?? _nodeClient?.FullDeviceId ?? "local MCP";
        var gateway = _nodeClient?.GatewayUrl;
        return string.IsNullOrWhiteSpace(gateway)
            ? device
            : $"{device} via {GatewayUrlHelper.SanitizeForDisplay(gateway)}";
    }

    private async Task<string> OnCanvasEval(string script)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    var result = await _canvasWindow.EvalAsync(script);
                    tcs.TrySetResult(result);
                }
                else if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
                {
                    // Native A2UI surface has no JS runtime; surface a structured error
                    // so callers can branch instead of pattern-matching free-text.
                    tcs.TrySetException(new InvalidOperationException(
                        "CANVAS_EVAL_UNAVAILABLE: native A2UI renderer has no JS runtime; use canvas.a2ui.dump for state introspection"));
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "CANVAS_NOT_OPEN: no canvas window is currently open"));
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));

        return await tcs.Task;
    }

    private async Task<string> OnCanvasSnapshot(CanvasSnapshotArgs args)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    var base64 = await _canvasWindow.CaptureSnapshotAsync(args.Format);
                    tcs.TrySetResult(base64);
                }
                else if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
                {
                    // Render the native XAML surface to PNG/JPEG via RenderTargetBitmap
                    // so vision pipelines and regression diffs keep working post-cutover.
                    var base64 = await _a2uiCanvasWindow.CaptureSnapshotAsync(args.Format);
                    tcs.TrySetResult(base64);
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "CANVAS_NOT_OPEN: no canvas window is currently open"));
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));

        return await tcs.Task;
    }

    private Task<string> OnCanvasA2UIDumpAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_a2uiCanvasWindow == null || _a2uiCanvasWindow.IsClosed)
                {
                    tcs.TrySetResult(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        renderer = "none",
                        a2uiVersion = "0.8",
                        surfaceCount = 0,
                        surfaces = new { },
                    }));
                    return;
                }
                tcs.TrySetResult(_a2uiCanvasWindow.GetStateSnapshot());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));
        return tcs.Task;
    }

    private Task<string> OnCanvasCapsAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                bool nativeOpen = _a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed;
                bool webOpen = _canvasWindow != null && !_canvasWindow.IsClosed;
                var caps = new
                {
                    renderer = nativeOpen ? "native" : (webOpen ? "web" : "none"),
                    eval = webOpen,
                    snapshot = webOpen || nativeOpen,
                    // navigate is always available: when no web canvas is open
                    // we fall back to launching the OS default browser. The
                    // navigate response carries an "opener" field so the agent
                    // can tell which path was taken.
                    navigate = true,
                    a2ui = new
                    {
                        version = "0.8",
                        push = true,
                        reset = true,
                        introspect = nativeOpen,
                    },
                };
                tcs.TrySetResult(System.Text.Json.JsonSerializer.Serialize(caps));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));
        return tcs.Task;
    }

    private void EnsureCanvasWindow()
    {
        if (_canvasWindow == null || _canvasWindow.IsClosed)
        {
            _canvasWindow = new CanvasWindow();
            _canvasWindow.SetTrustedGatewayOrigin(GatewayUrl, _token);
            _canvasWindow.Activate();
        }
    }

    // Mutable context shared with GatewayActionTransport. SessionKey is updated
    // from push props (when the agent supplies one); host/instance stay tied to
    // the node client identity. Default sessionKey is "main", matching Android's
    // resolveMainSessionKey() fallback.
    private sealed class GatewayActionContext : IGatewayActionContext
    {
        private readonly Func<WindowsNodeClient?> _client;
        private string _sessionKey = "main";
        public GatewayActionContext(Func<WindowsNodeClient?> client) { _client = client; }
        public string SessionKey
        {
            get => _sessionKey;
            set => _sessionKey = string.IsNullOrWhiteSpace(value) ? "main" : value.Trim();
        }
        public string Host => _client()?.DisplayName ?? $"Windows Node ({Environment.MachineName})";
        public string InstanceId => _client()?.FullDeviceId.ToLowerInvariant() ?? string.Empty;
    }

    private GatewayActionContext? _actionContext;

    /// <summary>
    /// Lazily build the action dispatcher + media resolver shared by the
    /// native A2UI canvas. The dispatcher routes outbound user actions to
    /// the gateway when connected, falling back to a logger-only sink for
    /// MCP-only mode (a future MCP notifications channel will replace it).
    /// </summary>
    private ActionDispatcher GetOrCreateActionDispatcher()
    {
        if (_actionDispatcher != null) return _actionDispatcher;

        _actionContext = new GatewayActionContext(() => _nodeClient);
        var transports = new IA2UIActionTransport[]
        {
            new GatewayActionTransport(() => _nodeClient, _actionContext, _logger),
            new LoggingActionTransport(_logger),
        };
        _actionDispatcher = new ActionDispatcher(transports, _logger);
        return _actionDispatcher;
    }

    /// <summary>
    /// Pull <c>sessionKey</c> out of the push props blob and update the action
    /// context so subsequent button clicks route to the same session. Silently
    /// no-ops when props is malformed or doesn't include a sessionKey — the
    /// previous (or default "main") value stays in effect.
    /// </summary>
    private void UpdateSessionKeyFromPushProps(string? propsJson)
    {
        if (_actionContext == null) return;
        if (string.IsNullOrWhiteSpace(propsJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(propsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("sessionKey", out var sk) &&
                sk.ValueKind == JsonValueKind.String)
            {
                var v = sk.GetString();
                if (!string.IsNullOrWhiteSpace(v)) _actionContext.SessionKey = v!;
            }
        }
        catch
        {
            // Bad props JSON is a gateway/agent bug, not an action-routing bug.
            // Keep the previous sessionKey rather than failing the push.
        }
    }

    private MediaResolver GetOrCreateMediaResolver()
    {
        if (_mediaResolver != null) return _mediaResolver;
        _mediaResolver = new MediaResolver(_logger);
        // Settings.A2UIImageHosts is the single source of truth for HTTPS image
        // fetching. Empty list = inline data: only, which is the safe default.
        if (_settings?.A2UIImageHosts is { Count: > 0 } hosts)
        {
            foreach (var host in hosts) _mediaResolver.AllowHost(host);
        }
        return _mediaResolver;
    }

    private void EnsureA2UICanvasWindow()
    {
        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed) return;

        // Native A2UI is taking the foreground — close the legacy WebView2 canvas
        // so its placeholder doesn't mask the rendered surface.
        CloseWebCanvasWindow();

        var actions = GetOrCreateActionDispatcher();
        var media = GetOrCreateMediaResolver();
        _a2uiCanvasWindow = new A2UICanvasWindow(actions, media, _logger);
        _a2uiCanvasWindow.Activate();
        _a2uiCanvasWindow.BringToFront(false);
    }

    private void OnCanvasA2UIPush(object? sender, CanvasA2UIArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                EnsureA2UICanvasWindow();
                if (_a2uiCanvasWindow == null)
                {
                    _logger.Error("Canvas A2UI push failed: native canvas window not available");
                    return;
                }
                // Pick up an explicit sessionKey from props if the agent supplied one,
                // so a Button click on this surface routes back to the same session.
                UpdateSessionKeyFromPushProps(args.Props);
                _a2uiCanvasWindow.Push(args.Jsonl ?? string.Empty);
                _a2uiCanvasWindow.BringToFront(false);
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas A2UI push failed", ex);
            }
        });
    }

    private void OnCanvasA2UIReset(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_a2uiCanvasWindow == null || _a2uiCanvasWindow.IsClosed)
                {
                    _logger.Debug("Canvas A2UI reset: no native canvas to reset");
                    return;
                }
                _a2uiCanvasWindow.Reset();
                _logger.Info("Canvas A2UI reset");
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas A2UI reset failed", ex);
            }
        });
    }
    
    #endregion
    
    #region Screen Capability Handlers
    
    private async Task<ScreenCaptureResult> OnScreenCapture(ScreenCaptureArgs args)
    {
        if (_screenCaptureService == null)
        {
            throw new InvalidOperationException("Screen capture service not available");
        }
        
        // Notify user that screen capture is happening (throttled to avoid spam)
        var now = DateTime.Now;
        if ((now - _lastScreenCaptureNotification).TotalSeconds > 10)
        {
            _lastScreenCaptureNotification = now;
            try
            {
                new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_ScreenCaptured"))
                    .AddText(LocalizationHelper.GetString("Toast_ScreenCapturedDetail"))
                    .Show();
            }
            catch { /* ignore notification errors */ }
        }
        
        return await _screenCaptureService.CaptureAsync(args);
    }

    private Task<ScreenRecordResult> OnScreenRecord(ScreenRecordArgs args)
    {
        if (_screenRecordingService == null)
        {
            throw new InvalidOperationException("Screen recording service not available");
        }

        return _screenRecordingService.RecordAsync(args);
    }
    
    #endregion
    
    #region Camera Capability Handlers
    
    private Task<CameraInfo[]> OnCameraList()
    {
        if (_cameraCaptureService == null)
        {
            throw new InvalidOperationException("Camera capture service not available");
        }
        
        return _cameraCaptureService.ListCamerasAsync();
    }
    
    private async Task<CameraSnapResult> OnCameraSnap(CameraSnapArgs args)
    {
        if (_cameraCaptureService == null)
        {
            throw new InvalidOperationException("Camera capture service not available");
        }
        
        try
        {
            return await _cameraCaptureService.SnapAsync(args);
        }
        catch (UnauthorizedAccessException ex)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_CameraBlocked"))
                    .AddText(LocalizationHelper.GetString("Toast_CameraBlockedDetail"))
                    .Show();
            }
            catch { }
            
            throw new InvalidOperationException(
                "Camera access blocked. Enable camera access for desktop apps in Windows Privacy settings.",
                ex);
        }
    }
    
    private async Task<CameraClipResult> OnCameraClip(CameraClipArgs args)
    {
        if (_cameraCaptureService == null)
        {
            throw new InvalidOperationException("Camera capture service not available");
        }
        
        try
        {
            return await _cameraCaptureService.ClipAsync(args);
        }
        catch (UnauthorizedAccessException ex)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_CameraBlocked"))
                    .AddText(LocalizationHelper.GetString("Toast_CameraBlockedDetail"))
                    .Show();
            }
            catch { }
            
            throw new InvalidOperationException(
                "Camera access blocked. Enable camera access for desktop apps in Windows Privacy settings.",
                ex);
        }
    }
    
    private async Task<LocationResult> GetLocationAsync(LocationGetArgs args)
    {
        var geolocator = new global::Windows.Devices.Geolocation.Geolocator
        {
            DesiredAccuracy = args.Accuracy == "precise"
                ? global::Windows.Devices.Geolocation.PositionAccuracy.High
                : global::Windows.Devices.Geolocation.PositionAccuracy.Default
        };
        
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(args.TimeoutMs));
        var position = await geolocator.GetGeopositionAsync().AsTask(cts.Token);
        
        return new LocationResult
        {
            Latitude = position.Coordinate.Point.Position.Latitude,
            Longitude = position.Coordinate.Point.Position.Longitude,
            AccuracyMeters = position.Coordinate.Accuracy,
            TimestampMs = position.Coordinate.Timestamp.ToUnixTimeMilliseconds()
        };
    }

    private Task<TtsSpeakResult> OnTtsSpeakAsync(TtsSpeakArgs args, CancellationToken cancellationToken)
    {
        if (_textToSpeechService == null)
            throw new InvalidOperationException("Text-to-speech service not available");

        return _textToSpeechService.SpeakAsync(args, cancellationToken);
    }
    
    #endregion
    
    public void Dispose()
    {
        StopMcpServer();

        var client = _nodeClient;
        _nodeClient = null;
        try { client?.Dispose(); } catch { /* ignore */ }

        try { _cameraCaptureService?.Dispose(); } catch { /* ignore */ }
        try { _screenRecordingService?.Dispose(); } catch { /* ignore */ }
        try { _textToSpeechService?.Dispose(); } catch { /* ignore */ }
        // MediaResolver owns SocketsHttpHandler + HttpClient (disposeHandler:true);
        // without disposal the connection pool survives node teardown/recreate.
        try { _mediaResolver?.Dispose(); } catch { /* ignore */ }
        _mediaResolver = null;
        // ActionDispatcher owns a SemaphoreSlim; without disposal the kernel
        // handle survives node teardown/recreate.
        try { _actionDispatcher?.Dispose(); } catch { /* ignore */ }
        _actionDispatcher = null;

        try { _navigationPromptGate.Dispose(); } catch { /* ignore */ }

        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            var window = _canvasWindow;
            _canvasWindow = null;
            _dispatcherQueue.TryEnqueue(() => { try { window?.Close(); } catch { } });
        }

        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
        {
            var window = _a2uiCanvasWindow;
            _a2uiCanvasWindow = null;
            _dispatcherQueue.TryEnqueue(() => { try { window?.Close(); } catch { } });
        }
    }
}
