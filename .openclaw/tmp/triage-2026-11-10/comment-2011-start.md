I am taking this up as a small, focused fix during the issue-backlog cleanup. Current main still uses the Python-3.14-only `\z` spelling and relies on `Path.is_junction()`, which is unavailable on Python 3.11.

The planned repair is to use the compatible `\Z` anchor and refuse unsupported interpreters clearly before native initialization, inventory, or any mutation. I will keep the existing junction and deletion safeguards intact and add regression coverage for startup and the early refusal.

Keeping this issue open until the fix is reviewed and merged. The fix is being prepared in an isolated worktree; Windows validation and a draft PR are still pending. No wipe of a real profile is part of this work.

<!-- roboclaw-issue-cleanup:2026-11-10:2011:start -->
