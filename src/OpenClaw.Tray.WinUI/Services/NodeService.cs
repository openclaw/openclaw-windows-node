using System;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
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
    private WindowsNodeClient? _nodeClient;
    private CanvasWindow? _canvasWindow;
    private ScreenCaptureService? _screenCaptureService;
    private CameraCaptureService? _cameraCaptureService;
    private DateTime _lastScreenCaptureNotification = DateTime.MinValue;
    private string? _a2uiHostUrl;
    
    // Capabilities
    private SystemCapability? _systemCapability;
    private CanvasCapability? _canvasCapability;
    private ScreenCapability? _screenCapability;
    private CameraCapability? _cameraCapability;
    private readonly string _dataPath;
    
    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SystemNotifyArgs>? NotificationRequested;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    
    public bool IsConnected => _nodeClient?.IsConnected ?? false;
    public string? NodeId => _nodeClient?.NodeId;
    public bool IsPendingApproval => _nodeClient?.IsPendingApproval ?? false;
    public bool IsPaired => _nodeClient?.IsPaired ?? false;
    public string? ShortDeviceId => _nodeClient?.ShortDeviceId;
    public string? FullDeviceId => _nodeClient?.FullDeviceId;
    public string? GatewayUrl => _nodeClient?.GatewayUrl;
    
    public NodeService(IOpenClawLogger logger, DispatcherQueue dispatcherQueue, string dataPath)
    {
        _logger = logger;
        _dispatcherQueue = dispatcherQueue;
        _dataPath = dataPath;
        _screenCaptureService = new ScreenCaptureService(logger);
        _cameraCaptureService = new CameraCaptureService(logger);
    }
    
    /// <summary>
    /// Initialize and connect the node
    /// </summary>
    public async Task ConnectAsync(string gatewayUrl, string token)
    {
        if (_nodeClient != null)
        {
            await DisconnectAsync();
        }
        
        _logger.Info($"Starting Windows Node connection to {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");
        
        _nodeClient = new WindowsNodeClient(gatewayUrl, token, _dataPath, _logger);
        _nodeClient.StatusChanged += OnNodeStatusChanged;
        _nodeClient.PairingStatusChanged += OnPairingStatusChanged;
        
        // Register capabilities
        RegisterCapabilities();
        
        // Set permissions
        _nodeClient.SetPermission("camera.capture", true);
        _nodeClient.SetPermission("screen.record", true);
        
        await _nodeClient.ConnectAsync();
        
        _a2uiHostUrl = BuildA2UIHostUrl(_nodeClient.GatewayUrl);
    }
    
    /// <summary>
    /// Disconnect the node
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_nodeClient != null)
        {
            await _nodeClient.DisconnectAsync();
            _nodeClient.Dispose();
            _nodeClient = null;
        }
        
        // Close canvas window
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            _dispatcherQueue.TryEnqueue(() => _canvasWindow.Close());
            _canvasWindow = null;
        }
    }
    
    private void RegisterCapabilities()
    {
        if (_nodeClient == null) return;
        
        // System capability (notifications + command execution)
        _systemCapability = new SystemCapability(_logger);
        _systemCapability.NotifyRequested += OnSystemNotify;
        _systemCapability.SetCommandRunner(new LocalCommandRunner(_logger));
        _systemCapability.SetApprovalPolicy(new ExecApprovalPolicy(_dataPath, _logger));
        _nodeClient.RegisterCapability(_systemCapability);
        
        // Canvas capability
        _canvasCapability = new CanvasCapability(_logger);
        _canvasCapability.PresentRequested += OnCanvasPresent;
        _canvasCapability.HideRequested += OnCanvasHide;
        _canvasCapability.NavigateRequested += OnCanvasNavigate;
        _canvasCapability.EvalRequested += OnCanvasEval;
        _canvasCapability.SnapshotRequested += OnCanvasSnapshot;
        _canvasCapability.A2UIPushRequested += OnCanvasA2UIPush;
        _canvasCapability.A2UIResetRequested += OnCanvasA2UIReset;
        _nodeClient.RegisterCapability(_canvasCapability);
        
        // Screen capability
        _screenCapability = new ScreenCapability(_logger);
        _screenCapability.ListRequested += OnScreenList;
        _screenCapability.CaptureRequested += OnScreenCapture;
        _nodeClient.RegisterCapability(_screenCapability);

        // Camera capability
        _cameraCapability = new CameraCapability(_logger);
        _cameraCapability.ListRequested += OnCameraList;
        _cameraCapability.SnapRequested += OnCameraSnap;
        _nodeClient.RegisterCapability(_cameraCapability);
        
        _logger.Info("All capabilities registered");
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
        
        _dispatcherQueue.TryEnqueue(async () =>
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
        
        return await tcs.Task;
    }
    
    private async Task<string> OnCanvasSnapshot(CanvasSnapshotArgs args)
    {
        var tcs = new TaskCompletionSource<string>();
        
        _dispatcherQueue.TryEnqueue(async () =>
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
        
        return await tcs.Task;
    }

    private void EnsureCanvasWindow()
    {
        if (_canvasWindow == null || _canvasWindow.IsClosed)
        {
            _canvasWindow = new CanvasWindow();
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
    
    private Task<ScreenInfo[]> OnScreenList()
    {
        return _screenCaptureService?.ListScreensAsync() 
            ?? Task.FromResult(Array.Empty<ScreenInfo>());
    }
    
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
                    .AddText("📸 Screen Captured")
                    .AddText("OpenClaw agent captured your screen")
                    .Show();
            }
            catch { /* ignore notification errors */ }
        }
        
        return await _screenCaptureService.CaptureAsync(args);
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
                    .AddText("📷 Camera access blocked")
                    .AddText("Enable camera access in Windows Privacy settings for OpenClaw Tray")
                    .Show();
            }
            catch { }
            
            throw new InvalidOperationException(
                "Camera access blocked. Enable camera access for desktop apps in Windows Privacy settings.",
                ex);
        }
    }
    
    #endregion
    
    public void Dispose()
    {
        var client = _nodeClient;
        _nodeClient = null;
        try { client?.Dispose(); } catch { /* ignore */ }
        
        try { _cameraCaptureService?.Dispose(); } catch { /* ignore */ }
        
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            var window = _canvasWindow;
            _canvasWindow = null;
            _dispatcherQueue.TryEnqueue(() => { try { window?.Close(); } catch { } });
        }
    }
}
