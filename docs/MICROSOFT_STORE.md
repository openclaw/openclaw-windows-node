# Request Microsoft Store publication

Use this guide to request central publication of an already validated Companion
release bundle. This repository builds the unsigned x64/ARM64 Store inputs and
records release evidence. The standalone
[`windows-store-publish.yml` publisher in `openclaw/releases`](https://github.com/openclaw/releases/actions/workflows/windows-store-publish.yml)
authorizes the exact source, obtains release-manager approval, and submits to
Partner Center. It never runs publisher code from this repository.

A successful native request is not approval, a Store commit, certification, or
public rollout. The existing stable-promotion approval, signing, GitHub release,
MSIX version reservation, and install/upgrade acceptance remain separate gates.

## Required administrator cutover

Complete the central publisher setup before merging or using this request-only
workflow. These are administrator changes, not effects of merging this PR:

1. Configure the central `microsoft-store-companion` environment to allow only
   `main`, require `@openclaw/release-managers-openclaw` approval, prevent
   self-review, and disallow administrator deployment bypass. Central release
   code and its identity policy also need enforced release-manager review.
2. Move the dedicated Companion Store application's Entra federation to the
   approved central environment. Remove the old native-repository federated
   credential and every other publishing writer. Removing the old workflow from
   main does not revoke the identity available to historical workflow code.
   Keep Store identifiers and the short-lived OIDC exchange in the central
   publisher, not a native-repository environment. No new persistent Store
   secret is required.
3. Make the existing release GitHub App (ID `2729701`) and
   `GH_APP_PRIVATE_KEY` available to this workflow, with its installation able
   to request workflows in `openclaw/releases`. The minted installation token
   selects only `releases` and requests only Actions write permission. It has no
   Store authority. Verify the installation grant; do not assume the existing
   key alone grants access. No new PAT is required.
4. Enforce main's required release-manager code-owner review for the paths in
   [CODEOWNERS](../.github/CODEOWNERS), including CODEOWNERS itself. Dismiss stale
   approvals, require approval of the latest reviewable push, and prevent direct
   push or actor bypass for protected automation changes. The file alone does
   not turn on GitHub enforcement.
5. Leave repository Actions variable `MSSTORE_ENABLED` unset or `false` until a
   manual central request, the protected approval path, and required Store
   install/upgrade acceptance have been verified. Do not re-enable the retired
   native publisher as a fallback.

The central publisher owns the fixed Companion product identity, Store policy,
OIDC trust checks, source validation, artifact download, and evidence. An
operator with write access here can request publication, but cannot approve the
central publishing environment merely by having that access.

## Request manually

Select the successful run ID for a stable/correction-tag **Build and Test** run
or an approved **Promote alpha to stable** run. The number follows
`/actions/runs/` in its URL. The source must retain its unexpired authorized
unsigned bundle and current-attempt release witness.

From **Actions > Request Microsoft Store publication > Run workflow**, select
`main` and enter `run_id`. The equivalent command is:

```powershell
gh workflow run submit-microsoft-store.yml `
  --repo openclaw/openclaw-windows-node --ref main `
  -f run_id=RELEASE_RUN_ID
```

The main workflow sends exactly `product=companion` and the source `run_id` to
`windows-store-publish.yml` on central `main`. It neither rebuilds the package
nor republishes the GitHub release. Manual requests do not require
`MSSTORE_ENABLED`, but always require central source authorization and
release-manager publication approval.

Open the linked central Actions workflow, inspect the matching product and
source run, and review its source commit, release version, run attempt,
artifact identity, and bundle hash before approving publication. Approval of
stable promotion here does not substitute for central Store approval.

## Enable automatic requests

After the manual central path and release acceptance are verified, set the
**repository** Actions variable `MSSTORE_ENABLED` to `true`. Its retained name
now enables automatic requests, never automatic Store publishing. The main
workflow cheaply filters successful stable/correction-tag CI runs and stable
promotion runs with a current, unexpired release witness before minting the
request token. Preview-only promotion, PR, alpha, ordinary-main, failed, and
foreign-repository runs do not request publication. The central
publisher still makes the authoritative release and artifact decision.

Unset the variable or set it to `false` to pause future automatic requests.
This does not cancel an accepted central request or an active Store upload.
Native request concurrency does not replace central per-product publication
serialization or approval.

## Verify and recover

The native workflow summary confirms only that GitHub accepted the central
request. Retain that workflow URL and the corresponding central run URL. The
central publisher owns the approval, submission receipt, exact bundle SHA-256,
Store product and submission IDs, metadata-preservation evidence, and commit
result. Partner Center's `CommitStarted` means commit accepted, not certification
or public rollout completed. Follow those states in Partner Center.

If request dispatch fails or its response is lost, inspect central Actions for
an existing request before retrying. If central source verification fails,
resolve the reported source, witness, or artifact issue rather than bypassing
it. If a bundle expired, produce and validate a new candidate through the
release process.

For Store draft or ambiguous-commit recovery, use the central publisher's
runbook and Partner Center state. Do not rerun a possibly accepted submission
or restore native Store credentials. A denied or waiting central approval is
not a failed build and must not cause the native release to be rebuilt.

## Source binding

The native release pipeline still records `store-release-source-<attempt>` only
after stable GitHub publication and bundle validation succeed. The central
publisher verifies that witness against the canonical source run, its current
attempt, the published stable tag and commit, and the downloaded bundle hash.
Promotion keeps its product source separate from the main pipeline commit.
Releases predating the witness cannot be selected. This request adapter does
not duplicate that authorization policy or accept caller-supplied product
identities, artifact hashes, scripts, or publishing credentials.
