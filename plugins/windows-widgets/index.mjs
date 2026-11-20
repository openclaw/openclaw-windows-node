import { definePluginEntry } from "openclaw/plugin-sdk/plugin-entry";
import { createMercuryTracker, SOURCE_URL } from "./mercury.mjs";
import { presentationSchema } from "./presentation.mjs";
import { getWidgetCapabilities } from "./toolbox.mjs";
import { createWidgetBridge } from "./bridge.mjs";

export default definePluginEntry({
  id: "windows-widgets",
  name: "Windows Widgets",
  description: "Prompt-driven trackers on the native Windows Widgets Board.",
  register(api) {
    let tracker;
    let bridge;
    function getBridge() {
      bridge ??= createWidgetBridge({
        state: api.runtime.state.openKeyedStore({ namespace: "widget-bridge", maxEntries: 20 }),
        invoke: request => api.runtime.nodes.invoke(request),
        nodeId: api.pluginConfig?.nodeId,
        allowedSourceUrls: api.pluginConfig?.allowedSourceUrls ?? [],
      });
      return bridge;
    }
    function getTracker() {
      tracker ??= createMercuryTracker({
        state: api.runtime.state.openKeyedStore({ namespace: "trackers", maxEntries: 1 }),
        invoke: (request) => api.runtime.nodes.invoke(request),
        nodeId: api.pluginConfig?.nodeId,
        refreshMinutes: api.pluginConfig?.refreshMinutes ?? 60,
      });
      return tracker;
    }
    api.registerTool({
      name: "windows_widgets",
      description: `Author and publish native Windows Adaptive Card widgets from caller-collected observations. The recommended workflow is publish with a unique id and snapshot; publish again to update it. This never fetches a source or starts polling. Supply value and actual observedAt, or snapshot.series with timestamped numeric points and explicit units to derive the latest value, observation time and a local PNG graph. Optional artworkFiles transfers selected Gateway-local PNGs. list/status return metadata, not source readings. Call capabilities before authoring. Read the windows-widgets skill, references/adaptive-card-toolbox.md and references/microsoft-widget-guidance.md. Reuse the installed C# provider; do not generate or deploy a new app per widget. Author a complete presentation.card with separate presentation.data, not themes or HTML. Preserve the provider-owned timestamp/source footer and use host-native size/theme conditions. Designer charts need standard fallbacks and native proof. Series owns content.source and source-series artwork; never invent samples, clinical thresholds or automatic hardware links. Use scalar initialState for local preferences. Lighting requires typed literal widget.lighting actions, explicit Windows-local consent and a user click; publication/refresh must not change lights. No arbitrary shell/node commands in cards. style preserves observation time and does not fetch. Existing create/source routes are opt-in polling compatibility, not required for publication. Without id, legacy Mercury actions still track ${SOURCE_URL}. Windows owns pinning; delivery does not prove visible rendering.`,
      parameters: {
        type: "object",
        additionalProperties: false,
        required: ["action"],
        properties: {
          action: { type: "string", enum: ["capabilities", "list", "create", "publish", "style", "refresh", "status", "stop"] },
          id: { type: "string", pattern: "^[a-z0-9][a-z0-9-]{0,63}$", description: "Independent reusable widget id. Omit only for the legacy Mercury tracker." },
          title: { type: "string", minLength: 1, maxLength: 100 },
          refreshMinutes: { type: "integer", minimum: 1, maximum: 1440 },
          staleAfterMinutes: { type: "integer", minimum: 5, maximum: 10080 },
          source: {
            type: "object", additionalProperties: false, required: ["kind", "url"],
            description: "Exact URL must already be in plugin config allowedSourceUrls. json requires valuePointer; nightscout requires units. Scalar fields are placed in content.source. observedAtPointer is an ISO timestamp; otherwise observation time is the collection time.",
            properties: {
              kind: { enum: ["json", "nightscout"] }, url: { type: "string", maxLength: 2048 },
              valuePointer: { type: "string" }, observedAtPointer: { type: "string" },
              fields: { type: "object", maxProperties: 16, additionalProperties: { type: "string" } },
              valueMap: { type: "object", maxProperties: 16, additionalProperties: { type: "string", maxLength: 120 } },
              units: { enum: ["mg/dL", "mmol/L"] }, windowMinutes: { type: "integer", minimum: 15, maximum: 1440 },
              maxPoints: { type: "integer", minimum: 2, maximum: 96 },
              gapMinutes: { type: "integer", minimum: 1, maximum: 60 },
            },
          },
          snapshot: {
            type: "object", additionalProperties: false, required: ["title", "sourceUrl"],
            anyOf: [{ required: ["value", "observedAt"] }, { required: ["series"] }],
            description: "Bot-pushed observations, without fetching, polling or hardware execution. Supply value and observedAt, or series to derive both. With series, optional supplied facts must match its latest sample. Each publish replaces the complete snapshot. Do not copy sensitive data into responses.",
            properties: {
              title: { type: "string", maxLength: 100 }, value: { type: "string", maxLength: 120 },
              detail: { type: "string", maxLength: 600 }, sourceUrl: { type: "string", maxLength: 2048 },
              observedAt: { type: "string" }, staleAfterMinutes: { type: "integer", minimum: 5, maximum: 10080 },
              series: {
                type: "object", additionalProperties: false, required: ["points", "units"],
                description: "Actual caller-collected values already in the declared units. Sorts timestamps, merges identical duplicates, rejects conflicting duplicates and leaves gaps unconnected. No unit conversion or clinical interpretation.",
                properties: {
                  units: { type: "string", minLength: 1, maxLength: 32 },
                  windowMinutes: { type: "integer", minimum: 15, maximum: 1440, default: 240 },
                  gapMinutes: { type: "integer", minimum: 1, maximum: 60, default: 15 },
                  points: {
                    type: "array", minItems: 1, maxItems: 96,
                    items: { type: "object", additionalProperties: false, required: ["x", "y"],
                      properties: { x: { type: "string", description: "Actual ISO timestamp with timezone." },
                        y: { type: "number", description: "Finite numeric value in the declared units." } } },
                  },
                },
              },
              presentation: presentationSchema,
            },
          },
          sourceUrl: { type: "string", enum: [SOURCE_URL] },
          presentation: presentationSchema,
          artworkFiles: {
            type: "object",
            description: "Optional named PNG files created or selected on the Gateway host. With create/style or publish (snapshot.presentation), these are transferred to the approved Windows node as presentation.assets. Use openclaw-asset:<name> in the card. Names are chosen for this design, not presets. PNG only, 262144 decoded bytes total, up to 4096 pixels per side. Obtain user agreement for the selected artwork and its transfer; never send secrets or unrelated files.",
            propertyNames: { pattern: "^[a-z0-9][a-z0-9-]{0,63}$" },
            additionalProperties: { type: "string" },
          },
        },
      },
      async execute(_id, params, signal) {
        if (params.action === "capabilities") {
          if (Object.keys(params).some(key => key !== "action"))
            throw new Error("Widget capabilities does not accept other arguments.");
          const details = getWidgetCapabilities();
          return { content: [{ type: "text", text: JSON.stringify(details) }], details };
        }
        if (params.id !== undefined || ["list", "publish"].includes(params.action)) {
          const { action, ...args } = params;
          const details = await getBridge().execute(action, { ...args, signal });
          return { content: [{ type: "text", text: JSON.stringify(details) }], details };
        }
        if (Object.keys(params).some(key => !["action", "sourceUrl", "presentation", "artworkFiles"].includes(key)))
          throw new Error("New source parameters require a unique widget id.");
        const details = await getTracker().execute(params.action, {
          signal, sourceUrl: params.sourceUrl, presentation: params.presentation, artworkFiles: params.artworkFiles,
        });
        // Keep image bytes in durable state, not in the agent's tool-response context.
        const response = structuredClone(details);
        const assets = response.presentation?.assets ?? response.snapshot?.presentation?.assets;
        if (assets) {
          response.artwork = Object.fromEntries(Object.entries(assets).map(([name, asset]) =>
            [name, { bytes: Buffer.byteLength(asset.base64, "base64") }]));
          if (response.presentation) delete response.presentation.assets;
          if (response.snapshot?.presentation) delete response.snapshot.presentation.assets;
        }
        return { content: [{ type: "text", text: JSON.stringify(response) }], details: response };
      },
    }, { optional: true });
    api.registerService({
      id: "windows-widgets-refresh",
      apiVersion: 2,
      start({ scheduler }) {
        const owner = getTracker();
        scheduler.schedule({
          id: "mercury-refresh",
          delayMs: 0,
          everyMs: 60_000,
          run: async () => {
            const results = await Promise.allSettled([
              owner.tick(scheduler.signal), getBridge().tick(scheduler.signal),
            ]);
            const failures = results.filter(result => result.status === "rejected").map(result => result.reason);
            if (failures.length) throw new AggregateError(failures, "Windows widget refresh failed.");
          },
        });
      },
      async stop() { await Promise.all([tracker?.drain(), bridge?.drain()]); },
    });
  },
});
