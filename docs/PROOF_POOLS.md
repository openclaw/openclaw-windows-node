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

Onboarding compositor captures, full galleries, and native keyboard selection
are tagged `Category=NativeOnboardingProof` in `OpenClaw.Tray.UITests`. They belong
to `windows-winui-interactive`, not the hosted framework UI shard. Run that
category on an authorized interactive desktop with all three isolated tray roots,
`OPENCLAW_REPO_ROOT`, `OPENCLAW_UI_PROOF_DIR`, and the reviewed source/build freeze
(`OPENCLAW_UI_PROOF_FREEZE_DIR`, `OPENCLAW_UI_PROOF_MANIFEST_SHA256`). Missing
foreground, capture, or freeze prerequisites still fail; exclusion from hosted CI
does not fulfill the proof pool. Framework onboarding behavior remains in CI
and invokes the XAML-wired action boundary without claiming native input or
compositor proof. Button controls retain their automation Invoke coverage.

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

## Validate local execution evidence

The inventory describes what should run; it does not validate completed proof.
Exact inventory-owned `run-proof-tests.ps1` recipes emit
`evidence-summary.json` beside their invocation-specific TRX:

```text
TestResults/ProofPools/<result-name>/<invocation-id>/
```

An unbound ad hoc filter still receives strict test-result validation, but it
does not receive an inventory-bound evidence summary. Follow the inventory's
exact project, filter, result name, runtime identifier, and environment gates.
The Gateway recipe requires and records both `OPENCLAW_RUN_E2E=1` and
`OPENCLAW_RUN_OLLAMA_E2E=1` before execution. The latter enables the selected
Ollama permission case, which uses a fake loopback server rather than a
downloaded model or a real Ollama service. Validation checks those recorded
observations, not the validating shell's current environment. No arbitrary
environment values are collected.

For inventory-bound runs, custom `-ResultsDirectory` locations must be under
the repository's `TestResults` or `artifacts` directory, for example
`-ResultsDirectory .\TestResults\proof-output`. Other locations are rejected
before test execution so generated reports cannot invalidate source identity.
Existing summaries outside these roots must be regenerated in a supported
location. Ad hoc runs without an inventory binding retain custom destinations
but do not produce pool evidence summaries.

Validate an existing authorized accessibility run locally:

```powershell
.\scripts\validate-proof-evidence.ps1 `
  -SummaryPath '.\TestResults\ProofPools\winui-accessibility\<invocation-id>\evidence-summary.json' `
  -RequiredPoolId windows-winui-interactive `
  -RequiredCommandId 'windows-winui-interactive/run-accessibility-scan'
```

The validator checks inventory correspondence, source HEAD/relevant dirty-tree
digest, invocation/report identity, actual result counts, and artifact hashes.
Missing required pool/command execution, stale source, altered artifacts,
blocked runs, or inconsistent reports fail. Generating evidence or checking it
does not authorize the underlying runtime, host, or publication.

Inventory-bound runs may reference existing repository-contained artifacts
with `-CaptureArtifact`, `-BinaryArtifact`, `-NativeReceipt`, and
`-FixtureReceipt`. Only supported PNG, executable/library, named native receipt,
and fixture `run.json` references are accepted; do not attach arbitrary JSON or
logs. Reparse-point paths are rejected. Keep the pool's evidence/redaction
policy even when a file extension is accepted.

**Assurance limit:** a valid summary establishes selected-test execution
correspondence, not complete pool proof, a signed attestation, image quality,
host capability, or source-to-binary provenance. `completePoolProof` remains
false. Native proof's reviewed freeze/source manifest/loaded-binary checks and
fixture receipt verification remain authoritative. Receipt attachments here
are digest references only. Missing UI, real Gateway, or MXC assertions stay
blocked rather than being filled by neighboring unit-test evidence.

Run `.\scripts\test-proof-evidence.ps1` for the synthetic report, controlled
runner, source-drift, and artifact-correspondence regression cases. It does not
exercise the actual desktop or Gateway.
