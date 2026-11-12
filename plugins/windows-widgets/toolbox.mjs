// Describes authoring options, not a renderer allowlist or a detected Windows feature set.
// Usage: getWidgetCapabilities() returns a detached, offline catalog for windows_widgets discovery.
const capabilities = {
  schemaVersion: 1,
  nativeRenderingVerified: false,
  scope: "Adaptive Card authoring for Windows widgets. Tracking remains the fixed Mercury API.",
  sources: [
    "https://adaptivecards.microsoft.com/designer",
    "https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widgets-create-a-template",
  ],
  reference: "skills/windows-widgets/references/adaptive-card-toolbox.md",
  publication: {
    versions: "1.0-1.6",
    nativeProperties: "Pass through unchanged, subject to resource, action and size policy. This catalog is not an allowlist.",
    hostSupport: "Windows decides rendering. Designer registrations, schema version and successful delivery do not prove native support.",
    extensionFallback: "Use a standard element as fallback on unfamiliar elements. New layout properties also need a usable ordinary items/columns layout.",
  },
  toolbox: {
    card: {
      types: ["AdaptiveCard"],
      properties: ["version", "body", "actions", "header", "refresh", "backgroundImage", "verticalContentAlignment"],
      use: "Native card root. Do not rebind or hide the root with $data/$when; the provider appends protected timestamp/source controls.",
    },
    text: {
      types: ["TextBlock", "RichTextBlock", "TextRun"],
      properties: ["text", "size", "weight", "color", "isSubtle", "wrap", "maxLines", "horizontalAlignment", "inlines"],
      use: "Native text hierarchy and mixed inline emphasis. Keep meaningful text outside artwork.",
    },
    layout: {
      types: ["Container", "ColumnSet", "Column", "FactSet", "Table", "TableRow", "TableCell", "ActionSet"],
      properties: ["items", "columns", "rows", "facts", "width", "height", "spacing", "separator", "style", "bleed", "verticalContentAlignment"],
      use: "Compose a hero, auto/stretch columns, compact facts and optional detail. Table requires a compatible host.",
    },
    images: {
      types: ["Image", "ImageSet"],
      properties: ["url", "altText", "size", "width", "height", "style", "imageSize", "backgroundImage", "fillMode"],
      use: "Public HTTPS or snapshot-owned openclaw-asset:<name> PNGs. Backgrounds belong on a hero container, not behind the provider footer.",
    },
    inputs: {
      types: ["Input.Text", "Input.Number", "Input.Date", "Input.Time", "Input.Toggle", "Input.ChoiceSet", "Input.Rating"],
      properties: ["id", "label", "value", "placeholder", "isRequired", "errorMessage", "min", "max", "choices", "style"],
      use: "Customize per-instance initialState through matching input IDs and widget.set. Input.Rating is a Designer extension; offer Input.ChoiceSet as fallback.",
    },
    decoration: {
      types: ["Icon", "Badge", "CompoundButton", "Rating", "ProgressBar", "ProgressRing", "CodeBlock", "IconRun", "ImageRun"],
      properties: ["name", "text", "title", "description", "icon", "size", "style", "value", "max", "selectAction", "fallback"],
      use: "Designer extensions, not promised Windows features. Prefer native text/images/columns as fallback. Progress must come from real supplied data.",
    },
    pages: {
      types: ["Carousel", "CarouselPage", "TabSet", "TabPage", "Accordion", "AccordionPage"],
      properties: ["pages", "items", "title", "fallback"],
      use: "Designer extensions requiring native proof. Keep essential status visible in a plain Container fallback. Do not auto-rotate essential information.",
    },
    charts: {
      types: ["Chart.Line", "Chart.Donut", "Chart.Pie", "Chart.Gauge", "Chart.HorizontalBar", "Chart.HorizontalBar.Stacked", "Chart.VerticalBar", "Chart.VerticalBar.Grouped"],
      properties: ["data", "title", "colorSet", "showLegend", "showTitle", "xAxisTitle", "yAxisTitle", "yMin", "yMax", "value", "min", "max", "segments", "fallback"],
      dataFields: {
        "Chart.Line": ["legend", "color", "values", "x", "y"],
        "Chart.Donut": ["legend", "color", "value"],
        "Chart.Pie": ["legend", "color", "value"],
        "Chart.HorizontalBar": ["color", "x", "y"],
        "Chart.HorizontalBar.Stacked": ["title", "data", "legend", "color", "value"],
        "Chart.VerticalBar": ["color", "x", "y"],
        "Chart.VerticalBar.Grouped": ["legend", "color", "values", "x", "y"],
        "Chart.Gauge": ["segments", "legend", "color", "size"],
      },
      use: "Designer extensions requiring real chart data and native proof. Use TextBlock/FactSet fallback. The Mercury boolean source supplies no history or percentage.",
    },
    advancedLayouts: {
      types: ["Layout.Stack", "Layout.Flow", "Layout.AreaGrid", "GridArea"],
      properties: ["layouts", "targetWidth", "areas", "grid.area", "itemWidth", "minItemWidth", "maxItemWidth", "itemFit", "columnSpacing", "rowSpacing", "column", "row", "columnSpan", "rowSpan"],
      use: "Designer layout objects, not body elements. They may be ignored on Windows; retain a readable standard Container/ColumnSet structure and $host.widgetSize conditions.",
    },
    media: {
      types: ["Media"],
      properties: ["poster", "altText", "sources", "mimeType", "url"],
      use: "Transported but native playback is unverified. Prefer an Image poster and public HTTPS OpenUrl fallback.",
    },
  },
  actions: {
    accepted: ["Action.OpenUrl", "Action.Execute", "Action.ShowCard", "Action.ToggleVisibility"],
    rejected: ["Action.Submit", "Action.ResetInputs", "Action.Popover", "Action.Overflow", "Action.RunCommands", "Action.InsertImage", "Action.OpenUrlDialog"],
    executeVerbs: ["widget.set", "widget.increment", "widget.toggle", "widget.reset", "widget.refresh"],
    guidance: "Map Submit to Execute/widget.set and ResetInputs to Execute/widget.reset when appropriate; do not send them unchanged. Only OpenUrl is supported in unauthenticated Windows contexts. ShowCard/ToggleVisibility still need host proof. No arbitrary commands, cloud/bot integrations or authentication.",
  },
  bindings: {
    source: ["title", "value", "detail", "observedAt", "freshness", "sourceUrl"],
    authored: "presentation.data is available as content; scalar initialState is per-instance state.",
    template: ["$when", "$data", "$root", "$index"],
    host: ["$host.widgetSize", "$host.hostTheme", "$host.isSettingsPayload", "$host.isHeaderSupported", "$host.isHeader", "$host.isWebSupported", "$host.isUserContextAuthenticated"],
    resources: "Literal public HTTPS or direct ${$root.content.path} bindings only; no computed or loop-relative resource URLs.",
  },
  examples: ["examples/responsive-status.json", "examples/mercury-demo.json", "examples/fluent-mystic.json"],
  workflow: "Read the reference, inspect status, author a full presentation, then style without fetching or changing observations. Reserve the provider-owned timestamp/source footer. Verify actual size/theme/rendering in Widgets Board before claiming visual support.",
};

/** Returns the authoring catalog without reading tracker state, networking or selecting a node. */
export function getWidgetCapabilities() {
  return structuredClone(capabilities);
}
