# Setup wizard UX refresh - screens & accessibility proof

Screenshots captured from the live isolated dev app (`OPENCLAW_FORCE_ONBOARDING=1`,
`run-app-local.ps1 -NoBuild -Isolated`) walking the full gateway setup + onboard flow.
Accessibility was scanned per-screen with a standalone Axe.Windows (v2.4.1) scanner
against the live process - the same engine Accessibility Insights uses.

## Screens

| # | Screen | Notes |
|---|--------|-------|
| 01 | Welcome + security notice | Selectable RichTextBlock body |
| 02 | Setup mode | Single-selection `ListView` (ItemContainer), first item selected by default |
| 03 | Config handling | Single-selection list, standardized bottom bar |
| 04 | Model/auth provider (collapsed) | "Skip for now" pinned to top, subtle **More ▾** expander |
| 05 | Model/auth provider (expanded) | One-click expand keeps "Skip for now"; full provider list |
| 06 | Auth method | Single-selection list |
| 07 | Text input step | API-key entry |
| 08 | Capabilities | Radio group with accessible names (a11y fix) |

Bottom action bar across steps: standard **Back** button, `AccentButtonStyle`
primary (Continue), subtle **More options** / **Skip** / **Start over** controls.

## Accessibility results (Axe.Windows, actionable violations)

All refactored surfaces flagged `NameNotNull` (focusable element with a null
accessible Name) before the fix; every one is 0 after.

| Screen | Before | After |
|--------|:------:|:-----:|
| Capabilities radio group | 3 | 0 |
| Setup mode ListView | 2 | 0 |
| Provider list (collapsed) | 6 | 0 |
| Provider list (expanded) | 7 | 0 |
| Welcome / Confirm / Auth / Text input | 0 | 0 |

### Fixes
- `WizardPage` option lists: each `ListViewItem` (and multiselect `CheckBox`) now
  gets `AutomationProperties.Name` = label + hint via a shared `CreateOptionItem`
  helper.
- `CapabilitiesPage`: the three profile radio buttons (Read-only / Standard /
  Full access) now set `AutomationProperties.Name`.

### Known framework limitation (not fixed)
Inline `Hyperlink` elements inside a `RichTextBlock` (security disclaimer link)
report a null `BoundingRectangle` to UIA when scrolled out of view. This is a
WinUI framework behavior for inline hyperlinks, not a control we construct.
