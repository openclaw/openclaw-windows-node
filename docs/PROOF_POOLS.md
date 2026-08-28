# Custom Windows proof pools

Custom proof is capacity-dependent validation that cannot be claimed from the
normal GitHub-hosted matrix. The machine-readable inventory is
[`.github/proof-pools.json`](../.github/proof-pools.json), governed by
[`proof-pools.schema.json`](../.github/proof-pools.schema.json). It gives a
scheduler stable pool IDs, required host capabilities, authoritative commands,
safe evidence, redaction boundaries, and selection rules. It does not provision
hosts or imply that a pool ran.

## Named pools

| Pool ID | Use it for |
|---|---|
| `windows-11-sac-on` | Official signed installer and process reputation under enforcing Smart App Control |
| `windows-wsl-mxc` | Real Gateway to Windows node `system.run` containment where MXC cannot skip |
| `windows-11-arm64` | Native ARM64 build, launch, WebView2, and architecture-specific runtime behavior |
| `windows-wsl-dgx-blackwell` | WSL-visible NVIDIA DGX or Blackwell detection, setup, restart, and fixed-prompt inference |
| `windows-clean-installer-upgrade` | Clean install, previous-version upgrade, repair, and uninstall using exact signed artifacts |
| `windows-wsl-gateway-e2e` | Product WSL setup, bootstrap, operator and node pairing, recovery, and Gateway invocation |
| `windows-winui-interactive` | Current-head visual, accessibility, permission, and diagnostic proof in the isolated WinUI app |

Every pool is `maintainer-scheduled`, requires approval, and is
`capacity-dependent`. A scheduler must match every `requiredCapabilities`
entry. It must not weaken a command, convert a skip into a pass, or substitute a
different pool without updating the PR declaration.

## Declare pools in a PR

Fill the PR template's `## Required proof pools` section with one line per pool:

```markdown
- `windows-wsl-mxc`: `system.run` containment changed.
- `windows-winui-interactive`: the approval dialog changed.
```

Use `- \`none\`: <reason>` only when no custom host class is required. The
declaration schedules work; it is not evidence that work completed. Put actual
results under `## Validation` and `## Real Behavior Proof`. If a requested pool
is unavailable, retain the declaration and report `Not verified / blocked`
instead of claiming adjacent automated coverage.

Choose every applicable pool. For example, an ARM64 installer change that also
changes first-run UI needs `windows-11-arm64`,
`windows-clean-installer-upgrade`, and `windows-winui-interactive`.

## Evidence and secret boundaries

Collect only the evidence allowlisted for the selected pool. Prefer exit codes,
test counts, artifact hashes, public signature metadata, coarse versions,
redacted state names, and focused app-window captures. Use isolated tray data
and restorable machine state.

Never publish gateway or device tokens, setup codes, private keys,
`gateways.json`, raw settings or identity files, signing credentials, full
environment dumps, arbitrary prompts or command output, or unrelated desktop
content. Apply each pool's `redact` list before attaching logs or screenshots.
The inventory's `neverCollect` list is a hard boundary, not a suggestion.

## Validate the inventory

The existing documentation gate validates both JSON files, unique command IDs,
repository entry points, the PR template heading, and exact inventory-to-table
ID parity. It runs the built-in schema engine and the PowerShell 5.1-compatible
fallback so either path fails closed on drift. Regression cases also reject
type-less assertion schemas, unsupported `additionalProperties` schema objects,
and raw `dotnet test` hidden inside wrappers. PowerShell 7 coverage runs when
available; Windows PowerShell 5.1 remains the required fallback:

```powershell
.\scripts\validate-proof-pools.ps1
.\scripts\test-proof-pool-validator.ps1
.\scripts\test-validate-docs-proof-pool-flow.ps1
.\scripts\validate-docs.ps1
```

`.\build.ps1` and CI run `validate-docs.ps1`, so inventory drift fails the
normal build and pull request validation path. CI runs the full
malformed-contract matrix separately so local builds keep the documentation
gate fast.
