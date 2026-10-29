# Use connected Gateway voice

Companion can delegate native chat Read Aloud, automatic reply speech and the
settings preview to the connected Gateway's configured text-to-speech provider.
This is opt-in. Local providers and inward node/MCP speech remain unchanged.

## Prerequisites and selection

1. Connect Companion to the intended Gateway in **Connection** settings.
2. Configure a speech provider on that Gateway. For Fish Audio, use the official
   plugin and [provider documentation](https://docs.openclaw.ai/providers/fish-audio).
   Provider discovery alone does not establish working credentials or quota.
3. The operator connection must advertise `tts.status` and `tts.speak` and have
   read/write authority (`operator.read` and `operator.write`, or a supported
   superset). Companion does not broaden permissions or retrieve Talk secrets.
4. Enable text-to-speech in **Permissions**. In voice settings, enable **Use
   connected Gateway voice**, then **Check readiness** and **Preview Gateway voice**.

Text is sent to the Gateway and its speech provider. Provider keys stay on the
Gateway. Selection does not modify Gateway-wide TTS settings, voice or model.
Read Aloud works while automatic reply speech is muted. Muting the speaker also
stops pending/current delegated playback. **Read responses aloud** controls
whether replies are spoken automatically.

There is **no automatic local fallback**. Turn Gateway voice off to return chat
to the existing local-provider behavior. The local provider selector remains the
default for inward Windows node/MCP speech even while delegation is enabled.

## Readiness, providers and limits

Readiness reads `tts.status` without synthesizing. An available configured
provider must be in the selected provider/fallback chain. Gateway automatic TTS
being off does not prevent explicit Companion speech. The actual audio response
identifies the provider that synthesized the clip. If it differs from the earlier
selection, both are shown. This can mean fallback or a settings change between
requests; the protocol does not return an authoritative fallback reason.

Failures appear in settings or the chat speech status bar. Nothing is retried
automatically. Stop or a timeout cannot roll back provider usage, but retired
results cannot start playback. Check the Gateway before explicitly retrying an
uncertain synthesis outcome.

Companion accepts up to 4096 text characters and 8 MiB of inline MP3 or WAV.
Smaller Gateway text limits still apply. Raw PCM, Opus/Ogg and WebM are not
admitted by this first implementation. Readiness, synthesis and playback are
bounded to 10 seconds, 60 seconds and five minutes respectively. Companion sends one synthesis RPC and does not chunk or replay it; the Gateway
retains control of its own configured provider fallback chain.

## Native Windows validation

Use task-owned Windows baseline and candidate worktrees. Do not reset existing
work, replace normal settings, register another Gateway, or stop unowned apps.
From each worktree root, check prerequisites without installing anything:

```powershell
.\scripts\setup-dev.ps1 -CheckOnly
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet build .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj
dotnet build .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

The project builds prevent first-run `--no-restore` from silently executing no
tests. Focused filters are `FullyQualifiedName~GatewaySpeechTests` in Shared and
`FullyQualifiedName~GatewaySpeechPlaybackTests` in Tray. They do not replace the
full build and suites. Also run the changed-controls
[accessibility lane](ACCESSIBILITY.md).

For a current-build isolated app, with use of the installed Store Gateway
runtime already authorized:

```powershell
$env:OPENCLAW_SKIP_UPDATE_CHECK = '1'
.\run-app-local.ps1 -NoBuild -Isolated -UseStoreGateway
```

This skips development Gateway packaging/registration, but still launches a live
app. Select the approved remote test Gateway in that isolated profile. Do not
copy provider keys to Windows. If the installed runtime is unavailable, use the
[proof runtime guidance](../.agents/skills/openclaw-proof-validation/SKILL.md#select-the-runtime-and-its-authority)
to choose an authorized launch of the exact built executable. Do not omit the
Store flag and implicitly register a development runtime.

### Capture the changed behavior

Use only synthetic text, such as "Synthetic Windows voice check. One, two,
three." Record the source/diff and binary identities, Windows architecture,
Gateway version and actual provider ID. Never capture private chat, credentials,
settings files, or unrelated desktop content.

1. **Before:** capture baseline voice settings without delegation and verify
   the existing local provider preview.
2. **After:** capture candidate settings with Gateway voice enabled and Fish
   ready. Preview once. Verify the actual provider is `fish-audio` and audio
   is heard through Windows. Fallback audio does not establish Fish proof.
3. In a fresh synthetic conversation, use Read Aloud with automatic speech
   muted, then enable automatic speech and verify one synthetic reply. Capture
   the provider information in the chat speech status bar.
4. Stop during synthesis and playback. Late results must not play. Repeat
   with disconnect/reconnect, chat/Gateway selection and app close. Reconnection
   must not resume old requests. Mute during both synthesis and playback, then
   unmute: neither late audio nor stale success/error UI may return. Leave and
   reopen the retained chat page after a failure and verify it shows current state.
5. With microphone use already authorized, verify playback mutes capture and
   capture resumes after completion, Stop and error. Keep all input synthetic.
6. Disable delegation and verify the previous local provider still works.
   Protocol tests cover missing method/scope/provider and invalid audio. Use
   dedicated approved test connections for corresponding native UI negatives,
   or mark them not verified. Exercise an uncertain synthesis timeout without
   automatically retrying or falling back locally. Do not reconfigure production
   merely to force errors.
7. While delegation is enabled, follow the existing
   [local MCP guide](../.agents/skills/windows-app-mcp/SKILL.md) to discover and
   invoke inward `tts.status` and `tts.speak` with synthetic text. Bind the client
   to this run's isolated profile/endpoint. Confirm they still report/use the
   configured local provider, not the remote Fish route.

A screenshot proves UI, not audibility. Use inspected video with synthetic
system audio or an explicit human playback observation. Keep mocked protocol,
real remote Gateway synthesis and audible native playback as separate claims.
Publish one useful before/after pair plus playback evidence under
[the proof-pool guidance](PROOF_POOLS.md). Until captured, native proof is pending.
