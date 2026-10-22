# Repository PR review configuration

[review.json](review.json) opts admitted ClawSweeper pull-request reviews into
the repository-owned [Hanselman skill](../.agents/skills/hanselman-code-review/SKILL.md)
without replacing normal Sweeper duties or its output schema. The host owns
two independent full assessments followed by one consolidation. Reviewers do
not launch another pair. Typed durable assessments and rendered consensus
replace the skill's SQL-table example; no SQL tool or database is implied.

## Activation prerequisites

Do not enable this configuration until the host supports the version 1
`skills`, `additionalPrompt` and `multiModel` contract and its strict durable
assessment readers. The operator must qualify and authorize the configured
native provider bindings. Repository configuration does not install tools,
select credentials, authorize another provider, or waive review admission.

The native model references are `anthropic/claude-opus-5-5` (Claude Opus 5.5)
and `openai/gpt-6-astra`, both requesting high reasoning. Astra also performs
consolidation. These are requested settings, not observed execution facts.
No model or provider fallback is authorized.

**Context adaptation requires approval before enabling.** The skill asks for
Astra `long_context`, while the current Sweeper adapters accept only
`contextTier: "default"`. This candidate explicitly requests native default
context and requires the review report to disclose that limitation. Default
context is not a claimed equivalent window or a completed exact long-context
Hanselman run. Obtain approval for this adaptation or wait for a qualified
long-context binding; do not add an unsupported value or silently downgrade.

## Trusted source and existing dispatch

The common authenticated Sweeper backend resolves configuration and selected
skill files from one pinned default-branch commit. PR-head configuration and
skill edits must not affect that run. Both App-webhook and OIDC target-dispatch
delivery use that backend contract; the existing
[dispatch workflow](../.github/workflows/clawsweeper-dispatch.yml) remains
unchanged and must not check out untrusted pull-request code.

The augmentation applies only after an item is admitted as a PR review,
including an authorized review comment on a PR. Ordinary issues and non-review
commands keep their existing behavior. A config change alone does not reopen
automatic review admission.

## Verify before enabling

Use the qualified producer's actual config loader against an isolated Git
fixture containing this file and the unchanged skill bundle. Verify the
pinned-base configuration wins over changed HEAD files, native materialization
retains the selected bundle, and invalid fields and `long_context` fail closed.
Schema acceptance and fixture success are not model-access or runtime proof.

Then require controlled full-assessment proof from the normal Sweeper path:
two independently attributable results and one consolidation on the same
head/base, one combined report and canonical label plan, and incomplete
status for failed or stale assessments. Record requested settings separately
from runtime facts. Do not dispatch production reviews merely to test this
configuration. Provider errors remain blockers, not reasons to change auth,
models, context settings or output limits.
