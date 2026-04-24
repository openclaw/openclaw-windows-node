using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// Captures frames from the WinUI preview window and serves them over a local HTTP endpoint.
/// Uses Win32 PrintWindow for reliable capture of WinUI 3 content.
/// Designed for integration with a VS Code extension that displays a live thumbnail.
/// </summary>
internal sealed class PreviewCaptureServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Window _window;
    private readonly DispatcherQueueTimer _captureTimer;
    private readonly IntPtr _hwnd;

    private byte[] _latestFrame = [];
    private bool _disposed;
    private int _captureErrorCount;

    public int Port { get; }
    public int Fps { get; }

    /// <summary>Returns the list of available component names.</summary>
    public Func<List<string>>? GetComponents { get; set; }

    /// <summary>Returns the name of the currently previewed component.</summary>
    public Func<string?>? GetCurrentComponent { get; set; }

    /// <summary>Switches to a different component by name. Returns true on success.</summary>
    public Func<string, bool>? SwitchComponent { get; set; }

    public PreviewCaptureServer(DispatcherQueue dispatcherQueue, Window window, int fps = 10)
    {
        _dispatcherQueue = dispatcherQueue;
        _window = window;
        Fps = fps;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        Port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");

        _captureTimer = _dispatcherQueue.CreateTimer();
        _captureTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        _captureTimer.Tick += OnCaptureTimerTick;
    }

    public void Start()
    {
        _listener.Start();
        _captureTimer.Start();
        _ = ListenAsync().ContinueWith(
            t => Console.Error.WriteLine($"[devtools:capture] Listener loop failed: {t.Exception!.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);

        Console.WriteLine($"[devtools:capture] Serving on http://localhost:{Port}");
        Console.WriteLine($"CAPTURE_PORT={Port}");
        Console.Out.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureTimer.Stop();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    // -- Frame Capture (UI thread) -----------------------------------------------

    private void OnCaptureTimerTick(DispatcherQueueTimer timer, object args)
    {
        try
        {
            if (!NativeMethods.GetClientRect(_hwnd, out var clientRect)) return;

            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;
            if (width <= 0 || height <= 0) return;

            var clientOrigin = new NativeMethods.POINT { X = 0, Y = 0 };
            NativeMethods.ClientToScreen(_hwnd, ref clientOrigin);

            NativeMethods.GetWindowRect(_hwnd, out var windowRect);

            int offsetX = clientOrigin.X - windowRect.Left;
            int offsetY = clientOrigin.Y - windowRect.Top;
            int windowWidth = windowRect.Right - windowRect.Left;
            int windowHeight = windowRect.Bottom - windowRect.Top;

            if (windowWidth <= 0 || windowHeight <= 0) return;

            using var windowBmp = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
            using (var g = global::System.Drawing.Graphics.FromImage(windowBmp))
            {
                IntPtr hdc = g.GetHdc();
                NativeMethods.PrintWindow(_hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }

            using var clientBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = global::System.Drawing.Graphics.FromImage(clientBmp))
            {
                g.DrawImage(windowBmp,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(offsetX, offsetY, width, height),
                    GraphicsUnit.Pixel);
            }

            using var ms = new MemoryStream();
            clientBmp.Save(ms, ImageFormat.Jpeg);
            Interlocked.Exchange(ref _latestFrame, ms.ToArray());
        }
        catch (Exception ex)
        {
            var count = Interlocked.Increment(ref _captureErrorCount);
            if (count == 1 || (count % 100 == 0))
                Console.Error.WriteLine($"[devtools:capture] Frame capture error (count={count}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -- HTTP Server (background thread) -----------------------------------------

    private async Task ListenAsync()
    {
        while (!_disposed && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }

            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var response = ctx.Response;

        // Restrict CORS to localhost and VS Code webview origins
        var origin = ctx.Request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) &&
            (origin.StartsWith("http://localhost:") || origin.StartsWith("https://localhost:") ||
             origin.StartsWith("vscode-webview://")))
        {
            response.Headers.Add("Access-Control-Allow-Origin", origin);
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }

        try
        {
            switch (path)
            {
                case "/frame":
                    ServeFrame(response);
                    break;
                case "/status":
                    ServeStatus(response);
                    break;
                case "/focus":
                    HandleFocus(response);
                    break;
                case "/components":
                    ServeComponents(response);
                    break;
                case "/preview":
                    HandleSwitchComponent(ctx.Request, response);
                    break;
                default:
                    response.StatusCode = 404;
                    response.Close();
                    break;
            }
        }
        catch
        {
            try { response.StatusCode = 500; response.Close(); } catch { }
        }
    }

    private void ServeFrame(HttpListenerResponse response)
    {
        var frame = _latestFrame;
        if (frame.Length == 0)
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }

        response.ContentType = "image/jpeg";
        response.ContentLength64 = frame.Length;
        response.Headers.Add("Cache-Control", "no-store");
        response.OutputStream.Write(frame, 0, frame.Length);
        response.Close();
    }

    private void ServeStatus(HttpListenerResponse response)
    {
        var json = $"{{\"building\":false,\"fps\":{Fps},\"port\":{Port}}}";
        var bytes = Encoding.UTF8.GetBytes(json);

        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.Headers.Add("Cache-Control", "no-store");
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private void HandleFocus(HttpListenerResponse response)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try { NativeMethods.SetForegroundWindow(_hwnd); }
            catch { }
        });

        response.StatusCode = 200;
        var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private void ServeComponents(HttpListenerResponse response)
    {
        var components = GetComponents?.Invoke() ?? [];
        var current = GetCurrentComponent?.Invoke();
        var json = JsonSerializer.Serialize(
            new PreviewComponentsPayload { Components = components, Current = current },
            PreviewJsonContext.Default.PreviewComponentsPayload);
        var bytes = Encoding.UTF8.GetBytes(json);

        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.Headers.Add("Cache-Control", "no-store");
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private void HandleSwitchComponent(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            response.Close();
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        string? componentName = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            componentName = doc.RootElement.GetProperty("component").GetString();
        }
        catch { }

        if (string.IsNullOrEmpty(componentName) || SwitchComponent == null)
        {
            response.StatusCode = 400;
            var errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Missing component name\"}");
            response.ContentType = "application/json";
            response.ContentLength64 = errBytes.Length;
            response.OutputStream.Write(errBytes, 0, errBytes.Length);
            response.Close();
            return;
        }

        var success = SwitchComponent(componentName);
        JsonObject resultNode = success
            ? new JsonObject { ["ok"] = true, ["component"] = componentName }
            : new JsonObject { ["ok"] = false, ["error"] = $"Component '{componentName}' not found" };
        var resultBytes = Encoding.UTF8.GetBytes(resultNode.ToJsonString());

        response.StatusCode = success ? 200 : 404;
        response.ContentType = "application/json";
        response.ContentLength64 = resultBytes.Length;
        response.OutputStream.Write(resultBytes, 0, resultBytes.Length);
        response.Close();
    }

    // -- Helpers -----------------------------------------------------------------

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static class NativeMethods
    {
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}

// Named payload types for AOT-compatible JSON serialization.
internal sealed class PreviewComponentsPayload
{
    public List<string> Components { get; set; } = [];
    public string? Current { get; set; }
}

[global::System.Text.Json.Serialization.JsonSerializable(typeof(PreviewComponentsPayload))]
[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = global::System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal partial class PreviewJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext
{
}
