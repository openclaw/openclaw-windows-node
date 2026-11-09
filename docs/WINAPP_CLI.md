# WinApp CLI for developers and coding agents

Use Microsoft's **WinApp CLI proactively** for Companion development, not just
packaging. It supplies Windows API/sample discovery, native UI inspection,
WinUI crash diagnostics, XAML DevTools, performance recording, and Windows
Sandbox execution. Start with the relevant capability before inventing a
diagnostic script or guessing at Windows behavior.

This guide covers developer tools and evidence selection. It does not replace
the repository build, test harnesses, release packaging, or runtime authorization.
`.\build.ps1`, the project files, and repository packaging scripts remain
authoritative for both the XAML shell and Reactor chat.

## Install, update, and discover

The researched baseline is [WinApp CLI 0.8.0][release], released November 7,
2026, source `e5cedcc04921f6f74535cf45668292203e439d45`. Upstream still calls
the product **Public Preview**. Use released builds, not unreviewed `main`
artifacts, and record the actual version with evidence.

From the repository root, start without launching an app:

```powershell
Get-Command winapp -All
winapp --version
winapp --help
winapp ui --help
winapp target snapshot sandbox --json
```

`target snapshot` checks host prerequisites and existing guest readiness without
starting, reconnecting, or repairing Sandbox. No running guest is a valid result,
not successful app proof. `setup-dev.ps1 -CheckOnly` and `-AgentReadiness`
currently do **not** check or install WinApp; run these WinApp checks separately.

With permission to install or update developer tools:

```powershell
# Install if absent.
winget install --id Microsoft.WinAppCli --exact --source winget

# Upgrade an existing installation.
winget upgrade --id Microsoft.WinAppCli --exact --source winget

winapp --version
```

The exact package ID is `Microsoft.WinAppCli`. If the package cannot be found,
check `winget search winapp --source winget`, the configured source, and the
[official installation instructions][installation]. Reopen the shell if its
PATH is stale; inspect `Get-Command winapp -All` for a shadowing npm or ZIP copy.
Do not silently substitute a nightly build or weaken signature checks.
**`winapp update` updates project SDK dependencies, not the CLI.**

Agents should inspect `winapp <command> --help` before using unfamiliar flags.
`winapp --cli-schema` returns machine-readable commands, arguments, options and
defaults; save or filter this large JSON instead of repeatedly loading all of it.
Use `--json` on commands that support it, and check their actual error/result
contract as well as the exit code. Agent plugins are not required to use the CLI.
Do not add a WinApp NuGet reference, run `init`, or change SDK versions merely
to make the tool discoverable.

## Choose the capability

| Need | Preferred WinApp tool | What it establishes, and what it does not |
|---|---|---|
| Verify a Windows API or find a native control pattern | `find-api`, `find-ui` | API metadata from restored project packages; real Gallery/Toolkit samples, with Reactor opt-in. Samples still need to match our versions, owners and design resources. |
| Inspect or automate a mounted Windows UI | `ui inspect`, `search`, `get-property`, `wait-for`, `invoke`, `get-value` | Real UIA elements and outcomes. A tree is not pixel, keyboard, screen-reader, or Gateway proof. |
| Capture a native/managed crash | `run --debug-output`, optionally `--symbols` | Debug output, first-chance exceptions, crash dumps and WinUI stowed-exception triage. It is not a general attach-to-existing-testhost or hang-dump command. |
| Explain a XAML property, layout, or binding | `devtools inspect`, `get-property`, `get-source`, `diagnose-binding` | In-process WinUI diagnostics. Late attachment and generated Reactor controls have source/binding limits. |
| Explain expensive layout, scrolling, parsing or GC overlap | `perf start`, `mark`, `stop`, `analyze`; `run --profile` | Bounded WinUI ETW and process counters. Not sampled CPU stacks, disk/Defender activity, GPU time, displayed FPS, or end-to-end latency. |
| Run an app away from the developer's desktop/profile | `run --on sandbox`, `ui --on sandbox`, `target` | Guest deployment, runtime provisioning, commands, file transfer and captures. Builds still run on the host; one guest is shared, not one sandbox per agent. |
| Explore identity or MSIX behavior | `run`, `create-debug-identity`, `package`, `tool` | Development registration and Windows SDK tooling. Not Store ingestion, signed-upgrade, reputation, or production-identity acceptance. |

**New versus already available:** 0.8.0 adds DevTools, WinUI performance
recording, and `run --unique-identity`. UI automation, crash diagnostics and
persistent Sandbox execution already existed; this release improves them.
Relevant fixes include WebView2 UIA hangs, popup capture, minimized-window
handling, typed single-element selectors, crash-context attribution, Sandbox
prerequisite reporting, and expected-instance binding. Do not attribute every
capability to 0.8.0 or assume an upstream fix resolves an OpenClaw defect.

## Recommended agent workflow

### Ground the change before writing code

Search existing repository owners and tests first. For Windows API or control
questions, supplement that search with:

```powershell
winapp find-ui 'ItemsView' --source reactor --json
winapp find-ui 'NavigationView' --json
winapp find-api members 'Microsoft.UI.Xaml.Controls.ItemsView' `
    --project-dir .\src\OpenClaw.Tray.WinUI `
    --project OpenClaw.Tray.WinUI --json
```

`find-api` needs the project's restored package metadata; missing metadata is
not proof that an API does not exist. `find-ui` has a bundled corpus and can
refresh public samples over the network. Reactor is excluded from its default
search: use `--source reactor` for chat, not for ordinary XAML pages.
Follow [DESIGN.md](../DESIGN.md) and the existing architecture owner rather than
pasting a sample or introducing another UI framework.

### Select and verify the runtime

For app-only work, use the [fixture-backed app](GATEWAY_FIXTURE_TESTING.md#build-and-explore)
with an explicit current-build executable. It prints the owned PID, isolated
profile and artifact directory. Keep its process lifecycle and synthetic
Gateway under the fixture's control. For real Gateway work, follow the
[runtime-selection rules](../.agents/skills/openclaw-proof-validation/SKILL.md#select-the-runtime-and-its-authority).

Do not replace either path with an unqualified `winapp run .` at the solution
root. The launcher can build, register packages, replace a running app, or
select a different identity. `run-app-local.ps1 -UseWinApp` is the existing
integration: it supplies the build-output folder, source `Package.appxmanifest`,
executable and, by default, `--debug-output`. It does **not** forward Sandbox,
DevTools, profiling, or unique-identity options. `-NoBuild` skips only the
Companion build, and the normal launcher can still prepare a native Gateway.

Before any authorized alternative launch, verify the built identity,
configuration, architecture, resolved profile, selected Gateway and process.
`-Isolated` is profile isolation, not OS or package-registration isolation.
`-Dev` does not by itself rewrite the source manifest passed by `-UseWinApp`.
Use the [Dev MSIX workflow](../DEVELOPMENT.md#local-development-msix) when a
generated side-by-side package identity is needed.

### Observe, act, and assert

Use the verified PID, not a broad `OpenClaw` name that could select another
session's app. Replace the placeholders below with values from that run:

```powershell
$appPid = '<owned-app-pid>'
winapp ui list-windows --app $appPid --json
winapp ui inspect --app $appPid --interactive --json
winapp ui wait-for '<selector-from-inspect>' --app $appPid --timeout 10000 --json
winapp ui get-value '<selector-from-inspect>' --app $appPid --json
```

Prefer AutomationId and scoped `--root`, `--type`, or `--class-name` queries.
Use the returned HWND with `--window` to disambiguate dialogs. Reinspect after
navigation or recycling; a stale slug is not a stable test identity.
Wait for the actual required condition, then assert the expected value/state.
Existence alone does not establish visibility, readiness, focus or correct data.

In an authorized CLI-driven test, prefer UIA `invoke`/`set-value` for behavior
that does not depend on physical input. Preserve dedicated native keyboard,
caret, IME, focus and screen-reader tests; replacing Shift+Enter with `set-value`
would bypass the regression. UIA inspection does not replace Axe.Windows or
human assistive-technology checks.

Honor the agent host's computer-use and consent rules. When those require
computer-use for direct local interaction, use that engine rather than shell
input injection; WinApp remains useful for diagnostics and controlled test
harnesses. Do not mix independent input engines on one desktop.

Mouse, touch, pen and SendInput operations require an unlocked interactive
desktop. Screenshots may restore or foreground a target. For cooperating
WinApp recording/actions, set the same `WINAPP_UI_WORKFLOW_ID` on **every**
invocation, bound recordings with `--duration-sec`, and call `winapp ui yield`
after both recording and actions finish. CLI arbitration does not coordinate
humans, computer-use, existing UI tests, or direct NuGet callers. Reinspect after
a pause. Never remove a focus/ownership check just to complete a test.

### Diagnose a crash or inspect XAML

For an authorized disposable packaged repro, the launch shape is:

```powershell
winapp run '<built-layout>' --manifest '<validated-manifest>' `
    --executable 'OpenClaw.Tray.WinUI.exe' --debug-output --symbols
```

Use matching binaries/PDBs and a verified isolated profile. Do not register our
production manifest over an installed Companion. `--debug-output` occupies the
process's debugger slot and cannot be combined with `--detach` or `--json`;
do not attach an IDE debugger simultaneously. Symbol and triage dependencies
may download on first use. Check the log for triage completion or its blocker,
not just the outer `CoreMessagingXP.dll` / `Microsoft.UI.Xaml.dll` crash module.
Keep existing WER/testhost diagnostics for failures not launched this way.

For XAML inspection of an already authorized fixture process:

```powershell
winapp devtools attach --pid $appPid `
    --source-root .\src\OpenClaw.Tray.WinUI --json
winapp devtools inspect --app $appPid --all --json
winapp devtools get-property '<verified-selector>' ActualHeight --app $appPid --json
```

Attachment injects an engine until process exit; it is not read-only discovery.
Late attachment cannot add the managed startup hook or launch-time source
information. Full binding/source diagnosis may need a separately authorized
project launch with `--devtools headless`; do not relaunch the fixture
independently or claim that C#-generated Reactor elements map to authored XAML.
Our non-Dev Release build disables managed debugger support, so record that
configuration and do not silently substitute Debug evidence for Release.

DevTools live edits are experiments, not source fixes. They can replace
bindings, and restoring compiled bindings may reapply an entire owner.
Make the real correction in XAML/C#/`.resw`, rebuild and verify without the
override. Comments are stored in the repository's `.winapp\ui-comments.json`;
review their content before committing or sharing. Source getters/converters
can run during binding diagnosis. Never attach to another session's app.

### Measure a slow native UI

Use a task-owned, already running WinUI process and an empty private output
directory. Capture a bounded synthetic navigation/scroll/expansion scenario:

```powershell
$capture = winapp perf start --app $appPid --output '<empty-capture-directory>' `
    --duration-sec 30 --max-size-mib 256 --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $capture.id) { throw 'WinApp capture did not start.' }
winapp perf mark $capture.id --name scenario-start
# Exercise the authorized scenario and wait for its actual completion.
winapp perf mark $capture.id --name scenario-end
winapp perf stop $capture.id
winapp perf analyze '<capture-directory>' `
    --from-marker scenario-start --to-marker scenario-end --json
winapp perf analyze '<capture-directory>' --view hotspots --json
```

Check each command's result, capture status and `coverage`, including provider
failures, event loss, size-limit stops and partial data. Analyze can return useful
partial stdout with a nonzero exit; retain it without calling the result complete.
Inspect `--view calls`, `--view gc` and `--view resources` for expensive operations,
suspension overlap, private bytes and handle counts. No observed events does not
mean no cost. Traces can contain source paths, URIs and user data; filtering an
analysis report does not sanitize raw ETL.

`run --profile` normally attaches after launch and can miss startup. Recording
from launch uses `--profile-mode elevated`, which needs separate UAC approval.
Profiling is **host-only**: `--profile --on sandbox` is unsupported. Do not
profile with debugger pauses or claim GPU/Defender attribution from WinUI ETW.
Stopping a capture does not stop the app.

### Choose WinApp Sandbox or sandbox-mcp

Our [sandbox-mcp project][sbx-project] is also a first-choice tool.
Its `sbx` CLI and optional **`windows-sandbox` MCP server** use the same engine;
MCP is not required when the CLI is available. Discover the server's actual tool
schemas in the agent host, rather than assuming a particular prefix or inventing
arguments. This comparison checks the installed plugin and advertised tools at
[sandbox-mcp 1.2.0][sbx-release] against WinApp 0.8.0.

**Choose by the question, not the interface or framework.** Both support WinUI
and other Windows apps. WinApp optimizes getting a developer's app running with
identity and diagnostics; sandbox-mcp optimizes finding what a fresh customer
machine lacks and what installation/execution leaves behind.

| Question | Preferred path | Why / evidence boundary |
|---|---|---|
| Does this exact EXE or MSIX work without my developer-machine prerequisites? | `sbx deps`, then authorized `sbx run`; MCP `sandbox_deps` / `sandbox_run` | Static diagnosis followed by a fresh guest, with no implicit app-runtime installation. Supply a behavioral test and inspect screenshots/crash events; startup PASS alone is insufficient. |
| Does the artifact work without any guest network adapter? | sandbox-mcp `network: "off"` / `sbx run --network off` | Control uses the shared-folder mailbox. Both tools' ordinary development paths must not be assumed offline. sandbox-mcp's normal run/start default is network **on**; `firewall` is weaker than no adapter because guest code runs as admin. |
| What does our Inno installer add, and does uninstall remove only what it should? | `sandbox_install_test` / `sbx install-test` | Before/install/uninstall snapshots, file/registry/service/task/autorun differences, lingering processes, error logs and durable reports. Use the actual installer, not a loose-layout registration. |
| Does a packaged app activate with its shipped dependencies? | `sandbox_run` with the exact MSIX, public `cert`, offline `deps` and `appId` where needed | Installs supplied dependencies and activates the package without a Store download. Record those inputs. The installer-audit tool is for MSI/EXE/scripts, not a substitute MSIX uninstall audit. |
| I need a warm project build/deploy/debug loop with package identity and WinUI diagnostics | WinApp `run --on sandbox` and `ui` / explicitly enabled `devtools --on sandbox` | Host build and incremental deployment, automatic supported guest-runtime provisioning, package-aware activation and WinUI tooling. Good developer loop, not an untouched-machine dependency test. |
| I already own a sandbox-mcp guest and need to iterate a prebuilt payload | `sandbox_deploy` / `sbx deploy` | Verified changed-file transfer and optional targeted restart. No reason to switch controllers just to deploy; warm state is not a clean baseline. The dedicated destination has removed/extra files deleted. |
| Why is a native chat view slow or throwing a stowed exception? | Fixture/authorized repro plus host WinApp `perf`, `devtools`, or `run --debug-output` | Prefer the existing app boundary. Do not start either Sandbox unnecessarily. WinApp profiling is not supported in Sandbox. |
| What does an explicitly authorized untrusted file do? | `sandbox_detonate` / `sbx detonate`, normally network off | Bounded behavior observations and system differences, not a malware verdict or proof of safety. Never use WinApp's host build/restore path for untrusted source. |
| Does real WSL/MXC, Store migration, hardware or native ARM64 behavior work? | The existing qualified proof pool | Neither tool supplies these guarantees by launching an app. sandbox-mcp's 1.2.0 portable distribution is x64; upstream sample results do not prove Companion or ARM64 acceptance. |

For sandbox-mcp, start with read-only `sbx doctor`, `sbx status`, and
`sbx deps '<artifact>'` when that CLI is installed. MCP exposes `sandbox_status`
and `sandbox_deps`; the [setup guide][sbx-setup] covers prerequisites.
An installed MCP plugin does not imply `sbx` is on PATH. Use the available
interface rather than installing/configuring a second one without permission.
The [app-test][sbx-app-skill], [installer-test][sbx-installer-skill] and
[interactive][sbx-interactive-skill] skills document exact recipes.

**Keep a missing dependency as a finding.** Do not quietly rerun a failed clean
test through WinApp's automatic runtime preparation, or add sandbox-mcp
`provision: "auto"` to make it green. If provisioning is explicitly requested,
retain the original failure and label the new run **provisioned, not a
clean-machine pass**. Supplied MSIX dependency packages are also recorded test
inputs. Warm deploy, arbitrary `exec` installs and an adopted guest are not a
fresh baseline.

Read `report.json`/`report.md`, the actual test result, screenshots and
`sandbox_app_errors`. An app showing an error dialog can still pass a startup
smoke. Installer leftovers do not automatically fail `sandbox_install_test`;
use `failOnLeftovers` for a contract that requires no leftovers and always
inspect the diff. Intentional retained user state needs explicit preservation
assertions rather than a blanket deletion requirement. Reboot-required exits
do not establish post-reboot success. Snapshots and recordings can contain
private data, and copied-out files remain untrusted.

#### One Sandbox, one lifecycle owner

Both tools use Windows Sandbox's **single host-wide guest slot**. They are not
parallel isolation providers, and their desktop-turn mechanisms are not a
shared cross-tool lock.

Before any start, inspect `sandbox_status` (or `sbx status`) and
`winapp target snapshot sandbox --json`, record the owning workflow, and choose
one controller. sandbox-mcp one-shot runs fail if a guest already exists; WinApp
can adopt a running instance and prepare it. Do not treat either behavior as
permission to take over another task.

Do not routinely mix the controllers in one guest. WinApp preparation changes
its runtime baseline, and its registrations/artifacts are not automatically
owned by sandbox-mcp's reports. If a deliberately mixed diagnostic experiment
is authorized, bind WinApp with `--expect-sandbox <provider-guid>`, serialize
desktop work, record preparation as a changed baseline, and retain the original
lifecycle owner. WinApp's GUID check is not sandbox-mcp's boot epoch, and neither
permits using stale PIDs or stopping somebody else's work.

For an ordinary handoff, export evidence, obtain permission for destructive
teardown of the owned guest, stop it through its owner, verify it is gone, then
start a fresh guest with the other tool. Never use `sandbox_stop force: true`,
remove an expected GUID, or kill an unrelated Sandbox to get past contention.
Stop authority should be agreed before a one-shot workflow, because those
workflows destroy their guest automatically unless explicitly kept.

### Use WinApp Sandbox for the right boundary

WinApp's Sandbox path requires a supported Windows 11 24H2-or-newer client,
virtualization, the enabled feature and an installed Sandbox client. A ready
snapshot is not permission to start or adopt a guest. Windows permits one
Sandbox at a time; it may already belong to another tool or person.

After an explicitly authorized guest has been created, record its provider GUID
from `wsb list` and bind **every** command to it:

```powershell
$sandboxId = '<owned-provider-guid>'
winapp run '<approved-project-or-layout>' --on sandbox `
    --expect-sandbox $sandboxId --detach --json
winapp ui inspect --on sandbox --expect-sandbox $sandboxId `
    --app '<returned-guest-pid>' --json
winapp target snapshot sandbox --expect-sandbox $sandboxId --json
```

Keep `--on sandbox` even with a guest PID/HWND; omitting it targets the host.
An expected GUID prevents starting/adopting another guest, but is not a boot
identity or exclusive lease. Use a new GUID for each new guest. Preparation adds
guest tooling, shared bootstrap folders, Developer Mode and a firewall rule,
and may install guest runtimes. Builds/restores still execute on the host.
Starting/reconnecting can briefly take focus.

Use `target push`, `exec`, `pull`, `screenshot` and bounded `record` for approved
installer experiments and artifacts. Transfer destinations are relative to the
reported guest `workRoot`; use the returned `targetPath`, not an assumed drive.
App/desktop capture outputs return to the host. Export evidence before ending
an owned guest; never stop an adopted guest or use `--clean` without the
appropriate destructive permission. A warm persistent guest is not a fresh
installation. Missing runtimes, desktop readiness or delivery remain blockers.

Keep Sandbox distinct from MXC, WSL2 and real Gateway/node proof. It can help
with clean app/installer experiments, but does not establish Store upgrade,
hardware/microphone/GPU behavior, ARM64 parity, or nested WSL/MXC support.
Use the existing [proof pools](PROOF_POOLS.md) and
[Store migration procedure](STORE_MIGRATION_TESTING.md) for those claims.

### Do not assume unique identity fits Companion

`run --unique-identity` derives a package name and execution aliases from a
checkout path without editing its source manifest. However, 0.8.0 rejects
unpackaged/sparse/bundled apps and manifests with system-wide extensions such
as protocols. Our [manifest](../src/OpenClaw.Tray.WinUI/Package.appxmanifest)
declares the `openclaw` protocol, COM/toast activation and startup behavior.
The normal project build is also unpackaged unless `PackageMsix=true`.

Do **not** add the flag blindly to `run-app-local.ps1`, strip those extensions
to claim production-package proof, or assume it isolates app data, mutexes,
Gateway ports or WSL state. Use existing Dev identity/profile controls or a
disposable qualified host. Revisit upstream compatibility before proposing a
launcher change.

## Quality opportunities from our history

This is a sampled August-November 2026 issue/PR review, checked November 9 UTC,
not a claim that every old report is still open or has one root cause.
Suggestions below are **not implemented CI gates or completed runtime proof**.

| Priority | Observed history | Concrete next use and acceptance signal |
|---|---|---|
| First: make crashes actionable | [#1270](https://github.com/openclaw/openclaw-windows-node/issues/1270) (Windows Tray app crash-loops with Microsoft.UI.Xaml.LayoutCycleException under rapid event throughput), [#1074](https://github.com/openclaw/openclaw-windows-node/pull/1074) (fix(chat): avoid layout cycles during tool expansion), and [#2069](https://github.com/openclaw/openclaw-windows-node/issues/2069) ([Bug] Hub 2026.10.5-alpha.4 crashes on chat send (0xc000027b in CoreMessagingXP.dll) against 2026.11.1 gateway) show recurring layout/stowed-exception investigations. | Run a synthetic, debugger-compatible repro with `--debug-output --symbols`; retain exact binary/PDB identity, terminating exception, triage verdict and visible-window outcome. Success is an actionable failure location and regression reproduction, not merely a process that stayed alive. |
| First: measure chat layout instead of guessing | [#1409](https://github.com/openclaw/openclaw-windows-node/issues/1409) (tray connects but no window opens; initial-tail layout loop) and [#1935](https://github.com/openclaw/openclaw-windows-node/pull/1935) (fix(chat): chat freezes when opening tall message history) describe costly or blocked native scrolling. The latter remains an open candidate, not an accepted fix. | Profile the same fixture history, tall-row expansion, session switch and reader-scroll scenario before/after. Compare scenario completion, complete layout-call p95/max, observed process costs and coverage on the same host/configuration. Keep visible tail placement and reader-position assertions; faster timings cannot excuse clipping. |
| First: diagnose flaky UI without bypassing input | [#1922](https://github.com/openclaw/openclaw-windows-node/issues/1922) ([Lever lane] Reduce mock Gateway UI scenario failures and rerun waste), [#1981](https://github.com/openclaw/openclaw-windows-node/pull/1981) (test: stabilize flaky UI-job and SetupEngine CI failures), and [#1492](https://github.com/openclaw/openclaw-windows-node/issues/1492) ([Bug]: Shift+Enter in chat composer moves caret to start of text instead of inserting newline). | Add targeted UIA tree/property evidence and bounded condition waits to an owned repro; capture flyouts with the improved popup support. Compare failure/rerun rate separately from duration. Preserve keyboard/caret tests, missing-control failures, actual test counts and existing time bounds. Do not change the main harness solely to adopt a different API. |
| Next: inspect accessibility and binding failures at the control | [#1945](https://github.com/openclaw/openclaw-windows-node/issues/1945) ([Bug] Screen reader hits unnamed, icon-only and type-named controls in Settings, Bindings, Config, Sessions and Usage) exposes gaps between visible labels and UIA. | Use `ui inspect/get-property` for names, roles and enabled state, plus DevTools for the realized control and value source. Require existing Axe tests and a focused screen-reader/keyboard journey; a DevTools tree alone is not accessibility acceptance. |
| Next: isolate installer and desktop side effects | [#1374](https://github.com/openclaw/openclaw-windows-node/issues/1374) (Migrate from the Inno (.exe) app to the Store MSIX), [#2021](https://github.com/openclaw/openclaw-windows-node/issues/2021) (Bug Report: Console window opens every ~60 s and steals focus on Windows (MSIX companion cleanup task)), and [#1961](https://github.com/openclaw/openclaw-windows-node/pull/1961) (fix(test): a plain Tray test run writes into the real Companion profile) concern state/focus leakage. | Prefer sandbox-mcp's fresh artifact run and installer/uninstaller diff for this pilot, without implicit runtime provisioning. Use synthetic state and whole-guest recording; assert expected preservation and cleanup. Retain event-based transient-window checks, since video can miss brief flashes. Keep ordinary test-profile isolation fixes and real WSL migration proof separate. |
| Ongoing: reduce speculative Windows code | The XAML shell and Reactor chat use different authoring surfaces and pinned packages. | Use project-scoped `find-api` and opt-in Reactor `find-ui` before proposing APIs or patterns. Validate against current packages and tests; sample discovery is not compilation or performance proof. |

Start with one opt-in crash/layout investigation and one deterministic fixture
scenario, not a wholesale test-runner migration. If a repeatable pilot improves
diagnosis, propose the smallest harness integration separately. Before a CI
change, pin the CLI release, preserve test inventory and assertions, compare a
same-source baseline, and define artifact privacy/retention. WinApp's automation
NuGet packages and setup action are options, not dependencies this guide adds.

WinApp does not solve Gateway protocol/authentication errors, Node version
selection, WSL service ownership, signing/promotion failures or Local AI GPU
compatibility. Follow those production owners and keep the unresolved findings
in [CI_FAILURES.md](CI_FAILURES.md) and their issues.

## Evidence and maintenance

For this documentation update, the local CLI was upgraded from 0.7.1 to 0.8.0
through WinGet with installer-hash verification; its installed package reported
0.8.0.0 and `Ok`. Version/help/schema discovery and a Reactor ItemsView sample
query succeeded. The read-only Sandbox snapshot reported host prerequisites
ready and no running guest. No Companion, debugger, DevTools injection,
performance capture, installer trial or Sandbox app run was performed.
The configured `windows-sandbox` MCP server also answered `sandbox_status` with
no running guest or other Sandboxes. Its installed plugin identifies as 1.2.0;
`sbx` was not on PATH. No server update or second CLI installation was needed
to discover its tools. This is connection/discovery evidence, not a guest run.

These are tooling checks, not proof that the proposed workflows fix a product
defect. Commands were checked against the installed 0.8.0 schema and the
release-tagged sources below. Recheck help/schema after upgrades and preserve
the distinction between released capabilities, local integration and experiments.

- [Release and change list][release]
- [Command reference][usage] and [UI automation][ui]
- [Debug output and stowed exceptions][debugging]
- [WinUI performance recording and interpretation][perf]
- [DevTools][devtools] and [advanced targeting/attachment limits][devtools-advanced]
- [Sandbox prerequisites, ownership and execution][sandbox]
- [sandbox-mcp project][sbx-project], [1.2.0 release][sbx-release] and
  [measured compatibility and limitations][sbx-compatibility]

[release]: https://github.com/microsoft/winappCli/releases/tag/v0.8.0
[installation]: https://github.com/microsoft/winappCli/blob/v0.8.0/README.md#-installation
[usage]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/usage.md
[ui]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/ui-automation.md
[debugging]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/debugging.md
[perf]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/guides/winui-performance.md
[devtools]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/guides/devtools.md
[devtools-advanced]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/guides/devtools-advanced.md
[sandbox]: https://github.com/microsoft/winappCli/blob/v0.8.0/docs/sandbox-execution.md
[sbx-project]: https://github.com/shanselman/sandbox-mcp
[sbx-release]: https://github.com/shanselman/sandbox-mcp/releases/tag/v1.2.0
[sbx-setup]: https://github.com/shanselman/sandbox-mcp/blob/v1.2.0/docs/agent-setup.md
[sbx-compatibility]: https://github.com/shanselman/sandbox-mcp/blob/v1.2.0/docs/compatibility.md
[sbx-app-skill]: https://github.com/shanselman/sandbox-mcp/blob/v1.2.0/skills/windows-sandbox-app-test/SKILL.md
[sbx-installer-skill]: https://github.com/shanselman/sandbox-mcp/blob/v1.2.0/skills/windows-sandbox-installer-test/SKILL.md
[sbx-interactive-skill]: https://github.com/shanselman/sandbox-mcp/blob/v1.2.0/skills/windows-sandbox-interactive/SKILL.md
