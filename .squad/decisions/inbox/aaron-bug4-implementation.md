# Aaron — Bug #4 implementation (Wizard hung at "Authenticating")

- **Branch:** `feat/wsl-gateway-clean`
- **Commit:** `d4bc385` — `fix(app): broaden gateway client credential resolver -- Token -> BootstrapToken -> DeviceIdentity (Bug #4 from manual test)`
- **Status:** Source change + tests + commit done. **Not pushed.**
- **Re-reviewer:** RubberDucky-4 (CONDITIONAL AGREE follow-up); e2e: Bostick.

## File changes

| Path | LOC | Purpose |
|---|---|---|
| `src/OpenClaw.Tray.WinUI/Services/GatewayCredentialResolver.cs` | +71 (new) | Static, WinUI-free resolver. Returns `GatewayCredential(Token, IsBootstrapToken, Source)` or `null`. |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | -6 / +28 around L1030 | `InitializeGatewayClient` now delegates credential lookup to the resolver. Caller's `useBootstrapHandoffAuth` hint is OR'd with the resolver flag for backwards compat with bootstrap-handoff callers. |
| `tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj` | +1 | Links resolver into Tray.Tests via `<Compile Include="...">`. |
| `tests/OpenClaw.Tray.Tests/GatewayCredentialResolverTests.cs` | +163 (new) | 8 xUnit tests covering all branches + regressions. |

Scope is exactly what RubberDucky asked for: App-init resolver + test seam + tests. No touches to `SettingsOperatorPairingService.PairAsync`, `WizardPage`, `OnboardingWindow`, `ConnectionPage`, or `QuickSendCoordinator`.

## RubberDucky closure conditions — line-by-line satisfaction

### 1. Hotfix in `App.InitializeGatewayClient`, not producer-side
- Edit is in `src/OpenClaw.Tray.WinUI/App.xaml.cs` `InitializeGatewayClient` at L1030–L1063 (post-edit).
- `SettingsOperatorPairingService.PairAsync` was **not modified** (`git diff --name-only HEAD~1 HEAD` confirms only the four files above changed).
- `useBootstrapHandoffAuth` is preserved (`tokenIsBootstrapToken = credential.IsBootstrapToken || useBootstrapHandoffAuth;` at App.xaml.cs L1054), so when later producer-side promotion or QR dual-token harvest lands, the App resolver remains harmless because settings.Token wins by precedence.

### 2. Credential resolution order mirrors prototype
Implemented in `GatewayCredentialResolver.Resolve` (`src/OpenClaw.Tray.WinUI/Services/GatewayCredentialResolver.cs`):
- L33–L36 — `settings.Token` populated → `IsBootstrapToken=false`, `Source=settings.Token`.
- L38–L41 — else `settings.BootstrapToken` populated → `IsBootstrapToken=true`, `Source=settings.BootstrapToken`.
- L43–L60 — else read `device-key-ed25519.json` → `DeviceToken` field → `IsBootstrapToken=false`, `Source=deviceIdentity.DeviceToken`.
- L62 — else `null`. Caller in App.xaml.cs L1043–L1046 logs `"Gateway token not configured — skipping operator client initialization"` (exact text preserved).

Construction at `App.xaml.cs` L1057–L1061: `new OpenClawGatewayClient(gatewayUrl, credential.Token, new AppLogger(), tokenIsBootstrapToken)`.

### 3. Test seam
`GatewayCredentialResolver` is `public static` with no WinUI dependencies — only `System.IO`, `System.Text.Json`. Tray.Tests links it via the existing `<Compile Include="..\..\src\...">` pattern (csproj diff above), no project reference to `OpenClaw.Tray.WinUI` needed. Tests assert both the selected token and the `IsBootstrapToken` flag without spinning up WinUI.

### 4. Tests
File: `tests/OpenClaw.Tray.Tests/GatewayCredentialResolverTests.cs`. Each test name + 1-line behavioral assertion:

| Test | Asserts |
|---|---|
| `Resolve_PrefersSettingsToken_AsNonBootstrap` | Token populated → returned as-is, `IsBootstrapToken=false`, `Source=settings.Token`. **Doubles as the regression guard for the existing manual ConnectionPage flow** that sets `_settings.Token` after the user submits URL+token. |
| `Resolve_FallsBackToBootstrapToken_AsBootstrap` | Token empty + BootstrapToken populated → returned, `IsBootstrapToken=true`, `Source=settings.BootstrapToken`. (Load-bearing per RubberDucky note 4 — `BuildAuthPayload` keys off this flag.) |
| `Resolve_FallsBackToDeviceIdentityDeviceToken_AsNonBootstrap` | Both settings empty + DeviceIdentity has DeviceToken → returned from disk, `IsBootstrapToken=false`. **The literal Bug #4 path** Mike hit. |
| `Resolve_AllEmpty_ReturnsNull` | All three empty + no identity file → null (caller skips, logs). |
| `Resolve_DeviceIdentityWithoutDeviceToken_ReturnsNull` | Identity JSON exists but lacks `DeviceToken` field → null (no silent empty token). |
| `Resolve_DeviceIdentityCorrupt_LogsWarningAndReturnsNull` | Corrupt JSON → warn callback fires with `"Failed to inspect stored gateway device token"`, returns null, no throw. |
| `Resolve_WhitespaceTokens_AreIgnored` | `"   "` / `"\t\n"` are not credentials. |
| `Resolve_TokenWinsOverBootstrap_WhenBothPresent` | Precedence test: future producer-side promotion / dual-token harvest can't accidentally promote a bootstrap value. |

### 5. No downstream consumers touched
`git show --stat HEAD` shows only the four files above. WizardPage, OnboardingWindow, ConnectionPage, QuickSendCoordinator are untouched.

## Validation results

| Step | Result |
|---|---|
| `./build.ps1` | ✅ All builds succeeded (Shared, Cli, WinNodeCli, WinUI default platform). |
| `dotnet test tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj` (with `OPENCLAW_REPO_ROOT` set) | ✅ **559/559 passed** (551 baseline + 8 new). 0 failed. |
| `dotnet test tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj` | ⚠️ 1157 passed, 22 skipped, **1 pre-existing failure** (`ReadmeValidationTests.ReadmeAllowCommandsJsonExample_IsValid`) — unrelated to this change, on `main` baseline. |

Note on Tray.Tests: the 6 `LocalizationValidationTests` failures vanish once `OPENCLAW_REPO_ROOT` is exported. Same env behavior as before this commit.

## Binary freshness

- **`bin\Debug\...\win-x64\OpenClaw.Tray.WinUI.exe`** (default platform, built by `./build.ps1`): **fresh**, `LastWriteTime = 2026-05-05 09:36:32`.
- **`bin\x64\Debug\...\win-x64\OpenClaw.Tray.WinUI.exe`** (explicit `-p:Platform=x64` per spec): **STALE**, `LastWriteTime = 2026-05-05 08:55:41`. PID 6652 (Mike's running tray) holds it locked.
- **WinUI x64 build deferred until Mike kills PID 6652** (per task guardrail "do NOT kill PID 6652"). For e2e verification, Mike must run `Stop-Process -Id 6652` and then `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64` (or just relaunch from the default-platform exe which already contains the fix).

## Notes for RubberDucky's re-review

1. Resolver source/tests live in `src/OpenClaw.Tray.WinUI/Services/GatewayCredentialResolver.cs` and `tests/OpenClaw.Tray.Tests/GatewayCredentialResolverTests.cs`. Both are pure C# / no WinUI.
2. `useBootstrapHandoffAuth` parameter on `InitializeGatewayClient` was kept and OR'd into the resolver flag (App.xaml.cs L1054) — necessary because two existing call sites still set settings.Token to a bootstrap value and pass `true`. Resolver-determined flag dominates when source is `BootstrapToken` (always true) or `DeviceIdentity` (always false); caller hint only adds `true` for the `settings.Token` branch. This is conservative; happy to invert if you prefer the resolver to be authoritative.
3. Logging change: on success now emits `"Gateway credential resolved from <source> (bootstrap=<bool>)"` at Info. Original `"Gateway token not configured — skipping operator client initialization"` text preserved exactly for the null path so any log scrapers / Bostick's e2e search still hit.

## Notes for Bostick's e2e verification

Walk-throughs to capture log evidence for:

1. **Bug #4 reproducer (the main path):** Fresh install → onboarding → Local mode → Phase 12 completes (settings.Token NOT promoted, BootstrapToken cleared, operator token in DeviceIdentity only) → LocalSetupProgress completes → Wizard renders. **Expected log:** `"Gateway credential resolved from deviceIdentity.DeviceToken (bootstrap=False)"`, then `wizard.start` sent within 30s, provider-picker step renders. **Should NOT see:** `"Gateway token not configured"`.
2. **Existing manual ConnectionPage path (regression guard):** User enters URL + token in ConnectionPage → settings.Token populated → reinit → expected log: `"Gateway credential resolved from settings.Token (bootstrap=False)"`.
3. **Bootstrap handoff:** Fresh install → URL + bootstrap token in ConnectionPage with `useBootstrapHandoffAuth=true` → log: `"Gateway credential resolved from settings.Token (bootstrap=True)"` (the OR with caller hint).
4. **All-empty regression:** Wipe `~/.openclaw/` device-key + clear settings tokens → relaunch → `"Gateway token not configured — skipping operator client initialization"`.

Bostick should also confirm the four downstream consumers (Wizard, OnboardingWindow, ConnectionPage, QuickSendCoordinator) all stop hanging after this single App-init fix — that is the whole point of fixing it consumer-side once.
