# Windows issue review: batch 2 of 4

Complete read-only review of all **31 assigned issues** and **86 comments**, selected from 124 open issues with 93 open upstream PRs. Source HEAD and a fresh GitHub main read both resolve to `e59374c064f43d68f5c72d070d4c89ef2d20ec3b`. All assigned bodies and all snapshot comments were read.

## Summary

| Disposition | Count |
|---|---:|
| keep-open | 16 |
| existing-pr | 7 |
| needs-info | 4 |
| close-obsolete | 1 |
| simple-fix | 3 |

- **No close-fixed recommendation.**
- **One duplicate-only closure:** #1975 into verified-open #1701. The defect remains unresolved; preserve the newer 2026.11.1 reproduction and canonical issue.
- **Best small patches without an active upstream PR:** #2002, #2006, #2010. All already have contributor commits; adopt with credit rather than duplicate.
- **Strong small existing-PR adoption:** #2034, fork PR #4 (fix: keep a partial model when a full response has the wrong length).
- **Important patch defect:** #2006’s supplied patch changes an unused private builder and misses the active invalid-URL error in `GatewayChatUrlBuilder`.

## Evidence boundaries

- Only the deterministic triage/dashboard tests ran: **71 passed, 0 failed, 0 skipped**.
- No full build, .NET tests, WinUI application, Windows runtime, MXC, hardware or live Gateway scenario was executed. This is issue triage, not merge acceptance.
- Contributor/bot test claims are distinguished from this review’s executed results. Bot labels and stale notices were not treated as completion evidence.
- Supplemental GitHub reads verified linked PR states, the canonical duplicate, three contributor commit patches and current packaging status-selection source. Some non-mutating PR reads used the shared cache; refresh mutable state before any parent mutation. Main and duplicate-canonical status were explicitly read fresh.
- No source edits, commits, comments, labels, closures, publication, native launches or provisioning were performed. Only the two requested review artifacts were written.

## Material updates since durable bot reviews

- #1576 (clawhub: embed store and install listings) is closed unmerged, not an active fix for #1216.
- #1880 (fix: clarify Store migration repair and app removal) merged and #1740 reporter recovered, but missing-payload installed repair/retry remains unverified.
- #2079 (fix: fail pending Gateway requests promptly after transport loss) is open and owns #2074.
- #2006 contributor patch misses the active GatewayChatUrlBuilder invalid-URL path.
- Current main and fresh remote main remain e59374c064f43d68f5c72d070d4c89ef2d20ec3b.

## All assigned issues

| Issue | Classification | Disposition | Confidence |
|---|---|---|---:|
| [#1000 Keep Windows Companion aligned with current OpenClaw stable and beta releases](https://github.com/openclaw/openclaw-windows-node/issues/1000) | tracker | keep-open | 98% |
| [#1216 Proposal: Add ClawHub page to the UI for easy skill/plugin installation](https://github.com/openclaw/openclaw-windows-node/issues/1216) | enhancement | keep-open | 98% |
| [#1411 [Bug]: Companion /new creates session but does not select or materialize it in chat UI](https://github.com/openclaw/openclaw-windows-node/issues/1411) | bug | keep-open | 99% |
| [#1432 Windows companion 2026.7.1-4: dashboard-triggered PowerShell/where fail with 0xc0000142; manual PowerShell succeeds](https://github.com/openclaw/openclaw-windows-node/issues/1432) | bug | keep-open | 97% |
| [#1493 [Bug]: Image attachments render twice in native chat with Gateway 2026.9.5 (local echo not reconciled)](https://github.com/openclaw/openclaw-windows-node/issues/1493) | bug | existing-pr | 99% |
| [#1550 Copying chat contents in companion app is janky](https://github.com/openclaw/openclaw-windows-node/issues/1550) | bug | existing-pr | 98% |
| [#1571 Tray spawns `wsl.exe` during session teardown, producing an "Application Error 0xc0000142" popup on every shutdown/restart](https://github.com/openclaw/openclaw-windows-node/issues/1571) | bug tracker | keep-open | 99% |
| [#1575 Provide one shared Windows session-ending signal and wire it for production use](https://github.com/openclaw/openclaw-windows-node/issues/1575) | bug prerequisite | keep-open | 99% |
| [#1673 fix: gateway remove and switch fail after setup is closed](https://github.com/openclaw/openclaw-windows-node/issues/1673) | bug | existing-pr | 99% |
| [#1679 [Enhancement] Option to show the full embedded Control UI in Windows Companion Web UI mode](https://github.com/openclaw/openclaw-windows-node/issues/1679) | enhancement | keep-open | 99% |
| [#1691 [Bug] Companion cannot connect: Gateway MSIX missing isolated-session contract](https://github.com/openclaw/openclaw-windows-node/issues/1691) | bug | keep-open | 99% |
| [#1706 OpenClaw Companion crashes on startup with .NET Runtime error 0x80131506](https://github.com/openclaw/openclaw-windows-node/issues/1706) | bug | needs-info | 97% |
| [#1740 Clicking "Install Store version and migrate" shows migration consent/Store listing error](https://github.com/openclaw/openclaw-windows-node/issues/1740) | bug | keep-open | 95% |
| [#1761 ChatGPT (Beta) sign-in is not detected after Gateway reconnect](https://github.com/openclaw/openclaw-windows-node/issues/1761) | bug | keep-open | 97% |
| [#1781 WSL setup: no window appears at all after post-setup restart (native-handoff ai-v3: path throws InvalidOperationException/Unavailable)](https://github.com/openclaw/openclaw-windows-node/issues/1781) | bug | existing-pr | 98% |
| [#1829 Gateway capability tracking](https://github.com/openclaw/openclaw-windows-node/issues/1829) | permanent operational tracker | keep-open | 100% |
| [#1846 [Bug]: camera.snap always fails with "Snap failed" while camera.clip works on the same device (v2026.9.8-1, Windows 10 22H2)](https://github.com/openclaw/openclaw-windows-node/issues/1846) | bug | needs-info | 97% |
| [#1882 Hitting an error with PR 1869](https://github.com/openclaw/openclaw-windows-node/issues/1882) | bug | needs-info | 96% |
| [#1902 [Bug] cron permanently UNAVAILABLE: stale cron-authority lock held by an orphaned Gateway process, `doctor --fix` cannot clear it](https://github.com/openclaw/openclaw-windows-node/issues/1902) | bug (Core/package ownership unresolved) | needs-info | 97% |
| [#1943 WSL Gateway cannot host Windows-only stdio MCP servers; Windows node exposes no MCP execution capability](https://github.com/openclaw/openclaw-windows-node/issues/1943) | enhancement/topology gap | keep-open | 99% |
| [#1947 Windows Hub tray crashes with unhandled LayoutCycleException — window destroyed, process stays alive in tray-only mode (unfocusable)](https://github.com/openclaw/openclaw-windows-node/issues/1947) | bug | keep-open | 98% |
| [#1975 Native Windows Gateway: browser control blocked by listener-owner verification](https://github.com/openclaw/openclaw-windows-node/issues/1975) | bug (verified duplicate) | close-obsolete | 98% |
| [#2002 Setup-code preview prints the full gateway URL](https://github.com/openclaw/openclaw-windows-node/issues/2002) | bug (local credential display) | simple-fix | 99% |
| [#2006 Chat connection errors print the raw gateway URL](https://github.com/openclaw/openclaw-windows-node/issues/2006) | bug (local credential display) | simple-fix | 99% |
| [#2010 Exec approval display keeps a braille blank in the command](https://github.com/openclaw/openclaw-windows-node/issues/2010) | bug (approval-display security) | simple-fix | 98% |
| [#2025 Track and reduce CI failures and flaky tests](https://github.com/openclaw/openclaw-windows-node/issues/2025) | tracker | keep-open | 100% |
| [#2034 A full Hugging Face response with the wrong length wipes the partial model](https://github.com/openclaw/openclaw-windows-node/issues/2034) | bug (partial download data loss) | existing-pr | 99% |
| [#2040 Turning auto-start off can succeed while the logon task remains](https://github.com/openclaw/openclaw-windows-node/issues/2040) | bug | existing-pr | 99% |
| [#2069 [Bug] Hub 2026.10.5-alpha.4 crashes on chat send (0xc000027b in CoreMessagingXP.dll) against 2026.11.1 gateway](https://github.com/openclaw/openclaw-windows-node/issues/2069) | bug | keep-open | 99% |
| [#2074 Run disconnect cleanup on every gateway connection loss](https://github.com/openclaw/openclaw-windows-node/issues/2074) | bug | existing-pr | 100% |
| [#2092 Auto-repair force-terminates the WSL distro but skips the keepalive re-arm when the restart fails](https://github.com/openclaw/openclaw-windows-node/issues/2092) | bug | keep-open | 99% |

## #1000: Keep Windows Companion aligned with current OpenClaw stable and beta releases

**Classification:** tracker. **Disposition:** `keep-open`. **Recommendation confidence:** 98%. Read body and 7 comments.

The original stale install pin is gone: GatewayInstallPolicy defaults to npm latest and channel-selector tests exist. This does not complete the broader upgrade, state-preservation and beta-acceptance commitments. The monitor merged, but monitoring is not upgrade proof. Open PR #1884 (feat: open Gateway updates from Windows tray and Settings) owns Gateway-update UI, so do not revive the rejected Windows-owned package lifecycle design.

### Verified source and references

- [`src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs:145-179`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs#L145-L179)
- [`tests/OpenClaw.SetupEngine.Tests/GatewayInstallPolicyTests.cs:34-75`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/GatewayInstallPolicyTests.cs#L34-L75)
- [`docs/TEST_COVERAGE.md:57-78`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/TEST_COVERAGE.md#L57-L78)
- https://github.com/openclaw/openclaw-windows-node/issues/1000
- https://github.com/openclaw/openclaw-windows-node/pull/1312
- https://github.com/openclaw/openclaw-windows-node/pull/1381
- https://github.com/openclaw/openclaw-windows-node/pull/1692
- https://github.com/openclaw/openclaw-windows-node/pull/1884
- https://github.com/openclaw/openclaw-windows-node/issues/1000#issuecomment-5129138623

### Exact proposed comment

> The historical install pin is addressed by #1312 (improve(setup): install latest stable Gateway from npm) and #1381 (fix(setup): complete npm latest Gateway landing). Keeping this tracker open for the remaining state-preserving upgrade and stable/beta acceptance work. The next maintainer step is to record those remaining acceptance criteria and reconcile #1884 (feat: open Gateway updates from Windows tray and Settings) with the Core-owned update boundary. Protocol monitoring in #1692 (feat: monitor upstream Gateway protocol changes nightly) is now merged, but does not by itself prove upgrades.

### Missing proof / next gate

- Explicit remaining supported upgrade/beta acceptance contract.
- Exact current release install/upgrade, retained state and node reconnect proof.

## #1216: Proposal: Add ClawHub page to the UI for easy skill/plugin installation

**Classification:** enhancement. **Disposition:** `keep-open`. **Recommendation confidence:** 98%. Read body and 4 comments.

Requested discovery/install and private installed-app recommendations are not implemented. Important correction to the durable bot comment: #1576 (clawhub: embed store and install listings) is now CLOSED UNMERGED, not an active draft. Current main has retired native Skills pages and routes supported Skills search results to Dashboard. Neither closing an unmerged draft nor removing the old page establishes that the feature proposal is obsolete.

### Verified source and references

- [`docs/ARCHITECTURE.md:147`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L147)
- [`src/OpenClaw.Tray.WinUI/Presentation/HubSearchCatalog.cs:132-146`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/HubSearchCatalog.cs#L132-L146)
- [`tests/OpenClaw.Tray.Tests/Presentation/HubSearchCatalogTests.cs:210`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/Presentation/HubSearchCatalogTests.cs#L210)
- [`tests/OpenClaw.Tray.Tests/SetupNativeHandoffTests.cs:718`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/SetupNativeHandoffTests.cs#L718)
- https://github.com/openclaw/openclaw-windows-node/issues/1216
- https://github.com/openclaw/openclaw-windows-node/pull/1576
- https://github.com/openclaw/openclaw-windows-node/pull/1576#issuecomment-6223487842

### Exact proposed comment

> Keeping this as an enhancement, not a resolved bug. #1576 (clawhub: embed store and install listings) is now closed without merging, and current main no longer has the former native Skills page. A maintainer should first decide how this proposal fits the Dashboard-owned skills surface and whether local installed-app recommendations remain a separate follow-up. The earlier stabilization pause is not evidence that the requested feature shipped or was rejected.

### Missing proof / next gate

- Current product decision for native ClawHub discovery versus Dashboard routing.
- Privacy-preserving publisher identity/matching contract and implementation.

## #1411: [Bug]: Companion /new creates session but does not select or materialize it in chat UI

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 5 comments.

Merged #1705 (improve(chat): mirror dashboard welcome and recent chats) retains pending selection, but current welcome eligibility still depends on ComposeTarget or published history. The current delayed-publication UI test asserts welcome only after publishing the thread/history. Barbara explicitly retained this issue after closing #1420 (fix(chat): keep new sessions selected and usable after /new) as superseded. No original-flow current-head proof was found.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Chat/OpenClawReactorChatRoot.cs:162-231`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/OpenClawReactorChatRoot.cs#L162-L231)
- [`tests/OpenClaw.Tray.UITests/ReactorChatLayoutProofTests.cs:346-374`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/ReactorChatLayoutProofTests.cs#L346-L374)
- [`docs/ARCHITECTURE.md:125`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L125)
- https://github.com/openclaw/openclaw-windows-node/issues/1411
- https://github.com/openclaw/openclaw-windows-node/pull/1705
- https://github.com/openclaw/openclaw-windows-node/pull/1420#issuecomment-6285190012
- https://github.com/openclaw/openclaw-windows-node/pull/1420#issuecomment-6285577466

### Exact proposed comment

> Keeping this open for the original-flow verification explicitly retained when #1420 (fix(chat): keep new sessions selected and usable after /new) was superseded by #1705 (improve(chat): mirror dashboard welcome and recent chats). The next check is an isolated current-main run: start on Main, send /new without /reset, verify the empty new conversation immediately, then verify its first message and reply stay on that exact key before delayed catalog publication. Existing selection-retention coverage does not prove that complete sequence.

### Missing proof / next gate

- Current-head native /new, unpublished empty-session usability, first-send/reply and navigation-away proof.

## #1432: Windows companion 2026.7.1-4: dashboard-triggered PowerShell/where fail with 0xc0000142; manual PowerShell succeeds

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 97%. Read body and 4 comments.

Current MXC request tests confirm Ui.Disable=false with containment retained, and the command adapter adds sanitized DLL initialization context. These remove a plausible cause but do not identify this installation’s original failed launch. The report itself lacks the raw failing request and effective launch policy, and its comments contain no affected-host successful retest. Do not repeat the recent diagnostic request or widen sandbox permissions.

### Verified source and references

- [`src/OpenClaw.Shared/Mxc/MxcCommandRunner.cs:51-64`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mxc/MxcCommandRunner.cs#L51-L64)
- [`src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs:151-165`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mxc/MxcRequestBuilder.cs#L151-L165)
- [`tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs:274-288`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/Mxc/MxcRequestBuilderTests.cs#L274-L288)
- [`tests/OpenClaw.Shared.Tests/Mxc/MxcCommandRunnerTests.cs:76-99`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/Mxc/MxcCommandRunnerTests.cs#L76-L99)
- https://github.com/openclaw/openclaw-windows-node/issues/1432
- https://github.com/openclaw/openclaw-windows-node/issues/1432#issuecomment-5697139239
- https://github.com/openclaw/openclaw-windows-node/pull/1726

### Exact proposed comment

> Keeping this open. Current source retains MXC containment while avoiding optional Win32k blocking and adding context for 0xC0000142, but that is not a verified resolution for this host. The existing diagnostic request remains the next step: correlate one failing node invocation with its effective execution mode and sanitized launch result on a current build. There is no basis here for another permission change or a separate speculative fix.

### Missing proof / next gate

- Affected-host correlated request/result and current build retest.
- Cause of the original PowerShell/where launch failure.

## #1493: [Bug]: Image attachments render twice in native chat with Gateway 2026.9.5 (local echo not reconciled)

**Classification:** bug. **Disposition:** `existing-pr`. **Recommendation confidence:** 99%. Read body and 3 comments.

Live parsing still omits the structured-media append used by history; provider projects raw text, and correlation rejects attachment-bearing local candidates without a media envelope. A current test explicitly demonstrates two rows for a no-envelope echo. Open draft #1535 (fix(chat): reconcile structured media echoes) explicitly closes this issue. Do not duplicate it or claim history-only repair completed the live path.

### Verified source and references

- [`src/OpenClaw.Shared/OpenClawGatewayClient.cs:1048-1050`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/OpenClawGatewayClient.cs#L1048-L1050)
- [`src/OpenClaw.Shared/OpenClawGatewayClient.cs:4513`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/OpenClawGatewayClient.cs#L4513)
- [`src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs:1506`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs#L1506)
- [`src/OpenClaw.Tray.WinUI/Chat/ChatAttachmentEchoCorrelation.cs:25-42`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatAttachmentEchoCorrelation.cs#L25-L42)
- [`tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs:4309-4343`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs#L4309-L4343)
- https://github.com/openclaw/openclaw-windows-node/issues/1493
- https://github.com/openclaw/openclaw-windows-node/pull/1535

### Exact proposed comment

> The live structured-media echo gap is still present on current main. #1535 (fix(chat): reconcile structured media echoes) already owns this repair and remains open, so no parallel fix is needed. Continue that PR with structured-only live frames, caption escaping, ambiguous matching and retained local-preview coverage, then show one user bubble through the real native chat path before closing this issue.

### Missing proof / next gate

- Existing PR current-base review/validation and native Gateway structured-echo proof.

## #1550: Copying chat contents in companion app is janky

**Classification:** bug. **Disposition:** `existing-pr`. **Recommendation confidence:** 98%. Read body and 2 comments.

Current Reactor Markdown options do not introduce message-wide selection; code and prose are still separately rendered controls. Open draft #1844 (fix(chat): text selection stops at Markdown block boundaries) owns cross-block selection. Whole-message Copy exists but does not fulfill excerpt selection. Do not revive the older FunctionalUI fix or call UnifiedRichText alone a complete solution.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs:820-848`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs#L820-L848)
- [`src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs:851-863`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs#L851-L863)
- [`src/OpenClaw.Tray.WinUI/Chat/ChatMarkdownPresentation.cs:13-50`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ChatMarkdownPresentation.cs#L13-L50)
- https://github.com/openclaw/openclaw-windows-node/issues/1550
- https://github.com/openclaw/openclaw-windows-node/pull/1844

### Exact proposed comment

> Keeping this with #1844 (fix(chat): text selection stops at Markdown block boundaries), which is still an open draft. Current main still renders separate Markdown selection scopes; whole-message Copy is an alternative, not a fix for selecting an excerpt. The next step is to finish the existing PR and verify mixed paragraph/list/code/table selections with mouse, keyboard, clipboard and accessibility behavior.

### Missing proof / next gate

- Production viewport interactive selection, clipboard and accessibility acceptance on the existing PR.

## #1571: Tray spawns `wsl.exe` during session teardown, producing an "Application Error 0xc0000142" popup on every shutdown/restart

**Classification:** bug tracker. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 6 comments.

Source still has no shared Windows session-ending observer; keepalive starts wsl.exe directly and reconnect admits work after backoff without an OS lifecycle signal. Reporter explicitly retained this umbrella when splitting #1572-#1575. Corrected evidence is 1-15 dialogs and includes unexplained mid-session bursts, not exactly two dialogs or an exclusively shutdown cause.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs:61-100`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs#L61-L100)
- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:1058-1074`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L1058-L1074)
- [`src/OpenClaw.Shared/WebSocketClientBase.cs:663-694`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L663-L694)
- [`docs/ARCHITECTURE.md:328`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L328)
- https://github.com/openclaw/openclaw-windows-node/issues/1571
- https://github.com/openclaw/openclaw-windows-node/issues/1571#issuecomment-5929742331
- https://github.com/openclaw/openclaw-windows-node/issues/1571#issuecomment-5931969973
- https://github.com/openclaw/openclaw-windows-node/issues/1575

### Exact proposed comment

> Keeping this as the evidence umbrella for #1572, #1573, #1574 and #1575. Current main still lacks a shared session-ending signal at the WSL launch/reconnect boundaries. The reporter’s correction matters: counts vary from 1 to 15 and some mid-session bursts remain unexplained. Complete the split zero-launch regressions, then verify filtered wsl.exe Event 26 records around real shutdowns; absence of a visible dialog alone is not success.

### Missing proof / next gate

- Shared lifecycle implementation and all three consumer repairs.
- Zero-launch regressions plus Windows shutdown/canceled-shutdown proof; separate attribution of mid-session failures.

## #1575: Provide one shared Windows session-ending signal and wire it for production use

**Classification:** bug prerequisite. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 3 comments.

No SessionEnding/WM_QUERYENDSESSION/WM_ENDSESSION/HasShutdownStarted references exist under current src or tests. AppServiceRegistration lacks the shared signal and existing AppShutdownCoordinator owns explicit shutdown ordering, not Windows lifecycle observation. This is a real prerequisite, not a duplicate of the consumer bugs or a one-line fix.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Presentation/AppServiceRegistration.cs:38-69`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Presentation/AppServiceRegistration.cs#L38-L69)
- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:1058-1074`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L1058-L1074)
- [`docs/ARCHITECTURE.md:328`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L328)
- [`tests/OpenClaw.Tray.Tests/Presentation/AppServiceRegistrationTests.cs:61`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/Presentation/AppServiceRegistrationTests.cs#L61)
- https://github.com/openclaw/openclaw-windows-node/issues/1575
- https://github.com/openclaw/openclaw-windows-node/issues/1571
- https://github.com/openclaw/openclaw-windows-node/issues/1572
- https://github.com/openclaw/openclaw-windows-node/issues/1573
- https://github.com/openclaw/openclaw-windows-node/issues/1574

### Exact proposed comment

> The shared prerequisite is still missing. Implement one injectable Shared-layer lifecycle contract and one host-owned Windows observer, registered before background keepalive/reconnect work. Define cancellation of a session-end request so normal recovery can resume, and add production-wiring and transition tests. Preserve AppShutdownCoordinator’s existing ordered-shutdown ownership and leave the three consumer repairs tracked separately.

### Missing proof / next gate

- Windows observer implementation, early production registration, lifetime disposal and canceled-session-end semantics.
- Substitution, production wiring and native notification proof.

## #1673: fix: gateway remove and switch fail after setup is closed

**Classification:** bug. **Disposition:** `existing-pr`. **Recommendation confidence:** 99%. Read body and 3 comments.

Setup still constructs a separate registry; CompleteSetupCloseAsync only awaits cleanup and drops the window reference. Registry CAS correctly rejects stale persisted authority. Open #1864 (fix: gateway remove and switch fail after setup is closed) explicitly owns reconciliation and failed-action rollback, so preserve it despite adjacent setup-settlement improvements.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Services/WindowManager.cs:968-975`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/WindowManager.cs#L968-L975)
- [`src/OpenClaw.SetupEngine.UI/Pages/NativeGatewaySetupPage.xaml.cs:280`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine.UI/Pages/NativeGatewaySetupPage.xaml.cs#L280)
- [`src/OpenClaw.Connection/GatewayRegistry.cs:311-332`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/GatewayRegistry.cs#L311-L332)
- [`tests/OpenClaw.Connection.Tests/GatewayRegistryPersistenceTests.cs:14`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/GatewayRegistryPersistenceTests.cs#L14)
- https://github.com/openclaw/openclaw-windows-node/issues/1673
- https://github.com/openclaw/openclaw-windows-node/pull/1864

### Exact proposed comment

> Current main still leaves the tray’s registry unchanged when setup closes, and the stale-save guard then rejects later gateway actions. #1864 (fix: gateway remove and switch fail after setup is closed) already owns the guarded reconciliation and rollback repair. Keep this open until that PR is validated and merged, preserving concurrent edits and saved gateway identities rather than bypassing the registry conflict check.

### Missing proof / next gate

- Current-head setup-close/remove/switch runtime proof and existing PR validation.

## #1679: [Enhancement] Option to show the full embedded Control UI in Windows Companion Web UI mode

**Classification:** enhancement. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 4 comments.

Current ChatPage still intentionally injects CSS hiding embedded navigation; UseLegacyWebChat and its resolver tests select native versus integrated web chat, not a full embedded mode. The reporter corrected the original native-sidebar diagnosis and confirmed CSS removal reveals web navigation. This remains a product choice, not a proven regression or completed feature.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs:1009`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs#L1009)
- [`src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml:125-127`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml#L125-L127)
- [`tests/OpenClaw.Tray.Tests/ChatSurfaceResolverTests.cs:23-39`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ChatSurfaceResolverTests.cs#L23-L39)
- https://github.com/openclaw/openclaw-windows-node/issues/1679
- https://github.com/openclaw/openclaw-windows-node/issues/1679#issuecomment-6058520644

### Exact proposed comment

> Keeping this as an enhancement. Current main deliberately hides the embedded web sidebar, while native navigation is working. The next step is a product decision on full embedded Control UI support and which surface owns session navigation. Simply removing the CSS is not complete acceptance: reload, back/forward navigation, authentication and an obvious return to the native interface still need proof.

### Missing proof / next gate

- Product decision and full embedded navigation/lifecycle acceptance.

## #1691: [Bug] Companion cannot connect: Gateway MSIX missing isolated-session contract

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 8 comments.

Latest human report on November 10 still reproduces on Gateway 2026.11.100.0 and Companion 2026.11.1/Store 2026.11.102.0. Current Companion requires gateway.port for running status. Independently read current packaging Program.cs: it still selects port only for exactly one observed listener, otherwise null (blob d9fd53f295d636cc8b9568d432bf1fc4cf9b42d0). Prior local recovery and merged stale-session repair do not fix every cause. No more version-only request is needed.

### Verified source and references

- [`src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs:189-222`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/NativeGateway/NativeGatewayPackageClient.cs#L189-L222)
- [`tests/OpenClaw.Connection.Tests/NativeGatewayPackageClientTests.cs:329-333`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/NativeGatewayPackageClientTests.cs#L329-L333)
- https://github.com/openclaw/openclaw-windows-node/issues/1691
- https://github.com/openclaw/openclaw-windows-node/issues/1691#issuecomment-6765579570
- https://github.com/openclaw/openclaw-windows-node/issues/1691#issuecomment-6306087257
- https://github.com/openclaw/openclaw-windows-packaging/blob/main/src/OpenClaw.Launcher/Program.cs#L1095-L1097
- https://github.com/openclaw/openclaw-windows-node/issues/2020

### Exact proposed comment

> The November 10 report supplies current package versions and confirms that running status still omits gateway.port despite valid integration metadata. Keeping this open. The next engineering step belongs to the package status producer: preserve a configured port only when fresh listener ownership confirms it, test the multi-listener case, and verify a data-preserving official upgrade plus Companion reconnect. Companion’s missing-port diagnostic should also distinguish this from an unsupported integration version. Do not bypass ownership checks or reset Gateway data.

### Missing proof / next gate

- Official packaging fix and multi-listener regression.
- Data-preserving supported upgrade, fresh ownership and chat reconnect on the affected topology.

## #1706: OpenClaw Companion crashes on startup with .NET Runtime error 0x80131506

**Classification:** bug. **Disposition:** `needs-info`. **Recommendation confidence:** 97%. Read body and 3 comments.

Reported Windows 11 22000.194 is not excluded by current README. Release packaging is self-contained, so installing a different system Desktop Runtime is not proof of repair. The fatal CoreCLR 0x80131506 lacks a native stack/loaded module paths; normal startup UI tests and Debugger.IsSupported=false do not establish this crash resolved. Reporter explicitly requested guided diagnostic steps rather than another vague log request.

### Verified source and references

- [`README.md:23`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/README.md#L23)
- [`src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj:46`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj#L46)
- [`.github/workflows/ci.yml:1460`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/.github/workflows/ci.yml#L1460)
- [`tests/OpenClaw.Tray.UITests/StartupWindowUiTests.cs:13`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/StartupWindowUiTests.cs#L13)
- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:405-409`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L405-L409)
- https://github.com/openclaw/openclaw-windows-node/issues/1706
- https://github.com/openclaw/openclaw-windows-node/issues/1706#issuecomment-6123314913

### Exact proposed comment

> This is still unresolved, and reinstalling the system .NET runtime is not a confirmed fix for the self-contained Companion package. You already supplied the CoreCLR version and exception code. For the next useful detail: reproduce one launch, open Start > Event Viewer > Windows Logs > Application, and look for an Application Error entry at the same time as the .NET Runtime entry. Please report whether that adjacent entry exists and its fault offset, omitting computer/user names and personal path prefixes. A maintainer can then arrange private native-stack collection if needed. Do not post a full crash dump publicly.

### Missing proof / next gate

- Actual loaded CoreCLR/native exception stack on the reported OS and package.
- Current supported-package retest or verified targeted repair.

## #1740: Clicking "Install Store version and migrate" shows migration consent/Store listing error

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 95%. Read body and 4 comments.

Material update: #1880 (fix: clarify Store migration repair and app removal) is merged, and original reporter junyi96 states on November 4 that migration became possible after opening the Store app. Current source maps missing support files to repair-specific guidance and an official installer link. However the underlying missing-payload installation still deliberately fails admission, and the merged PR explicitly leaves candidate installed repair/retry unverified. Do not call the whole upgrade defect fixed solely from a workaround or old bot comment.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Helpers/InnoMigrationHandoff.cs:50-92`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Helpers/InnoMigrationHandoff.cs#L50-L92)
- [`src/OpenClaw.Connection/Migration/InnoInstallationDetector.cs:111-124`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/Migration/InnoInstallationDetector.cs#L111-L124)
- [`tests/OpenClaw.Connection.Tests/InnoInstallationDetectorTests.cs:256-265`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/InnoInstallationDetectorTests.cs#L256-L265)
- https://github.com/openclaw/openclaw-windows-node/issues/1740
- https://github.com/openclaw/openclaw-windows-node/pull/1880
- https://github.com/openclaw/openclaw-windows-node/issues/1740#issuecomment-6625141308

### Exact proposed comment

> Thanks for confirming that migration became available after opening the Store app. The recovery guidance from #1880 (fix: clarify Store migration repair and app removal) has also merged; it now distinguishes missing installation files and offers the official Setup repair link. Keeping the engineering follow-up open until a supported missing-payload installation is repaired and migration retried with data preserved. The reporter’s recovered case and the remaining upgrade/repair proof should not be conflated.

### Missing proof / next gate

- Installed missing-script repair followed by migration retry on current candidate, preserving existing data.
- Cause of the original update transition losing migration payload files.

**Conditional closure boundary:** Could close the individual support incident as reporter-recovered only if maintainers explicitly scope remaining installer work to another verified tracker. No such complete transfer was verified here.

## #1761: ChatGPT (Beta) sign-in is not detected after Gateway reconnect

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 97%. Read body and 2 comments.

Current GatewayAiSetupClient correctly reconciles an owned uncertain operation without replaying activation and verifies exact routes after reconnect. Tests prove those protocol safeguards, not a localhost OAuth callback or model availability on this host. The issue has no successful login/model retest; actual Gateway topology and callback port remain missing. Avoid duplicating the November 1 diagnostic request or removing reconciliation safety.

### Verified source and references

- [`src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs:251-267`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs#L251-L267)
- [`tests/OpenClaw.SetupEngine.Tests/GatewayAiSetupClientTests.cs:286-314`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/GatewayAiSetupClientTests.cs#L286-L314)
- https://github.com/openclaw/openclaw-windows-node/issues/1761
- https://github.com/openclaw/openclaw-windows-node/issues/1761#issuecomment-6232031335

### Exact proposed comment

> Keeping this open. Reconnect and Recheck do not establish that the browser callback completed or that the exact model route is usable. The existing request for the actual Gateway runtime/topology and refused callback host/port remains the next diagnostic step; OAuth query values and codes must stay private. Once that boundary is identified, repair the failed callback or verification owner rather than starting another activation while the first result is uncertain.

### Missing proof / next gate

- Actual Gateway runtime/topology, callback host/port and sanitized failed-stage evidence.
- Successful exact-model verification and first chat after sign-in.

**Posting guidance:** No new diagnostic request needed; use only if adding a consolidated triage update.

## #1781: WSL setup: no window appears at all after post-setup restart (native-handoff ai-v3: path throws InvalidOperationException/Unavailable)

**Classification:** bug. **Disposition:** `existing-pr`. **Recommendation confidence:** 98%. Read body and 2 comments.

#1626 (fix(onboarding): finish setup before showing AI as ready) merged model-recovery-before-WSL-borrow plus visible progress/recovery, verified in current source and ordering tests. #1789 (fix(setup): open chat after WSL Local AI restart) remains an open draft closing this issue. The exact clean WSL Local AI setup, post-restart automatic Chat and first-reply scenario remains unproved; do not duplicate the implementation or close from source-only recovery ordering.

### Verified source and references

- [`src/OpenClaw.SetupEngine/SetupNativeCompletionVerifier.cs:41-66`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/SetupNativeCompletionVerifier.cs#L41-L66)
- [`tests/OpenClaw.SetupEngine.Tests/SetupGatewaySessionBindingTests.cs:79`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/SetupGatewaySessionBindingTests.cs#L79)
- [`tests/OpenClaw.Tray.Tests/NativeCompletionPresentationTests.cs:8-25`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/NativeCompletionPresentationTests.cs#L8-L25)
- https://github.com/openclaw/openclaw-windows-node/issues/1781
- https://github.com/openclaw/openclaw-windows-node/pull/1626
- https://github.com/openclaw/openclaw-windows-node/pull/1789

### Exact proposed comment

> Current main includes recovery-before-verification and early progress from #1626 (fix(onboarding): finish setup before showing AI as ready). #1789 (fix(setup): open chat after WSL Local AI restart) is still open and should be reconciled with that landing rather than duplicated. Keep this issue until an isolated managed WSL Local AI setup proves restart, automatic usable Chat and first reply, including a visible Retry/Fix path if recovery fails.

### Missing proof / next gate

- Exact current-head clean WSL Local AI setup-to-Chat and first-reply proof.
- Reconcile the still-open overlapping PR with merged onboarding code.

## #1829: Gateway capability tracking

**Classification:** permanent operational tracker. **Disposition:** `keep-open`. **Recommendation confidence:** 100%. Read body and 3 comments.

This is required durable automation state, not an actionable standalone bug. getTracker rejects a closed or markerless #1829; documentation and regressions explicitly prohibit replacement. The tracker reports collection failure and a completed existing-work investigation, neither of which permits closure or resets reservations. Current monitor reporting PR #1850 (improve: explain Gateway monitor assessment coverage) is not a replacement tracker.

### Verified source and references

- [`.github/scripts/upstream-gateway-publish.cjs:126-150`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/.github/scripts/upstream-gateway-publish.cjs#L126-L150)
- [`.github/scripts/upstream-gateway-protocol.test.cjs:604-651`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/.github/scripts/upstream-gateway-protocol.test.cjs#L604-L651)
- [`docs/gateway-protocol-drift-guard.md:285-299`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/gateway-protocol-drift-guard.md#L285-L299)
- https://github.com/openclaw/openclaw-windows-node/issues/1829
- https://github.com/openclaw/openclaw-windows-node/actions/runs/42868352824
- https://github.com/openclaw/openclaw-windows-node/pull/1850

### Exact proposed comment

> Retain this permanent tracker. The publisher requires #1829 to remain open with its existing markers and durable state. The next step is to diagnose the recorded collection failure and repair the failing observation inputs; do not close, replace or clear reservations on the tracker. A completed capability investigation is not a claim of released Gateway compatibility.

### Missing proof / next gate

- Successful current observation after diagnosing protocol/welcome collection failures.

**Posting guidance:** Normally no comment: preserve the existing durable tracker and avoid repeated unchanged observations.

## #1846: [Bug]: camera.snap always fails with "Snap failed" while camera.clip works on the same device (v2026.9.8-1, Windows 10 22H2)

**Classification:** bug. **Disposition:** `needs-info`. **Recommendation confidence:** 97%. Read body and 3 comments.

Source confirms CPU MediaCapture initialization precedes all snapshot fallback paths and can throw past them, whereas clip uses a different configuration. It does not prove this device fails at initialization or that Auto solves it. Capability error sanitization is intentional and tested. No open snapshot repair PR in the complete upstream inventory. Need narrow device-stage evidence, not raw exception/log export.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Services/CameraCaptureService.cs:65-136`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/CameraCaptureService.cs#L65-L136)
- [`src/OpenClaw.Shared/Capabilities/CameraCapability.cs:124-132`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Capabilities/CameraCapability.cs#L124-L132)
- [`tests/OpenClaw.Shared.Tests/CapabilityTests.cs:3216-3224`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/CapabilityTests.cs#L3216-L3224)
- https://github.com/openclaw/openclaw-windows-node/issues/1846
- https://github.com/openclaw/openclaw-windows-node/issues/1846#issuecomment-6357849491
- https://github.com/openclaw/openclaw/issues/181348

### Exact proposed comment

> The snapshot fallback gap is present, but the CPU-initialization explanation is still a hypothesis. For one failed snap, please report only the hexadecimal HRESULT from the local camera.snap failure line and whether “MediaCapture initialized” and “CapturePhotoToStreamAsync completed” appeared for that attempt. Do not paste raw exception text, device IDs or a log tail. That distinguishes initialization, photo capture and encoding before selecting a bounded fallback; permission failures and cancellation must not trigger retries.

### Missing proof / next gate

- Affected camera capture stage and HRESULT.
- Targeted recovery regression and hardware snap/list/invoke proof, preserving consent and sanitization.

## #1882: Hitting an error with PR 1869

**Classification:** bug. **Disposition:** `needs-info`. **Recommendation confidence:** 96%. Read body and 2 comments.

Current SetupLocalAiHost rejects native review when a managed runtime or unresolved route remains; AiSetupPage normally renders the retained observation after rejection, so the reported warning path remains. HealthyOwnedRuntimeBlocksFileRepairUntilStopped protects the safety condition. The claimed clean-state trigger needs exact commit and isolated-profile evidence; #1771 (feat(local-ai): reset an unusable Local AI install without uninstalling) reset and #1979 (improve(setup): clarify local AI setup choices) setup presentation are adjacent, not proven fixes for it.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Services/SetupLocalAiHost.cs:174-188`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/SetupLocalAiHost.cs#L174-L188)
- [`src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml.cs:1429-1449`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml.cs#L1429-L1449)
- [`tests/OpenClaw.SetupEngine.Tests/LocalAiOnboardingTests.cs:457`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/LocalAiOnboardingTests.cs#L457)
- https://github.com/openclaw/openclaw-windows-node/issues/1882
- https://github.com/openclaw/openclaw-windows-node/issues/1883
- https://github.com/openclaw/openclaw-windows-node/pull/1771
- https://github.com/openclaw/openclaw-windows-node/pull/1979

### Exact proposed comment

> Keeping this as the Local AI ownership-warning report, separate from the Bolt receipt issue. The report identifies the branch being tested and the launch command, but not the exact compiled commit. Which commit SHA produced the screenshot, and was a model process already running before the isolated setup began? Please provide those facts without private paths or receipt contents. The next regression should reproduce rejected review with changed ownership and refresh the available actions, without deleting ownership evidence or bypassing Stop.

### Missing proof / next gate

- Exact tested source/build and isolation scope.
- Controlled clean-state or stale-observation reproduction with ownership preserved.

## #1902: [Bug] cron permanently UNAVAILABLE: stale cron-authority lock held by an orphaned Gateway process, `doctor --fix` cannot clear it

**Classification:** bug (Core/package ownership unresolved). **Disposition:** `needs-info`. **Recommendation confidence:** 97%. Read body and 1 comments.

Companion delegates cron to Gateway RPC and native service lifetime to the package; it intentionally does not stop released/pre-existing services. The report alternates between PID reuse and a still-running old Gateway, which are different defects. No source or PR evidence establishes a safe lock reclamation fix, and deleting a lock for a living owner could create competing writers.

### Verified source and references

- [`src/OpenClaw.Shared/OpenClawGatewayClient.cs:1951-1959`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/OpenClawGatewayClient.cs#L1951-L1959)
- [`tests/OpenClaw.Connection.Tests/IsolatedGatewayRuntimeTests.cs:863-887`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Connection.Tests/IsolatedGatewayRuntimeTests.cs#L863-L887)
- [`docs/ARCHITECTURE.md:207-208`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/ARCHITECTURE.md#L207-L208)
- https://github.com/openclaw/openclaw-windows-node/issues/1902

### Exact proposed comment

> The outage is valid, but the repair owner is not yet established. Can the lock’s recorded starttime be compared with the actual start time and executable type of that PID, and can you identify the installed Gateway package version? Share only the comparison result and public package version, not the lock file or raw process dump. A live old Gateway and a recycled unrelated PID require different fixes. Keep this open; Companion should not delete authority locks or kill a process solely because it no longer owns the listening port.

### Missing proof / next gate

- Lock-owner identity/start-time comparison and package supervisor ancestry across restart.
- Separate reproductions for startup-preparation race and Doctor hang.

## #1943: WSL Gateway cannot host Windows-only stdio MCP servers; Windows node exposes no MCP execution capability

**Classification:** enhancement/topology gap. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 1 comments.

Local MCP serves the registered built-in node capabilities, not third-party stdio hosting. Managed WSL explicitly disables interop/automount. Native Gateway runs under a different identity/session, so its ability to access interactive Photoshop COM is not established merely by being Windows-native. This needs a product/transport contract, not an arbitrary command launcher or WSL isolation bypass.

### Verified source and references

- [`docs/MCP_MODE.md:9-44`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/MCP_MODE.md#L9-L44)
- [`src/OpenClaw.Shared/Mcp/McpToolBridge.cs:491-520`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Mcp/McpToolBridge.cs#L491-L520)
- [`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:308-345`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/NodeService.cs#L308-L345)
- [`src/OpenClaw.SetupEngine/default-config.json:63-65`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/default-config.json#L63-L65)
- https://github.com/openclaw/openclaw-windows-node/issues/1943
- https://github.com/openclaw/openclaw-windows-node/issues/1717

### Exact proposed comment

> Keeping this as a supported-topology enhancement. The current local MCP server exposes Windows node capabilities; it does not host arbitrary third-party stdio servers. First validate Photoshop discovery and invocation under the intended Windows identity and desktop session, then decide whether native Gateway hosting or a supported authenticated bridge is the right owner. Enabling WSL interop or adding an unrestricted node launcher would not safely resolve this contract.

### Missing proof / next gate

- Accepted host identity/session and Gateway-to-node contract if needed.
- Real Photoshop initialize/tools-list/tools-call proof plus approval/lifecycle boundaries.

## #1947: Windows Hub tray crashes with unhandled LayoutCycleException — window destroyed, process stays alive in tray-only mode (unfocusable)

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 98%. Read body and 1 comments.

The report documents repeated unusable native UI, but MainWindowHandle=0 alone does not prove a destroyed WinUI window, and no full stack identifies the owner. Current code already virtualizes, has a global handled-XAML-exception hook and now avoids the earlier synchronous item-bring path; docs separately record an unverified native WinUI layout-cycle mechanism. These changes and open scrolling PRs are not proof of this report’s resolution.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:405-409`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L405-L409)
- [`src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs:210-257`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs#L210-L257)
- [`src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749-779`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749-L779)
- [`docs/CI_FAILURES.md:11-18`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/CI_FAILURES.md#L11-L18)
- [`tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs:11`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.UITests/GatewayFixtureReactorProofTests.cs#L11)
- https://github.com/openclaw/openclaw-windows-node/issues/1947
- https://github.com/openclaw/openclaw-windows-node/pull/1549
- https://github.com/openclaw/openclaw-windows-node/pull/1935
- https://github.com/openclaw/openclaw-windows-node/pull/2083

### Exact proposed comment

> Keeping this report open rather than treating related scrolling work as a verified fix. Current main already has virtualization and a global XAML exception handler; suppressing another exception would not prove a usable visual tree. The outstanding diagnostic step is to identify the Companion build and the actual exception stack/content shape, then match it to a failing native regression. The newly documented native WinUI layout-cycle investigation is relevant, but payload size and a similar error name do not establish a duplicate root cause.

### Missing proof / next gate

- Companion package version and sanitized exception stack/triggering structural fixture.
- Current-head reproduction and visible native recovery.

**Posting guidance:** Avoid reposting the existing diagnostic request; use only to clarify that related changes are not closure proof.

## #1975: Native Windows Gateway: browser control blocked by listener-owner verification

**Classification:** bug (verified duplicate). **Disposition:** `close-obsolete`. **Recommendation confidence:** 98%. Read body and 3 comments.

Duplicate consolidation only, NOT fixed. Read canonical #1701 body and human follow-up: same native non-SSH topology, same exact pre-HTTP browser ownership failure, and same missing native branch. Current App sends a native record with no WSL distro to fixture-only ownership, which fails in production. Fresh API reads confirm both #1701 and #1975 remain open; retain #1701 and the newer 2026.11.1 reproduction from #1975. This is distinct from #1691 Gateway-port status failure.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:2497-2514`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L2497-L2514)
- [`src/OpenClaw.Connection/FixtureLoopbackListenerOwner.cs:22`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/FixtureLoopbackListenerOwner.cs#L22)
- [`src/OpenClaw.Connection/GatewayRecord.cs:195-198`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/GatewayRecord.cs#L195-L198)
- [`src/OpenClaw.Shared/Capabilities/BrowserProxyCapability.cs:73-84`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/Capabilities/BrowserProxyCapability.cs#L73-L84)
- [`tests/OpenClaw.Shared.Tests/CapabilityTests.cs:966-985`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/CapabilityTests.cs#L966-L985)
- https://github.com/openclaw/openclaw-windows-node/issues/1975
- https://github.com/openclaw/openclaw-windows-node/issues/1701
- https://github.com/openclaw/openclaw-windows-node/issues/1701#issuecomment-6453537396
- https://github.com/openclaw/openclaw-windows-node/issues/1975#issuecomment-6730900436

### Exact proposed comment

> Consolidating this report into #1701 (Native Gateway: browser status/profiles blocked by listener ownership verification). Both reports hit the same production gap: a non-SSH Native Gateway has no managed WSL distro, and the browser-listener callback falls back to a fixture-only check before sending HTTP credentials. This is not fixed. Your November 9 confirmation on Companion/CLI 2026.11.1 remains important evidence: https://github.com/openclaw/openclaw-windows-node/issues/1975#issuecomment-6730900436. Keeping #1701 open for native browser-port attestation and status/profiles proof, including rejection of foreign/replaced listeners. No ownership checks should be disabled.

### Missing proof / next gate

- No additional runtime proof is required for duplicate-only consolidation; canonical #1701 must remain open.
- Actual repair still requires native browser-listener attestation, supported account topology and real invocation proof.

**Closure reason:** Duplicate of verified-open #1701; close as not planned/duplicate, never completed/fixed.

**Before mutation:** Refresh both issues; keep #1701 open and preserve/link the November 9 reproduction when closing #1975.

## #2002: Setup-code preview prints the full gateway URL

**Classification:** bug (local credential display). **Disposition:** `simple-fix`. **Recommendation confidence:** 99%. Read body and 2 comments.

Both current setup-code handlers still render decoded.Url directly. Existing SanitizeForDisplay removes userinfo/query/fragment while preserving path. No active upstream PR in the full inventory; contributor commit 9957449 already implements the two display substitutions and a source contract test. Adopt/credit that existing patch instead of independently duplicating it, and strengthen behavioral coverage.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Windows/ConnectionStatusWindow.xaml.cs:311-324`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Windows/ConnectionStatusWindow.xaml.cs#L311-L324)
- [`src/OpenClaw.Tray.WinUI/Pages/ConnectionPage.xaml.cs:3045-3055`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ConnectionPage.xaml.cs#L3045-L3055)
- [`src/OpenClaw.Shared/GatewayUrlHelper.cs:79-92`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/GatewayUrlHelper.cs#L79-L92)
- [`tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs:130-150`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs#L130-L150)
- https://github.com/openclaw/openclaw-windows-node/issues/2002
- https://github.com/openclaw/openclaw-windows-node/issues/2002#issuecomment-6642814797
- https://github.com/SebTardif/openclaw-windows-node/commit/99574492f15bcc5144e1f663b1c5c7d3a19daf14

### Exact proposed comment

> Confirmed in current source: both setup-code previews still display the original URL. There is already a focused contributor patch at 99574492f15bcc5144e1f663b1c5c7d3a19daf14, so the next step is upstream adoption with credit, not a parallel rewrite. Apply SanitizeForDisplay only to preview text in both handlers, preserve the original connection target and token truncation, and verify userinfo/query/fragment are absent while /path remains visible.

### Missing proof / next gate

- Behavioral preview regression and visible Windows preview using synthetic secrets.
- Required full build, Shared/Tray suites and focused Connection coverage not run for this review.

### Bounded implementation handoff

- **Change:** Adopt contributor 9957449: sanitize decoded.Url in ConnectionStatusWindow.OnSetupCodeChanged and ConnectionPage.OnSetupCodeTextChanged only. Keep localized fallback, connectivity input, stored URL and token hint unchanged.
- **Regression:** Decode a synthetic URL containing userinfo, /path, token query and fragment; assert both previews contain the sanitized /path URL and no secret components, while connection target is unchanged. Existing contributor test is source-only; add behavioral or mounted-owner coverage.
- **Validation:** ./build.ps1; Shared and Tray required suites; focused SetupCodeDecoder/GatewayUrlHelper and preview tests; current-head isolated Windows preview proof; security-aware/rubber-duck review.

## #2006: Chat connection errors print the raw gateway URL

**Classification:** bug (local credential display). **Disposition:** `simple-fix`. **Recommendation confidence:** 99%. Read body and 2 comments.

Current ChatPage connection-failure text and active GatewayChatUrlBuilder invalid-input error both expose raw URLs. Contributor commit 490acc6 patches ChatPage’s active failure text plus an UNUSED private TryBuildChatUrl, but does not patch active GatewayChatUrlBuilder. Its source-only test would therefore pass while the active invalid-input path remains unsafe. No upstream active fix found. Small adoption/repair candidate with explicit owner correction.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs:973`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs#L973)
- [`src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs:1022`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs#L1022)
- [`src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs:1187`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml.cs#L1187)
- [`src/OpenClaw.Tray.WinUI/Helpers/GatewayChatUrlBuilder.cs:26-30`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Helpers/GatewayChatUrlBuilder.cs#L26-L30)
- [`src/OpenClaw.Shared/GatewayUrlHelper.cs:84-92`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/GatewayUrlHelper.cs#L84-L92)
- [`tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs:140-150`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/GatewayUrlHelperTests.cs#L140-L150)
- https://github.com/openclaw/openclaw-windows-node/issues/2006
- https://github.com/openclaw/openclaw-windows-node/issues/2006#issuecomment-6642816511
- https://github.com/SebTardif/openclaw-windows-node/commit/490acc6e19ca796a35ac2d07abe4393a9039f66d

### Exact proposed comment

> Confirmed, with one important owner correction: ChatPage currently delegates URL construction through GatewayChatHelper to GatewayChatUrlBuilder. The supplied 490acc6e19ca796a35ac2d07abe4393a9039f66d patch fixes the connection-failure text but sanitizes the unused private builder, leaving the active invalid-URL error unchanged. Adopt the useful part with contributor credit, sanitize GatewayChatUrlBuilder’s failure text too, and add a behavioral invalid-input regression rather than relying only on source-string assertions.

### Missing proof / next gate

- Active builder regression with malformed secret-bearing URL plus failed-navigation display proof.
- Required local build/suites and security-aware review.

### Bounded implementation handoff

- **Change:** Sanitize credential.GatewayUrl in ChatPage_CannotConnectToGateway formatting and gatewayUrl in GatewayChatUrlBuilder’s active invalid-input error. Preserve navigation/stored URLs. Credit/adapt contributor 490acc6 rather than claiming a new independent fix; do not stop at the unused private builder.
- **Regression:** Call the actual GatewayChatUrlBuilder with malformed URL containing synthetic query/fragment/userinfo and assert error excludes secret values. Exercise active NavigationCompleted connection error with the sanitized display. Verify normal URL construction unchanged.
- **Validation:** ./build.ps1; Shared and Tray suites; focused GatewayChatUrlBuilder/GatewayUrlHelper and chat-error tests; isolated Windows WebView failed-navigation proof; security/rubber-duck review.

## #2010: Exec approval display keeps a braille blank in the command

**Classification:** bug (approval-display security). **Disposition:** `simple-fix`. **Recommendation confidence:** 98%. Read body and 2 comments.

Current IsInvisible explicit set still excludes U+2800; its Unicode category is not caught by the general categories. Existing invisible tests cover Hangul fillers and other controls but not braille blank. Contributor commit 68f6829 adds the exact constant and one test, with no active upstream PR found. Current sanitizer has evolved command-review layout behavior, and shared stripped-redaction semantics need coverage; raw execution payload must remain unchanged.

### Verified source and references

- [`src/OpenClaw.Shared/ExecApprovals/ExecApprovalCommandDisplaySanitizer.cs:285-302`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/ExecApprovals/ExecApprovalCommandDisplaySanitizer.cs#L285-L302)
- [`tests/OpenClaw.Shared.Tests/ExecApprovalCommandDisplaySanitizerTests.cs:13-20`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/ExecApprovalCommandDisplaySanitizerTests.cs#L13-L20)
- [`src/OpenClaw.Shared/ExecApprovals/ExecApprovalV2UiPromptHandler.cs:49`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/ExecApprovals/ExecApprovalV2UiPromptHandler.cs#L49)
- [`src/OpenClaw.Tray.WinUI/Dialogs/ExecApprovalDialog.cs:138`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Dialogs/ExecApprovalDialog.cs#L138)
- https://github.com/openclaw/openclaw-windows-node/issues/2010
- https://github.com/openclaw/openclaw-windows-node/issues/2010#issuecomment-6642818680
- https://github.com/SebTardif/openclaw-windows-node/commit/68f682996f315a705e04866299c8954d6c5d0781

### Exact proposed comment

> U+2800 still survives the current invisible-character set. The contributor’s 68f682996f315a705e04866299c8954d6c5d0781 patch is the right small starting point: add it to the explicit invisible set and render the visible code-point escape, without changing the command that executes. Before publication, cover both sanitizer/presenter display and the shared stripped-view secret-redaction behavior, then verify the approval prompt with a synthetic command. This is a display-hardening fix, not evidence of an execution-policy bypass.

### Missing proof / next gate

- Current sanitizer/presenter and redaction regressions; actual prompt rendering.
- Required validation and security review.

### Bounded implementation handoff

- **Change:** Adopt contributor one-code-point addition (0x2800) to IsInvisible’s explicit set, preserving current commandReviewLayout behavior and unchanged execution input.
- **Regression:** Assert visible \u{2800} for both general sanitizer and command-review presenter, unchanged legitimate Unicode and raw command, and no secret leakage when U+2800 splits a redaction token.
- **Validation:** ./build.ps1; full Shared and Tray suites; ExecApprovalCommandDisplaySanitizer/ExecApprovalV2UiPromptHandler/secret redaction tests; current-head Windows approval screenshot or copied UI diagnostics; security/rubber-duck review. No real command execution needed for display proof.

## #2025: Track and reduce CI failures and flaky tests

**Classification:** tracker. **Disposition:** `keep-open`. **Recommendation confidence:** 100%. Read body and 1 comments.

Current CI_FAILURES.md retains multiple independent unresolved failures and adds a native animated-layout-cycle investigation; some mitigations are landed but await hosted confirmation. #2019 (fix: CI fails intermittently on loaded runners, and oversized A2UI SVGs freeze the UI) remains open and owns only selected stabilization/SVG work. A single green rerun cannot establish every issue fixed, and widening assertions would mask product stalls.

### Verified source and references

- [`docs/CI_FAILURES.md:1-18`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/CI_FAILURES.md#L1-L18)
- [`docs/CI_FAILURES.md:30-75`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/CI_FAILURES.md#L30-L75)
- [`docs/TEST_COVERAGE.md:30-49`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/TEST_COVERAGE.md#L30-L49)
- https://github.com/openclaw/openclaw-windows-node/issues/2025
- https://github.com/openclaw/openclaw-windows-node/pull/2019
- https://github.com/openclaw/openclaw-windows-node/pull/1981
- https://github.com/openclaw/openclaw-windows-node/actions/runs/42684502858

### Exact proposed comment

> Retain this umbrella. The current ledger still contains distinct startup, flyout, teardown, native layout and setup failures; some entries now have narrower attributions or mitigations rather than completed fixes. Reconcile each row with its actual landing and passing CI evidence, and keep #2019 (fix: CI fails intermittently on loaded runners, and oversized A2UI SVGs freeze the UI) scoped to its owned subset. Do not close the tracker or widen assertions based on one successful rerun.

### Missing proof / next gate

- Per-entry exact-head reproduction or explicit disproval, landed fix and passing CI rerun.

## #2034: A full Hugging Face response with the wrong length wipes the partial model

**Classification:** bug (partial download data loss). **Disposition:** `existing-pr`. **Recommendation confidence:** 99%. Read body and 1 comments.

Current full-response branch truncates existing partial bytes at line 531 before the declared length check at 535. Existing tests cover a valid 200 restart and invalid 206 preservation, not invalid 200. Contributor fork PR #4 is open and its body reports a fail-before/pass-after byte-preservation probe. It is a strong small upstream-adoption candidate, but not unowned work and not merged upstream.

### Verified source and references

- [`src/OpenClaw.SetupEngine/HuggingFaceModelInstaller.cs:507-539`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.SetupEngine/HuggingFaceModelInstaller.cs#L507-L539)
- [`tests/OpenClaw.SetupEngine.Tests/LocalAiInstallRecoveryTests.cs:57-113`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.SetupEngine.Tests/LocalAiInstallRecoveryTests.cs#L57-L113)
- https://github.com/openclaw/openclaw-windows-node/issues/2034
- https://github.com/SebTardif/openclaw-windows-node/pull/4
- https://github.com/SebTardif/openclaw-windows-node/commit/37a748e18795d4f9966bbf52280a385da212f538

### Exact proposed comment

> The truncate-before-length-check defect remains on current main. SebTardif/openclaw-windows-node#4 (fix: keep a partial model when a full response has the wrong length) already supplies a focused repair, so review that for upstream adoption rather than create a competing fix. Validate the declared full-response length before truncating/resetting the hash, and prove wrong-length 200 preserves bytes while valid 200 restart and 206 resume still work. Keep this issue open until the upstream repair lands.

### Missing proof / next gate

- Upstream adoption and fail-before/pass-after current-base regression.
- Required build, Shared/Tray and focused SetupEngine suites.

### Bounded implementation handoff

- **Change:** Adopt existing fork PR, calculate expected body length based on append mode and validate declared Content-Length before non-append truncation/hash reset.
- **Regression:** Seed verified-model[..4], reply HTTP 200 Content-Length 2; assert exception and unchanged four partial bytes. Retain valid-200 and 206-resume controls.
- **Validation:** Required build and Shared/Tray suites plus LocalAiInstallRecoveryTests/SetupEngine; no live Hugging Face download needed to prove header ordering. Existing fork proof is contributor-reported, not executed by this review.

## #2040: Turning auto-start off can succeed while the logon task remains

**Classification:** bug. **Disposition:** `existing-pr`. **Recommendation confidence:** 99%. Read body and 1 comments.

AutoStartManager still ignores Unregister false and swallows exceptions before logging success; required preference rollback therefore cannot see failure. Fork PR #8 is open. Its Exists-based repair needs strengthening because Exists conflates absence and query failure, while current Shared exposes typed StartupTaskInspection. Separate from installer-shortcut issue #1956 and open #1964 (fix(setup): Start with Windows cannot stop the installer startup shortcut).

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs:194-231`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs#L194-L231)
- [`src/OpenClaw.Shared/WindowsStartupTaskRegistration.cs:5-26`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WindowsStartupTaskRegistration.cs#L5-L26)
- [`tests/OpenClaw.Tray.Tests/SetupStartupPolicyTests.cs:52`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/SetupStartupPolicyTests.cs#L52)
- https://github.com/openclaw/openclaw-windows-node/issues/2040
- https://github.com/SebTardif/openclaw-windows-node/pull/8
- https://github.com/openclaw/openclaw-windows-node/pull/1964
- https://github.com/openclaw/openclaw-windows-node/issues/1956

### Exact proposed comment

> Current main still ignores failed scheduled-task removal. Continue the existing SebTardif/openclaw-windows-node#8 (fix: fail unpackaged auto-start off when the logon task remains) for upstream adoption. One required correction is to distinguish confirmed absence from a failed task query; Boolean Exists alone cannot prove removal. Propagate refusal through the existing preference rollback, accept an already-absent task, and test failed deletion and failed inspection. This remains separate from #1964 (fix(setup): Start with Windows cannot stop the installer startup shortcut).

### Missing proof / next gate

- Typed absence/unknown postcondition and executable failed-delete/query tests.
- Isolated Settings toggle and logon-registration proof with preference rollback.

## #2069: [Bug] Hub 2026.10.5-alpha.4 crashes on chat send (0xc000027b in CoreMessagingXP.dll) against 2026.11.1 gateway

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 1 comments.

Versioned repeated packaged crashes are documented, with the actual stowed exception dump offered but not attached. Current source already changed the old synchronous item-bring path and docs describe a native layout-cycle mechanism sharing 0x88000FA8, but no stack links this operator-only LAN send to it. No verified complete repair or duplicate relationship. Do not infer Gateway incompatibility from timing alone or repeat a three-day-old dump request.

### Verified source and references

- [`src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs:749-779`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Chat/ReactorItemsViewScrollController.cs#L749-L779)
- [`tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs:2803`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/OpenClawChatDataProviderTests.cs#L2803)
- [`src/OpenClaw.Tray.WinUI/App.xaml.cs:405-409`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/App.xaml.cs#L405-L409)
- [`docs/CI_FAILURES.md:11-18`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/docs/CI_FAILURES.md#L11-L18)
- https://github.com/openclaw/openclaw-windows-node/issues/2069
- https://github.com/openclaw/openclaw-windows-node/pull/1549
- https://github.com/openclaw/openclaw-windows-node/pull/2083

### Exact proposed comment

> Keep this separate and open until the stowed-exception stack is identified. The existing offered dump should be reviewed through an approved private diagnostic route, not posted publicly. Current main has related scrolling changes and a native WinUI layout-cycle investigation, but neither proves this operator-only LAN send crash is fixed. Reproduce that exact route and add a failing native regression before selecting a renderer, scroll or transport repair.

### Missing proof / next gate

- Underlying stowed-exception stack from offered dump through private handling.
- Exact packaged operator-only LAN send reproduction and current-head proof.

**Posting guidance:** No duplicate public dump request; parent can arrange the existing private diagnostic follow-up.

## #2074: Run disconnect cleanup on every gateway connection loss

**Classification:** bug. **Disposition:** `existing-pr`. **Recommendation confidence:** 100%. Read body and 1 comments.

Main still skips OnDisconnected on client-abort and oversized-message exits; existing abort test explicitly expects zero callbacks. Open #2079 (fix: fail pending Gateway requests promptly after transport loss) closes this issue and describes generation-fenced invalidation and health ownership with focused proof. Do not implement another transport patch. Its disclosed CI/MXC gaps are not completion; body claims are not independently rerun here.

### Verified source and references

- [`src/OpenClaw.Shared/WebSocketClientBase.cs:551-558`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L551-L558)
- [`src/OpenClaw.Shared/WebSocketClientBase.cs:603-646`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Shared/WebSocketClientBase.cs#L603-L646)
- [`tests/OpenClaw.Shared.Tests/WebSocketClientBaseTests.cs:275-298`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Shared.Tests/WebSocketClientBaseTests.cs#L275-L298)
- https://github.com/openclaw/openclaw-windows-node/issues/2074
- https://github.com/openclaw/openclaw-windows-node/pull/2079
- https://github.com/openclaw/openclaw-windows-node/actions/runs/42460930325

### Exact proposed comment

> #2079 (fix: fail pending Gateway requests promptly after transport loss) is open and explicitly owns this lane. Current main still has the reported skipped cleanup, so retain the issue until that repair is reviewed and landed. Continue its generation-fenced abort/oversize/error and stale-health-retry coverage; preserve node cancellation and terminal auth/pairing policy. The PR’s disclosed CI and MXC proof gaps remain work to resolve, not grounds for a duplicate implementation.

### Missing proof / next gate

- Existing PR final current-head CI/review and required real Gateway/MXC acceptance.

## #2092: Auto-repair force-terminates the WSL distro but skips the keepalive re-arm when the restart fails

**Classification:** bug. **Disposition:** `keep-open`. **Recommendation confidence:** 99%. Read body and 1 comments.

Source proves forced WSL termination can be followed by failed cold restart, and coordinator returns before keepalive rearm. The existing RestartFails test checks no reconnect but does not assert restoration. No matching upstream PR found. Not classified simple-fix: result/exception reporting must indicate termination while preserving Stop/Disconnect/gateway-change/cancellation/session-end ownership; an unconditional finally could restart something intentionally stopped.

### Verified source and references

- [`src/OpenClaw.Connection/WslGatewayController.cs:111-135`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Connection/WslGatewayController.cs#L111-L135)
- [`src/OpenClaw.Tray.WinUI/Services/ManagedLocalGatewayRepairCoordinator.cs:393-430`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/ManagedLocalGatewayRepairCoordinator.cs#L393-L430)
- [`src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs:73-100`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/src/OpenClaw.Tray.WinUI/Services/WslGatewayKeepAliveService.cs#L73-L100)
- [`tests/OpenClaw.Tray.Tests/ManagedLocalGatewayRepairCoordinatorTests.cs:517-530`](https://github.com/openclaw/openclaw-windows-node/blob/e59374c064f43d68f5c72d070d4c89ef2d20ec3b/tests/OpenClaw.Tray.Tests/ManagedLocalGatewayRepairCoordinatorTests.cs#L517-L530)
- https://github.com/openclaw/openclaw-windows-node/issues/2092
- https://github.com/openclaw/openclaw-windows-node/issues/1571
- https://github.com/openclaw/openclaw-windows-node/issues/1575

### Exact proposed comment

> Confirmed in current source: a failed cold restart returns before keepalive restoration. Keep this open for a focused lifecycle repair with a failing terminate-then-restart-failure regression. Carry whether termination occurred back to the existing coordinator, then restore only while the same managed gateway and automatic-repair intent still own the operation. Do not use an unconditional finally or a second respawn supervisor: Stop, Disconnect, switching, cancellation and Windows session ending must still suppress new launches.

### Missing proof / next gate

- Combined termination/failure/rearm regression with negative user-intent/cancellation/switch/session-end controls.
- Current-head authorized WSL recovery and strict required E2E validation; reported disk impact not independently attributed.
