using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinDataTransfer = global::Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class DebugPage : Page
{
    private const string MxcSandboxExampleRelativePath = "tools\\mxc\\simple-sandbox-example.cjs";

    private HubWindow? _hub;

    private static readonly string LocalAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray");
    private static readonly string RoamingAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");
    private static readonly string LogPath = Path.Combine(LocalAppData, "openclaw-tray.log");
    private static readonly string DeviceKeyPath = Path.Combine(LocalAppData, "device-key-ed25519.json");

    public DebugPage() { InitializeComponent(); }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        LoadLog();
        LoadConnectionStatus();
        LoadDeviceIdentity();
    }

    // ── Log Viewer ───────────────────────────────────────────────────

    private void LoadLog()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                var lines = File.ReadLines(LogPath).TakeLast(100).ToArray();
                LogText.Text = string.Join("\n", lines);

                // Auto-scroll to bottom
                DispatcherQueue?.TryEnqueue(() =>
                {
                    LogScrollViewer.UpdateLayout();
                    LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
                });
            }
            else
            {
                LogText.Text = "No log file found.";
            }
        }
        catch (Exception ex)
        {
            LogText.Text = $"Failed to read log: {ex.Message}";
        }
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e) => LoadLog();

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(LogText.Text ?? "");
        WinDataTransfer.Clipboard.SetContent(dp);
    }

    // ── Connection Status ────────────────────────────────────────────

    private void LoadConnectionStatus()
    {
        if (_hub == null) return;

        var statusText = _hub.CurrentStatus switch
        {
            ConnectionStatus.Connected => "🟢 Connected",
            ConnectionStatus.Connecting => "🟡 Connecting",
            ConnectionStatus.Disconnected => "🔴 Disconnected",
            ConnectionStatus.Error => "❌ Error",
            _ => "Unknown"
        };
        OperatorStatusText.Text = statusText;

        GatewayUrlText.Text = _hub.Settings?.GetEffectiveGatewayUrl() ?? "—";
        NodeModeText.Text = _hub.Settings?.EnableNodeMode == true ? "Enabled" : "Disabled";
    }

    // ── Device Identity ──────────────────────────────────────────────

    private void LoadDeviceIdentity()
    {
        try
        {
            if (File.Exists(DeviceKeyPath))
            {
                var json = File.ReadAllText(DeviceKeyPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("deviceId", out var id))
                {
                    var deviceId = id.GetString() ?? "Unknown";
                    DeviceIdText.Text = deviceId.Length > 16 ? deviceId[..16] + "…" : deviceId;
                    DeviceIdText.Tag = deviceId; // store full ID for copy
                }
                else
                {
                    DeviceIdText.Text = "Not found";
                }

                if (doc.RootElement.TryGetProperty("publicKey", out var pk))
                    PublicKeyText.Text = pk.GetString() ?? "Unknown";
                else
                    PublicKeyText.Text = "Not found";
            }
            else
            {
                DeviceIdText.Text = "No device key file";
                PublicKeyText.Text = "—";
            }
        }
        catch (Exception ex)
        {
            DeviceIdText.Text = $"Error: {ex.Message}";
            PublicKeyText.Text = "—";
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        var fullId = DeviceIdText.Tag as string ?? DeviceIdText.Text;
        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(fullId);
        WinDataTransfer.Clipboard.SetContent(dp);
    }

    // ── Debug Actions ────────────────────────────────────────────────

    private void OnOpenLogFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogPath))
                Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RoamingAppData);
            Process.Start(new ProcessStartInfo(RoamingAppData) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LocalAppData);
            Process.Start(new ProcessStartInfo(LocalAppData) { UseShellExecute = true });
        }
        catch { }
    }

    private async void OnInvokeNodeCapabilityViaMxc(object sender, RoutedEventArgs e)
    {
        var nodeExecutable = FindNodeExecutable();
        if (string.IsNullOrWhiteSpace(nodeExecutable))
        {
            await ShowMessageDialogAsync(
                "MXC Sandbox Example",
                "Node.js executable was not found. Install Node.js or set OPENCLAW_NODE_EXEC.");
            return;
        }

        var scriptPath = FindMxcSandboxExampleScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            await ShowMessageDialogAsync(
                "MXC Sandbox Example",
                "Could not find tools/mxc/simple-sandbox-example.cjs from the current app location.");
            return;
        }

        var timeoutMs = 30000;
        string stdout;
        string stderr;
        int exitCode;

        try
        {
            (exitCode, stdout, stderr) = await ExecuteNodeScriptAsync(
                nodeExecutable,
                scriptPath,
                timeoutMs,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ShowMessageDialogAsync("MXC Sandbox Example Failed", ex.Message);
            return;
        }

        var outputText = new TextBlock
        {
            Text = BuildSandboxDialogText(exitCode, stdout, stderr),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };

        var outputScroll = new ScrollViewer
        {
            Content = outputText,
            MinHeight = 240,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var outputDialog = new ContentDialog
        {
            Title = "MXC Sandbox Example Result",
            Content = outputScroll,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };

        await outputDialog.ShowAsync();
    }

    private static string BuildSandboxDialogText(int processExitCode, string processStdout, string processStderr)
    {
        var sanitizedStdout = SanitizeForDialog(processStdout);
        var sanitizedStderr = SanitizeForDialog(processStderr);
        var outputBuilder = new StringBuilder();

        outputBuilder.AppendLine("MXC Sandbox Example");
        outputBuilder.AppendLine("===================");
        outputBuilder.AppendLine($"Node Process Exit Code: {processExitCode}");

        if (TryParseSandboxPayload(processStdout, out var payload))
        {
            outputBuilder.AppendLine($"Payload Parsed: Yes");
            outputBuilder.AppendLine($"Ran Inside MXC: {(payload.RanInsideMxc ? "Yes" : "No")}");
            outputBuilder.AppendLine($"Runner: {payload.Runner}");
            outputBuilder.AppendLine($"Sandbox Exit Code: {payload.ExitCode}");
            outputBuilder.AppendLine();
            outputBuilder.AppendLine("Executed Command In Sandbox:");
            outputBuilder.AppendLine(SanitizeForDialog(payload.ExecutedCommandInSandbox));
            outputBuilder.AppendLine();
            outputBuilder.AppendLine("Sandbox STDOUT:");
            outputBuilder.AppendLine(SanitizeForDialog(payload.Stdout));
            outputBuilder.AppendLine();
            outputBuilder.AppendLine("Sandbox STDERR:");
            outputBuilder.AppendLine(SanitizeForDialog(payload.Stderr));
            outputBuilder.AppendLine();
            outputBuilder.AppendLine("Raw Node STDERR:");
            outputBuilder.AppendLine(sanitizedStderr);
            return outputBuilder.ToString();
        }

        outputBuilder.AppendLine("Payload Parsed: No");
        outputBuilder.AppendLine();
        outputBuilder.AppendLine("Raw Node STDOUT:");
        outputBuilder.AppendLine(sanitizedStdout);
        outputBuilder.AppendLine();
        outputBuilder.AppendLine("Raw Node STDERR:");
        outputBuilder.AppendLine(sanitizedStderr);
        return outputBuilder.ToString();
    }

    private static bool TryParseSandboxPayload(string text, out SandboxPayload payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            payload = new SandboxPayload(
                root.TryGetProperty("ranInsideMxc", out var ranInsideMxc) && ranInsideMxc.GetBoolean(),
                root.TryGetProperty("runner", out var runner) ? (runner.GetString() ?? "unknown") : "unknown",
                root.TryGetProperty("executedCommandInSandbox", out var command) ? (command.GetString() ?? "") : "",
                root.TryGetProperty("exitCode", out var exitCode) ? exitCode.GetInt32() : -1,
                root.TryGetProperty("stdout", out var stdout) ? (stdout.GetString() ?? "") : "",
                root.TryGetProperty("stderr", out var stderr) ? (stderr.GetString() ?? "") : "");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeForDialog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var value = text.Replace("\r\n", "\n").Replace('\r', '\n');
        value = Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        value = Regex.Replace(value, "\\x1B\\][^\\a]*(\\a|\\x1B\\\\)", string.Empty);
        value = value.Replace("\u001b", string.Empty);

        var cleaned = new string(value.Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray());
        cleaned = Regex.Replace(cleaned, "^\\]0;.*$", string.Empty, RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "\\n{3,}", "\n\n");
        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? "(empty)" : cleaned;
    }

    private readonly record struct SandboxPayload(
        bool RanInsideMxc,
        string Runner,
        string ExecutedCommandInSandbox,
        int ExitCode,
        string Stdout,
        string Stderr);

    private static async Task<(int exitCode, string stdout, string stderr)> ExecuteNodeScriptAsync(
        string nodeExecutable,
        string scriptPath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = nodeExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
        };

        psi.ArgumentList.Add(scriptPath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static string? FindNodeExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("OPENCLAW_NODE_EXEC");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        return FindExecutableOnPath("node.exe") ?? FindExecutableOnPath("node");
    }

    private static string? FindMxcSandboxExampleScriptPath()
    {
        var probeRoots = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(DebugPage).Assembly.Location) ?? string.Empty,
        };

        foreach (var root in probeRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var current = new DirectoryInfo(root);
            for (var depth = 0; depth < 10 && current != null; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, MxcSandboxExampleRelativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindExecutableOnPath(string executableName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void OnCopySupportContext(object sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            $"Gateway URL: {GatewayUrlText.Text}",
            $"Status: {OperatorStatusText.Text}",
            $"Node Mode: {NodeModeText.Text}",
            $"Device ID: {DeviceIdText.Tag as string ?? DeviceIdText.Text}",
            $"Machine: {Environment.MachineName}",
            $"OS: {Environment.OSVersion}",
            $"Time: {DateTime.UtcNow:u}"
        };

        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(string.Join("\n", lines));
        WinDataTransfer.Clipboard.SetContent(dp);

        if (sender is Button btn)
        {
            btn.Content = "✓ Copied";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (t, a) => { btn.Content = "📋 Copy Support Context"; timer.Stop(); };
            timer.Start();
        }
    }

    private void OnRelaunchOnboarding(object sender, RoutedEventArgs e)
    {
        _hub?.OpenSetupAction?.Invoke();
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }
}
