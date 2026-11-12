import { definePluginEntry } from "openclaw/plugin-sdk/plugin-entry";
import { createMercuryTracker, SOURCE_URL } from "./mercury.mjs";
import { presentationSchema } from "./presentation.mjs";
import { getWidgetCapabilities } from "./toolbox.mjs";

export default definePluginEntry({
  id: "windows-widgets",
  name: "Windows Widgets",
  description: "Prompt-driven trackers on the native Windows Widgets Board.",
  register(api) {
    let tracker;
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
      description: `Create, design, restyle, refresh, inspect or stop a persistent native Windows widget tracking whether Mercury is in retrograde using ${SOURCE_URL}. Call capabilities before authoring for the Adaptive Cards Designer toolbox, permitted actions and Windows host limits, without reading state or contacting a node. Read the windows-widgets skill and references/adaptive-card-toolbox.md. Author a complete presentation.card from the user's request; native elements/properties pass through, not preset themes or HTML. Use examples/responsive-status.json for responsive columns, rich text, fallback and preferences, or examples/fluent-mystic.json for Fluent weather-widget artwork composition. Use semantic host styles and preserve the provider-owned timestamp/source footer. Newer Designer charts, badges, paging and layouts need standard fallbacks and native proof, not assumptions of Windows support or fabricated data. Use presentation.data for content bindings and scalar initialState for per-instance preferences. Execute actions are bounded local state operations, never arbitrary shell/node commands. Use style to change design without fetching or changing observation time. The source remains this Mercury API, not arbitrary source tracking. Creation enables periodic polling on the configured Windows PC and requires Companion's widget PoC opt-in. Windows owns pinning; successful delivery does not prove visible rendering.`,
      parameters: {
        type: "object",
        additionalProperties: false,
        required: ["action"],
        properties: {
          action: { type: "string", enum: ["capabilities", "create", "style", "refresh", "status", "stop"] },
          sourceUrl: { type: "string", enum: [SOURCE_URL] },
          presentation: presentationSchema,
          artworkFiles: {
            type: "object",
            description: "Optional named PNG files created or selected on the Gateway host. With create/style, these are read and transferred to the approved Windows node as presentation.assets. Use openclaw-asset:<name> in the card. Names are chosen for this design, not presets. PNG only, 262144 decoded bytes total, up to 4096 pixels per side. Obtain user agreement for the selected artwork and its transfer; never send secrets or unrelated files.",
            propertyNames: { pattern: "^[a-z0-9][a-z0-9-]{0,63}$" },
            additionalProperties: { type: "string" },
          },
        },
      },
      async execute(_id, params, signal) {
        if (params.action === "capabilities") {
          if (params.presentation !== undefined || params.artworkFiles !== undefined || params.sourceUrl !== undefined)
            throw new Error("Widget capabilities does not accept presentation, artworkFiles or sourceUrl.");
          const details = getWidgetCapabilities();
          return { content: [{ type: "text", text: JSON.stringify(details) }], details };
        }
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
          run: () => owner.tick(scheduler.signal),
        });
      },
      async stop() { await tracker?.drain(); },
    });
  },
});
