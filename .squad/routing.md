# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Porting strategy, scope, architecture, code review | Kranz | "what do we port?", "review Aaron's diff", reviewer gate |
| WSL gateway plumbing, identity/tokens, gateway client, validation scripts | Aaron | `LocalGatewaySetup.cs`, `OpenClawGatewayClient.cs`, `DeviceIdentity.cs`, `validate-wsl-gateway.ps1` |
| Onboarding UX, WinUI3 XAML, forked warning page, layout | Mattingly | warning page, Setup-locally progress page, page navigation |
| Build, tests, validation runs, screenshot verification, baselines | Bostick | `./build.ps1`, `dotnet test`, `validate-wsl-gateway.ps1`, screenshots |
| Session logging | Scribe | Automatic — never needs routing |
| Backlog scan, idle-watch | Ralph | "Ralph, go", "what's on the board?" |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
