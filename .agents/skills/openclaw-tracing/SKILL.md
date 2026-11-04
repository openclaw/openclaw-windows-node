---
name: openclaw-tracing
description: "Record and analyze bounded OpenClaw Windows Gateway startup, Companion setup and real chat performance traces, including CPU, memory, I/O, Defender activity and timing. Requires explicit live-capture authority and keeps raw evidence private."
---

# OpenClaw Windows performance tracing

Use for slow startup, setup timeouts, chat response latency, resource-use
comparisons, or analysis of an existing Gateway performance recording.
Read the [trace runbook](../../../scripts/gateway-startup-trace/README.md)
before running commands. It owns parameters, prerequisites, output schemas,
metric definitions and recorder recovery. This skill selects the workflow and
the evidence required for an honest result, not a second implementation.

## Select the question and authority

Start with the reported delay and the exact measured boundary. Choose one:

| Question | Entry point | Boundary |
|---|---|---|
| Startup before the listener, host versus MXC | `scripts\Invoke-GatewayStartupTrace.ps1` | Trusted synthetic probe, one host and one temporary exact-owned MXC session |
| Real Companion native setup and timeout milestones | `scripts\Invoke-GatewaySetupTrace.ps1` | User starts setup in the already-running intended Companion |
| Actual chat interaction | `scripts\Invoke-GatewayChatTrace.ps1` | Already-running selected processes, operator-observed send/response/terminal markers |
| Existing recording only | Matching `Get-Gateway*TraceSummary.ps1` under `scripts\gateway-startup-trace` | Offline analysis; no new capture or workload |

Analysis permission does not authorize recording, app launch, setup, sending a
chat request, package registration, installs, reboots or runtime changes.
Obtain explicit authority for the selected live workload and elevated
machine-wide capture. UAC approval must come from the user; never bypass it.
If authority or prerequisites are missing, analyze saved evidence or report
the blocker rather than silently switching workloads.

Do not disable protection, add Defender exclusions, change power/system
settings, install tools, or stop another session's app, Gateway or recorder.
Do not clear caches or force prewarm to make a run faster. Cold-run reboots
need explicit consent at that time.

For app launch or native package preparation, follow
[proof validation](../openclaw-proof-validation/SKILL.md) and
[native Gateway from source](../native-gateway-from-source/SKILL.md)
with separate runtime authority. For failure-boundary diagnosis rather than
performance capture, use [Windows debugging](../openclaw-debugging/SKILL.md).

## Prepare before the measured interval

1. Record the question, source/build/artifact identities, architecture, process
   selection, payload roots, expected milestones, timeout and comparison order.
   Verify each selected PID belongs to the intended runtime. Include the
   Companion, Gateway host and local-model process where applicable.
2. Read the selected controller's parameters and the runbook. Use PowerShell 7
   **unelevated**; only the named recorder requests elevation. Check WPR and
   ConfigDefenderPerformance availability without starting capture.
3. Build the offline analyzer before a cold-run preparation:

```powershell
dotnet build .\scripts\gateway-startup-trace\Analyzer\GatewayTraceAnalyzer.csproj
```

4. Choose a private output directory, preferably under the current session's
   files directory. Controllers create an ACL-restricted per-run directory.
   Supply previously computed, validated `-BuildIdentityPath` metadata when
   available. Do not hash/read the payload immediately before a cold run.
5. Coordinate a quiet capture window with other active sessions when needed.
   Do not run builds, tests, downloads or other heavy agent work during the
   recording. Keep run order and the sampling delay identical in comparisons.
   Record unavoidable contention as a confounder.

The synthetic probe requires a payload root with metadata, a matching Node
archive/hash and cached MXC SDK. Its guard is not a security sandbox: it patches
selected Node APIs, but trusted payload code can still access files/native
modules. Real setup requires the correct log directory and payload aliases.
Executable-image selection is not descendant selection; use
`-ResourceImageRoot` or `-ResourceProcessId` for setup hosts outside those roots.
An MXC guest PID must not be assumed to be a host PID.

## Record one bounded workload

Use the runbook's command for the chosen mode and explicit timeouts. Do not
launch the elevated recorder directly or substitute a machine-wide unnamed
WPR command. Preserve the printed run directory and exact instance name.
Wait for recorder readiness before initiating the workload.

- **Startup:** keep the requested host/MXC order. The probe exits before any
  listener. Preserve its result and cleanup receipts before scratch removal.
- **Setup:** start setup in the intended Companion only after readiness.
  Follow the pinned attempt, including a possible unrecorded prefix if it
  began during UAC. `listener-verified` is a milestone, not success.
  `reached-ai-setup` means Gateway preparation reached AI setup, not completed
  onboarding.
- **Chat:** use a non-sensitive test prompt in the existing connected UI.
  The controller does not send prompts or inspect chat content. From a
  second console, use `scripts\Write-GatewayChatTraceMarker.ps1` with the
  printed run directory and `send`, `first-response`, then `completed`,
  or `failed`/`cancelled`. Record only events actually observed. Do not
  automate markers using guessed response timing.

Manual stop and timeout are incomplete outcomes, not successful interactions.
Save failed recordings and their receipts when available; do not silently
discard failures or rerun until a favorable result appears.

## Verify recorder shutdown

The controller requests stop; the elevated recorder saves or cancels only
`OpenClawStartup-<guid>`. Check the exact matching
`control\recorder-status.json`: a saved result needs `outcome = saved`,
`instance_recording_after = false` and an existing ETL. Missing or invalid
status is not proof of shutdown. Preserve failure/cancel results explicitly.

The recorder is not crash-safe. Closing/killing its window can leave capture
active. With user-approved elevation, use the **exact printed instance**:

```powershell
wpr.exe -status -instancename OpenClawStartup-<guid>
wpr.exe -cancel -instancename OpenClawStartup-<guid>
wpr.exe -status -instancename OpenClawStartup-<guid>
```

Cancel only a verified task-owned recording, then verify not-recording.
Never use bare `wpr -cancel`. Unknown status remains a blocker.
Do not claim independent Gateway guest exit from a teardown acknowledgment.
Preserve evidence before removing task-owned profiles or scratch state.
Package/profile cleanup needs the [uninstall skill](../uninstall/SKILL.md)
and explicit destructive authority; trace analysis does not grant it.

## Analyze and interpret

Use the matching summary script on the saved ETL and its original result:
`Get-GatewayStartupTraceSummary.ps1`, `Get-GatewaySetupTraceSummary.ps1` or
`Get-GatewayChatTraceSummary.ps1`. Pass the same payload roots, including
junction aliases where applicable, and a fresh output path. Never invent
missing samples or replace the original markers with reconstructed guesses.

- Include the interval before the first recorded phase as **unclassified**.
  It is not proof of Defender, disk or JS parsing delay.
- CPU time, sampled CPU events, overlapping scans, physical disk response time
  and phase windows cannot be added as exclusive wall-clock contributions.
  Correlation is not causal blocking.
- Memory peaks/timelines are sampled. Process counters exclude inaccessible
  and missed short-lived processes; one observation cannot measure a delta.
  Process logical I/O can include pipes/network/device I/O. Physical disk
  activity is machine-wide, not attributed Gateway delay.
- Check event loss, provider observation flags, maximum sample gaps, awake/
  clock gaps and process lifetime coverage. No matching events is unobserved,
  not proof of zero work. Old recordings without samples cannot yield memory
  history. Capped Defender reports fail rather than claim completeness.
- Chat markers include human reaction, console switching and scheduling.
  They are not instrumented server TTFT, provider latency or tokens/second.
  Remote Gateway/model CPU and memory are not measured by a local trace.
- File cache and Defender scan cache are separate. Report boot/cache state,
  standby, run order, thermal/power conditions, background work and observer
  overhead. A single warm or noninterleaved run cannot establish a cold-run
  improvement.

## Evidence handoff and validation

Keep ETLs, copied setup logs, raw samples and correlation/configuration inputs
private. Do not upload them or paste raw paths, command lines, user content,
session identifiers or credentials. Share only inspected sanitized summaries
and anonymous resource timelines, with payload-relative package/file names.

Report exact commands/results, tool and artifact identities, run order,
milestone timings and outcomes, selected-process coverage, resource/Defender
aggregates, confounders, private evidence locations, and the final exact named
recorder state. Separate cleanup receipts, actual guest-exit proof, offline
parser checks and live workload proof. State missing proof plainly.

When modifying trace tooling, use [Windows testing](../openclaw-testing/SKILL.md)
and the runbook's analyzer build and two offline trace suites, then required
repository closeout. A passing build/offline suite does not prove UAC,
current-head WPR providers, MXC guest attribution or real chat timing.
