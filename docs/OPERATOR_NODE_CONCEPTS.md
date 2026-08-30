# Operator and Node Concepts

OpenClaw Companion connects a Windows PC to an OpenClaw gateway in two separate
roles. A new install can use both roles at once, but they have different jobs and
different approval paths.

For the complete request, exec approval, protocol, and sandbox flow, see the
[Gateway, node, and exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md).

## Quick Glossary

| Term | Meaning |
| --- | --- |
| Gateway | The OpenClaw service that coordinates agents, channels, sessions, devices, and nodes. The Windows app talks to it over WebSocket. |
| Local WSL gateway | A dedicated `OpenClawGateway` WSL distro installed by the Windows onboarding flow. It is app-owned and locked down rather than a general-purpose Ubuntu profile. |
| Operator | The user-facing control role. The tray app uses the operator connection for Quick Send, chat, diagnostics, channel controls, setup, and approving pairing requests. |
| Node | The controllable Windows machine role. When Node Mode is enabled, the tray app advertises Windows capabilities such as screenshots, canvas, camera, notifications, and approved command execution. |
| Pairing | The gateway approval flow that turns a new device or node request into a trusted identity with a stored device token. |
| Reapproval | A later approval request when a paired node asks for new or changed trust, such as command capability access. |
| Allowlisted node capability | A node command the gateway is explicitly allowed to invoke, configured in the gateway `allowCommands` list. Windows-side settings and policies can still block the command. |
| App-managed Local AI | The companion-managed llama-server provider configured directly on the app-managed gateway. It is not a Windows node capability. |
| Shared Windows Ollama | An optional Windows node capability that lets the active paired local or remote gateway invoke a separately installed Ollama service on this PC. |

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

## Local WSL Gateway Versus Existing Gateway

The default onboarding path installs a local WSL gateway for users who do not
already have one. That gateway runs on the same Windows PC and is managed by the
OpenClaw Companion setup flow.

Advanced setup is for users who already have a local, remote, or manually
managed gateway. In that case, the Windows app still uses the same operator and
node roles; only the gateway location and credentials are different.

### Local AI provider versus shared Windows Ollama

These are independent paths:

- **App-managed Local AI** installs and supervises a qualified llama-server
  runtime and configures it as a model provider on the app-managed gateway.
- **Share Windows Ollama** advertises `ollama.models` and `ollama.chat` through
  the Windows node. The active paired gateway may be local or remote, while the
  Ollama HTTP service remains bound to Windows loopback.

Enabling one does not enable, reconfigure, stop, or replace the other. Windows
Ollama sharing is off by default and is controlled only from the Permissions
page.

## Pairing, Tokens, and Reapproval

Pairing is gateway-owned. Setup codes, bootstrap tokens, and shared gateway
tokens can help the app connect for the first time, but a paired device token
takes precedence after approval. This keeps long-lived operator and node
identity scoped to the gateway record that issued it.

Some trust decisions are intentionally not automatic. Node command trust and
capability reapproval stay pending until an operator explicitly approves them,
so a new or changed node capability is visible before the gateway can use it.

## Capability Allowlist

Node Mode advertises available Windows commands. The gateway combines the
paired node's approved declarations, canonical Windows platform defaults,
`gateway.nodes.allowCommands`, and `gateway.nodes.denyCommands` to decide which
commands it may call. Explicit allow/deny entries use exact command names;
wildcards such as `canvas.*` are not expanded.

Canonical paired Windows nodes already receive desktop defaults for
`system.run`, `system.run.prepare`, `system.which`, and `system.notify`. That
gateway default does not bypass the local **Run system tools** switch, Windows
V2 exec approvals, or sandbox policy. Commands outside the Windows defaults,
especially `screen.record`, `camera.snap`, `camera.clip`, `stt.transcribe`, and
`tts.speak`, should be allowlisted only when you want the gateway to request
that behavior. On gateways without the bundled Ollama node-inference policy,
`ollama.models` and `ollama.chat` also require exact
`gateway.nodes.allowCommands` entries.

## Where to Go Next

- Follow [Installation and setup](SETUP.md) for first-run onboarding and
  troubleshooting.
- See [Node Mode](../README.md#-node-mode-agent-control) for capability names
  and allowlist examples.
- Read [Connection architecture](CONNECTION_ARCHITECTURE.md) for contributor
  details about token precedence, pairing, and connection lifecycle.
- Use the [Gateway, node, and exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md)
  when tracing a request across agent, gateway, approval, node, and sandbox
  boundaries.
