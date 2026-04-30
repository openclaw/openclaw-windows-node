# A2UI v0.8 — Standard Catalog (Components)

Source of truth: <https://a2ui.org/specification/v0_8/standard_catalog_definition.json>.

The v0.8 standard catalog defines **18 components** across three loose
categories: containers, display, interactive. A v0.8-conformant client
MUST recognize all 18 and either render them or fall back to an "unknown"
placeholder for catalog-strict mode.

Each section below is the spec — required properties first, optional
after, with enums spelled out. Where the WinUI or Lit impl has a known gap
or improvement, it's flagged inline so this doc doubles as a quick lookup
when wiring a new component. Detailed grading is in
[`grading.md`](./grading.md).

Notation: `BoundValue` means an [`A2UIValue`](./data-and-actions.md#a2uivalue)
tagged union — typically `{ literalString }` or `{ path }`. `Children`
means `{ explicitList: string[] }` or `{ template: { dataBinding, componentId } }`.

---

## Containers

### `Row`
Horizontal layout container.

| Property | Type | Required | Notes |
| --- | --- | --- | --- |
| `children` | `Children` | ✓ | `explicitList` or `template` |
| `distribution` | enum | | `start` \| `center` \| `end` \| `spaceBetween` \| `spaceAround` \| `spaceEvenly` |
| `alignment` | enum | | `start` \| `center` \| `end` \| `stretch` |

**Behavior**: lays children left-to-right; cross-axis = vertical alignment.

> WinUI: `StackPanel` (horizontal); `distribution` collapsed onto WinUI
> `HorizontalAlignment` — `spaceBetween`/`spaceAround`/`spaceEvenly` all
> map to `Stretch` (justify-content not natively available). Wrap to next
> row not implemented.
> Lit: full distribution support via CSS flex.

### `Column`
Vertical layout container. Same property set as `Row`, swapping axes.

### `List`
Scrollable list of children.

| Property | Type | Required | Notes |
| --- | --- | --- | --- |
| `children` | `Children` | ✓ | |
| `direction` | enum | | `vertical` (default) \| `horizontal` |
| `alignment` | enum | | `start` \| `center` \| `end` \| `stretch` |

**Behavior**: virtualization-friendly; spec calls for the client to
realize only viewport children when possible.

> WinUI: `ItemsRepeater` w/ `StackLayout`, virtualized, child-element
> cache keyed by component id (preserves data-binding subscriptions
> across recycling).
> Lit: builds all children up-front (no virtualization).
> Lit: `template` form for List is partially honored only because all
> three list-bearing components share the same children resolver. WinUI
> only supports `explicitList` today.

### `Card`
Single-child container with elevation/border treatment.

| Property | Type | Required |
| --- | --- | --- |
| `child` | component-id (string) | ✓ |

> WinUI: `Border` w/ `CardBackgroundFillColorDefaultBrush`,
> `theme.CornerRadius`, padding = `theme.Spacing * 2`.
> Lit: slot-based wrap; CSS-driven elevation.

### `Tabs`
Tabbed container.

| Property | Type | Required |
| --- | --- | --- |
| `tabItems[]` | array | ✓ |
| `tabItems[].title` | `BoundValue<string>` | ✓ |
| `tabItems[].child` | component-id | ✓ |

> WinUI: `TabView`, close buttons disabled, no reorder/drag.
> Lit: button strip + content region; tracks `selected` index in state.

### `Modal`
Click-to-open dialog.

| Property | Type | Required |
| --- | --- | --- |
| `entryPointChild` | component-id | ✓ |
| `contentChild` | component-id | ✓ |

**Behavior**: render `entryPointChild` inline; on user interaction (e.g.,
click), open a modal containing `contentChild`. Spec leaves "what closes
the modal" open; both impls rely on platform dismissal (Esc, click-out).

> WinUI: `ContentDialog` triggered by wrapping `entryPointChild` in a
> transparent `Button`. Native modal semantics.
> Lit: `<dialog>` element + `showModal()`.

### `Divider`
Visual separator.

| Property | Type | Required |
| --- | --- | --- |
| `axis` | enum | | `horizontal` (default) \| `vertical` |

> WinUI: 1px `Rectangle`, `SystemControlForegroundBaseLowBrush`.
> Lit: `<hr>`. **Gap**: Lit also exposes `thickness` and `color` in
> types but doesn't apply them (root.ts:317 TODO).

---

## Display

### `Text`
Text display.

| Property | Type | Required |
| --- | --- | --- |
| `text` | `BoundValue<string>` | ✓ |
| `usageHint` | enum | | `h1`–`h5`, `caption`, `body` |

> WinUI: `TextBlock` w/ Fluent theme styles (`TitleLarge`, `Subtitle`,
> `BodyStrong`, `Caption`, `Body`). Plain text only.
> Lit: **renders Markdown** via `markdown-it`. HTML blocks sandboxed in
> `<iframe sandbox="">`; code blocks escaped. This is _beyond_ spec —
> see [`grading.md`](./grading.md#text-markdown-divergence) for whether
> that's a feature or a foot-gun.

### `Image`

| Property | Type | Required | Enum |
| --- | --- | --- | --- |
| `url` | `BoundValue<string>` | ✓ | |
| `altText` | `BoundValue<string>` | | |
| `fit` | enum | | `contain` \| `cover` \| `fill` \| `none` \| `scale-down` |
| `usageHint` | enum | | `icon` \| `avatar` \| `smallFeature` \| `mediumFeature` \| `largeFeature` \| `header` |

> WinUI: `Image`; `usageHint` maps to fixed pixel sizes (24/40/80/160/240/full).
> Avatar wraps in `Border` w/ circular `CornerRadius`. SVG via
> `SvgImageSource` w/ 8s timeout. URLs gated by `MediaResolver` allowlist
> + DNS-rebinding defense.
> Lit: `<img>` directly; **no URL filtering** — `data:` and other schemes
> pass through.

### `Icon`

| Property | Type | Required |
| --- | --- | --- |
| `name` | `BoundValue<string>` | ✓ |

The 48 supported icon names (canonical enum):

```
accountCircle, add, arrowBack, arrowForward, attachFile, calendarToday,
call, camera, check, close, delete, download, edit, event, error,
favorite, favoriteOff, folder, help, home, info, locationOn, lock,
lockOpen, mail, menu, moreVert, moreHoriz, notificationsOff,
notifications, payment, person, phone, photo, print, refresh, search,
send, settings, share, shoppingCart, star, starHalf, starOff, upload,
visibility, visibilityOff, warning
```

> WinUI: `FontIcon` over Segoe Fluent Icons (MDL2). Unknown names →
> `help` glyph; `moreHoriz` reuses `moreVert` (no canonical horizontal
> ellipsis in MDL2). Logs once per unmapped name per process.
> Lit: CSS background-image sprite; lowercases CamelCase to snake_case
> at lookup (icon.ts:53).

### `Video`

| Property | Type | Required |
| --- | --- | --- |
| `url` | `BoundValue<string>` | ✓ |

> WinUI: `MediaPlayerElement` w/ transport controls. URL gated by
> `MediaResolver` HTTPS+allowlist. **No DNS-rebinding pin** — the OS
> media stack does its own DNS lookup at playback time, so the
> hostname-allowlist is the load-bearing defense (image fetches use a
> separate, safer path that does pin).
> Lit: `<video controls>`.

### `AudioPlayer`

| Property | Type | Required |
| --- | --- | --- |
| `url` | `BoundValue<string>` | ✓ |
| `description` | `BoundValue<string>` | | |

> WinUI: `MediaPlayerElement` w/ `description` rendered above as
> `Caption`. URL gated by `MediaResolver` HTTPS+allowlist; same
> playback-time DNS caveat as `Video`.
> Lit: `<audio controls>`; **`description` is ignored** (audio.ts).

---

## Interactive

### `Button`

| Property | Type | Required |
| --- | --- | --- |
| `child` | component-id | ✓ |
| `action` | `Action` object | ✓ |
| `primary` | bool | | |

`Action` shape:
```json
{
  "name": "submit",
  "context": [
    { "key": "email", "value": { "path": "/form/email" } },
    { "key": "kind",  "value": { "literalString": "primary" } }
  ]
}
```

> WinUI: `Button`; `primary` → `AccentButtonStyle`; click raises
> `A2UIAction` through the dispatcher (see
> [`data-and-actions.md`](./data-and-actions.md#actions)).
> Lit: `<button>`; click dispatches a DOM `CustomEvent`.

### `CheckBox`

| Property | Type | Required |
| --- | --- | --- |
| `label` | `BoundValue<string>` | ✓ |
| `value` | `BoundValue<bool>` | ✓ |

> Both impls: bi-directional binding — toggle writes back to the
> `value.path` data-model location. Spec is silent on write-back.

### `TextField`

| Property | Type | Required | Enum |
| --- | --- | --- | --- |
| `label` | `BoundValue<string>` | ✓ | |
| `text` | `BoundValue<string>` | | |
| `textFieldType` | enum | | `shortText` (default) \| `longText` \| `number` \| `date` \| `obscured` |
| `validationRegexp` | string | | |

> WinUI: `TextBox` (or `PasswordBox` if `obscured`); `obscured` paths
> auto-marked as secrets. `InputScope` set per type. **`validationRegexp`
> not enforced**.
> Lit: `<input>` / `<textarea>`. **`validationRegexp` not enforced**
> (root.ts:367 TODO).

### `DateTimeInput`

| Property | Type | Required | Notes |
| --- | --- | --- | --- |
| `value` | `BoundValue<string>` | ✓ | ISO 8601 |
| `enableDate` | bool | | |
| `enableTime` | bool | | |

> WinUI: `CalendarDatePicker` + `TimePicker`; ISO-8601 round-trip.
> Lit: `<input type="date|time|datetime-local">`. **`outputFormat`
> noted in code but ignored** (datetime-input.ts:159 TODO).

### `MultipleChoice`

| Property | Type | Required | Enum |
| --- | --- | --- | --- |
| `selections` | `BoundValue<array>` (or `path`) | ✓ | |
| `options[]` | array | ✓ | each: `{ label: BoundValue<string>, value: string }` |
| `maxAllowedSelections` | integer | | |
| `variant` | enum | | `checkbox` \| `chips` |
| `filterable` | bool | | |

> WinUI: `ComboBox` (single) or `ListView` (multi). When
> `maxAllowedSelections == 1` it writes a scalar to the path (not a
> 1-element array) — back-compat reads tolerate either. **`variant` and
> `filterable` not honored**.
> Lit: `<select multiple>`. **`maxAllowedSelections` not enforced**
> (root.ts:334 TODO); selections array resolution incomplete
> (multiple-choice.ts:87–103).

### `Slider`

| Property | Type | Required |
| --- | --- | --- |
| `label` | `BoundValue<string>` | | |
| `value` | `BoundValue<number>` | ✓ |
| `minValue` | number | | |
| `maxValue` | number | | |

> WinUI: `Slider`, defaults min=0/max=100/step=1. Bi-directional bind.
> Lit: `<input type="range">`. Bi-directional bind.

---

## Catalog-strict mode

Both implementations must reject **anything not in the 18 above** by
rendering a placeholder, not by throwing. This is one of the few
"MUST" requirements in the spec:

> The full set of available component types and their properties is
> defined by a Catalog Schema, not in the core protocol schema.

> WinUI: `UnknownRenderer` — orange-bordered placeholder w/ warning
> icon and component name. Telemetry event fired.
> Lit: walks a `componentRegistry`; allows custom components when
> `enableCustomElements` flag is set (extension beyond spec).

## Catalog-level styles (theme tokens)

Each catalog optionally declares `styles`:

| Token | Type |
| --- | --- |
| `font` | string (font family) |
| `primaryColor` | hex `#RRGGBB` |

> WinUI: `A2UITheme.Parse()` reads these plus nested
> `colors.{accent,background,foreground,card}`,
> `typography.fontFamily`, `radius`, `spacing`. Applied to the surface
> Grid resource scope (not global).
> Lit: derives a `--p-0` … `--p-100` palette via CSS `color-mix` from
> `primaryColor`.
