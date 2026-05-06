# Hockney — PR #274 Contamination Audit

**Author:** Hockney (Tester / QA, second-string for Bostick)
**Date:** 2026-05-05T07:00:00-07:00
**Scope:** `origin/master..6e532f7` on `feat/wsl-gateway-clean`
**Requested by:** Mike Harsh — concern that prototype-worktree state (`openclaw-windows-node`, branch `pr-241-feedback-fixes`) leaked into the PR after Bostick's `dotnet run` resolved to the prototype binary.
**Mode:** READ-ONLY. No files modified. Tray / Gateway distro / 17 prototype distros not touched.

---

## Diff inventory

37 files changed, **all text** (`.cs`, `.csproj`, `.resw`, `.ps1`, `.md`). Zero binary files. Zero `bin/`/`obj/`/`.exe`/`.dll`/`.pdb`/`.user`/`.suo`/`.vs/`/`.vscode/`/`node_modules/`/`packages/`/`.nuget/`/`artifacts/reset-backups/`/`visual-test-output/` paths in the diff.

---

## Per-category findings

| # | Category | Verdict | Notes |
|---|---|---|---|
| 1 | Hardcoded absolute paths (`C:\Users\mharsh\...`) | **CLEAN** | Zero matches in diff. |
| 2 | References to prototype branch `pr-241-feedback-fixes` | **CLEAN** | Zero matches in diff. |
| 3 | References to prototype worktree `openclaw-windows-node` | **CLEAN** | Zero matches in diff. |
| 4 | Stale build artifacts (`bin/`/`obj/`/`*.exe`/`*.dll`/`*.pdb`/`*.cache`/`*.user`) | **CLEAN** | None in diff. `.gitignore` covers `[Bb]in/`, `[Oo]bj/`, `*.user`, `*.suo`, `*.[Cc]ache`, `*.pdb` correctly. |
| 5 | Local user state (`*.suo`, `.vs/`, `.vscode/settings.json`, `*.user.props`, `node_modules/`, `packages/`, `.nuget/`) | **CLEAN** | None in diff. All covered by `.gitignore`. |
| 6 | `%APPDATA%`/`%LOCALAPPDATA%` snapshots (`settings.json`, `paired.json`, `pending.json`, `setup-state.json`, `device-key*`) | **CLEAN** | Zero such filenames in the diff. No real Tokens / BootstrapTokens / deviceIds committed. |
| 7 | Test fixtures referencing local paths | **CLEAN** | `git diff origin/master..HEAD -- 'tests/**'` shows zero hits for `C:\`, `mharsh`, `AppData`, `LocalAppData`. |
| 8 | Reset / backup artifacts (`artifacts/reset-backups/<timestamp>/`) | **CLEAN** | None in diff. `artifacts/` is in `.gitignore`. |
| 9 | `visual-test-output/` PNG dumps | **CLEAN** | None in diff. Directory is in `.gitignore`. |
| 10 | `.squad/` references in app/test source | **CLEAN** | `git diff -- 'src/**' 'tests/**'` for `.squad` returns zero. The committed `.squad/` planning docs are decision artifacts only and are not imported by any source. |
| 11 | Worktree git state / SHA constants baked into source | **CLEAN** | No `.git/` artifacts; no `6e532f7` or other commit SHAs hardcoded in `src/**`. |
| 12 | Cross-worktree symlinks/junctions | **CLEAN** | All diff entries are normal text files under `src/`, `tests/`, `scripts/`, `docs/`, `.squad/decisions/inbox/`. None are reparse points. |

### Single string-scan hit (non-issue)

`.squad/decisions/inbox/aaron-uninstall-plan.md` line:
```
**Worktree:** `..\openclaw-wsl-gateway-clean` @ `feat/wsl-gateway-clean` (16 commits since `871b959`)
```

This is a **relative** worktree reference inside a planning markdown — names the *clean* worktree (the one we're shipping from), not the prototype. It does not embed `C:\Users\mharsh`, does not reference `openclaw-windows-node`, and is not consumed by any code. **Not contamination.** LOW / cosmetic at worst.

---

## Triage

| Severity | Count | Items |
|---|---|---|
| BLOCKER | 0 | — |
| MEDIUM | 0 | — |
| LOW | 0 (1 cosmetic note) | `.squad/decisions/inbox/aaron-uninstall-plan.md` mentions the clean worktree path by relative name. Optional to scrub; harmless. |

**No files need to be reverted, removed, or modified to merge.**

---

## Bostick's `dotnet run` observation — explained

The fact that `dotnet run` from the clean worktree produced a binary under `openclaw-windows-node\...` is a **launcher-environment artifact**, not committed contamination:

- Likely causes (not in diff): a stale `MSBUILDPROJECTEXTENSIONSPATH`/`OutputPath` env var inherited from a previous shell rooted in the prototype worktree, a developer-local `Directory.Build.props` outside this repo, or `dotnet` resolving a project reference via a `global.json` / SDK pin from a parent directory.
- None of the 37 changed files contain `<OutputPath>`, `<BaseOutputPath>`, `<IntermediateOutputPath>`, or absolute path overrides pointing at the prototype tree (verified — no `.csproj`/`.props`/`.targets` matches for the contamination strings).
- Recommendation for Bostick-13: launch from a fresh shell with `cd <clean-worktree>` and no inherited env, or pass `-p:BaseOutputPath=...` explicitly. This is an environment-hygiene fix, not a PR fix.

---

## Final verdict

**PR CLEAN.**
**Confidence: HIGH.**

37-file diff is text-only, contains zero hardcoded user paths, zero prototype-branch / prototype-worktree references in source, zero AppData snapshots, zero build artifacts, zero backup dumps, zero visual-test PNGs, and zero `.squad/` imports from app code. The single string-scan hit is a relative-path mention of the *clean* worktree inside a planning markdown — informational, not contamination.

Mike's concern was warranted given Bostick's launcher symptom, but the symptom is an environment-side issue (stale shell / inherited MSBuild state), not anything that committed into PR #274. **Safe to merge from a contamination standpoint.**

— Hockney
