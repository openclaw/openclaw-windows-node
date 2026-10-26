# Private-desktop MXC regression

## SDK initialization access evidence

On an explicitly authorized guest, the native probe records token owner,
groups (including deny-only attributes), restricted SIDs, capabilities,
mandatory policy and default DACL before dynamically loading USER32. Named
caller-supplied current/default desktop and station descriptors have separate
per-right `AccessCheck` results. API failure is recorded separately from access
denial. These DACL checks do not establish mandatory-integrity access or prove
which object the SDK assigned.

After successful USER32 loading, the probe records the actual assigned station
and desktop names and security descriptors. A failed load leaves that binding
explicitly unknown. Do not treat the supplied caller descriptor as the failed
SDK object's descriptor, or infer permission from Low/AppContainer status alone.
Capture both unchanged NULL-security and explicit-security variants before
changing any task-owned creation code. Their original success assertions remain
required.

The probe's `--test-descriptor-arguments` mode runs five argument-parser checks
and exits before token, desktop or USER32 queries. Its `--token-only` mode is
guest-only and exits before USER32 loading, while still collecting live token
evidence. The executable must not statically import USER32.

The harness's guest-only `--caller-preflight <native-library> <receipt>` mode
loads and frees the exact supplied native library and records source version,
SDK/native hashes and caller desktop context. Configure and verify the medium
caller's actual .NET root, executable and version before this apphost preflight.
An elevated recorder must validate the still-live original medium caller and
its creation identity before starting the one authorized startup trace. A
preflight receipt alone is not startup, SDK success or containment proof.

## Fresh-guest observer diagnostics

`Observation` owns the passive quiet-lane proof observer, promoted from the
incident capture artifact. It is not started by the harness executable.
Its hook process/thread filters remain 0/0, with OUTOFCONTEXT flags and
unchanged dialog/SHOW coverage. Incomplete classification still fails.

Run the native-free diagnostic regressions on the controller:

```powershell
pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Test-ObserverEvidence.ps1
pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Test-ProofCaseCollection.ps1
pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Build-ObserverEvidence.ps1 `
  -OutputDirectory '.\artifacts\observer-evidence'
```

Use a fresh PowerShell process for each command so previously loaded types
cannot shadow newly compiled source. The build only compiles a DLL. It never creates an observer or queries local
windows/tokens. Preserve each output directory. On an explicitly authorized
fresh guest, the existing proof controller can load this DLL with
`Add-Type -Path`: bind the fresh manifest's `observerSource` to the staged DLL
and `observerSha256` to its actual hash. Do not reuse or relax the older
capture manifest's source pins. The original controller and all managed,
SDK-four-controls, ownership-timeout-tree, and cleanup gates remain required.

`Test-DesktopFidelity.ps1` collects all four independent SDK cases even when an
earlier case fails, then runs the ownership regressions. The final
`desktop-fidelity-regression.json` preserves each failure and the separate
ownership outcome. Any failed case or ownership regression still makes the
script fail. A complete receipt is not evidence that its cases passed.
The collector preserves the case callback's output-directory binding across
successful and failed cases.

The observer records the original fallback's own OpenThread, owner, timing
and close results independently of the later diagnostic retry. Limited-query
access remains 0x0800. Successful handles are retained through owner/lifetime
queries and then closed. Startup and incomplete-classification records
capture process and effective thread-token identity, integrity and elevation
without changing privileges. These account identifiers are guest evidence,
not credentials; review them before publication. A failed token capture or
thread lookup is evidence of uncertainty, never permission to skip coverage.

Raw callbacks retain HWND/object/child/thread/event-time operands and
same-handle SHOW/DESTROY/tombstone correlation. Current thread identity and
numeric HWND correlation do not establish event-time ownership. The pure
tests prove diagnostic error preservation, not real native observation or a
fix for the unresolved thread-824 incident. Native guest proof is mandatory.

Each raw callback also records hook identity, nesting depth and parent sequence.
A separate completion receipt distinguishes entry order from reentrant completion
order. Native class and HWND-owner query intervals and the startup UTC/tick
clock anchor are diagnostic bounds, not atomic lifetime or ownership proof.
Original and retry thread opens, owner, timing and close calls have separate
tick intervals. Raw thread exit FILETIME is undefined for a running thread
according to `GetThreadTimes`; it must not establish termination without
independent lifecycle evidence.

For live callbacks, a synchronous first-window capture precedes logging,
thread/process/token queries and classification. It retains native returns,
last errors and individual start/end ticks for validity, class, **text length
only**, HWND owner PID/TID, rectangle, pointer-width style/ex-style and a second
owner bracket. It never calls `GetWindowText` or stores a title. Classification
uses those original class/owner values, not a later retry. A changed owner
bracket is evidence of inconsistency; a matching bracket does not prove HWND
lifetime identity. Length queries may reenter, so callback context is entered
before this capture and nested snapshots remain independent.

The first fallback thread lookup precedes additional ownership diagnostics.
A retained limited-access process handle supplies PID, creation time, image
path and session where permitted. A system-directory `csrss.exe` match is
labeled **callback-delivery time**, not event-emission ownership. If the HWND
has no owner, a synchronous Toolhelp event-thread snapshot supplies only a
candidate PID and an independently attempted process identity. Toolhelp has no
thread creation identity. Neither a later mapping, an OS class, a CSRSS name nor
an access denial authorizes exclusion. Unknown remains FAIL, including handles
already invalid at callback entry; owned Console/Ghost windows are not exempt.
Protected-process access failures remain explicit unknowns. The observer does
not parse undocumented reserved `NtQuerySystemInformation` fields to invent
missing creation times or image paths.

API contracts: [WinEventProc](https://learn.microsoft.com/windows/win32/api/winuser/nc-winuser-wineventproc),
[GetWindowTextLengthW](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowtextlengthw),
[GetClassNameW](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclassnamew),
[GetProcessTimes](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes),
[NtQuerySystemInformation](https://learn.microsoft.com/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation).

This opt-in real-runtime lane tests the security fidelity of
[`Invoke-PrivateDesktop.ps1`](../../scripts/Invoke-PrivateDesktop.ps1).
It requires an interactive Windows user session, .NET 10, installed MSVC/Windows
SDK tools, and the published MXC SDK runtime pinned by the current Shared project.
It never installs tools,
requests elevation, changes MXC policy, starts a Gateway, or provisions WSL.

The launcher creates a **new** owned desktop with the current Default desktop's
DACL and mandatory label. It refuses an existing name and never modifies Default
or switches the shared input desktop. Windows can add an auto-inherited SACL
control marker; DACL and label ACL contents must remain byte-identical.
Only `-NamePrefix` is caller-selectable; the actual name always gets an
unpredictable per-invocation suffix. Exact desktop names are not accepted.
Caller isolation environment values, including unset values, are restored after
both successful and failed launches.
Before CreateProcess, the launcher temporarily adds `SEM_FAILCRITICALERRORS` and
`SEM_NOGPFAULTERRORBOX` to its process-wide error mode. The child inherits those
flags; `CREATE_DEFAULT_ERROR_MODE` is not used. The exact previous caller mode is
restored immediately in finally, including CreateProcess failure. This covers
DLL initialization failures before managed Main without changing registry or WER
policy. A nonblocking launch still returns without waiting for child readiness.
Only a bounded-wait timeout retains the previous process-tree teardown contract:
the launcher pins the root's managed handle, compares its native creation FILETIME
with the retained CreateProcess handle's identity, calls `Kill(true)`, and waits
on the original native root handle. Identity, termination or wait failures are
explicit cleanup errors. Other failure paths retain exact-root-only cleanup.

Native-handle launch receipts (`.launch.json`) retain parent/child identities,
start times, requested desktop, flags and error modes before writing the ordinary
result receipt. Bounded waits retain explicit native exit status (`.exit.json`).
`0xC0000142` stops the proof with a native-initialization failure, not an inferred
timeout or an expected negative result. The status alone does not diagnose its
underlying cause.

Run from the repository root with the exact SDK native library from the managed
probe's build output and its independently verified input digest. The probe
requires that exact path to be loaded by the published SDK, rather than
overriding native discovery. Use an empty output directory:

```powershell
.\tests\OpenClaw.DesktopHarness\Test-DesktopFidelity.ps1 `
  -NativeRuntimeLibrary '<absolute path to the verified SDK native DLL>' `
  -ExpectedNativeSha256 '<verified 64-character SHA256>' `
  -OutputDirectory '<empty proof directory>'
```

The script builds the managed probe and discovers an existing MSVC toolchain to
build `token-probe.c`. Developer environment setup and native compilation stay
in the same process. Alternatively, pass `-NativeProbeExecutable` with a build
of that source. No package or workload installation is attempted.
When supplying `-NativeProbeExecutable`, also supply
`-NativeInitFailureExecutable`. The default native build creates that executable
and its statically imported DLL from `token-probe.c` with
`OPENCLAW_FAIL_ATTACH_EXE` and `OPENCLAW_FAIL_ATTACH_DLL`. The DLL returns FALSE
from `DLL_PROCESS_ATTACH`, producing a real loader failure before executable Main.
Build is safe; executing this failure fixture requires explicit coordinator/Home
approval after reviewing the quiet-launch fix. Never run an intentionally
unquiet negative control on a shared host.
`Test-LauncherOwnership.ps1 -QuietStartupOnly` selects the native-only startup,
receipt-failure, nonblocking and environment lane. It returns before the managed
SDK probe and live-contender lane. Its receipt explicitly leaves visible
no-modal observation unverified for the coordinated Home observer.

Four actual child-process cases must pass, with no skips: native token/USER32 and
controlled PowerShell on each of the NULL-security and Default-equivalent caller
desktops. All execute through the authoritative `MxcCommandRunner`. Its current
request builder selects container UI isolation, UI API compatibility, no
clipboard, no input injection and denied ingress/egress. Both caller desktop
variants must initialize USER32 and return contained exit 0 without a timeout.
The positive PowerShell cases must emit the exact controlled marker.

NULL describes the attributes passed at creation, not the final admission
security. `Invoke-PrivateDesktop.ps1` retains that original descriptor receipt,
then completes only its newly created, exclusively owned desktop with a Low
no-write-up label and the missing `DESKTOP_CREATEWINDOW | DESKTOP_WRITEOBJECTS`
bits (`0x82`) for ordinary/restricted application-package groups. It does not
grant full control, change the station or Default desktop, change tokens, or
override existing write/create deny ACEs. Explicit-security callers keep their
existing byte-equality contract with the Default reference. The separate
`access-completion.json` receipt records the original and applied descriptors.

This fixes a real caller-side admission defect: token-default desktop security
can grant package groups read-only access and omit the mandatory label (implicit
Medium), even when the interactive Default reference supports Low-integrity
USER32. The native probe must prove actual access and initialization; Low and
AppContainer flags alone are insufficient.

Run the fixture-only descriptor regression without live object/token queries:

```powershell
pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Test-PrivateDesktopSecurity.ps1
```

The original MXC 0.8 bridge proof is **HISTORICAL**. Its NULL-security caller
desktop failure predicate is not a current SDK contract. Do not infer a separate
execution desktop from the requested container UI policy. Published SDK 1.0.0
initializes `STARTUPINFOW.lpDesktop` to NULL; the actual native probe can inherit
the caller's task desktop. Requested startup binding and successful
`GetThreadDesktop` identity must establish the real boundary. No legacy bridge
or native executable override is retained.
The policy request, result, loaded native library version/digest and SDK assembly
version/digest are recorded for current-head qualification. Runtime qualification
must confirm this expected policy behavior before claiming this lane passes.

The native probe has no static USER32 dependency, so it records the real child
token, session and Win32k flags before attempting the load. It performs read-only
AccessCheck against captured descriptors; it does not change ACLs or tokens.
The native binary is copied into the case-owned tools directory, the only
explicit read-only tools grant added to the normal selected-folders policy.
Windows runtime access and protected-path denies remain owned by the production
request builder. No personal folder is granted.
All owned desktops must be absent after their exact child exits.
Additional real-launch cases retain a live Default-equivalent contender, verify
that the child's observed desktop is a distinct generated name, reject the old
exact-name parameter, and check caller environment restoration on successful and
failed launches. The child also reports its actual inherited isolation overrides.
Receipt-write failure after successful CreateProcess is covered with an actual
native event-controlled waiting child, independent of managed apphost startup.
The launcher terminates and reaps only the child
identified by its native process handle. A separate successful nonblocking launch
must retain its waiting child until the test releases it. Neither path switches
the input desktop.
The native child also reports its inherited error mode. Success, CreateProcess
failure, postlaunch failure and native DLL initialization failure must all restore
the caller's mode and isolation environment. The DLL failure must exit within the
existing child-signal budget with explicit `0xC0000142` and owned-desktop absence.
No readiness-timeout result qualifies as proof of that failure.
The native-only lane also holds an actual root and descendant on named events
until the existing child-signal bound expires. It verifies both recorded
PID/creation-time instances have exited, the owned desktop is absent and caller
environment/error mode is restored. Test cleanup releases and waits verified
survivors and reports any forced cleanup; it does not silently turn a leak green.
The task-owned test controller uses a known mode with both quiet bits cleared so
the inheritance check detects a missing setter even if ambient PowerShell was
already quiet. No child is launched with that nonquiet mode, and the ambient
controller mode is restored in finally.

For a cleanup baseline, safety-backport only the parent quiet-error-mode change
to the old launcher and label it accordingly. Such a baseline is not exact
historical source. Keep the old missing-cleanup behavior; do not replay the
unquiet historical launcher. Bounded visible no-modal observation must be
coordinated with Home; process exit and private-desktop absence alone do not
establish absence of a system-hosted dialog on another desktop.

`desktop-fidelity-regression.json`, per-case actual config/results and security
records are retained under the supplied output directory. The
`-NullSecurityNegativeControl` launcher option exists only for this regression.
It must not be used for an ordinary private validation run.

This lane does not replace the strict WSL Gateway/MXC formal proof or the required
full build, Shared and private-desktop Tray closeout.

## Explicit one-event attribution limitation

The default observer remains fail-closed. The four-argument constructor is only
for the user-authorized final #1791 discriminator: it can continue execution
after **one** callback-bound OBJECT_SHOW for OBJID_WINDOW/CHILDID_SELF whose
HWND was invalid at entry, class query failed 1400, no owner resolved and no
visibility was observed. End-bracket validity and ownership must also remain
unresolved, so a newly live/reused handle cannot qualify.
This preserves `Failed=true` and the raw unknown
receipt. `ExecutionBlocked` remains false only for that narrow attribution
limitation; a second unknown, known owner/class, live/visible window, dialog,
probe failure, cadence failure or modal still stops execution.

Supervisors opting in must record execution contracts separately from
attribution status and the explicit user policy. They must not call the unknown
foreign, infer an OS class, claim no flash occurred or claim full observation
acceptance. Owned Console/Ghost windows receive no class-based exemption.
An ETW correlation that identifies our own window requires a root fix, not this
limitation. The limitation does not change production behavior, rights or timeouts.

## Full-wrapper dry-run before a single recording

`StartupProof.ps1` owns the common execution and receipt state machine.
`StartupProof.Native.ps1` binds it to the real medium caller, observer, job
supervisor and existing full functional stage scripts. There is no test-mode
stage path. Only `StartupTraceAdapter.Mode` changes between `Disabled` and
`Record`. `Invoke-StartupProofGuest.ps1` controls the elevated guest recorder
and a separate LIMITED interactive scheduled task running the original actor.
These entrypoints require an explicitly authorized disposable guest; never run
them on the controller or a developer's normal desktop.

Before recording:

1. Run `Get-StartupGuestQualification.ps1 -GuestAuthorized` with the exact new
   `-LeaseId` and a fresh `-OutputDirectory`. It collects installed tool help and
   provider schemas without starting a session. Metadata remains private.
2. Stage official hash-verified tools in separate prerequisite phases. Build
   the actual current-head harness and native fixtures. Preserve the published
   SDK 1.0.0 pins. Reuse the reviewed job/stage helpers as hash-pinned inputs.
3. Build the observer with `Build-ObserverEvidence.ps1 -OutputDirectory
   '<capsule>\observer-final'`. The exact
   `observer-final\QuietLaneCaptureObserver.dll` path is mandatory. Keep the
   capsule immutable and put all run outputs outside it.
4. Generate the installed-schema-qualified profile with the reviewed profile
   generator. Use `New-StartupProofConfig.ps1` with the actual manifest, actor
   SID/session, original qualified caller PATH, profile/receipt, WPR path,
   harness and fresh proof root. It checks installed global named-instance
   grammar using `wpr -help advanced`, verb help, nonrecording `-profiledetails`
   and exact instance absence.
   It pins the full capsule, wrappers, actual build outputs and input manifest.
   Recorder operations build complete named-instance process argument vectors.
   Status retains the qualified text and exit-code checks while normalizing
   native CRLF to LF.
   The recorder and watchdog finish their bound preparation before the measured
   observer interval begins. A second, bound observer qualification is required
   before either adapter admits scenarios or the recording adapter starts WPR.
   Receipt creation is not completion. Empty, partial, null, array and scalar
   JSON remain unqualified until a complete object is readable within the same
   existing receipt deadline.
5. Run the complete wrapper with tracing disabled:

   ```powershell
   pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Invoke-StartupProofGuest.ps1 `
     -Config '<guest configuration>' -LeaseId '<authorized cbx ID>' `
     -RunRoot '<new dry-run directory>' -Mode Disabled
   ```

   Success requires actual managed, SDK **four of four**, ownership/HANDLE/tree
   and desktop-closure contracts, observer finalization, recorder/watchdog exit,
   medium-controller exit, exact task removal, named-session absence, final
   formatting and parsed receipts. It writes `DryRunReceipt.json` only after
   these pass. TraceStartsCounter remains **0**. Preserve every failed run;
   fix orchestration and repeat only the disabled run until it passes.
   A blocking observation discovered while stopping and draining the observer
   rejects completion even when all stages passed and resources were reaped.
   Attribution-only observer failures remain limitations, not blocking failures.
6. With the same immutable configuration, hashes, environment, actor and
   canonical argument layout, call the **same** entrypoint with `-Mode Record`
   and a new output `-RunRoot`. Both medium wrapper and elevated recorder
   independently validate the passed dry-run binding. A single-use reservation
   prevents a second recording. The trace-attempt ledger is written only at
   the actual WPR start boundary. No trace retry is authorized.
   Recording completion additionally requires WPR stop exit code **0** and a
   nonempty ETL with its SHA-256 receipt (`recordingOutputVerified=true`).
   Cancellation and named-session absence establish cleanup, not a saved trace.

The recording covers the full managed/SDK/ownership scenario within the
unchanged 900-second recorder bound, rather than stopping before later
callbacks. The observer and jobs finalize before signaling recorder stop.
The recorder always stops/cancels only its uniquely named instance in finally;
an independent identity-bound watchdog also enforces the bound. Raw ETL,
decoded XML, configuration, PATH and command arguments remain private.
Review actual lifecycle/rundown/class fields, event losses and callback/ETW
time overlap before any causal claim. Capture-state requests alone do not
prove that preexisting-thread lifetime or window class was recorded.

The explicitly user-authorized five-argument observer constructor can count
**N** eligible invalid-at-entry unresolved SHOWs without aborting independent
functional collection at the second event. Eligibility remains exactly the
same as the single-event policy. Previously identified owned HWND history,
live/reused/visible windows, known classes/owners, dialog events, native probe
failures and cadence failures still block. `Failed` and `noDialogVerified=false`
are preserved for any counted unknown. Functional success never establishes
foreign ownership or a no-flash claim. Reporting “invalid before delivery”
requires actual lifecycle evidence, not merely `IsWindow=false` at callback
entry. Any proposed N-event limitation wording is conditional on all clauses,
including actual SDK4 and ownership success, being supported.

Native-free regression:

```powershell
pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Test-StartupProof.ps1 `
  -OutputDirectory '.\.artifacts\startup-proof-fixtures'
```

These fixtures drive the real common state machine and final JSON formatter
with fake native boundaries. They also execute the actual configuration builder
against a nonrecording tool fixture, then carry that configuration through the
same engine, controller finalizer and parsed frozen receipt. Unreaped
controllers, malformed functional receipts and mismatched-guest entrypoints
remain failures. They cannot qualify a guest dry-run or trace.

Build the real managed harness before entering startup observation. A bound
scenario passes `-ExpectedHarnessSha256` to `Test-DesktopFidelity.ps1`, which
verifies and executes that prepared binary without invoking a compiler.
Missing or changed frozen binaries fail closed. Ordinary fidelity invocations
without a frozen digest retain their existing build step. Include the harness
executable, assemblies and runtime metadata in the frozen artifact set.
