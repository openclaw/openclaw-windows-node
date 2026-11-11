# Submit a release to Microsoft Store

Use this guide to submit an already validated release bundle, retry a failed
Store delivery without rebuilding, or enable automatic Store delivery. This
workflow does not change signing, release approval, or install/upgrade acceptance.
GitHub releases and the existing unsigned Store artifact handoff remain available.

## Configure the publishing identity

1. Link the correct Microsoft Entra tenant to Partner Center. Add a dedicated
   Entra application to Partner Center with the **Manager (Windows)** role.
   The Store product must already have a completed, published first submission
   and completed age ratings. Do not reuse the Azure code-signing identity.
2. Add one federated credential to that Entra application:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Audience: `api://AzureADTokenExchange`
   - Subject: `repo:openclaw/openclaw-windows-node:environment:microsoft-store`
   Verify the repository's current OIDC subject configuration before provisioning;
   immutable-subject settings affect the exact subject. The dedicated application
   must have no client secrets, certificates, or other federated writers.
3. In the repository's **Settings > Environments > microsoft-store**, set these
   environment variables (not secrets):
   - `MSSTORE_TENANT_ID`: Entra tenant GUID.
   - `MSSTORE_CLIENT_ID`: dedicated application's client GUID.
   - `MSSTORE_APPLICATION_ID`: Partner Center Store product ID, not the client GUID.
4. Allow deployments from the `main` **branch** in this environment. The submission
   workflow always runs trusted main code, including when it consumes a tag's
   bundle. Restrict the environment to main, and configure release-owner approval
   if required by your publishing policy. Tag-only protection will block this job.
5. Leave the repository variable `MSSTORE_ENABLED` unset until the first manual
   submission and the required Store install/upgrade acceptance have succeeded.
   No persistent Store password is required.

## Submit manually

Merge the workflow onto main first. Select the successful run ID for a stable/correction tag **Build and Test** run or an approved **Promote alpha to stable** run.
The ID is the number after `/actions/runs/` in its URL. The source run must have
completed successfully and retained its unexpired authorized Store bundle.

From **Actions > Submit Microsoft Store > Run workflow**, select `main` and
enter `run_id`. The equivalent CLI command is:

```powershell
gh workflow run submit-microsoft-store.yml `
  --repo openclaw/openclaw-windows-node --ref main `
  -f run_id=RELEASE_RUN_ID
```

Manual dispatch deliberately does not require `MSSTORE_ENABLED`. It uses the same
source checks, protected environment, OIDC identity, and publisher as automatic
submission. It never rebuilds a package or republishes the GitHub release.

Only the multi-architecture Store `.msixbundle` is uploaded. The publisher checks
its identity against repository policy and the actual Partner Center product
before creating a draft. It preserves listing, pricing, availability, and other
product metadata while replacing the packages with the selected bundle.

## Enable automatic delivery

After the manual rehearsal and release acceptance, set the **repository** Actions
variable `MSSTORE_ENABLED` to `true`. It must be repository-scoped because the
source job checks it before entering the protected environment. Successful eligible
release runs then trigger **Submit Microsoft Store** through `workflow_run`.
PRs, ordinary main builds, failed runs, and prereleases do not submit. Ineligible
automatic runs are skipped; selecting an ineligible run manually fails explicitly.

Remove the variable or set it to `false` to pause future automatic submissions.
This does not cancel a submission already in progress. Both manual and automatic
runs share one concurrency group and never cancel an active Store upload.

## Verify and recover

A successful submission means Partner Center returned `CommitStarted`, not that
certification or public rollout has finished. Retain the Store workflow URL and
its 90-day submission evidence artifact. The evidence includes the bundle SHA-256,
product ID, exact submission ID, metadata hashes, and commit acceptance. Follow
certification and publication in Partner Center before declaring Store delivery
complete. The current policy requests a 100% package rollout and preserves the
product's publication mode, including manual or scheduled publication.

Before dispatch, finish or explicitly discard any existing draft in Partner
Center. Automation rejects existing drafts and never replaces them. Do not edit
an API-created draft in the portal while automation runs. A failure before commit
can remove only the draft created by this run. A lost or ambiguous commit response
preserves that draft: inspect Partner Center before retrying. Never blindly rerun
a submission that may already have been accepted. If the release artifact expired,
use the release process to produce and validate a new candidate.

See Microsoft's [submission API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
and [application identity resource](https://learn.microsoft.com/en-us/windows/uwp/monetize/get-app-data).

## Source binding

The release pipeline records `store-release-source-<attempt>` only after stable
GitHub publication and bundle validation succeed. The submission workflow checks
that witness against the source run, its current attempt, the published stable
tag and commit, and the downloaded bundle hash. Promotion keeps its product source
separate from the main pipeline commit. Releases created before this witness was
introduced cannot be used as manual submission sources.
