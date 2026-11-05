# Native proof for openclaw/openclaw-windows-node PR #1971

Related to issue #1945, originally reported by @fire0220. PR head tested: `76cc4cbd5bf7fc74b44c4836eebce10dfc693a0c`. Native host: Windows 10 Pro x64 build 19045; .NET SDK 10.0.400; Windows App Runtime 2.6.4. The isolated synthetic Gateway provided test data and continued to reject config writes. No working credentials were used.

## Before and after

Before the repair, the actual UIA names were empty for Bindings Refresh, Config Save, both Config Remove buttons, and the Sessions list. A Usage provider row exposed `OpenClawTray.Pages.UsagePage+ProviderRow`. The Config test reached the mounted Messages tree item, invoked it, edited `alpha`, and verified that Save and Discard were enabled before reaching its intended failing name assertion.

The selected native UIA tests on the repaired source reported:

```text
Bindings refresh: name=[Refresh], id=[RefreshButton]
Config save: name=[Save Changes], id=[SaveButton]
Alpha remove: name=[Remove item], type=[ControlType.Button]; Bravo remove: name=[Remove item].
Provider row: name=[Fixture Provider, Pro, 37% used, Fixture quota warning], class=[ListViewItem]
Initial rows: provider=[Fixture Provider, Pro, 37% used, Fixture quota warning], empty=[Fixture Empty], day1=[2026-10-31, $1.25], day2=[2026-10-30, $0.50].
Changed rows: provider=[Fixture Provider, Pro, 53% used, Fixture changed warning], day1=[2026-10-31, $2.50], after period=[2026-10-31, $2.50].
```

The actual Sessions list name passed its assertion. Config keyboard removal, reachability of the remaining row, and Discard restoration passed; no config mutation was sent. Sessions keyboard row/action navigation and notification checkbox traversal also passed.

The ordinary accessibility selection passed 6, failed 0, skipped 0. The separately enabled keyboard selection passed 5, failed 2, skipped 0. Bindings focus after refresh was observed on an in-app Close button rather than Refresh. Clipboard keyboard coverage could not pass because all four options were disabled on the Windows 10 host. Both assertions remain in place. The complete native UIA output is retained locally in `issue-1945-review-normal.trx` and `issue-1945-review-keyboard.trx`.

## Owned-window screenshots

- `issue-1945-config-save.png`: synthetic Config tree, edited fixture label, two remove buttons, and enabled Save/Discard.
- `issue-1945-usage-rows.png`: synthetic provider and daily-cost rows.

Both images were captured with the repository's owned-HWND PrintWindow helper, which verifies process ownership and rejects blank content; both were visually inspected. They show a Windows 10 containment warning covering part of the owned app. Screenshots demonstrate visible state and do not establish screen-reader speech or UIA names. Temporary screenshot hooks were added only to the test harness for these captures and then removed; the shipping app source was unchanged. Screenshot invocation: 2 passed, 0 failed, 0 skipped.

## Remaining limitations

Actual Usage scrolling and container re-realization were not established; invoking ScrollIntoView when available is not sufficient proof. NVDA 2026.3 was available but not running, so speech and group announcements are unverified. The two keyboard failures above remain open. The UIA screenshots do not prove a Windows 11 environment or screen-reader narration. Independent review remains outstanding.

Full Windows app build and UI test build passed. The Shared suite produced 4,808 passed, 2 failed, 25 skipped; both failures were PowerShell executable-resolution cases reproduced on the parent. The Tray suite produced 4,790 passed and 9 failed, all due to missing `pwsh` in that run. A task-owned portable PowerShell 7.5.13 was subsequently downloaded from the official PowerShell release, verified against SHA-256 `120b8f0b04f3b3f6e8c351ddbe568bf596cdca15eeb2ecb7e724060051a36d65`, and the repository CI workflow contract passed. The supported full fixture run using portable PowerShell reported profile/app 34 passed, 0 failed, 0 skipped; its normal UI stage reported 47 passed, 3 failed, 0 skipped. The failed cases timed out in two search scenarios and one chat-run correlation scenario. This full fixture run failed and is not a green result. Receipts: `fixture-profile-app.trx` and `fixture-ui.trx`.

With portable PowerShell available on the task process PATH, the Shared rerun passed 4,810, failed 0 and skipped 25; receipt `issue-1945-pwsh-shared.trx`. The earlier missing-pwsh failures are preserved above as historical results.

With portable PowerShell on the task process PATH, the Tray rerun passed 4,799, failed 0 and skipped 0; receipt `issue-1945-pwsh-tray.trx`. The earlier missing-pwsh failures are preserved above as historical results.
