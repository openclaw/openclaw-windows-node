# ReactorCharting Chart Authoring Guide

ReactorCharting is a C# port of D3.js that renders charts as Reactor Element trees on WinUI3 Canvas. Charts are pure functions: `data => Element`. The goal is a single `return D3Canvas(...)` expression per chart with no imperative mutation.

## Imports

Every chart file needs these:

```csharp
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Charting.D3;
using Microsoft.UI.Reactor.Charting;
using static Microsoft.UI.Reactor.Charting.D3;   // D3Rect, D3Circle, D3LinePath, Brush, Gray, etc.
using static Microsoft.UI.Reactor.Factories;              // Text, Canvas, ScrollView, etc.
```

## The Ideal Shape

The best charts follow this structure — one return, one collection expression, data transforms via LINQ, all primitives from the DSL:

```csharp
return D3Canvas(W, H,
    [.. D3Grid(ys, left, pw),
     .. D3Axes(xs, ys, left, top, pw, ph),
     .. data.Select(d => D3Circle(xs.Map(d.x), ys.Map(d.y), r: 4)
         with { Fill = fill, Stroke = stroke }),
     D3Text(left, 6, "Title", fontSize: 14, foreground: Gray(40))]
);
```

Key elements: `D3Canvas` wraps everything, `[.. spread]` composes flat element lists, `.Select()` / `.SelectMany()` map data to elements, `with { }` sets visual properties.

## Scales

Scales map data values to pixel positions. Create them, then call `.Map(value)` in accessors.

```csharp
// Continuous: numeric domain -> pixel range
var xs = new LinearScale([0, 100], [left, left + pw]).Nice();
var ys = new LinearScale([0, maxVal], [top + ph, top]).Nice();  // Y is inverted (0 at bottom)

// Categorical: discrete labels -> bands
var band = BandScale.Create(labels)
    .SetRange(0, plotW)
    .SetPaddingInner(0.2)
    .SetPaddingOuter(0.1);
double x = left + band.Map("Jan");
double barWidth = band.Bandwidth;
```

Always call `.Nice()` on Y scales to get clean tick numbers. The Y range goes from `[bottom, top]` (inverted) so larger values map to smaller Y pixel values.

## DSL Primitives

All return Reactor Elements. Customize with `with { }` for Fill, Stroke, etc.

```csharp
D3Rect(x, y, width, height)                          // Rectangle
D3Circle(cx, cy, r: 4)                               // Circle (name r: when literal)
D3Line(x1, y1, x2, y2)                               // Line segment
D3Text(x, y, "label", fontSize: 11, foreground: brush) // Left-aligned text
D3TextRight(x, y, "label", width: 50, fontSize: 10)  // Right-aligned (for Y axis labels)
D3TextCenter(x, y, "label", width: 40, fontSize: 10) // Center-aligned
```

Use named parameters for `fontSize:`, `foreground:`, `r:`, `width:`, `alpha:`, `opacity:` — bare numeric literals are ambiguous without names.

## Generator Helpers

Use the one-shot DSL helpers instead of constructing generator instances:

```csharp
// Line from data -> PathElement (one expression, no intermediate variables)
D3LinePath(data, x: d => xs.Map(d.x), y: d => ys.Map(d.y),
    stroke: Brush(Palette[0]), strokeWidth: 2, curve: D3Curve.MonotoneX)

// Area from data -> PathElement
D3AreaPath(data, x: d => xs.Map(d.x), y0: d => ys.Map(0), y1: d => ys.Map(d.y),
    fill: Brush(Palette[0], opacity: 0.3))

// Arc sector -> PathElement (for pie slices, sunbursts)
D3ArcPath(startAngle, endAngle, cx, cy,
    innerRadius: 80, outerRadius: 150, fill: sliceBrush)

// Pie slices from data -> Element[] (simple case, no labels)
D3Pie(data, value: d => d.Value, cx, cy,
    outerRadius: 150, innerRadius: 0, sort: false)
```

Always use named parameters `x:`, `y:`, `y0:`, `y1:`, `value:` on these helpers — three consecutive lambdas are unreadable without names.

For pie charts with labels, use the static one-shot + D3ArcPath:
```csharp
var arcs = PieGenerator.Generate(data, value: d => d.Value, sort: false, padAngle: 0.02);
arcs.SelectMany((a, i) => new Element[]
{
    D3ArcPath(a.StartAngle, a.EndAngle, cx, cy, outerRadius: 150, fill: Brush(Palette[i])),
    D3Text(cx + lx, cy + ly, a.Data.Name, fontSize: 10, foreground: brush),
})
```

## Composite Helpers

```csharp
D3Grid(yScale, left, plotWidth)                    // Horizontal grid lines -> Element[]
D3Axes(xScale, yScale, left, top, width, height)   // Axis lines + tick labels -> Element[]
D3Legend(x, y, items.Select((label, brush) => (label, brush)))  // Vertical legend -> Element[]
D3Link(x1, y1, x2, y2, stroke: brush)              // Bezier tree link -> PathElement
```

## Color Helpers

```csharp
Brush(Palette[0])                      // Categorical color from D3 Category10
Brush(Palette[i], opacity: 0.6)        // With opacity (always name opacity:)
Brush("#2ca02c")                       // From hex string
Gray(100)                              // Grayscale
Gray(100, alpha: 180)                  // Grayscale with alpha (always name alpha:)
```

`Palette` is `D3Color.Category10` — 10 categorical colors. Access with `Palette[i % Palette.Length]` to avoid index errors.

## Data-to-Elements Patterns

### Simple projection — `.Select()`

When each data item produces one element:

```csharp
.. points.Select(p => (Element)(D3Circle(xs.Map(p.x), ys.Map(p.y), r: 4)
    with { Fill = fill }))
```

The `(Element)` cast is sometimes needed for spread in collection expressions — this is a C# limitation, not a DSL issue.

### Multi-element projection — `.SelectMany()`

When each data item produces multiple elements (e.g., bar + label):

```csharp
.. items.SelectMany(item => new Element[]
{
    D3Rect(x, y, w, h) with { Fill = fill },
    D3Text(x + 4, y + 3, item.Name, fontSize: 9, foreground: Gray(20)),
})
```

### Query syntax — `from`/`let`/`select`

When you need 3+ intermediate values before the final element, use query syntax instead of a multi-line lambda:

```csharp
.. (from t in candles.Select((c, i) => (c, i))
    let cx = xs.Map(t.i)
    let bullish = t.c.Close >= t.c.Open
    let brush = bullish ? bullBrush : bearBrush
    let bodyTop = ys.Map(Math.Max(t.c.Open, t.c.Close))
    let bodyH = Math.Max(ys.Map(Math.Min(t.c.Open, t.c.Close)) - bodyTop, 1)
    from el in new Element[]
    {
        D3Line(cx, ys.Map(t.c.High), cx, ys.Map(t.c.Low)) with { Stroke = brush },
        D3Rect(cx - barW / 2, bodyTop, barW, bodyH) with { Fill = brush },
    }
    select el)
```

Keep `.Select()` for simple 1-line projections. Switch to query syntax at 3+ `let` bindings.

### Stacked/grouped series

```csharp
.. series.SelectMany((s, si) =>
{
    var fill = Brush(Palette[si]);
    return months.Select((month, j) =>
    {
        var pt = s.Points[j];
        double x = left + band.Map(month);
        return D3Rect(x, y1Screen, band.Bandwidth, height) with { Fill = fill };
    });
})
```

## Things to Do

- **Return a single `D3Canvas(W, H, [...])` expression** — the entire chart is one return statement
- **Use `[.. spread]` syntax** to compose flat element arrays: `[.. grid, .. axes, .. bars, title]`
- **Use LINQ `.Select()` / `.SelectMany()`** to map data to elements — no for-loops
- **Use `with { Fill = ..., Stroke = ... }`** to set visual properties on elements
- **Use named parameters** on calls with multiple double/brush args: `fontSize:`, `foreground:`, `opacity:`, `alpha:`, `r:`, `x:`, `y:`, `y0:`, `y1:`, `value:`, `width:`
- **Use `D3LinePath` / `D3AreaPath`** instead of constructing LineGenerator/AreaGenerator instances
- **Use `D3ArcPath`** instead of constructing ArcGenerator instances
- **Use `D3Grid` / `D3Axes` / `D3Legend`** instead of manually building grid lines, axis labels, or legends
- **Use `Palette[i % Palette.Length]`** to avoid index-out-of-range on category colors
- **Use `D3Extent.Extent(data, accessor)`** for finding min/max — not manual loops
- **Use `.Nice()` on scales** to get clean tick boundaries
- **Pre-compute brushes** outside the element expression when reused: `var fill = Brush(Palette[0], opacity: 0.6);`

## Things to Avoid

- **Don't construct generator instances** (e.g., `new ArcGenerator()`, `LineGenerator.Create(...)`) when a DSL helper exists — use `D3LinePath`, `D3AreaPath`, `D3ArcPath`, `D3Pie` instead
- **Don't use for-loops** to build element arrays — use `.Select()`, `.SelectMany()`, or query syntax
- **Don't use `XamlHostElement`** or raw XAML — everything should go through the D3 DSL primitives
- **Don't use `.Set(tb => tb.TextAlignment = ...)`** — use `D3TextCenter` or `D3TextRight` instead
- **Don't use `Text(...).Foreground(...).Canvas(...)`** — use `D3Text(x, y, text, fontSize:, foreground:)` instead
- **Don't create mutable state** — no `var elements = new List<Element>(); elements.Add(...)` patterns
- **Don't use `Array.Empty<Element>()`** in ternary spreads for null paths — `D3Path` accepts null gracefully
- **Don't duplicate generators** — e.g., creating two identical AreaGenerators when one suffices; or creating a separate `labelArc` ArcGenerator just for centroids (use `ArcGenerator.Centroid(...)` static method instead)
- **Don't use `.Where().Select()` chains that shift indices** — either inline the filter or use query syntax with `where` before `select` to preserve the original index
- **Don't write bare numeric literals** for optional parameters — `Gray(100, 180)` is ambiguous; write `Gray(100, alpha: 180)`
- **Don't use `Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)`** — write `Brush("#ffffff")`
- **Don't manually build legends** with `SelectMany` producing `(rect, text)` pairs — use `D3Legend(x, y, items)`
- **Don't manually build grid lines** with tick iteration — use `D3Grid(yScale, left, width)`
- **Don't manually build axis labels** when `D3Axes(xs, ys, ...)` covers your case
- **Don't use `D3Color.Parse(...)` then `Brush(...)`** — just use `Brush("#hex")` directly
