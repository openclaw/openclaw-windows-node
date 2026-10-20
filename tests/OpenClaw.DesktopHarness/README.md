# Private-desktop MXC regression

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
event-controlled waiting child. The launcher terminates and reaps only the child
identified by its native process handle. A separate successful nonblocking launch
must retain its waiting child until the test releases it. Neither path switches
the input desktop.

`desktop-fidelity-regression.json`, per-case actual config/results and security
records are retained under the supplied output directory. The
`-NullSecurityNegativeControl` launcher option exists only for this regression.
It must not be used for an ordinary private validation run.

This lane does not replace the strict WSL Gateway/MXC formal proof or the required
full build, Shared and private-desktop Tray closeout.
