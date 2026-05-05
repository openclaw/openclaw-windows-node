# A2UI v0.8 — Implementation Grading

This grades two implementations against the v0.8 spec
(<https://a2ui.org/specification/v0.8-a2ui/>):

- **Lit reference** at `C:\Users\andersonch\Code\openclaw\vendor\a2ui\renderers\lit\src\0.8`
- **Native WinUI** in this repo at `src/OpenClaw.Tray.WinUI/A2UI/`

The Lit code looks like the canonical browser renderer the OpenClaw
canvas host ships; the WinUI code is this repo's branch
`feat/a2ui-native-winui`.

Citations use repo-local paths. Lit paths are anchored at the OpenClaw
checkout: `openclaw\vendor\a2ui\renderers\lit\src\0.8\`. WinUI paths are
anchored at `src/OpenClaw.Tray.WinUI/A2UI/`.

## Method

For each spec area, deductions land in two buckets:

- **Gap** — implementation is missing or wrong vs. spec. Letter grade penalty.
- **Good deviation** — implementation does something the spec _doesn't say
  to do_, but it's the correct call. Listed but doesn't penalize.

Grades are A–F, separately for Lit and WinUI. There is no curving —
"A" means it would pass a strict spec audit and a strict security
audit; "B" means it works for normal traffic but fails under a hostile
agent; etc.

---

## Scorecard

| Area | Lit | WinUI | Notes |
| --- | --- | --- | --- |
| Component coverage (catalog completeness) | A | A | both 18/18 |
| Component property completeness | B | A− | Lit has 4 documented TODOs; WinUI has minor distribution mappings |
| Streaming / JSONL parsing | B | A | Lit: lenient; WinUI: lenient + size caps |
| Data binding / `A2UIValue` | B+ | A | Lit auto-parses JSON strings (surprising); WinUI strict RFC 6901 |
| Action transport | B | A | Lit: DOM event passthrough; WinUI: debounced + single-flight + fallback queue + gateway tag protocol |
| Action context security | D | A | Lit punts to host; WinUI scopes to declared `dataBinding` and redacts secrets |
| Theming | A− | A− | Equivalent power; different idioms |
| URL safety / SSRF | F | A− | Lit unrestricted; WinUI HTTPS+allowlist for `Image`/`Video`/`AudioPlayer`, plus DNS-rebinding pin on `Image` fetches only |
| Modal lifecycle | A− | A | Both work; WinUI uses native `ContentDialog` |
| List virtualization | C | A | Lit builds all items; WinUI uses `ItemsRepeater` w/ recycling |
| Bi-directional binding (write-back) | A | A | Both implement; spec is silent (good deviation) |
| Markdown in `Text` | B+ | n/a | Lit's enhancement is real but increases attack surface |
| Test coverage | D | A− | Lit: 1 model test, no per-component; WinUI: render matrix + scale + integration |
| Spec deviations called out (good ones) | B | A | Lit's improvements partially offset its gaps |
| **Overall** | **B−** | **A−** | |

The two "A" grades have very different shapes:

- **Lit** is a smaller codebase that gets the happy path right, with two
  notable **good** deviations (Markdown rendering, bi-directional binding)
  but several papercut **gaps** and a **non-trivial security delta**
  inherited from a "the host will sanitize" posture.
- **WinUI** is significantly more code, fills almost every gap, and adds
  defenses the spec doesn't ask for. Its remaining minus comes from the
  things it _doesn't_ do yet (List `template` mode, Row wrap, `MultipleChoice.variant`).

---

## Lit implementation — detailed deductions

### Documented `TODO` gaps

Verbatim TODOs in `vendor/a2ui/.../ui/root.ts` and component files:

| Property | File:Line | Status |
| --- | --- | --- |
| `Divider.thickness` / `axis` / `color` | `ui/root.ts:317` | type declared, value not applied to `<hr>` |
| `MultipleChoice.maxAllowedSelections` | `ui/root.ts:334` | accepted but not enforced |
| `TextField.validationRegexp` | `ui/root.ts:367` | not applied to `<input>` |
| `DateTimeInput.outputFormat` | `ui/datetime-input.ts:159` | placeholder; always uses browser format |
| `MultipleChoice.selections` resolution | `ui/multiple-choice.ts:87–103` | logic incomplete when `selections` is path-bound |
| `AudioPlayer.description` | `ui/audio.ts` | spec'd property silently dropped |

Letter penalty: **−1 step on Component Property Completeness** (A → B).

### `A2UIValue.path` resolver auto-parses JSON-shaped strings

`data/model-processor.ts:198–225` detects `valueString` payloads that look
like `{...}` or `[...]` and **silently parses them as JSON**. The intent is
"developer convenience"; the consequence is that a string literal containing
a `[` or `{` becomes a structured value. This is a **gap** because the spec
distinguishes `valueString` from `valueArray`/`valueMap` precisely so the
agent can be unambiguous. Letter penalty: **−1 step on data binding**.

### URLs are passed through to the DOM

`ui/image.ts:67–74` binds `<img src="${url}">` directly. There is no
allowlist for `data:` / `javascript:` / `file:` / private-IP hosts, no
SSRF protection, no DNS rebinding defense. The WinUI impl has all of
these. The host **may** sanitize before forwarding URLs, but the
renderer offers no defense in depth. Letter penalty: **−2 steps on URL
safety** (this is the F).

### Component registry allows arbitrary custom elements

`vendor/a2ui/.../ui/root.ts:118–140, 441–471` lets the embedding app set
`enableCustomElements = true` and then renders any `<component>` whose tag
is registered in `componentRegistry`. This is **beyond spec** — useful for
extensibility, dangerous for catalog-strict mode. **Not graded as a gap**
since it's behind a flag, but it's worth flagging at the host level.

### One unit test covers everything

`vendor/a2ui/.../model.test.ts` exercises `A2uiMessageProcessor` for
`beginRendering` and `surfaceUpdate`. There are **no per-component
render tests, no event-dispatch tests, no markdown sanitizer tests, no
data-binding edge-case tests**. Letter penalty: **−2 steps on test
coverage** (D).

### Good deviations

- **Markdown rendering** in `Text` (`ui/text.ts`, `ui/directives/markdown.ts`).
  HTML blocks wrapped in `<iframe sandbox="">`; code blocks escaped via
  `sanitizer.escapeNodeText`. The spec says plain string. Whether this
  counts as good depends on the threat model — see
  [the Text/Markdown divergence](#text-markdown-divergence).
- **Signal-driven re-render** via `@lit-labs/signals`. Cleaner reactivity
  than naive `requestUpdate()`.
- **Bi-directional binding** in `CheckBox`, `TextField`, `Slider`,
  `DateTimeInput`. Spec is silent on write-back; both impls add it.

---

## WinUI implementation — detailed deductions

### Property-coverage misses

| Property | File:Line | Status |
| --- | --- | --- |
| `Row.distribution` `spaceBetween/Around/Evenly` | `Rendering/Renderers/ContainerRenderers.cs:10–32` | all three collapse to `HorizontalAlignment.Stretch` (WinUI `StackPanel` doesn't natively express justify-content) |
| `Row.wrap` (multi-row) | n/a | not implemented; would need a custom `Panel` |
| `List.template` mode | `Rendering/Renderers/ContainerRenderers.cs:57–159` | only `explicitList` supported |
| `MultipleChoice.variant` (`chips`) | `Rendering/Renderers/InteractiveRenderers.cs:279–430` | always `ComboBox`/`ListView` |
| `MultipleChoice.filterable` | same | not honored |
| `TextField.validationRegexp` | `Rendering/Renderers/InteractiveRenderers.cs:98–199` | not enforced |
| `Tabs` close / reorder | `Rendering/Renderers/ContainerRenderers.cs:187–235` | disabled |
| Component `weight` | `Protocol/A2UIProtocol.cs:111–151` | parsed but not applied |

Letter penalty: **−1 step on Component Property Completeness**, but
balanced by being the only impl that fills the corresponding Lit gaps
(`maxAllowedSelections` is enforced; `Divider.axis` is honored).

### Action context scoping (the centerpiece win)

`Rendering/IComponentRenderer.cs:183–249` (`BuildActionContext`):

1. Collect `allowed` paths from the component's explicit `dataBinding`
   array, or — if absent — implicitly walk every `A2UIValue.path` referenced
   by the component's own properties.
2. For each `action.context[]` entry, resolve only if `IsAllowedPath`
   matches (exact or ancestor with `/` boundary).
3. Strip secret paths via `SecretRedactor` (`Rendering/SecretRedactor.cs`):
   - Registered paths (e.g., obscured `TextField` fields).
   - Substring denylist: `password`, `secret`, `token`.

This blocks the trivial "exfiltrate the whole tree" attack without
requiring the host to know about A2UI internals. The Lit impl can't
do this because it dispatches `action` straight through.

### URL safety — DNS rebinding defense (Image fetches)

`Rendering/MediaResolver.cs:57–95`:

```csharp
new SocketsHttpHandler {
    ConnectCallback = async (ctx, ct) => {
        var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        foreach (var ip in addresses) {
            if (!IsPublicAddress(ip)) throw ...;   // loopback, RFC1918, link-local, multicast
        }
        // connect to resolved IP, not hostname (no second DNS lookup)
    },
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
};
```

Plus an allowlist gate in `IsAllowed(url)`. Closes a TOCTOU window
between an allowlist check and the actual TCP connect. The Lit impl
does none of this.

**Limitation: this pin is image-only.** `Video`/`AudioPlayer` route through
`MediaSource.CreateFromUri`, which performs its own DNS resolution at
playback time outside the resolver. The HTTPS+allowlist gate still
applies to those URLs, but the connect-time IP check does not — see
`MediaResolver.TryResolveMediaUri`. A local-proxy approach was scoped
out of the v0.8 native renderer; the allowlist is the load-bearing
defense for media playback.

### Streaming hardening

`Protocol/A2UIProtocol.cs:176–367` and `Hosting/A2UIRouter.cs`:

| Cap | Value |
| --- | --- |
| Max line length | 1 MiB |
| Max components per surface | 2000 |
| Max entries per `dataModelUpdate` | 1024 |
| Max key length | 256 |
| Max string value | 64 KiB |
| Max `valueMap` depth | 32 |
| Max render depth | 64 |

All limits log + drop, never throw. Cycle detection in `_renderingIds`
prevents id-loops in malformed surfaces.

### Component diff on `surfaceUpdate`

`Hosting/SurfaceHost.cs:ApplyComponents` compares incoming defs (name,
weight, properties JSON-string) against the previous set and **skips
rebuild if unchanged**. Effect: a re-emitted surface preserves
`TextBox` caret position, scroll offset, and `Tabs` selection. The
spec calls for "structural diffing"; this is a heuristic that catches
the most common case (agent re-emits whole surface).

### Modal as native `ContentDialog`

`Rendering/Renderers/ContainerRenderers.cs:237–284` wires up a
`ContentDialog` whose `Content` is the `contentChild` and whose trigger
is the `entryPointChild` wrapped in a transparent `Button`. Spec leaves
the modal _shape_ open; the WinUI impl gives it the full platform-modal
treatment (focus trap, ESC dismiss, screen-reader announcement).

### List virtualization

`Rendering/Renderers/ContainerRenderers.cs:57–159` uses an
`ItemsRepeater` with a `ChildIdTemplate` cache keyed by component id.
Recycled elements are pulled from the cache so their data-binding
subscriptions stay alive across scrolling. The Lit impl has no
virtualization.

### Test surface

| Project | Focus |
| --- | --- |
| `OpenClaw.Shared.Tests/A2UICapabilitySecurityTests.cs` | protocol, secret redaction |
| `OpenClaw.Tray.UITests/A2UIRenderingTests.cs` | per-component XAML rendering, data binding, live updates |
| `OpenClaw.Tray.UITests/A2UIControlMatrixTests.cs` | property matrix coverage |
| `OpenClaw.Tray.UITests/A2UIDashboardScaleTest.cs` | 1000+ component stress |
| `OpenClaw.Tray.UITests/A2UIThemeTests.cs` | theme parsing |
| `OpenClaw.Tray.UITests/A2UISvgTests.cs` | SVG decode + 8s timeout |
| `OpenClaw.Tray.IntegrationTests/A2UICanvasIntegrationTests.cs` | end-to-end MCP smoke + PNG capture |

Coverage merged across all three suites via `dotnet-coverage` (per the
auto-memory note). Letter grade A−; the missing step is that the
gateway-action transport unit tests aren't fully isolated (depend on a
fake `WindowsNodeClient`).

### Good deviations

| Deviation | File | Why it's good |
| --- | --- | --- |
| DNS rebinding defense (image fetches) | `Rendering/MediaResolver.cs:57–95` | spec doesn't ask but a hostile agent can otherwise pivot through the image fetch path to internal HTTP services. Does not extend to `Video`/`AudioPlayer` — see "URL safety" section. |
| Action context allowlist | `Rendering/IComponentRenderer.cs:183–249` | minimum-information principle; spec leaves this open |
| Secret denylist | `Rendering/SecretRedactor.cs` | catches `/auth/sessionToken` style names automatically |
| `surfaceUpdate` diff | `Hosting/SurfaceHost.cs` | preserves caret/scroll/selection on re-emit |
| Single-flight gate on action dispatch | `Actions/IActionSink.cs:27–142` | prevents fallback dequeue racing fresh send |
| Per-surface theme scope | `Hosting/SurfaceHost.cs ApplyThemeToScope` | multi-surface tab views don't bleed themes |
| `IA2UITelemetry` seam | `Telemetry/IA2UITelemetry.cs` | structured events instead of log scraping |
| Single-handler `Func` events on `CanvasCapability` | reviewed in commit `5b9c468` | catches accidental multi-subscribe instead of silent `Delegate.Combine` |
| MCP bearer token in Settings UI | `SettingsPage.xaml.cs` | quality-of-life for MCP setup, kept out of action payloads |

---

## Side-by-side: where they diverge meaningfully

### `Text` / Markdown divergence

The Lit impl renders Markdown; the WinUI impl renders plain text. This is
the **biggest functional UX difference** between the two.

Lit's defense is `iframe sandbox=""` for HTML blocks plus
`escapeNodeText` for code. That's a reasonable sandbox model in the
browser — but every line still expands the renderer's attack surface
beyond the spec's "plain string" promise.

For ms-windows-node, parity is **probably not worth chasing** unless
the agent surfaces depend on it: WinUI doesn't have a built-in
Markdown engine, and adding one means importing a dependency that has
to be kept in lockstep with Lit's rendering choices to avoid surfaces
that look right in the browser and broken on Windows. The defensible
choice is to ask the agent to emit explicit `Text + usageHint`
hierarchies instead of inline Markdown.

### List performance

If a surface includes a `List` of 200+ items, the Lit renderer will
build all 200 children before paint. WinUI builds ~10 (whatever fits
the viewport) and recycles as the user scrolls. For this repo's
typical agent surfaces (dashboards, conversation panels) this is the
single biggest performance delta.

### Action security model

The two impls have completely different threat models:

- **Lit + browser canvas host**: assume the embedding app is
  trustworthy and will sanitize. The renderer is a thin presenter.
- **WinUI tray**: assume the renderer talks to a hostile agent over an
  arbitrary network. Apply policy in the renderer.

Neither is wrong, but a host that wants Lit-grade isolation has to
build the same allowlist/denylist logic that WinUI bakes in. In
practice that means anyone embedding the Lit renderer outside
OpenClaw's canvas host needs to **wrap action handlers**, never just
forward them.

---

## Known deviations by category

For PR reviewers — quick "is this OK?" reference.

| Deviation | Spec status | Lit | WinUI | Verdict |
| --- | --- | --- | --- | --- |
| Bi-directional data-model write on user input | silent | ✓ | ✓ | Good — spec assumes it implicitly |
| Markdown in `Text` | violation (plain string) | ✓ | ✗ | Lit: useful but expands attack surface; WinUI: stay plain |
| Custom-element registry beyond catalog | violation (catalog-strict) | ✓ (flag) | ✗ | Risk; only enable in trusted hosts |
| `valueString` auto-parsed as JSON | violation (type erasure) | ✓ | ✗ | Bug-shaped; rely on `valueMap`/`valueArray` |
| Hard size caps on stream / model | silent | ✗ | ✓ | Good — DoS defense |
| URL allowlist on media | silent | ✗ | ✓ | Good — SSRF defense |
| DNS-rebinding defense (image fetches) | silent | ✗ | ✓ | Good — beyond allowlist. Image only; `Video`/`AudioPlayer` rely on the allowlist alone (OS media stack re-resolves at playback). |
| Action context allowlist | silent | ✗ | ✓ | Good — minimum information |
| Secret-path redaction | silent | ✗ | ✓ | Good — keeps tokens off the wire |
| Component diff on `surfaceUpdate` | "structural diffing" (vague) | ✗ | ✓ | Good — preserves UI state |
| `List` virtualization | "should virtualize" | ✗ | ✓ | Good — required for non-trivial surfaces |
| `Modal` as native `ContentDialog` | shape open | `<dialog>` | `ContentDialog` | Both fine |
| `MultipleChoice` single-mode writes scalar | spec implies array | array | scalar | WinUI's reads tolerate either; talk to your agent format |
| `validationRegexp` (TextField) | spec property | ✗ TODO | ✗ | Both have a gap here |

---

## Recommended follow-ups (not part of grading)

These are the changes that would close the remaining minuses:

**WinUI (A− → A)**
- Honor `MultipleChoice.variant` (`chips`) and `filterable`.
- Apply `TextField.validationRegexp` (the catalog says it's a string;
  compile + on-change validate).
- Consider `List.template` mode for surfaces that bind a list to a
  data-model array (also unblocks v0.9 readiness).
- Add unit tests for `GatewayActionTransport` payload shape.

**Lit (B− → B+ or higher)**
- Resolve the four documented `TODO`s (Divider, TextField,
  DateTimeInput, MultipleChoice).
- Add per-component render tests and a markdown-sanitizer test suite.
- Add at least an opt-in URL allowlist for media components.
- Document the `enableCustomElements` flag's risk surface for
  embedding apps.
