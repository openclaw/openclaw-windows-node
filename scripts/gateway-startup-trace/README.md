# Gateway startup, setup and chat performance traces

`scripts\Invoke-GatewayStartupTrace.ps1` measures where native Gateway startup time goes, with
a focus on timing, resource use and Microsoft Defender scanning of the payload's many files. It runs the
same bounded startup on the host and in a temporary MXC isolated session while one named
WPR instance records Defender scans, file I/O, disk I/O and CPU sampling.
The same recorder supports real Companion setup and user-driven chat. A bounded sampler
adds process CPU, memory, logical I/O and host contention evidence. These tools are for
individual investigations, not continuous monitoring.

Agents should start with the [Windows tracing skill](../../.agents/skills/openclaw-tracing/SKILL.md)
for workflow selection, capture authority, interpretation and evidence handoff.
This runbook remains the command and metric reference.

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
- .NET 10 SDK for the offline ETL analyzer. Build it **before** preparing a cold run:

```powershell
dotnet build .\scripts\gateway-startup-trace\Analyzer\GatewayTraceAnalyzer.csproj
```

The analyzer uses Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0. It runs after capture,
not inside the measured workload. Without its build, summaries explicitly report
`unavailable-build-analyzer`; sampled counters and Defender summaries still remain.

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
| `resource-samples.jsonl` | Private process creation identities and host/process counter snapshots | No |
| `control\resource-config.json` | Private process/image-root selection and supplied build identities | No |
| `resource-analysis-request.json` | Private ETL correlation windows and process identities | No |
| `resource-etl-summary.json` | Sanitized CPU samples, logical file activity and machine disk activity | Yes |
| `resource-timeline.json` | Sanitized sampled memory timeline with anonymous process indexes | Yes |

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
when `-MaxRecordingSeconds` elapses or when it fails. Start, stop, status and cancel calls
are bounded. Start defaults to 60 seconds and status or cancel to 30 seconds. The controller
gives WPR stop up to half the recording limit, with a 180-second minimum and 1800-second
maximum, then allows 90 seconds for cancellation, final status and polling overhead. The
controller's `-RecorderStopTimeoutSeconds` must exceed that WPR stop limit by at least
90 seconds.
A stop that times out is killed and followed by a cancel of the same instance. Recorder
status is atomically published. The status check recognizes English `WPR is recording`,
`WPR recording is in progress`, and `WPR is not recording` output. Nonzero status
commands or unrecognized output fail closed as unknown; an unknown preflight state is
reported conservatively as possibly still recording without canceling the instance.
If the recorder exits before it signals readiness, the controllers preserve any status
it already published, including a possible-active warning.

The setup controller requires `MaxRecordingSeconds` to exceed `SetupTimeoutSeconds` by at
least 60 seconds. The startup comparison requires it to exceed two benchmark timeouts by
300 seconds. A failed or missing recorder status makes the controller fail, and no
successful trace summary is emitted.

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
the private ETL for deeper file I/O, disk and CPU detail, including MiniFilter delay when present.
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

The sampler includes the given Companion PID and executable images under the payload roots.
Node, `clawctl` and a local-model host can live elsewhere. Pass their executable directories
with `-ResourceImageRoot`, or their already-running PIDs with `-ResourceProcessId`. Only
processes whose full image path matches a configured root, or whose PID **and native creation
time** match an explicit selection, contribute process counters. A child is not automatically
included just because its parent was selected.

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

## Resource outputs and limits

Startup and setup summaries retain their Defender fields and add `resources`. Resource
windows use the same phase/milestone boundaries, including unclassified startup before the
first marker. Whole-recording/whole-arm windows overlap the phase windows; do not sum them.

| Output | Meaning and limit |
|---|---|
| Process CPU user/kernel milliseconds | Differences between native counters for the same PID and creation time. Only sample endpoints inside the window contribute. One observation gives `null`, not zero |
| Sampled peak working set and private bytes | Highest observed value in that window. Not the process's lifetime peak, committed system memory, or a sum of exclusive memory across processes |
| Logical read/write bytes and operations | Process I/O counter deltas. Can include pipes/network/device I/O. Not physical disk traffic |
| Host CPU fraction and available memory | Machine-wide context, including other workloads and the recorder. Not proof that contention caused the measured delay |
| Maximum sample gap, non-awake gap and clock adjustment | Sampling uses a 500 ms delay after each capture, so the real cadence is slower. Unbiased awake time and the monotonic clock distinguish non-awake intervals from wall-clock adjustment. These are not named sleep/resume ETW events |
| ETL CPU sample counts and image groups | Selected process lifetimes, split at observed process starts/exits. Counts are not milliseconds or provider/JS parse time. Missing native images and JIT frames stay unresolved |
| ETL file open requests, read/write counts and bytes | Logical activity grouped by package and top payload-relative files. Open requests are not verified successful opens. Missing filenames remain unresolved |
| ETL machine disk bytes, counts and response time | Machine-wide requests assigned by completion timestamp. Response time is ETW-reported device response time, not independently correlated issue-to-completion latency. Do not attribute these totals or overlapping request durations to Gateway blocking |
| Event-loss and observation flags | Loss makes the ETL report incomplete. No events is unobserved, not proof of no activity or complete provider capture |

`resource-timeline.json` contains available host memory and selected-process working set/private
bytes at each observation. Process labels are anonymous and stable within that run's resource
outputs. Defender process indexes are a separate namespace.
Each process has its own observation timestamp because process enumeration is not atomic.
Its phase assignment and timeline offset use that timestamp, not enumeration start.
If ETL lifetime boundaries are missing, attribution is bounded conservatively by native
observations rather than extending a selected PID indefinitely. Gap metrics are `null`
when a window has no complete sampling interval.

The recorder enumerates processes to discover matching executable images. Unavailable/exited
process observations are counted; explicit-PID-only sampling skips unrelated processes
before querying their properties. Image-root discovery can count inaccessible candidates
whose paths could not be checked. Native queries use limited-information process handles;
denied I/O counters remain `null`. Short-lived processes
between samples can be missed completely. A guest PID is not assumed to be a host PID. If MXC
guest activity is not visible/matchable, process coverage stays absent rather than claiming
attribution. Use the private ETL for additional investigation.

Sampling and ETW have observer overhead. `sampler_maximum_capture_ms` reports the longest
snapshot plus serialization/write cost, not total recorder CPU. Host measurements include
that overhead. Record competing work, boot/cache state, run order, sleep and thermal/power
conditions separately. The scripts do not control or change them.
To change the default 500 ms post-capture delay, set `OPENCLAW_TRACE_SAMPLE_INTERVAL_MS`
to an integer from 100 to 10000 before starting the controller. The selected delay is
recorded in context; keep it identical across comparisons.

For reproducible identities, optionally pass `-BuildIdentityPath` to any controller. Its JSON
object accepts only `companion_sha256`, `gateway_sha256`, `local_model_sha256`,
`companion_commit`, `gateway_commit`, `payload_commit`, and the corresponding
`companion_version`, `gateway_version`, `payload_version`, `node_version` fields. Hashes must
be 64 hex digits, commits 40 hex digits, versions dot-separated with an initial numeric
component. Unknown fields and free text are rejected. Supply previously computed identities;
do not hash the payload immediately before a cold run. These identities are explicitly
operator-supplied, not independently verified during capture. If omitted, identity is
`not-supplied`. Synthetic comparison results retain their existing payload identity checks.
Resource context also records OS version, architecture, logical processor count and a digest
of the trace tool's source files.

Old traces without `resource-samples.jsonl` report `not-recorded`; no memory/counter history
is reconstructed from nothing. Re-run the existing summary script after building the analyzer
to analyze a saved recording, without recording again.

## Real chat mode

Launch and connect the intended Companion/Gateway normally first. Do not change another
person's runtime, package or profile for a trace. Obtain approval for the real chat operation
and elevated machine-wide capture; use a non-sensitive test prompt. The controller does not
send a prompt, read credentials, inspect chat logs, or start/stop any application.

From an unelevated PowerShell 7 console:

```powershell
.\scripts\Invoke-GatewayChatTrace.ps1 `
    -ProcessId <Companion-PID>, <Gateway-host-PID>, <optional-local-model-PID> `
    -PayloadAppRoot 'C:\payloads\candidate\app' `
    -ChatTimeoutSeconds 600
```

Pass only the real, already-running process IDs whose resource use you intend to measure.
For a remote Gateway, select the local Companion process and omit `-PayloadAppRoot`; no
remote CPU/memory or provider timing can be measured. PID reuse does not silently select
a different process. Restarted/new processes must be selected by a new recording.

After UAC approval, the controller prints the private run directory and marker command.
In a second console, use the same directory for each observation:

```powershell
.\scripts\Write-GatewayChatTraceMarker.ps1 -RunDirectory '<printed-run-directory>' -Event send
.\scripts\Write-GatewayChatTraceMarker.ps1 -RunDirectory '<printed-run-directory>' -Event first-response
.\scripts\Write-GatewayChatTraceMarker.ps1 -RunDirectory '<printed-run-directory>' -Event completed
```

Mark `send` as you send in the actual chat UI, `first-response` when the first response
becomes visible, and `completed` when the reply ends. Use `failed` or `cancelled` instead
of `completed` when applicable. Only those event names and timestamps are recorded.
Duplicate, reordered, missing-send and conflicting terminal markers fail closed.
Live polling defers markers newer than its snapshot cutoff until the next poll.
Shutdown collects markers once more against the final recording boundary. A readiness-file
cleanup failure is reported but cannot bypass the owned recorder stop request.
To stop without a terminal observation, create `<run>\control\chat-stop`. Timeout or manual
stop is incomplete and exits nonzero; failure/cancellation remain explicit chat outcomes.
The recording limit must exceed the chat timeout by more than 60 seconds.

`chat-summary.json` reports observed send-to-first-response and send-to-terminal times,
pre-send and between-marker resource windows, and Defender coverage when payload roots were
supplied. Marker timing includes console switching, human reaction and command scheduling.
Delay in marking send versus first response can bias the observed duration in either direction.
Controller polling affects the recording-stop boundary, not the marker-to-marker duration.
They are **not instrumented server time to first token**, network latency, or tokens/second.
Those fields remain unavailable. This mode measures a real chat interaction without
claiming a more precise timing boundary than the observation actually provides.

The same named-instance deadline, stop/cancel and manual recovery rules apply to chat.
Closing the controller does not close the chat or the app. Keep ETLs and raw samples private:
ETLs can contain unrelated activity even though the summary and marker files contain no
prompt, response text, session key, credentials or command lines.

## Tests

```powershell
dotnet build .\scripts\gateway-startup-trace\Analyzer\GatewayTraceAnalyzer.csproj
.\scripts\test-gateway-startup-trace.ps1
.\scripts\test-gateway-resource-trace.ps1
```

These run offline helper, guard, refusal, resource projection, PID reuse, marker publication
and ordering checks, plus a current-test-process sampler smoke check. They never start WPR,
MXC, Companion, a Gateway or a chat request. A successful build/offline suite does not prove
current-head WPR provider coverage, UAC, actual chat timing or MXC guest attribution.
