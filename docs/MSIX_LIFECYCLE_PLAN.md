# One supported install, update, repair, and removal path for OpenClaw on Windows

The way OpenClaw is installed, updated, repaired, and removed on Windows should be changed because those responsibilities are currently split across mechanisms that do not know about each other.  This document presents the recommended design, the evidence behind it, the remaining validation gates, and the decisions that need team agreement.

## Executive summary

OpenClaw on Windows is currently installed by an executable built with Inno Setup, updated by an OpenClaw-specific ZIP updater, and paired with a gateway that the existing SetupEngine component installs separately in Windows Subsystem for Linux (WSL).  That arrangement works for prerelease development, but it divides responsibility for one product among unrelated installation, update, repair, and removal mechanisms.

The recommended design is:

1. Package the **Windows companion** as MSIX.  The companion package contains the WinUI tray application, the Windows node hosted in that process, setup and connection libraries, and all required Windows runtime files for one architecture.
2. Keep the **gateway separate from the companion package**, both now and if a Windows-hosted gateway becomes available.  The current gateway remains a separately installed WSL component.  A Windows-hosted gateway appears likely to arrive through separate OpenClaw and Windows platform work, but it has no published schedule or contract at the time of writing.  This proposal allows the companion to ship without it and adopt it later; construction of that gateway is not proposed here.
3. Install each supported companion channel through App Installer.  A `.appinstaller` document associates the installed package with that channel's HTTPS update source so Windows can check, stage, verify, and apply later companion versions.
4. Use the same small signed **per-user Inno coordinator** for WSL-backed Stable, Preview, and installed Dev channels.  This creates an additional Windows Installed Apps maintenance entry alongside each channel's companion MSIX; it is not another package containing the companion.  It installs a signed `OpenClawMaintenance.exe` program that inventories, migrates, repairs, and removes OpenClaw components even after the companion has been removed.  The coordinator also installs the companion through its approved `.appinstaller` source so that the update association is recorded.
5. Keep SetupEngine responsible for the current WSL gateway.  Its acquisition path should be strengthened so reviewed gateway releases are described by signed, immutable metadata and exact payload hashes rather than relying on a mutable HTTPS installation script.
6. Support Stable, Preview, and installed Dev side by side using separate package identities, URI protocol handlers, state roots, startup registrations, maintenance registrations, `.appinstaller` feeds, local Model Context Protocol (MCP) endpoints and tokens, and app execution aliases when approved.  Versions within one channel update in place.  Installing one channel must not force removal of another.
7. Treat the `windows.customInstall` MSIX extension, which declares install, repair, and uninstall actions, as a promising alternative to the coordinator.  It should not be used until the [five clearance requirements](#windowscustominstall) covering platform guidance, production lifecycle proof, managed deployment, Store review when applicable, and OpenClaw approval are met.  The disposable prototype established only action ordering and interactive-user file-system scope.
8. Do not use a machine-context Windows Installer (MSI) package as the baseline for cleaning up the current per-user WSL installation.  WSL registration and credentials protected for the signed-in user require cleanup in that user's loaded profile.  The prototype showed that a maintenance tool run as `SYSTEM` could return success while leaving the user's installation and state untouched.  No advantage over the tested per-user Inno design has been demonstrated.
9. Keep the current full Inno companion installer and ZIP updater until the MSIX path has passed installation, update, migration, repair, recovery, and complete-removal gates.  The full Inno companion payload should be retired only after every responsibility has a validated replacement.  The small Inno coordinator in item 4 remains part of the WSL-period design.
10. Advance the design through explicit validation gates.  The package boundary, custom-install action mechanics, and coordinator removal ordering have been established by prototypes.  Production-trusted signing, App Installer update association and package replacement, package repair, managed deployment, ARM64 runtime behavior, and migration of real OpenClaw state remain required before the design is supported.

MSIX is recommended for the companion for the reasons listed under [Why MSIX is recommended for the companion](#why-msix-is-recommended-for-the-companion).  Those benefits apply only to the companion MSIX.  Comparable gateway assurances should be provided separately through SetupEngine's signed release metadata, exact payload verification, inventory, transactional update, and recovery contract.  Neither mechanism reduces the permissions of the full-trust tray and in-process Windows node.

## Scope

This document recommends installation technology and the product lifecycle it must support.  It includes the package boundary, update authorities, state ownership, migration, repair, removal, signing, and validation work required to make the design supportable.

It does not include:

- Performing an enterprise rollout.  The same signed package and maintenance commands must be deployable by an enterprise software-distribution system, but selecting a fleet, scheduling a rollout, and operating that rollout are separate activities.
- Building the anticipated Windows-hosted gateway or the platform service expected to install it under a restricted Windows identity.  Those are expected external dependencies.  OpenClaw integration work can begin after their supported installation, identity, policy, update, repair, and removal contracts are available.
- Putting either the current WSL gateway or the future Windows gateway inside the companion MSIX.  That is not the proposed end state.

Current behavior is documented in [Setup](SETUP.md), the [SetupEngine redesign](SETUP_ENGINE_REDESIGN.md), the [connection architecture](CONNECTION_ARCHITECTURE.md), [release process](RELEASING.md), [versioning contract](VERSIONING.md), [Windows node testing](WINDOWS_NODE_TESTING.md), and [current MSIX uninstall guidance](uninstall-msix.md).  Those documents remain authoritative for the existing product until this proposal is approved and implemented.  The repository currently calls its signed prerelease channel Alpha; this proposal uses Preview for the future installed prerelease channel.  [Step 0](#step-0-approve-outcomes-product-contracts-and-owners) must approve that name and update the versioning contract before implementation.

## Terminology

- **AppContainer:** the Windows isolation boundary used by OpenClaw's command-execution capability, `system.run`.  Access is denied unless the generated policy grants the requested files, registry locations, network destinations, or other resources.
- **App execution alias:** an MSIX registration that makes a packaged executable available by a command such as `winnode` from a terminal.  Publishing one turns that command name and behavior into a supported package contract.
- **App Installer:** the Windows application that installs MSIX packages and can associate them with update and repair sources.  It is built into Windows 10 version 1803 and later and Windows 11.
- **App Installer document (`.appinstaller`):** an unsigned XML document that names an approved MSIX source and configures App Installer's update and repair behavior.  The referenced MSIX remains the signed application payload.
- **App Installer feed:** the channel-specific HTTPS location and `.appinstaller` document through which App Installer obtains approved companion packages and update policy.
- **CurrentUser:** Windows credential protection and state scoped to the signed-in user's profile.  A different account cannot use it merely by locating the files.
- **Dev, Preview, and Stable:** installed engineering builds, signed prerelease builds used by testers, and the supported general release.  A source-built or portable engineering run is not an installed Dev channel unless its package and coordinator are installed.
- **Inno Setup:** the third-party system that builds the current OpenClaw installer executable from an OpenClaw-owned script.
- **Maintenance coordinator:** the small installed Inno product registration for one channel that keeps `OpenClawMaintenance.exe` available independently of the companion MSIX.
- **MakeAppx:** the Windows SDK tool that creates and validates MSIX packages from a manifest and payload directory.
- **MCP:** Model Context Protocol, the protocol through which OpenClaw exposes tools for discovery and invocation.
- **MSI:** the Windows Installer package format.  A machine-context MSI is registered and serviced for the device rather than only for the signed-in user.
- **MSIX:** the Windows package format proposed for the companion.  Its package identity records the publisher, product, version, architecture, capabilities, and registrations used by Windows.
- **MSIX bundle (`.msixbundle`):** one signed container that can hold the x64 and ARM64 variants of the same companion release while preserving separate architecture-specific packages.
- **MXC:** the Windows command-isolation platform OpenClaw already uses, through an SDK helper, to contain individual `system.run` commands.  The SDK ships that helper as `wxc-exec.exe`, so both names appear in the repository.  Hosting a long-running gateway through MXC would be separate future work.
- **`OpenClawMaintenance.exe`:** a signed OpenClaw program that inventories, migrates, repairs, or removes product components for its installed channel.
- **Remote gateway:** a gateway on another machine.  Its lifecycle is owned by whoever operates that machine.
- **RFC 3161 timestamp:** a signed statement from a trusted timestamp authority proving when a package signature was created.  It allows the signature to remain verifiable after the code-signing certificate later expires, provided that the certificate was valid when the timestamp was issued.
- **SetupEngine:** the existing OpenClaw component that installs, inventories, repairs, updates, and removes the current local WSL gateway.
- **`system.run`:** the OpenClaw capability that runs a command on the Windows node on a connected gateway's behalf.
- **URI protocol handler:** a Windows registration such as `openclaw-preview:` that launches a specific installed channel from a URI.
- **Windows companion:** the WinUI tray application, settings and onboarding UI, the Windows node currently hosted in the tray process, setup and connection libraries, and their Windows runtime dependencies.
- **Windows-hosted gateway:** an anticipated future gateway running natively on Windows and delivered by separate platform work.  It is not built by this proposal.
- **Windows isolation broker:** the anticipated platform service that would install a Windows-hosted gateway, bind it to a restricted identity and MXC policy, and own that component's lifecycle.
- **Windows node:** the companion code that exposes approved Windows capabilities to a connected gateway.
- **WSL gateway:** the gateway OpenClaw installs today inside an app-owned WSL distribution.  SetupEngine installs, updates, repairs, and removes it.
- **WSL period:** the interval during which the app-owned WSL distribution is the only supported local gateway, ending only if a supported Windows-hosted gateway lifecycle becomes available.

## Required outcomes

The design must provide all eight outcomes below:

1. **Publisher and payload trust.**  The publisher of each installed component can be identified, and the exact reviewed payload can be verified.
2. **Deterministic installation.**  A given companion version and architecture always contain the same files, dependencies, registrations, and capabilities.
3. **One update authority per component.**  The companion, current WSL gateway, maintenance coordinator, and future Windows gateway each have one named update owner.
4. **Recoverable updates.**  A failed update to version N+1 leaves version N usable, and a bad release can be replaced by a higher-version package containing known-good behavior through a reviewed release action whose package, source commit, hashes, publisher, and approval are recorded.
5. **Enterprise deployability.**  An administrator can assign, inventory, pin, update, and remove OpenClaw through a selected software-distribution system without repackaging it or writing an organization-specific cleanup script.
6. **Understandable repair and removal.**  Companion repair, companion reset, gateway repair, product repair, companion-only removal, and complete product removal are separate operations with named entry points and tested outcomes.
7. **Safe migration and channel coexistence.**  Existing prerelease users can preserve supported state, and Stable, Preview, and installed Dev can be installed side by side without identifier or update collisions.
8. **A durable gateway boundary.**  The companion package does not make WSL permanent, does not absorb the future Windows gateway, and does not imply that package identity provides runtime containment.

## Why MSIX is recommended for the companion

The current companion is installed as ordinary files and later replaces itself from a ZIP release.  Windows can inventory the Inno product record, but it does not own one signed application identity that ties the publisher, version, capabilities, files, activation registrations, and update source together.

MSIX improves that lifecycle for the companion:

- The package publisher and package-family identity are recorded by Windows.
- The package block map records package-file hashes.
- Package files are installed under a protected, versioned location.
- A replacement package is staged and committed as one unit rather than copied over a running installation.
- Package version and architecture are available to Windows inventory and deployment tools.
- Package-owned files and registrations are removed by Windows.
- Optional package-integrity enforcement can block launch when installed files no longer match the signed package, and can point Windows at a repair source.

The companion remains a full-trust desktop application.  The tray and Windows node still run as the signed-in user, subject to ordinary Windows access checks and the separate command-specific MXC policy.  MSIX provides identity and servicing; it is not a sandbox.

### Package boundary

The **OpenClaw Windows Companion MSIX** contains:

- The WinUI tray application.
- The Windows node code hosted in the tray process.
- SetupEngine, connection, chat, setup UI, and other runtime libraries used by that process.
- The matching `wxc-exec.exe` supplied by the MXC SDK, with its Microsoft signature, expected version, and hash preserved.
- Managed and native Windows dependencies, assets, configuration, and manifest registrations needed at runtime.
- Only the target architecture's Windows native files.

It does not contain:

- The current WSL distribution, Linux root filesystem, gateway service state, configuration, credentials, or virtual disk.
- The future Windows gateway.
- Linux or macOS runtime files.
- Native files for the other Windows architecture.
- SDKs, tests, build tools, developer certificates, or mutable feed documents.

If an `.msixbundle` is introduced later, it should contain the x64 and ARM64 variants of the companion only.

## Alternatives considered

Package format, distribution channel, deployment scope, and the component lifecycle are separate decisions.

| Option | Where it fits | Recommendation |
|---|---|---|
| Current full Inno installer | It can copy the companion, provision prerequisites, run SetupEngine, and perform custom cleanup. | Keep it as the supported fallback until the replacement passes all lifecycle gates.  Do not use it as the long-term companion update mechanism. |
| Small per-user Inno coordinator plus companion MSIX | It keeps a maintenance executable available after package removal and naturally runs against the current user's WSL and CurrentUser state. | Recommended for each installed WSL-backed Stable, Preview, and Dev channel.  It is a temporary coordinator, not a second copy of the companion. |
| Machine-context MSI coordinator | It offers standardized Windows Installer product, repair, logging, and enterprise policy concepts. | Rejected as the baseline for the current per-user WSL lifecycle.  WSL registration and CurrentUser credentials require target-user execution.  A machine-context design would need a supported way to identify and act as that user, and the prototype demonstrated the risk of a success result scoped only to `SYSTEM`. |
| Per-user MSI coordinator | It could run in the correct user context while retaining Windows Installer semantics. | Not selected.  It adds MSI component, upgrade, and custom-action complexity without a demonstrated lifecycle advantage over the existing Inno expertise. |
| `windows.customInstall` | It can declare package install, repair, and uninstall actions and request user-context execution. | Technically promising but not a baseline.  The [five clearance requirements](#windowscustominstall) apply: platform eligibility and semantics; production-signed App Installer lifecycle proof, including repair, removal, failure recovery, and target-user execution; managed-deployment proof; Store approval when applicable; and OpenClaw release and security approval. |
| Direct MSIX | It provides an immutable signed companion artifact and works for offline or controlled installation. | Supported as an artifact, but not the primary self-service path because a direct MSIX does not establish an update association. |
| Portable ZIP | It is useful for development, diagnostics, and recovery. | Retain as a support artifact.  It is not an installed product lifecycle. |
| Put the gateway in the companion MSIX | It appears to simplify first acquisition. | Rejected.  The gateway is optional, writable, long-running, independently versioned, and owned by a different update and repair lifecycle. |
| Wait for the future Windows gateway before packaging the companion | It avoids a temporary WSL coordinator. | Rejected.  The companion boundary is valid now, and the external gateway schedule should not block deterministic packaging and Windows-managed companion servicing. |

If the future gateway requires a restricted Windows identity, that identity should be created and removed through the supported Windows broker contract expected from the platform work, not through an OpenClaw-specific account-management design.

## Recommended end-to-end experience

OpenClaw remains one product from the user's point of view, but its companion and gateway are separate installed components.  A common Settings and maintenance experience coordinates them, even though they are separate packages or installations.

### From zero to a working installed channel

Stable, Preview, and installed Dev use the same lifecycle pattern.  Their identities, feeds, state, maintenance registrations, and display names are separate, but the installation steps are the same:

1. The user runs a signed per-user `OpenClaw <Channel> Setup` executable built with Inno, where `<Channel>` is Stable, Preview, or Dev.
2. A channel-specific maintenance coordinator is installed.  `OpenClawMaintenance.exe` is placed under a stable per-user program location, and an **OpenClaw <Channel>** product entry is registered in Windows Installed Apps.
3. The coordinator opens the channel's approved `.appinstaller` document.  App Installer verifies the publisher and package, installs the companion MSIX, and records the update association.
4. Windows records the companion MSIX independently.  The intended Installed Apps model for each channel is:
   - **OpenClaw <Channel>:** product-level repair and complete removal through the coordinator.
   - **OpenClaw <Channel> Companion:** Windows-managed companion repair, reset, and companion-only removal.
5. The companion is launched, and onboarding asks whether a remote gateway will be used or a local gateway will be installed.
6. A remote-gateway choice creates only local connection, pairing, and credential records.  Installation, update, repair, and removal of the remote gateway remain the remote operator's responsibility.
7. A local-gateway choice invokes SetupEngine.  The app-owned WSL distribution is created, an exact reviewed gateway payload is verified and installed, credentials and service state are configured, and gateway health and pairing are checked.  The current [SetupEngine installation path](../src/OpenClaw.SetupEngine/SetupSteps.cs#L1228-L1304) already pins a requested gateway version, but [Step 3](#step-3-build-gateway-trust-and-per-channel-maintenance-coordination) adds immutable signed release metadata and exact payload verification.
8. Diagnostics report the companion package identity and version, update authority, coordinator version, gateway location and version, compatibility result, and WSL distribution name when applicable.

The additional Inno registration is required while the local gateway is WSL-backed.  Inno no longer owns or updates companion files.  Its smaller responsibility is to keep cross-component maintenance and complete removal available outside the package that may be removed first.  SetupEngine remains responsible for WSL prerequisite enablement, including any elevation and restart, and onboarding invokes that work when a local gateway is selected.

App Installer is built into Windows 10 version 1803 and later and Windows 11, so it should normally already be present on supported systems.  It is serviced through Microsoft Store updates when those updates are enabled.  A current installer is also published by Microsoft for systems without Store access, and `winget upgrade Microsoft.AppInstaller` can update an existing installation.  Updating App Installer does not add package-schema features that the installed Windows version does not support, so the selected Windows floor still limits repair metadata.  Prerequisite detection is required because App Installer may be absent from a stripped image, damaged, outdated, or blocked by policy.  In that case, setup should explain the condition and use an approved managed or offline prerequisite path rather than silently falling back to direct MSIX installation without an update association.

Before a channel is tester-facing, gateway records and non-secret cross-component state should be moved to an approved cross-component location.  Device identity, credentials, and that channel's MCP token should be moved to approved protected-credential locations so companion removal or reset does not delete them.  Signed packages produced before that relocation are internal prototypes rather than supported channel releases.

The prototype established that independent Inno and package registrations can survive in either removal order.  The intended Installed Apps labels, Advanced options, Repair, Reset, and Modify behavior have not been established.  A normal App Installer deployment reached package trust validation but was blocked by the disposable certificate's untrusted root.  Loose developer registration was not accepted as equivalent proof, and Windows Settings automation remained policy-blocked.  Production-trusted lifecycle testing should therefore confirm that both entries and their actions are understandable before any channel is approved.

### From an installed product to nothing remaining

Complete removal for a channel is started from:

- **OpenClaw Settings > Maintenance > Remove this OpenClaw channel from this PC**, while that channel's companion is present; or
- the channel's **OpenClaw <Channel>** product entry in Windows Installed Apps, even if the companion package has already been removed.

The independently installed maintenance executable performs the same product-removal contract for Stable, Preview, and installed Dev:

1. Inventories:
   - the companion package and App Installer association;
   - local and remote gateway records, the WSL distribution, and any other installed channel that references that local gateway;
   - credentials and device identity;
   - startup registrations; and
   - logs and other owned state.
2. Presents the retention choices for preferences, logs, credentials, device identity, and the local gateway, then applies the user's selection.  Local-gateway removal is unavailable while another installed channel references that gateway, unless those channels are explicitly disconnected as part of the operation.  The unattended default remains a team decision.  Remote gateway software is never removed from another machine.
3. Stops that channel's companion.  The local gateway is stopped only if it will be removed.
4. Resolves local-gateway ownership before removal.  If another installed channel references the gateway, it is retained.  If the selected channel owns that gateway's lifecycle, ownership is explicitly transferred to a compatible remaining coordinator before the selected channel is removed.  If no remaining coordinator can own it, the operation must either be cancelled or explicitly disconnect the other channels and remove the gateway; an ownerless gateway is not left behind.
5. Removes the app-owned WSL gateway and virtual disk only when no other channel references them and the user did not choose to retain them.
6. Removes startup entries and URI protocol handlers not owned by another channel, then removes the gateway records, credentials, device identity, logs, migration journals, and preferences that belong only to the selected channel and were not retained.
7. Removes the companion MSIX if it is still installed.
8. Verifies that no process, package, or registration belonging to the selected channel remains, that other installed channels are untouched, and that the only selected-channel state left is what the user chose to retain.
9. Removes that channel's maintenance coordinator last.

An interrupted operation is resumed from a non-secret journal.  The coordinator is not removed while it is still the only recovery path.  Production coordinator removal cannot rely only on `[UninstallRun]`, the Inno directive that runs a command during uninstall, because maintenance failure must stop deletion.  Custom uninstall code must invoke maintenance before destructive steps, require it to reach a recoverable state, and leave the coordinator and Installed Apps entry in place when it does not.

### Companion-only removal

Removing **OpenClaw <Channel> Companion** through Windows removes that MSIX and its package-private state.  After the shared-state relocation, the separately installed gateway, shared gateway records, credentials, and channel coordinator are intentionally preserved for reinstall, adoption, repair, or later complete removal.  The contract is not valid for internal prototype packages built before that relocation, which is why those builds are not published as supported channel releases.

Reinstall must detect retained state and offer **Adopt**, **Repair**, or **Remove**.  It must not silently delete an app-owned WSL distribution as stale state.

### Future Windows gateway

The expected Windows-hosted gateway does not change the companion package boundary.  It is still an optional, separately versioned component.

For OpenClaw to integrate with a Windows-hosted gateway, the external platform contract would need to provide a Windows isolation broker that can:

1. Verify and install the reviewed gateway component.
2. Create or resolve the restricted gateway identity.
3. Bind the gateway version, identity, and MXC policy.
4. Start and health-check the gateway.
5. Repair missing registration, identity, policy, or startup state.
6. Update or remove the gateway independently from the companion.
7. Revoke the restricted identity and policy during complete removal.

OpenClaw integration work will call that supported contract and migrate approved WSL state after it exists.  This proposal does not create the broker, account model, or Windows gateway package.  The per-user Inno coordinators can be retired only after the external component lifecycle provides equivalent repair, recovery, and complete removal.

## Repair entry points

Only the OpenClaw Settings and Installed Apps entries registered by the coordinator are under OpenClaw's control.  Whether Windows exposes Repair and Reset for the production package type remains a release gate.

| Operation | User entry point | Managed or recovery entry point | Result | Status |
|---|---|---|---|---|
| Companion package repair | **Windows Settings > Apps > Installed apps > OpenClaw <Channel> Companion > Advanced options > Repair**, where Windows exposes it for the package | App Installer repair metadata or managed redeployment of the same signed package | Missing or damaged companion files and registrations are restored.  Gateway and shared state are not treated as package files.  On Windows versions below Windows 11, version 21H2, build 22000, the 2021 App Installer repair-URI schema is unavailable and managed redeployment or another approved recovery path is required. | Unproved |
| Companion reset | **Windows Settings > Apps > Installed apps > OpenClaw <Channel> Companion > Advanced options > Reset** | The equivalent package reset operation when supported by the selected deployment path | Companion-private preferences and caches are cleared and first-run UI state is restored.  After shared-state relocation, gateway records, credentials, and local gateway state are preserved. | Unproved |
| Gateway repair | **OpenClaw Settings > Gateway > Repair local gateway** in the lifecycle-owner channel.  Other channels identify the owner and direct the user there. | The lifecycle owner's `OpenClawMaintenance.exe gateway repair` in the target user's context | WSL registration, gateway files, configuration, service, credentials, connectivity, and compatibility are inventoried.  Only the failed layer is replaced.  A file-layer repair uses the same signed gateway metadata and payload-verification contract as SetupEngine rather than a separate coordinator source. | Existing SetupEngine foundation; final contract unproved |
| Product repair | **Windows Installed Apps > OpenClaw <Channel> > Modify or Repair** | `OpenClawMaintenance.exe product repair` in the target user's context | Companion repair is available for every channel.  Gateway repair is invoked only when this channel owns that gateway's lifecycle; another channel reports the owner instead.  Version compatibility and pairing are then verified. | Proposed for all installed channels |
| Companion-only removal | **Windows Settings > Apps > Installed apps > OpenClaw <Channel> Companion > Uninstall** | Managed package removal | The companion package and package-private state are removed.  After shared-state relocation, the gateway, gateway records, credentials, and maintenance coordinator are preserved. | Package removal proved with a synthetic package, not the real companion; final state boundary unproved |
| Complete removal | **OpenClaw Settings > Maintenance > Remove this OpenClaw channel from this PC**, or **Windows Installed Apps > OpenClaw <Channel> > Uninstall** | `OpenClawMaintenance.exe product remove --confirm` in the target user's context | The selected channel's components and state are removed.  A local gateway referenced by another channel is retained or explicitly transferred rather than removed.  Recovery remains available after companion-first removal because the coordinator is independent. | Removal ordering proved with synthetic state, not real OpenClaw state; real flow unproved |

The exact Windows Settings labels should be verified against the production package and supported Windows releases.  These state boundaries depend on the ownership split in [State ownership and current paths](#state-ownership-and-current-paths), which is not yet settled.

## How companion updates work

An `.appinstaller` file is an unsigned XML feed document, not the application package itself.  It names the companion package, its URI, publisher, architecture or bundle, package version, update locations, and update policy.  In the 2021 schema, `s4:RepairUris` can name App Installer documents used for repair.  The feed also has its own four-part document version, separate from the package version.

`s4:UpdateUris` and `s4:RepairUris` require the 2021 App Installer namespace, introduced in Windows 11, version 21H2, build 22000.  If the selected Windows floor is lower, repair locations are unavailable on the low end and the repair matrix must record the resulting behavior there.

The feed's integrity depends on its transport and on write control of the hosting origin.  The enforceable payload boundary is the signed package: App Installer validates the package signature, identity, and publisher.  Feed-origin access control, immutable release publication, and review of feed URI changes are therefore release requirements.  The feed document and package version must both advance as intended for each release; the exact refresh behavior must be verified rather than inferred.

When the companion is first installed through that document:

1. Windows records the package identity and the `.appinstaller` association.
2. App Installer checks the configured source on launch, in the background, or under the configured prompt and deferral policy.
3. A candidate must have the expected identity and publisher and normally a higher package version.
4. The package is downloaded and its signature and block map are verified before replacement.
5. Windows stages the new version and switches the package registration as one deployment operation.
6. If acquisition or verification fails before the replacement is committed, the installed version remains available.
7. If application shutdown or restart is required, behavior follows the declared update policy and must be tested rather than inferred.

App Installer supports on-launch checks, background checks every eight hours, prompted or silent updates, and an optional activation-blocking prompt.  The intended user experience is a background check with no blocking prompt and no forced restart, so a running session is not interrupted.  The exact prompt, deferral, and background settings should be selected and tested on every supported Windows version.

A direct MSIX installation does not create this update association.  Each channel coordinator must install through the approved `.appinstaller` source if App Installer is expected to own later companion updates.  Direct MSIX installation is therefore limited to offline, diagnostic, or controlled deployment in which the person or system that installed the artifact remains the named servicing authority.  It is not a supported interactive self-service path.  Recovery testing should determine whether App Installer association can be added in place or requires removal and reinstallation through the feed.

The application must disable construction and scheduling of the existing ZIP updater automatically whenever package identity is present.  No user setting or deployment instruction should be required.  The updater is currently constructed by a [static field initializer](../src/OpenClaw.Tray.WinUI/App.xaml.cs#L46-L50) before it is passed to the [update coordinator](../src/OpenClaw.Tray.WinUI/App.xaml.cs#L621-L636), so implementation must move construction behind the package-identity decision rather than merely skipping coordinator setup.  Diagnostics must report one of three explicit companion update states: App Installer association, a named managed or manual direct-package servicing owner, or no supported update authority with instructions to reinstall through the approved `.appinstaller` source.  Tests must prove that no ZIP check, download, or replacement can start in packaged execution.  Otherwise two mechanisms would compete to replace the same companion, and the ZIP updater would attempt to write into the protected, versioned package directory.

If a release is bad, the known-good payload is published under a new, higher package version.  The release record identifies the new package version and the earlier source and payload being restored.  `ForceUpdateFromAnyVersion` should not be enabled because unrestricted downgrade authority makes version ordering and vulnerability response harder to audit.

The maintenance coordinator is versioned separately.  Its compatibility contract is:

- The coordinator carries the maintenance-contract and SetupEngine cleanup code it needs rather than loading files from the companion package, which may already be gone.
- The coordinator and companion report a shared maintenance-schema version and supported compatibility range.
- Every companion release declares its minimum coordinator version.
- A companion release remains compatible with every coordinator version still in support, or the coordinator update is deployed and verified before that companion feed is promoted.
- If the installed coordinator is too old, state-changing gateway and product-maintenance operations are blocked with a clear signed-coordinator recovery path.  The companion does not write a newer state schema first.
- A signed coordinator update manifest and transactional self-replacement path are required before any installed channel is supported.

Managed deployment can update the coordinator and companion together as one reviewed assignment, but their installed versions remain independently visible.

## Companion trust and gateway trust

### Companion

The companion receives Windows-managed package identity, signature verification, protected versioned files, and atomic replacement:

- The package publisher must exactly match the signing certificate and intended package identity.
- The final x64 and ARM64 artifacts must be immutable, signed, and timestamped through an approved RFC 3161 service.
- CI must unpack each package and verify its manifest, files, signatures, hashes, architecture, software bill of materials, and provenance against a reviewed allowlist.
- The production manifest should declare `uap10:PackageIntegrity` with content enforcement for non-Store packages.
- The `.appinstaller` document should provide update locations and, using the selected schema version's `s4:RepairUris` element, repair locations.
- The `.appinstaller` origin must have named ownership, restricted write access, immutable release publication, and reviewed URI changes.
- Managed deployment should use detection and redeployment rather than modifying package files.

### Current gateway

SetupEngine already provides useful lifecycle behavior:

- It chooses a pinned gateway version.
- It creates the app-owned WSL distribution.
- It configures the service and credentials.
- It starts and health-checks the gateway.
- It can rerun setup and remove the local gateway.

The current gateway path does not yet identify and verify one immutable reviewed payload.  The code pins a requested version but downloads and executes an HTTPS installation script.  HTTPS protects the transport connection; it does not make a mutable URL an immutable reviewed release.

Before a supported WSL-backed MSIX release:

1. The OpenClaw Windows release owner reviews the upstream gateway release.
2. Signed immutable metadata maps the gateway version to exact payload URIs and SHA-256 hashes.
3. The verification key is pinned in the signed companion and maintenance payload.
4. SetupEngine verifies the metadata signature and every payload hash before execution.
5. The installed version, metadata signature, payload hashes, WSL distribution, service state, and configuration schema are recorded in inventory.
6. Installation and update use a resumable journal, backup supported configuration, health-check the new gateway, and retain the previous working state until validation succeeds.
7. Signing-key rotation uses a documented overlap in which old and new keys can authenticate the next release metadata before the old key is retired.

This gives the gateway a reviewed release record, exact payload verification, repeatable acquisition, inventory, transactional update, and recovery.  Windows still does not protect the gateway files as an MSIX package or inventory them as a Windows package.  SetupEngine's versioned gateway-lifecycle contract remains the update authority.  The maintenance coordinator carries that contract and the code required after companion removal.  It can reacquire damaged gateway files only through the same signed metadata, pinned verification key, and payload hashes; it does not define a separate payload source or update policy.

## State ownership and current paths

The current state is split across two per-user roots:

| State | Current location |
|---|---|
| Gateway registry, including URL, local or remote marker, WSL distribution name, connection metadata, shared token, and bootstrap token | `%APPDATA%\OpenClawTray\gateways.json` |
| Per-gateway device identity and device token material | `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json` |
| Local MCP bearer token | `%APPDATA%\OpenClawTray\mcp-token.txt` |
| Settings and other roaming companion data | `%APPDATA%\OpenClawTray\` |
| Setup state, logs, WSL storage, and other local machine state | `%LOCALAPPDATA%\OpenClawTray\` |

The gateway paths are established by [`GatewayRegistry`](../src/OpenClaw.Connection/GatewayRegistry.cs) and its per-gateway identity migration.  The MCP token path is defined by [`OpenClawAppIdentity`](../src/OpenClaw.Shared/OpenClawAppIdentity.cs#L43-L75).  SetupEngine's default roaming and local roots are defined in [`SetupContext`](../src/OpenClaw.SetupEngine/SetupContext.cs#L458-L504).

The production design should split state by owner:

- Companion-only preferences and disposable caches should use package-private storage so companion reset and removal have a clean boundary.
- Gateway registry, maintenance journal, and other cross-component non-secret records need a stable per-user schema available to the companion, maintenance executable, and gateway lifecycle.
- Each installed channel needs its own local MCP endpoint and bearer token.  Those values should survive companion repair or reinstall but must not be shared across Stable, Preview, and Dev.
- Credentials and private keys need an approved protected store and explicit access rules.  General configuration JSON should not become the permanent secret store.
- Logs need named retention and export behavior.

The exact long-term shared root must be selected and implemented before the companion-only removal and Reset contracts can be supported.  A different directory under `%APPDATA%` or `%LOCALAPPDATA%` is not sufficient by itself because packaged desktop AppData writes can be virtualized.  The candidate direct-file design is a declared per-user OpenClaw root excluded from virtualization through the restricted `unvirtualizedResources` capability, which disables virtualization for approved resources.

If OpenClaw is not eligible to use that capability, cross-component records need a supported maintenance API outside the package, while credentials and private keys use an approved Windows protected store.  The current paths above remain migration inputs, not a permanent compatibility promise.

## Migration from the current Inno layout

A supported transition should be automatic.  A user should not need to discover internal folders or understand which files contain credentials.

The maintenance executable should:

1. Run in the target user's context.
2. Stop the current tray and inventory the Inno installation, startup choice, settings, gateway records, credentials, device identity, WSL registration, service state, and local storage.
3. Create a resumable backup of the supported state before changing either installation.
4. Install the new coordinator and companion without opening the packaged tray against half-migrated state.
5. Import supported preferences, preserve gateway records and identity with their access controls, and adopt the existing healthy WSL gateway.
6. Translate the old scheduled task, Run-key value, or Startup-folder shortcut into the package startup task only when startup was enabled.
7. Remove the old Inno binaries and registrations without invoking the current destructive silent-uninstall path.
8. Launch and verify the packaged companion, gateway health, pairing, local MCP, URI protocol ownership, startup state, and Windows node discovery.
9. Remove the backup and migration journal only after the new installation is healthy.

The current Inno uninstaller cannot be used unchanged because its silent path can remove `%LOCALAPPDATA%\OpenClawTray` and the WSL gateway.  A final legacy release needs a migration-preserving mode, or the new coordinator installer must remove the old registration and files through a separately reviewed path.

The production migration design must also define the brief handoff between the old full Inno product and the new maintenance coordinator: their application identifiers, Installed Apps entries, upgrade detection, file ownership, and rollback behavior.  The old registration is removed only after the new coordinator and companion are healthy.

A documented manual recovery path should also exist.  Non-secret preferences and connection metadata can be restored from the maintenance backup while OpenClaw is stopped.  Device keys, tokens, and credentials should be imported by the signed maintenance executable rather than copied by hand so their access controls and schema can be validated.

An explicit clean break may still be offered for prerelease installations, but it should be a user-selected choice that safely removes the old gateway and state.  It should not be the only path merely because the current product is prerelease.

## Stable, Preview, and Dev coexistence

Stable, Preview, and installed Dev should be installable side by side.  Installing one channel must not remove or replace either of the others.

Each channel needs a complete identity:

- Package name and display name.
- URI protocol handler.
- Companion state root.
- Single-instance mutex.
- Startup task.
- Notification identity.
- App Installer feed and policy.
- App execution alias, if that surface is approved.
- Local MCP endpoint and protected bearer-token location.
- Maintenance-coordinator application identifier, installation root, and Installed Apps display name.
- Diagnostics and update authority.

Versions within one channel use the same identity and update in place.  Installed Dev uses a separate development publisher and identity.  Its signing root must be deliberately provisioned through a documented developer or managed bootstrap; an untrusted self-signed package is rejected with trust-remediation guidance rather than treated as a normal self-service installation.  The release-promotion order is Dev, then Preview, then Stable; this describes the order in which a source change is validated and promoted, not an installation sequence imposed on users.

The gateway remains separate from the companion channel:

- Any combination of Stable, Preview, and Dev may connect to the same remote gateway when it is compatible.
- Multiple channels may connect to one existing local gateway only when the installed gateway version falls within every connected channel's compatibility range.  The gateway record names one lifecycle-owner channel: the channel whose coordinator created or explicitly adopted it.  Other channels are read-only consumers of that gateway lifecycle.  Ownership transfer is explicit and requires a compatible remaining coordinator.
- If a channel requires an incompatible gateway, a remote test gateway can be selected.  A second isolated local gateway is supportable only after distinct WSL names, ports, credentials, service records, and cleanup have been implemented and tested.
- Installing another companion channel never initiates a cross-channel migration or removes an existing package.

This preserves the side-by-side tester experience and avoids multiple companions racing to upgrade one shared gateway.

## Command-line execution aliases

MSIX can register a command such as `winnode` through `windows.appExecutionAlias`.  After registration, entering that name in a terminal resolves to the executable supplied by the installed package.

Channel collisions are avoidable.  If the command-line interface is approved as a supported installed surface, `winnode` remains the unqualified Stable name, while Preview and Dev use `winnode-preview` and `winnode-dev`.  Those names allow all three channels to coexist.

The open question is the support contract, not whether the names are available.  The command names, arguments, output, update behavior, gateway selection, and compatibility guarantees would become user-facing commitments.  `winnode` is currently a developer and diagnostic surface, and its future relationship to the separately installed gateway component has not been settled.  No alias should therefore be published until that contract is approved.  If it is approved for the first installed release, the Stable, Preview, and Dev registrations should be implemented and tested together rather than postponed solely because of a solvable collision.  Item 7 under [Proposed team decisions](#proposed-team-decisions) covers this.

## What the lifecycle prototypes established

The sanitized lifecycle evidence is summarized in the [prototype evidence inventory](#prototype-evidence-inventory).  The source archive contains scripts, manifests, JSON results, and deployment logs, but no package binaries, certificates, private keys, tokens, or OpenClaw user state.

The lifecycle tests ran on a disposable x64 VM running Windows 11 Enterprise Insider Preview build 26658.  Remotely issued commands ran as `NT AUTHORITY\SYSTEM`; interactive phases ran as a local administrator account.  Those different contexts matter for the machine-context result below.  Full environment details are in the evidence bundle.

### `windows.customInstall`

**Method**

- A disposable x64 package declared `windows.customInstall`, install, repair, and uninstall actions, the `customInstallActions` restricted capability, and `desktop8:RunAsUser="true"`.
- `MakeAppx` accepted the manifest.
- Normal signed deployment was attempted with a disposable self-signed certificate.
- Loose developer registration, which registers an unpacked layout after its package signature is removed and its publisher is rewritten for development, was then used to exercise the action mechanics.  That path is not supported for production, so the result covers mechanics only.

**Observed result**

- Normal signed deployment failed with `0x800B0109` because the disposable signing root was not trusted at machine scope.
- The unsigned-package variants were rejected because an executable package cannot use the unsigned deployment path.
- Loose developer registration succeeded.
- The install action wrote its marker in the interactive user's `%LOCALAPPDATA%` before the first application launch completed.  Because registration was also performed by that interactive user, this confirms action ordering and file-system scope but does not isolate the effect of `desktop8:RunAsUser="true"`.
- The application then launched as the interactive user.
- Package removal ran the uninstall action.
- `Reset-AppxPackage` failed for the loose development registration with `0x80073CFA` and reported error `0x80070032`, so the repair action was not exercised.

**Recommendation**

The install and uninstall actions ran on the tested build, but this path is not the baseline.  Release use requires all of the following:

1. Written confirmation from the Windows package-platform owner that OpenClaw is eligible to use the restricted capability in the intended package type and that the requested actions run with the required lifecycle and user-context semantics.
2. Production-trusted App Installer validation of install, update from a package without actions, repair, uninstall, failure recovery, and target-user execution.
3. Managed-deployment validation of the same lifecycle.
4. Store validation if Store distribution remains a goal.
5. OpenClaw release and security approval of the action payload, update model, and signing design.

Until those gates pass, the per-user coordinator owns complete removal.

### Per-user Inno coordinator

**Method**

- Inno Setup 6.7.3 was verified and installed into the disposable VM.
- A synthetic per-user coordinator installed a maintenance executable outside the package.
- Signed installation through App Installer failed because the disposable certificate was not trusted, so the coordinator-phase package operations used loose developer registration of unpacked layouts with the package signature removed and the publisher rewritten.  The feed document used a local `file:` URI rather than HTTPS.
- A synthetic companion layout, not the real OpenClaw companion, was registered as version 1, replaced by version 2, removed before the coordinator, and registered again.
- Coordinator-first and companion-first removal orders were exercised.
- Runtime-generated package payloads were added after the coordinator installation to test cleanup of files Inno had not originally installed.

**Observed result**

- The companion package and coordinator had independent package and HKCU uninstall records.
- The coordinator and synthetic gateway state survived replacement of the registered companion layout from version 1 to version 2.  Because this was loose developer re-registration rather than a signed App Installer update, it shows coordinator independence from companion re-registration; it does not demonstrate App Installer update behavior.
- Package-first removal left the coordinator available.  Its recovery command then removed the retained synthetic gateway state.
- Coordinator-first removal removed the companion package, coordinator registration, synthetic gateway state, and coordinator files.
- The first coordinator version left runtime-generated `.appinstaller`, ZIP, and loose package-layout files under its directory because Inno removes only files it installed.
- Adding an explicit `[UninstallDelete]` rule for the payload directory removed those generated files on the corrected run.

**Recommendation**

A small per-user Inno coordinator is feasible and is the recommended WSL-period design for Stable, Preview, and installed Dev.  Production work must replace the synthetic operations with the versioned gateway-maintenance contract, verify real App Installer association under a trusted signature, test the visible Installed Apps experience, provide a signed coordinator update path, and prove that maintenance failure leaves the coordinator available for recovery.

### Machine-context cleanup

**Method**

- The per-user coordinator and companion were installed under the interactive account.
- The maintenance executable was then run as `NT AUTHORITY\SYSTEM` from a machine-context session rather than the interactive user's session.
- The target user's package, uninstall record, state root, and synthetic gateway state were inventoried before and after.

**Observed result**

- The maintenance command returned success and wrote a result under the `SYSTEM` profile.
- Its current SID was `S-1-5-18`; no target-user SID was known.
- The interactive user's package, coordinator record, state, and synthetic gateway state all remained.

**Recommendation**

A machine-context MSI or service is not selected.  WSL distribution registration and CurrentUser-protected credentials require cleanup in the target user's loaded profile.  Machine-context orchestration would therefore need a supported way to identify and act as the intended user, and it must not search another user's profile for secrets.  The prototype is a cautionary result rather than proof that every machine-context design fails: the per-user maintenance tool returned success while acting only on the `SYSTEM` profile.  A per-user MSI remains possible in principle, but it has no demonstrated advantage over the tested Inno approach.

### Remaining prototype limitations

The lifecycle prototype did not prove:

- Successful installation from a production-trusted signing chain.
- App Installer update association, version N to N+1 replacement, background checks, or repair URIs.
- Actual managed-deployment behavior.
- Store eligibility or acceptance, which gates Store publication only rather than the App Installer or coordinator paths.
- `windows.customInstall` repair.
- The real OpenClaw companion, WSL gateway, credentials, and state migration through the coordinator.
- The visible Installed Apps labeling, Advanced options, Repair, Reset, Modify, and uninstall experience.  A second attempt reached normal App Installer package-trust validation, and an interactive-user loose registration succeeded, but the disposable signing root remained untrusted and Windows Settings automation remained policy-blocked.  Neither result was treated as equivalent production UI proof.
- ARM64 lifecycle behavior.
- Behavior on supported production Windows releases rather than Insider Preview builds.

Each of these remains an implementation exit gate.

## What earlier repository and package work established

The earlier sanitized evidence is summarized in the [prototype evidence inventory](#prototype-evidence-inventory).

### Package composition

Baseline x64 and ARM64 packages were built from commit `48a9b9d`, unpacked, hashed, and inventoried.

Five packaging issues were identified.  Items 1 through 3 also persist on current main; items 4 and 5 are observations of the baseline package contents:

1. The disabled [`build-msix` CI job](../.github/workflows/ci.yml#L528-L622) patches the source manifest directly, while the project now generates an intermediate manifest from explicit build inputs.  CI and local builds therefore still have competing metadata paths that must be unified before release output is deterministic.
2. The supported Windows floor is inconsistent between the [`.NET target framework`](../src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L3-L18) and the [source manifest](../src/OpenClaw.Tray.WinUI/Package.appxmanifest#L15-L28).
3. `wxc-exec.exe`, required by `system.run`, is copied into the build and publish directories by [post-build targets](../src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L241-L299) that never declare it as package payload, so it was absent from the baseline package.  The existing check looks at the build or publish output rather than the package, so it passes anyway.  With the helper missing, MXC reports unavailable and `system.run` does not fail closed by default: [`SystemRunBlockHostFallbackWhenMxcUnavailable`](../src/OpenClaw.Shared/SettingsData.cs#L160-L176) defaults to `false`, so commands are routed to the uncontained host runner.  Deterministic package work must declare and verify the helper in the final package, and packaged runtime work must make the default fail closed rather than use host fallback.
4. Both Windows architectures include unrelated Whisper speech-to-text native runtimes.
5. `default-config.json` is duplicated.

A throwaway composition correction added the missing payload and removed the unintended files.  The corrected source tree passed the repository build, Shared and Tray tests, and both WSL gateway to Windows node MXC end-to-end tests.  Those changes were removed after evidence collection, and the exact file-count comparison is retained in the evidence bundle.  Clean-machine installation and packaged runtime behavior remain to be established.

### AppData virtualization

The x64 layout was registered and a small test process was run under that package identity on Windows build 28615, a different Insider build from the lifecycle prototype.  Newly created Roaming AppData, Local AppData, and current-user registry values were redirected to package-private storage.  An unpackaged process could not see them, and package removal deleted them.  The current shared paths in [OpenClawAppIdentity](../src/OpenClaw.Shared/OpenClawAppIdentity.cs#L48-L75) and [SetupContext](../src/OpenClaw.SetupEngine/SetupContext.cs#L489-L504) make the real-tray validation below a release gate rather than an optional optimization.

The real tray was not launched through its manifest entry point, so the final state design still needs a test that runs the real tray executable from a signed installed package.  That test determines whether the candidate `unvirtualizedResources` shared-root design described above is required and whether the package is eligible to use it.

### WSL survival

A test WSL distribution was imported under `%LOCALAPPDATA%\OpenClawTray\wsl` while the caller had package identity.  The distribution registration and virtual disk survived package removal and the distribution still ran.  The prototype did not establish whether the survival occurred because WSL wrote the state through an out-of-process service or for another reason, so this is survival evidence rather than a supported shared-storage contract.

That result proves that companion package removal is not complete product removal.  Reinstall must therefore explicitly adopt, repair, or remove retained gateway state.

### Signing

The x64 package was signed with a matching self-signed certificate and its cryptographic signature was verified.  Deployment failed with `0x800B0109` because the root was not trusted machine-wide.  The current paused [MSIX CI job](../.github/workflows/ci.yml#L528-L622) does not sign the package.

Production package identity, publisher, certificate chain, and RFC 3161 timestamping must be selected before the first supported package.  Preview and Stable should use different package names under one production publisher where the intended Store identity permits it.  Dev should use a separate development publisher whose trusted root is provisioned by the documented developer or managed installation path.

### PR #732

[PR #732](https://github.com/openclaw/openclaw-windows-node/pull/732) explored x64 and ARM64 packaging, feed generation, certificate setup, release signing, update rehearsal, and Stable, Alpha, and Dev naming, which is useful groundwork.  The proposal uses Preview for the prerelease channel that the PR called Alpha.  The branch also removed Inno and the ZIP updater.  That removal should not be carried forward until migration, state, recovery, and complete removal are covered and validated.

Useful scripts and tests should be ported individually after their assumptions are validated against current main.

## Distribution roles

| Delivery | Role |
|---|---|
| Direct MSIX | Offline, diagnostic, or controlled sideload artifact.  No automatic update association is created.  The installing person or managed system remains the named servicing authority until the installation is moved to the approved `.appinstaller` path, with the exact recovery behavior still to be tested. |
| App Installer | Companion installation and update authority for Stable, Preview, and installed Dev direct distribution. |
| Per-user Inno maintenance coordinator | WSL-backed Stable, Preview, and installed Dev acquisition, maintenance registration, App Installer association, product repair, and complete removal. |
| Enterprise software distribution | Deploys the same signed package and coordinator with documented user-context install, detection, update, and removal commands.  Proving deployability is in scope; operating a fleet rollout is not. |
| Microsoft Store | Deferred until representative policy and submission evidence covers full-trust execution, requested capabilities, external gateway lifecycle, state, identity, and complete removal. |
| WinGet | Added only after it points to an approved installer and the resulting update authority is unambiguous. |

Store publication during the WSL period remains difficult because an ordinary Store listing does not deliver the separate Inno coordinator.  A Store release should wait for one of these:

- OpenClaw is approved to use `windows.customInstall` and the production lifecycle passes;
- the future Windows gateway and broker provide complete component removal; or
- the Store owner confirms another supported product-coordination model.

The package should remain Store-compatible where practical, but Store publication is not required to obtain package identity, signing, servicing, inventory, or enterprise deployability.

## Implementation sequence

Each step has an exit gate.  A failed gate changes the design or keeps the current installer in place.  The relative sizes are planning comparisons rather than estimates; schedule and staffing ranges should be added only after the owners have decomposed the work.

| Step | Purpose | Relative size |
|---|---|---|
| 0 | Approve outcomes, product contracts, and owners | Small, decision-heavy |
| 1 | Produce deterministic signed companion packages | Medium |
| 2 | Prove the signed companion runtime and App Installer lifecycle | Large |
| 3 | Build gateway trust and per-channel maintenance coordination | Large |
| 4 | Migrate existing installations and prove Stable/Preview/Dev coexistence | Large |
| 5 | Prove deployment readiness and promote the supported channel | Medium |
| Future integration | Integrate the externally delivered Windows gateway and broker | Platform-dependent |

### Step 0: Approve outcomes, product contracts, and owners

**Work**

1. Approve the [eight required outcomes](#required-outcomes).
2. Confirm MSIX as the companion package and the small per-user Inno coordinator as the WSL-period bridge for Stable, Preview, and installed Dev.
3. Confirm that the gateway remains outside the companion MSIX in both the WSL and Windows-hosted periods.
4. Confirm x64 and ARM64 support, the supported Windows floor, and per-user scope while WSL and CurrentUser credentials are involved.
5. Confirm Stable, Preview, and installed Dev side-by-side installation.
6. Approve the state ownership, migration, repair, removal, and retention contracts.
7. Select the package publisher, signing authority, timestamp service, artifact promotion owner, companion feed owner, gateway release-metadata owner, maintenance-coordinator owner, and Store identity reviewer.
8. Confirm that actual enterprise rollout operation is outside this plan while enterprise deployment contracts and proof remain required.
9. Record the Windows gateway and broker as external dependencies with named platform contacts and expected integration contracts.

**Exit gate**

The supported matrix, component boundaries, owners, state rules, and removal definitions are approved.  Publisher, feed, cleanup owner, and gateway dependency values are complete rather than placeholders.

### Step 1: Produce deterministic signed companion packages

**Work**

- Use one template-to-intermediate-manifest path for local builds and CI.
- Make package version, `.appinstaller` version, channel, publisher, architecture, supported Windows floor, and maximum tested Windows version explicit inputs.
- Generate every channel-visible identifier, such as package name, URI protocol handler, state root, local MCP endpoint, token location, startup task, and notification identity, from one channel definition.
- Declare and test that the desktop entry point runs as the intended full-trust packaged desktop application, including the trust level Windows applies.
- Add package-integrity enforcement and repair locations using a named `.appinstaller` schema version and element.
- Establish named ownership, restricted write access, immutable release publication, and change review for the `.appinstaller` hosting origin.
- Declare the matching signed `wxc-exec.exe` and its sibling files as package payload, and check for them in the package rather than only in the build output.
- Include only target-architecture Windows runtimes.
- Remove duplicate and accidental payloads.
- Sign and timestamp immutable x64 and ARM64 packages.
- Unpack and validate every release artifact against the reviewed manifest and file allowlist.
- Supersede the [current MSIX uninstall guidance](uninstall-msix.md) and the guidance comments in [`validate-msix-storage-paths.ps1`](../scripts/validate-msix-storage-paths.ps1), which describe the current package limitations and packaged AppData redirection.  The prototypes observed custom uninstall actions and AppData redirection, but the restricted capability still requires the five clearance gates in this proposal.

**Exit gate**

Clean x64 and ARM64 machines with no SDK, build tools, or developer certificate install and launch the packages.  Manifest, files, signatures, hashes, architecture, software bill of materials, and provenance match the reviewed expectations.

### Step 2: Prove the signed companion runtime and App Installer lifecycle

**Work**

- Implement one complete pilot-channel identity without colliding with the other two channels.  Preview is the recommended pilot because it exercises the production signing and update pattern before Stable promotion.
- Disable construction and scheduling of the ZIP updater automatically under package identity, report the selected update authority in diagnostics, and test that no ZIP update work can start.
- Publish an immutable HTTPS `.appinstaller` feed with independent document and package versions.
- Verify behavior when App Installer is absent, outdated, or blocked by policy, and document the controlled-deployment fallback.
- Add and migrate the package startup task.
- Run the real tray state probe on clean and pre-existing profiles.
- Select the cross-component state root and protected credential store, then relocate the gateway registry, per-gateway device keys, credentials, and per-channel MCP tokens out of package-virtualized storage with explicit access rules.  Include forward migration and rollback.
- Implement and validate the in-app complete-removal flow and the package-first reinstall recovery path.
- Prove Start menu, URI protocol, startup, notification activation, onboarding, chat, canvas, MCP, and gateway-mediated Windows node behavior.
- Make uncontained host fallback unavailable under package identity.  If MXC or its helper is unavailable, packaged `system.run` must return an explicit error.
- Prove packaged `system.run` on x64 and ARM64, including helper discovery, signature, AppContainer grant and denial behavior, policy passed directly to the helper, policy passed through the temporary configuration file in the command scratch directory, and fail-closed behavior.
- Prove version N to N+1 on launch and in the background, offline launch, missing feed, corrupted package, wrong publisher, interrupted download, and higher-version known-good recovery.
- Validate App Installer update and repair association from the same path every channel coordinator will invoke.

**Exit gate**

The signed installed package performs the full companion workflow on x64 and ARM64.  Startup and activation survive update.  A failed update to version N+1 leaves version N usable.  Repair uses a trusted source, and the ZIP updater never competes with Windows.  Companion-only removal and package Reset leave the gateway registry, per-gateway device keys, credentials, and per-channel MCP token intact and usable by a reinstalled companion, verified by inventory before and after.

### Step 3: Build gateway trust and per-channel maintenance coordination

**Work**

- Replace mutable gateway-script acquisition with signed immutable release metadata and exact payload verification.
- Refactor current in-app removal, `CliUninstallHandler`, SetupEngine cleanup, and `Uninstall-LocalGateway.ps1` behind one tested maintenance contract.
- Build the signed per-user Inno maintenance coordinator and independently installed `OpenClawMaintenance.exe` from one channel-parameterized implementation for Stable, Preview, and installed Dev.  Include the maintenance and SetupEngine cleanup code required after companion removal.
- Add the shared maintenance-schema compatibility contract, minimum-coordinator declaration, feed-promotion checks, and signed coordinator recovery path.
- Install the companion through the approved `.appinstaller` source and prove that the update association survives.
- Add interactive and unattended inventory, gateway repair, product repair, companion-first recovery, and complete removal.
- Gate coordinator uninstall on successful or recoverable maintenance completion.  If maintenance fails or is interrupted, preserve the coordinator, journal, and Installed Apps entry so the operation can be retried.
- Add a signed coordinator update path and damaged-coordinator recovery.
- Validate the two Installed Apps entries through the real UI and revise names or descriptions if users cannot distinguish product-level removal from companion-only removal.
- Validate target-user execution from the selected enterprise deployment mechanism without conducting an actual fleet rollout.
- Continue the `windows.customInstall` path only after written eligibility guidance.  If it remains eligible, rerun installation, update, repair, removal, and managed deployment with a production-trusted signature.

**Exit gate**

The real companion, WSL gateway, credentials, startup registrations, and state can be repaired and completely removed in either uninstall order without manual WSL or file-system commands.  App Installer remains the companion update authority.  No tester-facing channel is promoted to this design if this gate fails, and the current full Inno installer remains the supported fallback.

### Step 4: Migrate existing installations and prove coexistence

**Work**

- Implement automatic migration from the current Inno layout.
- Add an integrity-protected backup created by the signed maintenance executable, a non-secret journal, interruption recovery, and a manual non-secret recovery path.
- Define and test the identity, Installed Apps, file-ownership, upgrade-detection, and rollback handoff between the old full Inno product and the new maintenance coordinator.
- Remove legacy Task Scheduler, Run-key, and Startup-folder registrations after the package startup task is healthy.
- Install Stable, Preview, and Dev side by side and prove package, URI protocol handler, app execution alias when approved, mutex, state, local MCP endpoint and token, startup, notification, maintenance-registration, and feed isolation.
- Prove all three channels against a compatible shared gateway.
- Prove lifecycle-owner assignment, transfer, and removal blocking for a shared local gateway.
- Define the incompatible-gateway experience and prototype an isolated Preview local gateway only if that scenario is required.
- Exercise companion-only removal, retained-state reinstall, gateway adoption, gateway repair, complete removal, and reinstall from nothing.

**Exit gate**

Existing users can retain supported settings, gateway records, identity, and WSL state automatically.  Stable, Preview, and installed Dev can all remain installed without collision.  Every removal choice has the documented result.

### Step 5: Prove deployment readiness and promote the supported channel

**Work**

- Produce documented install, detection, update, repair, and removal commands for the enterprise software-distribution systems selected by the deployment owners.
- Validate the supported target-user mechanism, including a signed-out assigned user and a departed-user profile, on representative managed machines.  Actual fleet rollout remains out of scope.
- Publish support, migration, state-retention, recovery, and removal documentation.
- Review the real manifest and lifecycle with the Store owner or submit a representative private package.
- Add an `.msixbundle` only after both companion architectures pass equivalent tests.
- Add WinGet only after installer and update ownership are clear.
- Retire the full Inno companion payload and ZIP updater only after the MSIX and coordinator own every required responsibility.

**Exit gate**

Support and deployment owners can identify each installed component, version, update authority, compatibility result, and retained state.  A failed migration is recoverable, a known-good higher version can be deployed without launching the broken app, and complete removal does not depend on remembering an in-app step.

### Future integration: Adopt the Windows gateway and broker

This begins after the Windows platform owners publish supported gateway installation, identity, policy, update, repair, and removal contracts.

**OpenClaw integration work**

- Call the supported component installation and broker APIs.
- Verify gateway package identity and compatibility.
- Use the brokered restricted identity and MXC policy.
- Implement authenticated companion-to-gateway communication.
- Migrate supported WSL state only after the Windows gateway is healthy.
- Remove the WSL distribution when the user approves or policy requires it.
- Retire the WSL coordinator only after the new lifecycle proves independent update, repair, recovery, and complete removal.

The external Windows gateway remains a separate installed component.  It is not added to the companion MSIX.

## Validation matrix

| Area | Required cases |
|---|---|
| Architecture and Windows version | Clean x64 and ARM64 systems on the Windows version selected in [Step 0](#step-0-approve-outcomes-product-contracts-and-owners) and current Windows 11.  ARM64 runtime behavior must be exercised on representative ARM64 hardware. |
| Install | No prior state, current Inno state, direct MSIX, and Inno-coordinated App Installer installation for Stable, Preview, and Dev, plus representative managed assignment. |
| Package identity and trust | Correct and wrong publisher, trusted and untrusted chain, altered package, expired signing certificate with valid RFC 3161 timestamp, package integrity failure, trusted repair source, and no repair source. |
| App Installer | First install association, package-version advance, `.appinstaller` document-version advance, on-launch and background checks, prompt and deferral policy, offline launch, failed download, interrupted update, and higher-version known-good replacement. |
| Core companion | Start menu, URI protocol, startup task, notifications, onboarding, chat, canvas, local MCP, gateway-mediated node calls, and declared device capabilities after grant, denial, and revocation. |
| Contained command execution | Installed-package `system.run` on x64 and ARM64, protected helper discovery, vendor signature and expected hash, policy passed directly and through the scratch-directory configuration file, intended AppContainer grants and denials, and no uncontained fallback. |
| Gateway acquisition | Valid and invalid metadata signature, valid and invalid payload hash, version mismatch, interrupted installation, signing-key rotation overlap, health-check failure, and restoration of the previous gateway. |
| Repair | Companion repair, companion reset, gateway repair, product repair, coordinator update, damaged coordinator, package already removed, and user-visible entry points. |
| Migration | Automatic current-Inno migration, interruption and rerun, backup restore, startup migration, gateway adoption, non-secret manual recovery, and explicit clean break. |
| Channels | Stable, Preview, and installed Dev side by side; independent updates, URI protocol handlers, aliases when approved, local MCP endpoints and tokens, startup, state, notifications, and maintenance registrations; shared compatible gateway with one lifecycle owner; incompatible gateway; companion-only removal of each channel. |
| Removal | In-app complete removal, coordinator Installed Apps removal, companion-first recovery, coordinator-first removal, shared-gateway reference and ownership checks, keep-for-reinstall, reinstall adoption, generated payload cleanup, maintenance failure and interruption, proof that the recovery path survives, and final inventory showing no selected state remains. |
| User context | Correct assigned user, signed-out assigned user, second user on the same device, `SYSTEM` negative test, retained departed-user profile, and device decommissioning boundary. |
| Distribution | App Installer and representative enterprise deployment.  Store only after direct policy review or representative submission.  No actual fleet rollout is required by this plan. |
| Future Windows gateway | Separate component identity, brokered restricted identity, MXC policy, authenticated communication, independent update and repair, WSL migration, and complete removal after the external contract exists. |

Every run records:

- the source commit and package and coordinator versions;
- the package family, publisher, and architecture;
- the signature and timestamp authority and artifact hashes;
- the Windows build and deployment identity;
- the exact commands or UI steps;
- the before-and-after inventory and state paths; and
- the requirement result.

## Proposed team decisions

The OpenClaw Windows maintainers, release owner, security owner, and Windows package-platform partners should resolve these items in a design review and record the agreed answers in this document before [Step 1](#step-1-produce-deterministic-signed-companion-packages) begins:

1. Approve the companion-only MSIX boundary and the rule that gateways remain separate installed components.
2. Approve the per-user Inno coordinator as the WSL-period bridge for Stable, Preview, and installed Dev, subject to the production lifecycle gates.
3. Confirm x64, ARM64, and the supported Windows floor.
4. Confirm Stable, Preview, and installed Dev side-by-side installation, the lifecycle-owner rule for a shared local gateway, and whether isolated additional local gateways are required.
5. Approve the automatic migration and state-retention contracts.
6. Decide the default for complete removal of preferences, logs, credentials, device identity, and the local gateway.
7. Decide whether the installed CLI is a supported user-facing surface.  If it is, approve `winnode` for Stable, `winnode-preview` for Preview, and `winnode-dev` for Dev.
8. Name owners for signing, timestamping, feed promotion, gateway release metadata, maintenance tooling, enterprise deployment contracts, Store review, and emergency rollback.
9. Record the expected Windows gateway and broker teams as external dependencies rather than commitments made by this proposal.

Any correction to a current product or platform contract should be raised during that review.  [Step 1](#step-1-produce-deterministic-signed-companion-packages) should not begin until the [Step 0](#step-0-approve-outcomes-product-contracts-and-owners) decisions are recorded.

## Evidence and references

### Prototype evidence inventory

Two sanitized evidence archives were produced and their embedded manifests were verified during the investigation.  Binary archives are not checked into `docs` because they are not reviewable source.  The prototype methods, observed results, and limitations are recorded in the sections above, and every remaining behavior is carried as a production validation gate rather than inferred from the prototypes.

- Package composition, signing, AppData, WSL survival, and earlier validation evidence: SHA-256 `698F366003AAE762DEBB44FFB541B8374BBCE35AA2625B6CD65DC7E8EEF9DB4D`.
- `windows.customInstall`, coordinator, removal-order, and user-context lifecycle evidence: SHA-256 `EC3614122DD034EBE545AAD1F438A37F249BC451A738D6B2D061AAB441E27889`.

The lifecycle evidence inventory contained:

| Finding | Entries |
|---|---|
| VM, tool, hash, and limitation context | `metadata/prototype-context.json`, `metadata/manifest.json` |
| Custom install and uninstall action behavior | `results/custom-install-loose-runtime.json`, `results/custom-action-markers.json` |
| Signed deployment trust failure | `results/signed-custom-trust.json` |
| Unsigned executable-package rejection | `results/unsigned-custom-executable.json` |
| Coordinator update and both removal orders | `results/coordinator-lifecycle.json`, `results/coordinator-installed.json` |
| App Installer failure and loose developer-registration substitute | `results/install-v1.json` |
| Later signed App Installer trust validation and blocked Installed Apps/repair UI attempt | `results/repair-ui-attempt.json` |
| Machine-context cleanup returned success without removing the user's installation or state | `results/machine-context-cleanup.json`, `results/machine-context-cleanup-recovery.json` |
| Initial generated-payload cleanup defect and correction | `metadata/initial-cleanup-observation.json`, `scripts/coordinator.iss` (corrected).  The defective first script is described in the observation record but is not retained. |
| Prototype scripts retained | `scripts/*`.  These reproduce the coordinator install, replacement, and removal phases.  The `windows.customInstall` loose-registration phase and `SYSTEM` machine-context phase were run interactively and are represented by result records rather than retained driver scripts. |

### OpenClaw sources

- [Current optional MSIX build and payload configuration](../src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L41-L114)
- [Current intermediate manifest generation](../src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L116-L222)
- [Current package manifest and capabilities](../src/OpenClaw.Tray.WinUI/Package.appxmanifest#L15-L65)
- [Current Release and Dev identity contract](../src/OpenClaw.Shared/OpenClawAppIdentity.cs#L7-L76)
- [Current ZIP updater construction](../src/OpenClaw.Tray.WinUI/App.xaml.cs#L46-L50)
- [Current update-coordinator construction](../src/OpenClaw.Tray.WinUI/App.xaml.cs#L621-L636)
- [Current auto-start registration](../src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs#L13-L83)
- [Current Inno lifecycle](../installer.iss#L43-L350)
- [Current headless SetupEngine removal entry point](../src/OpenClaw.Tray.WinUI/CliUninstallHandler.cs#L18-L73)
- [Current local-gateway cleanup utility](../scripts/Uninstall-LocalGateway.ps1)
- [Current gateway registry paths and schema](../src/OpenClaw.Connection/GatewayRegistry.cs)
- [Current MCP token path](../src/OpenClaw.Shared/OpenClawAppIdentity.cs#L43-L75)
- [Current setup data roots](../src/OpenClaw.SetupEngine/SetupContext.cs#L458-L504)
- [Current pinned gateway version and installer URL](../src/OpenClaw.SetupEngine/GatewayLkgVersion.cs#L3-L22)
- [Current gateway installation](../src/OpenClaw.SetupEngine/SetupSteps.cs#L1228-L1304)
- [Current native Windows gateway architecture option](WINDOWS_NODE_ARCHITECTURE.md)
- [Current MSIX uninstall analysis](uninstall-msix.md)
- [PR #732](https://github.com/openclaw/openclaw-windows-node/pull/732)

### Windows references

- [MSIX overview](https://learn.microsoft.com/windows/msix/overview)
- [How packaged desktop applications install, virtualize state, and uninstall](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [App Installer file overview](https://learn.microsoft.com/windows/msix/app-installer/app-installer-file-overview)
- [App Installer availability and installation](https://learn.microsoft.com/windows/msix/app-installer/app-installer-root)
- [Install and update App Installer](https://learn.microsoft.com/windows/msix/app-installer/install-update-app-installer)
- [App Installer update settings](https://learn.microsoft.com/windows/msix/app-installer/update-settings)
- [App Installer update and repair locations](https://learn.microsoft.com/uwp/schemas/appinstallerschema/element-appinstaller)
- [MSIX signing and package integrity](https://learn.microsoft.com/windows/msix/package/signing-package-overview)
- [RFC 3161 Authenticode timestamp guidance](https://learn.microsoft.com/windows/win32/seccrypto/time-stamping-authenticode-signatures)
- [Package startup tasks](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-startuptask)
- [App execution aliases](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-appexecutionalias)
- [Restricted capabilities](https://learn.microsoft.com/windows/uwp/packaging/app-capability-declarations)
- [`windows.customInstall`](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-custominstall)
- [`windows.customInstall` uninstall action](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-uninstallaction)
- [Windows Installer](https://learn.microsoft.com/windows/win32/msi/windows-installer-portal)
- [Intune Win32 application command and detection model](https://learn.microsoft.com/intune/app-management/deployment/add-win32)

### Planning inputs

- Internal Microsoft MSIX packaging requirements document (not publicly accessible)
- [PR #732](https://github.com/openclaw/openclaw-windows-node/pull/732)
