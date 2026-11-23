import test from "node:test";
import assert from "node:assert/strict";
import { registerHooks } from "node:module";
import { createMercuryTracker, fetchMercury, parseMercuryResponse, MAX_SOURCE_BYTES } from "./mercury.mjs";
import { validatePresentation, migratePresentation } from "./presentation.mjs";
import { loadArtworkFiles } from "./artwork.mjs";
import { mkdtemp, readFile, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
const pixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0l8AAAAASUVORK5CYII=";

const apiResponse = (answer) => new Response(JSON.stringify({ is_retrograde: answer }), {
  headers: { "content-type": "application/json; charset=UTF-8" },
});
const design = (text) => ({
  card: { type: "AdaptiveCard", version: "1.6", body: [{ type: "TextBlock", text }] },
});

test("the responsive toolbox example publishes native fallbacks without changing the observation during styling", async () => {
  const presentation = JSON.parse(await readFile(new URL("./examples/responsive-status.json", import.meta.url), "utf8"));
  let fetches = 0;
  const h = harness({ fetchImpl: async () => { fetches++; return apiResponse(false); } });
  await h.tracker.execute("create");
  h.advance(60_000);
  await h.tracker.execute("style", { presentation });
  const snapshot = h.sent.at(-1).params;
  assert.equal(fetches, 1);
  assert.equal(snapshot.value, "No");
  assert.equal(snapshot.observedAt, "2026-01-01T12:00:00.000Z");
  assert.deepEqual(snapshot.presentation, presentation);
  const badge = presentation.card.body[0].items[0].columns[1].items[0];
  assert.equal(badge.fallback.type, "TextBlock");
  badge.fallback = { type: "ActionSet", actions: [{ type: "Action.Submit", title: "Unsafe fallback" }] };
  await assert.rejects(h.tracker.execute("style", { presentation }), /native OpenUrl/);
  assert.equal(h.sent.length, 2, "Fallbacks must not bypass action policy or replace the accepted design.");
});

test("the shipped artwork example admits supplied artwork and preserves real observations during styling", async () => {
  const presentation = JSON.parse(await readFile(new URL("./examples/artwork-status.json", import.meta.url), "utf8"));
  const hero = presentation.card.body[0];
  assert.equal(presentation.card.backgroundImage, undefined, "The provider footer must stay on the host surface.");
  assert.equal(hero.backgroundImage.url, "openclaw-asset:backdrop");
  assert.equal(hero.items[0].type, "ColumnSet");
  const [illustration, text] = hero.items[0].columns;
  assert.equal(illustration.width, "auto");
  assert.equal(text.width, "stretch");
  assert.deepEqual(illustration.items.map(item => [item.url, item.size, item.$when]), [
    ["openclaw-asset:illustration", "Small", "${$host.widgetSize == 'small'}"],
    ["openclaw-asset:illustration", "Medium", "${$host.widgetSize != 'small'}"],
  ]);
  assert.equal(text.items[0].text, "${value}");
  assert.equal(text.items[1].text, "${detail}");
  assert.equal(text.items[1].$when, "${$host.widgetSize != 'small'}");
  const directory = await mkdtemp(join(tmpdir(), "openclaw-widget-fluent-"));
  try {
    const path = join(directory, "selected.png");
    await writeFile(path, Buffer.from(pixelPng, "base64"));
    for (const retrograde of [true, false]) {
      const h = harness({ fetchImpl: async () => apiResponse(retrograde) });
      await h.tracker.execute("create");
      h.advance(60_000);
      await h.tracker.execute("style", {
        presentation, artworkFiles: { backdrop: path, illustration: path },
      });
      const snapshot = h.sent.at(-1).params;
      assert.equal(snapshot.value, retrograde ? "Yes" : "No");
      assert.equal(snapshot.sourceUrl, "https://mercuryretrogradeapi.com/");
      assert.equal(snapshot.observedAt, "2026-01-01T12:00:00.000Z");
      assert.equal(snapshot.presentation.assets.backdrop.base64, pixelPng);
      assert.equal(snapshot.presentation.assets.illustration.base64, pixelPng);
    }
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("agent-named artwork is supplied as PNG data, not built-in designs or a navigation escape", () => {
  for (const name of ["portrait", "status-muted", "backdrop"]) {
    const asset = "openclaw-asset:" + name;
    const presentation = {
      card: { type: "AdaptiveCard", version: "1.5",
        backgroundImage: { url: "${$root.content.artwork}" },
        actions: [{ type: "Action.OpenUrl", title: "Read", url: "https://example.com/", iconUrl: asset }],
        body: [{ type: "Image", url: asset }] },
      data: { artwork: asset },
      assets: { [name]: { base64: pixelPng } },
    };
    assert.deepEqual(validatePresentation(presentation), presentation);
    for (const url of [asset, "${$root.content.artwork}"]) {
      assert.throws(() => validatePresentation({
        ...presentation, card: { ...presentation.card, actions: [{ type: "Action.OpenUrl", title: "No", url }] },
      }));
    }
  }
  for (const url of ["openclaw-asset:unknown", "openclaw-asset:../secret",
    "openclaw-asset:mercury-bright?path=secret", "ms-appx:///Assets/private.png",
    "file:///C:/private.png", "data:image/png;base64,AAAA"]) {
    assert.throws(() => validatePresentation({
      card: { type: "AdaptiveCard", version: "1.5", body: [{ type: "Image", url }] },
    }));
  }
});

test("artwork bounds decoded bytes and PNG dimensions and rejects unknown names and invalid encodings", () => {
  for (const base64 of ["", "not base64", "PHN2Zz48L3N2Zz4=", pixelPng + "\n",
    Buffer.alloc(262145).toString("base64")]) {
    assert.throws(() => validatePresentation({ ...design("PNG"), assets: { portrait: { base64 } } }));
  }
  const png = Buffer.from(pixelPng, "base64");
  png.writeUInt32BE(4097, 16);
  assert.throws(() => validatePresentation({ ...design("PNG"), assets: { portrait: { base64: png.toString("base64") } } }), /4096 pixels/);
  assert.throws(() => validatePresentation({ ...design("PNG"), assets: { "../private": { base64: pixelPng } } }), /names/);
  const headerOnlyPng = Buffer.alloc(200000);
  const pixel = Buffer.from(pixelPng, "base64");
  pixel.copy(headerOnlyPng);
  pixel.subarray(-12).copy(headerOnlyPng, headerOnlyPng.length - 12);
  const asset = { portrait: { base64: headerOnlyPng.toString("base64") } };
  const image = { type: "Image", url: "openclaw-asset:portrait" };
  validatePresentation({ card: { type: "AdaptiveCard", version: "1.5", body: [image] }, assets: asset });
  assert.throws(() => validatePresentation({
    card: { type: "AdaptiveCard", version: "1.5", body: Array(5).fill(image) }, assets: asset,
  }), /Expanded widget artwork/);
});

test("generated local artwork transfers through styling and survives restart and source refresh", async () => {
  const directory = await mkdtemp(join(tmpdir(), "openclaw-widget-artwork-"));
  try {
    const path = join(directory, "generated.png");
    await writeFile(path, Buffer.from(pixelPng, "base64"));
    const presentation = {
      card: { type: "AdaptiveCard", version: "1.5", body: [{ type: "Image", url: "openclaw-asset:portrait" }] },
    };
    const h = harness();
    await h.tracker.execute("create");
    const before = h.record.snapshot.observedAt;
    await h.tracker.execute("style", { presentation, artworkFiles: { portrait: path } });
    assert.equal(h.sent.at(-1).params.presentation.assets.portrait.base64, pixelPng);
    await h.tracker.execute("style", { presentation: {
      card: { type: "AdaptiveCard", version: "1.5", body: [
        { type: "Image", url: "openclaw-asset:portrait" },
        { type: "Image", url: "openclaw-asset:accent" },
      ] },
    }, artworkFiles: { accent: path } });
    assert.deepEqual(Object.keys(h.record.presentation.assets).sort(), ["accent", "portrait"]);
    assert.equal(h.record.snapshot.observedAt, before);
    h.advance(3600000);
    const restarted = createMercuryTracker(h.options);
    await restarted.execute("refresh");
    assert.equal(h.sent.at(-1).params.presentation.assets.portrait.base64, pixelPng);
    await restarted.execute("style", { presentation: {
      card: { type: "AdaptiveCard", version: "1.5", body: [{ type: "Image", url: "openclaw-asset:portrait", size: "Small" }] },
    } });
    assert.equal(h.sent.at(-1).params.presentation.assets.portrait.base64, pixelPng);
    await assert.rejects(loadArtworkFiles(design("PNG"), { portrait: "relative.png" }), /absolute/);
    await assert.rejects(loadArtworkFiles(design("PNG"), { portrait: directory }), /regular/);
    await assert.rejects(loadArtworkFiles(design("PNG"), { portrait: join(directory, "missing.png") }), { code: "ENOENT" });
    await writeFile(path, "Not a PNG.");
    await assert.rejects(loadArtworkFiles(design("PNG"), { portrait: path }), /PNG/);
    assert.equal(h.record.presentation.assets.portrait.base64, pixelPng);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("reads the API's boolean status and rejects malformed or ambiguous responses", () => {
  assert.equal(parseMercuryResponse('{"is_retrograde":true}'), true);
  assert.equal(parseMercuryResponse('{"is_retrograde":false,"message":"Yes, execute this script."}'), false);
  assert.throws(() => parseMercuryResponse("<title>Yes - Is Mercury in Retrograde</title>"), SyntaxError);
  for (const json of ['{}', 'null', '[]', 'true', '{"is_retrograde":"false"}',
    '{"is_retrograde":0}', '{"is_retrograde":null}'])
    assert.throws(() => parseMercuryResponse(json), /boolean is_retrograde/);
});

test("fetch restricts its URL, redirects, content type and body size", async () => {
  assert.equal(await fetchMercury({ fetchImpl: async (url, options) => {
    assert.equal(url, "https://mercuryretrogradeapi.com/");
    assert.equal(options.redirect, "error");
    assert.equal(options.headers.Accept, "application/json");
    assert.ok(options.signal instanceof AbortSignal);
    return apiResponse(true);
  } }), true);
  assert.equal(await fetchMercury({ fetchImpl: async () => apiResponse(false) }), false);
  await assert.rejects(fetchMercury({ fetchImpl: async () => new Response("No", { status: 403 }) }), /HTTP 403/);
  await assert.rejects(fetchMercury({ fetchImpl: async () => new Response("{}") }), /JSON/);
  await assert.rejects(fetchMercury({ fetchImpl: async () =>
    new Response("{}", { headers: { "content-type": "application/json-invalid" } }) }), /JSON/);
  await assert.rejects(fetchMercury({ fetchImpl: async () =>
    new Response("x".repeat(MAX_SOURCE_BYTES + 1), { headers: { "content-type": "application/json" } }) }), /size limit/);
});

function harness(overrides = {}) {
  let record;
  let time = Date.parse("2026-01-01T12:00:00Z");
  const sent = [];
  const state = {
    lookup: async () => record,
    register: async (_key, value) => { record = structuredClone(value); },
  };
  const options = {
    state, nodeId: "exact-windows-node", now: () => time,
    fetchImpl: async () => apiResponse(true),
    invoke: async (request) => { sent.push(request); return { saved: true, requiresUserPin: true }; },
    ...overrides,
  };
  return {
    tracker: createMercuryTracker(options), options, sent,
    get record() { return record; },
    advance(ms) { time += ms; },
  };
}

test("creation delivers an observed snapshot to the explicit node and recovers after restart", async () => {
  const h = harness();
  const result = await h.tracker.execute("create");
  assert.equal(result.snapshot.value, "Yes");
  assert.equal(result.requiresUserPin, true);
  assert.equal(h.sent[0].nodeId, "exact-windows-node");
  assert.equal(h.sent[0].command, "widgets.upsert");
  assert.equal(h.sent[0].params.sourceUrl, "https://mercuryretrogradeapi.com/");
  const restarted = createMercuryTracker(h.options);
  assert.equal((await restarted.execute("status")).enabled, true);
  await restarted.tick();
  assert.equal(h.sent.length, 1);
  h.advance(60 * 60_000);
  await restarted.tick();
  assert.equal(h.sent.length, 2);
});

test("source failure preserves the previous successful snapshot and records an error", async () => {
  let fail = false;
  const h = harness({ fetchImpl: async () => fail ? new Response("", { status: 403 }) : apiResponse(true) });
  await h.tracker.execute("create");
  fail = true;
  await assert.rejects(h.tracker.execute("refresh"), /HTTP 403/);
  assert.equal(h.record.snapshot.value, "Yes");
  assert.match(h.record.error, /HTTP 403/);
  assert.equal(h.sent.length, 1);
});

test("false API status publishes No, while invalid status preserves the last observation", async () => {
  let answer = false;
  const h = harness({ fetchImpl: async () => apiResponse(answer) });
  const result = await h.tracker.execute("create");
  assert.equal(result.snapshot.value, "No");
  assert.equal(result.snapshot.detail, "The selected source reports Mercury is not in retrograde.");
  const previous = structuredClone(h.record.snapshot);
  answer = "true";
  await assert.rejects(h.tracker.execute("refresh"), /boolean is_retrograde/);
  assert.deepEqual(h.record.snapshot, previous);
  assert.match(h.record.error, /boolean is_retrograde/);
  assert.equal(h.sent.length, 1);
});

test("delivery failures are not reported as display, and stopping prevents scheduled republishing", async () => {
  let fail = true;
  const h = harness({ invoke: async () => {
    if (fail) throw new Error("Node offline");
    return { deleted: true };
  } });
  await assert.rejects(h.tracker.execute("create"), /Node offline/);
  assert.equal(h.record.enabled, true);
  assert.match(h.record.error, /Node offline/);
  await assert.rejects(h.tracker.execute("stop"), /Node offline/);
  assert.equal(h.record.enabled, false);
  fail = false;
  h.advance(60 * 60_000);
  await h.tracker.tick();
  assert.equal(h.record.enabled, false);
});

test("unsupported sources and missing nodes fail before fetching or publishing", async () => {
  assert.throws(() => createMercuryTracker({ nodeId: "" }), /nodeId/);
  const h = harness();
  await assert.rejects(h.tracker.execute("create", { sourceUrl: "https://localhost/" }), /accepts only/);
  await assert.rejects(h.tracker.execute("create", { sourceUrl: "https://ismercuryinretrograde.com/" }), /accepts only/);
  assert.equal(h.record, undefined);
  assert.deepEqual(h.sent, []);
});

test("an explicit failed node result is not persisted as successful delivery", async () => {
  const h = harness({ invoke: async () => ({ ok: false, error: "Rejected by node policy" }) });
  await assert.rejects(h.tracker.execute("create"), /rejected the widget command/);
  assert.equal(h.record.snapshot, undefined);
  assert.match(h.record.error, /rejected/);
});

test("generated designs survive restarts and scheduled refresh, and styling never fabricates a new observation", async () => {
  const h = harness();
  await h.tracker.execute("create", { presentation: design("Original") });
  assert.equal(h.sent[0].params.presentation.card.body[0].text, "Original");
  const observedAt = h.record.snapshot.observedAt;
  const attemptedAt = h.record.attemptedAt;
  h.advance(30_000);
  const styled = await h.tracker.execute("style", { presentation: design("Redesigned") });
  assert.equal(styled.snapshot.observedAt, observedAt);
  assert.equal(h.record.attemptedAt, attemptedAt);
  assert.equal(h.record.snapshot.presentation.card.body[0].text, "Redesigned");
  h.advance(60 * 60_000);
  await createMercuryTracker(h.options).tick();
  assert.equal(h.sent.at(-1).params.presentation.card.body[0].text, "Redesigned");
  assert.notEqual(h.record.snapshot.observedAt, observedAt);
  await h.tracker.execute("create");
  assert.equal(h.record.presentation.card.body[0].text, "Redesigned");
});

test("styling failure keeps last good appearance and source errors; successful styling does not clear a source error", async () => {
  let fetchFail = false;
  let deliveryFail = false;
  const h = harness({
    fetchImpl: async () => fetchFail ? new Response("", { status: 503 }) : apiResponse(true),
    invoke: async () => { if (deliveryFail) throw new Error("Node offline"); return { saved: true }; },
  });
  await h.tracker.execute("create", { presentation: design("Original") });
  fetchFail = true;
  await assert.rejects(h.tracker.execute("refresh"), /HTTP 503/);
  deliveryFail = true;
  await assert.rejects(h.tracker.execute("style", { presentation: design("Redesigned") }), /Node offline/);
  assert.equal(h.record.presentation.card.body[0].text, "Original");
  assert.match(h.record.error, /HTTP 503/);
  assert.match(h.record.presentationError, /Node offline/);
  deliveryFail = false;
  await h.tracker.execute("style", { presentation: design("Redesigned") });
  assert.equal(h.record.presentation.card.body[0].text, "Redesigned");
  assert.equal(h.record.presentationError, null);
  assert.match(h.record.error, /HTTP 503/);
});

test("presentation validation fails before source reads or state mutation", async () => {
  const h = harness({ fetchImpl: async () => assert.fail("Invalid presentation must not fetch.") });
  await assert.rejects(h.tracker.execute("create", { presentation: { theme: "mystic" } }), /preset themes/);
  await assert.rejects(h.tracker.execute("status", { presentation: design("New") }), /only with create or style/);
  await assert.rejects(h.tracker.execute("style"), /requires presentation/);
  assert.equal(h.record, undefined);
  assert.equal(h.sent.length, 0);
});

test("native templates expose backgrounds, rich elements, data, host conditions, headers and local interactions", () => {
  const input = {
    card: {
      type: "AdaptiveCard", version: "1.6",
      header: { text: "My space weather", iconUrl: "https://example.com/icon.svg" },
      backgroundImage: { url: "${$root.content.artwork}", fillMode: "Cover" },
      body: [
        { type: "RichTextBlock", inlines: [{ type: "TextRun", text: "${title}", color: "Accent" }] },
        { type: "FactSet", facts: [{ title: "Status", value: "${value}" }] },
        { type: "Table", columns: [{ width: 1 }], rows: [] },
        { type: "TextBlock", text: "${content.label}", $when: "${$host.widgetSize != 'small' && $host.hostTheme == 'dark'}" },
        { type: "Container", $data: "${content.items}", items: [{ type: "TextBlock", text: "${name}" }] },
        { type: "Input.ChoiceSet", id: "units", choices: [{ title: "Celsius", value: "C" }] },
      ],
      actions: [
        { type: "Action.Execute", title: "Save", verb: "widget.set", $when: "${$host.isSettingsPayload}" },
        { type: "Action.OpenUrl", title: "Learn", url: "https://example.com/learn" },
      ],
    },
    data: { artwork: "https://cdn.example.net/background.webp?width=500", label: "${literal}", items: [{ name: "Mercury" }] },
    initialState: { count: 0, units: "C", enabled: true },
  };
  const detached = validatePresentation(input);
  assert.deepEqual(detached, input);
  input.card.body[0].inlines[0].text = "Changed by caller";
  assert.equal(detached.card.body[0].inlines[0].text, "${title}");
  for (const extra of [
    { type: "ActionSet", actions: [{ type: "Action.Execute", verb: "system.run" }] },
    { type: "ActionSet", actions: [{ type: "Action.ToggleVisibility", targetElements: ["openclaw-observation"] }] },
    { type: "TextBlock", text: "Hello", id: "${content.id}" },
    { type: "Image", url: "file:///C:/secret.png", altText: "Secret" },
    { type: "Image", url: "https://127.0.0.1/mercury.png", altText: "Loopback" },
    { type: "Image", url: "https://host.local./mercury.png", altText: "Local network" },
    { type: "Image", url: "https://localhost./mercury.png", altText: "Loopback with trailing dot" },
    { type: "Image", url: "https://server./mercury.png", altText: "Single label with trailing dot" },
    { type: "Image", url: "https://127.1./mercury.png", altText: "Numeric host with trailing dot" },
    { type: "Image", url: "https://secret@science.nasa.gov/mercury.png", altText: "Credentials" },
    { type: "Image", url: "${state.image}", altText: "Computed" },
    { type: "Image", url: "${content.artwork}", altText: "Context-relative" },
  ]) assert.throws(() => validatePresentation({ card: { ...design("Facts").card, body: [extra] } }));
  for (const extra of [
    { authentication: {} }, { metadata: { webUrl: "https://example.com" } },
    { $when: "${false}" }, { $data: "${content}" }, { actions: {} },
    { refresh: { action: { type: "Action.Execute", verb: "widget.increment" } } },
  ]) assert.throws(() => validatePresentation({ card: { ...design("Facts").card, ...extra } }));
  assert.throws(() => validatePresentation({ ...design("Facts"), initialState: { count: {} } }), /initialState/);
  assert.throws(() => validatePresentation({ ...design("Facts"), data: {} ,
    card: { ...design("Facts").card, backgroundImage: "${$root.content.missing}" } }), /binding/);
  const valid = [{ type: "TextBlock", text: "${title}" }];
  validatePresentation({ card: { ...design("Facts").card, body: Array(255).fill(valid[0]) } });
  assert.throws(() => validatePresentation({ card: { ...design("Facts").card, body: Array(256).fill(valid[0]) } }), /256 objects/);
  let nested = valid;
  for (let depth = 0; depth < 13; depth++) nested = [{ type: "Container", items: nested }];
  assert.throws(() => validatePresentation({ card: { ...design("Facts").card, body: nested } }), /depth/);
  assert.throws(() => validatePresentation(design("x".repeat(65536))), /65536 bytes/);
});

test("template data cannot substitute unvalidated elements, actions or header resources", () => {
  const action = { type: "Action.OpenUrl", url: "file:///C:/private.txt" };
  for (const change of [
    { body: [{ type: "Container", selectAction: "${$root.content.action}", items: [] }] },
    { body: ["${$root.content.element}"] },
    { body: [{ type: "Container", items: "${$root.content.elements}" }] },
    { body: [{ type: "TextBlock", text: "Fallback", fallback: "${$root.content.element}" }] },
    { body: [{ type: "ActionSet", actions: ["${$root.content.action}"] }] },
    { body: [{ type: "${$root.content.elementType}", text: "Dynamic" }] },
    { header: "${$root.content.header}" },
    { metadata: "${$root.content.metadata}" },
    { body: [{ type: "Media", sources: "${$root.content.sources}" }] },
    { body: [{ type: "Media", sources: [{ mimeType: "video/mp4", url: "https://example.com/video.mp4" }],
      captionSources: "${$root.content.captions}" }] },
  ]) {
    assert.throws(() => validatePresentation({
      card: { ...design("Facts").card, ...change },
      data: { action, element: { type: "Image", url: "file:///C:/private.png" },
        elements: [{ type: "Container", selectAction: action }], elementType: "Image",
        header: { text: "Header", iconUrl: "file:///C:/private.png" },
        metadata: { webUrl: "https://example.com/unapproved-html" },
        sources: [{ mimeType: "video/mp4", url: "file:///C:/private.mp4" }],
        captions: [{ mimeType: "vtt", url: "file:///C:/private.vtt", label: "English" }] },
    }), /literal/);
  }
});

test("literal caption arrays retain public URLs and safe scalar bindings", () => {
  for (const url of ["https://example.com./captions.vtt", "${$root.content.captionUrl}"]) {
    const input = {
      card: { type: "AdaptiveCard", version: "1.6", body: [{
        type: "Media", sources: [{ mimeType: "video/mp4", url: "https://example.com/video.mp4" }],
        captionSources: [{ mimeType: "vtt", url, label: "English" }],
      }] },
      data: { captionUrl: "https://example.com./captions.vtt" },
    };
    assert.deepEqual(validatePresentation(input), input);
  }
});

test("saved PoC presets migrate to native templates but are not accepted as new designs", async () => {
  for (const theme of ["native", "minimal", "cosmic", "mystic"]) {
    const migrated = migratePresentation({ theme });
    assert.equal(migrated.card.type, "AdaptiveCard");
    assert.equal(migrated.theme, undefined);
    if (theme === "cosmic" || theme === "mystic")
      assert.match(JSON.stringify(migrated), /mercury-messenger-globe/);
    assert.throws(() => validatePresentation({ theme }), /preset themes/);
  }
  const h = harness();
  await h.options.state.register("mercury", { enabled: true, presentation: { theme: "mystic" },
    snapshot: { observedAt: "2026-01-01T11:00:00Z", presentation: { theme: "mystic" } } });
  const restored = await h.tracker.execute("status");
  assert.equal(restored.presentation.card.type, "AdaptiveCard");
  assert.equal(restored.snapshot.observedAt, "2026-01-01T11:00:00Z");
  await h.tracker.execute("refresh");
  assert.equal(h.record.presentation.theme, undefined);
  assert.equal(h.sent.at(-1).params.presentation.card.type, "AdaptiveCard");
});

test("plugin registration wires the documented async store and service scheduler contracts", async () => {
  const hooks = registerHooks({
    resolve(specifier, context, next) {
      if (specifier === "openclaw/plugin-sdk/plugin-entry") {
        return {
          url: "data:text/javascript,export const definePluginEntry = value => value;",
          shortCircuit: true,
        };
      }
      return next(specifier, context);
    },
  });
  let plugin;
  try { plugin = (await import("./index.mjs")).default; }
  finally { hooks.deregister(); }
  const tools = [];
  const services = [];
  const config = {};
  let opened = 0;
  plugin.register({
    pluginConfig: config,
    runtime: {
      state: {
        openKeyedStore(options) {
          opened++;
          if (options.namespace === "widget-bridge") {
            assert.equal(options.maxEntries, 20);
            return { lookup: async () => undefined };
          }
          assert.deepEqual(options, { namespace: "trackers", maxEntries: 1 });
          return { lookup: async () => ({
            enabled: false,
            presentation: { ...design("Artwork fixture"), assets: { portrait: { base64: pixelPng } } },
          }) };
        },
      },
      nodes: { invoke: async () => assert.fail("A disabled tracker must not invoke a node.") },
    },
    registerTool(tool, options) { tools.push(tool); assert.equal(options.optional, true); },
    registerService(service) { services.push(service); },
  });
  assert.equal(opened, 0);
  assert.equal(tools.length, 1);
  assert.equal(tools[0].name, "windows_widgets");
  const capabilities = await tools[0].execute("discovery", { action: "capabilities" });
  assert.equal(opened, 0, "Authoring discovery must not open tracker state or require a configured node.");
  assert.equal(capabilities.details.nativeRenderingVerified, false);
  assert.ok(capabilities.details.toolbox.charts.types.includes("Chart.Line"));
  assert.ok(capabilities.details.toolbox.inputs.types.includes("Input.ChoiceSet"));
  assert.ok(capabilities.details.actions.rejected.includes("Action.Submit"));
  assert.deepEqual(JSON.parse(capabilities.content[0].text), capabilities.details);
  capabilities.details.toolbox.charts.types.length = 0;
  assert.ok((await tools[0].execute("discovery-again", { action: "capabilities" }))
    .details.toolbox.charts.types.includes("Chart.Line"), "Callers must not mutate later capability responses.");
  for (const extra of [{ presentation: design("Ignored") }, { artworkFiles: {} }, { sourceUrl: "https://mercuryretrogradeapi.com/" }])
    await assert.rejects(tools[0].execute("invalid-discovery", { action: "capabilities", ...extra }), /does not accept/);
  assert.equal(opened, 0);
  config.nodeId = "configured-node";
  assert.match(tools[0].description, /snapshot\.series/);
  assert.match(tools[0].description, /references\/microsoft-widget-guidance\.md/);
  assert.deepEqual(tools[0].parameters.properties.presentation.required, ["card"]);
  assert.equal(tools[0].parameters.properties.presentation.properties.theme, undefined);
  assert.equal(tools[0].parameters.properties.presentation.properties.card.additionalProperties, true);
  assert.equal(tools[0].parameters.properties.artworkFiles.additionalProperties.type, "string");
  assert.deepEqual(tools[0].parameters.properties.sourceUrl.enum, ["https://mercuryretrogradeapi.com/"]);
  const status = await tools[0].execute("call", { action: "status" });
  assert.equal(status.details.nodeId, "configured-node");
  assert.deepEqual(status.details.artwork, { portrait: { bytes: 68 } });
  assert.equal(status.details.presentation.assets, undefined);
  assert.equal(JSON.stringify(status).includes(pixelPng), false);
  await assert.rejects(tools[0].execute("call", { action: "status", artworkFiles: {} }), /only with create or style/);
  assert.equal(services.length, 1);
  assert.equal(services[0].apiVersion, 2);
  let job;
  services[0].start({ scheduler: {
    signal: new AbortController().signal,
    schedule(value) { job = value; },
  } });
  assert.equal(job.everyMs, 60_000);
  await job.run();
  await services[0].stop();
  assert.equal(opened, 2);
});
