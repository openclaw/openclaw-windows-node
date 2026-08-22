using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public enum GatewayTailscaleAuthUpgradeOutcome
{
    Succeeded,
    AlreadyEnabled,
    Ineligible,
    NotActive,
    NotConnected,
    MissingConfigScope,
    ConfigUnavailable,
    PatchRejected,
    PersistenceFailed,
}

public sealed record GatewayTailscaleAuthUpgradeResult(
    GatewayTailscaleAuthUpgradeOutcome Outcome,
    string? Error = null)
{
    public bool IsSuccess => Outcome is
        GatewayTailscaleAuthUpgradeOutcome.Succeeded or
        GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled;
}

public static class GatewayTailscaleAuthUpgradePolicy
{
    public static bool IsEligible(GatewayRecord? record)
    {
        if (record is null ||
            !record.IsLocal ||
            record.SshTunnel is not null ||
            GatewayRecordEditing.ResolveManagedDistroName(record) is null)
        {
            return false;
        }

        // Topology only limits where the opt-in is offered; confirmation and a successful Core patch grant trust.
        return GatewayTopologyClassifier.Classify(record.Url, useSshTunnel: false).DetectedKind ==
            GatewayKind.Tailscale;
    }

    public static bool CanOffer(GatewayRecord? record) =>
        record?.TrustTailscaleAuth != true && IsEligible(record);
}

internal interface IGatewayTailscaleAuthConfigClient
{
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }
    Task<JsonElement> RequestConfigDetailedAsync(int timeoutMs = 15000);
    Task<ConfigPatchResult> PatchConfigDetailedAsync(
        JsonElement fullConfig,
        string? baseHash,
        int timeoutMs = 15000);
}

internal sealed class GatewayTailscaleAuthConfigClientAdapter(IOperatorGatewayClient client)
    : IGatewayTailscaleAuthConfigClient
{
    public IReadOnlyList<string> GrantedOperatorScopes => client.GrantedOperatorScopes;
    public bool IsConnectedToGateway => client.IsConnectedToGateway;
    public Task<JsonElement> RequestConfigDetailedAsync(int timeoutMs = 15000) =>
        client.RequestConfigDetailedAsync(timeoutMs);
    public Task<ConfigPatchResult> PatchConfigDetailedAsync(
        JsonElement fullConfig,
        string? baseHash,
        int timeoutMs = 15000) =>
        client.PatchConfigDetailedAsync(fullConfig, baseHash, timeoutMs);
}

internal enum GatewayTailscaleAuthLiveState
{
    Ready,
    NotReady,
    Unavailable,
}

internal interface IGatewayTailscaleAuthLiveVerifier
{
    Task<GatewayTailscaleAuthLiveState> VerifyAsync(
        GatewayRecord record,
        int gatewayPort,
        CancellationToken cancellationToken);
}

internal sealed class GatewayTailscaleAuthLiveVerifier : IGatewayTailscaleAuthLiveVerifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyList<string> StatusCommand =
        ["/usr/bin/tailscale", "status", "--json"];
    private static readonly IReadOnlyList<string> ServeStatusCommand =
        ["/usr/bin/tailscale", "serve", "status", "--json"];

    private readonly IWslCommandRunner _commandRunner;
    private readonly TimeSpan _timeout;

    public GatewayTailscaleAuthLiveVerifier(
        IWslCommandRunner commandRunner,
        TimeSpan? timeout = null)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<GatewayTailscaleAuthLiveState> VerifyAsync(
        GatewayRecord record,
        int gatewayPort,
        CancellationToken cancellationToken)
    {
        if (GatewayRecordEditing.ResolveManagedDistroName(record) is not { } distroName ||
            !Uri.TryCreate(record.Url, UriKind.Absolute, out var gatewayUri) ||
            gatewayPort is <= 0 or > 65535)
        {
            return GatewayTailscaleAuthLiveState.Unavailable;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        WslCommandResult statusResult;
        try
        {
            statusResult = await _commandRunner.RunAsync(
                    BuildRootProbeArguments(distroName, StatusCommand),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GatewayTailscaleAuthLiveState.Unavailable;
        }

        if (!statusResult.Success)
            return GatewayTailscaleAuthLiveState.Unavailable;

        string dnsName;
        try
        {
            using var status = JsonDocument.Parse(statusResult.StandardOutput);
            var root = status.RootElement;
            var backendRunning =
                root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("BackendState", out var backendState) &&
                backendState.ValueKind == JsonValueKind.String &&
                string.Equals(backendState.GetString(), "Running", StringComparison.Ordinal);
            dnsName =
                root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Self", out var self) &&
                self.ValueKind == JsonValueKind.Object &&
                self.TryGetProperty("DNSName", out var dnsNameElement) &&
                dnsNameElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(dnsNameElement.GetString())
                    ? dnsNameElement.GetString()!.Trim().TrimEnd('.')
                    : string.Empty;

            if (!backendRunning ||
                !string.Equals(dnsName, gatewayUri.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                return GatewayTailscaleAuthLiveState.NotReady;
            }
        }
        catch (JsonException)
        {
            return GatewayTailscaleAuthLiveState.Unavailable;
        }

        WslCommandResult serveResult;
        try
        {
            serveResult = await _commandRunner.RunAsync(
                    BuildRootProbeArguments(distroName, ServeStatusCommand),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GatewayTailscaleAuthLiveState.Unavailable;
        }

        if (!serveResult.Success ||
            !TailscaleServeStatusPolicy.TryParse(
                serveResult.StandardOutput,
                gatewayPort,
                gatewayUri,
                out var serveStatus))
        {
            return GatewayTailscaleAuthLiveState.Unavailable;
        }

        if (serveStatus.FunnelEnabled)
            return GatewayTailscaleAuthLiveState.NotReady;
        if (serveStatus.ForegroundProxyPort is not { } managedIngressPort)
        {
            return serveStatus.RoutesToGateway
                ? GatewayTailscaleAuthLiveState.Ready
                : GatewayTailscaleAuthLiveState.NotReady;
        }

        WslCommandResult ownershipResult;
        try
        {
            ownershipResult = await _commandRunner.RunInDistroAsync(
                    distroName,
                    BuildManagedIngressOwnershipCommand(gatewayPort, managedIngressPort),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GatewayTailscaleAuthLiveState.Unavailable;
        }

        if (!ownershipResult.Success)
            return GatewayTailscaleAuthLiveState.Unavailable;

        return ownershipResult.StandardOutput.Trim() switch
        {
            "owned" => GatewayTailscaleAuthLiveState.Ready,
            "not-owned" => GatewayTailscaleAuthLiveState.NotReady,
            _ => GatewayTailscaleAuthLiveState.Unavailable,
        };
    }

    private static IReadOnlyList<string> BuildRootProbeArguments(
        string distroName,
        IReadOnlyList<string> command) =>
        ["-d", distroName, "--user", "root", "--", .. command];

    private static IReadOnlyList<string> BuildManagedIngressOwnershipCommand(
        int gatewayPort,
        int managedIngressPort)
    {
        var script = $$"""
            pid_before=$(/usr/bin/systemctl --user show openclaw-gateway -p MainPID --value 2>/dev/null)
            case "$pid_before" in ''|*[!0-9]*) /usr/bin/printf unavailable; exit 0;; esac
            test "$pid_before" -gt 0 || { /usr/bin/printf unavailable; exit 0; }
            main_cgroup=$(/usr/bin/awk -F: '$1 == "0" { print $3 }' "/proc/$pid_before/cgroup" 2>/dev/null)
            main_start_before=$(/usr/bin/awk '{ print $22 }' "/proc/$pid_before/stat" 2>/dev/null)
            main_exe=$(/usr/bin/readlink -f "/proc/$pid_before/exe" 2>/dev/null)
            test -n "$main_cgroup" && test -n "$main_start_before" && test -n "$main_exe" || { /usr/bin/printf unavailable; exit 0; }
            listener_pattern='(^|[[:space:]])127\.0\.0\.1:({{gatewayPort}}|{{managedIngressPort}})([[:space:]]|$)'
            listeners_raw_before=$(/usr/bin/ss -H -ltnp 2>/dev/null) || { /usr/bin/printf unavailable; exit 0; }
            listeners_before=$(/usr/bin/printf '%s\n' "$listeners_raw_before" | /usr/bin/grep -E "$listener_pattern" || true)
            candidate_count=0
            matched_pid=''
            matched_start=''
            matched_parent=''
            matched_parent_start=''
            matched_worker=''
            matched_worker_start=''
            for proc in /proc/[0-9]*; do
              unset argv
              declare -a argv=()
              while IFS= read -r -d '' arg; do argv+=("$arg"); done < "$proc/cmdline" 2>/dev/null
              test "${#argv[@]}" -eq 5 || continue
              test "${argv[0]}" = /usr/bin/tailscale && test "${argv[1]}" = serve && test "${argv[2]}" = --yes && test "${argv[3]}" = --bg=false && test "${argv[4]}" = {{managedIngressPort}} || continue
              candidate_pid=${proc##*/}
              candidate_exe=$(/usr/bin/readlink -f "$proc/exe" 2>/dev/null)
              candidate_cgroup=$(/usr/bin/awk -F: '$1 == "0" { print $3 }' "$proc/cgroup" 2>/dev/null)
              candidate_start=$(/usr/bin/awk '{ print $22 }' "$proc/stat" 2>/dev/null)
              candidate_parent=$(/usr/bin/awk '/^PPid:/ { print $2 }' "$proc/status" 2>/dev/null)
              test "$candidate_exe" = /usr/bin/tailscale && test "$candidate_cgroup" = "$main_cgroup" && test -n "$candidate_start" || continue
              route_owner=$candidate_parent
              parent_exe=$(/usr/bin/readlink -f "/proc/$candidate_parent/exe" 2>/dev/null)
              if test "$parent_exe" = /usr/bin/sudo; then
                unset sudo_argv
                declare -a sudo_argv=()
                while IFS= read -r -d '' arg; do sudo_argv+=("$arg"); done < "/proc/$candidate_parent/cmdline" 2>/dev/null
                test "${#sudo_argv[@]}" -eq 7 && test "${sudo_argv[0]}" = sudo && test "${sudo_argv[1]}" = -n && test "${sudo_argv[2]}" = /usr/bin/tailscale && test "${sudo_argv[3]}" = serve && test "${sudo_argv[4]}" = --yes && test "${sudo_argv[5]}" = --bg=false && test "${sudo_argv[6]}" = {{managedIngressPort}} || continue
                parent_cgroup=$(/usr/bin/awk -F: '$1 == "0" { print $3 }' "/proc/$candidate_parent/cgroup" 2>/dev/null)
                test "$parent_cgroup" = "$main_cgroup" || continue
                route_owner=$(/usr/bin/awk '/^PPid:/ { print $2 }' "/proc/$candidate_parent/status" 2>/dev/null)
              fi
              worker_exe=$(/usr/bin/readlink -f "/proc/$route_owner/exe" 2>/dev/null)
              worker_cgroup=$(/usr/bin/awk -F: '$1 == "0" { print $3 }' "/proc/$route_owner/cgroup" 2>/dev/null)
              worker_parent=$(/usr/bin/awk '/^PPid:/ { print $2 }' "/proc/$route_owner/status" 2>/dev/null)
              worker_start=$(/usr/bin/awk '{ print $22 }' "/proc/$route_owner/stat" 2>/dev/null)
              test "$worker_exe" = "$main_exe" && test "$worker_cgroup" = "$main_cgroup" && test "$worker_parent" = "$pid_before" && test -n "$worker_start" || continue
              unset worker_argv
              declare -a worker_argv=()
              while IFS= read -r -d '' arg; do worker_argv+=("$arg"); done < "/proc/$route_owner/cmdline" 2>/dev/null
              test "${#worker_argv[@]}" -eq 4 || continue
              case "${worker_argv[1]}" in */dist/infra/tailscale-route-owner.worker.js) ;; *) continue;; esac
              test "${worker_argv[2]}" = --openclaw-tailscale-route-owner || continue
              expected_direct='{"argv":["/usr/bin/tailscale","serve","--yes","--bg=false","{{managedIngressPort}}"]}'
              expected_sudo='{"argv":["sudo","-n","/usr/bin/tailscale","serve","--yes","--bg=false","{{managedIngressPort}}"]}'
              test "${worker_argv[3]}" = "$expected_direct" || test "${worker_argv[3]}" = "$expected_sudo" || continue
              candidate_count=$((candidate_count + 1))
              matched_pid=$candidate_pid
              matched_start=$candidate_start
              matched_parent=$candidate_parent
              matched_parent_start=$(/usr/bin/awk '{ print $22 }' "/proc/$candidate_parent/stat" 2>/dev/null)
              matched_worker=$route_owner
              matched_worker_start=$worker_start
            done
            pid_after=$(/usr/bin/systemctl --user show openclaw-gateway -p MainPID --value 2>/dev/null)
            main_start_after=$(/usr/bin/awk '{ print $22 }' "/proc/$pid_before/stat" 2>/dev/null)
            main_exe_after=$(/usr/bin/readlink -f "/proc/$pid_before/exe" 2>/dev/null)
            listeners_raw_after=$(/usr/bin/ss -H -ltnp 2>/dev/null) || { /usr/bin/printf unavailable; exit 0; }
            listeners_after=$(/usr/bin/printf '%s\n' "$listeners_raw_after" | /usr/bin/grep -E "$listener_pattern" || true)
            test "$pid_before" = "$pid_after" && test "$main_start_before" = "$main_start_after" && test "$main_exe" = "$main_exe_after" && test "$listeners_before" = "$listeners_after" || { /usr/bin/printf unavailable; exit 0; }
            gateway_owner=$(/usr/bin/printf '%s\n' "$listeners_after" | /usr/bin/grep -E '(^|[[:space:]])127\.0\.0\.1:{{gatewayPort}}([[:space:]]|$)' | /usr/bin/grep -F "pid=$pid_before,")
            ingress_owner=$(/usr/bin/printf '%s\n' "$listeners_after" | /usr/bin/grep -E '(^|[[:space:]])127\.0\.0\.1:{{managedIngressPort}}([[:space:]]|$)' | /usr/bin/grep -F "pid=$pid_before,")
            candidate_start_after=$(/usr/bin/awk '{ print $22 }' "/proc/$matched_pid/stat" 2>/dev/null)
            candidate_parent_after=$(/usr/bin/awk '/^PPid:/ { print $2 }' "/proc/$matched_pid/status" 2>/dev/null)
            parent_start_after=$(/usr/bin/awk '{ print $22 }' "/proc/$matched_parent/stat" 2>/dev/null)
            worker_start_after=$(/usr/bin/awk '{ print $22 }' "/proc/$matched_worker/stat" 2>/dev/null)
            worker_parent_after=$(/usr/bin/awk '/^PPid:/ { print $2 }' "/proc/$matched_worker/status" 2>/dev/null)
            if test -n "$gateway_owner" && test -n "$ingress_owner" && test "$candidate_count" -eq 1 && test "$matched_start" = "$candidate_start_after" && test "$matched_parent" = "$candidate_parent_after" && test "$matched_parent_start" = "$parent_start_after" && test "$matched_worker_start" = "$worker_start_after" && test "$worker_parent_after" = "$pid_before"; then
              /usr/bin/printf owned
            else
              /usr/bin/printf not-owned
            fi
            """;
        return ["/bin/bash", "-lc", script];
    }
}

internal sealed class GatewayTailscaleAuthUpgradeService
{
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(15);
    private readonly GatewayRegistry _registry;
    private readonly IGatewayTailscaleAuthLiveVerifier? _liveVerifier;

    public GatewayTailscaleAuthUpgradeService(GatewayRegistry registry)
        : this(registry, liveVerifier: null)
    {
    }

    public GatewayTailscaleAuthUpgradeService(
        GatewayRegistry registry,
        IGatewayTailscaleAuthLiveVerifier? liveVerifier)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _liveVerifier = liveVerifier;
    }

    public async Task<GatewayTailscaleAuthUpgradeResult> EnableAsync(
        string gatewayId,
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var record = _registry.GetById(gatewayId);
        if (!GatewayTailscaleAuthUpgradePolicy.IsEligible(record))
            return new(GatewayTailscaleAuthUpgradeOutcome.Ineligible);

        if (!string.Equals(_registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal))
            return new(GatewayTailscaleAuthUpgradeOutcome.NotActive);

        if (!client.IsConnectedToGateway)
            return new(GatewayTailscaleAuthUpgradeOutcome.NotConnected);

        if (!OperatorScopeHelper.CanReadConfig(client.GrantedOperatorScopes) ||
            !OperatorScopeHelper.CanWriteConfig(client.GrantedOperatorScopes))
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.MissingConfigScope);
        }

        ConfigSnapshot snapshot;
        try
        {
            snapshot = await ReadConfigAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (record!.TrustTailscaleAuth && AllowsTailscaleAuth(snapshot.Root))
            return new(GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled);

        if (string.IsNullOrWhiteSpace(snapshot.BaseHash))
        {
            return new(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, "Config base hash is unavailable.");
        }

        var previousValue = false;
        var changed = false;
        if (!record.TrustTailscaleAuth)
        {
            _registry.Update(gatewayId, current =>
            {
                if (!GatewayTailscaleAuthUpgradePolicy.CanOffer(current))
                    return current;

                previousValue = current.TrustTailscaleAuth;
                changed = true;
                return current with { TrustTailscaleAuth = true };
            });

            if (!changed)
                return new(GatewayTailscaleAuthUpgradeOutcome.NotActive);

            try
            {
                _registry.Save();
            }
            catch (Exception ex)
            {
                _registry.Update(gatewayId, current => current with { TrustTailscaleAuth = previousValue });
                return new(GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed, ex.Message);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var rollback = RollBackMarkerAfterRejectedPatch(
                gatewayId,
                changed,
                previousValue,
                patchError: null);
            if (rollback.Outcome == GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed)
                return rollback;
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Core beta.7 and current main define config.patch as JSON Merge Patch.
        // Send only the intended leaf: replaying the full snapshot would turn
        // unrelated null values into deletion markers.
        var configPatch = CreateAllowTailscalePatch();
        ConfigPatchResult patch;
        Task<ConfigPatchResult>? patchTask = null;
        try
        {
            patchTask = client.PatchConfigDetailedAsync(configPatch, snapshot.BaseHash);
            patch = await patchTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dispatch cannot be canceled at the client boundary. Observe its bounded
            // result before returning so callers never proceed beside an in-flight
            // mutation. Roll back only a definitive gateway rejection; otherwise the
            // marker remains for the next trust-aware live revalidation.
            try
            {
                var completedPatch = await patchTask!.ConfigureAwait(false);
                if (!completedPatch.Ok && completedPatch.IsGatewayRejection)
                {
                    var rollback = RollBackMarkerAfterRejectedPatch(
                        gatewayId,
                        changed,
                        previousValue,
                        completedPatch.Error);
                    if (rollback.Outcome == GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed)
                        return rollback;
                }
            }
            catch
            {
                // A transport failure is ambiguous: Core may have committed.
            }
            throw;
        }
        catch (Exception ex)
        {
            // The response can be lost after Core commits the patch. Keep the marker so
            // the next trust-aware launch revalidates the authoritative Core state.
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, ex.Message);
        }

        if (!patch.Ok && patch.IsGatewayRejection)
            return RollBackMarkerAfterRejectedPatch(gatewayId, changed, previousValue, patch.Error);

        if (!patch.Ok)
        {
            // The request may have committed before its response was lost. Preserve
            // the marker so the next trust-aware launch revalidates Core state.
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patch.Error);
        }

        return new(GatewayTailscaleAuthUpgradeOutcome.Succeeded);
    }

    public async Task<bool> RevalidateAsync(
        string gatewayId,
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var record = _registry.GetById(gatewayId);
        if (record?.TrustTailscaleAuth != true ||
            !GatewayTailscaleAuthUpgradePolicy.IsEligible(record) ||
            !string.Equals(_registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal) ||
            !client.IsConnectedToGateway ||
            !OperatorScopeHelper.CanReadConfig(client.GrantedOperatorScopes))
        {
            return false;
        }

        ConfigSnapshot snapshot;
        try
        {
            snapshot = await ReadConfigAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (AllowsTailscaleAuth(snapshot.Root))
        {
            if (_liveVerifier is null || !TryGetGatewayPort(snapshot.Root, out var gatewayPort))
                return false;

            try
            {
                return await _liveVerifier.VerifyAsync(record, gatewayPort, cancellationToken).ConfigureAwait(false) ==
                    GatewayTailscaleAuthLiveState.Ready;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        _registry.Update(gatewayId, current => current with { TrustTailscaleAuth = false });
        try
        {
            _registry.Save();
        }
        catch
        {
            _registry.Update(gatewayId, current => current with { TrustTailscaleAuth = true });
        }
        return false;
    }

    private static async Task<ConfigSnapshot> ReadConfigAsync(
        IGatewayTailscaleAuthConfigClient client,
        CancellationToken cancellationToken)
    {
        var timeoutMs = checked((int)ConfigTimeout.TotalMilliseconds);
        var response = await client.RequestConfigDetailedAsync(timeoutMs)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return CaptureSnapshot(response);
    }

    private static ConfigSnapshot CaptureSnapshot(JsonElement response)
    {
        var root = response.TryGetProperty("parsed", out var parsed)
            ? parsed
            : response.TryGetProperty("config", out var config)
                ? config
                : response;
        var baseHash = response.TryGetProperty("baseHash", out var baseHashElement) &&
            baseHashElement.ValueKind == JsonValueKind.String
                ? baseHashElement.GetString()
                : response.TryGetProperty("hash", out var hashElement) &&
                    hashElement.ValueKind == JsonValueKind.String
                    ? hashElement.GetString()
                    : null;
        if (baseHash is null &&
            response.TryGetProperty("raw", out var rawElement) &&
            rawElement.ValueKind == JsonValueKind.String &&
            rawElement.GetString() is { } raw)
        {
            baseHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }

        return new(root.Clone(), baseHash);
    }

    private static JsonElement CreateAllowTailscalePatch()
    {
        using var document = JsonDocument.Parse("""
            { "gateway": { "auth": { "allowTailscale": true } } }
            """);
        return document.RootElement.Clone();
    }

    private GatewayTailscaleAuthUpgradeResult RollBackMarkerAfterRejectedPatch(
        string gatewayId,
        bool changed,
        bool previousValue,
        string? patchError)
    {
        if (!changed)
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patchError);

        _registry.Update(gatewayId, current => current with { TrustTailscaleAuth = previousValue });
        try
        {
            _registry.Save();
            return new(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, patchError);
        }
        catch (Exception ex)
        {
            return new(
                GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed,
                $"Local Tailscale trust-marker rollback failed: {ex.Message}");
        }
    }

    // A true marker is issued only after this service or setup explicitly writes
    // allowTailscale=true. Omission therefore means that grant drifted or was revoked;
    // do not duplicate Core's environment-sensitive implicit auth resolver here.
    private static bool AllowsTailscaleAuth(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object &&
        config.TryGetProperty("gateway", out var gateway) &&
        gateway.ValueKind == JsonValueKind.Object &&
        gateway.TryGetProperty("auth", out var auth) &&
        auth.ValueKind == JsonValueKind.Object &&
        auth.TryGetProperty("allowTailscale", out var allowTailscale) &&
        allowTailscale.ValueKind is JsonValueKind.True;

    private static bool TryGetGatewayPort(JsonElement config, out int port)
    {
        const int defaultGatewayPort = 18789;
        port = 0;
        if (config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("gateway", out var gateway) ||
            gateway.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!gateway.TryGetProperty("port", out var gatewayPort))
        {
            port = defaultGatewayPort;
            return true;
        }

        return gatewayPort.ValueKind == JsonValueKind.Number &&
            gatewayPort.TryGetInt32(out port) &&
            port is > 0 and <= 65535;
    }

    private sealed record ConfigSnapshot(JsonElement Root, string? BaseHash);
}
