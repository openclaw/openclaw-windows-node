# Mandatory MXC and preset-owned file permissions

Status: accepted 2026-10-07; amended by explicit user agreement on 2026-10-08 to user-folder scope.

This adds a filesystem layer to the display names accepted in decision 0002.
It does not rewrite that copy-only history or alter capability membership:
Strict remains `ReadOnly` (Canvas/Screen, command capability disabled);
Balanced remains `Standard`; Open remains `Full`.

| Persona | New-profile file intent | Internet recipe |
| --- | --- | --- |
| Strict | Runtime reads and private scratch only; no personal files | Off |
| Balanced (untouched onboarding default) | Read the OS profile and resolved personal/redirected folders, no personal writes, protected roots excluded | On |
| Open (explicit choice) | Read/write that same declared user-folder scope, protected roots excluded | On |

Internet recipes continue the prior sandbox presets; they remain separately
reviewable. Clipboard, Windows UI and limits stay independent. Every preset
retains command approvals and MXC on supported Windows. No LAN, inbound, loopback or
input-injection permission is silently enabled.

`SetupAccessDraft` carries explicit persona intent. `SetupSettingsWriter`
initializes once through the authoritative store and rejects stale same-policy
changes. Existing Custom settings survive resume/restart. Explicitly reapplying
a preset previews and confirms replacement of its owned folder/network fields.
Settings shows Custom when these fields differ from a preset. Additional folder
grants do not remove access already provided by the selected scope; "Use scope"
means no additional grant, not a deny. Legacy filtered/conflicting
grants remain visibly blocked until reviewed; old Off never means Open.

Preset headings retain the lock, shield and warning metaphors for Strict,
Balanced and Open. They render decorative catalog `FontIcon` glyphs rather than
embedding emoji in localized labels; the preset buttons keep their existing
accessible names.

| Preset | Catalog glyph |
|---|---|
| Strict | `SandboxStrict`, U+E72E ([Lock](https://learn.microsoft.com/windows/apps/design/iconography/segoe-fluent-icons-font)) |
| Balanced | `Permissions`, U+EA18 (Shield) |
| Open | `StatusWarn`, U+E7BA (Warning) |

The original unpublished drive-wide proposal was blocked. The agreed replacement
is **user-folder scope**: OS profile plus resolved Documents, Desktop, Downloads
and configured available OneDrive roots. Redirected roots outside the profile are
included explicitly. Other accounts and whole drives are not included. Projects
outside those roots need Custom grants; selected RW children can supplement a RO
profile. Numeric values of the unpublished drive intents now denote the narrower
user-folder scopes; no external broad-drive compatibility framework is added.

No volume-root grants, including readonly metadata grants, are emitted. Native
proof on the tested client found readonly root entries allowed unrelated sibling
reads. Strict contains no personal roots. Missing/inaccessible required locations
block with diagnostics; optional unavailable OneDrive is explicitly not included.
Settings displays resolved roots. Root/cwd reparse aliases remain rejected;
synthetic child-junction proof does not justify claiming every topology supported.
When a protected path has missing parents, its first missing ancestor is denied.
This keeps future protected descendants excluded while satisfying the native
policy's existing-parent requirement, without creating folders on the host.

Code anchors: `Onboarding_MxcFileScope`, `SandboxPage_Preset*`,
`SandboxPage_UserFiles*`, `SandboxPage_ScopeResolutionBlocked*`,
`SystemRunPermissionPresets`, `MxcRequestContext`, `MxcRequestBuilder`,
`SetupSettingsWriter`.

Product-runner native proof must use synthetic personal and sensitive roots,
outside-checkout owned scratch, PID/start-time/command ownership and unchanged
real-appdata/ancestor ACL manifests. Rendered UI, gateway/MCP, installer/SAC,
broader alias topologies and ARM64 evidence remain separate gates. Compilation
and unit tests do not prove those behaviors.

## Revised unsupported-Windows compatibility decision

The user's subsequent explicit decision supersedes mandatory containment on
Windows versions lacking the required MXC containment, but not Companion's own
OS minimum. A positive OS verdict selects approved direct uncontained execution.
Node Sandbox shows a persistent warning and disables permission controls without
modifying saved settings. File, network, clipboard and Windows UI restrictions
are not enforced in this mode. Approval revalidation, pinned argv/cwd, deadline,
bounded output and process ownership remain active. Missing native components,
unknown/failed probes, unsupported policy or execution errors never downgrade.

The retired `SystemRunBlockHostFallbackWhenMxcUnavailable` flag was hidden JSON
configuration, not a UI option. The user's explicit uniform compatibility
decision retires it without a hidden override or extra toggle. Permissions'
System tools switch remains the user-facing control that blocks command execution.
