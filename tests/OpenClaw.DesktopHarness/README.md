# Private-desktop MXC regression

This opt-in real-runtime lane tests the security fidelity of
[`Invoke-PrivateDesktop.ps1`](../../scripts/Invoke-PrivateDesktop.ps1).
It requires an interactive Windows user session, .NET 10, installed MSVC/Windows
SDK tools, and an admitted MXC BaseContainer executor. It never installs tools,
requests elevation, changes MXC policy, starts a Gateway, or provisions WSL.

The launcher creates a **new** owned desktop with the current Default desktop's
DACL and mandatory label. It refuses an existing name and never modifies Default
or switches the shared input desktop. Windows can add an auto-inherited SACL
control marker; DACL and label ACL contents must remain byte-identical.

Run from the repository root with an exact native executable and its independently
verified input digest. Use an empty output directory:

```powershell
.\tests\OpenClaw.DesktopHarness\Test-DesktopFidelity.ps1 `
  -NativeExecutable '<absolute path to the verified wxc-exec.exe>' `
  -ExpectedNativeSha256 '<verified 64-character SHA256>' `
  -OutputDirectory '<empty proof directory>'
```

The script builds the managed probe and discovers an existing MSVC toolchain to
build `token-probe.c`. Developer environment setup and native compilation stay
in the same process. Alternatively, pass `-NativeProbeExecutable` with a build
of that source. No package or workload installation is attempted.

Four actual child-process cases must pass, with no skips:

- The old NULL-security desktop denies the low AppContainer write/create-window
  access and `LoadLibraryEx(user32.dll)` fails with error 1114.
- The same old-desktop PowerShell request fails without emitting its marker.
- The corrected owned desktop loads USER32 with the same sandbox policy.
- The corrected desktop executes controlled PowerShell and returns the exact
  marker, contained exit 0 and no timeout.

The native probe has no static USER32 dependency, so it records the real child
token, session and Win32k flags before attempting the load. It performs read-only
AccessCheck against captured descriptors; it does not change ACLs or tokens.
All owned desktops must be absent after their exact child exits.

`desktop-fidelity-regression.json`, per-case actual config/results and security
records are retained under the supplied output directory. The
`-NullSecurityNegativeControl` launcher option exists only for this regression.
It must not be used for an ordinary private validation run.

This lane does not replace the strict WSL Gateway/MXC formal proof or the required
full build, Shared and private-desktop Tray closeout.
