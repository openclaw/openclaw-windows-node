using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Owns one isolated OpenClaw process for the accessibility test collection.
/// Navigation is sent through the same deep-link IPC path used by installed apps.
/// </summary>
public sealed class AccessibilityAppFixture : IDisposable
{
    private const int ShowMaximized = 3;
    private const int VirtualScreenLeft = 76;
    private const int VirtualScreenTop = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeepLinkTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NavigationSettleTime = TimeSpan.FromMilliseconds(1_000);

    private readonly string _dataDirectory;
    private readonly string _executablePath;
    private readonly Process _process;

    public IntPtr HubWindowHandle { get; }

    public AccessibilityAppFixture()
    {
        _executablePath = Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe");
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException(
                "The real tray executable was not copied beside the UI test assembly.",
                _executablePath);
        }

        _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"OpenClaw.Tray.Axe.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(
            Path.Combine(_dataDirectory, "settings.json"),
            """
            {
              "SettingsSchemaVersion": 1,
              "EnableMcpServer": true,
              "GlobalHotkeyEnabled": false,
              "AutoStart": false
            }
            """);

        _process = StartProcess($"{OpenClawTray.AppIdentity.ProtocolScheme}://hub/connection");
        HubWindowHandle = WaitForHubWindow();
        AxeHelper.Initialize(_process.Id);
    }

    public async Task NavigateAsync(string pageTag, string pageMarkerAutomationId)
    {
        EnsureTargetIsAlive();

        using var sender = StartProcess($"{OpenClawTray.AppIdentity.ProtocolScheme}://hub/{pageTag}");
        using var timeout = new CancellationTokenSource(DeepLinkTimeout);
        try
        {
            await sender.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!sender.HasExited)
                sender.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Timed out forwarding the '{pageTag}' deep link to the accessibility app.");
        }

        EnsureTargetIsAlive();
        await WaitForPageMarkerAsync(pageTag, pageMarkerAutomationId);
    }

    public string? CaptureHubScreenshotIfRequested()
    {
        var configuredPath = Environment.GetEnvironmentVariable("OPENCLAW_UI_SCREENSHOT_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        EnsureTargetIsAlive();
        var foreground = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            _ = ShowWindow(HubWindowHandle, ShowMaximized);
            _ = BringWindowToTop(HubWindowHandle);
            _ = SetForegroundWindow(HubWindowHandle);
            if (GetForegroundWindow() == HubWindowHandle)
            {
                foreground = true;
                break;
            }
            Thread.Sleep(100);
        }
        if (!foreground)
            throw new InvalidOperationException("Could not foreground the Hub window for screenshot capture.");
        Thread.Sleep(500);

        var bounds = AutomationElement.FromHandle(HubWindowHandle).Current.BoundingRectangle;
        var screenLeft = GetSystemMetrics(VirtualScreenLeft);
        var screenTop = GetSystemMetrics(VirtualScreenTop);
        var screenRight = screenLeft + GetSystemMetrics(VirtualScreenWidth);
        var screenBottom = screenTop + GetSystemMetrics(VirtualScreenHeight);
        var left = Math.Max(screenLeft, (int)Math.Floor(bounds.Left));
        var top = Math.Max(screenTop, (int)Math.Floor(bounds.Top));
        var right = Math.Min(screenRight, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(screenBottom, (int)Math.Ceiling(bounds.Bottom));
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Hub screenshot bounds were invalid: {width}x{height}.");

        var path = Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                left,
                top,
                0,
                0,
                new Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        var sampledColors = new HashSet<int>();
        var stepX = Math.Max(1, width / 32);
        var stepY = Math.Max(1, height / 32);
        for (var y = 0; y < height && sampledColors.Count < 8; y += stepY)
        {
            for (var x = 0; x < width && sampledColors.Count < 8; x += stepX)
                sampledColors.Add(bitmap.GetPixel(x, y).ToArgb());
        }
        if (sampledColors.Count < 3)
            throw new InvalidOperationException("Hub screenshot capture was blank or near-uniform.");

        bitmap.Save(path, ImageFormat.Png);

        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException("Hub screenshot capture produced an empty file.");
        return path;
    }

    private async Task WaitForPageMarkerAsync(string pageTag, string automationId)
    {
        var stopwatch = Stopwatch.StartNew();
        var condition = new PropertyCondition(
            AutomationElement.AutomationIdProperty,
            automationId);

        while (stopwatch.Elapsed < NavigationTimeout)
        {
            EnsureTargetIsAlive();
            var hub = AutomationElement.FromHandle(HubWindowHandle);
            if (hub.FindFirst(TreeScope.Descendants, condition) != null)
                return;

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"The '{pageTag}' page did not expose its '{automationId}' marker " +
            $"within {NavigationTimeout.TotalSeconds:0} seconds.");
    }

    private Process StartProcess(string deepLink)
    {
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(deepLink);
        startInfo.Environment["OPENCLAW_TRAY_DATA_DIR"] = _dataDirectory;
        startInfo.Environment["OPENCLAW_SKIP_UPDATE_CHECK"] = "1";
        startInfo.Environment["OPENCLAW_FORCE_ONBOARDING"] = "0";
        startInfo.Environment["OPENCLAW_LANGUAGE"] = "en-US";
        startInfo.Environment["OPENCLAW_ACCESSIBILITY_TEST_CHAT"] = "1";
        startInfo.Environment["OPENCLAW_ACCESSIBILITY_TEST_SESSIONS"] = "1";

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the OpenClaw tray executable.");
    }

    private IntPtr WaitForHubWindow()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StartupTimeout)
        {
            EnsureTargetIsAlive();
            _process.Refresh();
            if (_process.MainWindowHandle != IntPtr.Zero)
            {
                Thread.Sleep(NavigationSettleTime);
                EnsureTargetIsAlive();
                _process.Refresh();
                if (_process.MainWindowHandle != IntPtr.Zero)
                {
                    _ = ShowWindow(_process.MainWindowHandle, ShowMaximized);
                    Thread.Sleep(NavigationSettleTime);
                    return _process.MainWindowHandle;
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            $"OpenClaw did not expose its Hub window within {StartupTimeout.TotalSeconds:0} seconds.");
    }

    private void EnsureTargetIsAlive()
    {
        if (!_process.HasExited)
            return;

        var crashLogPath = Path.Combine(_dataDirectory, "crash.log");
        var crashLog = File.Exists(crashLogPath)
            ? $" Crash log: {File.ReadAllText(crashLogPath)}"
            : string.Empty;
        throw new InvalidOperationException(
            $"OpenClaw exited unexpectedly with code {_process.ExitCode}.{crashLog}");
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }
        _process.Dispose();

        // slopwatch-ignore: SW003 Test-owned temporary data cleanup is best-effort after process teardown.
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll")]
    private static extern int ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
