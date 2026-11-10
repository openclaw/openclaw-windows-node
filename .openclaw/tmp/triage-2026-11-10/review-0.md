# Windows issue review: batch 0 of 4

Reviewed all **31 assigned issue bodies and 95 comments**, from the parent snapshot of 124 open issues and 93 open PRs. Source: e59374c064f43d68f5c72d070d4c89ef2d20ec3b; GitHub main rechecked at the same SHA. No source changes or GitHub mutations.

## Summary

- 2 close-fixed: **#1330, #1843**. No age-based or unreproduced closures.
- 2 small fix candidates: **#2001** (strongest; reuse credited fork commit) and **#2087** (narrow archive identity propagation; compatibility proof required).
- 10 existing-PR dispositions; 17 keep-open.
- No new needs-info comment recommended. Existing unanswered asks are retained instead of repeated.
- Recommendation confidence is not merge readiness.

## Material updates since older issue reviews

- Main already includes #2100 (fix(chat): completed replies stay busy after delayed updates); #1462 retains unmatched event sequence and /stop semantics.
- Main includes #1744 (feat: change the Local AI model in settings and choose the Gateway default model); #1725 retains live-switch and uncertain-recovery gaps.
- Main removed synchronous ItemsView bring via #2093 (improve(chat): align native conversation and tool activity with web); #1568 still has overlapping open PRs and incomplete acceptance.
- Published stable v2026.11.1 contains the Node 24.22.1 fix and x64 installer.
- Existing SebTardif fork PR #3 (fix: keep keystrokes when a voice transcript is appended), #7 (fix: reject a concatenated dotnet command in proof pools), and #11 (fix: copy the diagnostics device id from the active gateway) own #2033/#2039/#2045; preserve contribution and do not duplicate.

## Validation and limits

node --test .github/scripts/repository-triage.test.cjs .github/extensions/openclaw-triage-dashboard/triage-state.test.mjs: **71 passed, 0 failed, 0 skipped**.

No Windows build, .NET suite, native UI, real Gateway, MXC or hardware test ran in this read-only review. Historical PR evidence is attributed, not claimed as execution here. PR mappings are ownership/scope checks, not complete landing reviews. No external comments were posted.

## Issue decisions

| Issue | Classification | Disposition | Confidence |
|---|---|---|---:|
| [#246](https://github.com/openclaw/openclaw-windows-node/issues/246) Decide Windows packaging and auto-update strategy | enhancement / packaging-policy tracker | keep-open | 97% |
| [#1173](https://github.com/openclaw/openclaw-windows-node/issues/1173) [Bug] Windows Hub tray (OpenClaw.Tray.WinUI.exe) crashes repeatedly with access violation in Microsoft.UI.Reactor.Core during UI mount | bug / native rendering crash | keep-open | 98% |
| [#1330](https://github.com/openclaw/openclaw-windows-node/issues/1330) Chat pane truncates bulleted list lines mid-word, no wrap, no ellipsis | bug / Markdown list layout | close-fixed | 97% |
| [#1428](https://github.com/openclaw/openclaw-windows-node/issues/1428) Feature: Support RFC 6750 / Can`t use openclaw windows via gateway that is behind an oauth proxy | enhancement / proxy authentication | existing-pr | 99% |
| [#1462](https://github.com/openclaw/openclaw-windows-node/issues/1462) [Bug]: Chat stays on “Assistant is thinking” after session completes; /stop is queued | bug / chat run lifecycle and stop handling | keep-open | 96% |
| [#1542](https://github.com/openclaw/openclaw-windows-node/issues/1542) Windows Companion v2026.9.4: Allow Windows UI APIs reverts to false after confirmation/relaunch | bug / retired setting persistence with execution symptom | keep-open | 98% |
| [#1568](https://github.com/openclaw/openclaw-windows-node/issues/1568) Chat tail-scroll controller spins the UI thread at 100% CPU on layout changes | bug / native chat hang | existing-pr | 99% |
| [#1574](https://github.com/openclaw/openclaw-windows-node/issues/1574) Reconnect retry loop invokes reconnect authorization with no Windows session-ending check | bug / shutdown reconnect admission | keep-open | 99% |
| [#1642](https://github.com/openclaw/openclaw-windows-node/issues/1642) `OPENCLAW_UPDATE_CHANNEL=alpha` is shown in Settings but ignored by the updater; alpha installs can never discover newer alphas | bug / alpha update discovery | existing-pr | 99% |
| [#1678](https://github.com/openclaw/openclaw-windows-node/issues/1678) Feature request : Openclaw local Windows Node need to start gateway automatically when i launch it | enhancement / Gateway lifetime policy | keep-open | 99% |
| [#1689](https://github.com/openclaw/openclaw-windows-node/issues/1689) [Bug] Hub 2026.9.8-1: premature WebSocket closures and lost system.run.prepare result with WSL Gateway | bug / intermittent transport and result delivery | keep-open | 98% |
| [#1704](https://github.com/openclaw/openclaw-windows-node/issues/1704) Companion app: “gateway connection check failed” with token_mismatch on a local gateway running under another Windows user | bug report / cross-account token and setup-code failure | keep-open | 96% |
| [#1725](https://github.com/openclaw/openclaw-windows-node/issues/1725) Can't change local AI model for ollama | bug / model-change UX and uncertain recovery | keep-open | 97% |
| [#1758](https://github.com/openclaw/openclaw-windows-node/issues/1758) [Windows ARM64] First-run native Gateway and Local AI setup take about 44 minutes | bug report / setup performance and progress UX | keep-open | 99% |
| [#1779](https://github.com/openclaw/openclaw-windows-node/issues/1779) [Bug]: Native chat vs 2026.10.5 gateway: Enter/Ctrl+Enter dead, reply never renders, app freezes, orphaned ssh.exe holds tunnel port 18789 | bug / multi-symptom native chat and SSH lifecycle | keep-open | 99% |
| [#1826](https://github.com/openclaw/openclaw-windows-node/issues/1826) Sandboxed Python loopback IPC fails with SO_ERROR 10013 on Companion 2026.9.8-1 | bug / sandbox internal loopback compatibility inquiry | keep-open | 99% |
| [#1843](https://github.com/openclaw/openclaw-windows-node/issues/1843) [Bug] Companion v2026.10.5-alpha.4 WSL setup fails: pinned Node 24.19.0 rejected by install-cli (requires 24.22.1+) | bug / obsolete released Node pin | close-fixed | 99% |
| [#1881](https://github.com/openclaw/openclaw-windows-node/issues/1881) Local AI upgrade can strand old model receipts; migrate known IDs or offer confirmed recovery | bug / persisted Local AI upgrade compatibility | existing-pr | 99% |
| [#1901](https://github.com/openclaw/openclaw-windows-node/issues/1901) Windows Companion 2026.9.8-1 stuck on Loading messages / pairing required with reachable 2026.10.4 Gateway | bug report / pairing and loading state | keep-open | 97% |
| [#1942](https://github.com/openclaw/openclaw-windows-node/issues/1942) Local AI picker hides Nemotron 3.5 Bolt on 64 GB RTX Spark without explanation | bug / unavailable-model explanation plus feature qualification request | keep-open | 98% |
| [#1946](https://github.com/openclaw/openclaw-windows-node/issues/1946) [Bug] Screen reader is not told when saving, copying or a connection action finishes | bug / accessibility action-result announcements | keep-open | 99% |
| [#1957](https://github.com/openclaw/openclaw-windows-node/issues/1957) Autostart turned on during setup opens the Companion window at logon | bug / startup registration inconsistency | existing-pr | 99% |
| [#2001](https://github.com/openclaw/openclaw-windows-node/issues/2001) Connection Status lists the saved gateway URL with its query and userinfo | bug / credential-safe diagnostics display | simple-fix | 99% |
| [#2005](https://github.com/openclaw/openclaw-windows-node/issues/2005) Config objects without a property list are edited as raw JSON | bug / nested config secret handling | existing-pr | 99% |
| [#2009](https://github.com/openclaw/openclaw-windows-node/issues/2009) MSIX storage validation leaves the sideload certificate in TrustedPeople | bug / validation trust cleanup | keep-open | 99% |
| [#2020](https://github.com/openclaw/openclaw-windows-node/issues/2020) Misleading missing-contract error hides native Gateway response validation failures | bug / native Gateway error classification | keep-open | 99% |
| [#2033](https://github.com/openclaw/openclaw-windows-node/issues/2033) A voice transcript can replace keystrokes typed while it is appended | bug / unsent draft data loss | existing-pr | 99% |
| [#2039](https://github.com/openclaw/openclaw-windows-node/issues/2039) A concatenated dotnet command passes the proof-pool word block | bug / proof tooling command validation | existing-pr | 99% |
| [#2045](https://github.com/openclaw/openclaw-windows-node/issues/2045) Diagnostics copies the device id from the wrong file | bug / active identity diagnostics | existing-pr | 99% |
| [#2073](https://github.com/openclaw/openclaw-windows-node/issues/2073) Accept forwarded launches and deep links before the startup update prompt | bug / startup activation ordering | existing-pr | 99% |
| [#2087](https://github.com/openclaw/openclaw-windows-node/issues/2087) Archive/unarchive rejected by Gateway 2026.11.1: SessionPatch omits expectedSessionId | bug / Gateway lifecycle protocol compatibility | simple-fix | 98% |

## #246: Decide Windows packaging and auto-update strategy

**keep-open** · enhancement / packaging-policy tracker · recommendation confidence 97% · 3 comments read.

The maintainer already selected MSIX-first. Current code now skips release updating for packaged installs, so the old no-guard claim is obsolete, but README still leads with Inno installers and release docs retain transitional ZIP/Dev-MSIX delivery and coordinated migration acceptance. No evidence closes the complete install/update support-policy criteria.

**Owner / next step:** Release/packaging owner: reconcile acceptance criteria with current release contracts.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/246](https://github.com/openclaw/openclaw-windows-node/issues/246)
- [README.md:20](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/README.md#L20)
- [docs/RELEASING.md:53](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/RELEASING.md#L53)
- [src/OpenClaw.Tray.WinUI/Services/UpdateCoordinator.cs:66](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/UpdateCoordinator.cs#L66)
- [tests/OpenClaw.Tray.Tests/UpdateBackendLoaderTests.cs:146](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/UpdateBackendLoaderTests.cs#L146)
- [https://github.com/openclaw/openclaw-windows-node/issues/246#issuecomment-4473122457](https://github.com/openclaw/openclaw-windows-node/issues/246#issuecomment-4473122457)

### Exact proposed comment

> The MSIX-first direction is already recorded, and current main now keeps packaged installs out of the GitHub/Updatum updater. This is not ready to close as fully delivered: README still presents the Inno installers, and the release/migration guide retains acceptance gates for the supported transition. Next step is a release-owner checklist of the supported Store, Inno and portable update paths, with signed x64/ARM64 install, upgrade and migration evidence. No new packaging decision request or duplicate implementation is needed.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Complete signed install/update/migration acceptance and final user-facing distribution policy.

## #1173: [Bug] Windows Hub tray (OpenClaw.Tray.WinUI.exe) crashes repeatedly with access violation in Microsoft.UI.Reactor.Core during UI mount

**keep-open** · bug / native rendering crash · recommendation confidence 98% · 7 comments read.

The rich-text/Expander boundary remains in production. Main now uses Reactor preview.17 and a different tail-navigation implementation, but those changes are not proof of the specific RichTextBlock.set_IsTextSelectionEnabled access violation. The explicit maintainer request for redacted native analysis is outstanding; do not repeat it as a new needs-info demand or close by dependency version.

**Owner / next step:** Native chat owner: use existing sanitized crash-evidence request and attached-tree reproduction.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1173](https://github.com/openclaw/openclaw-windows-node/issues/1173)
- [src/OpenClaw.Tray.WinUI/Chat/ToolCallCardRenderer.cs:276](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ToolCallCardRenderer.cs#L276)
- [src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj:105](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L105)
- [tests/OpenClaw.Tray.UITests/ReactorToolActivityProofTests.cs:605](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/ReactorToolActivityProofTests.cs#L605)
- [src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749)
- [https://github.com/openclaw/openclaw-windows-node/issues/1173#issuecomment-5335403821](https://github.com/openclaw/openclaw-windows-node/issues/1173#issuecomment-5335403821)
- [https://github.com/openclaw/openclaw-windows-node/pull/1549](https://github.com/openclaw/openclaw-windows-node/pull/1549)
- [https://github.com/openclaw/openclaw-windows-node/pull/2093](https://github.com/openclaw/openclaw-windows-node/pull/2093)

### Exact proposed comment

> Keeping this open. Main has moved beyond the original renderer/scrolling versions, but #1549 (fix(chat): adopt Reactor preview.16 session-switch crash workaround) proves a narrower invalid-anchor mitigation, not this rich-text access violation. The existing request for redacted native exception analysis remains the next useful evidence; no full dump should be posted publicly. A maintainer should replay the tool-card mount/scroll case on the current build before attributing or closing this report.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Affected-host or equivalent current-build rich-text mount crash reproduction and safe native exception analysis.

## #1330: Chat pane truncates bulleted list lines mid-word, no wrap, no ellipsis

**close-fixed** · bug / Markdown list layout · recommendation confidence 97% · 7 comments read.

AArnott supplied the screenshot and explicitly confirmed non-reproduction on September 28. Main additionally has active-renderer long ordered/bulleted-list wrapping and resize regressions, and merged #1549 adopts upstream Grid list layout rather than the old horizontal callback. No later human report contradicts the resolution; stale notices are not evidence either way.

**Owner / next step:** Parent: eligible for close with the narrow resolution comment.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1330](https://github.com/openclaw/openclaw-windows-node/issues/1330)
- [tests/OpenClaw.Tray.UITests/ReactorMarkdownListProofTests.cs:23](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/ReactorMarkdownListProofTests.cs#L23)
- [src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj:103](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L103)
- [https://github.com/openclaw/openclaw-windows-node/issues/1330#issuecomment-5862598488](https://github.com/openclaw/openclaw-windows-node/issues/1330#issuecomment-5862598488)
- [https://github.com/openclaw/openclaw-windows-node/pull/1549](https://github.com/openclaw/openclaw-windows-node/pull/1549)

- [Verified release ancestry](https://github.com/openclaw/openclaw-windows-node/compare/a4dd026cc5f85a444a0fcd05eef5b86282dbda8c...v2026.11.1)

### Exact proposed comment

> Closing as resolved for the reported mid-word bullet clipping. The September 28 follow-up confirms it no longer reproduces, and the active Reactor renderer now has long bulleted/numbered-list wrapping and narrow-to-wide resize regression coverage. #1549 (fix(chat): adopt Reactor preview.16 session-switch crash workaround) also carries the upstream Grid-based list layout. This does not close the separate chat scrolling or native-crash reports. If list text is still cut off on a current build, please reopen with the failing Markdown, pane width and display scaling.

**Posting policy:** Proposed parent action; recheck live state before publishing.

### Missing proof

- No new Windows execution in this review; recommendation rests on explicit reporter-side resolution plus checked-in production/test evidence.

## #1428: Feature: Support RFC 6750 / Can`t use openclaw windows via gateway that is behind an oauth proxy

**existing-pr** · enhancement / proxy authentication · recommendation confidence 99% · 7 comments read.

The basic environment/proxy questions are answered. Main still has only HTTP Basic request-header setup; separate Gateway tokens are protocol credentials. Open #1802 explicitly closes this issue but is Bearer-only, whereas the later discussion asks for Cloudflare multi-header parity. Do not create another fix or treat a clearer error as implementing proxy access.

**Owner / next step:** Existing proxy-auth PR owner and credential-security reviewer: reconcile supported proxy contract.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1428](https://github.com/openclaw/openclaw-windows-node/issues/1428)
- [src/OpenClaw.Shared/WebSocketClientBase.cs:306](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L306)
- [src/OpenClaw.Connection/GatewayRecord.cs:7](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/GatewayRecord.cs#L7)
- [https://github.com/openclaw/openclaw-windows-node/issues/1428#issuecomment-6016695338](https://github.com/openclaw/openclaw-windows-node/issues/1428#issuecomment-6016695338)
- [https://github.com/openclaw/openclaw-windows-node/issues/1428#issuecomment-6358686540](https://github.com/openclaw/openclaw-windows-node/issues/1428#issuecomment-6358686540)
- [PR #1802 (feat(connection): support RFC 6750 proxy Bearer authentication)](https://github.com/openclaw/openclaw-windows-node/pull/1802)

### Exact proposed comment

> Implementation is already tracked by #1802 (feat(connection): support RFC 6750 proxy Bearer authentication); please keep this issue open until that path is verified. The environment details are sufficient. The remaining scope decision is whether Bearer-only support satisfies this issue after the Cloudflare Access multi-header request, or whether custom-header parity is a separate follow-up. Validation must cover origin-bound protected credentials, redirect refusal, the probe and both WebSocket roles; no proxy bypass is proposed.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Native protected-storage/UI and real proxy/Gateway proof; scope agreement for multi-header providers.

## #1462: [Bug]: Chat stays on “Assistant is thinking” after session completes; /stop is queued

**keep-open** · bug / chat run lifecycle and stop handling · recommendation confidence 96% · 4 comments read.

Newly merged #2100 directly repairs delayed transcript updates reactivating completed turns, with native fixture queue-drain proof. It does not add /stop interception, explicitly adds no queue bypass, and this incident has no captured event order linking it conclusively to that mechanism. Preserve the remaining reported control-command behavior rather than claiming complete resolution.

**Owner / next step:** Chat lifecycle owner: validate the original sequence and scope remaining /stop behavior.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1462](https://github.com/openclaw/openclaw-windows-node/issues/1462)
- [src/OpenClaw.Tray.WinUI/Chat/ChatConversationState.cs:1863](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatConversationState.cs#L1863)
- [src/OpenClaw.Tray.WinUI/Chat/ChatLifecycleCommandDispatcher.cs:29](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatLifecycleCommandDispatcher.cs#L29)
- [tests/OpenClaw.Tray.UITests/GatewayFixtureChatRunCorrelationTests.cs:23](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureChatRunCorrelationTests.cs#L23)
- [tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs:8579](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs#L8579)
- [https://github.com/openclaw/openclaw-windows-node/pull/2100](https://github.com/openclaw/openclaw-windows-node/pull/2100)

### Exact proposed comment

> There is now a concrete partial fix on main: #2100 (fix(chat): completed replies stay busy after delayed updates), merged November 10, prevents completed runs from reopening on delayed transcript updates and verifies queue draining in the mounted app fixture. Keeping this report open because its exact event ordering was not captured and typed /stop still is not a native control command; that PR intentionally adds no queue bypass. Next step is to verify the original sequence on a build containing the fix, then separately disposition /stop control semantics rather than reopen the repaired lifecycle work.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Affected incident replay on a fix-containing build; explicit /stop control-action acceptance.

## #1542: Windows Companion v2026.9.4: Allow Windows UI APIs reverts to false after confirmation/relaunch

**keep-open** · bug / retired setting persistence with execution symptom · recommendation confidence 98% · 4 comments read.

The old toggle is removed and current MXC requests unconditionally enable contained UI compatibility, with legacy true/false migration tests. However shanselman explicitly retained the issue pending graceful-exit and real Gateway execution evidence, and 0xC0000142 startup was not proved fixed. Respect that retention; do not restore the retired toggle.

**Owner / next step:** Sandbox/MXC owner: complete previously requested affected-host execution proof.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1542](https://github.com/openclaw/openclaw-windows-node/issues/1542)
- [src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs:183](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs#L183)
- [tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs:198](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs#L198)
- [tests/OpenClaw.Tray.Tests/SettingsRoundTripTests.cs:49](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/SettingsRoundTripTests.cs#L49)
- [https://github.com/openclaw/openclaw-windows-node/issues/1542#issuecomment-6183763566](https://github.com/openclaw/openclaw-windows-node/issues/1542#issuecomment-6183763566)
- [https://github.com/openclaw/openclaw-windows-node/pull/1726](https://github.com/openclaw/openclaw-windows-node/pull/1726)

### Exact proposed comment

> Current main retires SystemRunAllowWindowsUi and uses a fixed contained compatibility policy, so another toggle-persistence patch would target obsolete behavior. Keeping this open in line with the existing maintainer retention: the affected-host 0xC0000142 command-startup result still needs verification through the real Gateway after graceful Exit/relaunch. The next run should use an SDK-migration build and record only versions, the sanitized command result and containment outcome, not full settings or credentials.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Affected-host graceful Exit/relaunch plus real Gateway system.run without weakening containment.

## #1568: Chat tail-scroll controller spins the UI thread at 100% CPU on layout changes

**existing-pr** · bug / native chat hang · recommendation confidence 99% · 8 comments read.

Open #1935 owns the tall-history reproducer; #2083 overlaps. Important current-main change: merged #2093 already removes synchronous ItemsView.StartBringItemIntoView, so older bot conclusions that main still calls it are false. Current native acceptance still has scrolling failures, and open branches must be reconciled with the imported workaround before closure.

**Owner / next step:** Existing chat scrolling PR owners: compare with current main and retain one verified repair lane.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1568](https://github.com/openclaw/openclaw-windows-node/issues/1568)
- [src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749)
- [src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:771](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L771)
- [tests/OpenClaw.Tray.Tests/ChatTimelinePresentationTests.cs:175](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ChatTimelinePresentationTests.cs#L175)
- [docs/ARCHITECTURE.md:1054](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L1054)
- [https://github.com/openclaw/openclaw-windows-node/pull/2093](https://github.com/openclaw/openclaw-windows-node/pull/2093)
- [https://github.com/openclaw/openclaw-windows-node/issues/1568#issuecomment-6516641109](https://github.com/openclaw/openclaw-windows-node/issues/1568#issuecomment-6516641109)
- [PR #1935 (fix(chat): chat freezes when opening tall message history)](https://github.com/openclaw/openclaw-windows-node/pull/1935)
- [PR #2083 (fix(chat): switching conversations can land on old messages or freeze the chat)](https://github.com/openclaw/openclaw-windows-node/pull/2083)

### Exact proposed comment

> Do not start a third scrolling fix. #1935 (fix(chat): chat freezes when opening tall message history) and #2083 (fix(chat): switching conversations can land on old messages or freeze the chat) already cover this area. Main has also changed materially through #2093 (improve(chat): align native conversation and tool activity with web): it no longer calls synchronous ItemsView.StartBringItemIntoView. Keeping this open until the existing synthetic tall-history reproducer, Notifications > Back and reader-position checks are rerun against that current implementation. Older source claims about the synchronous call no longer describe main.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Current-main tall-history and route-return/reader-position runtime acceptance; reconciliation of overlapping open PRs.

## #1574: Reconnect retry loop invokes reconnect authorization with no Windows session-ending check

**keep-open** · bug / shutdown reconnect admission · recommendation confidence 99% · 3 comments read.

The retry loop still invokes authorization after delay without a process-level Windows session-end signal. No matching abstraction was found; #1575 is a prerequisite tracker, not implemented evidence. Existing transport loss/reconnect PRs do not supply that Windows lifecycle contract.

**Owner / next step:** Connection lifecycle owner: establish #1575, then implement bounded consumer guard.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1574](https://github.com/openclaw/openclaw-windows-node/issues/1574)
- [src/OpenClaw.Shared/WebSocketClientBase.cs:650](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L650)
- [src/OpenClaw.Shared/WebSocketClientBase.cs:689](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L689)
- [tests/OpenClaw.Shared.Tests/WebSocketClientBaseTests.cs:194](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/WebSocketClientBaseTests.cs#L194)
- [https://github.com/openclaw/openclaw-windows-node/issues/1575](https://github.com/openclaw/openclaw-windows-node/issues/1575)
- [https://github.com/openclaw/openclaw-windows-node/issues/1574#issuecomment-5932168079](https://github.com/openclaw/openclaw-windows-node/issues/1574#issuecomment-5932168079)

### Exact proposed comment

> The reconnect authorization gap remains in current main. #1575 is the shared-signal prerequisite, not proof that the signal has been implemented. Next step is to wire that single lifecycle owner, including cancellation of a session-end request, then guard authorization before retry and after backoff. The existing substituted authorization delegate can prove ending-before-retry, ending-during-delay and normal operation without real sockets or wsl.exe. Do not add an independent Win32 observer to Shared or change reconnect policy to hide the symptom.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Shared lifecycle abstraction/production wiring; failing-before regression and Windows shutdown proof.

## #1642: `OPENCLAW_UPDATE_CHANNEL=alpha` is shown in Settings but ignored by the updater; alpha installs can never discover newer alphas

**existing-pr** · bug / alpha update discovery · recommendation confidence 99% · 5 comments read.

Main still uses FetchOnlyLatestRelease=true and rejects prereleases in fallback. Open #1960 explicitly implements alpha opt-in and ordering, including the alpha.93 to alpha.97 case. Flags alone remain insufficient; no duplicate fix.

**Owner / next step:** Existing updater PR owner: finish and land the alpha-aware selection lane.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1642](https://github.com/openclaw/openclaw-windows-node/issues/1642)
- [src/OpenClaw.Tray.WinUI/Services/UpdatumUpdateBackend.cs:12](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/UpdatumUpdateBackend.cs#L12)
- [src/OpenClaw.Tray.WinUI/Services/UpdateCheckPipeline.cs:164](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/UpdateCheckPipeline.cs#L164)
- [tests/OpenClaw.Tray.Tests/UpdateCheckPipelineTests.cs:146](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/UpdateCheckPipelineTests.cs#L146)
- [PR #1960 (fix(windows): honor alpha channel when selecting Companion updates)](https://github.com/openclaw/openclaw-windows-node/pull/1960)

### Exact proposed comment

> This is already covered by #1960 (fix(windows): honor alpha channel when selecting Companion updates), which remains open. Current main still performs stable-only discovery. Keep the issue open while that PR completes real installed-alpha update proof; acceptance must include alpha.93 to alpha.97 ordering, stable defaults and Store-managed servicing, not just enabling Updatum prerelease flags. The secondary Installed Apps/version issue should remain separate.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Real installed-alpha discovery/install path and native UI proof on the existing PR.

## #1678: Feature request : Openclaw local Windows Node need to start gateway automatically when i launch it

**keep-open** · enhancement / Gateway lifetime policy · recommendation confidence 99% · 5 comments read.

The author clarified start-with-Companion and stop-on-exit, not only startup. WSL keepalive deliberately outlives tray exit; native runtime tests preserve independently running services. This needs a product/ownership decision, not closure as an already-working start feature.

**Owner / next step:** Gateway lifecycle/product owner: decide opt-in lifetime semantics.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1678](https://github.com/openclaw/openclaw-windows-node/issues/1678)
- [src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs:26](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs#L26)
- [tests/OpenClaw.Connection.Tests/IsolatedGatewayRuntimeTests.cs:863](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/IsolatedGatewayRuntimeTests.cs#L863)
- [https://github.com/openclaw/openclaw-windows-node/issues/1678#issuecomment-6057305491](https://github.com/openclaw/openclaw-windows-node/issues/1678#issuecomment-6057305491)

### Exact proposed comment

> Keeping this as a feature request for Companion-bound Gateway lifetime, including explicit application exit. WSL Gateways currently remain running by design, and native Gateways distinguish services Companion started from services it only attached to. Next step is a maintainer decision on an opt-in managed-Gateway mode and the meaning of window close versus Exit. A universal stop-on-close change could interrupt other clients and background jobs, so it is not a safe cleanup fix.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Product decision and runtime-specific lifetime/ownership contract.

## #1689: [Bug] Hub 2026.9.8-1: premature WebSocket closures and lost system.run.prepare result with WSL Gateway

**keep-open** · bug / intermittent transport and result delivery · recommendation confidence 98% · 3 comments read.

No complete root cause or matching repair established. Main has gained handshake observations and generation-fenced sends, but the node send still logs before transmission and ignores the tracked send boolean. Premature-close log remains generic. #2079 improves cleanup after loss, not the reported heartbeat cause or delivery confirmation.

**Owner / next step:** Transport owner: collect one bounded correlated failure without repeating already supplied environment questions.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1689](https://github.com/openclaw/openclaw-windows-node/issues/1689)
- [src/OpenClaw.Shared/WindowsNodeClient.cs:793](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WindowsNodeClient.cs#L793)
- [src/OpenClaw.Shared/WebSocketClientBase.cs:595](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L595)
- [src/OpenClaw.Shared/WebSocketClientBase.cs:878](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L878)
- [src/OpenClaw.Shared/GatewayHandshakeObservation.cs:25](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/GatewayHandshakeObservation.cs#L25)
- [tests/OpenClaw.Shared.Tests/WindowsNodeClientTests.cs:3655](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/WindowsNodeClientTests.cs#L3655)
- [PR #2079 (fix: fail pending Gateway requests promptly after transport loss)](https://github.com/openclaw/openclaw-windows-node/pull/2079)

### Exact proposed comment

> Keeping this open. Current main adds transport-generation observations and fences accepted node-result sends, but a local ok=True log still does not prove Gateway receipt. #2079 (fix: fail pending Gateway requests promptly after transport loss) is related cleanup work, not a demonstrated fix for the missed heartbeat/prepare response here. The next investigation should correlate one command and one socket loss by operator/node role, generation, receive progress and actual result-write outcome, using bounded metadata only. No timeout increase, JSON ping or sandbox change is justified by the current evidence.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Correlated real Hub/Gateway loss and result-write evidence; root-cause reproduction.

## #1704: Companion app: “gateway connection check failed” with token_mismatch on a local gateway running under another Windows user

**keep-open** · bug report / cross-account token and setup-code failure · recommendation confidence 96% · 2 comments read.

Source retains explicit shared tokens and accepts base64url. The earlier specific request to pin the CLI to the identical endpoint/account and use a synthetic setup-code fixture remains unanswered. No new question needed. Stored-device-token recovery and open rollback work do not establish this shared-token report resolved.

**Owner / next step:** Connection owner: perform the already requested controlled comparison.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1704](https://github.com/openclaw/openclaw-windows-node/issues/1704)
- [src/OpenClaw.Connection/SetupCodeDecoder.cs:26](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/SetupCodeDecoder.cs#L26)
- [src/OpenClaw.Tray.WinUI/Services/GatewayDirectConnectService.cs:358](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/GatewayDirectConnectService.cs#L358)
- [tests/OpenClaw.Connection.Tests/SetupCodeFlowTests.cs:276](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/SetupCodeFlowTests.cs#L276)
- [tests/OpenClaw.Connection.Tests/SetupCodeDecoderTests.cs:11](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/SetupCodeDecoderTests.cs#L11)
- [PR #1509 (fix(connection): a rejected shared token drops the bootstrap token and SSH tunnel)](https://github.com/openclaw/openclaw-windows-node/pull/1509)

### Exact proposed comment

> Keeping this open without recommending credential deletion or a reinstall. Current source already preserves a nonempty typed shared token and accepts padded or unpadded base64url setup codes. The existing targeted diagnostic request remains appropriate: compare CLI and Companion against the same explicit endpoint under the same account context, and reproduce decoding with a synthetic credential-free fixture. #1509 (fix(connection): a rejected shared token drops the bootstrap token and SSH tunnel) covers rollback after rejection, not why this valid-token attempt is rejected.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Endpoint/account-pinned control and synthetic failing setup-code shape; current-build reproduction.

## #1725: Can't change local AI model for ollama

**keep-open** · bug / model-change UX and uncertain recovery · recommendation confidence 97% · 5 comments read.

Important update: #1744 merged November 4 and main now provides in-place model selection, removing ShowLocalAiModelSetup. The report is not yet safely closed: full live switch was explicitly unverified in the PR, and the ambiguous Cancel/Check again loop remains a separate safety-owned path. Open #1538/#1232 overlap old recovery routing and must not be represented as the sole current implementation.

**Owner / next step:** Local AI owner: verify merged in-place picker and separately reproduce uncertain cancellation.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1725](https://github.com/openclaw/openclaw-windows-node/issues/1725)
- [src/OpenClaw.Tray.WinUI/Pages/LocalAiPage.xaml.cs:184](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/LocalAiPage.xaml.cs#L184)
- [src/OpenClaw.Tray.WinUI/Pages/LocalAiPage.xaml.cs:304](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/LocalAiPage.xaml.cs#L304)
- [src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs:286](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs#L286)
- [tests/OpenClaw.Tray.Tests/Presentation/LocalAiPageViewModelTests.cs:1213](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/Presentation/LocalAiPageViewModelTests.cs#L1213)
- [tests/OpenClaw.SetupEngine.Tests/GatewayAiSetupClientTests.cs:182](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/GatewayAiSetupClientTests.cs#L182)
- [https://github.com/openclaw/openclaw-windows-node/pull/1744](https://github.com/openclaw/openclaw-windows-node/pull/1744)
- [https://github.com/openclaw/openclaw-windows-node/issues/1725#issuecomment-6198044370](https://github.com/openclaw/openclaw-windows-node/issues/1725#issuecomment-6198044370)
- [PR #1538 (fix(local-ai): Change model opens the scoped Local AI setup instead of full onboarding)](https://github.com/openclaw/openclaw-windows-node/pull/1538)
- [PR #1232 (fix: resume Local AI setup with a new model)](https://github.com/openclaw/openclaw-windows-node/pull/1232)

### Exact proposed comment

> The model-picker part now has a merged implementation: #1744 (feat: change the Local AI model in settings and choose the Gateway default model) adds direct selection in Settings > Model > Local AI and removes the old full-wizard Change model route. Companion-managed llama.cpp is not an Ollama endpoint. Keeping this open pending a complete current-build model switch and resolution of the separately reported uncertain Cancel/Check again loop; the merged PR explicitly did not prove a full live switch. Recovery must not discard an unconfirmed Gateway mutation just to dismiss the dialog.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Complete download/restart/chat switch on supported hardware; actual uncertain Gateway response and recovery outcome.

## #1758: [Windows ARM64] First-run native Gateway and Local AI setup take about 44 minutes

**keep-open** · bug report / setup performance and progress UX · recommendation confidence 99% · 3 comments read.

The old five-minute Store timeout is now 30 minutes with elapsed status; tests pin it. Maintainer explicitly reopened this broader stage-measurement report, which is not closed by a timeout increase or readiness change. No current cold/cached 64-GB ARM64 measurements were supplied.

**Owner / next step:** Setup/performance owner: execute bounded stage measurement on the affected hardware class.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1758](https://github.com/openclaw/openclaw-windows-node/issues/1758)
- [src/OpenClaw.SetupEngine/NativeGatewayPackageAcquisition.cs:19](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/NativeGatewayPackageAcquisition.cs#L19)
- [src/OpenClaw.SetupEngine.UI/Pages/NativeGatewaySetupPage.xaml.cs:397](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine.UI/Pages/NativeGatewaySetupPage.xaml.cs#L397)
- [tests/OpenClaw.SetupEngine.Tests/NativeGatewayPackageAcquisitionTests.cs:459](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/NativeGatewayPackageAcquisitionTests.cs#L459)
- [https://github.com/openclaw/openclaw-windows-node/issues/1758#issuecomment-6224395169](https://github.com/openclaw/openclaw-windows-node/issues/1758#issuecomment-6224395169)
- [https://github.com/openclaw/openclaw-windows-node/pull/1750](https://github.com/openclaw/openclaw-windows-node/pull/1750)

### Exact proposed comment

> Keeping this open as the maintainer requested for stage-specific setup measurement. The five-minute acquisition limit is already replaced by a 30-minute budget and elapsed waiting text through #1750 (fix(setup): give native Gateway Store install a 30-minute budget with elapsed-minute wait status). That does not prove the full 44-minute experience resolved. Next step is one cold and one cached ARM64 run recording Store/model acquisition, verification, startup, configuration and response-check boundaries, plus visible wait/retry behavior. No duplicate timeout patch is needed.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Cold/cached current-build ARM64 stage timings and actual slow Store wait/continuation experience.

## #1779: [Bug]: Native chat vs 2026.10.5 gateway: Enter/Ctrl+Enter dead, reply never renders, app freezes, orphaned ssh.exe holds tunnel port 18789

**keep-open** · bug / multi-symptom native chat and SSH lifecycle · recommendation confidence 99% · 4 comments read.

Enter handling is fixed on main and now shipped after the old release. Main has also changed scroll implementation, but missing reply delivery and forced-exit SSH ownership are not proved resolved; open scrolling PRs are partial. Do not close the whole cascade from one keyboard fix.

**Owner / next step:** Chat and SSH owners: test each remaining symptom independently on supported current builds.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1779](https://github.com/openclaw/openclaw-windows-node/issues/1779)
- [src/OpenClaw.Tray.WinUI/Chat/ReactorChatComposer.cs:440](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorChatComposer.cs#L440)
- [src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749)
- [src/OpenClaw.Connection/SshTunnelService.cs:1](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/SshTunnelService.cs#L1)
- [tests/OpenClaw.Connection.Tests/SshTunnelServiceTests.cs:1](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/SshTunnelServiceTests.cs#L1)
- [https://github.com/openclaw/openclaw-windows-node/pull/1674](https://github.com/openclaw/openclaw-windows-node/pull/1674)
- [https://github.com/openclaw/openclaw-windows-node/releases/tag/v2026.11.1](https://github.com/openclaw/openclaw-windows-node/releases/tag/v2026.11.1)
- [https://github.com/openclaw/openclaw-windows-node/pull/2093](https://github.com/openclaw/openclaw-windows-node/pull/2093)
- [PR #1935 (fix(chat): chat freezes when opening tall message history)](https://github.com/openclaw/openclaw-windows-node/pull/1935)
- [PR #2083 (fix(chat): switching conversations can land on old messages or freeze the chat)](https://github.com/openclaw/openclaw-windows-node/pull/2083)

- [Verified release ancestry](https://github.com/openclaw/openclaw-windows-node/compare/037c17dcb581cbe427ab1939576515643b9e0707...v2026.11.1)

### Exact proposed comment

> There is now a newer stable installer, v2026.11.1, and the Enter fix from #1674 (fix(chat): make Enter send from the multiline composer) is on main. The old assertion that stable has not moved beyond v2026.9.8-1 is no longer current. Keeping this issue open for the rest of the cascade: no evidence yet proves reply rendering and task-owned SSH cleanup after forced app termination. The next controlled run should separate keyboard submission, event delivery, native hang and listener/process ownership rather than assume one root cause or kill unrelated listeners.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Complete native chat-over-SSH sequence, sanitized delivery/hang evidence, exact-owned tunnel cleanup after forced termination.

## #1826: Sandboxed Python loopback IPC fails with SO_ERROR 10013 on Companion 2026.9.8-1

**keep-open** · bug / sandbox internal loopback compatibility inquiry · recommendation confidence 99% · 3 comments read.

Precise bounded Python reproducer already supplied, including successful outside-sandbox control; no more generic repro request needed. Main typed policy denies host loopback but this is not proof of sandbox-internal IPC behavior. Existing tests assert policy, not socketpair. SDK migration is not a verified fix.

**Owner / next step:** Sandbox owner: reuse the supplied safe reproducer, preserve host-service isolation.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1826](https://github.com/openclaw/openclaw-windows-node/issues/1826)
- [src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs:177](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs#L177)
- [tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs:185](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs#L185)
- [https://github.com/openclaw/openclaw-windows-node/issues/1826#issuecomment-6335911554](https://github.com/openclaw/openclaw-windows-node/issues/1826#issuecomment-6335911554)
- [https://github.com/openclaw/openclaw-windows-node/pull/1726](https://github.com/openclaw/openclaw-windows-node/pull/1726)

### Exact proposed comment

> The supplied bounded IPv4/IPv6 probe and ordinary-PowerShell control are sufficient to investigate; please do not relax containment or add a host-loopback exemption. Keeping this open because #1726 (feat: upgrade Windows node sandboxing to MXC 1.0 .NET SDK) and current policy tests do not establish same-invocation socket support. Next step is for the sandbox owner to run that existing probe through current real system.run, record effective containment and identify whether the limitation belongs to Companion, MXC or Windows. Internal IPC and access to host services must remain separate contracts.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Current SDK real system.run bounded IPv4/IPv6/socketpair proof and identified denial mechanism.

## #1843: [Bug] Companion v2026.10.5-alpha.4 WSL setup fails: pinned Node 24.19.0 rejected by install-cli (requires 24.22.1+)

**close-fixed** · bug / obsolete released Node pin · recommendation confidence 99% · 4 comments read.

#1824 merged the exact 24.19.0 to 24.22.1 correction. Current main and published v2026.11.1 tag both contain 24.22.1; GitHub compare reports the release ahead of the fix with behind_by=0. November 7 stable release includes x64 installer, satisfying the request for refreshed delivery. Its reported WSL CI proves fresh setup with the supported runtime. The November 6 no-release statement is superseded.

**Owner / next step:** Parent: eligible for close with actual release/installer recovery link.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1843](https://github.com/openclaw/openclaw-windows-node/issues/1843)
- [src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs:149](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs#L149)
- [tests/OpenClaw.SetupEngine.Tests/SetupStepsTests.cs:1982](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/SetupStepsTests.cs#L1982)
- [src/OpenClaw.SetupEngine/InstallCliStep.cs:77](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/InstallCliStep.cs#L77)
- [https://github.com/openclaw/openclaw-windows-node/pull/1824](https://github.com/openclaw/openclaw-windows-node/pull/1824)
- [https://github.com/openclaw/openclaw-windows-node/releases/tag/v2026.11.1](https://github.com/openclaw/openclaw-windows-node/releases/tag/v2026.11.1)
- [https://github.com/openclaw/openclaw-windows-node/blob/v2026.11.1/src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs#L149](https://github.com/openclaw/openclaw-windows-node/blob/v2026.11.1/src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs#L149)
- [https://github.com/openclaw/openclaw-windows-node/compare/51f18c1222c996fb6fa2eb4d7e8d668492103a0e...v2026.11.1](https://github.com/openclaw/openclaw-windows-node/compare/51f18c1222c996fb6fa2eb4d7e8d668492103a0e...v2026.11.1)
- [https://github.com/openclaw/openclaw-windows-node/actions/runs/39732987388](https://github.com/openclaw/openclaw-windows-node/actions/runs/39732987388)

### Exact proposed comment

> The refreshed installer is now published. v2026.11.1, released November 7, includes OpenClawCompanion-Setup-x64.exe and the Node 24.22.1 correction from #1824 (fix(setup): install a Node runtime supported by the Gateway). The release tag itself pins 24.22.1, and the fixing PR records successful real WSL setup with that runtime. Closing this specific 24.19.0 rejection as fixed. Update Companion using the v2026.11.1 installer and retry the normal setup flow; no manual WSL/runtime edits or data reset are required by this correction. If it still reports 24.19.0 after updating, please reopen with the running Companion version and sanitized install-cli error. Store rollout timing and unrelated setup failures are not implied by this closure.

**Posting policy:** Proposed parent action; recheck live state before publishing.

### Missing proof

- No affected-machine retry executed here; Store rollout not verified. Narrow fixed-pin release delivery and fixing-PR WSL evidence are established.

## #1881: Local AI upgrade can strand old model receipts; migrate known IDs or offer confirmed recovery

**existing-pr** · bug / persisted Local AI upgrade compatibility · recommendation confidence 99% · 2 comments read.

Exact model-ID equality and additional-asset receipt matching still reject old IDs/text-only receipts. #1771 remains open and now proposes automatic one-shot reset, not only the old manual reset; it still does not establish preserving compatible historical recipes. That change in consent behavior must be explicitly reconciled with this issue, not silently treated as complete.

**Owner / next step:** Local AI compatibility and existing reset PR owners: reconcile preservation versus automatic reset acceptance.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1881](https://github.com/openclaw/openclaw-windows-node/issues/1881)
- [src/OpenClaw.Shared/Inference/Catalog/LocalModelCatalog.cs:553](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Inference/Catalog/LocalModelCatalog.cs#L553)
- [src/OpenClaw.SetupEngine/LocalAiInstallReconciler.cs:484](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/LocalAiInstallReconciler.cs#L484)
- [src/OpenClaw.Connection/LocalAi/LlamaServerRouterConfiguration.cs:179](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/LocalAi/LlamaServerRouterConfiguration.cs#L179)
- [tests/OpenClaw.Connection.Tests/LocalAiBoltRuntimeTests.cs:58](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/LocalAiBoltRuntimeTests.cs#L58)
- [https://github.com/openclaw/openclaw-windows-node/pull/1771#issuecomment-6611500646](https://github.com/openclaw/openclaw-windows-node/pull/1771#issuecomment-6611500646)
- [PR #1771 (feat(local-ai): reset an unusable Local AI install without uninstalling)](https://github.com/openclaw/openclaw-windows-node/pull/1771)

### Exact proposed comment

> This remains open and overlaps #1771 (feat(local-ai): reset an unusable Local AI install without uninstalling). Its latest proposal now automatically resets an unusable receipt once during setup; it is no longer only the manual reset described in the original discussion. Before closure, explicitly reconcile that behavior with this issue’s preserve-compatible-receipts and confirmed-recovery criteria. Add real historical old-ID and public text-only Bolt fixtures, prove reuse where safe, and verify both Store/Inno upgrade recovery without deleting unrelated Gateway, pairing or cache state. Do not start a second reset implementation.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Historical-receipt migration/reuse, owner-approved consent contract, real Store/Inno upgrade and route-ownership failure proof.

## #1901: Windows Companion 2026.9.8-1 stuck on Loading messages / pairing required with reachable 2026.10.4 Gateway

**keep-open** · bug report / pairing and loading state · recommendation confidence 97% · 1 comments read.

Browser health and listening endpoint are not the same identity as authenticated Companion operator state. Current structured pairing parser retains request IDs; stale-device-token recovery predates the reported release. The existing request for one correlated handshake/device listing is still unanswered; no new duplicated needs-info question.

**Owner / next step:** Native connection owner: complete existing non-destructive handshake diagnosis.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1901](https://github.com/openclaw/openclaw-windows-node/issues/1901)
- [src/OpenClaw.Shared/OpenClawGatewayClient.cs:3445](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/OpenClawGatewayClient.cs#L3445)
- [src/OpenClaw.Tray.WinUI/Windows/WorkspaceWindow.xaml.cs:891](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Windows/WorkspaceWindow.xaml.cs#L891)
- [tests/OpenClaw.SetupEngine.Tests/NativeGatewaySetupConnectionTests.cs:22](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/NativeGatewaySetupConnectionTests.cs#L22)
- [https://github.com/openclaw/openclaw-windows-node/issues/1901#issuecomment-6455804629](https://github.com/openclaw/openclaw-windows-node/issues/1901#issuecomment-6455804629)

### Exact proposed comment

> Keeping this open without a reset or reinstall recommendation. A reachable isolated Gateway and working browser identity do not establish that Companion has operator authorization. The existing next step remains one correlated Companion connect result and device/pairing listing from that same package-owned Gateway, with credentials and personal paths omitted. Use the structured error/request identifier to distinguish rejected credentials, missing approval and a stale Workspace view before selecting a repair; a generic loading-screen fix is not evidence of resolution.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Correlated Companion handshake, exact isolated-Gateway target and paired/pending identity state.

## #1942: Local AI picker hides Nemotron 3.5 Bolt on 64 GB RTX Spark without explanation

**keep-open** · bug / unavailable-model explanation plus feature qualification request · recommendation confidence 98% · 1 comments read.

Picker still enumerates only OfferedPlans; Bolt CanOffer intentionally requires Eligible and >=128K. Available installed RAM alone cannot prove the current CUDA/commit budget. Existing tests intentionally reject busy/unknown capacity. Explanation is valid work; reducing admission thresholds is not a simple fix.

**Owner / next step:** Local AI eligibility/UI owners: expose existing assessment before expanding supported profiles.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1942](https://github.com/openclaw/openclaw-windows-node/issues/1942)
- [src/OpenClaw.SetupEngine.UI/Controls/LocalAiSetupControl.xaml.cs:472](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine.UI/Controls/LocalAiSetupControl.xaml.cs#L472)
- [src/OpenClaw.Shared/Inference/Catalog/LocalInferenceEligibility.cs:53](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Inference/Catalog/LocalInferenceEligibility.cs#L53)
- [tests/OpenClaw.Shared.Tests/NemotronBoltEligibilityTests.cs:36](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/NemotronBoltEligibilityTests.cs#L36)
- [tests/OpenClaw.Shared.Tests/NemotronBoltEligibilityTests.cs:73](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/NemotronBoltEligibilityTests.cs#L73)

### Exact proposed comment

> The explanation gap remains: the picker only shows offered plans, while Bolt intentionally requires a currently fitting 128K-or-larger profile. Keeping this open, but not treating 64 GB installed RAM as proof that admission should succeed. Next step is to capture the existing eligibility failure code and effective CUDA/free-commit budgets on the affected device, then expose that reason in the picker with Recheck. Lower-context or weaker-memory admission needs separate hardware qualification rather than a threshold-only patch.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Affected-device eligibility result/current version and native localized unavailable-choice UI design/proof.

## #1946: [Bug] Screen reader is not told when saving, copying or a connection action finishes

**keep-open** · bug / accessibility action-result announcements · recommendation confidence 99% · 1 comments read.

MCP copy still only assigns status text without announcement semantics; broad page action feedback remains unproved. A SearchHost notification now exists, disproving the old absolute zero-RaiseNotificationEvent claim, but it does not fix the listed surfaces. #2070 is message accessibility, not action feedback. Multi-surface native announcement behavior is not a one-line safe fix.

**Owner / next step:** Accessibility owner: focused action feedback seam plus native screen-reader validation.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1946](https://github.com/openclaw/openclaw-windows-node/issues/1946)
- [src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml.cs:361](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml.cs#L361)
- [src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml:253](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml#L253)
- [src/OpenClaw.Tray.WinUI/Controls/SearchHost.xaml.cs:188](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Controls/SearchHost.xaml.cs#L188)
- [tests/OpenClaw.Tray.Tests/NodeModeUiStateTests.cs:141](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/NodeModeUiStateTests.cs#L141)
- [tests/OpenClaw.Tray.UITests/AccessibilityScanTests.cs:65](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/AccessibilityScanTests.cs#L65)

### Exact proposed comment

> Keeping this open. Current MCP copy handlers still only replace status text, and static accessibility scans do not prove spoken action-result feedback. The source now has a notification call in SearchHost, but it does not cover these save/copy/connection actions. Next step is one shared, tested action-announcement mechanism with repeated-result, error urgency and no-focus-move cases, followed by an NVDA pass. Do not copy the proposed reopen helper unchanged without checking duplicate announcements; coordinate Cron-specific work with its existing Automations replacement.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Observable single-delivery, repeated result, urgency and no-focus-change NVDA/native proof.

## #1957: Autostart turned on during setup opens the Companion window at logon

**existing-pr** · bug / startup registration inconsistency · recommendation confidence 99% · 1 comments read.

Main still writes argument-free setup Run key and strict task matcher still expects empty arguments, unlike actual registration. #1963 explicitly closes this issue and remains open; existing matcher tests use old fixture. No duplicate implementation.

**Owner / next step:** Existing startup PR owner: complete validation and land shared command/matcher fix.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/1957](https://github.com/openclaw/openclaw-windows-node/issues/1957)
- [src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs:92](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs#L92)
- [src/OpenClaw.Shared/WindowsStartupTaskRegistration.cs:136](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WindowsStartupTaskRegistration.cs#L136)
- [tests/OpenClaw.Shared.Tests/StartupTaskInspectionTests.cs:57](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/StartupTaskInspectionTests.cs#L57)
- [tests/OpenClaw.Tray.Tests/ActivationRouterTests.cs:117](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ActivationRouterTests.cs#L117)
- [PR #1963 (fix(setup): autostart from setup opens the window at logon)](https://github.com/openclaw/openclaw-windows-node/pull/1963)

### Exact proposed comment

> This is already being repaired in #1963 (fix(setup): autostart from setup opens the window at logon). Both inconsistencies remain on main, so keep the issue open until that PR lands and its startup proof is complete. Acceptance should cover the non-elevated Run-key fallback and exact scheduled-task inspection, preserve rejection/unknown fallback rules, and verify a real quiet logon plus elevated task round-trip. No second startup-command patch is needed.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Exact-head setup-to-logon and elevated scheduled-task round-trip; existing PR closeout.

## #2001: Connection Status lists the saved gateway URL with its query and userinfo

**simple-fix** · bug / credential-safe diagnostics display · recommendation confidence 99% · 2 comments read.

Current RefreshGateways displays gw.Url verbatim; the existing sanitizer removes userinfo/query/fragment without changing stored values. No upstream active PR found. Contributor already supplied a precise two-file fork commit, which should be reused/credited rather than independently duplicated. It is the strongest genuinely small repair in this batch.

**Owner / next step:** Parent may select a bounded upstream repair based on the existing credited fork commit.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2001](https://github.com/openclaw/openclaw-windows-node/issues/2001)
- [src/OpenClaw.Tray.WinUI/Windows/ConnectionStatusWindow.xaml.cs:254](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Windows/ConnectionStatusWindow.xaml.cs#L254)
- [src/OpenClaw.Shared/GatewayUrlHelper.cs:84](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/GatewayUrlHelper.cs#L84)
- [src/OpenClaw.Tray.WinUI/Services/GatewayDirectConnectService.cs:367](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/GatewayDirectConnectService.cs#L367)
- [tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs:140](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs#L140)
- [https://github.com/openclaw/openclaw-windows-node/issues/2001#issuecomment-6642814387](https://github.com/openclaw/openclaw-windows-node/issues/2001#issuecomment-6642814387)
- [https://github.com/openclaw/openclaw-windows-node/commit/d0dafd852a8ec4d04be2f8448aaf12a6f721eda9](https://github.com/openclaw/openclaw-windows-node/commit/d0dafd852a8ec4d04be2f8448aaf12a6f721eda9)

### Exact proposed comment

> Confirmed in current main: Connection Status renders the saved URL unchanged even though this window already uses SanitizeForDisplay for connection results. The existing contributor commit d0dafd852a8ec4d04be2f8448aaf12a6f721eda9 is the narrow fix to bring upstream with attribution: sanitize only the displayed row, never the stored connection URL. Add query, fragment and saved-userinfo cases plus a stored-record preservation assertion, then verify the visible diagnostics row with synthetic credentials. No broader URL-storage rewrite is needed.

**Posting policy:** Proposed parent action; recheck live state before publishing.

### Missing proof

- Focused call-site and stored-record preservation regression; Windows visible diagnostics proof and required build/Shared/Tray suites.

### Bounded fix plan

In RefreshGateways, replace Text = $"{statusIcon} {gw.Url}" with Text = $"{statusIcon} {GatewayUrlHelper.SanitizeForDisplay(gw.Url)}". Reuse contributor commit as starting point; strengthen source-only regression with visible/data preservation coverage.

Validation:
- Fail-before row-level synthetic query/fragment/userinfo regression; stored GatewayRecord.Url unchanged.
- Run ./build.ps1 and full Shared/Tray suites with isolated roots.
- Run GatewayUrlHelperTests and relevant Connection Status regression; native windows-winui-interactive capture with synthetic secrets.

Risk: Low code scope, privacy-sensitive surface; preserve all connection/storage semantics.

## #2005: Config objects without a property list are edited as raw JSON

**existing-pr** · bug / nested config secret handling · recommendation confidence 99% · 2 comments read.

Raw JSON object and complex-array paths still bypass scalar sensitivity and stage whole containers. #1513 and #1500 already own sensitive rendering and placeholder preservation; #2036 handles masked containers. Contributor fork commit is additional overlapping work, not authorization to create a fourth implementation. Gateway-native sentinel resend is not proof of corruption.

**Owner / next step:** Existing Config PR owners: incorporate the contributor case into one coherent secret-safe editor contract.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2005](https://github.com/openclaw/openclaw-windows-node/issues/2005)
- [src/OpenClaw.Tray.WinUI/Controls/SchemaConfigEditor.xaml.cs:245](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Controls/SchemaConfigEditor.xaml.cs#L245)
- [src/OpenClaw.Tray.WinUI/Controls/SchemaConfigEditor.xaml.cs:542](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Controls/SchemaConfigEditor.xaml.cs#L542)
- [tests/OpenClaw.Tray.Tests/ConfigEditorModelTests.cs:139](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ConfigEditorModelTests.cs#L139)
- [https://github.com/openclaw/openclaw-windows-node/issues/2005#issuecomment-6642816141](https://github.com/openclaw/openclaw-windows-node/issues/2005#issuecomment-6642816141)
- [PR #1513 (fix(config): nostr keys and webhook URLs show in a plain text box)](https://github.com/openclaw/openclaw-windows-node/pull/1513)
- [PR #1500 (fix(config): refuse a legacy placeholder over the native secret mask)](https://github.com/openclaw/openclaw-windows-node/pull/1500)
- [PR #2036 (fix: keep masked configuration objects opaque during refresh)](https://github.com/openclaw/openclaw-windows-node/pull/2036)

### Exact proposed comment

> Keep this open and consolidate the repair with #1513 (fix(config): nostr keys and webhook URLs show in a plain text box), #1500 (fix(config): refuse a legacy placeholder over the native secret mask), and #2036 (fix: keep masked configuration objects opaque during refresh). Main still preloads object JSON, but sending the Gateway-native redaction sentinel alone does not establish secret loss. The next regression should edit one nonsecret sibling and prove that nested credentials remain hidden and unchanged under the real Gateway contract. Preserve the existing hidden-container Replace all / confirmed Clear all direction rather than adding an incompatible per-entry editor.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Actual Gateway nested-secret preservation and native container masking; coordinated acceptance across existing PRs.

## #2009: MSIX storage validation leaves the sideload certificate in TrustedPeople

**keep-open** · bug / validation trust cleanup · recommendation confidence 99% · 2 comments read.

The script imports machine-wide trust and discards ownership information; teardown/finally do not remove it. Contributor fork fix exists but no upstream PR. This is bounded, yet not a trivial cleanup: preserve identical preexisting trust and manual continuation/failure state. Current CI has an ownership-aware certificate pattern to consult.

**Owner / next step:** Packaging/security owner: review existing fork patch and add isolated certificate ownership proof.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2009](https://github.com/openclaw/openclaw-windows-node/issues/2009)
- [scripts/validate-msix-storage-paths.ps1:591](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/scripts/validate-msix-storage-paths.ps1#L591)
- [scripts/validate-msix-storage-paths.ps1:1041](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/scripts/validate-msix-storage-paths.ps1#L1041)
- [.github/workflows/ci.yml:2160](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/.github/workflows/ci.yml#L2160)
- [tests/OpenClaw.Tray.Tests/MsixDevelopmentSigningTests.cs:11](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/MsixDevelopmentSigningTests.cs#L11)
- [https://github.com/openclaw/openclaw-windows-node/issues/2009#issuecomment-6642818108](https://github.com/openclaw/openclaw-windows-node/issues/2009#issuecomment-6642818108)
- [https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/2009-msix-sideload-cert?expand=1](https://github.com/openclaw/openclaw-windows-node/compare/main...SebTardif:fix/2009-msix-sideload-cert?expand=1)

### Exact proposed comment

> Confirmed source gap: the validator imports TrustedPeople trust but neither teardown nor finally restores that trust state. Reuse the existing contributor branch as the starting point rather than duplicate it. Before an upstream repair, record whether the exact thumbprint existed before this run and define cleanup for normal completion, installation failure and manual continuation. Remove only validation-owned trust and prove a preexisting identical certificate survives. This needs an isolated elevated Windows certificate-store test; no blanket trust-store cleanup is appropriate.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Review focused contributor patch; ownership-safe normal/failure/manual-continuation certificate-store tests.

## #2020: Misleading missing-contract error hides native Gateway response validation failures

**keep-open** · bug / native Gateway error classification · recommendation confidence 99% · 1 comments read.

RequiredInt still throws generic Unsupported(), including gateway.port after integration validation. Endpoint security returns that misleading message while withholding credentials. Several validation/process categories and safe diagnostics need preserving, so do not classify as a trivial string replacement or claim underlying connection repaired.

**Owner / next step:** Native package client owner: scoped error-model repair, not a protocol/authorization relaxation.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2020](https://github.com/openclaw/openclaw-windows-node/issues/2020)
- [src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs:215](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs#L215)
- [src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs:398](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs#L398)
- [src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs:463](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs#L463)
- [src/OpenClaw.Connection/NativeGatewayEndpointSecurity.cs:58](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/NativeGatewayEndpointSecurity.cs#L58)
- [tests/OpenClaw.Connection.Tests/NativeGatewayPackageClientTests.cs:305](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/NativeGatewayPackageClientTests.cs#L305)

### Exact proposed comment

> The classification defect is still present on main: a missing gateway.port after successful integration validation reaches the same Unsupported() message as an absent contract. Next step is a behavioral decoder-to-authorization regression using valid initial status followed by an invalid lifecycle response, then preserve an allowlisted operation/field/shape error through the existing UI. Keep credentials withheld, cancellation intact and probe count unchanged. This is not evidence that the underlying reported connection failure or package selection is diagnosed, and raw producer text must not become the new diagnostic.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Fail-before structured classification and redaction regressions; existing UI surfaced-error proof without extra probes.

## #2033: A voice transcript can replace keystrokes typed while it is appended

**existing-pr** · bug / unsent draft data loss · recommendation confidence 99% · 1 comments read.

Queued SetDraft plus AppendVoiceTranscript deterministically reads stale _draft. Existing open fork PR #3 and exact two-file commit move the append inside Mutate and include held-dispatch regression. Not upstream yet; do not duplicate active contribution.

**Owner / next step:** Composer owner/parent: review and upstream the existing fork contribution.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2033](https://github.com/openclaw/openclaw-windows-node/issues/2033)
- [src/OpenClaw.Tray.WinUI/Chat/ChatComposerViewModel.cs:253](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatComposerViewModel.cs#L253)
- [tests/OpenClaw.Tray.Tests/ChatComposerViewModelTests.cs:421](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ChatComposerViewModelTests.cs#L421)
- [https://github.com/SebTardif/openclaw-windows-node/pull/3](https://github.com/SebTardif/openclaw-windows-node/pull/3)
- [https://github.com/SebTardif/openclaw-windows-node/commit/4ac31a11557e80bfc93359c516287c2242c857ec](https://github.com/SebTardif/openclaw-windows-node/commit/4ac31a11557e80bfc93359c516287c2242c857ec)

### Exact proposed comment

> The stale-read race remains on main, and the existing fork PR #3 (fix: keep keystrokes when a voice transcript is appended) already contains the focused mutation-boundary fix. Bring that contribution upstream with attribution instead of creating a competing patch. Keep its held-dispatch regression: queued SetDraft("typed") followed by AppendVoiceTranscript("heard") must yield "typed heard". Also preserve draft revision, slash-menu reconciliation and disposal behavior, then verify the actual voice/typing/Send path in an isolated app.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Upstream integration, required build/Shared/Tray closeout, live voice/keyboard/send interleaving proof.

## #2039: A concatenated dotnet command passes the proof-pool word block

**existing-pr** · bug / proof tooling command validation · recommendation confidence 99% · 1 comments read.

Host-kind commands bypass repository parser checks and quote-stripped do+tnet misses raw-tool words. Existing open fork PR #7 has focused patterns, but its regression changes pool[2] repository command already rejected by main. Need host-kind failing-before test, not another repair.

**Owner / next step:** Proof-tooling owner: refine the existing fork contribution and upstream it.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2039](https://github.com/openclaw/openclaw-windows-node/issues/2039)
- [scripts/validate-proof-pools.ps1:739](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/scripts/validate-proof-pools.ps1#L739)
- [scripts/validate-proof-pools.ps1:752](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/scripts/validate-proof-pools.ps1#L752)
- [scripts/test-proof-pool-validator.ps1:429](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/scripts/test-proof-pool-validator.ps1#L429)
- [.github/proof-pools.json:27](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/.github/proof-pools.json#L27)
- [https://github.com/SebTardif/openclaw-windows-node/pull/7](https://github.com/SebTardif/openclaw-windows-node/pull/7)
- [https://github.com/SebTardif/openclaw-windows-node/commit/1dca39e60abf08f535758a0b1a23660a4ed94156](https://github.com/SebTardif/openclaw-windows-node/commit/1dca39e60abf08f535758a0b1a23660a4ed94156)

### Exact proposed comment

> Keep this open and continue through fork PR #7 (fix: reject a concatenated dotnet command in proof pools). The affected path is kind=host: it skips the repository-command parser, so the constructed executable name escapes the word block. The current fork regression edits a repository command that main already rejects. Move/add the regression to the existing host-command matrix and require it to fail before the fix under every supported validator mode, while retaining accepted direct build and proof-test recipes.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Fail-before host-kind regression and full Windows PowerShell 5.1/PowerShell 7 validator matrix.

## #2045: Diagnostics copies the device id from the wrong file

**existing-pr** · bug / active identity diagnostics · recommendation confidence 99% · 1 comments read.

DebugPage still reads local-root file and camelCase keys instead of active Gateway identity. Existing fork PR #11 addresses path/schema but helper tests do not prove actual gateway selection or clipboard. Patch also leaves old Tag values on missing/error branches, a case to cover during upstream integration.

**Owner / next step:** Diagnostics owner: upstream/refine existing contribution with actual page selection/copy tests.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2045](https://github.com/openclaw/openclaw-windows-node/issues/2045)
- [src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs:56](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs#L56)
- [src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs:632](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs#L632)
- [src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs:676](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml.cs#L676)
- [tests/OpenClaw.Tray.Tests/DiagnosticsPageContractTests.cs:191](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/DiagnosticsPageContractTests.cs#L191)
- [https://github.com/SebTardif/openclaw-windows-node/pull/11](https://github.com/SebTardif/openclaw-windows-node/pull/11)
- [https://github.com/SebTardif/openclaw-windows-node/commit/8065eec19130ad8604dcdc50de28e14882322f8f](https://github.com/SebTardif/openclaw-windows-node/commit/8065eec19130ad8604dcdc50de28e14882322f8f)

### Exact proposed comment

> Main still reads the wrong directory and JSON property names. The existing fork PR #11 (fix: copy the diagnostics device id from the active gateway) should be the upstream starting point, not duplicated. Add a behavioral active-Gateway selection test, switch to a missing/malformed identity and ensure stale copy values are cleared, then verify the actual Diagnostics clipboard. Read/copy only DeviceId and PublicKeyBase64; do not generate or rewrite an identity or expose PrivateKeyBase64.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Active-Gateway/missing-identity behavioral selection, stale copy-tag clearing and native clipboard proof.

## #2073: Accept forwarded launches and deep links before the startup update prompt

**existing-pr** · bug / startup activation ordering · recommendation confidence 99% · 1 comments read.

App still awaits ordinary update prompt before starting forwarded listener and still directly replaces _globalHotkey. Open #2080 explicitly closes this lane. Existing startup order test pins old behavior. No duplicate app-lifecycle patch.

**Owner / next step:** Existing startup activation PR owner: complete current-head proof and land.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2073](https://github.com/openclaw/openclaw-windows-node/issues/2073)
- [src/OpenClaw.Tray.WinUI/App.xaml.cs:1116](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L1116)
- [src/OpenClaw.Tray.WinUI/App.xaml.cs:1127](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L1127)
- [src/OpenClaw.Tray.WinUI/App.xaml.cs:1132](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L1132)
- [src/OpenClaw.Tray.WinUI/App.SettingsChangeCoordinator.cs:117](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.SettingsChangeCoordinator.cs#L117)
- [tests/OpenClaw.Tray.Tests/AppRefactorContractTests.cs:127](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/AppRefactorContractTests.cs#L127)
- [PR #2080 (fix: accept forwarded launches during startup update prompts)](https://github.com/openclaw/openclaw-windows-node/pull/2080)

### Exact proposed comment

> This is already owned by #2080 (fix: accept forwarded launches during startup update prompts). Current main still has the old listener ordering and second hotkey writer, so keep the issue open until that PR lands. Its synthetic pending-update/pipe proof is useful; remaining acceptance should include an actual ordinary release update prompt and a settings-save/disable hotkey interleaving, while preserving current-user IPC, native setup receipt bypass and installer-exit handling.

**Posting policy:** Do not post a redundant status-only comment automatically. Use the draft only when the parent judges a substantive update useful. Existing unanswered asks are deliberately not duplicated.

### Missing proof

- Real ordinary release update-prompt behavior and hotkey settings interleaving on exact candidate.

## #2087: Archive/unarchive rejected by Gateway 2026.11.1: SessionPatch omits expectedSessionId

**simple-fix** · bug / Gateway lifecycle protocol compatibility · recommendation confidence 98% · 1 comments read.

Both archive callers omit observed SessionId and SessionPatch cannot serialize it. Released Gateway v2026.11.1 admission was independently fetched and requires expectedSessionId for archived. Fixture permits requests the Gateway rejects. No active issue-specific PR found; #1897 is overlapping Sessions redesign, not demonstrated ownership of this defect. Small bounded fix if limited to archive/restore; delete/reset expansion is not justified.

**Owner / next step:** Parent may choose narrow protocol repair, coordinating only authorized existing Sessions work.

### Evidence

- [https://github.com/openclaw/openclaw-windows-node/issues/2087](https://github.com/openclaw/openclaw-windows-node/issues/2087)
- [src/OpenClaw.Shared/GatewayProtocolModels.cs:322](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/GatewayProtocolModels.cs#L322)
- [src/OpenClaw.Tray.WinUI/Windows/WorkspaceSessionMenuController.cs:287](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Windows/WorkspaceSessionMenuController.cs#L287)
- [src/OpenClaw.Tray.WinUI/Pages/SessionsPage.xaml.cs:391](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/SessionsPage.xaml.cs#L391)
- [tests/OpenClaw.TestSupport/Gateway/GatewayScenario.cs:754](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.TestSupport/Gateway/GatewayScenario.cs#L754)
- [tests/OpenClaw.Shared.Tests/GatewayProtocolModelsTests.cs:608](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayProtocolModelsTests.cs#L608)
- [tests/OpenClaw.Shared.Tests/GatewayFixtureProtocolTests.cs:455](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayFixtureProtocolTests.cs#L455)
- [https://github.com/openclaw/openclaw/blob/v2026.11.1/src/gateway/sessions-patch-admission.ts#L47](https://github.com/openclaw/openclaw/blob/v2026.11.1/src/gateway/sessions-patch-admission.ts#L47)
- [PR #1897 (feat: manage sessions and worktrees in native Windows UI)](https://github.com/openclaw/openclaw-windows-node/pull/1897)

### Exact proposed comment

> Confirmed against current main and the released Gateway v2026.11.1 admission rule: Archive and Unarchive send archived without expectedSessionId, and the local fixture currently accepts that invalid request. A focused fix can add the shared field and pass the identity captured with each sidebar/archived row, refusing missing or stale identities without changing the UI on rejection. Coordinate the overlap with #1897 (feat: manage sessions and worktrees in native Windows UI), but keep this compatibility repair narrow. Delete/reset require their own contract check and should not be bundled by assumption.

**Posting policy:** Proposed parent action; recheck live state before publishing.

### Missing proof

- Older supported Gateway schema compatibility; failing-before identity-enforcing fixture and real Gateway archive/restore acceptance.

### Bounded fix plan

Add optional ExpectedSessionId to SessionPatch and ToPayload; capture SessionInfo.SessionId for sidebar Archive/Unarchive. Retain it in ArchivedSessionViewModel for Settings restore. Fail safely for missing identity. Update GatewayScenario lifecycle admission to reject missing/mismatched expectedSessionId before mutation.

Validation:
- GatewayProtocolModelsTests serialization cases and authenticated fixture archive/unarchive requiring observed identity.
- Missing/stale identity leaves active/archived lists and navigation unchanged; newer connection/session identity fenced.
- Verify field support with oldest supported Gateway before unconditionally sending; do not retry unguarded.
- Run ./build.ps1, full Shared/Tray suites, and current-head real Gateway 2026.11.1 archive/restore plus native UI proof.

Risk: Bounded cross-owner compatibility fix; optimistic-concurrency guard must never be synthesized from a refreshed unrelated session.
