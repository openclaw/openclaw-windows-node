# OpenClaw Windows Node issue review: shard 3

Reviewed **31/31 issues and all 77 comments** from the supplied complete inventory of 124 open issues and 93 open PRs. Assignment: ascending issue number, zero-based index modulo 4 = 3.

Source and rechecked main: `e59374c064f43d68f5c72d070d4c89ef2d20ec3b`. No source changes or GitHub mutations. PR references distinguish merged changes, open candidates and fork contributions.

## Summary

- **existing-pr: 10**
- **keep-open: 16**
- **needs-info: 2**
- **close-obsolete: 1**
- **simple-fix: 2**

- Best closure candidate: **#1764**, limited to the now-retired Win32k-denial trigger; the reporter already demonstrated successful `whoami` with MXC enabled and compatibility on. No universal compatibility claim.
- Best small fixes without an upstream open PR: **#2004** (sanitize two URL display boundaries) and **#2008** (remove argument-derived support activity titles). Both already have contributor fork branches to review/reuse.
- **#1840 is not unowned:** the exact history fallback is already changed by #1685 (fix: retain companion sessions and bound large history loading).
- Do not close **#1756**, **#1899**, or **#1945** based only on their partial merged fixes.

## Validation and limits

- Executed deterministic triage checks: `node --test .github/scripts/repository-triage.test.cjs .github/extensions/openclaw-triage-dashboard/triage-state.test.mjs`: **71 passed, 0 failed, 0 skipped**.
- Product build/.NET suites and native Windows/UI/Store/WSL/MXC proof were **not run**. This is read-only Linux triage; existing test source was inspected, not counted as a passing run. No hosts were launched or provisioned.
- Recommendation confidence below is not merge readiness or verified fix confidence.
- All comments below are drafts, not posted. Where **Post new comment: no**, retain the existing discussion instead of adding a duplicate request. Parent should refresh exact issue/PR state before mutation.

## Issue index

| Issue | Classification | Disposition | Confidence | New comment? |
|---|---|---|---:|---|
| [#1167 [Bug] Switching sessions can hang WinUI while projecting ~200 history messages (v2026.7.1)](https://github.com/openclaw/openclaw-windows-node/issues/1167) | bug | **existing-pr** | 97% | yes |
| [#1292 [Bug]:  Windows Desktop UI clips config panels and chat bubbles at high display scaling](https://github.com/openclaw/openclaw-windows-node/issues/1292) | bug | **keep-open** | 97% | no |
| [#1421 [Bug] Windows Companion 2026.9.3 crashes in WinUI ArrangeOverride after chat.history](https://github.com/openclaw/openclaw-windows-node/issues/1421) | bug | **needs-info** | 95% | yes |
| [#1442 Claude CLI auth not detected in onboarding despite successful claude auth login inside OpenClawGateway WSL distro](https://github.com/openclaw/openclaw-windows-node/issues/1442) | bug | **needs-info** | 94% | yes |
| [#1527 Proposal: add discovery schemas for five audio tools](https://github.com/openclaw/openclaw-windows-node/issues/1527) | enhancement | **keep-open** | 98% | no |
| [#1567 Tray menu renders empty after extended uptime: icon failures are swallowed silently](https://github.com/openclaw/openclaw-windows-node/issues/1567) | bug | **keep-open** | 97% | no |
| [#1573 Tray keep-alive can still start during Windows session end and its repair adapter ignores the cancellation token](https://github.com/openclaw/openclaw-windows-node/issues/1573) | bug | **keep-open** | 98% | no |
| [#1614 Hub 2026.9.4: browser.proxy approved, but Edge extension cannot find native helper](https://github.com/openclaw/openclaw-windows-node/issues/1614) | support-and-enhancement | **keep-open** | 97% | no |
| [#1677 Hub sidebar ignores gateway session groups, and can show names that differ from the gateway label](https://github.com/openclaw/openclaw-windows-node/issues/1677) | enhancement-with-partial-bug-fix | **keep-open** | 99% | yes |
| [#1687 system.run.prepare rejects every gateway exec: "custom-env-not-supported" (OPENCLAW_CHANNEL_CONTEXT)](https://github.com/openclaw/openclaw-windows-node/issues/1687) | bug | **keep-open** | 99% | yes |
| [#1702 [Feature Request] Add Fish Audio TTS provider to Windows Hub voice settings](https://github.com/openclaw/openclaw-windows-node/issues/1702) | enhancement | **keep-open** | 98% | no |
| [#1722 Windows Companion browser proxy rejects native Store Gateway; required browser bootstrap helper unavailable](https://github.com/openclaw/openclaw-windows-node/issues/1722) | bug-and-support | **keep-open** | 99% | yes |
| [#1756 [Windows ARM64] Native Gateway setup times out while Microsoft Store deployment continues](https://github.com/openclaw/openclaw-windows-node/issues/1756) | bug-with-merged-fix-awaiting-proof | **keep-open** | 99% | no |
| [#1764 Windows Node MXC sandbox — whoami fails with 0xC0000142](https://github.com/openclaw/openclaw-windows-node/issues/1764) | bug-with-obsolete-trigger | **close-obsolete** | 94% | yes |
| [#1823 fix(chat): keep a lifecycle end that arrives before the reset echo](https://github.com/openclaw/openclaw-windows-node/issues/1823) | bug | **keep-open** | 98% | no |
| [#1840 fix(chat): keep a second identical message the history snapshot missed](https://github.com/openclaw/openclaw-windows-node/issues/1840) | bug | **existing-pr** | 98% | yes |
| [#1871 Windows node missing computer.act — CUA diagnostic database access blocked](https://github.com/openclaw/openclaw-windows-node/issues/1871) | support | **keep-open** | 98% | no |
| [#1899 fix(tests): fixture UI checks fail on missing placement RPC and intermittent wide-window visibility](https://github.com/openclaw/openclaw-windows-node/issues/1899) | test-infrastructure-tracker | **keep-open** | 99% | no |
| [#1937 Local AI: first message after launch re-processes the whole system prompt (5-16 s)](https://github.com/openclaw/openclaw-windows-node/issues/1937) | performance-enhancement | **existing-pr** | 99% | yes |
| [#1945 [Bug] Screen reader hits unnamed, icon-only and type-named controls in Settings, Bindings, Config, Sessions and Usage](https://github.com/openclaw/openclaw-windows-node/issues/1945) | bug-with-partial-fix | **keep-open** | 99% | yes |
| [#1956 Turning off Start with Windows does not stop the startup shortcut the installer created](https://github.com/openclaw/openclaw-windows-node/issues/1956) | bug | **existing-pr** | 99% | yes |
| [#1983 fix: cancel a stalled portable update download](https://github.com/openclaw/openclaw-windows-node/issues/1983) | bug | **existing-pr** | 99% | no |
| [#2004 Tray gateway menu and Local AI page show the raw gateway URL](https://github.com/openclaw/openclaw-windows-node/issues/2004) | bug-privacy | **simple-fix** | 99% | yes |
| [#2008 Copied support text includes tool commands it says are excluded](https://github.com/openclaw/openclaw-windows-node/issues/2008) | bug-privacy | **simple-fix** | 99% | yes |
| [#2015 Failed exec-policy save can miss the permissions page](https://github.com/openclaw/openclaw-windows-node/issues/2015) | defensive-threading-concern | **keep-open** | 96% | no |
| [#2032 Cron Remove deletes on the first click and Run Now starts a disabled job](https://github.com/openclaw/openclaw-windows-node/issues/2032) | bug-and-ux-proposal | **existing-pr** | 98% | yes |
| [#2038 A late workspace file can be shown for the wrong agent](https://github.com/openclaw/openclaw-windows-node/issues/2038) | bug | **existing-pr** | 99% | no |
| [#2042 Cancel does not stop a Whisper or Piper download, and the body has no size cap](https://github.com/openclaw/openclaw-windows-node/issues/2042) | bug-security-resource-exhaustion | **existing-pr** | 99% | no |
| [#2072 Make NodeService MCP server start and stop atomic](https://github.com/openclaw/openclaw-windows-node/issues/2072) | bug-security-lifecycle | **existing-pr** | 99% | yes |
| [#2076 Give the cron Run Now safety timer a per-run token](https://github.com/openclaw/openclaw-windows-node/issues/2076) | bug | **existing-pr** | 99% | yes |
| [#2098 Tray/Hub 2026.11.1: intermittent hard crashes (stowed exception 0xC000027B in CoreMessagingXP.dll / Microsoft.UI.Xaml.dll) - 3 dumps in 5 days](https://github.com/openclaw/openclaw-windows-node/issues/2098) | bug-crash | **keep-open** | 99% | no |

## Per-issue evidence and exact proposed comments

### #1167: [Bug] Switching sessions can hang WinUI while projecting ~200 history messages (v2026.7.1)

- **Classification:** bug
- **Disposition:** existing-pr
- **Recommendation confidence:** 97%
- **Discussion reviewed:** body and all 10 comments
- **Post new comment:** yes, after live reconciliation

A real sustained history-restoration hang is documented, but no reporter-specific resolution is established. Current main is newer than the durable bot review: Reactor is preview.17 and tail navigation no longer calls ItemsView.StartBringItemIntoView directly. Open #1935 (fix(chat): chat freezes when opening tall message history) and #2083 (fix(chat): switching conversations can land on old messages or freeze the chat) still overlap the diagnosed navigation owner, with incomplete full-app acceptance. Do not create another scrolling fix or close from dependency upgrades alone.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749-832`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749-L832)
- [`src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj:105-106`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L105-L106)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs:16-48`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs#L16-L48)

**Pull requests**

- [#1935 (fix(chat): chat freezes when opening tall message history)](https://github.com/openclaw/openclaw-windows-node/pull/1935): open; observed head `bc2f27744463fbea9b8a77ac7173e107cb01e53c`.
- [#2083 (fix(chat): switching conversations can land on old messages or freeze the chat)](https://github.com/openclaw/openclaw-windows-node/pull/2083): open (draft); observed head `374d67bae08e54dd656eb05e74dbdf03e6e307b8`.
- [#1685 (fix: retain companion sessions and bound large history loading)](https://github.com/openclaw/openclaw-windows-node/pull/1685): open (draft); observed head `a79b8c4410b59faf9fedc123365da4236cb3d7e5`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1167
- https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-6352852481

**Missing proof / limits**

- Affected native x64/current-build history restoration and sustained responsiveness
- Complete full-app reader-position and tail acceptance for selected candidate

**Exact proposed comment**

> Keeping this open for the sustained startup/history-restoration hang. Current main now uses Reactor preview.17 and a different tail-realization path, so the older source description is no longer current. #1935 (fix(chat): chat freezes when opening tall message history) and #2083 (fix(chat): switching conversations can land on old messages or freeze the chat) already cover the relevant navigation work. Acceptance still needs the affected native x64 history-restoration case, final-row visibility and scrolled-up reader preservation. The existing redacted-stack request remains sufficient; no conversation reset is requested.

### #1292: [Bug]:  Windows Desktop UI clips config panels and chat bubbles at high display scaling

- **Classification:** bug
- **Disposition:** keep-open
- **Recommendation confidence:** 97%
- **Discussion reviewed:** body and all 5 comments
- **Post new comment:** no; draft is retained for the next substantive update

Current Config implements compact reflow and chat has newer wrapping/layout code, but the issue reports two surfaces at a specific 3840x2160/300% configuration. No equivalent complete current-head proof or reporter confirmation exists. Source tests are not scaled-viewport proof.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Pages/ConfigPage.xaml.cs:26-31`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ConfigPage.xaml.cs#L26-L31)
- [`src/OpenClaw.Tray.WinUI/Pages/ConfigPage.xaml.cs:788-837`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ConfigPage.xaml.cs#L788-L837)
- [`tests/OpenClaw.Tray.Tests/AdaptiveSettingsLayoutSourceTests.cs:43-62`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/AdaptiveSettingsLayoutSourceTests.cs#L43-L62)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1292
- https://github.com/openclaw/openclaw-windows-node/issues/1292#issuecomment-5789581650

**Missing proof / limits**

- Current-head native Config and full long-chat rendering at 300% scaling

**Exact proposed comment**

> Keeping this open until both reported surfaces are verified at 3840x2160 with 300% scaling. Current main has Config reflow and newer chat wrapping, but these changes do not by themselves prove that all text and actions are reachable at the reported viewport. The next step is a current-build Config and long-chat resize check, retaining any remaining failing case as a layout regression.

### #1421: [Bug] Windows Companion 2026.9.3 crashes in WinUI ArrangeOverride after chat.history

- **Classification:** bug
- **Disposition:** needs-info
- **Recommendation confidence:** 95%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** yes, after live reconciliation

The original x64 ArrangeOverride crash is concrete. Merged #1549 (fix(chat): adopt Reactor preview.16 session-switch crash workaround) and later preview.17 are present, but reduced invalid-anchor proof does not establish this exact history crash resolved. Source currentness corrects the old bot claim that the candidate remains open.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj:105-106`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L105-L106)
- [`src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749-832`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749-L832)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs:16-48`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs#L16-L48)

**Pull requests**

- [#1549 (fix(chat): adopt Reactor preview.16 session-switch crash workaround)](https://github.com/openclaw/openclaw-windows-node/pull/1549): merged; merge `a4dd026cc5f85a444a0fcd05eef5b86282dbda8c`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1421
- https://github.com/openclaw/openclaw-windows-node/issues/1421#issuecomment-5668460714

**Missing proof / limits**

- Post-mitigation affected-conversation result
- Symbolized failing control/stowed exception if still reproducible

**Exact proposed comment**

> The renderer mitigation in #1549 (fix(chat): adopt Reactor preview.16 session-switch crash workaround) has merged, and current main uses preview.17, but that is not proof that this specific crash is resolved. Does opening the same preserved conversation still terminate Companion after updating through its normal update UI? If it does, please share the exact Companion version and a redacted symbolized exception/stack below ArrangeOverride. Please do not reset the conversation or publish a full memory dump.

### #1442: Claude CLI auth not detected in onboarding despite successful claude auth login inside OpenClawGateway WSL distro

- **Classification:** bug
- **Disposition:** needs-info
- **Recommendation confidence:** 94%
- **Discussion reviewed:** body and all 4 comments
- **Post new comment:** yes, after live reconciliation

Authentication is Gateway-mediated, not a local credential-file lookup. The reporter already confirmed openclaw-user login and later missing executable; asking them to log in as that user again would duplicate answered information. Missing versions and the operation that removes the CLI prevent safe auth or cleanup changes.

**Source and test evidence**

- [`src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs:181-207`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs#L181-L207)
- [`src/OpenClaw.SetupEngine/CleanupStaleDistroStep.cs:32`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/CleanupStaleDistroStep.cs#L32)
- [`tests/OpenClaw.SetupEngine.Tests/GatewayAiSetupClientTests.cs:108-138`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/GatewayAiSetupClientTests.cs#L108-L138)
- [`tests/OpenClaw.SetupEngine.Tests/OnboardingFlowPolicyTests.cs:147`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/OnboardingFlowPolicyTests.cs#L147)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1442
- https://github.com/openclaw/openclaw-windows-node/issues/1442#issuecomment-5729142975

**Missing proof / limits**

- Companion/Gateway/Claude CLI versions
- Restart-only versus install-retry isolation
- Redacted service execution-context comparison

**Exact proposed comment**

> The follow-up already confirms successful login as the openclaw user, so repeating that setup is not the next step. Please provide the affected Companion, Gateway and Claude CLI versions, and clarify whether claude disappears after only quitting/reopening Companion, or only after pressing Install/retrying setup. A redacted diagnostic identifying the Gateway service user and whether claude is found in that service context would separate authentication from reinstall cleanup. Please do not reinstall the distro or copy credentials for this investigation.

### #1527: Proposal: add discovery schemas for five audio tools

- **Classification:** enhancement
- **Disposition:** keep-open
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** no; draft is retained for the next substantive update

All command discovery schemas are still empty/permissive on main. This is a concrete discovery enhancement, not a runtime regression. The five-tool fork proposal is not an upstream open PR; schema ownership and strictness must be agreed, preserving parser coercion and adding current Kokoro metadata.

**Source and test evidence**

- [`src/OpenClaw.Shared/Mcp/McpToolBridge.cs:265-286`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mcp/McpToolBridge.cs#L265-L286)
- [`docs/MCP_MODE.md:395`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/MCP_MODE.md#L395)
- [`tests/OpenClaw.Shared.Tests/McpToolBridgeTests.cs:739-840`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/McpToolBridgeTests.cs#L739-L840)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1527
- https://github.com/luisquintanilla/openclaw-windows-node/tree/proposal/mcp-audio-tool-schemas

**Missing proof / limits**

- Maintainer ownership/scope decision
- Current-head parser/schema compatibility and MCP discovery/invocation

**Exact proposed comment**

> Keeping this as the five-audio-tool discovery proposal. Main still advertises generic schemas. Before refreshing the contributor branch, the maintainer decision is whether to expose capability-owned descriptors, as documented in MCP_MODE.md, or accept the small bridge helper. Preserve current parser coercion and permissive additional properties, include current provider information, and prove tools/list plus tools/call compatibility rather than introducing runtime validation implicitly.

### #1567: Tray menu renders empty after extended uptime: icon failures are swallowed silently

- **Classification:** bug
- **Disposition:** keep-open
- **Recommendation confidence:** 97%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** no; draft is retained for the next substantive update

The uptime-related empty menu is credible and remains unsolved. The suggested null-icon cause is not established: text rows are built independently and ShowMenu already catches/logs construction errors. A hardcoded Fluent font fallback would conflict with Windows 10 compatibility tests. Shared exception code with 2098 is not duplicate evidence.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Windows/TrayMenuWindow.xaml.cs:571-610`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Windows/TrayMenuWindow.xaml.cs#L571-L610)
- [`src/OpenClaw.Tray.WinUI/Services/TrayController.cs:180-206`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/TrayController.cs#L180-L206)
- [`src/OpenClaw.Tray.WinUI/Helpers/FluentIconCatalog.cs:134-148`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Helpers/FluentIconCatalog.cs#L134-L148)
- [`tests/OpenClaw.Tray.Tests/FluentIconCatalogTests.cs:112-138`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/FluentIconCatalogTests.cs#L112-L138)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1567
- https://github.com/openclaw/openclaw-windows-node/issues/2098

**Missing proof / limits**

- Current failing menu visual tree and matching exceptions
- Allocation/lifetime evidence tying popup or GDI growth to missing rows

**Exact proposed comment**

> Keeping this open as an empty-menu/resource-lifetime investigation, separate from the hard-crash report #2098 unless matching evidence establishes a common cause. Current source creates row labels outside the optional-icon branch and already logs menu-construction exceptions. The next useful evidence is the failing menu visual tree, relevant redacted tray errors and resource counts from the same process; a font fallback or window-cache reset would be speculative at this point.

### #1573: Tray keep-alive can still start during Windows session end and its repair adapter ignores the cancellation token

- **Classification:** bug
- **Disposition:** keep-open
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** no; draft is retained for the next substantive update

Main still drops the repair callback token and TryEnsureAsync has no cancellation parameter or final lifecycle admission before Process.Start. The reporter explicitly widened scope beyond App-only guards. Shared Windows session-ending ownership in 1575 is a dependency, so this is not a safe isolated one-line repair.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:1057-1074`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L1057-L1074)
- [`src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs:31-97`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs#L31-L97)
- [`tests/OpenClaw.Tray.Tests/AppRefactorContractTests.cs:143-170`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/AppRefactorContractTests.cs#L143-L170)
- [`tests/OpenClaw.Tray.Tests/ManagedLocalGatewayRepairCoordinatorTests.cs:480`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ManagedLocalGatewayRepairCoordinatorTests.cs#L480)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1573
- https://github.com/openclaw/openclaw-windows-node/issues/1575
- https://github.com/openclaw/openclaw-windows-node/issues/1573#issuecomment-5932164369

**Missing proof / limits**

- Shared lifecycle contract from 1575
- Deterministic zero-process-start races and normal re-arm
- Authorized Windows session-end proof

**Exact proposed comment**

> The token-loss and session-ending gap remain on main. The accepted scope is the minimal App adapter plus keep-alive cancellation boundary and regression tests, not only an entry guard. Establish the shared lifecycle owner from #1575, forward the repair token, and recheck cancellation/session ending immediately before process creation after asynchronous preparation. Preserve normal startup/re-arm behavior and existing repair budgets; prove zero starts in both early and mid-preparation cancellation cases.

### #1614: Hub 2026.9.4: browser.proxy approved, but Edge extension cannot find native helper

- **Classification:** support-and-enhancement
- **Disposition:** keep-open
- **Recommendation confidence:** 97%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** no; draft is retained for the next substantive update

Capability approval exposes a proxy, not native-helper provisioning. Main still documents the bundled browser-control host installer as unfinished. The split-machine Edge/native-extension topology needs a maintained support/ownership decision; absence of a helper is not fixed by pairing again.

**Source and test evidence**

- [`src/OpenClaw.Shared/Capabilities/BrowserProxyCapability.cs:64-100`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Capabilities/BrowserProxyCapability.cs#L64-L100)
- [`docs/WINDOWS_NODE_ARCHITECTURE.md:874`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/WINDOWS_NODE_ARCHITECTURE.md#L874)
- [`tests/OpenClaw.Shared.Tests/CapabilityTests.cs:966-1005`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/CapabilityTests.cs#L966-L1005)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1614
- https://github.com/openclaw/openclaw-windows-node/issues/1722

**Missing proof / limits**

- Supported Edge and split-machine helper/runtime contract
- Native-host discovery and browser invocation on supported topology

**Exact proposed comment**

> Keeping this open for the supported browser-host setup contract. Enabling browser.proxy does not install or register the extension native helper; it forwards to a separately owned browser-control runtime. The next maintainer action is to document the supported Edge/remote-Gateway matrix and the official helper installation owner, then verify this two-PC scenario. Re-pairing or bypassing listener-owner checks is not a supported repair.

### #1677: Hub sidebar ignores gateway session groups, and can show names that differ from the gateway label

- **Classification:** enhancement-with-partial-bug-fix
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** yes, after live reconciliation

Naming enrichment is merged and guarded by real-client protocol tests. WorkspaceSession still has no group/category field and projection preserves pinned-first stable ordering. Grouping remains an unimplemented product choice, so closing the combined issue as fully fixed would discard its central residual request.

**Source and test evidence**

- [`src/OpenClaw.Shared/OpenClawGatewayClient.cs:1839-1843`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/OpenClawGatewayClient.cs#L1839-L1843)
- [`src/OpenClaw.Tray.WinUI/Presentation/WorkspaceProjection.cs:9-11`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/WorkspaceProjection.cs#L9-L11)
- [`src/OpenClaw.Tray.WinUI/Presentation/WorkspaceProjection.cs:136-159`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/WorkspaceProjection.cs#L136-L159)
- [`tests/OpenClaw.Shared.Tests/GatewayProtocolLiveRoundTripTests.cs:55-103`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayProtocolLiveRoundTripTests.cs#L55-L103)

**Pull requests**

- [#1768 (fix: show generated names for unlabeled conversations)](https://github.com/openclaw/openclaw-windows-node/pull/1768): merged; merge `18f33cee71aa933aecbaf0d6a91f8743a27453f5`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1677

**Missing proof / limits**

- Gateway group/category contract and ordering policy
- Maintainer decision on grouping parity

**Exact proposed comment**

> The generated-name portion is fixed by #1768 (fix: show generated names for unlabeled conversations): session-list requests now ask for derived titles, with protocol regression coverage. Keeping this open for the remaining grouping request. Companion still projects a flat pinned-first conversation list. The next step is a maintainer decision to document that design or support verified Gateway group/order fields without losing pinned, archived and background-session behavior.

### #1687: system.run.prepare rejects every gateway exec: "custom-env-not-supported" (OPENCLAW_CHANNEL_CONTEXT)

- **Classification:** bug
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 4 comments
- **Post new comment:** yes, after live reconciliation

The supplied nonempty env payload still deterministically fails custom-env-not-supported in current validation. Previously cited #1639 (feat(node): honor the typed system.run execution context) is now closed unmerged, not an active fix. A repair requires the modern typed execution-context contract and current SDK ownership; permitting arbitrary env is unsafe and not a small fix.

**Source and test evidence**

- [`src/OpenClaw.Shared/ExecApprovals/ExecApprovalV2InputValidator.cs:45-60`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/ExecApprovals/ExecApprovalV2InputValidator.cs#L45-L60)
- [`tests/OpenClaw.Shared.Tests/ExecApprovalV2InputValidationTests.cs:469-480`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/ExecApprovalV2InputValidationTests.cs#L469-L480)
- [`src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs:170-194`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs#L170-L194)

**Pull requests**

- [#1639 (feat(node): honor the typed system.run execution context)](https://github.com/openclaw/openclaw-windows-node/pull/1639): closed-unmerged.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1687

**Missing proof / limits**

- Current typed-context contract integration
- Strict non-skipping MXC E2E and pairing-transition proof

**Exact proposed comment**

> Keeping this open. The reported OPENCLAW_CHANNEL_CONTEXT environment payload still reaches custom-env-not-supported on main. #1639 (feat(node): honor the typed system.run execution context) closed without merging, so it is no longer an active fixing PR. A replacement needs to reconcile typed routing metadata with the current SDK and approval owners, preserve rejection of arbitrary environment overrides, and prove real contained Gateway-to-node execution and pairing compatibility.

### #1702: [Feature Request] Add Fish Audio TTS provider to Windows Hub voice settings

- **Classification:** enhancement
- **Disposition:** keep-open
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** no; draft is retained for the next substantive update

Fish Audio and Gateway-delegated synthesis remain absent from the five-provider dropdown/dispatch. This is an optional provider feature with a concrete language use case, not a broken existing provider. Pricing assertions are not independently verified and are not a reason to close.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Pages/VoiceSettingsPage.xaml:232-237`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/VoiceSettingsPage.xaml#L232-L237)
- [`src/OpenClaw.Tray.WinUI/Services/TextToSpeech/TextToSpeechService.cs:65-115`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/TextToSpeech/TextToSpeechService.cs#L65-L115)
- [`tests/OpenClaw.Shared.Tests/CapabilityTests.cs:3657-3660`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/CapabilityTests.cs#L3657-L3660)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1702

**Missing proof / limits**

- Synthesis-owner product decision
- Authenticated audio-delivery contract and native playback

**Exact proposed comment**

> Keeping this feature request open. The current Windows provider list still has no Fish Audio or delegated Gateway speech option. The maintainer decision is whether Windows should own another provider client or consume a verified authenticated Gateway audio-delivery contract. Preserve explicit provider selection and local/offline behavior, and validate Malay playback before treating either route as delivered.

### #1722: Windows Companion browser proxy rejects native Store Gateway; required browser bootstrap helper unavailable

- **Classification:** bug-and-support
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** yes, after live reconciliation

Current browser authorization still accepts owned SSH or managed WSL routes; the otherwise fallback is explicitly fixture-only, not native Store support. The exact refusal precedes bearer-token delivery. Human reports confirm two distinct missing pieces: trusted native browser endpoint and helper distribution. Neither is fixed by weakening ownership checks.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:2475-2514`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L2475-L2514)
- [`src/OpenClaw.Connection/FixtureLoopbackListenerOwner.cs:16-22`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/FixtureLoopbackListenerOwner.cs#L16-L22)
- [`src/OpenClaw.Shared/Capabilities/BrowserProxyCapability.cs:84-94`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Capabilities/BrowserProxyCapability.cs#L84-L94)
- [`tests/OpenClaw.Shared.Tests/CapabilityTests.cs:966-986`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/CapabilityTests.cs#L966-L986)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1722
- https://github.com/openclaw/openclaw-windows-node/issues/1722#issuecomment-6400366145
- https://github.com/openclaw/openclaw-windows-node/issues/1722#issuecomment-6471359129

**Missing proof / limits**

- Browser-specific cross-account listener attestation
- Official helper distribution and supported native/WSL/Edge matrix
- Real invocation preserving token protection

**Exact proposed comment**

> Keeping this open. Current main still lacks a production native-Store browser-listener authorization path; its non-WSL fallback is test-fixture-only. The recent reports also confirm missing native-host provisioning on the managed-WSL route. These require separate trusted-endpoint and official-helper delivery decisions, with visible setup guidance. A Gateway connection or capability approval alone must not authorize sending the shared token to an unverified browser port.

### #1756: [Windows ARM64] Native Gateway setup times out while Microsoft Store deployment continues

- **Classification:** bug-with-merged-fix-awaiting-proof
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 4 comments
- **Post new comment:** no; draft is retained for the next substantive update

The 30-minute shared budget is merged and present, but the explicit human keep-open requirement is not met. The Nov 4 production-path Store run completed in 51 seconds, so it neither crosses the old five-minute boundary nor observes elapsed wait UI. No new implementation is needed pending the requested slow deployment proof.

**Source and test evidence**

- [`src/OpenClaw.SetupEngine/NativeGatewayPackageAcquisition.cs:19-43`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/NativeGatewayPackageAcquisition.cs#L19-L43)
- [`src/OpenClaw.SetupEngine/NativeGatewayMsixInstaller.cs:39-66`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/NativeGatewayMsixInstaller.cs#L39-L66)
- [`tests/OpenClaw.SetupEngine.Tests/NativeGatewayPackageAcquisitionTests.cs:363-459`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/NativeGatewayPackageAcquisitionTests.cs#L363-L459)

**Pull requests**

- [#1750 (fix(setup): give native Gateway Store install a 30-minute budget with elapsed-minute wait status)](https://github.com/openclaw/openclaw-windows-node/pull/1750): merged; merge `64357496432f63d77bc56c687aac39fe989169e7`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1756
- https://github.com/openclaw/openclaw-windows-node/issues/1756#issuecomment-6224392915
- https://github.com/openclaw/openclaw-windows-node/issues/1756#issuecomment-6624949109

**Missing proof / limits**

- Slow cold Store deployment beyond five minutes
- Visible waiting, automatic continuation, cancellation and retry on repaired app

**Exact proposed comment**

> Keeping this open per the existing proof requirement. #1750 (fix(setup): give native Gateway Store install a 30-minute budget with elapsed-minute wait status) is merged. The November 4 real Store check is useful, but its 51-second completion would also pass the previous deadline. The remaining step is the already-requested cold Store deployment exceeding five minutes, observing elapsed waiting, verified continuation, cancellation and retry. No duplicate deadline patch is needed.

### #1764: Windows Node MXC sandbox — whoami fails with 0xC0000142

- **Classification:** bug-with-obsolete-trigger
- **Disposition:** close-obsolete
- **Recommendation confidence:** 94%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** yes, after live reconciliation

The affected reporter explicitly verified whoami exit 0 with MXC enabled after enabling the former Windows UI API compatibility setting. Merged #1726 (feat: upgrade Windows node sandboxing to MXC 1.0 .NET SDK) permanently enables that contained compatibility policy and retires the default-off setting; current MxcRequestBuilder emits Ui.Disable=false and regression tests ignore both legacy flag values while retaining containment/input restrictions. The original allowWindowsUi=false trigger no longer exists on main. This does not establish universal MXC compatibility.

**Source and test evidence**

- [`src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs:179-192`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs#L179-L192)
- [`tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs:194-222`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs#L194-L222)
- [`docs/ARCHITECTURE.md:178-179`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L178-L179)

**Pull requests**

- [#1726 (feat: upgrade Windows node sandboxing to MXC 1.0 .NET SDK)](https://github.com/openclaw/openclaw-windows-node/pull/1726): merged; merge `c6e0f6d56242f0f0243d7ea408d2f51af6fd02e8`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1764
- https://github.com/openclaw/openclaw-windows-node/issues/1764#issuecomment-6258607388
- https://github.com/openclaw/openclaw-windows-node/commit/c6e0f6d56242f0f0243d7ea408d2f51af6fd02e8

**Missing proof / limits**

- No new affected-host current-main run was performed; reporter success was on 2026.10.4-alpha.6
- Reporter did not capture effective-policy logs; closure is limited to retirement of the old disabled-UI trigger

**Exact proposed comment**

> Closing the original Win32k-denial case as obsolete on current main. The affected-host follow-up confirmed that the same whoami command succeeds with MXC still enabled when Windows UI API compatibility is enabled. #1726 (feat: upgrade Windows node sandboxing to MXC 1.0 .NET SDK) subsequently made that compatibility policy fixed and retired the old toggle; current code and regression tests keep container isolation and input-injection restrictions. This does not claim that every 0xC0000142 cause is solved. If it recurs on a build containing that change, please reopen with the exact version and sanitized effective-containment result, without disabling MXC.

### #1823: fix(chat): keep a lifecycle end that arrives before the reset echo

- **Classification:** bug
- **Disposition:** keep-open
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** no; draft is retained for the next substantive update

Current reset gate still buffers lifecycle starts but drops a matching early end/error while waiting for the echo. Existing test delivers the terminal after echo. The fork branch is a candidate, not an upstream PR. Fix must complete provider-side effects and queued-message draining as well as TurnActive, so it is not selected as a trivial patch.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Chat/ChatResetState.cs:381-404`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatResetState.cs#L381-L404)
- [`tests/OpenClaw.Tray.Tests/ChatRuntimeOwnersTests.cs:350-391`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ChatRuntimeOwnersTests.cs#L350-L391)
- [`src/OpenClaw.Tray.WinUI/Chat/ChatConversationState.cs:2087`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatConversationState.cs#L2087)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1823
- https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/f044-keep-reset-lifecycle-end?expand=1

**Missing proof / limits**

- Failing-then-passing early end/error regression through provider effects
- Queued drain, generation isolation, deferred abort and terminal deduplication

**Exact proposed comment**

> Keeping this specific reset/start/end-or-error/echo race open. Main still drops the early terminal while retaining the start, and current reset coverage uses the opposite terminal ordering. The contributor branch is the starting point for review, but acceptance must cover normal completion effects, queued-message draining, duplicate terminals and reset-generation isolation, not only clearing TurnActive. This is not proof that every stale-thinking report has the same cause.

### #1840: fix(chat): keep a second identical message the history snapshot missed

- **Classification:** bug
- **Disposition:** existing-pr
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** yes, after live reconciliation

Current non-authoritative history merge still applies the two-second content fallback to a live row with unmatched GatewayMessageId/sequence. However, direct inspection of open draft #1685 (fix: retain companion sessions and bound large history loading) shows an identity guard in this exact fallback, so the apparent one-line candidate is already overlapped by active work. Its guard covers known IDs; sequence-only preservation needs explicit acceptance.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Chat/ChatHistoryState.cs:359-407`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatHistoryState.cs#L359-L407)
- [`tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs:4678-4707`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs#L4678-L4707)

**Pull requests**

- [#1685 (fix: retain companion sessions and bound large history loading)](https://github.com/openclaw/openclaw-windows-node/pull/1685): open (draft); observed head `a79b8c4410b59faf9fedc123365da4236cb3d7e5`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1840
- https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/f046-keep-second-identical-message?expand=1
- https://github.com/openclaw/openclaw-windows-node/pull/1685/files

**Missing proof / limits**

- Known-ID and sequence-only stale-snapshot regressions on the selected candidate
- Optimistic deduplication and authoritative reload preservation

**Exact proposed comment**

> The identity-blind two-second fallback remains on main, but #1685 (fix: retain companion sessions and bound large history loading) already changes this exact merge path to preserve known-ID rows. Please include the supplied msg-1/msg-2 regression in that work rather than start a competing fix. Acceptance should also distinguish sequence-only identity, identity-free optimistic echoes and authoritative reloads; the existing five-minute-separated duplicate test does not exercise this race.

### #1871: Windows node missing computer.act — CUA diagnostic database access blocked

- **Classification:** support
- **Disposition:** keep-open
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** no; draft is retained for the next substantive update

Companion does not implement computer.act; its screen capability is capture/record only. Reporter has already supplied the exact readiness command and database path in the first follow-up, so asking again would be wasteful. A different user reports an interactive npm-node route, but that is not proof for this disabled-plugin WSL setup and includes security-sensitive setup changes the reporter has not approved.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:308-405`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/NodeService.cs#L308-L405)
- [`src/OpenClaw.Shared/Capabilities/ScreenCapability.cs:14-18`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Capabilities/ScreenCapability.cs#L14-L18)
- [`tests/OpenClaw.Shared.Tests/CapabilityTests.cs:2646`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/CapabilityTests.cs#L2646)
- [`src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs:454-511`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs#L454-L511)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1871
- https://github.com/openclaw/openclaw-windows-node/issues/1871#issuecomment-6392202729
- https://github.com/openclaw/openclaw-windows-node/issues/1871#issuecomment-6672446569

**Missing proof / limits**

- Exact plugin version and supported component ownership
- Owner-reviewed sanitized diagnostics
- Reporter-specific interactive-node and native-host readiness

**Exact proposed comment**

> Keeping this open for supported, consent-preserving diagnosis. The readiness command and database path are already supplied in your follow-up; they do not need to be repeated. Companion exposes capture/record capabilities, not computer.act, and the other user’s separate interactive-node success does not establish a fix for your WSL setup. The owner can open Companion’s Diagnostics bundle preview, review/redact it before sharing, and include the still-unverified plugin version. Maintain the existing requirement for approval before enabling desktop control, changing permissions or installing another node. Do not treat the SQLite access error as corruption.

### #1899: fix(tests): fixture UI checks fail on missing placement RPC and intermittent wide-window visibility

- **Classification:** test-infrastructure-tracker
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** no; draft is retained for the next substantive update

The placement fixture omission is repaired in merged #1905 (chore(ci): run mock Gateway UI scenarios in pull requests) and current GatewayScenario implements projects.list/environments.list with a real-client regression. The author explicitly retains the separate intermittent wide-window visibility investigation. Passing one fixture run or changing SDK does not establish its mechanism fixed.

**Source and test evidence**

- [`tests/OpenClaw.TestSupport/Gateway/GatewayScenario.cs:392-405`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.TestSupport/Gateway/GatewayScenario.cs#L392-L405)
- [`tests/OpenClaw.Shared.Tests/GatewayFixtureProtocolTests.cs:60-84`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayFixtureProtocolTests.cs#L60-L84)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs:38-48`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs#L38-L48)

**Pull requests**

- [#1905 (chore(ci): run mock Gateway UI scenarios in pull requests)](https://github.com/openclaw/openclaw-windows-node/pull/1905): merged; merge `1912df7e2f976314d82baa88a5decf31218b33a1`.
- [#2083 (fix(chat): switching conversations can land on old messages or freeze the chat)](https://github.com/openclaw/openclaw-windows-node/pull/2083): open (draft); observed head `374d67bae08e54dd656eb05e74dbdf03e6e307b8`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1899
- https://github.com/openclaw/openclaw-windows-node/issues/1899#issuecomment-6473124185

**Missing proof / limits**

- Current failing wide-window predicate diagnosis
- Repeated uninstrumented current-head Debug/Release acceptance

**Exact proposed comment**

> The placement-catalog part is complete in #1905 (chore(ci): run mock Gateway UI scenarios in pull requests), and current source has projects.list/environments.list plus a real-client regression that preserves strict write/unknown-method rejection. Keeping this issue for the remaining intermittent wide-window visibility failure, as the follow-up requested. Capture the exact failed UIA identity/offscreen/bounds predicate and retain natural-tail and final-marker checks; do not hide it with sleeps or weaker assertions. Related navigation work in #2083 (fix(chat): switching conversations can land on old messages or freeze the chat) is not automatic closure evidence.

### #1937: Local AI: first message after launch re-processes the whole system prompt (5-16 s)

- **Classification:** performance-enhancement
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** yes, after live reconciliation

Main still pins b11320 and has no persistent prefix save/restore owner. Draft #1938 (improve(local-ai): reuse the saved system prompt after a Local AI restart) explicitly closes this issue and implements runtime upgrade, bounded cache and proxy integration. It contains a material unresolved startup-restore tradeoff; do not duplicate the cache or treat author timing claims as independently verified.

**Source and test evidence**

- [`src/OpenClaw.Shared/Inference/Catalog/LlamaRuntimeCatalog.cs:68-73`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Inference/Catalog/LlamaRuntimeCatalog.cs#L68-L73)
- [`src/OpenClaw.Connection/LocalAi/LlamaServerRuntimeService.cs:469-480`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/LocalAi/LlamaServerRuntimeService.cs#L469-L480)
- [`tests/OpenClaw.Connection.Tests/LocalAiRecipeCacheTests.cs:9-24`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/LocalAiRecipeCacheTests.cs#L9-L24)

**Pull requests**

- [#1938 (improve(local-ai): reuse the saved system prompt after a Local AI restart)](https://github.com/openclaw/openclaw-windows-node/pull/1938): open (draft); observed head `c41c57253ee81d2bf65261a47249fed4c170a012`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1937

**Missing proof / limits**

- Startup-restore default/product decision
- Exact-head real-model/Gateway reuse and mismatch behavior
- Cache privacy, retention and disk-bound validation

**Exact proposed comment**

> Tracking this through #1938 (improve(local-ai): reuse the saved system prompt after a Local AI restart); no parallel implementation is needed. Main still lacks persistent prefix reuse. The draft’s startup-restore policy is an explicit open decision because immediate first requests can be slower, and acceptance must cover exact prefix/model/runtime identity, bounded private storage, mismatch fallback and measured end-to-end Gateway latency.

### #1945: [Bug] Screen reader hits unnamed, icon-only and type-named controls in Settings, Bindings, Config, Sessions and Usage

- **Classification:** bug-with-partial-fix
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 3 comments
- **Post new comment:** yes, after live reconciliation

#1971 (test: establish Windows accessibility baseline) is now merged, not pending; current Config Save/Remove and Sessions list have names and Usage has native-row coverage. Bindings is removed. The remaining group-announcement/NVDA contract is not established by that PR, whose body explicitly leaves speech and actual Usage recycling unverified. Current notification SettingsCard and named Clipboard Expander require real accessibility evaluation, not an assumed missing peer.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Pages/ConfigPage.xaml:379-385`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ConfigPage.xaml#L379-L385)
- [`src/OpenClaw.Tray.WinUI/Controls/SchemaConfigEditor.xaml.cs:618`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Controls/SchemaConfigEditor.xaml.cs#L618)
- [`src/OpenClaw.Tray.WinUI/Pages/SessionsPage.xaml:110-111`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/SessionsPage.xaml#L110-L111)
- [`src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml:181-233`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml#L181-L233)
- [`src/OpenClaw.Tray.WinUI/Pages/SandboxPage.xaml:329-343`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/SandboxPage.xaml#L329-L343)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureAccessibilityBaselineTests.cs:301-322`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureAccessibilityBaselineTests.cs#L301-L322)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureAccessibilityBaselineTests.cs:570-576`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureAccessibilityBaselineTests.cs#L570-L576)

**Pull requests**

- [#1971 (test: establish Windows accessibility baseline)](https://github.com/openclaw/openclaw-windows-node/pull/1971): merged; merge `0618782678fa1ce1aeac19b8834fdf6a28951739`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1945
- https://github.com/openclaw/openclaw-windows-node/issues/1945#issuecomment-6622592314

**Missing proof / limits**

- NVDA notification/Clipboard group context
- Actual Usage row scrolling and container recycling

**Exact proposed comment**

> The naming work is now merged in #1971 (test: establish Windows accessibility baseline), and Bindings no longer exists. Keeping this open for the remaining option-group/screen-reader acceptance rather than closing step 5 with the naming fix. Current General notification cards and the named Clipboard expander need an NVDA group-navigation check, along with Usage scrolling/re-realization. Add a custom grouping peer only if that native test demonstrates a remaining gap, and preserve child reachability.

### #1956: Turning off Start with Windows does not stop the startup shortcut the installer created

- **Classification:** bug
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** yes, after live reconciliation

Installer still creates the Startup shortcut and disables only Run/scheduled-task paths in main. Open #1964 (fix(setup): Start with Windows cannot stop the installer startup shortcut) directly owns the fix, has substantial migration and native proof discussion, and explicitly remains not merge-ready. Do not duplicate installer ownership changes.

**Source and test evidence**

- [`installer.iss:105`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/installer.iss#L105)
- [`installer.iss:145`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/installer.iss#L145)
- [`src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs:194-224`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs#L194-L224)
- [`tests/OpenClaw.Tray.Tests/InstallerIssAssertionTests.cs:63`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/InstallerIssAssertionTests.cs#L63)

**Pull requests**

- [#1964 (fix(setup): Start with Windows cannot stop the installer startup shortcut)](https://github.com/openclaw/openclaw-windows-node/pull/1964): open; observed head `0700f09f739e6744941e6afc71041cce1adbdf78`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1956

**Missing proof / limits**

- Candidate unresolved migration cases and exact-head validation
- Signed installer/upgrade and remaining logon scenarios

**Exact proposed comment**

> Tracking this through #1964 (fix(setup): Start with Windows cannot stop the installer startup shortcut). Main still has the installer shortcut that the disable paths do not own. The PR already addresses registration consolidation and legacy migration; remaining acceptance must preserve disabled/customized registrations, Dev/release separation, signed upgrade behavior and real logon outcomes. This issue should stay open until that candidate is accepted and merged.

### #1983: fix: cancel a stalled portable update download

- **Classification:** bug
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** no; draft is retained for the next substantive update

Main Updatum backend still downloads without cancellation and closes/installs sequentially. Open #1994 (fix: cancel a stalled portable update download) owns the reported repair. Candidate window-close evidence is not equivalent to stalled-body deadline proof; its current head differs from the documented live demonstration.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Services/UpdatumUpdateBackend.cs:22-34`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/UpdatumUpdateBackend.cs#L22-L34)
- [`src/OpenClaw.Tray.WinUI/Dialogs/DownloadProgressDialog.cs:35`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Dialogs/DownloadProgressDialog.cs#L35)
- [`tests/OpenClaw.Tray.Tests/UpdateBackendLoaderTests.cs:62-81`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/UpdateBackendLoaderTests.cs#L62-L81)

**Pull requests**

- [#1994 (fix: cancel a stalled portable update download)](https://github.com/openclaw/openclaw-windows-node/pull/1994): open; observed head `3658305468dc0e1fe4941d08dec1e029d06d9ad5`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/1983
- https://github.com/openclaw/openclaw-windows-node/issues/1983#issuecomment-6616607132

**Missing proof / limits**

- Current-head stalled-body deadline and cancellation
- Startup recovery and successful-install control

**Exact proposed comment**

> Tracking this through #1994 (fix: cancel a stalled portable update download). Current main still performs a tokenless download. Before resolving, the candidate needs a body-stall test after response headers, both deadline and user-close cancellation with no installation, ordinary startup continuation, and a successful-download control that is not invalidated by programmatic progress-window closure. The existing window-close demonstration does not prove the deadline case.

### #2004: Tray gateway menu and Local AI page show the raw gateway URL

- **Classification:** bug-privacy
- **Disposition:** simple-fix
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** yes, after live reconciliation

Both reported presentation paths still expose raw saved URL details; the existing sanitizer removes userinfo/query/fragment without mutating connection data. No corresponding PR is in the complete 93-open-PR snapshot. The author supplied a fork branch; reuse/review that narrow contribution rather than recreate it without credit.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Presentation/TrayMenuPresenter.cs:174-180`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/TrayMenuPresenter.cs#L174-L180)
- [`src/OpenClaw.Tray.WinUI/Presentation/LocalAiPageViewModel.cs:154`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/LocalAiPageViewModel.cs#L154)
- [`src/OpenClaw.Shared/GatewayUrlHelper.cs:84`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/GatewayUrlHelper.cs#L84)
- [`tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs:134-146`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs#L134-L146)
- [`tests/OpenClaw.Tray.Tests/Services/TrayMenuPresenterTests.cs:163-175`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/Services/TrayMenuPresenterTests.cs#L163-L175)
- [`tests/OpenClaw.Tray.Tests/Presentation/LocalAiPageViewModelTests.cs:11`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/Presentation/LocalAiPageViewModelTests.cs#L11)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2004
- https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/2004-tray-local-ai-url?expand=1
- https://github.com/openclaw/openclaw-windows-node/issues/2004#issuecomment-6642815675

**Missing proof / limits**

- Fail-before/pass-after tests at both presentation consumers
- Current-head Windows visible-result proof
- Required build, Shared and Tray suites

**Bounded fix plan**

- Change: Use GatewayUrlHelper.SanitizeForDisplay for TrayMenuPresenter.BuildGatewayFlyout Detail and LocalAiPageViewModel.GatewayDetail URL fallback. Preserve null/blank handling deliberately and nonblank friendly names; never rewrite stored GatewayUrl.
- Tests: Extend Services/TrayMenuPresenterTests and Presentation/LocalAiPageViewModelTests with secret-bearing saved URLs, friendly-name/null cases and unchanged underlying snapshot. Existing GatewayUrlHelperTests already exercise helper behavior, but consumer tests are required.
- Validation: ./build.ps1; Shared and Tray tests with real nonzero inventories; focused presenter/view-model tests; windows-winui-interactive visible proof and security-aware review.
- Existing contributor: @SebTardif; candidate `56cb7282bfe9f34623020fceb85bc4bf85c60480`. Not executed or accepted by this review.

**Exact proposed comment**

> Confirmed on current main: the tray flyout uses Uri.ToString(), and Local AI falls back to the unsanitized saved URL when the friendly name is absent. This is a bounded fix using GatewayUrlHelper.SanitizeForDisplay at those two presentation boundaries, preserving the actual saved connection URL and friendly-name precedence. The existing contributor branch is the starting point. Add presenter/view-model regressions for userinfo, query and fragment removal, then verify the visible strings on Windows before closing.

### #2008: Copied support text includes tool commands it says are excluded

- **Classification:** bug-privacy
- **Disposition:** simple-fix
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** yes, after live reconciliation

Current GatewayService still records activity.Label in recent-activity Title, while gateway tool labels contain ordinary commands/paths/queries and BuildSupportContext emits Title. Final clipboard sanitization exists, so recognized secrets/URL queries are narrower than the original claim, but ordinary argument content survives. Existing tests only assert exclusion wording. No matching upstream open PR found; contributor branch exists.

**Source and test evidence**

- [`src/OpenClaw.Shared/OpenClawGatewayClient.cs:4388-4448`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/OpenClawGatewayClient.cs#L4388-L4448)
- [`src/OpenClaw.Shared/Models.cs:69-76`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Models.cs#L69-L76)
- [`src/OpenClaw.Tray.WinUI/Services/GatewayService.cs:282-289`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/GatewayService.cs#L282-L289)
- [`src/OpenClaw.Tray.WinUI/Helpers/CommandCenterTextHelper.cs:64-77`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Helpers/CommandCenterTextHelper.cs#L64-L77)
- [`src/OpenClaw.Tray.WinUI/Services/DiagnosticsExportSanitizer.cs:32`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/DiagnosticsExportSanitizer.cs#L32)
- [`tests/OpenClaw.Tray.Tests/DiagnosticsPageContractTests.cs:812-819`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/DiagnosticsPageContractTests.cs#L812-L819)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2008
- https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/2008-support-activity-title?expand=1
- https://github.com/openclaw/openclaw-windows-node/issues/2008#issuecomment-6642817451

**Missing proof / limits**

- Behavioral tool-event to support-export regression
- Current-head clipboard preview/copy proof with synthetic data
- Required build, Shared and Tray suites plus privacy review

**Bounded fix plan**

- Change: At GatewayService.OnActivityChanged recent-activity recording, project tool events from existing AgentActivity.ToolName and State only instead of Label; preserve intentional non-tool activity behavior. Do not weaken exclusion wording or rely on generic secret regexes.
- Tests: Drive a synthetic tool event containing command/path/query/url arguments through recent activity, BuildSupportContext and DiagnosticsExportSanitizer. Assert argument absence, retained tool/phase usefulness, and unchanged non-tool behavior. Test result/idle phases too, since Kind becomes Idle while ToolName/State remain populated.
- Validation: ./build.ps1; Shared and Tray suites; focused diagnostics/GatewayService tests; synthetic current-head Windows export/clipboard check and privacy review.
- Existing contributor: @SebTardif; candidate `d5af10df45f9522bed058382232556feac987c64`. Not executed or accepted by this review.

**Exact proposed comment**

> Confirmed on current main: ordinary tool arguments can still enter copied recent-activity support text despite its exclusion statement. The final clipboard sanitizer already removes recognized secrets and URL queries, so the remaining regression should use an ordinary synthetic argument such as invoice-483. Review the existing contributor branch to record tool name and phase instead of argument-derived activity.Label, and verify both BuildSupportContext and final export output. Keep other live-UI activity behavior separate from this support-export fix.

### #2015: Failed exec-policy save can miss the permissions page

- **Classification:** defensive-threading-concern
- **Disposition:** keep-open
- **Recommendation confidence:** 96%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** no; draft is retained for the next substantive update

Success dispatches through HandleOnUiThread but failure still publishes directly. The asserted production off-thread continuation is not established: entry points are UI events and awaits capture context. The existing failure test proves saved-policy presentation, not a real continuation escape. A consistency patch may be worthwhile, but do not represent it as a reproduced production failure or skip the requested evidence.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Presentation/PermissionsPageViewModel.cs:596-637`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/PermissionsPageViewModel.cs#L596-L637)
- [`tests/OpenClaw.Tray.Tests/Presentation/PermissionsPageViewModelTests.cs:614-627`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/Presentation/PermissionsPageViewModelTests.cs#L614-L627)

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2015
- https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/f079-exec-approvals-ui-thread

**Missing proof / limits**

- Actual asynchronous notification/thread-affinity contract or mounted failure reproduction
- Failing regression distinguishing defensive dispatch from production trigger

**Exact proposed comment**

> The dispatch inconsistency remains: success marshals through HandleOnUiThread and failure does not. Keeping this open without claiming a reproduced off-thread UI failure, because normal UI callers capture their synchronization context. The next step is an asynchronously failing store test against the mounted notification contract, recording thread access and preserving the previously persisted policy. The supplied held-dispatcher test is useful for a defensive contract but is not by itself a shipped reproduction.

### #2032: Cron Remove deletes on the first click and Run Now starts a disabled job

- **Classification:** bug-and-ux-proposal
- **Disposition:** existing-pr
- **Recommendation confidence:** 98%
- **Discussion reviewed:** body and all 2 comments
- **Post new comment:** yes, after live reconciliation

Main removes immediately and remembers IDs before acknowledgement; rejection can suppress a later natural completion. Open fork PR #2 (fix: ask before removing a cron job or running a disabled one) owns the supplied fix; its current body still checks a Boolean send result, which is not proof of Gateway acceptance. Disabled Run Now is an intentional existing contract, so confirmation is a product decision. Upstream #1893 (feat(automations): native Automations page with web parity) replaces this owner and #2082 (fix: keep older Run Now attempts from clearing newer runs) changes adjacent run tracking but explicitly excludes 2032.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs:332-342`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs#L332-L342)
- [`src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs:207-234`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs#L207-L234)
- [`tests/OpenClaw.Tray.Tests/CronPageContractTests.cs:35`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/CronPageContractTests.cs#L35)

**Pull requests**

- [#1893 (feat(automations): native Automations page with web parity)](https://github.com/openclaw/openclaw-windows-node/pull/1893): open (draft); observed head `d11898a373bece41ced81b6f476abb8f860adf20`.
- [#2082 (fix: keep older Run Now attempts from clearing newer runs)](https://github.com/openclaw/openclaw-windows-node/pull/2082): open; observed head `079b18dce43b8fd07d4fffc52913d194a3b84c7c`.
- [fork #2 (fix: ask before removing a cron job or running a disabled one)](https://github.com/SebTardif/openclaw-windows-node/pull/2): open; observed head `2c633fc48e1093f524c77bc866906acfbbc084c1`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2032

**Missing proof / limits**

- Response-aware rejected-removal regression
- Confirmation policy and native dialog behavior
- Integration with pending Automations owner

**Exact proposed comment**

> The rejected-remove notification defect remains on main. The existing fork PR #2 (fix: ask before removing a cron job or running a disabled one) is at https://github.com/SebTardif/openclaw-windows-node/pull/2; review it rather than duplicate the contribution. Acceptance must distinguish server rejection from successful socket submission before clearing or retaining the removal marker, and prove later one-shot completion still notifies. Confirm the disabled-job UX policy while preserving deliberate manual runs. #2082 (fix: keep older Run Now attempts from clearing newer runs) explicitly does not fix this separate removal behavior; coordinate with #1893 (feat(automations): native Automations page with web parity).

### #2038: A late workspace file can be shown for the wrong agent

- **Classification:** bug
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** no; draft is retained for the next substantive update

Legacy body publication remains unscoped while file lists check agent identity. An old same-name response can populate current rows. Verified open fork PR #6 (fix: ignore a workspace file body from another agent) is an existing contribution, but its reported helper proof does not establish original-response correlation when both agents have issued a same-name request.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Pages/WorkspacePage.xaml.cs:200-218`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/WorkspacePage.xaml.cs#L200-L218)
- [`src/OpenClaw.Tray.WinUI/Pages/WorkspacePage.xaml.cs:650-679`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/WorkspacePage.xaml.cs#L650-L679)
- [`tests/OpenClaw.Tray.Tests/WorkspaceFilesModelTests.cs:235-247`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/WorkspaceFilesModelTests.cs#L235-L247)

**Pull requests**

- [fork #6 (fix: ignore a workspace file body from another agent)](https://github.com/SebTardif/openclaw-windows-node/pull/6): open; observed head `5d42c2cfd5d59dce6fb1605a5a912c8e2f7714af`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2038

**Missing proof / limits**

- Original-response identity correlation
- Delayed A/B same-name production-client regression and native preview proof

**Exact proposed comment**

> Keeping this open and routing review to the existing fork PR #6 (fix: ignore a workspace file body from another agent), https://github.com/SebTardif/openclaw-windows-node/pull/6. Main still applies the legacy body by filename without original agent/request ownership. The required regression is A requesting SOUL.md, switching to B and requesting B’s SOUL.md, then A’s delayed response arriving. Bind the arriving response to its original agent, request and load generation; comparing only the latest request to the current page is insufficient.

### #2042: Cancel does not stop a Whisper or Piper download, and the body has no size cap

- **Classification:** bug-security-resource-exhaustion
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** no; draft is retained for the next substantive update

SingleFlightDownload still gives the shared transfer CancellationToken.None; caller cancellation stops only its wait. Whisper/Piper body loops write until EOF before hashing and do not cap bytes. Open fork PR #10 (fix: stop a Whisper or Piper download that is canceled or too large) owns the candidate, so no duplicate. Shared helper also serves other downloads and requires race/deadline/cap integration tests, not only helper-level checks.

**Source and test evidence**

- [`src/OpenClaw.Shared/Audio/SingleFlightDownload.cs:17-50`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Audio/SingleFlightDownload.cs#L17-L50)
- [`src/OpenClaw.Shared/Audio/WhisperModelManager.cs:124-155`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Audio/WhisperModelManager.cs#L124-L155)
- [`src/OpenClaw.Shared/Audio/PiperVoiceManager.cs:195-215`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Audio/PiperVoiceManager.cs#L195-L215)
- [`tests/OpenClaw.Shared.Tests/SingleFlightDownloadTests.cs:55-68`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/SingleFlightDownloadTests.cs#L55-L68)

**Pull requests**

- [fork #10 (fix: stop a Whisper or Piper download that is canceled or too large)](https://github.com/SebTardif/openclaw-windows-node/pull/10): open; observed head `74b20b3e586c09396c46918acae1df226e5b8cb0`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2042

**Missing proof / limits**

- Production-manager capped/stalled body regressions and cleanup
- Verified pinned asset sizes
- Last/remaining waiter, retry and Kokoro compatibility
- Current-head voice download cancellation UI

**Exact proposed comment**

> Keeping this open and reviewing the existing fork PR #10 (fix: stop a Whisper or Piper download that is canceled or too large), https://github.com/SebTardif/openclaw-windows-node/pull/10. Main still has uncancelable shared transfers and unbounded body writes before hashing. Validate authoritative asset-size limits and production-manager cleanup, last-waiter cancellation, remaining-waiter continuation, body deadlines and immediate retry races, including Kokoro/shared-helper callers. The supplied helper checks do not yet establish the complete download behavior.

### #2072: Make NodeService MCP server start and stop atomic

- **Classification:** bug-security-lifecycle
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** yes, after live reconciliation

Main still publishes/clears MCP listeners without a shared lifecycle lock while enabled intent uses a different lock. Open #2078 (fix(node): local MCP server can stay running or vanish when toggled during a restart) explicitly closes this issue and reports gated real-NodeService authenticated HTTP regressions. Its existence is not merged resolution; preserve security review and current-head gates.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:794-859`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/NodeService.cs#L794-L859)
- [`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:891-917`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/NodeService.cs#L891-L917)
- [`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:932-961`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/NodeService.cs#L932-L961)
- [`tests/OpenClaw.Tray.Tests/AppRefactorContractTests.cs:737-746`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/AppRefactorContractTests.cs#L737-L746)

**Pull requests**

- [#2078 (fix(node): local MCP server can stay running or vanish when toggled during a restart)](https://github.com/openclaw/openclaw-windows-node/pull/2078): open; observed head `b74a343ee694df5f01ba2e16709f38555833c434`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2072
- https://github.com/openclaw/openclaw-windows-node/issues/2071

**Missing proof / limits**

- Accepted/merged current-head candidate
- Verified current-head lifecycle, disable/re-enable and required validation gates

**Exact proposed comment**

> Tracking this audit lane through #2078 (fix(node): local MCP server can stay running or vanish when toggled during a restart). Main still has the competing-start and disable-during-start ownership race. Keep the issue open until the candidate is accepted and merged, retaining authenticated invocation after disable, same-client re-enable, request draining and lock-order coverage. A passing single-server transport test does not prove NodeService lifecycle ownership.

### #2076: Give the cron Run Now safety timer a per-run token

- **Classification:** bug
- **Disposition:** existing-pr
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** yes, after live reconciliation

Main safety continuation reads a HashSet off-thread and clears by jobId without per-run identity, so A timer can clear B. Open #2082 (fix: keep older Run Now attempts from clearing newer runs) closes this issue, adds token/run-ID ownership and preserves adjacent 2032 scope. It still discloses missing full native A/B sequence proof; #1893 (feat(automations): native Automations page with web parity) also replaces the page.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs:236-254`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs#L236-L254)
- [`src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs:312-316`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/CronPage.xaml.cs#L312-L316)
- [`tests/OpenClaw.Tray.Tests/CronRunningLatchTests.cs:5-44`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/CronRunningLatchTests.cs#L5-L44)

**Pull requests**

- [#2082 (fix: keep older Run Now attempts from clearing newer runs)](https://github.com/openclaw/openclaw-windows-node/pull/2082): open; observed head `079b18dce43b8fd07d4fffc52913d194a3b84c7c`.
- [#1893 (feat(automations): native Automations page with web parity)](https://github.com/openclaw/openclaw-windows-node/pull/1893): open (draft); observed head `d11898a373bece41ced81b6f476abb8f860adf20`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2076
- https://github.com/openclaw/openclaw-windows-node/issues/2071

**Missing proof / limits**

- Native A-completes/B-starts/stale-A-timeout-or-history/B-completes scenario
- Accepted/merged exact-head candidate

**Exact proposed comment**

> Tracking this through #2082 (fix: keep older Run Now attempts from clearing newer runs). The source race remains on main: an older job-only timer can clear a newer run. The candidate owns per-run tokens and exact RunId history matching; keep all mutable run-state reads inside the dispatched callback. Before closure, retain a deterministic stale-A/new-B regression and complete the native sequence beyond rejection recovery, coordinating with #1893 (feat(automations): native Automations page with web parity).

### #2098: Tray/Hub 2026.11.1: intermittent hard crashes (stowed exception 0xC000027B in CoreMessagingXP.dll / Microsoft.UI.Xaml.dll) - 3 dumps in 5 days

- **Classification:** bug-crash
- **Disposition:** keep-open
- **Recommendation confidence:** 99%
- **Discussion reviewed:** body and all 1 comments
- **Post new comment:** no; draft is retained for the next substantive update

Three recent stable-client hard crashes have concrete version/module metadata but no symbolized payload or shared root cause. Main already logs/handles managed WinUI exceptions; adding blanket catches is not a fix. The matching generic 0xC000027B code does not establish duplicate status with tray/menu/history reports. Candidate #2091 (fix: avoid native resource lookups during background session updates) addresses another resource-lookup stack and is not linked by a matching dump here.

**Source and test evidence**

- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:405-426`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L405-L426)
- [`src/OpenClaw.Tray.WinUI/Services/AppCrashLogger.cs:16-45`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/AppCrashLogger.cs#L16-L45)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs:16-48`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs#L16-L48)

**Pull requests**

- [#2091 (fix: avoid native resource lookups during background session updates)](https://github.com/openclaw/openclaw-windows-node/pull/2091): open; observed head `5fbea102f36aef4ffb5c9ab66328be1c811e1a2e`.

**Discussion/contribution evidence**

- https://github.com/openclaw/openclaw-windows-node/issues/2098
- https://github.com/openclaw/openclaw-windows-node/issues/1567
- https://github.com/openclaw/openclaw-windows-node/issues/2098#issuecomment-6757651591

**Missing proof / limits**

- Symbolized stowed payloads and matching crash logs
- Current failing owner and reproducible/native acceptance case
- Proof tying any candidate to these exact crashes

**Exact proposed comment**

> Keeping this recent crash report open and separate from #1567 and the history-hang reports until symbolized evidence establishes a shared cause. Managed exception logging already exists, so another blanket catch is not a diagnosed repair. The existing request for stowed-exception payloads, matching module/binary identity and redacted stacks from the preserved dumps is the next step; no additional duplicate request is needed. #2091 (fix: avoid native resource lookups during background session updates) is only a possible comparison, not a verified fix for these dumps. Full dumps should not be posted publicly.
