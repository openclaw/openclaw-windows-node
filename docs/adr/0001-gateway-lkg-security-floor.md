# ADR 0001: Gateway LKG Security Floor

## Status

Accepted

## Context

The Windows Companion installs an official OpenClaw Gateway version when a
composed package is not supplied. That ordinary fallback is a security and
compatibility boundary, not a rollback convenience.

OpenClaw `2026.7.1` includes requester-identity and browser-node authorization
hardening that is absent from `2026.6.11`. Reverting the fallback to
`2026.6.11` therefore traded known security fixes for compatibility and was the
wrong layer in which to address later release regressions.

Composed Companion builds have a separate immutable package contract: exact
version, credential-free package URI, and SHA-256. Their target may follow a
reviewed OpenClaw commit without changing the official fallback policy.

## Decision

The official Gateway LKG security floor is `2026.7.1`.

The pin may change only when all of the following are true:

1. The candidate is an official stable OpenClaw release.
2. The candidate version is strictly newer than the current pin.
3. Companion compatibility tests pass against the exact candidate.
4. The change is reviewed through the standing LKG update pull request.

The pin must not move to:

- an older stable release;
- a beta, release candidate, nightly, branch, or commit build;
- a custom or composed package reference.

Custom main-based builds remain represented only by the composed package
metadata contract. A regression in a newer stable release must be fixed,
isolated, or explicitly blocked. It must not be worked around by silently
downgrading the ordinary fallback below this security floor.

## Consequences

- Ordinary Companion installs retain the security fixes shipped in
  `2026.7.1`.
- The automated LKG workflow fails closed for prerelease versions and creates
  updates only for a strictly newer stable release.
- Main-based validation and operator builds remain possible without conflating
  composed package provenance with the official fallback.
- A future stable regression requires an explicit compatibility decision rather
  than an implicit downgrade.
