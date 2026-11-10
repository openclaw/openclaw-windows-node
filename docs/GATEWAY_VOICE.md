# Use connected Gateway voice

Companion can use its connected Gateway's speech provider, including Fish Audio,
for native chat Read Aloud and automatic reply speech. Provider keys stay on the
Gateway. Inward Windows node and local MCP speech keep their local provider.

## Enable and use

1. Connect Companion to the intended Gateway in **Connection** settings.
2. Configure working speech on that Gateway. For Fish Audio, follow the
   [provider documentation](https://docs.openclaw.ai/providers/fish-audio).
3. Approve an operator connection that advertises `tts.speak` and has
   `operator.write` authority or a supported superset. Companion does not widen
   the connection's grant or retrieve provider secrets.
4. Enable text-to-speech in **Permissions**, then enable **Use connected Gateway
   voice** in voice settings. Reply text goes to the Gateway and its provider.
5. Use Read Aloud on a message. **Read responses aloud** enables automatic speech
   for the active conversation. The speaker mute suppresses automatic replies;
   manual Read Aloud still works. Muting also stops admitted speech.

The chat speech status shows synthesis, the actual returned provider, or a finite
error. Use its **Stop** button to cancel pending or playing speech. Conversation
or Gateway changes, disconnect, page suspension and permission removal retire
pending results. Reconnection or restored permission does not resume them.

There is no automatic local fallback or retry. Turn Gateway voice off to return
chat to the existing local provider. Stop or a timeout cannot undo provider usage;
check the Gateway before explicitly retrying an uncertain request.

## Supported contract

Companion sends one `tts.speak` request containing only text. The Gateway owns
provider selection and fallback. Readiness catalogs and a dedicated Gateway
preview are outside this version; synthesis failures appear in chat.

Companion admits up to 4096 text characters and 8 MiB of inline MP3 or WAV.
Smaller Gateway text limits still apply. Raw PCM, Opus/Ogg and WebM are unsupported.
Synthesis is bounded to 60 seconds and playback to five minutes. Text is neither
chunked nor replayed automatically.

## Development validation

Follow [Windows testing](TEST_COVERAGE.md) for the full build and required Shared
and Tray suites. Focused cases are `GatewaySpeechTests`,
`GatewaySpeechPlaybackTests`, `GatewaySpeechPermissionTests`, and the existing
profile round-trip tests. The permission suite exercises actual persistence and
coordinator policy with substituted transport/player inputs; it is not rendering
or audible-playback proof.

For runtime proof, follow the [proof skill](../.agents/skills/openclaw-proof-validation/SKILL.md)
and use a verified isolated build, approved Gateway/provider, and synthetic text.
Collect one successful Read Aloud with actual provider and playback observation,
an automatic reply, Stop, a controlled pending-result retirement, and the changed
toggle/status accessibility state. Verify local node/MCP routing separately when
claiming it. A screenshot does not prove sound, and settings round trips do not
prove an installer upgrade. Preserve normal profiles and shared runtimes; the
launcher's `-Isolated` option alone does not isolate or authorize Gateway changes.
