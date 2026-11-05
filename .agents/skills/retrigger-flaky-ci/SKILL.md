---
name: retrigger-flaky-ci
description: "Triage failed CI on a pull request or the active branch, decide whether the failures come from its changes, and rerun only failures that are unrelated. Use when asked to retrigger, rerun, or kick CI on a PR with failing checks."
---

# Retrigger flaky CI

Use this skill when a user asks to retrigger, rerun, or restart CI for a pull
request, or for the PR backing the current branch. The request authorizes a
rerun of failed jobs on that PR only. It does not authorize pushing, editing
the PR, changing labels, or rerunning other PRs.

Never rerun first and investigate later. Read the failure, compare it with the
PR's changes, then either warn the user or rerun.

## 1. Resolve the PR and its changes

Resolve the PR by number, or from the current branch when no number is given:

```powershell
gh pr view <number> --json number,title,headRefName,headRefOid,files,statusCheckRollup,url
gh pr view --json number,title,headRefName,headRefOid,files,statusCheckRollup,url
```

Record the PR head SHA and the changed file list. When the request refers to the
active branch, also compare local work with the PR:

```powershell
git rev-parse HEAD
git status -sb
git fetch origin main --quiet
git diff --stat origin/main...HEAD
```

If local `HEAD` differs from the PR head, or there are uncommitted changes, the
CI run did not test them. Say so, and judge the failure against the PR head
that CI actually ran.

Pull request runs of `Build and Test` check out `refs/pull/<number>/merge`, so
they include the current `main`. A failure can come from `main` rather than from
the PR.

## 2. Read the actual failures

List the failing checks from `statusCheckRollup`. `CI Gate` only aggregates the
required jobs. Look at the jobs it reports as failed instead of `CI Gate` itself.
If checks are still queued or running, report that and stop. A partial run is
not grounds for a rerun.

Read the failed job logs:

```powershell
gh run view <run-id> --log-failed
gh run view <run-id> --job <job-id> --log-failed
```

Find the specific failure in each failing job: `[FAIL]` and `Failed <TestName>`
lines with their `Error Message` and stack trace, `error CS` or `error MSB`
build errors, `Test host process crashed`, validator output, or the step that
reported `##[error]`. Record the test or step, the error, and the top stack
frames that belong to the repository.

## 3. Decide whether the PR caused the failure

Treat a failure as **related** if any of the following is true:

- It is a build, analyzer, or compile error in a changed file, or in code that
  calls an API the PR changed.
- The failing test file is in the PR diff, or the test directly exercises a
  production owner, method, string resource, or contract the PR changed.
  Map the repository frames in the stack trace to the diff.
- It is a deterministic assertion about behavior, copy, localization,
  architecture ledgers, docs, skills, proof pools, or CI classification that
  the PR touched.
- It fails the same way on more than one attempt of the same head SHA.
- It reproduces locally on the PR head with a focused run, for example
  `dotnet test <project> --filter "FullyQualifiedName~<TestName>"`.

Treat a failure as **unrelated** only with positive evidence, such as:

- The failing code path does not touch anything in the diff, and the diff
  adds no startup, background, or Gateway traffic that could reach it.
- The same test or job fails on `main` or on unrelated PRs:

  ```powershell
  gh run list --workflow "Build and Test" --status failure --limit 25 --json databaseId,headBranch,event,createdAt,displayTitle
  gh run view <other-run-id> --log-failed
  ```

- The failure is listed in the [CI failure backlog](../../../docs/CI_FAILURES.md).
- It has the shape of a timing or infrastructure failure: a UI Automation
  element briefly missing (`NullReferenceException` inside a wait lambda), a
  `TimeoutException` from a fixture wait, a wall-clock limit on a loaded
  runner, a crashed test host, a network or restore error, a runner shutdown,
  or a missing hosted tool.

A timeout counts as flaky only when the waited-for behavior is outside the
diff. A timeout in a path the PR changed is a related failure until it is
reproduced or ruled out locally.

If the evidence is mixed and a focused local run is possible, run it. If it
remains ambiguous, do not rerun. Report the uncertainty and ask the user.

If the failure is unrelated but deterministic on `main` (it fails on every
recent `main` run), a rerun will not help. Report it as a `main` breakage
and do not rerun.

## 4a. Related: warn and do not rerun

Do not rerun. Tell the user that the failures come from the PR's changes. For
each failure, give the job, the test or step, the error, and the changed file or
owner that connects to it. Do not edit code unless the user asks for a fix.

## 4b. Unrelated: summarize, then rerun failed jobs

Show a short table before rerunning:

| Job | Failing test or step | Error | Why it is unrelated |
|---|---|---|---|

Then rerun only the failed jobs of each failed run. Rerunning a job also
reruns the jobs that depend on it, including `CI Gate`:

```powershell
gh run rerun <run-id> --failed
gh run view <run-id> --json status,attempt,url
```

Report the run URL and the new status. Do not wait for the rerun to finish
unless the user asks. If `gh run rerun` is refused, for example because of
missing permission or a fork run that needs approval, report the error as is.

Do not rerun the same failure on the same head more than once. A second
identical failure on the same SHA reclassifies it as related, or as a real
`main` breakage to record in the [CI failure backlog](../../../docs/CI_FAILURES.md).

## Report

- PR number and title, head SHA, and whether local work matches it.
- Each failing job with its test or step and error.
- The related or unrelated decision for each failure, with its evidence (diff
  overlap, other runs with the same failure, backlog entry, local reproduction).
- The action taken: no rerun with a warning, or the rerun command and the new
  run status.
