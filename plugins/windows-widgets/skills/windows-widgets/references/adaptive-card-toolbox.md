# Adaptive Card toolbox for Windows widgets

Use this reference to turn a user's design request into a complete
`presentation.card`. It describes authoring options, not a second renderer,
a preset catalog or a promise of Widgets Board compatibility.

Call `windows_widgets` with `{"action":"capabilities"}` before authoring.
It returns an offline, structured toolbox without fetching the source, opening
tracker state or contacting a Windows node. Then inspect `status` and use
`style` to preserve the existing observation and collection schedule.

## What was inspected

The [Designer](https://adaptivecards.microsoft.com/designer) is a client-rendered
application. Its HTML contains no toolbox inventory. On 2026-11-12, inspection
of its publicly served `main.bundle.js` found Elements, Inputs, Charts and
Actions peer registrations. The palette filters registrations by the selected
host and target version, so not every registered type appears in every preset.
The bundle's SHA256 was
`80852dd6bec657c27edad2a1a305224532ac29e84de3073cfbb43adb0153ab73`.
Only names and behavioral contracts were used; no Designer implementation is
bundled with the plugin. Nothing is scraped at widget creation or refresh time.

Microsoft's [Windows template guide](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widgets-create-a-template)
explicitly warns that the Designer has a different renderer and host options.
It documents Windows template bindings, size/theme conditions and Execute
callbacks. A successful plugin call proves publication, not visible rendering.
Keep card versions within `1.0` through `1.6`; raising that number does not add
features to Windows.

## Native composition building blocks

The plugin already transports native element properties unchanged within its
publication limits. These are authoring choices, not a new element allowlist.
Consult the [schema explorer](https://adaptivecards.microsoft.com/) for the
specific type's property definitions and version requirements.

| Toolbox family | Elements and useful properties | Use in a widget |
| --- | --- | --- |
| Text | `TextBlock`: `text`, semantic `size`, `weight`, `color`, `isSubtle`, `wrap`, `maxLines`, `horizontalAlignment` | One dominant status with restrained supporting copy |
| Rich text | `RichTextBlock`, inline `TextRun`: mixed weight/color, `selectAction` | Native inline emphasis and links, not HTML |
| Grouping | `Container`: `items`, `style`, `spacing`, `separator`, `height`, `verticalContentAlignment`, `bleed`, `backgroundImage` | A cohesive hero region; avoid nesting decorative card frames |
| Columns | `ColumnSet`, nested `Column`: `columns`, `width` auto/stretch, `items` | Illustration beside status; use conditional alternate layouts on Small |
| Images | `Image`, `ImageSet`: `url`, `altText`, `size`, `width`, `height`, `style`, `imageSize` | Lawful artwork with real alternate text; no image opacity property |
| Facts | `FactSet`: `facts` containing `title` and `value` | A few actual facts, not invented measurements |
| Tables | `Table`, nested `TableRow`/`TableCell`: `columns`, `rows`, cell `items`, headers/grid properties | Optional larger-card detail; requires a compatible host and a simpler fallback |
| Action groups | `ActionSet`: `actions` | Local preferences or HTTPS navigation above the provider footer |
| Media | `Media`: `poster`, `altText`, `sources` with `mimeType`/`url` | Playback is unverified in Windows; prefer an image plus an OpenUrl link |

`backgroundImage` can be a URL or an object with `url`, `fillMode`,
`horizontalAlignment` and `verticalAlignment`. It does not grant CSS gradients,
arbitrary fonts, margins, effects or WinUI resource lookups. Keep the provider's
timestamp/source footer on the plain host surface. Windows owns the outer
frame and dimensions, including the compact Small tile.

## Inputs and local preferences

The Designer registers `Input.Text`, `Input.Number`, `Input.Date`, `Input.Time`,
`Input.Toggle`, `Input.ChoiceSet` and the newer `Input.Rating`. Inputs support
their native properties, such as labels, defaults, required/error messages,
choices, multiline text and bounds, subject to the actual host.

Declare matching scalar `initialState` keys and use `Action.Execute` with
`verb: "widget.set"` to apply them. Bind saved values through `state`.
For boolean toggles, use `valueOn: "true"` and `valueOff: "false"`; the provider
converts these to booleans. Other values must match the declared JSON type.
Do not claim every host serializes numeric inputs identically.
The controls never edit `value`, `observedAt` or other source facts.

Keep customization controls inside `${$host.isSettingsPayload}` conditions.
Execute actions are gated by the provider for authenticated contexts.
Only OpenUrl is available when `$host.isUserContextAuthenticated` is false.
Input.Rating is an extension, not a guaranteed Windows control; use a labeled
`Input.ChoiceSet` fallback if needed.

## Newer Designer extensions

These registrations exist in the inspected Designer, but their presence does
not establish Windows support. They already pass through the plugin's native
transport, subject to the same bounds and resource/action policy. Prefer the
ordinary elements above unless you will verify the extension on the target
Widgets Board. Retain essential information in a standard `fallback`.

| Family | Designer types | Composition and fallback |
| --- | --- | --- |
| Icons and inline graphics | `Icon`, `IconRun`, `ImageRun` | Named icon/style/size or image content. Use ordinary text or Image fallback; inline run extensions may require falling back the containing RichTextBlock |
| Labels and buttons | `Badge`, `CompoundButton` | Badge text/icon/style or button title/description/selectAction. Fall back to TextBlock or columns with an allowed ActionSet |
| Ratings | `Rating`, `Input.Rating` | Real supplied values, not a rating invented from an unrelated status. Fall back to text or a labeled ChoiceSet |
| Progress | `ProgressBar`, `ProgressRing` | Real `value`/`max` data. Fall back to text; a boolean is not a completion percentage |
| Code | `CodeBlock` | Display content only, never executable code. Fall back to a wrapping TextBlock |
| Paging | `Carousel`/`CarouselPage`, `TabSet`/`TabPage`, `Accordion`/`AccordionPage` | Optional organized detail. Fall back to a plain Container and keep status visible without interaction |
| Charts | `Chart.Line`, `Chart.Donut`, `Chart.Pie`, `Chart.Gauge`, `Chart.HorizontalBar`, `Chart.HorizontalBar.Stacked`, `Chart.VerticalBar`, `Chart.VerticalBar.Grouped` | Follow each chart's schema and use real data. Native chart support is unverified. Pushed series supplies timestamped points and a bounded PNG Image with native text fallback; a scalar alone does not supply history |
| Layout objects | `Layout.Stack`, `Layout.Flow`, `Layout.AreaGrid`, nested `GridArea` | These belong in layout properties, not the body. Preserve an ordinary items/columns layout because unsupported properties may simply be ignored |

The bundle also includes service-backed types such as `LoopComponent` and
`Component.graph.microsoft.com/*`. The plugin does not supply their cloud
services, identities or credentials. Do not select them for Windows widgets
merely because their registrations exist in the Designer bundle.

For an unfamiliar **element**, a fallback keeps the core message available:

```json
{
  "type": "Badge",
  "text": "${value}",
  "fallback": {
    "type": "TextBlock",
    "text": "${value}",
    "weight": "Bolder",
    "wrap": true
  }
}
```

Fallback is not a universal polyfill. Unsupported properties on a known type
can be ignored without triggering it; clipping or an image-loading failure
does not necessarily trigger it either. Validate both the intended feature
and its simpler composition on Windows. Do not guess `requires` feature names
or invent a `$host` support flag for charts, High Contrast or advanced layouts.

## Actions: adapt instead of blindly copying

| Designer action | Plugin contract |
| --- | --- |
| `Action.OpenUrl` | Public HTTPS navigation; no files, credentials, computed URLs or custom protocols |
| `Action.Execute` | `widget.set`, `widget.increment`, `widget.toggle`, `widget.reset`, `widget.refresh` for local state/cached rendering; separately opted-in `widget.lighting` for admitted literal user-click controls, never arbitrary commands |
| `Action.ShowCard`, `Action.ToggleVisibility` | Transported; actual behavior is host-dependent. Toggle targets must be literal and cannot target provider IDs |
| `Action.Submit` | Rejected. Adapt a preference form to Execute with `widget.set` |
| `Action.ResetInputs` | Rejected. Use Execute with `widget.reset` to restore declared instance defaults; this is not identical to resetting unsaved form controls |
| `Action.Popover`, `Action.Overflow`, `Action.RunCommands`, `Action.InsertImage`, `Action.OpenUrlDialog` | Rejected. No host command bridge, cloud execution or arbitrary Designer action callbacks |

Action restrictions apply in fallback and nested cards too. Do not hide a
rejected action in a fallback and expect it to work. Use supported actions
throughout the complete template. Automatic `refresh.action` may only use
`widget.refresh`, which re-renders cached data, not a new source fetch.

## Template data, repetition and responsive layout

- Source bindings remain `title`, `value`, `detail`, `observedAt`, `freshness`,
  `sourceUrl`. Put authored display data under `presentation.data`, accessed
  through `content`; local preferences live under `state`.
- Use `$when` for conditions and `$data` for repeated body content.
  Within repetitions, use `$root` for the original context and `$index` for
  the current index. Keep repeated content bounded. Do not put `$data` or
  `$when` on the provider-owned root.
- Use `$host.widgetSize` (`small`, `medium`, `large`), `$host.hostTheme`
  (`light`, `dark`) and `$host.isSettingsPayload` from Windows, not fields you
  inject into sample data. Header customization uses `header` and remains
  host-dependent.
- Resources must be literal public HTTPS or direct
  `${$root.content.path}` bindings. A repeated row cannot construct its own
  URL. Use separately declared resource elements/conditions where necessary.
- JSON admission is not a template-expression evaluator or native preview.
  The Designer's separate sample data does not supply Windows `$host` values.

Read [the responsive composition](../../../examples/responsive-status.json)
for a full presentation with columns, semantic type hierarchy, a Badge
fallback, rich text, repeated large-card notes and local customization.
It preserves the real observation and does not add fake chart data.
It is a starting point, not an automatic restyle or a guaranteed native
rendering of every element.

## Publication and proof boundary

Use the existing PNG transfer/resource policy and limits from the
[main skill](../SKILL.md). The capabilities catalog does not relax them.
Keep unsupported optional features out of the Small state, reserve the
provider-owned bottom metadata/source controls and use semantic host colors.
Do not put preview hex colors or CSS in the JSON.

After publication, check actual text wrapping, artwork, fallback behavior,
actions and provider footer in the supported sizes/themes in Widgets Board.
Do not change source facts to force a screenshot. Report untested extensions
and themes explicitly; native support cannot be enabled by scraping a website.
