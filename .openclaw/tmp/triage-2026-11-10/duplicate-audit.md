# Windows Node duplicate audit, 2026-11-10

## Verified recommendation

**One additional consolidation: keep [#1568](https://github.com/openclaw/openclaw-windows-node/issues/1568), preserve the evidence from [#1167](https://github.com/openclaw/openclaw-windows-node/issues/1167), then close #1167 as a duplicate, not as fixed. Confidence: 96%.**

The older issue title suggests synchronous history projection. Its substantive follow-ups instead identify the same sustained, CPU-bound native tail-navigation hang as #1568: identical InitialTailPositioner/ItemsView stack boundary, startup/restoration trigger, healthy Gateway and matching released-version recurrence. This is more than title, shared library, or exception-code similarity.

**Why #1568 is canonical despite being newer:** it has the minimized 16-message native reproducer, repeated process stacks, the re-arm-cap counterexample, and the existing closing [#1935 (fix(chat): chat freezes when opening tall message history)](https://github.com/openclaw/openclaw-windows-node/pull/1935). Keeping it avoids redirecting contributor work. The original approximately 200-message switch case and older investigation must not disappear.

This is an issue-tracking consolidation, not a claim that all underlying WinUI defects are identical or fixed. #1167 also contains an invalid-anchor crash investigation and returned-but-mispositioned scroll observations; explicitly preserve their separate status. Current source already replaces the old synchronous call, so historical stacks do not prove that today's head still has that code.

## Evidence for the additional pair

- September 4 dump: startup/reconnect restored ~311 KB history; one full UI core; InitialTailPositioner.StartTailRequest -> ItemsView.StartBringItemIntoView; retrieval/parsing completed and Gateway remained healthy. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-5543591128)
- September 12 independent dump: same native call, valid final index 76/77; history restoration A/B and healthy Gateway distinguish the failure from generic slow projection. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-5645239747)
- October 11 v2026.9.8-1 recurrence: 886,583-byte restored history, UI thread consumes 3.016 CPU-seconds in 3 seconds while process and Gateway remain alive. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-6113641601)
- October 23 v2026.9.8-1 dump: same InitialTailPositioner -> ItemsView.StartBringItemIntoView UI hang; sessions.changed coalescing reduced frequency but did not remove it. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1568#issuecomment-6354484890)
- October 28 deterministic restored-chat launch: eight-entry managed re-arm cap did not help, bypassing synchronous navigation restored responsiveness; ScrollTo workaround raised layout exceptions. Treat patch outcomes as reporter observations, not independently run tests. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1568#issuecomment-6460815930)
- Three independent live stacks at the same blocked native navigation boundary, healthy /healthz and /readyz. Reporter explicitly corrects unproven managed-recursion explanation. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1568#issuecomment-6513417778)
- 16-message tall-row synthetic native reproducer and preservation requirements. Full-app route-return clipping and unproved reader retention explicitly remain. [Evidence](https://github.com/openclaw/openclaw-windows-node/issues/1568#issuecomment-6516641109)
- Historical release detaches LayoutUpdated before queued StartTailRequest; not evidence of unconditional handler recursion. [Evidence](https://github.com/openclaw/openclaw-windows-node/blob/v2026.9.8-1/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L181-L206)
- Historical release synchronously calls the exact native API recorded in both issues, after validating target. [Evidence](https://github.com/openclaw/openclaw-windows-node/blob/v2026.9.8-1/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L254-L274)
- Current main no longer calls ItemsView.StartBringItemIntoView. It realizes through ItemsRepeater and uses deferred element StartBringIntoView. Consolidation is not a fixed verdict. [Evidence](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749-L832)
- Current guard forbids synchronous item bring and forced layout. Source inspected, not executed as Windows proof. [Evidence](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ChatTimelinePresentationTests.cs#L175-L191)

### Unique information to preserve

- Original manual switch into approximately 200 messages; count is not an established threshold.
- Automatic startup/reconnect restoration of default history, independent ~311 KB/~726 KB dump reports, valid tail index 76/77, and later 886,583-byte v2026.9.8-1 reproduction.
- Healthy Gateway and sustained one-core UI saturation; distinguish hang from process exit.
- dotMorten invalid-anchor crash analysis is a separate failure observation; do not assert that the hang proves that exception.
- RomneyDa preview.16 synthetic traces (56 entries/56 returns) are returned-but-mispositioned navigation, not reproduction of sustained hang; ARM64 emulation is not native x64 acceptance.
- Preserve original evidence links without copying full dumps, conversation contents, or requesting destructive reset.

### Proposed comment on canonical #1568

Consolidating the sustained native-chat UI-thread hang from #1167 here so one open issue owns this failure. This is duplicate cleanup, not a claim that the hang is fixed.

The strongest shared evidence is the same blocked UI-thread path, InitialTailPositioner.StartTailRequest -> ItemsView.StartBringItemIntoView, with the Gateway still healthy. #1167 supplies independent startup/reconnect dumps and valid-tail evidence: https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-5543591128 and https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-5645239747. Its later v2026.9.8-1 report records 886,583 bytes of restored history and sustained single-core saturation: https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-6113641601.

Keep the original approximately 200-message session-switch scenario and automatic startup/reconnect restoration as acceptance cases. Also retain the separate invalid-anchor crash investigation (https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-5647075810) and returned-but-mispositioned scroll evidence (https://github.com/openclaw/openclaw-windows-node/issues/1167#issuecomment-6352852481) as distinct observations, not proof that every crash shares the hang mechanism. The reported 200-message count is not an established threshold, and neither initial history projection nor an unconditional managed LayoutUpdated recursion is a verified root cause.

#1935 (fix(chat): chat freezes when opening tall message history) already closes this issue; #2083 (fix(chat): switching conversations can land on old messages or freeze the chat) overlaps. Current main has already replaced the old synchronous navigation call, so reconcile those candidates and validate current-build native restoration, tall-history navigation, final-row visibility, route return and reader-position preservation before closing as fixed. Preserve conversation data; do not request public full dumps or require a reset.

### Proposed closing comment on #1167

Post this only after the preservation comment is confirmed on #1568. Include that new comment's actual URL in the closing comment when available.

Closing as a duplicate of #1568, not as fixed. The later dump evidence here narrows the sustained history-restoration hang to the same InitialTailPositioner.StartTailRequest -> ItemsView.StartBringItemIntoView UI-thread boundary documented there, with a healthy Gateway and matching released-build startup/reconnect reproductions. #1568 has the minimized tall-history reproducer and active #1935 (fix(chat): chat freezes when opening tall message history).

The independent dumps, valid tail index, original approximately 200-message switch scenario and later 886,583-byte startup restoration have been linked in #1568 for acceptance. This does not merge every native crash into that mechanism: the invalid-anchor crash and returned-but-mispositioned scroll observations remain explicitly distinguished. Please follow #1568 for the unresolved hang and current-build verification; no conversation reset or public full dump is requested.

### Mutation order for parent

1. Fresh-read both targets immediately before mutation.
2. Post the evidence-preservation comment on #1568 and verify its URL/body.
3. Only then post closing comment on #1167 referencing that canonical comment and close as duplicate/not-planned, not completed.
4. Keep #1568 open. Do not change PR bodies/targets or source as part of duplicate cleanup.

## Already parent-owned browser consolidation

Keep #1701; close #1975 as its duplicate. Confidence: 99%. Both describe the same non-SSH native Gateway browser-port authorization refusal before authentication, and the production callback reaches fixture-only authorization for a record without managed WSL.

The parent has already posted the preservation [comment on #1701](https://github.com/openclaw/openclaw-windows-node/issues/1701#issuecomment-6777781432) and the [closing explanation on #1975](https://github.com/openclaw/openclaw-windows-node/issues/1975#issuecomment-6777817576). Reuse them, do not add another pair. Preserve the [November 9 Companion/CLI 2026.11.1 reproduction](https://github.com/openclaw/openclaw-windows-node/issues/1975#issuecomment-6730900436). A fresh REST read at approximately 20:01 UTC still returned #1975 open, so this audit does not claim the closure completed.

**Do not also close #1722 into #1701:** its absent bootstrap-helper provisioning and local/managed-WSL support scope is independent of native browser HTTP attestation. #1614 additionally needs remote-PC/Edge topology acceptance.

## Tempting pairs and clusters that must remain separate

These are current duplicate-audit decisions, not new requests to comment on every issue. Where a narrower common symptom exists, cross-link it without discarding residual scope. No additional closing comments are proposed.

### #1701, #1722

**partial-overlap / multi-symptom. Confidence: 99%.** Native browser HTTP listener refusal overlaps #1701, but #1722 also owns absent BrowserBootstrap/helper distribution and cross-account interactive Chrome support. Its October 28 managed-WSL report has working Edge/CDP but absent helper/native-host registration. Closing the whole issue into #1701 would discard that independent provisioning contract.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1701) [2](https://github.com/openclaw/openclaw-windows-node/issues/1722) [3](https://github.com/openclaw/openclaw-windows-node/issues/1722#issuecomment-6400366145) [4](https://github.com/openclaw/openclaw-windows-node/issues/1722#issuecomment-6471359129)

### #1614, #1722

**different topology / partial helper overlap. Confidence: 97%.** #1614 requires a local helper/browser runtime on a laptop with the Gateway in WSL on another PC and Edge support. #1722 reports local native isolated-account and managed-WSL provisioning. A shared executable name is not proof of the same install path or missing owner. Keep remote-host/Edge acceptance explicit rather than closing as a native-attestation duplicate.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1614) [2](https://github.com/openclaw/openclaw-windows-node/issues/1722)

### #1701, #1691, #2020

**distinct security boundary and diagnostic contract. Confidence: 99%.** #1701 rejects the browser-control HTTP listener before auth. #1691 fails the native Gateway endpoint/status contract, including multi-listener port selection. #2020 is safe error classification for multiple native decoder failures, not a fix for missing ownership/port. Trusting the Gateway port cannot attest a separate browser port.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1701) [2](https://github.com/openclaw/openclaw-windows-node/issues/1691) [3](https://github.com/openclaw/openclaw-windows-node/issues/2020) [4](https://github.com/openclaw/openclaw-windows-node/issues/1691#issuecomment-6306087257) [5](https://github.com/openclaw/openclaw-windows-node/issues/1691#issuecomment-6765579570)

### #1432, #1542, #1764

**similar exit code / distinct or unproved trigger. Confidence: 99%.** #1432 lacks effective MXC/UI policy and exact failing invocation. #1542 specifically persists then loses the Allow UI setting; maintainer retained graceful-Exit and actual Gateway command verification. #1764 alone has affected-host whoami success with UI compatibility enabled. Fixed contained UI compatibility on current main does not establish #1432 or the entire #1542 report as the same defect.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1432) [2](https://github.com/openclaw/openclaw-windows-node/issues/1542) [3](https://github.com/openclaw/openclaw-windows-node/issues/1764) [4](https://github.com/openclaw/openclaw-windows-node/issues/1542#issuecomment-6183763566) [5](https://github.com/openclaw/openclaw-windows-node/issues/1764#issuecomment-6258607388)

### #1432, #1571, #1826, #1687

**distinct execution failure boundaries. Confidence: 99%.** Do not group every node execution failure: #1571 concerns Windows session-end process creation, #1826 sandbox-internal loopback IPC WSAEACCES, #1687 pre-execution rejection of a nonempty custom env payload, and #1432 insufficiently diagnosed DLL-init failures.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1432) [2](https://github.com/openclaw/openclaw-windows-node/issues/1571) [3](https://github.com/openclaw/openclaw-windows-node/issues/1826) [4](https://github.com/openclaw/openclaw-windows-node/issues/1687)

### #1173, #1414, #1421, #1568

**different crash signatures / insufficient identity. Confidence: 98%.** #1173 has a RichTextBlock.set_IsTextSelectionEnabled access violation while mounting rich text in an Expander; #1414 has older-session process exit without a terminating stack; #1421 ends at ArrangeOverride/invalid argument with no lower native frame. A shared render/scroll entry does not equate these terminating failures to #1568 sustained native navigation hang.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1173) [2](https://github.com/openclaw/openclaw-windows-node/issues/1414) [3](https://github.com/openclaw/openclaw-windows-node/issues/1421) [4](https://github.com/openclaw/openclaw-windows-node/issues/1568) [5](https://github.com/openclaw/openclaw-windows-node/issues/1173#issuecomment-5345455971) [6](https://github.com/openclaw/openclaw-windows-node/issues/1421#issuecomment-5668460714)

### #1414, #1421

**similar history trigger / insufficient shared mechanism. Confidence: 96%.** #1414 only identifies older-session selection; #1421 reproduces after clean Chat/UI state rebuild and points to an invalid-argument layout exception. No matching control, exception stack, content fixture or same-case repair ties them together.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1414) [2](https://github.com/openclaw/openclaw-windows-node/issues/1421)

### #1714, #1568, #1779

**multi-symptom reports. Confidence: 99%.** #1714 includes consumed/invalid node bootstrap and operator pairing flow as well as native-chat freeze; a 44-event follow-up shows history size/node mode are not necessary. #1779 additionally owns missing reply delivery and orphaned SSH after forced exit; keyboard fix is partial. Cross-link the hang, but do not close either whole report on a scrolling consolidation.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1714) [2](https://github.com/openclaw/openclaw-windows-node/issues/1568) [3](https://github.com/openclaw/openclaw-windows-node/issues/1779) [4](https://github.com/openclaw/openclaw-windows-node/issues/1714#issuecomment-6255689509) [5](https://github.com/openclaw/openclaw-windows-node/issues/1779#issuecomment-6460910101)

### #1947, #2069, #2098, #1567

**same exception family is not duplicate proof. Confidence: 99%.** #1947 logs LayoutCycleException and a surviving unusable process. #2069 terminates on send in operator-only LAN/MSIX alpha. #2098 intermittently terminates after normal use on stable portable with three differing native sites and no symbolized payload. #1567 measures empty tray menu/resource growth while UI still pumps. No matching stowed-exception stack proves identity; MainWindowHandle=0 alone does not prove window destruction.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1947) [2](https://github.com/openclaw/openclaw-windows-node/issues/2069) [3](https://github.com/openclaw/openclaw-windows-node/issues/2098) [4](https://github.com/openclaw/openclaw-windows-node/issues/1567)

### #1173, #1680, #1706, #2098

**different native failure entry points. Confidence: 99%.** Rich-text mounting AV, paperclip shell-dialog fail-fast, CoreCLR startup 0x80131506, and intermittent stowed failures are separate investigations. Loaded-module names or a broad UI subsystem do not establish a common failure.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1173) [2](https://github.com/openclaw/openclaw-windows-node/issues/1680) [3](https://github.com/openclaw/openclaw-windows-node/issues/1706) [4](https://github.com/openclaw/openclaw-windows-node/issues/2098) [5](https://github.com/openclaw/openclaw-windows-node/issues/1680#issuecomment-6732362933) [6](https://github.com/openclaw/openclaw-windows-node/issues/1680#issuecomment-6756065901)

### #1756, #1758, #2097

**specific stage / broad measurement / later stage. Confidence: 99%.** #1756 is Store acquisition exceeding old 5-minute bound, with slow-deployment acceptance outstanding. #1758 intentionally tracks 44-minute stage visibility and measurement including Local AI acquisition/startup. #2097 is the later gateway health RPC 10-second deadline during finalization; the outer CLI budget is distinct. Increasing one deadline cannot close all three.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1756) [2](https://github.com/openclaw/openclaw-windows-node/issues/1758) [3](https://github.com/openclaw/openclaw-windows-node/issues/2097) [4](https://github.com/openclaw/openclaw-windows-node/issues/1756#issuecomment-6624949109) [5](https://github.com/openclaw/openclaw-windows-node/issues/1758#issuecomment-6224395169)

### #1903, #2097

**saved runtime reconnect versus setup-owned finalization. Confidence: 98%.** #1903 rejects a pre-existing native service in starting state during app cold boot and lacks observation/retry; #2097 is setup VerifyHealthAsync after install/AI selection. Different lifecycle authority and command budgets, despite both involving readiness waits.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1903) [2](https://github.com/openclaw/openclaw-windows-node/issues/2097)

### #1571, #1572, #1573, #1574, #1575

**explicit umbrella / consumer subtasks / shared prerequisite. Confidence: 100%.** Reporter explicitly keeps #1571 as the evidence umbrella. #1572 guards provenance process-start, #1573 keepalive and cancellation, #1574 reconnect authorization, and #1575 owns production shared session-ending signal. They are not competing duplicate trackers.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1571) [2](https://github.com/openclaw/openclaw-windows-node/issues/1572) [3](https://github.com/openclaw/openclaw-windows-node/issues/1573) [4](https://github.com/openclaw/openclaw-windows-node/issues/1574) [5](https://github.com/openclaw/openclaw-windows-node/issues/1575) [6](https://github.com/openclaw/openclaw-windows-node/issues/1571#issuecomment-5931969973) [7](https://github.com/openclaw/openclaw-windows-node/issues/1573#issuecomment-5932164369) [8](https://github.com/openclaw/openclaw-windows-node/issues/1574#issuecomment-5932168079)

### #2071, #2072, #2073, #2074, #2075, #2076

**explicit umbrella / five independent repair lanes. Confidence: 100%.** #2071 completion requires all five lanes merged with regressions; publication handoff is not completion. Children have different lifecycle owners and five corresponding open PRs. Do not close as duplicates of their tracking umbrella.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/2071) [2](https://github.com/openclaw/openclaw-windows-node/issues/2072) [3](https://github.com/openclaw/openclaw-windows-node/issues/2073) [4](https://github.com/openclaw/openclaw-windows-node/issues/2074) [5](https://github.com/openclaw/openclaw-windows-node/issues/2075) [6](https://github.com/openclaw/openclaw-windows-node/issues/2076) [7](https://github.com/openclaw/openclaw-windows-node/issues/2071#issuecomment-6719988339)

### #1574, #1689, #2074, #2075, #2092

**adjacent reconnect and lifecycle owners. Confidence: 99%.** #1574 suppresses work on Windows session end; #1689 diagnoses heartbeat losses and unconfirmed prepare delivery; #2074 guarantees disconnect cleanup; #2075 fences queued reconnect intent; #2092 restores killed WSL keepalive after a failed repair without violating stop/disconnect. Sharing reconnect files is not identity.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1574) [2](https://github.com/openclaw/openclaw-windows-node/issues/1689) [3](https://github.com/openclaw/openclaw-windows-node/issues/2074) [4](https://github.com/openclaw/openclaw-windows-node/issues/2075) [5](https://github.com/openclaw/openclaw-windows-node/issues/2092)

### #1462, #1823

**symptom report versus concrete partial mechanism. Confidence: 99%.** #1823 explicitly tracks dropped lifecycle terminal before reset echo, not every thinking-stuck condition. #1462 has no captured reset/event ordering and separately requires /stop control behavior rather than queueing. A lifecycle repair can be related without closing the wider report.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1462) [2](https://github.com/openclaw/openclaw-windows-node/issues/1823)

### #1493, #1840, #2035

**separate chat state contracts. Confidence: 98%.** Attachment live-echo duplication, erroneous content-fallback loss of a second identical message, and destructive clear-before-checkpoint-history arrival affect different identity/transaction contracts. Do not consolidate because all touch timeline state.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1493) [2](https://github.com/openclaw/openclaw-windows-node/issues/1840) [3](https://github.com/openclaw/openclaw-windows-node/issues/2035)

### #1292, #1330

**multi-surface DPI report versus list renderer. Confidence: 99%.** #1292 includes Config and chat clipping at 4K/300%; #1330 is native bullet text wrapping and has its own reporter non-reproduction. Fixing list layout is not full scaled Config acceptance.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1292) [2](https://github.com/openclaw/openclaw-windows-node/issues/1330) [3](https://github.com/openclaw/openclaw-windows-node/issues/1330#issuecomment-5862598488)

### #1881, #1882, #1725

**upgrade receipts / fresh setup authority / model-choice UX. Confidence: 99%.** #1881 explicitly excludes #1882: stale artifact recipe IDs and missing projector receipts differ from clean-state native Gateway ownership warning before model selection. #1725 is changing a selected local model and recovery UI, not receipt migration.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1881) [2](https://github.com/openclaw/openclaw-windows-node/issues/1882) [3](https://github.com/openclaw/openclaw-windows-node/issues/1725)

### #1956, #1957, #2040

**independent autostart registrations and operations. Confidence: 100%.** #1956 is an installer-owned Startup shortcut untouched by Settings. #1957 is setup registration missing --background and matcher mismatch. #2040 ignores failure to unregister the logon scheduled task. Same user outcome, distinct triggers and repairs; #2040 body explicitly excludes #1956.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1956) [2](https://github.com/openclaw/openclaw-windows-node/issues/1957) [3](https://github.com/openclaw/openclaw-windows-node/issues/2040)

### #2001, #2002, #2004, #2006

**deliberate independent display sinks. Confidence: 100%.** Same reusable sanitizer does not make these reports duplicates: saved list, unsaved decoded preview, tray/Local AI fallback, and web-chat failure text are separate acceptance sinks with separate contributed branches. Each body explicitly excludes the others. They may share one implementation PR, but none fully subsumes another.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/2001) [2](https://github.com/openclaw/openclaw-windows-node/issues/2002) [3](https://github.com/openclaw/openclaw-windows-node/issues/2004) [4](https://github.com/openclaw/openclaw-windows-node/issues/2006)

### #2007, #2008

**redaction versus omission contract. Confidence: 100%.** Tooltip needs safe activity label rendering; support copy promises to exclude command arguments, including ordinary nonsecret arguments. Sanitizing a label is not the support omission contract. Bodies explicitly split these surfaces.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/2007) [2](https://github.com/openclaw/openclaw-windows-node/issues/2008)

### #2009, #2041

**same script / different safety failures. Confidence: 99%.** Unowned TrustedPeople certificate persists after validation versus validator falsely passing without token/distro cleanup evidence. Trust lifecycle and postcondition truthfulness need independent regressions.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/2009) [2](https://github.com/openclaw/openclaw-windows-node/issues/2041)

### #1944, #1945, #1946

**distinct accessibility contracts. Confidence: 100%.** Chat per-message navigation/tool names, nonchat control/list/group names, and user-action announcements are independent screen-reader outcomes. Reporter explicitly retains group context and distinguishes the companion reports.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1944) [2](https://github.com/openclaw/openclaw-windows-node/issues/1945) [3](https://github.com/openclaw/openclaw-windows-node/issues/1946) [4](https://github.com/openclaw/openclaw-windows-node/issues/1945#issuecomment-6592485160) [5](https://github.com/openclaw/openclaw-windows-node/issues/1945#issuecomment-6622592314)

### #1899, #2025, #1568

**specific fixture defects / CI umbrella / product hang. Confidence: 99%.** #1899 observes placement RPC fixture mismatch and intermittent wide visibility with app still responsive, and explicitly retains latter investigation. #2025 is broader CI reliability with many unrelated failures. Neither is a duplicate of sustained product UI hang #1568.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1899) [2](https://github.com/openclaw/openclaw-windows-node/issues/2025) [3](https://github.com/openclaw/openclaw-windows-node/issues/1568) [4](https://github.com/openclaw/openclaw-windows-node/issues/1899#issuecomment-6473124185)

### #2032, #2076

**same page / distinct mutation and timing safety. Confidence: 100%.** Remove confirmation, disabled-job force run and rejected-removal state in #2032 differ from stale Run Now safety timer clearing a newer run in #2076. Child explicitly calls for coordination, not duplicate closure.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/2032) [2](https://github.com/openclaw/openclaw-windows-node/issues/2076)

### #1983, #2034, #2042

**different download owner and safety contract. Confidence: 99%.** Portable update stalled-body deadline, invalid 200 response destroying partial Local AI model, and shared audio transfer cancellation/size limits are different protocols and ownership. Similar download words cannot consolidate them.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1983) [2](https://github.com/openclaw/openclaw-windows-node/issues/2034) [3](https://github.com/openclaw/openclaw-windows-node/issues/2042)

### #1579, #1761, #1870, #1442

**provider-specific auth/activation failures. Confidence: 97%.** OpenRouter isolated browser/wizard settlement and route mismatch, ChatGPT callback refusal/unconfirmed activation, Copilot saved-profile activation, and Claude executable/auth identity are distinct observed stages/providers without matching root-cause evidence.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1579) [2](https://github.com/openclaw/openclaw-windows-node/issues/1761) [3](https://github.com/openclaw/openclaw-windows-node/issues/1870) [4](https://github.com/openclaw/openclaw-windows-node/issues/1442)

### #1691, #1704, #1901, #1714

**ownership refusal versus credential/pairing errors. Confidence: 99%.** #1691 can withhold credentials before endpoint proof; #1704 actually reaches Gateway token_mismatch; #1901 has missing operator pairing despite reachable endpoint; #1714 adds redeemed setup-code/node credential behavior. Healthy browser chat alone does not identify these as one defect.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1691) [2](https://github.com/openclaw/openclaw-windows-node/issues/1704) [3](https://github.com/openclaw/openclaw-windows-node/issues/1901) [4](https://github.com/openclaw/openclaw-windows-node/issues/1714)

### #1614, #1722, #1871, #1943

**browser provisioning versus broader capability gaps. Confidence: 99%.** #1871 includes absent computer.act and protected CUA database access plus browser helper symptoms. #1943 requests Windows-only third-party stdio MCP hosting. Neither is a duplicate of browser-helper setup, and desktop authority must not be inferred from a browser issue.

Evidence: [1](https://github.com/openclaw/openclaw-windows-node/issues/1614) [2](https://github.com/openclaw/openclaw-windows-node/issues/1722) [3](https://github.com/openclaw/openclaw-windows-node/issues/1871) [4](https://github.com/openclaw/openclaw-windows-node/issues/1943)


## Coverage, freshness and verification

- All 124 inventory issues screened. Fresh full issue-body/comment collection contains 124 unique issues and 357 comments; no added or missing issue compared with the parent snapshot.
- Fresh collection completed at **2026-11-10 19:59:41 UTC** with **OCTOPOOL_FRESH=1**. Targeted #1167/#1568 views completed by **20:00:37 UTC**, both open and unchanged; PR #1935 and #2083 current bodies/heads were also read.
- Candidate clusters received substantive body/comment reads and relevant code inspection. The rest were screened using body scope, cross-references and the parent's complete evidence-backed reviews, not claimed as a second full 124-item code audit.
- Sources inspected at **e59374c064f43d68f5c72d070d4c89ef2d20ec3b**, plus v2026.9.8-1 for the historical synchronous navigation path.
- Relevant active PR heads: #1935 **bc2f27744463fbea9b8a77ac7173e107cb01e53c**; #2083 (fix(chat): switching conversations can land on old messages or freeze the chat) **374d67bae08e54dd656eb05e74dbdf03e6e307b8**. Neither PR is treated as merged or proof-complete.
- Deterministic triage harness: **71 passed, 0 failed, 0 skipped**. Command: `node --test .github/scripts/repository-triage.test.cjs .github/extensions/openclaw-triage-dashboard/triage-state.test.mjs`.
- No source edits, commits, GitHub writes or native Windows runs. Full build/Windows tests are not applicable to this read-only classification audit; no runtime repair proof claimed.
- Fresh inputs: `duplicate-live/issues.json`, `duplicate-live/issue-1167.json`, `duplicate-live/issue-1568.json`, `duplicate-live/pr-1935.json`, `duplicate-live/pr-2083.json`; test receipt: `duplicate-live/triage-tests.log`.
- Fresh inventory SHA-256: `22fff6e103d178df1ea32e8e4ae7793ae63fd93d052bbded1f9d1dcb0c4b2f36`.
- JSON contains exact proposed comments, preservation checklist, per-cluster confidence and mutation order. Parent owns all mutations and final fresh-read checks.

**No further whole-issue duplicates are sufficiently established beyond these two pairs.**
