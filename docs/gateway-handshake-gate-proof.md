# Gateway handshake gate: captured wire proof

Evidence artifact for PR #1425 (fixes #1418), captured at branch tip `5c02ab5`
from a real `dotnet test` execution of `tests/OpenClaw.Shared.Tests` on the
production client code path (`OpenClawGatewayClient` over real WebSockets
against a loopback gateway server). No live gateway or private endpoint was
contacted: the server under test runs inside the test process.

**Capture method:** `dotnet test --filter FullyQualifiedName~HandshakeGate
--logger "trx;LogFileName=proof.trx"`; the `[trace]` lines below are the
unmodified `ITestOutputHelper` output embedded in the TRX results file, with
auth tokens, device ids, and device key material redacted (all values are
test-only). Request ids and test nonces are harmless and kept verbatim.

**Suite status at the captured tip:** affected filter set 278/278 passed
(GatewayClient + live round-trip + AssistantMedia + ShutdownClassification);
protocol drift/contract/closure safety filters 30/30; `Tray.Tests` unchanged
versus a clean base run (18 pre-existing Linux-env failures, identical on
base `6466a04`; CI runs that suite on Windows).

The two scenarios below demonstrate the full gate lifecycle: suppression
before hello-ok (nothing on the wire), the gate holding while disconnected
(suppressed again, submission reported as false, not success), and the
client's **real auto-reconnect path** (receive-loop exit →
`ReconnectWithBackoffAsync`) re-running challenge → connect → hello-ok before
the gate reopens and the mutation reaches the wire.

## Scenario 1: mutation submitted before hello-ok (withheld, zero frames)

**Test:** `HandshakeGate_SuppressedMutation_ReportsNotSentNotSuccess` - outcome **Passed** (duration 00:00:00.1175257)

```text
[trace] socket open; challenge delivered; connect request captured (unanswered): {"type":"req","id":"3e7f62bf-d2e5-43b8-9145-0ecc2ed6a7c9","method":"connect","params":{"minProtocol":3,"maxProtocol":4,"client":{"id":"cli","version":"18.6.0","platform":"windows","deviceFamily":"Windows","mode":"cli","displayName":"OpenClaw Windows Tray"},"role":"operator","scopes":["operator.admin","operator.pairing"],"caps":[],"commands":[],"permissions":{},"auth":{"token":"***REDACTED***"},"locale":"en-US","userAgent":"openclaw-windows-tray/18.6.0","device":{"id":"***REDACTED***","publicKey":"***REDACTED***","signature":"***REDACTED***","signedAt":0,"nonce":"trace-challenge"}}}
[trace] ResetSessionAsync while handshake pending returned: False
[trace] no sessions.reset frame observed on the wire
```

## Scenario 2: reconnect flow (drop → gate holds → auto-reconnect → mutation sends after hello-ok)

**Test:** `HandshakeGate_Reconnect_SuppressesEarlyMutationThenSendsAfterHelloOk` - outcome **Passed** (duration 00:00:01.3212202)

```text
[trace] socket #1 open, challenge withheld: sessions.reset suppressed (submission=false); no frame reached the wire
[trace] challenge delivered; connect captured (unanswered): {"type":"req","id":"81269861-e101-4930-9de6-e3c17d78d6b1","method":"connect","params":{"minProtocol":3,"maxProtocol":4,"client":{"id":"cli","version":"18.6.0","platform":"windows","deviceFamily":"Windows","mode":"cli","displayName":"OpenClaw Windows Tray"},"role":"operator","scopes":["operator.admin","operator.pairing"],"caps":[],"commands":[],"permissions":{},"auth":{"token":"***REDACTED***"},"locale":"en-US","userAgent":"openclaw-windows-tray/18.6.0","device":{"id":"***REDACTED***","publicKey":"***REDACTED***","signature":"***REDACTED***","signedAt":0,"nonce":"trace-challenge"}}}
[trace] hello-ok processed; sessions.reset on the wire: {"type":"req","id":"e502fbba-2ee3-4275-96f7-71e1d14d562f","method":"sessions.reset","params":{"key":"agent:main:main"}}
[trace] server sent one-way Close frame on socket #1; client drop observation begins
[trace] client observed drop: handshake snapshot cleared, readiness false
[trace] sessions.reset during disconnected window: withheld (submission=false)
[trace] socket #2 accepted; challenge auto-resent; connect captured: {"type":"req","id":"0b1ae270-2fd8-424f-8025-08ff33c2b2fa","method":"connect","params":{"minProtocol":3,"maxProtocol":4,"client":{"id":"cli","version":"18.6.0","platform":"windows","deviceFamily":"Windows","mode":"cli","displayName":"OpenClaw Windows Tray"},"role":"operator","scopes":["operator.admin","operator.pairing"],"caps":[],"commands":[],"permissions":{},"auth":{"token":"***REDACTED***"},"locale":"en-US","userAgent":"openclaw-windows-tray/18.6.0","device":{"id":"***REDACTED***","publicKey":"***REDACTED***","signature":"***REDACTED***","signedAt":0,"nonce":"rt-challenge"}}}
[trace] gate reopened after reconnect; sessions.reset on the wire: {"type":"req","id":"415c05df-6e81-4297-9554-ea62575a2f3a","method":"sessions.reset","params":{"key":"agent:main:main"}}
```

**Wire-level assertions backing the trace** (same tests, same run):

- `Assert.Throws<InvalidOperationException>(() => server.FrameFor("sessions.reset"))`
  after the pre-handshake submission; no frame was captured on the wire.
- After the one-way server Close: `Assert.False(client.IsConnectedToGateway)`
  and the disconnected-window submission returns `false` with no captured
  frame.
- After auto-reconnect: `Assert.True(client.IsConnectedToGateway)` only
  following the fresh hello-ok, and the second `sessions.reset` is captured
  on the wire.
