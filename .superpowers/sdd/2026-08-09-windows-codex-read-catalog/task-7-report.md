# Task 7 report

## Result

- Added canonical bounded read-only MCP descriptions for exactly `codex.appServer.threads.list.v1` and `codex.appServer.thread.turns.list.v1`.
- Added matching `winnode` skill contracts and Windows testing permission-mode documentation for Off, Read only, and Read and steer.
- Documented honest Stage 0 behavior: Read and steer exposes the same two reads because owner control is unavailable. No resume, steer, interrupt, or other write command is exposed.
- Added real MCP command execution tests proving App Server failure details and transcript/private markers do not enter stable wire errors or structured audit completions.

## TDD evidence

- MCP RED: 46 passed, 1 failed because the Codex command received the generic capability description instead of a bounded read-only description.
- CLI drift RED: 2 failed, reporting both Codex descriptions and both skill headings missing.
- Focused GREEN: `McpToolBridgeTests` passed 47/47.
- CLI metadata GREEN: `SkillMdDriftTests` passed 2/2.

## Mutation evidence

- Deleting the transcript command description failed `ToolsList_CodexCatalog_AdvertisesExactlyTwoBoundedReadOnlyCommands`.
- Adding `codex.appServer.turn.steer.v1` as a skill heading failed both skill drift tests. Both mutations were restored before the final GREEN run.

## Full CLI contract run

- Sandboxed full suite: 88 passed, 39 failed. Every failure originated from `FakeMcpServer` startup with `HttpListenerException: The handle is invalid`.
- The unrestricted retry produced no output for about five minutes and was aborted. No active test process remained.
- No broad validation was run, per the Task 7 delegation.

## Hygiene

- `git diff --check` passed.
- Added user-facing copy contains no em dash.
- No commands or write configuration paths were added.
