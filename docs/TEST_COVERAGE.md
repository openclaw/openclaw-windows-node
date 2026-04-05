# Test Coverage Summary

**624 tests total** (525 shared + 99 tray) — all passing ✅  
*(plus 20 skipped integration tests that require Windows)*

| Metric | Value |
|--------|-------|
| Total Tests | 644 (incl. 20 skipped) |
| Passing | 624 (100% of runnable) |
| Failing | 0 |
| Framework | xUnit 2.9.3 / .NET 10.0 |

## Test Projects

### OpenClaw.Shared.Tests — 525 tests (+20 skipped integration tests)

#### ModelsTests (~91 test cases across 8 classes)
- **AgentActivityTests** (~15) — glyph mapping for all ActivityKind values, display text formatting
- **ChannelHealthTests** (~25) — status display for ON/OFF/ERR/LINKED/READY states, case-insensitive matching
- **SessionInfoTests** (~25) — display text, main/sub session prefixes, ShortKey extraction, context summaries
- **SessionInfoContextSummaryTests** (~8) — token window formatting, millions/thousands display
- **SessionInfoRichDisplayTextTests** (~8) — rich display labels, display name fallback
- **SessionInfoAgeTextTests** (~6) — relative time formatting (minutes, hours ago)
- **GatewayUsageInfoTests** (~12) — token counts (999, 15.0K, 2.5M), cost display, empty state
- **GatewayNodeInfoTests** (~10) — display name, node info formatting

#### OpenClawGatewayClientTests (~47 test cases)
- Notification classification (health, urgent, calendar, build, email alerts)
- Tool-to-activity mapping (exec, read, write, edit, search, browser, message)
- Path shortening and label truncation
- `ResetUnsupportedMethodFlags` — clearing unsupported flag state
- `BuildMissingScopeFixCommands` — scope defaulting, placeholder fallbacks, node-token detection
- `BuildPairingApprovalFixCommands` — deviceId fallback chain, approval instructions

#### ExecApprovalPolicyTests (~42 test cases)
- Policy rule evaluation, persistence, pattern matching, ReDoS timeout guard

#### CapabilityTests (~53 test cases across 4 classes)
- **SystemCapabilityTests** — system command handling
- **CanvasCapabilityTests** — canvas command handling
- **ScreenCapabilityTests** — screen command handling
- **CameraCapabilityTests** — camera command handling

#### NodeCapabilitiesTests (~17 test cases across 3 classes)
- **NodeCapabilityBaseTests** — base class parsing, `ExecuteAsync` return values, payload handling
- **NodeInvokeResponseTests** — default values, property setting
- **NodeRegistrationTests** — registration fields and defaults

#### DeviceIdentityTests (~3 test cases)
- **DeviceIdentityUnitTests** — payload format validation, hex SHA-256 device ID format
- **DeviceIdentityIntegrationTests** — pairing status events (skipped on non-Windows)

#### NotificationCategorizerTests (~29 test cases)
- Keyword matching, channel-to-type mapping (health, calendar, stock, build, email, urgent)
- Priority rules, structured vs keyword classification, default categorization

#### WebSocketClientBaseTests (~17 test cases)
- URL normalization, token handling, status change events, dispose lifecycle

#### GatewayUrlHelperTests (~17 test cases)
- URL normalization (http→ws, https→wss)
- Embedded credential stripping
- Port preservation, path handling, edge cases

#### SystemRunTests (~14 test cases — skipped on non-Windows)
- Command execution, timeout handling, environment variables
- Orphan child process stdout drain, non-zero exit codes

#### ShellQuotingTests (~21 test cases)
- Shell metachar detection (`&`, `|`, `;`, `$`, `` ` ``, `*`, `?`, `<`, `>`, brackets, newlines, CR)
- Quoting for safe shell invocation, `FormatExecCommand` edge cases

#### WindowsNodeClientTests (~3 test cases)
- URL normalization via base class
- `hello-ok` with device token fires `PairingStatusChanged` exactly once
- `hello-ok` without token fires `Pending` event exactly once

#### ReadmeValidationTests (~1 test case)
- Documentation sync checks

---

### OpenClaw.Tray.Tests — 99 tests

#### MenuDisplayHelperTests (~15 test cases)
- `GetStatusIcon` — emoji mapping for Connected/Disconnected/Connecting/Error states
- `GetChannelStatusIcon` — status icons for running/idle/pending/error/disconnected + case-insensitive variants
- `GetNextToggleValue` — ON↔OFF toggling, case handling
- Unknown/empty status fallback

#### MenuPositionerTests (~13 test cases)
- Screen edge clamping (top-left, bottom-right)
- Taskbar-at-right scenario
- Menu positioning relative to cursor

#### DeepLinkParserTests (~21 test cases)
- `ParseDeepLink` — protocol validation, null/empty handling, subpath parsing, trailing slash stripping
- Query parameter extraction (`GetQueryParam`)
- URL-encoded message handling
- Multiple query parameters, missing keys

#### SettingsRoundTripTests (~7 test cases)
- Serialization/deserialization round trips
- Default values on missing keys
- Backward compatibility with older settings formats

#### LocalizationValidationTests (~2 test cases)
- Resource file structure validation
- String key coverage across locale files

#### TrayMenuWindowMarkupTests (~1 test case)
- XAML markup validity check

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

# Verbose output
dotnet test --logger "console;verbosity=detailed"
```

## Not Covered (Requires Integration Tests)

- WebSocket connection/reconnection flow
- Real gateway message parsing
- Concurrent event handling
- File I/O and thread synchronization

---

**Last Updated**: 2026-04-05
**Framework**: xUnit 2.9.3 / .NET 10.0
**Status**: ✅ 624 tests passing (+ 20 skipped integration tests)
