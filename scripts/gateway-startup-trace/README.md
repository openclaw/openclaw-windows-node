# Gateway startup trace

`scripts\Invoke-GatewayStartupTrace.ps1` measures where native Gateway startup time goes, with
a focus on Microsoft Defender real-time scanning of the payload's many files. It runs the
same bounded startup on the host and in a temporary MXC isolated session while one named
WPR instance records Defender scans, file I/O, disk I/O and CPU sampling.

Use it to compare payload changes such as bundling, lazy loading or install-time prewarm.
It does not change Defender, power or system settings, and it never adds exclusions.

## Requirements

- PowerShell 7 (`pwsh`), started **unelevated**. The controller and the comparison driver
  both refuse to run elevated, so the measured startup uses the normal user token. Only the
  recorder is elevated, through a UAC prompt that you approve.
- A native Gateway payload root containing `payload-metadata.json` and `app\openclaw.mjs`,
  matching the PowerShell process architecture.
- The Node runtime zip for that payload and its SHA256.
- The MXC SDK version (default `1.0.1`) in the NuGet package cache (`NUGET_PACKAGES` or
  `%USERPROFILE%\.nuget\packages`).
- `wpr.exe` and the `ConfigDefenderPerformance` module (built into supported Windows).

Only trace **trusted** payloads. The startup guard (`startup-guard.cjs`) is not a security
sandbox. It patches selected Node APIs to block networking and child processes, redirects
profile variables to a synthetic profile, and exits at `admission.config-recovery
starting` before any listener. Payload code can still use the filesystem or native
modules, and the host arm runs as the current user.

## Run

```powershell
.\scripts\Invoke-GatewayStartupTrace.ps1 `
    -PayloadRoot 'C:\payloads\openclaw-2026.10.9-arm64' `
    -NodeArchive 'C:\payloads\node-v24.22.1-win-arm64.zip' `
    -NodeArchiveSha256 '<sha256>' `
    -Order isolated-then-host
```

Each run creates a private directory under `%LOCALAPPDATA%\OpenClawStartupTraces` (override
with `-OutputDirectory`) with inheritance disabled. It contains:

| File | Contents | Share? |
|---|---|---|
| `startup-trace.etl` | Full WPR trace | No. It contains machine-wide activity |
| `comparison-result.json` | Guard timings, cleanup and identity checks | Review first |
| `startup-summary.json` | Sanitized aggregates with payload-relative paths | Yes |

Scratch `node.exe` and the synthetic host profile are removed at the end. The driver stops
and deprovisions the temporary MXC session by exact ID. `-SkipRecording` runs the
comparison without WPR.

Run the arms in a fixed order and record it. The first arm after boot or after payload
extraction pays cold costs that the second arm may not. For a cold comparison, reboot
between runs, run one order per boot, alternate orders, and avoid hashing or reading the
payload just before the run.

## Recording safety

The recorder uses `OpenClawStartup-<guid>` with `-instancename` as the last WPR argument for
start, status, stop and cancel. It refuses to start if that instance is already recording.
It saves the trace when the controller writes its stop file. It cancels its own instance
when `-MaxRecordingSeconds` (default 600) elapses or when it fails. Each WPR call is bounded:
stop gets 240 seconds, inside the controller's default 300-second wait, and status or cancel 60 seconds (override with
`OPENCLAW_TRACE_WPR_STOP_TIMEOUT_SECONDS` and `OPENCLAW_TRACE_WPR_CONTROL_TIMEOUT_SECONDS`).
A stop that times out is killed and followed by a cancel of the same instance.

This is enforced inside the elevated recorder process, so it is **not crash-safe**. If that
window is closed, or the process is killed or crashes, the named instance keeps recording.
The controller prints the instance name at start. Recover from an elevated prompt with:

```powershell
wpr.exe -status -instancename OpenClawStartup-<guid>
wpr.exe -cancel -instancename OpenClawStartup-<guid>
```

Never use a bare `wpr -cancel`; it is not limited to this recording.

## Reading the summary

For each arm, `startup-summary.json` reports Defender scans attributed to the probe PID on
payload files:

- `scan_union_in_run_ms` is wall-clock time covered by at least one scan.
- `summed_scan_ms` adds scan durations and can exceed wall-clock time when scans overlap.
- `windows` splits the run into `before-first-phase`, `session-runtime-import`,
  `between-phases`, `plugin-config` and `after-plugin-config`. The interval before the first
  recorded phase is unclassified startup, not evidence of any particular cost.
- `top_groups` ranks `node_modules\<package>`, `dist\extensions\<name>` and `dist\other` by
  summed scan time.
- The summary fails instead of reporting partial data if the Defender report reaches
  `-TopFiles` or `-TopScansPerFile`. Rerun it with larger limits.

Scan coverage correlates with startup time but does not prove the probe was blocked on a
scan. Do not add scan time, CPU time and disk time as exclusive contributions. Use WPA on
the private ETL for file I/O, disk and CPU detail, including MiniFilter delay when present.
A change in the OS file cache is separate from Defender scan caching. Measure any prewarm
claim on a comparable packaged or MXC startup.

## Real setup mode

`scripts\Invoke-GatewaySetupTrace.ps1` records the full Companion native setup instead of
the synthetic probe. That covers `clawctl setup`, `openclaw config validate`, the isolated
Gateway start and readiness. Launch the Companion build under test with its intended profile
first, then run from an unelevated PowerShell 7 prompt:

```powershell
.\scripts\Invoke-GatewaySetupTrace.ps1 `
    -SetupLogDirectory <profile>\settings\Logs\Setup `
    -PayloadAppRoot <layout>\app, <payload>\app `
    -CompanionProcessId <task-owned Companion PID>
```

Approve the recorder UAC prompt, then start native Gateway setup in Companion when the script
asks. It follows the `native-setup-*.jsonl` attempt that started after the script started.
An attempt begun while the UAC prompt was open is still followed; the summary sets
`attempt_started_before_recording` and reports each window's `unrecorded_prefix_ms`. It
stops at the first of:

- a logged setup failure or cancellation
- the AI-setup page in the `-CompanionProcessId` process (outcome `reached-ai-setup`)
- the setup page's Retry state in that process; without a logged failure, the outcome is
  `failed-ui-only`, because some unexpected cancellation paths write no setup-log record
- creation of `<run>\control\setup-stop`
- `-SetupTimeoutSeconds`

The setup log has no explicit success record. `reached-ai-setup` means Gateway preparation
reached the AI-setup page, not full onboarding. Without `-CompanionProcessId`, a UI driver may
write exactly `ai-setup-reached` to the control file after verifying that page. Any other
content is a manual stop, reported as `incomplete`. UI state comes only from UI Automation IDs
of on-screen elements in the given process.

Pass every path that reaches the payload, such as a layout junction and its target. Each root
must contain `openclaw.mjs`. When calling the summary through `pwsh -File`, a comma-joined
list arrives as one string and is rejected; call it in-process to pass several roots.
`report_files_matching_payload` is 0, with a warning, when no Defender report file matched any
root, so a zero-scan result can be told apart from a wrong root.

`setup-summary.json` reports only fixed `stop_reason` codes. Raw controller error text stays
in the private `setup-result.json`.

`setup-summary.json` reports payload scans from every process, using anonymous process
indexes, and splits the recording at setup-log milestones. Those milestones run from
`attempt-started` through config validation, `gateway-start-requested` and
`gateway-start-acknowledged` to `listener-verified`, `readiness-timeout` or `setup-failed`,
plus `ai-setup-page` or `ui-retry-shown` when the UI stop was observed. `listener-verified` is
only the exact `Verifying the Gateway endpoint before connecting...` progress record after the
start acknowledgment, which setup logs once the runtime returns a verified listener. Other info
records, such as a re-logged setup step, are not listener evidence. It is not success:
handshake, pairing and AI preparation can still fail afterwards.

Setup milestones are not the Gateway's `session-runtime-import` or `plugin-config` phases.
Take those from Gateway diagnostics, collected with **Collect Gateway diagnostics** on the
setup page. Do not compare setup-mode scan counts directly with synthetic-probe counts.

## Tests

```powershell
.\scripts\test-gateway-startup-trace.ps1
```

This runs offline helper, guard and refusal checks. It never starts WPR, MXC or a Gateway.
