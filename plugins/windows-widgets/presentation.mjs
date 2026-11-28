// Native Adaptive Card transport, not a second element/property vocabulary.
// Use validatePresentation before a new request, migratePresentation only for persisted PoC state.
const verbs = ["widget.set", "widget.increment", "widget.toggle", "widget.reset", "widget.refresh", "widget.lighting"];
export const MAX_ARTWORK_BYTES = 262144;
const MAX_IMAGE_DIMENSION = 4096;
const structureArrays = new Set(["body", "items", "columns", "images", "inlines", "rows", "cells", "actions", "layouts", "pages", "sources", "captionSources"]);
const structureObjects = new Set(["selectAction", "inlineAction", "action", "card", "metadata"]);
export const presentationSchema = {
  type: "object", additionalProperties: false, required: ["card"],
  description: "Design a complete native Adaptive Card, not a preset. Native elements/properties, backgroundImage, rich text, columns, images, inputs, actions, header and Adaptive Card templating ($when, $data, $host.widgetSize, $host.hostTheme, $host.isSettingsPayload) pass to Windows, which decides host support. Version 1.0-1.6. Source facts are read-only bindings ${title}, ${value}, ${detail}, ${observedAt}, ${freshness}, ${sourceUrl}. Separate data is content; polling adapters reserve content.source and source-series artwork. Scalar initialState keys are per-instance state. Execute verbs: widget.set (input IDs match keys), widget.increment (data.key, amount), widget.toggle (data.key), widget.reset, widget.refresh (cached render). Separately opted-in widget.lighting accepts literal status/release/color/preset data with associatedInputs none, only on user click. It never accepts commands or bound inputs; driver ACK is not physical proof. Only widget.refresh may run automatically. No arbitrary commands, credentials or HTML. Images/backgrounds/icons can use public HTTPS or openclaw-asset:<name> for PNG artwork supplied in assets, with any valid name, not built-in designs. Prefer artworkFiles on the plugin tool for locally generated or selected PNG files; the plugin encodes them so you never need to paste base64. Resource bindings must be direct ${$root.content.path}; use separate conditional images to switch artwork with data. Navigation remains HTTPS-only. Provider appends observation/source. Layout/data/state max 65536 UTF-8 bytes; artwork max 262144 decoded bytes total, 4096 pixels per side, PNG only; 256 objects, JSON depth 24, state 4096 bytes. Snapshot max 524288 bytes, expanded native template max 1048576 bytes. {} does not reset: use a new minimal card.",
  properties: {
    card: {
      type: "object", additionalProperties: true, required: ["type", "version", "body"],
      properties: {
        type: { const: "AdaptiveCard" }, version: { type: "string", pattern: "^1\\.[0-6]$" },
        body: { type: "array", items: { type: "object", additionalProperties: true } },
      },
    },
    data: { type: "object", additionalProperties: true },
    assets: {
      type: "object",
      description: "Snapshot-owned PNG artwork, keyed by agent-chosen asset names. Reference as openclaw-asset:<name>. Prefer the tool's artworkFiles to encode local PNGs.",
      propertyNames: { pattern: "^[a-z0-9][a-z0-9-]{0,63}$" },
      additionalProperties: {
        type: "object", additionalProperties: false, required: ["base64"],
        properties: { base64: { type: "string", maxLength: 349528 } },
      },
    },
    initialState: {
      type: "object", additionalProperties: { type: ["string", "number", "boolean", "null"] },
      propertyNames: { pattern: "^[A-Za-z0-9_]{1,64}$" },
    },
  },
};

const object = value => value !== null && typeof value === "object" && !Array.isArray(value);
export function validatePublicUrl(value) {
  if (typeof value !== "string" || value.length > 2048 || value.includes("${"))
    throw new Error("Widget resource requires a literal public HTTPS URL or a direct content binding.");
  const url = new URL(value);
  const host = url.hostname.replace(/\.$/, "");
  if (url.protocol !== "https:" || url.port || url.username || url.password ||
    !host.includes(".") || /^[\d.]+$/.test(host) || host.includes(":") ||
    /\.(localhost|local|internal|home|lan)$/i.test(host))
    throw new Error("Widget resources require public HTTPS URLs without credentials, custom ports or local hosts.");
}

function validateLightingAction(action) {
  const data = action.data;
  if (action.associatedInputs !== "none" || !object(data) ||
    Object.entries(data).some(([key, value]) => !["operation", "color", "preset", "accentColor"].includes(key) ||
      typeof value !== "string"))
    throw new Error("Lighting actions require literal data and associatedInputs none.");
  const color = value => typeof value === "string" && /^#[0-9A-Fa-f]{6}$/.test(value);
  const count = Object.keys(data).length;
  const valid = ["status", "release"].includes(data.operation) ? count === 1
    : data.operation === "color" ? count === 2 && color(data.color)
    : data.operation === "preset" && ["solid", "wave", "breathe", "twinkle", "gradient", "rainbow"].includes(data.preset) &&
      color(data.color) && (data.accentColor === undefined || color(data.accentColor)) &&
      count === (data.accentColor === undefined ? 3 : 4);
  if (!valid) throw new Error("Lighting requires status, release, a #RRGGBB color, or a fixed preset and colors.");
}

/** Enforces the same bounded publication policy as C#; Windows owns final card schema/rendering support. */
export function validatePresentation(value) {
  if (!object(value) || Object.keys(value).some(key => !["card", "data", "initialState", "assets"].includes(key)))
    throw new Error("Widget presentation requires card, optional data, initialState and assets, not preset themes.");
  const { assets, ...layout } = value;
  if (Buffer.byteLength(JSON.stringify(layout), "utf8") > 65536)
    throw new Error("Widget presentation exceeds 65536 bytes.");
  if (assets !== undefined && !object(assets))
    throw new Error("Widget assets must be a named object of PNG base64 resources.");
  const artwork = new Set();
  let artworkBytes = 0;
  for (const [name, asset] of Object.entries(assets ?? {})) {
    if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(name) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/.test(name))
      throw new Error("Widget artwork names require 1-64 lowercase letters, digits or hyphens, not reserved names.");
    if (!object(asset) || Object.keys(asset).length !== 1 || typeof asset.base64 !== "string")
      throw new Error("Widget artwork requires only a base64 PNG field.");
    if (asset.base64.length > Math.ceil(MAX_ARTWORK_BYTES / 3) * 4)
      throw new Error("Widget artwork exceeds 262144 decoded bytes.");
    const png = Buffer.from(asset.base64, "base64");
    artworkBytes += png.length;
    if (artworkBytes > MAX_ARTWORK_BYTES)
      throw new Error("Widget artwork exceeds 262144 decoded bytes.");
    if (png.length < 45 || png.toString("base64") !== asset.base64 ||
      !png.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])) ||
      png.readUInt32BE(8) !== 13 || png.toString("ascii", 12, 16) !== "IHDR" ||
      !png.subarray(-12).equals(Buffer.from([0,0,0,0,73,69,78,68,174,66,96,130])) ||
      png.readUInt32BE(16) < 1 || png.readUInt32BE(16) > MAX_IMAGE_DIMENSION ||
      png.readUInt32BE(20) < 1 || png.readUInt32BE(20) > MAX_IMAGE_DIMENSION)
      throw new Error("Widget artwork requires a bounded PNG header and end marker, at most 4096 pixels per side.");
    artwork.add("openclaw-asset:" + name);
  }
  if (!object(value.card) || value.card.type !== "AdaptiveCard" ||
    !/^1\.[0-6]$/.test(value.card.version) || !Array.isArray(value.card.body))
    throw new Error("Widget card requires type AdaptiveCard, version 1.0-1.6 and a body array.");
  if (value.data !== undefined && !object(value.data))
    throw new Error("Widget presentation data must be an object, available as content.");
  if (Object.hasOwn(value.card, "$data") || Object.hasOwn(value.card, "$when"))
    throw new Error("Place $data and $when on body elements, not the provider-owned root.");
  if (value.initialState !== undefined && (!object(value.initialState) ||
    Buffer.byteLength(JSON.stringify(value.initialState), "utf8") > 4096 ||
    Object.entries(value.initialState).some(([key, item]) =>
      !/^[A-Za-z0-9_]{1,64}$/.test(key) || (item !== null && typeof item === "object"))))
    throw new Error("Widget initialState requires simple keys and scalar JSON values, at most 4096 bytes.");
  if (value.card.actions !== undefined && !Array.isArray(value.card.actions))
    throw new Error("Widget card actions must be an array.");
  if (value.card.refresh !== undefined && value.card.refresh?.action?.verb !== "widget.refresh")
    throw new Error("Automatic card refresh may only use widget.refresh, never a state mutation.");
  let count = 0;
  let expandedArtworkBytes = 0;
  function resource(input, navigation) {
    if (typeof input === "string" && /^\$\{\$root\.content(?:\.[A-Za-z_][A-Za-z0-9_]*)+\}$/.test(input)) {
      let resolved = value.data;
      for (const key of input.slice(2, -1).split(".").slice(2)) {
        if (!object(resolved) || !Object.hasOwn(resolved, key))
          throw new Error("Widget resource binding was not found in presentation data.");
        resolved = resolved[key];
      }
      input = resolved;
    }
    if (!navigation && artwork.has(input)) {
      const base64 = assets[input.slice("openclaw-asset:".length)].base64;
      // Match the native JSON encoder's escaping of '+' and leave space for layout/provider controls.
      expandedArtworkBytes += "data:image/png;base64,".length + base64.length + 2 + (base64.match(/\+/g)?.length ?? 0) * 5;
      if (expandedArtworkBytes > 1048576 - 65536 * 2)
        throw new Error("Expanded widget artwork exceeds the native template limit. Use fewer repeated image resources.");
      return;
    }
    validatePublicUrl(input);
  }
  function visit(node, depth, template) {
    if (depth > 24) throw new Error("Widget JSON exceeds depth 24.");
    if (Array.isArray(node)) {
      for (const child of node) {
        if (template && typeof child === "string" && child.includes("${"))
          throw new Error("Widget element and action structure must be literal JSON; bind scalar properties instead.");
        visit(child, depth + 1, template);
      }
      return;
    }
    if (!object(node)) return;
    if (++count > 256) throw new Error("Widget presentation exceeds 256 objects.");
    for (const [key, input] of Object.entries(node)) {
      if (template) {
        if (key.includes("${") ||
          (key === "type" && (typeof input !== "string" || input.includes("${"))) ||
          (structureArrays.has(key) && !Array.isArray(input)) ||
          (structureObjects.has(key) && !object(input)) ||
          (["fallback", "header"].includes(key) && typeof input === "string" && input.includes("${")))
          throw new Error("Widget element and action structure must be literal JSON; bind scalar properties instead.");
        if (key === "$when" && (typeof input !== "string" || !input.startsWith("${") || !input.endsWith("}")))
          throw new Error("Widget $when requires an Adaptive Card template expression.");
        if (key === "authentication" || key === "webUrl")
          throw new Error("Widget authentication and HTML web content are not enabled.");
        if (key === "id" && typeof input === "string" && (input.startsWith("openclaw-") || input.includes("${")))
          throw new Error("Widget ids must be literal and must not begin openclaw-.");
        if (key === "url" || key === "iconUrl" || key === "poster" || (key === "backgroundImage" && typeof input === "string"))
          resource(input, key === "url" && node.type === "Action.OpenUrl");
        if (key === "type" && typeof input === "string" && input.startsWith("Action.")) {
          if (!["Action.OpenUrl", "Action.Execute", "Action.ShowCard", "Action.ToggleVisibility"].includes(input))
            throw new Error("Use native OpenUrl, Execute, ShowCard or ToggleVisibility actions, subject to host support.");
          if (input === "Action.OpenUrl" && !Object.hasOwn(node, "url"))
            throw new Error("Widget OpenUrl requires a URL.");
          if (input === "Action.Execute" && !verbs.includes(node.verb))
            throw new Error("Unknown widget Execute verb.");
          if (input === "Action.Execute" && node.verb === "widget.lighting") validateLightingAction(node);
        }
        if (key === "targetElements") {
          if (!Array.isArray(input)) throw new Error("Widget targetElements must be an array.");
          for (const target of input) {
            const id = typeof target === "string" ? target : target?.elementId;
            if (typeof id !== "string" || id.startsWith("openclaw-") || id.includes("${"))
              throw new Error("ToggleVisibility can target only literal, non-provider element ids.");
          }
        }
      }
      visit(input, depth + 1, template && !["data", "choices", "facts"].includes(key));
    }
  }
  visit(value.card, 0, true);
  if (value.data) visit(value.data, 0, false);
  if (assets) visit(assets, 0, false);
  return structuredClone(value);
}

/** Preserve saved PoC compositions without advertising or accepting preset names in new tool calls. */
export function migratePresentation(value) {
  if (!value || Object.hasOwn(value, "card")) return value;
  const theme = value.theme ?? "native";
  if (!["native", "minimal", "cosmic", "mystic"].includes(theme))
    throw new Error("Unknown saved widget preset.");
  const center = theme === "mystic";
  const title = { type: "TextBlock", text: "${title}", weight: "Bolder", wrap: true, horizontalAlignment: center ? "Center" : "Left" };
  const status = { type: "TextBlock", text: "${value}", size: "ExtraLarge", wrap: true, horizontalAlignment: center ? "Center" : "Left" };
  const detail = { type: "TextBlock", text: "${detail}", wrap: true, $when: "${$host.widgetSize != 'small'}" };
  const image = {
    type: "Image", size: "Small", altText: "Mercury", horizontalAlignment: center ? "Center" : "Left",
    url: "https://science.nasa.gov/wp-content/uploads/2023/11/mercury-messenger-globe-pia15162.jpg",
    $when: "${$host.widgetSize != 'small'}",
  };
  let body = value.body ?? (theme === "minimal" ? [title, status] : theme === "mystic" ? [image, title, status, detail] :
    theme === "cosmic" ? [{ type: "ColumnSet", columns: [
      { type: "Column", width: "stretch", items: [title, status] },
      { type: "Column", width: "auto", items: [image] },
    ] }, detail] : [title, status, detail]);
  if (theme === "cosmic" || theme === "mystic") body = [{ type: "Container", style: "emphasis", items: body }];
  return validatePresentation({ card: { type: "AdaptiveCard", version: "1.6", body } });
}
