# Windows Chrome bootstrap

One packaged self-contained `OpenClaw.BrowserBootstrap.exe` owns Windows registration and transports Chrome native v1 requests. It does not implement a relay or replace Gateway authority.

- `--manage` accepts one bounded strict JSON request through stdin followed by EOF, and emits one bounded JSON receipt plus LF and a matching exit code. It never accepts paths or JSON through extra argv.
- `companion-managed-wsl` uses the fixed current-user pipe and existing active-Gateway, connection intent, preference, generation and managed-distro provenance owners.
- `native-windows-cli` invokes only its explicitly bound, admitted Windows Node/CLI and leaves config, credentials, origin and relay policy with canonical TS.
- Missing runtime/Companion never selects the other topology. Existing conflicting/legacy registrations are preserved, not migrated.
- The current-user Known Folder owns private immutable generations. The executable, binding and manifest hashes, current SID, DACLs, canonical path and origin links must validate. Complete Chrome/Chromium registry views are verified before any optional owned Store request.
- Management EOF completes a request. Chrome EOF revokes pending transport work.
- Installer and uninstaller use the same bounded native-pipe client in `scripts/BrowserBootstrapManagement.iss`; no credentials, shell pipeline, policy override or secondary writer is involved.

The cutover combines Peter Steinberger’s managed-WSL integration and fuller-stack-dev’s canonical Windows transport/setup contribution. Preserve both contributor trailers when publishing reused work.

## Proof boundary

Portable tests validate the agreed request/response and metadata grammar, state tuples, partial failures, framing and cancellation. Windows CI must separately prove the published executable, ACL/registry behavior and installer compilation. The executable registry/IPC smoke uses synthetic pairing material and is not production WSL/Chrome permission or signed installer upgrade proof. Dedicated Windows production-path evidence remains required before merge.
