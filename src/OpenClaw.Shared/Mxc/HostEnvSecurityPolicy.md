# HostEnvSecurityPolicy

## What this is

`HostEnvSecurityPolicy.json` is a **byte-identical copy** of
`openclaw/openclaw:src/infra/host-env-security-policy.json` — the canonical
list of environment variables an executor must refuse to set on a spawned
child process. It is consumed by `HostEnvSecurityPolicy.cs` (embedded as an
assembly resource) and used in `MxcConfigBuilder.BuildEnv` to filter
agent-supplied env before it reaches `wxc-exec.exe`.

## Why we ship a copy

openclaw enforces env scrubbing at every spawn boundary as defense-in-depth,
not centrally at the gateway. The macOS app does this via
`apps/macos/Sources/OpenClaw/HostEnvSanitizer.swift` (consumer) +
`HostEnvSecurityPolicy.generated.swift` (data, generated from the same JSON
by `scripts/generate-host-env-security-policy-swift.mjs`). We are the
Windows-node analog: same role, same data source. Loading the JSON directly
at runtime instead of code-generating is the only divergence.

## Update workflow

When the upstream JSON changes:

```powershell
# 1. Copy the latest from openclaw/openclaw
cp <path-to-openclaw-clone>/src/infra/host-env-security-policy.json `
   src/OpenClaw.Shared/Mxc/HostEnvSecurityPolicy.json

# 2. Re-run the policy tests (catch truncation / drift)
dotnet test ./tests/OpenClaw.Shared.Tests --filter "FullyQualifiedName~HostEnvSecurityPolicy"
```

`HostEnvSecurityPolicyTests` asserts a minimum size (≥200 blocked keys,
≥3 prefixes) plus the presence of well-known entries (`GITHUB_TOKEN`,
`LD_PRELOAD`, etc.) so an accidentally truncated or stale copy fails fast.

## Schema reference

| Key | Used by us | Meaning |
|---|---|---|
| `blockedEverywhereKeys` | ✅ blocked | always block, in both host inheritance and agent overrides |
| `blockedOverrideOnlyKeys` | ✅ blocked | block when set explicitly by the caller (we always treat agent env as an "override") |
| `blockedPrefixes` | ✅ blocked | block keys matching these prefixes (`LD_`, `DYLD_`, `BASH_FUNC_`) |
| `blockedOverridePrefixes` | ✅ blocked | block override-only prefixes (`GIT_CONFIG_`, `NPM_CONFIG_`, `CARGO_REGISTRIES_`, `TF_VAR_`) |
| `allowedInheritedOverrideOnlyKeys` | ❌ not used | narrow allow-list for vars that override-blocked but inheritance-allowed; only meaningful when a process inherits host env (we don't inherit, agent supplies env explicitly) |
