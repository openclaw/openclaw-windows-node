# Test Coverage Summary

**914 tests total** (652 shared + 262 tray) — all passing ✅

| Metric | Value |
|--------|-------|
| Total Tests | 914 |
| Passing | 914 (100%) |
| Failing | 0 |
| Framework | xUnit 2.9.3 / .NET 10.0 |

## Test Projects

### OpenClaw.Shared.Tests — 652 tests

#### ModelsTests
- **AgentActivityTests** (~15) — glyph mapping for all ActivityKind values, display text formatting
- **ChannelHealthTests** (~25) — status display for ON/OFF/ERR/LINKED/READY states, case-insensitive matching
- **SessionInfoTests** (~25) — display text, main/sub session prefixes, ShortKey extraction, context summaries
- **SessionInfoContextSummaryTests** (~8) — token window formatting, millions/thousands display
- **SessionInfoRichDisplayTextTests** (~8) — rich display labels, display name fallback
- **SessionInfoAgeTextTests** (~6) — relative time formatting (minutes, hours ago)
- **GatewayUsageInfoTests** (~12) — token counts (999, 15.0K, 2.5M), cost display, empty state
- **GatewayNodeInfoTests** (~10) — display name, node info formatting

#### OpenClawGatewayClientTests (~50)
- Notification classification (health, urgent, calendar, build, email alerts)
- Tool-to-activity mapping (exec, read, write, edit, search, browser, message)
- Path shortening and label truncation
- `ResetUnsupportedMethodFlags` — clearing unsupported flag state

#### ExecApprovalPolicyTests (~20)
- Policy rule evaluation, persistence, pattern matching

#### CapabilityTests (~30)
- **SystemCapabilityTests** — system command handling
- **CanvasCapabilityTests** — canvas command handling
- **ScreenCapabilityTests** — screen command handling
- **CameraCapabilityTests** — camera command handling

#### NodeCapabilitiesTests (~15)
- Base class parsing, `ExecuteAsync` return values, payload handling

#### DeviceIdentityTests (~15)
- Payload format validation, pairing status events

#### NotificationCategorizerTests (~30)
- Keyword matching, channel-to-type mapping (health, calendar, stock, build, email, urgent)
- Priority rules, default categorization

#### GatewayUrlHelperTests (~25)
- URL normalization (http→ws, https→wss)
- Embedded credential stripping
- Port preservation, path handling

#### SystemRunTests (~20)
- Command execution, timeout handling, environment variables

#### ShellQuotingTests (~20)
- Shell metachar detection (`&`, `|`, `;`, `$`, `` ` ``, `*`, `?`, `<`, `>`, etc.)
- Quoting for safe shell invocation

#### WindowsNodeClientTests (~10)
- URL handling, endpoint construction

#### NodeInvokeResponseTests (~5)
- Default values, property setting

#### ReadmeValidationTests (~5)
- Documentation sync checks

---

### OpenClaw.Tray.Tests — 262 tests

#### Core Tray Tests

- **MenuDisplayHelperTests** (~40) — `GetStatusIcon` emoji mapping for Connected/Disconnected/Connecting/Error states, `GetChannelStatusIcon` status icons for running/idle/pending/error/disconnected + case-insensitive variants, `GetNextToggleValue` ON↔OFF toggling, unknown/empty status fallback
- **MenuPositionerTests** (~15) — Screen edge clamping (top-left, bottom-right), taskbar-at-right scenario, menu positioning relative to cursor
- **SettingsRoundTripTests** (~15) — Serialization/deserialization round trips, default values on missing keys, backward compatibility with older settings formats
- **DeepLinkParserTests** (~23) — `ParseDeepLink` protocol validation, null/empty handling, subpath parsing, trailing slash stripping, query parameter extraction, URL-encoded message handling

#### Onboarding Tests

- **OnboardingStateTests** (19) — Page order, mode logic, route changes, wizard state persistence, completion, disposal
- **WizardStepPropsTests** (4) — Enum values, record defaults, callback verification
- **GatewayChatHelperTests** (11) — URL scheme conversion, token encoding, localhost checks, session keys
- **LocalGatewayApproverTests** (13) — IsLocalGateway for localhost/remote/edge cases
- **SetupCodeDecoderTests** (14) — Base64url decode, size limits, JSON validation, URL/token extraction
- **GatewayHealthCheckTests** (6) — Health URI building, scheme conversion, port preservation
- **SecurityValidationTests** (16) — Locale whitelist, port range, path traversal, URI scheme validation
- **WizardStepParsingTests** (12) — JSON step parsing, options, completion, sensitive fields
- **LocalizationValidationTests** (6) — 5-locale key parity, onboarding key presence, no duplicates

---

## Running Tests

```bash
# All tests
dotnet test

# Single project
dotnet test tests/OpenClaw.Shared.Tests
dotnet test tests/OpenClaw.Tray.Tests

# Specific test class
dotnet test --filter "FullyQualifiedName~MenuDisplayHelperTests"

# Onboarding tests only
dotnet test --filter "FullyQualifiedName~Onboarding"

# Verbose output
dotnet test --logger "console;verbosity=detailed"
```

## Not Covered (Requires Integration Tests)

- WebSocket connection/reconnection flow
- Real gateway message parsing
- Concurrent event handling
- File I/O and thread synchronization
- End-to-end onboarding wizard flow (WebView2 requires runtime)

---

**Last Updated**: 2026-04-26
**Framework**: xUnit 2.9.3 / .NET 10.0
**Status**: ✅ 914 tests passing
