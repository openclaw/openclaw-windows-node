# Latest-stable stored-profile fixture

These are **public, disposable synthetic test credentials**, not user data or a production key. Never use this identity outside a loopback test. The token strings and `.invalid` endpoint are intentionally non-production.

The files were emitted by the released `DeviceIdentity` and `GatewayRegistry` writers from stable `v2026.9.8-1` (source `2ba89f377ce8ce3f5ddfc452e4d509329f109114`, assembly version `2026.9.8.0`). The official release asset SHA-256 was verified before extracting the writer assemblies. `provenance.json` records the asset, assembly and generated-file hashes; `producer.cs.txt` records the exact producer. No old assembly is shipped here and tests make no release-download request.

The current-reader test loads this record and unchanged signing identity, verifies an Ed25519 signature with the stored public key, authenticates with the stored operator token (not a shared-token fallback), loads history, and retains/reconnects through the new owner. Only the in-memory endpoint is repointed to that test's ephemeral loopback server; the original stored files must remain byte-identical.

This establishes the stored-record/key compatibility path. It is not proof of an installed Windows upgrade, native rendering or a real Gateway/provider.
