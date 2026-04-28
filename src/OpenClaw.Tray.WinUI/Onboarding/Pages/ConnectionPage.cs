using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 1: Connection / Gateway Selection.
/// Lets users choose Local, Remote, or Configure Later,
/// enter gateway URL + token (or paste setup code),
/// toggle Node Mode, and performs a REAL WebSocket handshake
/// with Ed25519 device authentication.
/// </summary>
public sealed class ConnectionPage : Component<OnboardingState>
{
    private const string DefaultLocalUrl = "ws://localhost:18789";
    private const string DevLocalUrl = "ws://localhost:19001";

    // Cache the detected URL so we only probe once per app session
    private static string? s_detectedLocalUrl;

    /// <summary>
    /// Returns the detected local gateway URL. Does a fast synchronous TCP probe
    /// on common ports. Safe to call from Reactor Render() — no async/await.
    /// </summary>
    private static string GetDetectedLocalUrl()
    {
        if (s_detectedLocalUrl != null) return s_detectedLocalUrl;

        // Quick TCP port probe — much faster than HTTP health check
        foreach (var candidate in new[] { DefaultLocalUrl, DevLocalUrl })
        {
            try
            {
                var uri = new Uri(candidate);
                using var tcp = new System.Net.Sockets.TcpClient();
                var result = tcp.BeginConnect(uri.Host, uri.Port, null, null);
                var connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
                if (connected)
                {
                    try { tcp.EndConnect(result); } catch { /* connection refused */ }
                    if (tcp.Connected)
                    {
                        Logger.Info($"[Connection] Detected local gateway at {candidate}");
                        s_detectedLocalUrl = candidate;
                        return candidate;
                    }
                }
            }
            catch
            {
                // Port not reachable
            }
        }

        s_detectedLocalUrl = DefaultLocalUrl;
        return DefaultLocalUrl;
    }

    /// <summary>
    /// Probes common local gateway ports and returns the first reachable URL.
    /// Checks the default port (18789) first, then the dev port (19001).
    /// Uses a very short timeout for responsiveness.
    /// </summary>
    private static async Task<string> DetectLocalGatewayUrlAsync()
    {
        foreach (var candidate in new[] { DefaultLocalUrl, DevLocalUrl })
        {
            try
            {
                var uri = new Uri(candidate.Replace("ws://", "http://"));
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
                var response = await client.GetAsync($"{uri.GetLeftPart(UriPartial.Authority)}/health");
                if (response.IsSuccessStatusCode)
                {
                    Logger.Info($"[Connection] Detected local gateway at {candidate}");
                    return candidate;
                }
            }
            catch
            {
                // Port not reachable, try next
            }
        }
        return DefaultLocalUrl; // Fallback to default
    }

    public override Element Render()
    {
        var (mode, setMode) = UseState(Props.Mode);
        // For Local mode, use the detected gateway URL (probes 18789 and 19001)
        var initialUrl = Props.Mode == ConnectionMode.Local ? GetDetectedLocalUrl() : Props.Settings.GatewayUrl;
        var (url, setUrl) = UseState(initialUrl);
        var (token, setToken) = UseState(Props.Settings.Token);
        var (nodeMode, setNodeMode) = UseState(Props.Settings.EnableNodeMode);
        var (setupCode, setSetupCode) = UseState("");
        var detectedUrl = GetDetectedLocalUrl();
        var detectedMsg = detectedUrl != DefaultLocalUrl
            ? $"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusDetected")}"
            : LocalizationHelper.GetString("Onboarding_Connection_Ready");
        var (statusMsg, setStatusMsg) = UseState(Props.Mode == ConnectionMode.Local ? detectedMsg : LocalizationHelper.GetString("Onboarding_Connection_Ready"));
        var (testing, setTesting) = UseState(false);
        var (pairingDeviceId, setPairingDeviceId) = UseState("");
        var (pairingCommand, setPairingCommand) = UseState("");
        var (copied, setCopied) = UseState(false);

        var isLocal = LocalGatewayApprover.IsLocalGateway(url);

        void SelectMode(ConnectionMode m)
        {
            setMode(m);
            Props.Mode = m;
            Props.ConnectionTested = false;
            setStatusMsg("");
            setPairingDeviceId("");

            if (m == ConnectionMode.Local)
            {
                // Use cached detected URL (probed on first access)
                var detected = GetDetectedLocalUrl();
                setUrl(detected);
                Props.Settings.GatewayUrl = detected;
                if (detected != DefaultLocalUrl)
                    setStatusMsg($"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusDetected")}");
            }
        }

        void OnSetupCodeChanged(string code)
        {
            setSetupCode(code);
            if (string.IsNullOrWhiteSpace(code)) return;

            var result = SetupCodeDecoder.Decode(code);

            if (!result.Success)
            {
                // Not a valid setup code — user might be still typing
                if (code.Length > 2048)
                    Logger.Warn("[Connection] Setup code rejected: exceeds 2048 character limit");
                else
                    Logger.Debug($"[Connection] Setup code parse attempt failed: {result.Error}");
                return;
            }

            if (result.Url != null)
            {
                setUrl(result.Url);
                Props.Settings.GatewayUrl = result.Url;
            }
            if (result.Token != null)
            {
                setToken(result.Token);
                Props.Settings.Token = result.Token;
            }
            setStatusMsg($"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusDecoded")}");
        }

        void OnUrlChanged(string v)
        {
            setUrl(v);
            Props.Settings.GatewayUrl = v;
            Props.ConnectionTested = false;
            setStatusMsg("");
        }

        void OnTokenChanged(string v)
        {
            setToken(v);
            Props.Settings.Token = v;
            Props.ConnectionTested = false;
            setStatusMsg("");
        }

        void OnNodeModeToggled(bool v)
        {
            setNodeMode(v);
            Props.Settings.EnableNodeMode = v;
        }

        async void TestConnection()
        {
            Props.Settings.GatewayUrl = url;
            Props.Settings.Token = token;

            if (!GatewayUrlHelper.IsValidGatewayUrl(url))
            {
                setStatusMsg($"⚠️ {GatewayUrlHelper.ValidationMessage}");
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                setStatusMsg($"⚠️ {LocalizationHelper.GetString("Onboarding_Connection_StatusTokenRequired")}");
                return;
            }

            setTesting(true);
            setStatusMsg(LocalizationHelper.GetString("Onboarding_Connection_StatusConnecting"));
            setPairingDeviceId("");
            Props.ConnectionTested = false;

            try
            {
                // Phase 1: Quick HTTP health check — instant reachability feedback
                var healthResult = await GatewayHealthCheck.TestAsync(url, token);
                if (!healthResult.Success)
                {
                    Logger.Warn($"[Connection] Health check failed: {healthResult.Error}");
                    setStatusMsg($"❌ {healthResult.Error}");
                    setTesting(false);
                    return;
                }

                // Phase 2: Use App's PERSISTENT client (matching Mac app architecture)
                setStatusMsg($"🔄 {LocalizationHelper.GetString("Onboarding_Connection_StatusAuthenticating")}");
                Props.Settings.Save();

                var app = (App)Microsoft.UI.Xaml.Application.Current;

                // Reuse existing client if it already has a result, otherwise (re)initialize
                var existingClient = app.GatewayClient;
                if (existingClient == null ||
                    (!existingClient.IsConnectedToGateway && !existingClient.IsPairingRequired && !existingClient.IsAuthFailed))
                {
                    app.ReinitializeGatewayClient();
                }

                // Set Props.GatewayClient IMMEDIATELY so WizardPage can access it
                // even if still connecting (WizardPage will poll for Connected status)
                Props.GatewayClient = app.GatewayClient;

                // Poll for definitive auth result (V3→V2 fallback takes ~8s)
                bool connected = false;
                bool pairingRequired = false;
                bool authFailed = false;

                for (int attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(1000);
                    var client = app.GatewayClient;
                    Props.GatewayClient = client; // Keep in sync

                    if (client == null) continue;
                    if (client.IsConnectedToGateway) { connected = true; break; }
                    if (client.IsPairingRequired) { pairingRequired = true; break; }
                    if (client.IsAuthFailed) { authFailed = true; break; }
                }

                if (connected)
                {
                    setStatusMsg($"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusConnected")}");
                    Props.ConnectionTested = true;
                }
                else if (pairingRequired)
                {
                    var dataPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "OpenClawTray");
                    var identity = new DeviceIdentity(dataPath);
                    identity.Initialize();
                    var deviceId = identity.DeviceId;

                    setStatusMsg($"⏳ {LocalizationHelper.GetString("Onboarding_Connection_StatusPairing")}");
                    setPairingDeviceId(deviceId);
                    var cmd = $"cd ~/openclaw && npx openclaw devices approve {deviceId}";
                    setPairingCommand(cmd);
                    setCopied(false);
                }
                else if (authFailed)
                {
                    Logger.Warn("[Connection] Auth failed (all signature modes exhausted)");
                    setStatusMsg($"❌ {LocalizationHelper.GetString("Onboarding_Connection_StatusFailed")}");
                }
                else
                {
                    setStatusMsg($"❌ {LocalizationHelper.GetString("Onboarding_Connection_StatusTimeout")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Connection] Test exception: {ex}");
                setStatusMsg($"❌ {LocalizationHelper.GetString("Onboarding_Connection_StatusFailed")}");
            }
            finally
            {
                setTesting(false);
            }
        }

        var showFields = mode != ConnectionMode.Later;

        // Map mode to RadioButtons index
        var modeIndex = mode switch
        {
            ConnectionMode.Local => 0,
            ConnectionMode.Remote => 1,
            ConnectionMode.Later => 2,
            _ => 0
        };

        // Build the full status text for the always-visible status area
        var fullStatus = statusMsg;
        if (!string.IsNullOrEmpty(pairingDeviceId))
        {
            var shortId = pairingDeviceId.Length > 16 ? pairingDeviceId[..16] + "…" : pairingDeviceId;
            fullStatus += $"\nDevice ID: {shortId}";
            if (!isLocal)
                fullStatus += $"\n\n{LocalizationHelper.GetString("Onboarding_Connection_RunOnGateway")}\n" + pairingCommand;
        }

        var children = new List<Element>
        {
            // Title
            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Left)
        };

        // Build card content — RadioButtons always first, fields conditionally below
        var cardChildren = new List<Element>
        {
            // Mode selector inside card for consistent alignment
            RadioButtons(
                [$"🖥️ {LocalizationHelper.GetString("Onboarding_Connection_Local")}", $"🌐 {LocalizationHelper.GetString("Onboarding_Connection_Remote")}", $"⏭️ {LocalizationHelper.GetString("Onboarding_Connection_Later")}"],
                modeIndex,
                index =>
                {
                    var selected = index switch { 0 => ConnectionMode.Local, 1 => ConnectionMode.Remote, _ => ConnectionMode.Later };
                    SelectMode(selected);
                })
                .Set(rb => rb.MaxColumns = 3)
        };

        if (showFields)
        {
            // Setup code
            cardChildren.Add(
                TextField(setupCode, OnSetupCodeChanged,
                    placeholder: LocalizationHelper.GetString("Onboarding_Connection_SetupCodePlaceholder"),
                    header: LocalizationHelper.GetString("Onboarding_Connection_SetupCode"))
                    .OnGotFocus((sender, _) =>
                    {
                        if (sender is Microsoft.UI.Xaml.Controls.TextBox tb && string.IsNullOrEmpty(tb.Text))
                        {
                            try
                            {
                                var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                                if (content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                                {
                                    var task = content.GetTextAsync();
                                    task.Completed = (op, status) =>
                                    {
                                        if (status == global::Windows.Foundation.AsyncStatus.Completed)
                                        {
                                            var text = op.GetResults();
                                            tb.DispatcherQueue.TryEnqueue(() =>
                                            {
                                                tb.Text = text;
                                                OnSetupCodeChanged(text);
                                            });
                                        }
                                    };
                                }
                            }
                            catch { }
                        }
                    })
            );

            // Gateway URL
            cardChildren.Add(
                TextField(url, OnUrlChanged,
                    placeholder: "ws://host:port",
                    header: LocalizationHelper.GetString("Onboarding_Connection_GatewayUrl"))
                    .OnGotFocus((sender, _) =>
                    {
                        if (sender is Microsoft.UI.Xaml.Controls.TextBox tb)
                            tb.SelectAll();
                    })
                    .Set(tb => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tb, "OnboardingGatewayUrl"))
            );

            // Token
            cardChildren.Add(
                TextField(token, OnTokenChanged,
                    placeholder: LocalizationHelper.GetString("Onboarding_Connection_TokenPlaceholder"),
                    header: LocalizationHelper.GetString("Onboarding_Connection_Token"))
                    .Set(tb => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tb, "OnboardingToken"))
            );

            // Node Mode left + Test Connection right (same row via Grid)
            cardChildren.Add(
                Grid(["1*", "Auto"], ["Auto"],
                    HStack(4,
                        TextBlock(LocalizationHelper.GetString("Onboarding_Connection_NodeMode"))
                            .FontSize(14)
                            .VAlign(VerticalAlignment.Center),
                        TextBlock("\uE946")
                            .FontFamily("Segoe MDL2 Assets")
                            .FontSize(14)
                            .Opacity(0.5)
                            .VAlign(VerticalAlignment.Center)
                            .Set(tb => Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tb,
                                "Node Mode turns this PC into a remote compute node.\n" +
                                "The gateway can invoke screen capture, camera, system\n" +
                                "commands, and other capabilities on this machine.\n\n" +
                                "⚠️ This is a heavy hammer — it grants the gateway\n" +
                                "significant control over your PC. Only enable this if\n" +
                                "you trust the gateway operator and understand that\n" +
                                "remote commands will execute locally.\n\n" +
                                "Most users should leave this OFF (Operator mode)\n" +
                                "which only monitors and sends chat.")),
                        ToggleSwitch(nodeMode, OnNodeModeToggled)
                    ).Grid(row: 0, column: 0),
                    Button(LocalizationHelper.GetString("Onboarding_Connection_TestConnection"), TestConnection)
                        .Disabled(testing)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 1)
                        .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingTestConnection"))
                )
            );

            // Status display — always visible
            cardChildren.Add(
                Border(
                    TextBlock(string.IsNullOrEmpty(fullStatus) ? LocalizationHelper.GetString("Onboarding_Connection_Ready") : fullStatus)
                        .FontSize(12)
                        .TextWrapping()
                        .Padding(8)
                )
                .MinHeight(40)
                .CornerRadius(4)
                .Background("#EBEBEB")
            );
        }
        else
        {
            cardChildren.Add(
                TextBlock(LocalizationHelper.GetString("Onboarding_Connection_ConfigureLaterMsg"))
                    .FontSize(13)
                    .Opacity(0.6)
                    .TextWrapping()
                    .Margin(0, 8, 0, 0)
            );
        }

        // Card wrapper — always shown, contains RadioButtons + config fields
        children.Add(
            Border(
                VStack(8, cardChildren.ToArray()).Padding(12)
            )
            .CornerRadius(8)
            .Background("#F5F5F5")
            .Margin(0, 4, 0, 0)
        );

        if (showFields)
        {
            // Approval action (conditional — only when pairing needed, outside card)
            if (!string.IsNullOrEmpty(pairingDeviceId))
            {
                if (isLocal)
                {
                    // Local gateway: "Approve Connection" button
                    children.Add(
                        Button(LocalizationHelper.GetString("Onboarding_Connection_Approve"), async () =>
                        {
                            var (success, message) = LocalGatewayApprover.ApproveDevice(pairingDeviceId);
                            if (success)
                            {
                                setStatusMsg($"🔄 {LocalizationHelper.GetString("Onboarding_Connection_StatusAuthenticating")}");
                                setPairingDeviceId("");
                                setPairingCommand("");

                                // Reconnect the EXISTING client (no new client, no V3→V2 repeat)
                                var app = (App)Microsoft.UI.Xaml.Application.Current;
                                var client = app.GatewayClient;
                                if (client != null)
                                {
                                    client.ReconnectAfterApproval();

                                    // Poll for Connected (should be fast — no signature renegotiation)
                                    for (int i = 0; i < 20; i++)
                                    {
                                        await Task.Delay(1000);
                                        if (client.IsConnectedToGateway)
                                        {
                                            setStatusMsg($"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusConnected")}");
                                            Props.ConnectionTested = true;
                                            Props.GatewayClient = client;
                                            setTesting(false);
                                            return;
                                        }
                                    }
                                }
                                setStatusMsg($"❌ {LocalizationHelper.GetString("Onboarding_Connection_StatusTimeout")}");
                            }
                            else
                            {
                                setStatusMsg($"❌ {message}");
                            }
                            setTesting(false);
                        })
                        .HAlign(HorizontalAlignment.Stretch)
                        .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingApprove"))
                    );
                }
                else
                {
                    // Remote gateway: copyable CLI command
                    children.Add(
                        Button(
                            VStack(2,
                                TextBlock(pairingCommand)
                                    .FontSize(11)
                                    .FontFamily("Consolas")
                                    .TextWrapping(),
                                TextBlock(copied ? $"✅ {LocalizationHelper.GetString("Onboarding_Connection_Copied")}" : LocalizationHelper.GetString("Onboarding_Connection_ClickToCopy"))
                                    .FontSize(11)
                                    .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                                    .Opacity(copied ? 1.0 : 0.6)
                            ),
                            () =>
                            {
                                var dp = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
                                dp.SetText(pairingCommand);
                                global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                                setCopied(true);
                            })
                            .HAlign(HorizontalAlignment.Stretch)
                    );
                }
            }
        }

        return VStack(8, children.ToArray())
            .MaxWidth(460)
            .Padding(0, 12, 0, 0);
    }

    /// <summary>
    /// Lightweight logger that captures the first and last error/warning for UI display.
    /// Preserves the first error so reconnect noise doesn't overwrite the real cause.
    /// </summary>
    private sealed class ConnectionTestLogger : IOpenClawLogger
    {
        /// <summary>The first error captured — preserves the original cause.</summary>
        public string? FirstError { get; private set; }
        public string? LastError { get; private set; }
        public string? LastWarn { get; private set; }

        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message)
        {
            LastWarn = message;
            FirstError ??= message;
            LastError ??= message;
        }
        public void Error(string message, Exception? ex = null)
        {
            FirstError ??= message;
            LastError = message;
        }
    }
}
