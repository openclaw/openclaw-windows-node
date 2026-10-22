# Private-desktop MXC regression

## Fresh-guest observer diagnostics

`Observation` owns the passive quiet-lane proof observer, promoted from the
incident capture artifact. It is not started by the harness executable.
Its hook process/thread filters remain 0/0, with OUTOFCONTEXT flags and
unchanged dialog/SHOW coverage. Incomplete classification still fails.

Run the native-free diagnostic regressions on the controller:

```powershell
pwsh -NoProfile -File .\tests\OpenClaw.DesktopHarness\Test-ObserverEvidence.ps1
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
request builder selects a container-owned desktop, UI API compatibility, no
clipboard, no input injection and denied ingress/egress. Both caller desktop
variants must initialize USER32 and return contained exit 0 without a timeout.
The positive PowerShell cases must emit the exact controlled marker.

The original MXC 0.8 bridge proof is **HISTORICAL**. Its NULL-security caller
desktop failure predicate is not a current SDK contract: the SDK now owns the
execution desktop. No legacy bridge or native executable override is retained.
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
