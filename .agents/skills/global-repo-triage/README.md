# OpenClaw Windows Node Global Triage

This skill reproduces the full maintainer sweep used for
`openclaw/openclaw-windows-node`. It combines the repository's scheduled
read-only triage report with source review, proof-pool scheduling, active-owner
auditing, adversarial review, landing order, release planning, and a live
interactive canvas.

## Install

Repository use is automatic because the skill and canvas extension are checked
in together:

```text
.agents\skills\global-repo-triage\SKILL.md
.github\extensions\openclaw-triage-dashboard\extension.mjs
```

For portable user installation, copy both directories to the matching user skill
and extension locations. Restart Copilot if discovery does not refresh.

## Recommended prompt

```text
Run the OpenClaw Windows Node global triage. Read every open issue and PR,
compare with the previous report, identify what can safely land today, audit
active ownership, schedule required proof pools, save the evidence artifacts,
and open the live triage canvas. Do not mutate GitHub until I approve an action.
```

The canvas refreshes checks and plan gates automatically. Item actions create or
reuse one dedicated child project session per PR or issue. `Prepare merge` sends
a guarded request to that session; it does not merge directly.
The `examples` folder contains the two reports that established decision quality.
