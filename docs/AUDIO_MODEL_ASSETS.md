# Audio Model Asset Integrity

OpenClaw Companion downloads speech models and voice packages at runtime. These
assets execute inside local audio pipelines, so every shipped catalog entry is
bound to an HTTPS source and a pinned SHA-256 hash.

## Authoritative catalogs

| Asset | Source of truth | Runtime storage |
| --- | --- | --- |
| Whisper GGML models | `WhisperModelManager.AvailableModels` | `<tray-data>\models\` |
| Piper voice archives | `PiperVoiceManager.AvailableVoices` | `<tray-data>\models\piper\` |
| Silero VAD ONNX model | `SileroVadModelManifest` | Audio pipeline model directory |

The source catalogs contain the download URL, pinned hash, and approximate size
used by the UI. Do not duplicate those values in this document.

## Runtime enforcement

The download managers fail closed:

1. A catalog entry without a pinned hash is rejected before download.
2. Downloads use a temporary file.
3. The completed file's SHA-256 is compared with the catalog.
4. A mismatch deletes the partial asset and returns an error.
5. Only a verified asset is moved into its final location or extracted.

Concurrent requests for the same model or voice share one single-flight
download, preventing multiple writers from racing over the same temporary file.

`AssetHashPinningTests` guards the catalog shape by requiring a lowercase
64-character SHA-256 and an HTTPS URL for every shipped entry.

## Adding or updating an asset

1. Download the exact upstream artifact outside the application.
2. Verify the artifact identity and release provenance from an independent
   upstream source when one is available.
3. Compute the hash:

   ```powershell
   Get-FileHash .\artifact -Algorithm SHA256
   ```

4. Update the appropriate source catalog with the exact lowercase hash.
5. Run:

   ```powershell
   dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj `
     --filter AssetHashPinningTests
   ```

6. Exercise one real download and confirm a deliberately incorrect hash is
   rejected and the temporary file is removed.
7. Record the upstream release or commit and verification evidence in the
   change description.

Before every public release, re-verify every shipped audio-asset hash from the
published upstream artifact and record the evidence for release review.

## Future hardening

The current catalogs are compiled into the signed application. If the catalog
grows or needs out-of-band updates, replace the inline tables with a
versioned, signed manifest that binds URL, size, hash, model identity, and
upstream provenance. Runtime behavior must remain fail closed when the manifest
or asset cannot be verified.
