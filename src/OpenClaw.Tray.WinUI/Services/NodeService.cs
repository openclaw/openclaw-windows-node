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
using OpenClawTray.Helpers;
using OpenClawTray.Windows;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Services;

/// <summary>
/// Windows Node service - manages node connection and capabilities
/// </summary>
public class NodeService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<FrameworkElement?> _rootProvider;
    private readonly SettingsManager? _settings;
    private WindowsNodeClient? _nodeClient;
    private CanvasWindow? _canvasWindow;
    private ScreenCaptureService? _screenCaptureService;
    private ScreenRecordingService? _screenRecordingService;
    private CameraCaptureService? _cameraCaptureService;
    private DateTime _lastScreenCaptureNotification = DateTime.MinValue;
    private string? _a2uiHostUrl;
    
    // Capabilities
    private SystemCapability? _systemCapability;
    private CanvasCapability? _canvasCapability;
    private ScreenCapability? _screenCapability;
    private CameraCapability? _cameraCapability;
    private LocationCapability? _locationCapability;
    private DeviceCapability? _deviceCapability;
    private BrowserProxyCapability? _browserProxyCapability;
    private readonly string _dataPath;
    private string? _token;

    // Authoritative capability list — populated by RegisterCapabilities and
    // shared with both the gateway client (when present) and the MCP bridge.
    // Holding it here lets MCP-only mode skip the gateway client entirely.
    private readonly List<INodeCapability> _capabilities = new();

    // Local MCP server — exposes the same capabilities to local MCP clients.
    public const int McpDefaultPort = 8765;
    public static string McpServerUrl => $"http://127.0.0.1:{McpDefaultPort}/";
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

        _a2uiHostUrl = BuildA2UIHostUrl(_nodeClient.GatewayUrl);
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

        _capabilities.Clear();

        // Close canvas window
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            _dispatcherQueue.TryEnqueue(() => _canvasWindow.Close());
            _canvasWindow = null;
        }
    }
    
    private void RegisterCapabilities()
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
            // Snapshot via ToArray() so an MCP request enumerating the list
            // doesn't race with a re-register on the UI thread.
            var bridge = new McpToolBridge(
                () => _capabilities.ToArray(),
                _logger,
                serverName: "openclaw-tray-mcp",
                serverVersion: typeof(NodeService).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            attempt = new McpHttpServer(bridge, McpDefaultPort, _logger);
            attempt.Start();
            _mcpServer = attempt;
        }
        catch (Exception ex)
        {
            _logger.Error($"[MCP] Failed to start HTTP server on port {McpDefaultPort}", ex);
            _mcpStartupError = ex.Message;
            // Avoid leaking the half-constructed listener / CTS.
            try { attempt?.Dispose(); } catch { /* ignore */ }
            _mcpServer = null;
        }
    }

    private void StopMcpServer()
    {
        try { _mcpServer?.Dispose(); } catch (Exception ex) { _logger.Warn($"[MCP] Dispose error: {ex.Message}"); }
        _mcpServer = null;
        _mcpStartupError = null;
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
        _logger.Info($"Pairing status changed: {args.Status} (device: {args.DeviceId.Substring(0, 16)}...)");
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
    
    private void OnCanvasNavigate(object? sender, string url)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    _canvasWindow.Navigate(url);
                }
                else
                {
                    _logger.Warn("Canvas navigate ignored: canvas not available");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas navigate failed", ex);
            }
        });
    }
    
    private async Task<string> OnCanvasEval(string script)
    {
        var tcs = new TaskCompletionSource<string>();
        
        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    var result = await _canvasWindow.EvalAsync(script);
                    tcs.SetResult(result);
                }
                else
                {
                    tcs.SetException(new InvalidOperationException("Canvas not available"));
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("Dispatcher queue unavailable"));
        
        return await tcs.Task;
    }
    
    private async Task<string> OnCanvasSnapshot(CanvasSnapshotArgs args)
    {
        var tcs = new TaskCompletionSource<string>();
        
        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    var base64 = await _canvasWindow.CaptureSnapshotAsync(args.Format);
                    tcs.SetResult(base64);
                }
                else
                {
                    tcs.SetException(new InvalidOperationException("Canvas not available"));
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("Dispatcher queue unavailable"));
        
        return await tcs.Task;
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

    private static string? BuildA2UIHostUrl(string? gatewayUrl)
    {
        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedGatewayUrl))
            return null;
        
        if (!Uri.TryCreate(normalizedGatewayUrl, UriKind.Absolute, out var uri))
            return null;
        
        var scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var port = uri.Port;
        var host = uri.Host;
        return $"{scheme}://{host}:{port}/__openclaw__/a2ui/";
    }
    
    private void OnCanvasA2UIPush(object? sender, CanvasA2UIArgs args)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                EnsureCanvasWindow();
                if (_canvasWindow == null)
                {
                    _logger.Error("Canvas A2UI push failed: canvas window not available");
                    return;
                }
                
                var hostUrl = _a2uiHostUrl ?? BuildA2UIHostUrl(GatewayUrl);
                if (string.IsNullOrWhiteSpace(hostUrl))
                {
                    _logger.Error("Canvas A2UI push failed: A2UI host URL unavailable");
                    return;
                }
                
                await _canvasWindow.EnsureA2UIHostAsync(hostUrl);
                
                var jsonl = args.Jsonl ?? string.Empty;
                var lines = jsonl.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                var sent = 0;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    await _canvasWindow.SendA2UIMessageAsync(trimmed);
                    sent++;
                }
                
                _logger.Info($"Canvas A2UI push: {sent} message(s)");
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas A2UI push failed", ex);
            }
        });
    }
    
    private void OnCanvasA2UIReset(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_canvasWindow == null || _canvasWindow.IsClosed)
                {
                    _logger.Warn("Canvas A2UI reset ignored: canvas not available");
                    return;
                }
                
                var hostUrl = _a2uiHostUrl ?? BuildA2UIHostUrl(GatewayUrl);
                if (!string.IsNullOrWhiteSpace(hostUrl))
                {
                    await _canvasWindow.EnsureA2UIHostAsync(hostUrl);
                }
                
                await _canvasWindow.ResetA2UIAsync();
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
    
    #endregion
    
    public void Dispose()
    {
        StopMcpServer();

        var client = _nodeClient;
        _nodeClient = null;
        try { client?.Dispose(); } catch { /* ignore */ }

        try { _cameraCaptureService?.Dispose(); } catch { /* ignore */ }
        try { _screenRecordingService?.Dispose(); } catch { /* ignore */ }
        
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            var window = _canvasWindow;
            _canvasWindow = null;
            _dispatcherQueue.TryEnqueue(() => { try { window?.Close(); } catch { } });
        }
    }
}

