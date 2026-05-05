using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

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
    private const string DefaultLocalUrl = ConnectionPageModeSelector.DefaultLocalUrl;
    private const string DevLocalUrl = ConnectionPageModeSelector.DevLocalUrl;
    private const string VisualTestPairingDeviceId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Cache the detected URL so we only probe once per app session
    private static string? s_detectedLocalUrl;

    /// <summary>
    /// Returns the detected local gateway URL. Does a fast synchronous TCP probe
    /// on common ports. Safe to call from Render() — no async/await.
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

    private static string GetVisualTestPairingDeviceId() =>
        Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_PAIRING") == "1"
            ? VisualTestPairingDeviceId
            : "";

    public override Element Render()
    {
        var visualPairingDeviceId = GetVisualTestPairingDeviceId();
        var (mode, setMode) = UseState(Props.Mode);
        // For Local mode, use the detected gateway URL (probes 18789 and 19001)
        var initialUrl = ConnectionPageModeSelector.GetInitialUrl(
            Props.Mode,
            Props.Settings.GatewayUrl,
            Props.Settings.SshTunnelLocalPort,
            GetDetectedLocalUrl);
        var (url, setUrl) = UseState(initialUrl);
        var (token, setToken) = UseState(Props.Settings.Token);
        var (nodeMode, setNodeMode) = UseState(Props.Settings.EnableNodeMode);
        var (setupCode, setSetupCode) = UseState("");

        // SSH tunnel state — bound to Props.Settings.SshTunnel*
        var (useSshTunnel, setUseSshTunnel) = UseState(Props.Mode == ConnectionMode.Ssh || Props.Settings.UseSshTunnel);
        var (sshUser, setSshUser)           = UseState(Props.Settings.SshTunnelUser ?? "");
        var (sshHost, setSshHost)           = UseState(Props.Settings.SshTunnelHost ?? "");
        var (sshRemotePort, setSshRemotePort) = UseState(Props.Settings.SshTunnelRemotePort > 0 ? Props.Settings.SshTunnelRemotePort : 18789);
        var (sshLocalPort, setSshLocalPort)   = UseState(Props.Settings.SshTunnelLocalPort  > 0 ? Props.Settings.SshTunnelLocalPort  : 18789);

        var detectedUrl = GetDetectedLocalUrl();
        var detectedMsg = detectedUrl != DefaultLocalUrl
            ? $"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusDetected")}"
            : LocalizationHelper.GetString("Onboarding_Connection_Ready");
        var (statusMsg, setStatusMsg) = UseState(Props.Mode == ConnectionMode.Local ? detectedMsg : LocalizationHelper.GetString("Onboarding_Connection_Ready"));
        var (testing, setTesting) = UseState(false);
        var (pairingDeviceId, setPairingDeviceId) = UseState(visualPairingDeviceId);
        var (pairingCommand, setPairingCommand) = UseState(string.IsNullOrEmpty(visualPairingDeviceId) ? "" : App.BuildPairingApprovalCommand(visualPairingDeviceId));
        var (copied, setCopied) = UseState(!string.IsNullOrEmpty(visualPairingDeviceId));
        var (copyFailed, setCopyFailed) = UseState(false);

        var urlReadOnly = ConnectionPageModeSelector.IsGatewayUrlReadOnly(mode); // Ssh mode pins the local-forward URL

        void SelectMode(ConnectionMode m)
        {
            var result = ConnectionPageModeSelector.SelectMode(
                m,
                url,
                GetDetectedLocalUrl(),
                sshLocalPort,
                $"✅ {LocalizationHelper.GetString("Onboarding_Connection_StatusDetected")}",
                LocalizationHelper.GetString("Onboarding_Connection_LaterStatus"));

            setMode(m);
            Props.Mode = m;
            Props.ConnectionTested = result.ConnectionTested;
            setStatusMsg(result.StatusMessage);
            setPairingDeviceId(result.PairingDeviceId);
            setPairingCommand("");
            setCopied(false);
            setCopyFailed(false);
            setUseSshTunnel(result.UseSshTunnel);
            Props.Settings.UseSshTunnel = result.UseSshTunnel;

            if (result.UpdateGatewayUrl)
            {
                setUrl(result.Url);
                Props.Settings.GatewayUrl = result.Url;
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
                Props.Settings.BootstrapToken = result.Token;
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
            Props.Settings.BootstrapToken = "";
            Props.ConnectionTested = false;
            setStatusMsg("");
        }

        void OnNodeModeToggled(bool v)
        {
            setNodeMode(v);
            Props.Settings.EnableNodeMode = v;
        }

        bool TryCopyPairingCommand(string command)
        {
            try
            {
                App.CopyTextToClipboard(command);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Connection] Failed to copy pairing command: {ex.Message}");
                return false;
            }
        }

        async void TestConnection()
        {
            Props.Settings.GatewayUrl = url;
            Props.Settings.Token = token;

            // When SSH mode, start the managed tunnel before health-checking the local URL.
            if (mode == ConnectionMode.Ssh)
            {
                if (string.IsNullOrWhiteSpace(sshUser))
                {
                    setStatusMsg($"⚠️ {LocalizationHelper.GetString("Onboarding_Connection_SshUserInvalid")}");
                    return;
                }
                if (string.IsNullOrWhiteSpace(sshHost))
                {
                    setStatusMsg($"⚠️ {LocalizationHelper.GetString("Onboarding_Connection_SshHostInvalid")}");
                    return;
                }
                Props.Settings.UseSshTunnel       = true;
                Props.Settings.SshTunnelUser      = sshUser;
                Props.Settings.SshTunnelHost      = sshHost;
                Props.Settings.SshTunnelRemotePort = sshRemotePort;
                Props.Settings.SshTunnelLocalPort  = sshLocalPort;
                Props.Settings.Save();
                try
                {
                    ((App)Microsoft.UI.Xaml.Application.Current).EnsureSshTunnelStarted();
                    // Give the tunnel a brief moment to bind the local port before the health probe.
                    await Task.Delay(800);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Connection] SSH tunnel start failed: {ex.Message}");
                    setStatusMsg($"❌ {ex.Message}");
                    return;
                }
            }
            else
            {
                Props.Settings.UseSshTunnel = false;
            }

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
            setPairingCommand("");
            setCopied(false);
            setCopyFailed(false);
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
                    var useBootstrapHandoffAuth =
                        !string.IsNullOrWhiteSpace(Props.Settings.BootstrapToken) &&
                        string.Equals(token, Props.Settings.BootstrapToken, StringComparison.Ordinal);
                    app.ReinitializeGatewayClient(useBootstrapHandoffAuth);
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
                    var cmd = App.BuildPairingApprovalCommand(deviceId);
                    setPairingCommand(cmd);
                    var commandCopied = TryCopyPairingCommand(cmd);
                    setCopied(commandCopied);
                    setCopyFailed(!commandCopied);
                    app.ShowPairingPendingNotification(deviceId, cmd);
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

        var showFields = ConnectionPageModeSelector.ShouldShowConnectionFields(mode);

        // Build the full status text for the always-visible status area
        var fullStatus = statusMsg;
        if (!string.IsNullOrEmpty(pairingDeviceId))
        {
            var shortId = pairingDeviceId.Length > 16 ? pairingDeviceId[..16] + "…" : pairingDeviceId;
            fullStatus += $"\nDevice ID: {shortId}";
            fullStatus += $"\n\n{LocalizationHelper.GetString("Onboarding_Connection_RunOnGateway")}\n" + pairingCommand;
            fullStatus += copied
                ? $"\n{LocalizationHelper.GetString("Onboarding_Connection_Copied")}"
                : copyFailed
                    ? $"\n{LocalizationHelper.GetString("Onboarding_Connection_CopyFailed")}"
                    : "";
        }

        var children = new List<Element>
        {
            // Title
            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Left)
        };

        static string ModeAutomationId(ConnectionMode option) => option switch
        {
            ConnectionMode.Local => "OnboardingConnectionModeLocal",
            ConnectionMode.Ssh => "OnboardingConnectionModeSsh",
            ConnectionMode.Wsl => "OnboardingConnectionModeWsl",
            ConnectionMode.Later => "OnboardingConnectionModeLater",
            ConnectionMode.Remote => "OnboardingConnectionModeRemote",
            _ => "OnboardingConnectionModeUnknown"
        };

        Element ModeOption(ConnectionMode option, string label) =>
            RadioButton(label, mode == option, isChecked =>
                {
                    if (isChecked)
                        SelectMode(option);
                },
                groupName: "connection-mode")
                .Set(rb => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(rb, ModeAutomationId(option)));

        // Build card content — mode selector always first, fields conditionally below
        var cardChildren = new List<Element>
        {
            Grid(["1*", "1*"], ["Auto", "Auto", "Auto"],
                ModeOption(ConnectionMode.Local, $"🖥️ {LocalizationHelper.GetString("Onboarding_Connection_Local")}").Grid(0, 0),
                ModeOption(ConnectionMode.Ssh, $"🔐 {LocalizationHelper.GetString("Onboarding_Connection_Ssh")}").Grid(0, 1),
                ModeOption(ConnectionMode.Wsl, $"🐧 {LocalizationHelper.GetString("Onboarding_Connection_Wsl")}").Grid(1, 0),
                ModeOption(ConnectionMode.Later, $"⏭️ {LocalizationHelper.GetString("Onboarding_Connection_Later")}").Grid(1, 1),
                ModeOption(ConnectionMode.Remote, $"🌐 {LocalizationHelper.GetString("Onboarding_Connection_Remote")}").Grid(2, 0))
        };

        if (showFields)
        {
            // QR import handler — uses Helpers.QrSetupCodeReader on a stream from FileOpenPicker
            async void ImportQrFromFile()
            {
                try
                {
                    var picker = new FileOpenPicker();
                    var hwnd = ((App)Microsoft.UI.Xaml.Application.Current).GetOnboardingWindowHandle();
                    if (hwnd != IntPtr.Zero)
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                    picker.FileTypeFilter.Add(".png");
                    picker.FileTypeFilter.Add(".jpg");
                    picker.FileTypeFilter.Add(".jpeg");
                    picker.FileTypeFilter.Add(".bmp");
                    picker.FileTypeFilter.Add(".gif");

                    var file = await picker.PickSingleFileAsync();
                    if (file == null) return;

                    using var ras = await file.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var decoded = QrSetupCodeReader.Decode(stream);
                    if (string.IsNullOrWhiteSpace(decoded))
                    {
                        setStatusMsg($"⚠️ {LocalizationHelper.GetString("Onboarding_Connection_QrDecodeFailed")}");
                        return;
                    }
                    setSetupCode(decoded);
                    OnSetupCodeChanged(decoded);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Connection] QR import failed: {ex.Message}");
                    setStatusMsg($"⚠️ {LocalizationHelper.GetString("Onboarding_Connection_QrDecodeFailed")}");
                }
            }

            void PasteSetupCode()
            {
                try
                {
                    var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (!content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                        return;
                    var task = content.GetTextAsync();
                    task.Completed = (op, status) =>
                    {
                        if (status != global::Windows.Foundation.AsyncStatus.Completed) return;
                        var text = op.GetResults();
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
                        {
                            setSetupCode(text);
                            OnSetupCodeChanged(text);
                        });
                    };
                }
                catch { /* clipboard unavailable — ignore */ }
            }

            // Setup code row: TextField + Paste + QR buttons (Grid keeps the field expanding)
            cardChildren.Add(
                Grid(["1*", "Auto", "Auto"], ["Auto"],
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
                        .Grid(row: 0, column: 0),
                    Button(LocalizationHelper.GetString("Onboarding_Connection_PasteSetup"), PasteSetupCode)
                        .VAlign(VerticalAlignment.Bottom)
                        .Margin(6, 0, 0, 0)
                        .Grid(row: 0, column: 1)
                        .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingPasteSetupCode")),
                    Button(LocalizationHelper.GetString("Onboarding_Connection_QrButton"), ImportQrFromFile)
                        .VAlign(VerticalAlignment.Bottom)
                        .Margin(6, 0, 0, 0)
                        .Grid(row: 0, column: 2)
                        .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingImportQr"))
                )
            );

            // Gateway URL — read-only when SSH (the local-forward URL is fixed)
            cardChildren.Add(
                TextField(url, OnUrlChanged,
                    placeholder: "ws://host:port",
                    header: LocalizationHelper.GetString("Onboarding_Connection_GatewayUrl"))
                    .ReadOnly(urlReadOnly)
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

            // ── SSH panel (only shown when mode == Ssh) ─────────────────────
            if (mode == ConnectionMode.Ssh)
            {
                void OnSshUserChanged(string v)
                {
                    setSshUser(v);
                    Props.Settings.SshTunnelUser = v;
                }
                void OnSshHostChanged(string v)
                {
                    setSshHost(v);
                    Props.Settings.SshTunnelHost = v;
                }
                void OnSshRemotePortChanged(string v)
                {
                    if (int.TryParse(v, out var p) && p > 0 && p <= 65535)
                    {
                        setSshRemotePort(p);
                        Props.Settings.SshTunnelRemotePort = p;
                    }
                }
                void OnSshLocalPortChanged(string v)
                {
                    if (int.TryParse(v, out var p) && p > 0 && p <= 65535)
                    {
                        setSshLocalPort(p);
                        Props.Settings.SshTunnelLocalPort = p;
                        var sshUrl = $"ws://127.0.0.1:{p}";
                        setUrl(sshUrl);
                        Props.Settings.GatewayUrl = sshUrl;
                    }
                }

                // Live `ssh ...` preview — defensively wrapped so an in-progress invalid host
                // doesn't break the entire render pass.
                string sshPreview;
                try
                {
                    var args = SshTunnelCommandLine.BuildArguments(
                        sshUser, sshHost, sshRemotePort, sshLocalPort,
                        includeBrowserProxyForward: true);
                    sshPreview = $"ssh {args}";
                }
                catch
                {
                    sshPreview = "ssh -N -L <port>:127.0.0.1:<port> user@host";
                }

                cardChildren.Add(
                    Border(
                        VStack(8,
                            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_SshHint"))
                                .FontSize(12)
                                .Opacity(0.7)
                                .TextWrapping(),
                            Grid(["1*", "1*"], ["Auto", "Auto"],
                                TextField(sshUser, OnSshUserChanged,
                                    placeholder: "user",
                                    header: LocalizationHelper.GetString("Onboarding_Connection_SshUser"))
                                    .Grid(row: 0, column: 0)
                                    .Set(tb => { tb.Margin = new Thickness(0, 0, 4, 0); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tb, "OnboardingSshUser"); }),
                                TextField(sshHost, OnSshHostChanged,
                                    placeholder: "mac-studio.local",
                                    header: LocalizationHelper.GetString("Onboarding_Connection_SshHost"))
                                    .Grid(row: 0, column: 1)
                                    .Set(tb => { tb.Margin = new Thickness(4, 0, 0, 0); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tb, "OnboardingSshHost"); }),
                                TextField(sshRemotePort.ToString(), OnSshRemotePortChanged,
                                    placeholder: "18789",
                                    header: LocalizationHelper.GetString("Onboarding_Connection_SshRemotePort"))
                                    .Grid(row: 1, column: 0)
                                    .Set(tb => { tb.Margin = new Thickness(0, 8, 4, 0); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tb, "OnboardingSshRemotePort"); }),
                                TextField(sshLocalPort.ToString(), OnSshLocalPortChanged,
                                    placeholder: "18789",
                                    header: LocalizationHelper.GetString("Onboarding_Connection_SshLocalPort"))
                                    .Grid(row: 1, column: 1)
                                    .Set(tb => { tb.Margin = new Thickness(4, 8, 0, 0); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tb, "OnboardingSshLocalPort"); })
                            ),
                            VStack(2,
                                TextBlock(LocalizationHelper.GetString("Onboarding_Connection_SshPreviewLabel"))
                                    .FontSize(11)
                                    .Opacity(0.6),
                                TextBlock(sshPreview)
                                    .FontSize(11)
                                    .FontFamily("Consolas")
                                    .TextWrapping()
                            )
                        ).Padding(12)
                    )
                    .CornerRadius(6)
                    .BackgroundResource("CardBackgroundFillColorDefaultBrush")
                );
            }

            // Topology detection line — defensive against transient invalid SSH host/ports
            string topologyText;
            try
            {
                var info = GatewayTopologyClassifier.Classify(
                    url,
                    useSshTunnel: mode == ConnectionMode.Ssh,
                    sshHost: sshHost,
                    sshLocalPort: sshLocalPort,
                    sshRemotePort: sshRemotePort);
                var summary = string.IsNullOrEmpty(info.Detail)
                    ? $"{info.DisplayName} · {info.Transport} · {info.Host}"
                    : $"{info.DisplayName} · {info.Transport} · {info.Detail}";
                topologyText = string.Format(
                    LocalizationHelper.GetString("Onboarding_Connection_TopologyDetectedFmt"),
                    summary);
            }
            catch
            {
                topologyText = string.Empty;
            }
            if (!string.IsNullOrEmpty(topologyText))
            {
                cardChildren.Add(
                    TextBlock("● " + topologyText)
                        .FontSize(12)
                        .Opacity(0.75)
                        .TextWrapping()
                );
            }

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
                            .Set(ts => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(ts, "OnboardingNodeMode"))
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
                .BackgroundResource("CardBackgroundFillColorDefaultBrush")
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
            .BackgroundResource("CardBackgroundFillColorDefaultBrush")
            .Margin(0, 4, 0, 0)
        );

        if (showFields)
        {
            if (!string.IsNullOrEmpty(pairingDeviceId))
            {
                children.Add(
                    Button(
                        VStack(2,
                            TextBlock(pairingCommand)
                                .FontSize(11)
                                .FontFamily("Consolas")
                                .TextWrapping(),
                            TextBlock(copied
                                    ? $"✅ {LocalizationHelper.GetString("Onboarding_Connection_Copied")}"
                                    : copyFailed
                                        ? $"⚠️ {LocalizationHelper.GetString("Onboarding_Connection_CopyFailed")}"
                                        : LocalizationHelper.GetString("Onboarding_Connection_ClickToCopy"))
                                .FontSize(11)
                                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                                .Opacity(copied ? 1.0 : 0.7)
                        ),
                        () =>
                        {
                            var commandCopied = TryCopyPairingCommand(pairingCommand);
                            setCopied(commandCopied);
                            setCopyFailed(!commandCopied);
                        })
                        .HAlign(HorizontalAlignment.Stretch)
                        .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingCopyPairingCommand"))
                );
            }
        }

        return ScrollView(
            VStack(8, children.ToArray())
                .MaxWidth(460)
                .Padding(0, 12, 0, 12)
        );
    }
}
