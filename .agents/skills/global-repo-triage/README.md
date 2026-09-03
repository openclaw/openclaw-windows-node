# OpenClaw Windows Node Global Triage

This portable skill reproduces the full maintainer sweep used for
`openclaw/openclaw-windows-node`. It combines the repository's scheduled
read-only triage report with source review, proof-pool scheduling, active-owner
auditing, adversarial review, landing order, and release planning.

## Install

Unzip the package so this file exists:

```text
%USERPROFILE%\.copilot\skills\global-repo-triage\SKILL.md
```

Restart Copilot if the skill is not discovered immediately.

## Recommended prompt

```text
Run the OpenClaw Windows Node global triage. Read every open issue and PR,
compare with the previous report, identify what can safely land today, audit
active ownership, schedule required proof pools, and save the full Markdown
report plus execution handoff. Do not mutate GitHub until I approve the queue.
```

The `examples` folder contains the two real reports that established the format.

