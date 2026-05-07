# Commit 3 Complete: LocalGatewayUninstall Core Engine

**Branch:** `feat/wsl-gateway-uninstall`  
**Commit:** `bc08f11`  
**Date:** 2026-05-08  
**Author:** Aaron (Backend/Infrastructure)

---

## What Landed

### New: `LocalGatewayUninstall.cs`
`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewayUninstall.cs`

Public API surface:

```csharp
// Enums & records
public enum UninstallStepStatus { Executed, Skipped, DryRun, Failed }
public sealed record UninstallStep(string Name, UninstallStepStatus Status, string? Detail = null);
public sealed record LocalGatewayUninstallPostconditions { ... }  // WslDistroAbsent, AutostartCleared, etc.
public sealed record LocalGatewayUninstallOptions {
    bool DryRun { get; init; } = true;          // safety default
    bool ConfirmDestructive { get; init; }      // must be true for real run
    string DistroName { get; init; } = "OpenClawGateway";
    bool PreserveLogs { get; init; } = true;    // default: keep logs
    bool PreserveExecPolicy { get; init; } = true; // default: keep exec-policy.json
}
public sealed record LocalGatewayUninstallResult {
    bool Success { get; init; }
    IReadOnlyList<UninstallStep> Steps { get; init; }
    IReadOnlyList<string> Errors { get; init; }
    LocalGatewayUninstallPostconditions Postconditions { get; init; }
}

// Engine
public sealed class LocalGatewayUninstall {
    public static LocalGatewayUninstall Build(
        SettingsManager settings,
        IWslCommandRunner? wsl = null,
        IOpenClawLogger? logger = null,
        string? identityDataPath = null,
        string? localDataPath = null);

    public Task<LocalGatewayUninstallResult> RunAsync(
        LocalGatewayUninstallOptions options,
        CancellationToken ct = default);
}
```

### Modified: `DeviceIdentity.cs`
New static method on `DeviceIdentity`:

```csharp
public static bool TryClearDeviceToken(string dataPath, IOpenClawLogger? logger = null)
```

- Reads `device-key-ed25519.json`, nulls `DeviceToken` + `DeviceTokenScopes`, writes back
- Preserves all other fields (public key, node token, etc.)
- mcp-token.txt is NOT touched (it has its own file, per v3 §F)
- Returns `false` if file not found (idempotent)

---

## 13-Step Sequence (Kranz v3)

| # | Step name | DryRun behaviour |
|---|-----------|-----------------|
| 1 | Preflight gate | DryRun recorded |
| 2 | Stop keepalive process | DryRun |
| 3 | Stop systemd gateway service | DryRun |
| 4 | Revoke operator token | DryRun |
| 5 | Unregister WSL distro | DryRun |
| 6 | Reset autostart | DryRun (sub-steps: persist settings then delete reg value) |
| 7 | Clear device token | DryRun |
| 8 | Delete setup-state.json | DryRun |
| 9 | Delete VHD directory | DryRun |
| 10 | Delete exec-policy.json | DryRun (unless PreserveExecPolicy=true → Skipped) |
| 11 | Delete log files | DryRun (unless PreserveLogs=true → Skipped) |
| 12 | Preserve mcp-token.txt | Skipped (unconditional no-op, logged for audit) |
| 13 | Compute postconditions | DryRun (not evaluated in DryRun mode) |

**Safety invariant:** `DryRun=true` (default) → zero filesystem/registry mutations.  
**Hard gate:** `DryRun=false` without `ConfirmDestructive=true` → throws `InvalidOperationException`.

---

## Test Results

- `./build.ps1` → PASS (clean)  
- `dotnet test OpenClaw.Shared.Tests` → PASS  
- `dotnet test OpenClaw.Tray.Tests` → **632 pass / 8 fail** (8 pre-existing localization failures, unchanged)

20 new `[WindowsFact]` tests in `LocalGatewayUninstallTests.cs` covering:
- DryRun never mutates filesystem/registry
- Real destructive mode: files deleted, registry cleared, device token nulled
- Idempotency: absent distro → Skipped (not Failed)
- Step ordering: autostart settings persist before registry delete
- File preservation defaults: logs + exec-policy preserved by default
- mcp-token.txt always preserved
- ConfirmDestructive preflight throws without it
- Error propagation and cancellation

---

## For Mattingly — UI Integration (Commit 4)

To wire up in the uninstall page/wizard:

```csharp
// Minimal usage
var engine = LocalGatewayUninstall.Build(_settingsManager);

// DryRun preview first
var preview = await engine.RunAsync(new LocalGatewayUninstallOptions());

// Real run (requires user to click "Confirm" in UI)
var result = await engine.RunAsync(new LocalGatewayUninstallOptions {
    DryRun = false,
    ConfirmDestructive = true
});

// Bind result.Steps to progress list, result.Success to completion state
```

The `Steps` list provides per-step status for a progress UI.  
The `Postconditions` record gives post-uninstall verification signals for a summary page.

---

## For Bostick — Packaging Tests (Commit 7)

- `LocalGatewayUninstall.Build()` factory uses `OPENCLAW_TRAY_DATA_DIR` and `OPENCLAW_TRAY_LOCALAPPDATA_DIR` env vars for path isolation — same pattern as `SettingsManager`
- `IWslCommandRunner` is injectable for E2E test stubs
- All 13 steps are idempotent — safe to call twice
- `FakeWslCommandRunner` pattern used in `LocalGatewayUninstallTests.cs` can be reused in integration tests
