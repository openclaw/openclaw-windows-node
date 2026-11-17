import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { Readable } from "node:stream";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { inflateSync } from "node:zlib";
import { createWidgetBridge } from "./bridge.mjs";
import { fetchSourceJson, isPublicAddress, projectSource, validateSource } from "./sources.mjs";
import { seriesArtwork } from "./series-artwork.mjs";
import { validatePresentation } from "./presentation.mjs";

const url = "https://example.com/observations.json";
const at = Date.parse("2026-01-01T12:00:00Z");
const source = { kind: "json", url, valuePointer: "/value", observedAtPointer: "/at" };
const presentation = { card: { type: "AdaptiveCard", version: "1.6", body: [
  { type: "TextBlock", text: "${value}", size: "ExtraLarge" },
] } };
function harness() {
  const records = new Map(), sent = [];
  let time = at, failure = false, calls = 0;
  const options = {
    state: { lookup: async key => structuredClone(records.get(key)),
      register: async (key, value) => records.set(key, structuredClone(value)) },
    invoke: async request => { sent.push(request); return { ok: true }; },
    nodeId: "chosen-node", allowedSourceUrls: [url], now: () => time,
    fetchJson: async () => {
      calls++;
      if (failure) throw new Error("Do not expose a source body or credential-bearing URL.");
      return { value: "Observed", at: new Date(time).toISOString() };
    },
  };
  return { records, sent, options, bridge: createWidgetBridge(options),
    advance(minutes) { time += minutes * 60_000; }, fail() { failure = true; },
    get calls() { return calls; } };
}

test("independent tracker schedules survive restart and style preserves actual observation time", async () => {
  const h = harness();
  await h.bridge.execute("create", { id: "one", title: "First", source, refreshMinutes: 1 });
  await h.bridge.execute("create", { id: "two", title: "Second", source, refreshMinutes: 5 });
  assert.equal(h.sent[0].nodeId, "chosen-node");
  assert.deepEqual(h.sent[0].params.presentation.data.source, {});
  const observed = h.sent[0].params.observedAt;
  await h.bridge.execute("style", { id: "one", presentation });
  assert.equal(h.calls, 2);
  assert.equal(h.sent.at(-1).params.observedAt, observed);
  h.advance(2);
  await createWidgetBridge(h.options).tick();
  assert.equal(h.calls, 3);
  assert.equal(h.sent.at(-1).params.id, "one");
  assert.equal(h.records.get("two").snapshot.observedAt, observed);
  const status = await h.bridge.execute("list");
  assert.equal(status.widgets.length, 2);
  assert.equal(JSON.stringify(status).includes("Observed"), false);
  assert.equal(JSON.stringify(status).includes(url), false);
});

test("collection failure retains last-good snapshot, redacts errors, and stop prevents scheduled republication", async () => {
  const h = harness();
  await h.bridge.execute("create", { id: "one", title: "First", source });
  const previous = structuredClone(h.records.get("one").snapshot);
  h.fail();
  await assert.rejects(h.bridge.execute("refresh", { id: "one" }));
  assert.deepEqual(h.records.get("one").snapshot, previous);
  assert.equal(h.records.get("one").error, "refresh-or-publication-failed");
  await h.bridge.execute("style", { id: "one", presentation });
  assert.equal(h.records.get("one").error, "refresh-or-publication-failed");
  await h.bridge.execute("stop", { id: "one" });
  h.advance(100);
  await h.bridge.tick();
  assert.equal(h.sent.at(-1).command, "widgets.delete");
  assert.deepEqual(h.records.get("one"), { id: "one", enabled: false, error: null });
});

test("initial failed collectors can be retried explicitly and independent failures do not starve other widgets", async () => {
  const h = harness();
  let first = true;
  h.options.fetchJson = async () => {
    if (first) { first = false; throw new Error("Unavailable"); }
    return { value: "Recovered", at: new Date(at).toISOString() };
  };
  const bridge = createWidgetBridge(h.options);
  await assert.rejects(bridge.execute("create", { id: "one", title: "First", source }));
  await bridge.execute("refresh", { id: "one" });
  assert.equal(h.records.get("one").snapshot.value, "Recovered");
  await bridge.execute("create", { id: "two", title: "Second", source, refreshMinutes: 1 });
  h.advance(10);
  h.options.fetchJson = async () => ({ value: "unused" });
  // A new bridge picks up revoked administrator consent independently of durable tracker configuration.
  const revoked = createWidgetBridge({ ...h.options, allowedSourceUrls: [] });
  await assert.rejects(revoked.tick(), /One or more/);
  assert.ok(h.records.get("one").error);
  assert.ok(h.records.get("two").error);
});

test("manual deployment never fetches or starts a schedule and rejects overlapping source ownership", async () => {
  const h = harness();
  const snapshot = { title: "Collected elsewhere", value: "Ready", sourceUrl: url,
    observedAt: new Date(at).toISOString(), presentation };
  const result = await h.bridge.execute("publish", { id: "manual", snapshot });
  await h.bridge.tick();
  assert.equal(h.calls, 0);
  assert.equal(result.mode, "manual");
  assert.equal(result.visibleRenderingVerified, false);
  assert.equal(Object.hasOwn(result, "snapshot"), false);
  await h.bridge.execute("create", { id: "polling", title: "Polling", source });
  await assert.rejects(h.bridge.execute("publish", { id: "polling", snapshot }), /Stop/);
  await assert.rejects(h.bridge.execute("refresh", { id: "manual" }), /Manual/);
  await assert.rejects(h.bridge.execute("publish", { id: "bad", snapshot: { ...snapshot, value: "" } }), /value/);
});

test("pushed history derives current facts and graph, survives restyling and accepts later bot updates without collection", async () => {
  const h = harness();
  const snapshot = { title: "Synthetic history", sourceUrl: url, series: {
    units: "mg/dL", points: [
      { x: "2026-01-01T12:00:00Z", y: 108 },
      { x: "2026-01-01T11:55:00Z", y: 90 },
    ],
  } };
  const result = await h.bridge.execute("publish", { id: "history", snapshot });
  const published = h.sent.at(-1).params;
  assert.equal(published.value, "108 mg/dL");
  assert.equal(published.observedAt, "2026-01-01T12:00:00.000Z");
  assert.deepEqual(published.presentation.data.source.points, [
    { x: "2026-01-01T11:55:00.000Z", y: 90 },
    { x: "2026-01-01T12:00:00.000Z", y: 108 },
  ]);
  assert.equal(Object.hasOwn(published, "series"), false, "Only the native snapshot contract crosses the node boundary.");
  assert.ok(published.presentation.assets["source-series"]);
  validatePresentation(published.presentation);
  assert.equal(result.mode, "manual");
  assert.equal(JSON.stringify(result).includes("108"), false);
  h.advance(5);
  const restarted = createWidgetBridge(h.options);
  await restarted.tick();
  await restarted.execute("style", { id: "history", presentation });
  assert.equal(h.sent.at(-1).params.observedAt, published.observedAt);
  assert.deepEqual(h.sent.at(-1).params.presentation.assets, published.presentation.assets);
  await assert.rejects(restarted.execute("style", { id: "history",
    presentation: { ...presentation, data: { source: { latest: 999 } } } }), /owned/);
  await restarted.execute("publish", { id: "history", snapshot: {
    ...snapshot, series: { units: "mg/dL", points: [{ x: "2026-01-01T12:05:00Z", y: 109 }] },
  } });
  assert.equal(h.sent.at(-1).params.value, "109 mg/dL");
  assert.equal(h.sent.at(-1).params.observedAt, "2026-01-01T12:05:00.000Z");
  assert.equal(h.calls, 0);
});

test("invalid pushed history fails before storage or delivery instead of overriding supplied facts", async () => {
  const h = harness();
  const series = { units: "C", points: [{ x: "2026-01-01T12:00:00Z", y: -2 }] };
  for (const change of [
    { series: { ...series, points: [] } },
    { series: { ...series, points: [{ x: "2026-02-30T12:00:00Z", y: 1 }] } },
    { series: { ...series, points: [{ x: "2026-01-01T12:06:00Z", y: 1 }] } },
    { series: { ...series, points: [{ x: "2026-01-01T12:00:00Z", y: "2" }] } },
    { series: { ...series, points: [...series.points, { ...series.points[0], y: 3 }] } },
    { series: { ...series, points: Array.from({ length: 97 }, () => series.points[0]) } },
    { series: { ...series, gapMinutes: 0 } },
    { series: { ...series, units: "" } },
    { series: { ...series, windowMinutes: null } },
    { value: "Incorrect" },
    { observedAt: "2026-01-01T11:59:00Z" },
    { presentation: { ...presentation, data: { source: { latest: 999 } } } },
  ]) {
    await assert.rejects(h.bridge.execute("publish", { id: "history",
      snapshot: { title: "Synthetic", sourceUrl: url, series, ...change } }));
  }
  assert.equal(h.sent.length, 0);
  assert.equal(h.records.size, 0);
  assert.equal(h.calls, 0);
  await h.bridge.execute("publish", { id: "temperature",
    snapshot: { title: "Temperature", sourceUrl: url, series } });
  assert.equal(h.sent.at(-1).params.value, "-2 C", "Generic history must not impose glucose-only positive values.");
});

test("manual publishing transfers selected PNG files with a lighting panel but never invokes hardware", async () => {
  const h = harness();
  const directory = await mkdtemp(join(tmpdir(), "openclaw-widget-push-"));
  const png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0l8AAAAASUVORK5CYII=";
  try {
    const path = join(directory, "selected.png");
    await writeFile(path, Buffer.from(png, "base64"));
    const panel = JSON.parse(await readFile(new URL("./examples/lighting-controls.json", import.meta.url), "utf8"));
    await h.bridge.execute("publish", { id: "lighting",
      snapshot: { title: "Lighting", value: "Not enabled", sourceUrl: url,
        observedAt: "2026-01-01T12:00:00Z", presentation: panel },
      artworkFiles: { selected: path } });
    assert.equal(h.sent.length, 1);
    assert.equal(h.sent[0].command, "widgets.upsert");
    assert.equal(h.sent[0].params.presentation.assets.selected.base64, png);
    await h.bridge.tick();
    assert.equal(h.calls, 0);
    assert.equal(h.sent.length, 1);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("URL consent, pointer boundaries and literal projections fail before any unapproved fetch", async () => {
  const h = harness();
  for (const bad of [{ ...source, url: "https://unapproved.example/data" },
    { ...source, valuePointer: "/__proto__/value" }, { ...source, valuePointer: "/bad~2escape" }])
    await assert.rejects(h.bridge.execute("create", { id: "bad", title: "Bad", source: bad }));
  assert.equal(h.calls, 0);
  assert.equal(h.records.size, 0);
  const configured = validateSource({ kind: "json", url, valuePointer: "/active",
    observedAtPointer: "/time", valueMap: { true: "On", false: "Off" }, fields: { count: "/a~1b/~0name" } }, [url]);
  const projected = projectSource(configured, { active: false, time: "2026-01-01T10:00:00Z", "a/b": { "~name": 4 } }, at);
  assert.equal(projected.value, "Off");
  assert.equal(projected.observedAt, "2026-01-01T10:00:00.000Z");
  assert.deepEqual(projected.content, { count: 4 });
  assert.throws(() => projectSource(configured, { active: "injected", time: "2026-01-01T10:00:00Z" }, at), /unmapped/);
  assert.throws(() => projectSource({ ...configured, valueMap: undefined }, { active: {} }, at), /scalar/);
  for (const time of ["January 1, 2026", "2026-02-30T12:00:00Z", "2026-01-01T24:00:00Z", "2026-01-01"]) {
    assert.throws(() => projectSource(configured, { active: false, time, "a/b": { "~name": 4 } }, at), /observation/);
  }
  assert.equal(projectSource(configured, { active: false, time: "2026-01-01T11:00:00+01:00",
    "a/b": { "~name": 4 } }, at).observedAt, "2026-01-01T10:00:00.000Z");
});

test("collection-clock observations stay monotonic without rewriting explicit source timestamps", async () => {
  const h = harness();
  await h.bridge.execute("create", { id: "clock", title: "Clock", source: { ...source, observedAtPointer: undefined } });
  const first = h.records.get("clock").snapshot.observedAt;
  await h.bridge.execute("refresh", { id: "clock" });
  assert.equal(Date.parse(h.records.get("clock").snapshot.observedAt), Date.parse(first) + 1);
  await h.bridge.execute("create", { id: "sample", title: "Actual sample", source });
  await h.bridge.execute("refresh", { id: "sample" });
  assert.equal(h.records.get("sample").snapshot.observedAt, first);
});

test("Nightscout uses timestamp ordering, declared units and source time, rejecting conflicting or invalid samples", () => {
  const config = validateSource({ kind: "nightscout", url, units: "mmol/L" }, [url]);
  const samples = [
    { type: "sgv", date: at, sgv: 108 }, { type: "sgv", date: at - 300000, sgv: 90 },
    { type: "mbg", date: at + 1000, mbg: 123 },
  ];
  const result = projectSource(config, samples, at + 3_600_000);
  assert.equal(result.value, "6 mmol/L");
  assert.equal(result.observedAt, "2026-01-01T12:00:00.000Z");
  assert.deepEqual(result.content.points.map(point => point.y), [5, 6]);
  assert.deepEqual(result.content.points.map(point => point.x),
    ["2026-01-01T11:55:00.000Z", "2026-01-01T12:00:00.000Z"]);
  for (const bad of [
    [...samples, { type: "sgv", date: at, sgv: 109 }],
    [{ type: "sgv", date: at, sgv: -1 }],
    [{ type: "sgv", date: at + 300001, sgv: 108 }],
    [{ type: "sgv", date: at, sgv: "108" }], [],
  ]) assert.throws(() => projectSource(config, bad, at));
  assert.throws(() => validateSource({ ...config, units: undefined }, [url]), /units/);
});

function decodePng(asset) {
  const png = Buffer.from(asset.base64, "base64");
  assert.equal(png.readUInt32BE(16), 600);
  assert.equal(png.readUInt32BE(20), 200);
  let offset = 8, compressed;
  while (offset < png.length) {
    const length = png.readUInt32BE(offset);
    if (png.toString("ascii", offset + 4, offset + 8) === "IDAT")
      compressed = png.subarray(offset + 8, offset + 8 + length);
    offset += length + 12;
  }
  return inflateSync(compressed);
}

test("PNG plots real time spacing without bridging gaps and admits ordinary native image/text fallbacks", () => {
  const content = {
    windowStart: "2026-01-01T11:00:00Z", windowEnd: "2026-01-01T12:00:00Z", gapMinutes: 15,
    points: [{ x: "2026-01-01T11:00:00Z", y: 1 }, { x: "2026-01-01T12:00:00Z", y: 3 }],
  };
  const pixels = decodePng(seriesArtwork(content));
  assert.equal(pixels[60 * 601 + 1 + 300], 255, "A one-hour gap must not contain a connecting line.");
  assert.equal(pixels[187 * 601 + 1 + 12], 32);
  assert.equal(pixels[12 * 601 + 1 + 587], 32);
  const uneven = decodePng(seriesArtwork({ ...content, points: [
    { x: "2026-01-01T11:00:00Z", y: 2 }, { x: "2026-01-01T11:10:00Z", y: 2 },
    { x: "2026-01-01T12:00:00Z", y: 2 },
  ] }));
  assert.equal(uneven[100 * 601 + 1 + 108], 32, "The ten-minute sample belongs one sixth across the time axis.");
  assert.equal(uneven[100 * 601 + 1 + 300], 208, "Only the grid, not a sample or connecting line, belongs at the midpoint.");
});

test("Nightscout deployment transfers a bounded graph without returning readings or image bytes to the model", async () => {
  const h = harness();
  const bridge = createWidgetBridge({ ...h.options, fetchJson: async () => [
    { type: "sgv", date: at, sgv: 108 }, { type: "sgv", date: at - 300000, sgv: 90 },
  ] });
  const result = await bridge.execute("create", { id: "sensor", title: "Sensor",
    source: { kind: "nightscout", url, units: "mg/dL" } });
  const published = h.sent.at(-1).params;
  validatePresentation(published.presentation);
  assert.match(published.presentation.card.body.find(item => item.type === "Image").altText, /graphAlt/);
  assert.equal(published.value, "108 mg/dL");
  assert.equal(published.presentation.data.source.points.length, 2);
  assert.ok(Buffer.from(published.presentation.assets["source-series"].base64, "base64").length < 262144);
  assert.equal(JSON.stringify(result).includes("108"), false);
  assert.equal(Object.hasOwn(result, "snapshot"), false);
  await bridge.execute("style", { id: "sensor", presentation });
  assert.equal(h.sent.at(-1).params.observedAt, published.observedAt);
  assert.equal(h.sent.at(-1).params.presentation.data.source.points.length, 2);
});

test("source fetch rejects private DNS and mixed answers and pins HTTPS to the checked public address", async () => {
  for (const address of ["127.0.0.1", "10.1.2.3", "100.64.1.2", "169.254.169.254", "192.168.1.1",
    "::1", "::ffff:127.0.0.1", "fe80::1", "fc00::1", "2001:db8::1"])
    assert.equal(isPublicAddress(address), false, address);
  assert.equal(isPublicAddress("8.8.8.8"), true);
  assert.equal(isPublicAddress("2606:4700:4700::1111"), true);
  await assert.rejects(fetchSourceJson(url, {
    resolve: async () => [{ address: "8.8.8.8", family: 4 }, { address: "127.0.0.1", family: 4 }],
    requestImpl: () => assert.fail("Private DNS answers must not be connected."),
  }), /nonpublic/);
  const result = await fetchSourceJson(url, {
    resolve: async () => [{ address: "8.8.8.8", family: 4 }],
    requestImpl: (_url, options, callback) => {
      assert.equal(options.agent, false);
      options.lookup("rebound.example", { all: true }, (_error, addresses) =>
        assert.deepEqual(addresses, [{ address: "8.8.8.8", family: 4 }]));
      const request = new EventEmitter();
      request.end = () => {
        const response = Readable.from([Buffer.from('{"value":7}')]);
        response.statusCode = 200;
        response.headers = { "content-type": "application/json; charset=utf-8" };
        callback(response);
      };
      return request;
    },
  });
  assert.deepEqual(result, { value: 7 });
});

test("source transport rejects redirects, oversized streams and invalid JSON without exposing the response", async () => {
  for (const [status, body, expected] of [[302, "redirect", /http-status/],
    [200, "x".repeat(262145), /too-large/], [200, "secret body", /invalid-json/]]) {
    await assert.rejects(fetchSourceJson(url, {
      resolve: async () => [{ address: "8.8.8.8", family: 4 }],
      requestImpl: (_url, _options, callback) => {
        const request = new EventEmitter();
        request.end = () => {
          const response = Readable.from([Buffer.from(body)]);
          response.statusCode = status;
          response.headers = { "content-type": "application/json" };
          callback(response);
        };
        return request;
      },
    }), expected);
  }
});

test("source DNS cancellation and aborted response streams report bounded redacted failures", async () => {
  const cancellation = new AbortController();
  const pending = fetchSourceJson(url, { signal: cancellation.signal,
    resolve: () => new Promise(() => {}), requestImpl: () => assert.fail("Cancelled lookup must not connect.") });
  cancellation.abort();
  await assert.rejects(pending, /timeout-or-cancelled/);
  await assert.rejects(fetchSourceJson(url, {
    resolve: async () => [{ address: "8.8.8.8", family: 4 }],
    requestImpl: (_url, _options, callback) => {
      const request = new EventEmitter();
      request.end = () => {
        const response = new Readable({ read() {} });
        response.statusCode = 200;
        response.headers = { "content-type": "application/json" };
        callback(response);
        response.emit("aborted");
        response.destroy();
      };
      return request;
    },
  }), /transport/);
});

test("lighting cards admit only fixed literal click operations, never bound inputs or generated commands", async () => {
  validatePresentation(JSON.parse(await readFile(new URL("./examples/lighting-controls.json", import.meta.url), "utf8")));
  const action = { type: "Action.Execute", title: "Blue", verb: "widget.lighting",
    associatedInputs: "none", data: { operation: "color", color: "#1234ab" } };
  validatePresentation({ ...presentation, card: { ...presentation.card, actions: [action] } });
  for (const change of [
    { associatedInputs: "auto" }, { data: { operation: "color", color: "${content.color}" } },
    { data: { operation: "command", command: "DIAGNOSE" } },
    { data: { operation: "preset", preset: "script", color: "#123456" } },
    { data: { operation: "status", device: "all" } },
  ]) assert.throws(() => validatePresentation({ ...presentation,
    card: { ...presentation.card, actions: [{ ...action, ...change }] } }), /Lighting/);
});
