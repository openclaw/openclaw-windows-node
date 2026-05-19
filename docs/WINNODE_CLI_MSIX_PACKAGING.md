# Node CLI packaging under MSIX

> **Status:** Decision committed (recommended path: package the CLI inside the
> tray MSIX). Implementation is staged as a follow-up to PR #(this MSIX-E2E plan).

## Why this matters

`OpenClaw.WinNode.Cli` (the "worker node" CLI) and `OpenClaw.Tray.WinUI` (the
WinUI tray) share state via the file system. The contract today is implicit:

| Artifact                         | Path                                                | Writer | Reader |
|----------------------------------|-----------------------------------------------------|--------|--------|
| MCP bearer token                 | `%APPDATA%\OpenClawTray\mcp-token.txt`              | Tray   | CLI    |
| Device identity (Ed25519 keypair)| `%APPDATA%\OpenClawTray\device-key-ed25519.json`    | Tray   | CLI    |
| Exec-approval policy             | `%LOCALAPPDATA%\OpenClawTray\exec-policy.json`      | Tray   | CLI    |
| Operator pairing tokens          | `%APPDATA%\OpenClawTray\settings.json`              | Tray   | CLI    |

Under MSIX with package identity this contract becomes load-bearing in a way
that is easy to break by accident:

- `Environment.SpecialFolder.ApplicationData` returns the **user-profile**
  `%APPDATA%` from both packaged and unpackaged processes, so today's CLI does
  in fact see the same files the tray writes. This is the *only* reason the
  contract works at all in MSIX mode.
- `ApplicationData.Current.LocalFolder` returns a **per-package** path
  (`%LOCALAPPDATA%\Packages\<PFN>\LocalState\`) that only packaged code can read.
  Any future migration of the tray to that API would silently strand the CLI.
- MSIX file-system *redirection* (the legacy bridge that intercepts writes to
  Program Files / HKLM / etc.) does **not** apply to `%APPDATA%`. So the shared
  path keeps working — but only because nobody touches it.

In short: the contract works today by coincidence. If anyone moves the tray to
`StorageFolder` APIs, the CLI stops finding the token without any compile-time
or runtime warning. We need to lock this down before we ship MSIX-only.

## Options considered

### Option A — Keep the CLI unpackaged, formalize the shared path

- CLI ships as a stand-alone signed `.exe`, downloaded separately or bundled in
  the same release ZIP alongside the MSIX.
- Both processes resolve their state directory via a new `OPENCLAW_SHARED_DIR`
  environment variable, defaulting to `%APPDATA%\OpenClawTray`. The MSIX
  install writes this variable as part of first-run setup.
- Pros: zero MSIX manifest change, no impact on packaging pipeline, CLI can be
  invoked with absolute paths from anywhere.
- Cons: still two binaries to sign and distribute; users have to discover the
  CLI separately; CLI cannot use any packaged-app API (notifications under our
  package identity, AppCapability checks, etc.).
- Hazard: anyone who looks at `Environment.SpecialFolder.ApplicationData` in
  the tray code and "tidies it up" to `ApplicationData.Current.LocalFolder`
  breaks the CLI in a way that is invisible until a user hits it. The env-var
  contract has to be documented in big letters and enforced by a test.

### Option B — Package the CLI inside the same MSIX (recommended)

- Tray `Package.appxmanifest` declares a **second** `<Application>` element for
  the CLI, with a `windows.appExecutionAlias` extension publishing the alias
  `openclaw-winnode.exe` on `PATH`.
- Both processes use `ApplicationData.Current.LocalFolder` to resolve shared
  state; the package container guarantees they see identical paths.
- The CLI runs with package identity, which gives it:
  - notifications under the tray's package name,
  - `AppCapability.CheckAccess(...)` for capabilities declared in the same
    manifest,
  - first-class participation in the OS-level uninstall (its state inside the
    package container goes away cleanly when the package is removed).
- Pros: one signed artifact; uniform identity story; impossible-to-drift shared
  state contract; no orphaned files on uninstall for state inside the container.
- Cons: requires a second `<Application>` element + `appExecutionAlias` plumbing;
  CLI cold-start path goes through the AppExecutionAlias resolver (≈30 ms one-time
  overhead, irrelevant for our usage).

### Option C — Drop the CLI entirely

- Subsume all CLI functionality into a tray command palette and deep links.
- Pros: simplest manifest; simplest packaging; no shared-state contract at all.
- Cons: breaks every existing script / integration that shells out to the CLI;
  removes the "agent on a server has no UI" use case; explicitly out of scope
  for this plan.

## Recommendation: Option B (packaged CLI with `appExecutionAlias`)

Option B is the only choice that eliminates the "two processes happen to agree
on a path" hazard. The OS gives us a single uninstall path and a single signing
artifact, the CLI gets a real identity, and the contract between tray and CLI
becomes "we are the same package", which is enforceable rather than implicit.

### Manifest snippet (proposed, not yet wired)

```xml
<Applications>
  <!-- Existing tray application stays as-is -->
  <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
    <!-- ... existing VisualElements, protocol, startupTask ... -->
  </Application>

  <!-- New: the worker-node CLI, surfaced on PATH via appExecutionAlias -->
  <Application Id="WinNodeCli" Executable="OpenClaw.WinNode.Cli.exe" EntryPoint="Windows.FullTrustApplication">
    <uap:VisualElements
      DisplayName="OpenClaw Node CLI"
      Description="OpenClaw worker node command-line interface"
      BackgroundColor="transparent"
      AppListEntry="none"
      Square150x150Logo="Assets\Square150x150Logo.png"
      Square44x44Logo="Assets\Square44x44Logo.png" />
    <Extensions>
      <uap3:Extension Category="windows.appExecutionAlias"
                      Executable="OpenClaw.WinNode.Cli.exe"
                      EntryPoint="Windows.FullTrustApplication">
        <uap3:AppExecutionAlias>
          <desktop:ExecutionAlias Alias="openclaw-winnode.exe" />
        </uap3:AppExecutionAlias>
      </uap3:Extension>
    </Extensions>
  </Application>
</Applications>
```

Required namespace additions on `<Package>`:

```
xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
```

### Required CLI code changes (sketch, not yet committed)

1. `OpenClaw.WinNode.Cli/Program.cs`: replace `Environment.SpecialFolder.ApplicationData`
   resolution with a `PathResolver` that branches on `PackageHelper.IsPackaged`
   (a copy of the tray's helper, or share via `OpenClaw.Shared`). Packaged →
   `ApplicationData.Current.LocalFolder`. Unpackaged → today's path.
2. `OpenClaw.WinNode.Cli.csproj`: add `<EnableMsixTooling>true</EnableMsixTooling>`
   so the CLI exe is properly bundled into the parent MSIX.
3. Tray `Package.appxmanifest`: insert the second `<Application>` from the
   snippet above.
4. CI build job: add the CLI bin output to the AppPackages payload (today the
   MSIX only includes the WinUI tray output).

### Risks to track in the implementation PR

- **Discovery**: the alias `openclaw-winnode.exe` only resolves once the user
  has run the tray once (so the package registers). Anything that shells out
  during install can't use it yet — use the full container path for installer
  steps.
- **Console attach**: full-trust packaged consoles must `AllocConsole` /
  `AttachConsole` to inherit the calling cmd's stdio; otherwise the CLI looks
  like it does nothing when invoked from a terminal. The tray already has the
  pattern in `App.xaml.cs`'s `RunCliUninstallAsync`; lift it into a helper.
- **AppContainer**: `runFullTrust` is still required for WSL / wsl.exe spawning
  and for arbitrary file-system access. Do not remove it from the manifest.

### Acceptance criteria for the follow-up implementation PR

1. From a fresh PowerShell on a packaged install: `openclaw-winnode --version`
   prints the same version the tray reports.
2. `Get-AppxPackage OpenClaw.Companion | Select Applications | fl *` shows both
   applications.
3. CLI calls `--purge-wsl-orphans` and reports the same paths the tray would,
   verified against a tray `--uninstall` golden output.
4. `Remove-AppxPackage` removes the CLI exe along with the tray (no orphan
   `openclaw-winnode.exe` anywhere on disk).
