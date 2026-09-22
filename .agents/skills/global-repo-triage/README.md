# OpenClaw Windows Node Global Triage

This portable skill reproduces the full maintainer sweep used for
`openclaw/openclaw-windows-node`. It combines the repository's scheduled
read-only triage report with source review, proof-pool scheduling, active-owner
auditing, adversarial review, landing order, and release planning. It can also
open a live interactive canvas when requested.

## Install

Unzip the package so this file exists:

```text
%USERPROFILE%\.copilot\skills\global-repo-triage\SKILL.md
```

Restart Copilot if the skill is not discovered immediately. The optional
interactive dashboard is available when the repository also contains
`.github\extensions\openclaw-triage-dashboard\extension.mjs`.

## Open before state is available

Call `open_canvas` without input:

```json
{ "canvasId": "openclaw-triage-dashboard", "instanceId": "triage-bootstrap" }
```

The empty dashboard explains how to generate or supply state. It does not
load files or example decisions, query GitHub, poll, or route item actions.
Omitted input, `null`, and `{}` are supported; malformed nonempty state still
fails validation.

To load real data, ask the agent to run the skill and save a version-1
`global-triage-YYYY-MM-DD.json` artifact, or read an existing artifact. Pass its
parsed object as `input` to `open_canvas`, using a fresh instance ID such as
`global-triage-YYYY-MM-DD-v1`. Reopening the bootstrap ID may only focus that panel.
Use another fresh ID for revised input. The template is a format example, not
reviewed evidence. See section 10 of `SKILL.md` for the full state contract.

## Recommended prompt

```text
Run the OpenClaw Windows Node global triage. Read every open issue and PR,
compare with the previous report, identify what can safely land today, audit
active ownership, schedule required proof pools, save the evidence artifacts,
and save the full Markdown report plus execution handoff. Open the live triage
canvas too. Do not mutate GitHub until I approve an action.
```

The `examples` folder contains the two real reports that established the Markdown
format. When requested, the canvas supplements that report with live checks,
plan gates, and guarded child-session actions. It does not replace the report or
merge directly.
