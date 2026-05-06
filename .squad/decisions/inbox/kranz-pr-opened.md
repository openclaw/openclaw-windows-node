# Kranz — Draft PR opened

**Date:** 2026-05-05
**Squad:** Apollo 13
**Author:** Kranz (Squad Lead)

## Branch push confirmation

- **Branch:** `feat/wsl-gateway-clean`
- **Pushed to:** `fork` remote (`indierawk2k2/openclaw-windows-node`) — `origin` (openclaw/openclaw-windows-node) denied write to indierawk2k2 (403), so the PR is cross-repo from the fork into upstream `master`. Standard pattern; no policy issue.
- **Commits:** 25 on top of `origin/master @ 871b959`
  - Plan said 24; actual is 25 (the extra is `4d36dcd` Bug 3 fix landed after the verdict was written — already covered in the verdict scope).
- **Last SHA:** `6e532f7120e575f427167a180ce1b1448e42a7d1`
- **Working tree:** clean (only untracked file is `.squad/decisions/inbox/kranz-final-push-readiness-verdict.md`, intentional)

## PR

- **URL:** https://github.com/openclaw/openclaw-windows-node/pull/274
- **State:** DRAFT ✅
- **Base:** `openclaw/openclaw-windows-node:master`
- **Head:** `indierawk2k2:feat/wsl-gateway-clean`
- **Title:** `feat(onboarding): WSL gateway local-loopback onboarding — clean port from PR #241 prototype`

## Body summary

Comprehensive PR description covers: 1-paragraph overview + prototype lineage; "What's included" with all 8 phases + Bug 1/2/3 + localization + scripts + docs commits; Bostick Round 6 verification with screenshot path; Tray 524/524 (+77) and Shared 1180/1180 test counts; full Bug 1 6-commit journey with the meta-lesson on CLI v2026.5.3-1 exit-code-vs-JSON; architectural callouts for `IPendingDeviceApprover`, `RenderSnapshot` value-equality fix, and two-stage approve flow; 5-item follow-up punch list; 3 open questions for Mike; explicit "why no squash" justification; Co-authored-by trailer.

Body file kept at `.squad/decisions/inbox/pr-body.md` for reference.

## Notes for Mike — what to review

**Highest-value review areas (in order):**
1. **`LocalGatewaySetup` engine** (`src/OpenClaw.Tray/Setup/LocalGatewaySetup.cs`) — the heart of Phase 3, where the two-stage approve + retry budget lives. This is the code that took 6 commits to get right against CLI v2026.5.3-1.
2. **`RenderSnapshot` value-equality** in `LocalSetupProgressPage` — confirm the pattern is one we want to spread.
3. **`IPendingDeviceApprover` seam** + Phase 14 wiring (Bug 3 fix `4d36dcd`) — confirm the seam shape before we lock it in.
4. **Bostick Round 6 screenshot** at `visual-test-output\bostick-round6\06-onboarding-complete.png` to confirm the e2e verdict matches your eye.

**Decisions pending from Mike (block follow-up work, not this PR):**
- Operator-pair retry budget — configurable or hardcoded?
- WelcomePage removal — final or revisit for telemetry?
- Translation strings — defer or ship-and-iterate?
- Uninstall plan PR — 8 open questions in `.squad/decisions/inbox/uninstall-questions-for-mike.md` (separate branch, blocked on these)
- Push-vs-merge timing for this PR once review lands

## Guardrails honored

- ✅ DRAFT PR (`--draft` flag used)
- ✅ NOT merged
- ✅ NOT pushed to master (cross-repo PR from fork into upstream master, awaiting review)
- ✅ No source code modified in this final action
- ✅ OpenClawGateway distro untouched
