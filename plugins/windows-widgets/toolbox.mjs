// Describes authoring options, not a renderer allowlist or a detected Windows feature set.
// Usage: getWidgetCapabilities() returns a detached, offline catalog for windows_widgets discovery.
const capabilities = {
  schemaVersion: 2,
  nativeRenderingVerified: false,
  scope: "Bot-pushed Adaptive Card snapshots, timestamped history graphs and explicit lighting controls using the existing C# provider. Existing polling and Mercury routes remain compatible.",
  collection: {
    modes: ["json", "nightscout", "manual"],
    recommended: "publish with id and snapshot, then publish again for updates. No fetching or polling. Supply value/observedAt or series.points with explicit units.",
    series: "1-96 actual ISO x/numeric y points; derives latest value/time, native text and gap-aware PNG. No unit conversion. Series reserves content.source and source-series artwork.",
    consent: "Exact administrator-approved allowedSourceUrls for polling; public-address-pinned HTTPS, no redirects, 15 seconds, 256 KiB.",
    routing: "Use id for each new widget. Omit id only for legacy Mercury. Up to 19 reusable new ids plus Mercury on one configured Windows node.",
    privacy: "New tracker tool responses contain metadata, not readings or artwork. Source observations persist locally and appear on an ambient Windows surface.",
  },
  sources: [
    "https://adaptivecards.microsoft.com/designer",
    "https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widgets-create-a-template",
    "https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs",
    "https://learn.microsoft.com/en-us/windows/apps/develop/feeds/feed-providers",
  ],
  reference: "skills/windows-widgets/references/adaptive-card-toolbox.md",
  providerGuidance: "skills/windows-widgets/references/microsoft-widget-guidance.md",
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
      use: "Designer extensions requiring real chart data and native proof. Use TextBlock/FactSet fallback. A boolean status alone supplies no history or percentage.",
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
    executeVerbs: ["widget.set", "widget.increment", "widget.toggle", "widget.reset", "widget.refresh", "widget.lighting"],
    guidance: "Map Submit to Execute/widget.set and ResetInputs to Execute/widget.reset. Only widget.refresh may run automatically. widget.lighting requires local consent, associatedInputs none and admitted literal status/release/color/preset data. It targets the driver's first device; acknowledgments are not physical proof. Only OpenUrl works in unauthenticated contexts. Other host actions need proof. No arbitrary commands.",
  },
  bindings: {
    source: ["title", "value", "detail", "observedAt", "freshness", "sourceUrl"],
    authored: "presentation.data is content; scalar initialState is per-instance state. Series and polling adapters reserve content.source and source-series artwork. lighting.message is provider-owned feedback, never source telemetry.",
    template: ["$when", "$data", "$root", "$index"],
    host: ["$host.widgetSize", "$host.hostTheme", "$host.isSettingsPayload", "$host.isHeaderSupported", "$host.isHeader", "$host.isWebSupported", "$host.isUserContextAuthenticated"],
    resources: "Literal public HTTPS or direct ${$root.content.path} bindings only; no computed or loop-relative resource URLs.",
  },
  examples: ["examples/responsive-status.json", "examples/artwork-status.json", "examples/lighting-controls.json"],
  workflow: "Read the reference, inspect status, author a full presentation, then style without fetching or changing observations. Reserve the provider-owned timestamp/source footer. Verify actual size/theme/rendering in Widgets Board before claiming visual support.",
};

/** Returns the authoring catalog without reading tracker state, networking or selecting a node. */
export function getWidgetCapabilities() {
  return structuredClone(capabilities);
}
