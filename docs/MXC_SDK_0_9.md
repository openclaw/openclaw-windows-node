# MXC SDK 0.9 migration

The companion builds with `@microsoft/mxc-sdk` 0.9.0 and emits the exact
`0.9.0-alpha` policy contract. Build hosts need Node.js 24 or newer. Packaged
execution continues to use the native executor and its sibling DLLs.

| Previous policy | SDK 0.9 policy | Preserved boundary |
| --- | --- | --- |
| `network.defaultPolicy: block` | `network.egress.default: deny` | Outbound network denied |
| `network.defaultPolicy: allow` and `internetClient` | `network.egress.default: allow` | Public outbound access; no private-network capability |
| `network.enforcementMode: capabilities` | Omitted | Native directional policy owns enforcement and derives capabilities |
| No inbound or loopback grant | `network.ingress.default: deny`, `hostLoopback: deny` | No new inbound or host-loopback permission |
| `process.env: []` | Omitted | Clean Windows profile defaults, including SYSTEMROOT and LOCALAPPDATA |
| Filesystem, UI, clipboard, lifecycle | Same values | Deny filtering, non-cascading root grants and opt-in UI remain unchanged |

The C# builder no longer writes network capabilities. The native SDK removes
caller-supplied network capabilities and derives them from the directional
policy. Outbound allow derives `internetClient`; only inbound allow would derive
`privateNetworkClientServer`, which the companion never requests. See the
[upstream policy owner](https://github.com/microsoft/mxc/blob/86fb3d2abaf9c431556692037bff881830b543a5/src/backends/process_container/common/src/network_policy_helpers.rs).

The SDK removed its SBOX launch path. SBOX-only hosts without usable PSEC lose
MXC availability. The companion still requires a supported Windows client SKU,
`base-container`, and no DACL augmentation. Existing explicit host-fallback and
strict-block settings remain authoritative; this migration adds no fallback.

Custom `OPENCLAW_WXC_EXEC` executors are not bound to the packaged SDK version.
They may still be probed for diagnostics, but `system.run` refuses to send them
0.9 policy requests. Unset the override to use the packaged executor. This
unavailability follows the same existing host-fallback/strict-block policy.

Normal requests omit `process.env`: SDK 0.9 interprets `[]` as an explicitly
empty Windows environment, even with older schemas. The existing shell PATH
and scratch-directory bootstrap remains intact. Arbitrary caller environment
overrides remain rejected.

Regenerate policy fixtures on native Windows after `npm ci`:

```powershell
node tools/mxc/dump-sdk-config.cjs
node tools/mxc/dump-sdk-config.cjs --check
```

The generator uses the pinned SDK's `createConfigFromPolicy` for all four
presets. It strips the abstract containment selector, per-command fields and denied paths, and removes the JS
builder's redundant network capabilities after asserting their exact values.
The C# golden tests compare that normalized output. Focused tests also check
directional deny defaults, missing env serialization, exact schema admission,
and rejection of custom executors. These policy checks do not substitute for
native network, filesystem, and Gateway containment proof on the
`windows-wsl-mxc` pool.
