# Onboarding access preset display names

Status: accepted, 2026-09-25.

The onboarding display names are **Strict**, **Balanced (Recommended)** and
**Open**. This is a copy-only change, not a new sandbox or security-policy
selection. Existing enum names, resource keys, CLI/config meaning and exact
capability membership remain stable.

| Display name | Internal profile | Enabled selectable capabilities |
| --- | --- | --- |
| Strict | `ReadOnly` | Canvas, Screen |
| Balanced (Recommended) | `Standard` | System, Canvas, Screen, Tts, Stt |
| Open | `Full` | System, Canvas, Screen, Camera, Location, Browser, Tts, Stt |

Descriptions state only selectable capability effects. They make no promises
about filesystem folders, workspace boundaries, system-folder protection,
internet/LAN access or network restrictions. Device information is not a ninth
profile toggle. Execution approvals, Windows permissions, capture consent and
MXC policy remain independently enforced.

Localized strings remain under the existing `Onboarding_V2_Profile*` keys.
The ordinary capabilities page no longer includes the Windows-access advisory
panel. Its shared control and passive checks remain available in the legacy/debug
permissions page and do not change runtime enforcement.
