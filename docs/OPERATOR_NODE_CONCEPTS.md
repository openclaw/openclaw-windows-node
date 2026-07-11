# Operator and Node Concepts

OpenClaw Companion connects a Windows PC to an OpenClaw gateway in two separate
roles. A new install can use both roles at once, but they have different jobs and
different approval paths.

## Quick Glossary

| Term | Meaning |
| --- | --- |
| Gateway | The OpenClaw service that coordinates agents, channels, sessions, devices, and nodes. The Windows app talks to it over WebSocket. |
| Local WSL gateway | A dedicated `OpenClawGateway` WSL distro installed by the Windows onboarding flow. It is the recommended local mode for the safest isolation boundary and is app-owned and locked down rather than a general-purpose Ubuntu profile. |
| Local native gateway | OpenClaw installed directly in the Windows user profile and started through a per-user Windows Scheduled Task. This is the simpler setup mode, but it runs directly in the Windows user context. |
| Operator | The user-facing control role. The tray app uses the operator connection for Quick Send, chat, diagnostics, channel controls, setup, and approving pairing requests. |
| Node | The controllable Windows machine role. When Node Mode is enabled, the tray app advertises Windows capabilities such as screenshots, canvas, camera, notifications, and approved command execution. |
| Pairing | The gateway approval flow that turns a new device or node request into a trusted identity with a stored device token. |
| Reapproval | A later approval request when a paired node asks for new or changed trust, such as command capability access. |
| Allowlisted node capability | A node command the gateway is explicitly allowed to invoke, configured in the gateway `allowCommands` list. Windows-side settings and policies can still block the command. |

## How the Roles Work Together

The operator role is the control surface. It signs in to the gateway, sends chat
messages, shows status, opens diagnostics, and approves device or node pairing
requests when the gateway says approval is required.

The node role is the Windows capability surface. It tells the gateway which
Windows-native tools are available, then waits for approved gateway calls. Node
Mode does not mean every tool can run automatically. A capability has to be
enabled in Windows settings, allowed by the gateway, and in some cases approved
by a local Windows policy prompt.

A typical local setup uses this sequence:

1. OpenClaw Companion installs or connects to a gateway.
2. The tray app connects as an operator so you can send messages and manage setup.
3. If Node Mode is enabled, the same Windows app also connects as a node.
4. The gateway asks for pairing approval before trusting the new device or node.
5. After approval, the gateway can invoke only the node capabilities that are
   enabled locally and allowlisted by gateway policy.

## Local Gateway Modes Versus Existing Gateway

The recommended onboarding path installs an isolated WSL 2 gateway for the
safest local boundary. Users who prefer the simpler setup path can instead
choose the native Windows gateway. Both run on the same PC and are managed by the
OpenClaw Companion setup flow.

Advanced setup is for users who already have a local, remote, or manually
managed gateway. In that case, the Windows app still uses the same operator and
node roles; only the gateway location and credentials are different.

## Pairing, Tokens, and Reapproval

Pairing is gateway-owned. Setup codes, bootstrap tokens, and shared gateway
tokens can help the app connect for the first time, but a paired device token
takes precedence after approval. This keeps long-lived operator and node
identity scoped to the gateway record that issued it.

Some trust decisions are intentionally not automatic. Node command trust and
capability reapproval stay pending until an operator explicitly approves them,
so a new or changed node capability is visible before the gateway can use it.

## Capability Allowlist

Node Mode advertises available Windows commands, but the gateway decides which
commands it may call through `gateway.nodes.allowCommands` in
`~/.openclaw/openclaw.json`. Add exact command names such as `screen.snapshot`,
`canvas.present`, or `system.run`; wildcard entries are not expanded by the
gateway.

Privacy-sensitive commands, especially `screen.record`, `camera.snap`,
`camera.clip`, `stt.transcribe`, `tts.speak`, and `system.run`, should only be
allowlisted when you want the gateway to be able to request that behavior.
Windows permissions, Node Mode toggles, and the local exec policy can still add
stricter checks.

## Where to Go Next

- Follow [Installation and setup](SETUP.md) for first-run onboarding and
  troubleshooting.
- See [Node Mode](../README.md#-node-mode-agent-control) for capability names
  and allowlist examples.
- Read [Connection architecture](CONNECTION_ARCHITECTURE.md) for contributor
  details about token precedence, pairing, and connection lifecycle.
