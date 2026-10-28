using OpenClaw.Connection;
using OpenClawTray.Helpers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>Uses the normal Dashboard credential path without setup completion authority.</summary>
internal sealed class GatewayDashboardLauncher(
    Func<bool> ensureTunnel,
    Func<InteractiveGatewayCredential?> resolveCredential,
    Func<string, Task<bool>> launchBrowser,
    Action reportFailure,
    Action? reportOpened = null,
    Func<GatewayRecord?>? getActiveGateway = null,
    Func<GatewayRecord, CancellationToken, Task<GatewayEndpointProvenance>>? inspectWslOwner = null,
    IWslCommandRunner? wslCommands = null,
    TimeProvider? timeProvider = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";
    private static readonly TimeSpan BrowserHandoffLifetime = TimeSpan.FromMinutes(10);

    public async Task<bool> OpenAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ensureTunnel())
                throw new InvalidOperationException("The Gateway tunnel is unavailable.");
            var credential = resolveCredential()
                ?? throw new InvalidOperationException("The Gateway credential is unavailable.");
            var appendSharedToken = !credential.IsBootstrapToken &&
                credential.Source == CredentialResolver.SourceSharedGatewayToken;
            var url = GatewayDashboardUrlBuilder.Build(credential.GatewayUrl, path, credential.Token, appendSharedToken);
            var active = getActiveGateway?.Invoke();
            if (!appendSharedToken && active is not null &&
                GatewayRecordEditing.IsSetupManagedLocalRecord(active) &&
                GatewayRecordEditing.IsLoopbackEndpoint(active.Url))
            {
                var binding = GatewayDashboardBinding.Capture(active);
                void RequireCurrent()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = getActiveGateway?.Invoke();
                    if (current is null || GatewayDashboardBinding.Capture(current) != binding)
                        throw new InvalidOperationException("The selected Gateway changed.");
                }
                async Task<bool> IsOwnerReadyAsync()
                {
                    RequireCurrent();
                    var owner = inspectWslOwner is null ? GatewayEndpointProvenanceKind.UnknownListener :
                        (await inspectWslOwner(active, cancellationToken)).Kind;
                    RequireCurrent();
                    // A positively empty endpoint may be stopped/restarting. Open
                    // only the clean route so the browser can retain its own access.
                    return owner switch
                    {
                        GatewayEndpointProvenanceKind.ExpectedManagedGateway => true,
                        GatewayEndpointProvenanceKind.NoListener => false,
                        _ => throw new InvalidOperationException("The local Gateway owner is unavailable.")
                    };
                }

                if (!GatewayRecordEditing.AreEquivalentLoopbackEndpoints(active.Url, credential.GatewayUrl))
                    throw new InvalidOperationException("The local Gateway target is unavailable.");
                if (await IsOwnerReadyAsync() && wslCommands is not null)
                {
                    var result = await wslCommands.RunInDistroAsync(
                        GatewayRecordEditing.ResolveManagedDistroName(active)!, ["bash", "-s"], cancellationToken,
                        standardInput: BuildWslDashboardCommand(new Uri(active.Url).Port));
                    RequireCurrent();
                    var ownerReady = await IsOwnerReadyAsync();
                    // Issuance is an enhancement, not a prerequisite for an
                    // already-authorized browser. Never use legacy URL/token fields.
                    if (result.Success && !result.IsIndeterminate && !IsLegacyDashboardResponse(result.StandardOutput))
                    {
                        var handoff = BuildBrowserHandoffUrl(result.StandardOutput, active, path,
                            (timeProvider ?? TimeProvider.System).GetUtcNow());
                        if (ownerReady)
                            url = handoff;
                    }
                }
                RequireCurrent();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!await launchBrowser(url))
                throw new InvalidOperationException("Windows did not open the Dashboard.");
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            // Browser failures can include the full credential-bearing URL. Do not log them.
            reportFailure();
            return false;
        }
        reportOpened?.Invoke();
        return true;
    }

    // RunInDistroAsync has no output/diagnostics tee. Only fixed, secret-free argv
    // is logged. Use the verified service's environment, not the Windows user's CLI profile.
    internal static string BuildWslDashboardCommand(int port) => $$"""
        set -e
        pid=$(systemctl --user show openclaw-gateway -p MainPID --value 2>/dev/null)
        test "${pid:-0}" -gt 0
        ss -ltnp 2>/dev/null | grep -E ':{{port}}([^0-9]|$)' | grep -F "pid=$pid," >/dev/null
        environment=()
        owner_path=
        owner_home=
        while IFS= read -r -d '' entry; do
            environment+=("$entry")
            case "$entry" in
                PATH=*) owner_path=${entry#PATH=} ;;
                HOME=*) owner_home=${entry#HOME=} ;;
            esac
        done < "/proc/$pid/environ"
        test -n "$owner_path" && test -n "$owner_home"
        cd "/proc/$pid/cwd"
        cli=$(PATH="$owner_path" command -v openclaw || true)
        if test -z "$cli" || ! test -x "$cli"; then
            # Qualified locations installed/verified by InstallCliStep and WslConstants.
            for candidate in "$owner_home/.openclaw/bin/openclaw" /opt/openclaw/bin/openclaw /usr/local/bin/openclaw; do
                if test -x "$candidate"; then cli=$candidate; break; fi
            done
        fi
        test -n "$cli" && test -x "$cli"
        test "$pid" = "$(systemctl --user show openclaw-gateway -p MainPID --value 2>/dev/null)"
        ss -ltnp 2>/dev/null | grep -E ':{{port}}([^0-9]|$)' | grep -F "pid=$pid," >/dev/null
        # Restore with shell builtins: secret environment values must never become argv.
        for name in $(compgen -e); do unset "$name" 2>/dev/null || true; done
        for entry in "${environment[@]}"; do export "$entry"; done
        exec "$cli" dashboard --json
        """;

    private static bool IsLegacyDashboardResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                !root.TryGetProperty("browserUrl", out _) &&
                !root.TryGetProperty("browserBootstrapExpiresAtMs", out _);
        }
        catch (JsonException) { throw InvalidHandoff(); }
    }

    internal static string BuildBrowserHandoffUrl(string json, GatewayRecord target, string? path, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("browserBootstrapExpiresAtMs", out var expiry) ||
                !expiry.TryGetInt64(out var expiresAt) || expiresAt <= now.ToUnixTimeMilliseconds() ||
                expiresAt > now.Add(BrowserHandoffLifetime).ToUnixTimeMilliseconds() ||
                !root.TryGetProperty("port", out var port) || !port.TryGetInt32(out var selectedPort))
                throw InvalidHandoff();
            var ws = new Uri(root.GetProperty("wsUrl").GetString()!, UriKind.Absolute);
            var http = new Uri(root.GetProperty("httpUrl").GetString()!, UriKind.Absolute);
            var browser = new Uri(root.GetProperty("browserUrl").GetString()!, UriKind.Absolute);
            var selected = new Uri(target.Url, UriKind.Absolute);
            if (selectedPort != selected.Port || ws.Scheme is not ("ws" or "wss") ||
                http.Scheme is not ("http" or "https") ||
                !SameLocalEndpoint(ws, selected) || !SameLocalEndpoint(ws, http) ||
                browser.UserInfo.Length != 0 ||
                browser.GetLeftPart(UriPartial.Query) != http.GetLeftPart(UriPartial.Query))
                throw InvalidHandoff();
            var fields = browser.Fragment.TrimStart('#').Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal);
            if (fields.Count != 3 || !fields.TryGetValue("bootstrapToken", out var token) || token.Length == 0 ||
                !fields.TryGetValue("bootstrapProfile", out var profile) || profile != "owner" ||
                !fields.TryGetValue("gatewayUrl", out var gateway) ||
                !GatewayRecordEditing.AreEquivalentLoopbackEndpoints(gateway, ws.AbsoluteUri))
                throw InvalidHandoff();
            // Compose the route first, then retain the canonical bootstrap fragment intact.
            var route = GatewayDashboardUrlBuilder.Build(http.AbsoluteUri, path, null, false);
            return route + (route.Contains('#') ? "&" + browser.Fragment[1..] : browser.Fragment);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or
            ArgumentException or InvalidOperationException or FormatException)
        {
            // Neither parser errors nor CLI output may leave this secret boundary.
            throw InvalidHandoff();
        }
    }

    private static bool SameLocalEndpoint(Uri left, Uri right) =>
        left.UserInfo.Length == 0 && right.UserInfo.Length == 0 &&
        left.Query.Length == 0 && right.Query.Length == 0 &&
        left.Fragment.Length == 0 && right.Fragment.Length == 0 &&
        GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
            GatewayDashboardUrlBuilder.Build(left.AbsoluteUri, null, null, false),
            GatewayDashboardUrlBuilder.Build(right.AbsoluteUri, null, null, false));

    private static InvalidOperationException InvalidHandoff() =>
        new("The local Dashboard handoff was invalid or expired.");

    private static bool IsExpectedLaunchFailure(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception or COMException or OperationCanceledException or TimeoutException;
}
