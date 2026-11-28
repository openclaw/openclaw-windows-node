// Owns multiple source schedules, authored cards and native deployment independently of source adapters.
// Usage: createWidgetBridge(host).execute("create", { id, title, source, presentation }) opts into collection.
import { collectSource, normalizeObservationTime, projectSeries, validateSource, WidgetSourceError } from "./sources.mjs";
import { seriesArtwork } from "./series-artwork.mjs";
import { validatePresentation, validatePublicUrl } from "./presentation.mjs";
import { loadArtworkFiles } from "./artwork.mjs";
import { TRACKER_ID } from "./mercury.mjs";

const INDEX_KEY = "__index";
const MAX_TRACKERS = 19; // One native slot remains available for the existing Mercury tracker.
const CONTROL_KEYS = new Set(["id", "signal", "title", "source", "presentation", "artworkFiles",
  "refreshMinutes", "staleAfterMinutes", "snapshot"]);
const object = value => value !== null && typeof value === "object" && !Array.isArray(value);
const noControls = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u;

function id(value) {
  if (typeof value !== "string" || !/^[a-z0-9][a-z0-9-]{0,63}$/.test(value) ||
    /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/.test(value) || value === TRACKER_ID)
    throw new Error("Use a unique widget id of 1-64 lowercase letters, digits or hyphens; Mercury's id is reserved.");
  return value;
}

function text(value, maximum, field, allowEmpty = false) {
  if (typeof value !== "string" || value.length > maximum || noControls.test(value) ||
    (!allowEmpty && !value.trim())) throw new Error(`Invalid widget ${field}.`);
  return value;
}

function integer(value, minimum, maximum, field) {
  if (!Number.isInteger(value) || value < minimum || value > maximum)
    throw new Error(`${field} must be an integer between ${minimum} and ${maximum}.`);
  return value;
}

function validateSnapshot(snapshot, now) {
  id(snapshot.id);
  text(snapshot.title, 100, "title");
  text(snapshot.value, 120, "value");
  text(snapshot.detail, 600, "detail", true);
  validatePublicUrl(snapshot.sourceUrl);
  normalizeObservationTime(snapshot.observedAt, now);
  integer(snapshot.staleAfterMinutes, 5, 10080, "staleAfterMinutes");
  if (snapshot.presentation) validatePresentation(snapshot.presentation);
  if (Buffer.byteLength(JSON.stringify(snapshot)) > 524288)
    throw new Error("Widget snapshot exceeds 524288 bytes.");
}

function sourcePresentation(authored, result) {
  const hasSeries = result.content.graphUrl !== undefined;
  const presentation = structuredClone(authored ?? {
    card: { type: "AdaptiveCard", version: "1.6", body: [
      { type: "TextBlock", text: "${title}", weight: "Bolder", wrap: true, $when: "${$host.widgetSize != 'small'}" },
      { type: "TextBlock", text: "${value}", size: "ExtraLarge", wrap: true },
      ...(hasSeries ? [
        { type: "Image", url: "${$root.content.source.graphUrl}", altText: "${content.source.graphAlt}",
          size: "Stretch", $when: "${$host.widgetSize != 'small'}" },
        { type: "TextBlock", text: "${content.source.rangeLabel}", size: "Small", wrap: true,
          $when: "${$host.widgetSize != 'small'}" },
      ] : []),
    ] },
  });
  presentation.data = { ...presentation.data, source: result.content };
  if (hasSeries)
    presentation.assets = { ...presentation.assets, "source-series": seriesArtwork(result.content) };
  return validatePresentation(presentation);
}

function metadata(record) {
  return { id: record.id, enabled: record.enabled, mode: record.mode,
    refreshMinutes: record.refreshMinutes, observedAt: record.snapshot?.observedAt,
    attemptedAt: record.attemptedAt, error: record.error ?? null,
    lastDelivery: record.lastDelivery ?? null,
    requiresUserPin: true, visibleRenderingVerified: false };
}

function deliveryMetadata(result, reportedAt) {
  const payload = result?.payload ?? result;
  const host = payload?.host;
  return {
    reportedAt,
    saved: typeof payload?.saved === "boolean" ? payload.saved : null,
    host: typeof host?.supported === "boolean" && Number.isSafeInteger(host.pinnedCount) && host.pinnedCount >= 0
      ? { supported: host.supported, pinnedCount: host.pinnedCount,
        reason: typeof host.reason === "string" ? host.reason.slice(0, 512) : null }
      : null,
  };
}

export function createWidgetBridge({ state, invoke, nodeId, allowedSourceUrls = [], now = Date.now, fetchJson }) {
  if (typeof nodeId !== "string" || !nodeId.trim()) throw new Error("Configure an exact Windows nodeId.");
  let queue = Promise.resolve();
  const serialize = operation => {
    const result = queue.then(operation);
    queue = result.then(() => undefined, () => undefined);
    return result;
  };
  const read = key => state.lookup(key);
  const save = record => state.register(record.id, record);
  async function send(command, params, signal) {
    try {
      const result = await invoke({ nodeId, command, params, timeoutMs: 30000, signal });
      if (result?.ok === false) throw new Error("Rejected");
      return result;
    } catch { throw new Error("Windows widget command failed. Check node connectivity and widget consent."); }
  }
  async function list() {
    const index = await read(INDEX_KEY) ?? { ids: [] };
    const records = [];
    for (const key of index.ids) {
      const record = await read(key);
      if (record) records.push(record);
    }
    return records;
  }
  async function reserve(key) {
    const index = await read(INDEX_KEY) ?? { ids: [] };
    if (index.ids.includes(key)) return;
    if (index.ids.length >= MAX_TRACKERS)
      throw new Error("The demo supports 19 reusable widget slots plus Mercury. Reuse an existing stopped id.");
    await state.register(INDEX_KEY, { ids: [...index.ids, key] });
  }
  async function authoredPresentation(record, presentation, artworkFiles) {
    if (presentation === undefined && artworkFiles === undefined) return record?.presentation;
    const retained = presentation ?? record?.presentation;
    if (!retained) throw new Error("artworkFiles requires an authored presentation.");
    if ((record?.mode !== "manual" || record.series) && (Object.hasOwn(retained.data ?? {}, "source") || Object.hasOwn(retained.assets ?? {}, "source-series") ||
      Object.hasOwn(artworkFiles ?? {}, "source-series"))
    )
      throw new Error("content.source and source-series artwork are owned by the source adapter.");
    return validatePresentation(await loadArtworkFiles({
      ...retained, assets: retained.assets ?? record?.presentation?.assets,
    }, artworkFiles));
  }
  async function refresh(record, signal) {
    const attemptedAt = new Date(now()).toISOString();
    try {
      const result = await collectSource(record.source, { allowedSourceUrls, signal, now: now(), fetchJson });
      signal?.throwIfAborted();
      if (record.source.kind === "json" && record.source.observedAtPointer === undefined) {
        const previous = Date.parse(record.snapshot?.observedAt ?? "");
        result.observedAt = new Date(Math.max(now(), Number.isFinite(previous) ? previous + 1 : 0)).toISOString();
      }
      const snapshot = { id: record.id, title: record.title, value: result.value, detail: result.detail,
        sourceUrl: record.source.url, observedAt: result.observedAt, staleAfterMinutes: record.staleAfterMinutes,
        presentation: sourcePresentation(record.presentation, result) };
      validateSnapshot(snapshot, now());
      const delivery = await send("widgets.upsert", snapshot, signal);
      const saved = { ...record, enabled: true, snapshot, attemptedAt, error: null,
        lastDelivery: deliveryMetadata(delivery, new Date(now()).toISOString()) };
      await save(saved);
      return metadata(saved);
    } catch (error) {
      await save({ ...record, attemptedAt, error: error instanceof WidgetSourceError ? error.code : "refresh-or-publication-failed" });
      throw error;
    }
  }
  return {
    execute(action, params = {}) {
      return serialize(async () => {
        if (Object.keys(params).some(key => !CONTROL_KEYS.has(key))) throw new Error("Unsupported widget bridge parameter.");
        if (action === "list") {
          if (Object.keys(params).some(key => key !== "signal")) throw new Error("list takes no widget arguments.");
          return { widgets: (await list()).map(metadata), reusableSlots: MAX_TRACKERS };
        }
        const key = id(params.id);
        const record = await read(key);
        const { signal } = params;
        const permitted = {
          create: ["title", "source", "presentation", "artworkFiles", "refreshMinutes", "staleAfterMinutes"],
          publish: ["snapshot", "artworkFiles"], style: ["presentation", "artworkFiles"],
          refresh: [], stop: [], status: [],
        }[action];
        if (!permitted || Object.keys(params).some(name => !["id", "signal", ...permitted].includes(name)))
          throw new Error("Parameters are not supported for this widget action.");
        if (action === "status") {
          if (!record) throw new Error("Unknown widget id.");
          return metadata(record);
        }
        if (action === "stop") {
          if (!record) throw new Error("Unknown widget id.");
          await save({ id: key, enabled: false, error: "delete-pending" });
          await send("widgets.delete", { id: key }, signal);
          await save({ id: key, enabled: false, error: null });
          return { id: key, stopped: true };
        }
        if (action === "publish") {
          if (!object(params.snapshot) || Object.keys(params.snapshot).some(name =>
            !["title", "value", "detail", "sourceUrl", "observedAt", "staleAfterMinutes", "presentation", "series"].includes(name)))
            throw new Error("publish requires an explicit data-only snapshot.");
          if (record?.enabled && record.mode !== "manual") throw new Error("Stop the polling widget before switching it to manual publication.");
          const { series: inputSeries, ...input } = params.snapshot;
          const observation = inputSeries === undefined ? undefined : projectSeries(inputSeries, now());
          if (observation && ((input.value !== undefined && input.value !== observation.value) ||
            (input.observedAt !== undefined && normalizeObservationTime(input.observedAt, now()) !== observation.observedAt)))
            throw new Error("Supplied value and observedAt must match the latest series sample, or be omitted.");
          const authored = await authoredPresentation({ mode: "manual", series: observation?.content },
            input.presentation, params.artworkFiles);
          const snapshot = { ...input, ...(observation
            ? { value: observation.value, observedAt: observation.observedAt } : {}), id: key, detail: input.detail ?? "",
            staleAfterMinutes: params.snapshot.staleAfterMinutes ?? 120 };
          if (observation) snapshot.presentation = sourcePresentation(authored, observation);
          else if (authored) snapshot.presentation = authored;
          validateSnapshot(snapshot, now());
          signal?.throwIfAborted();
          await reserve(key);
          const delivery = await send("widgets.upsert", snapshot, signal);
          const saved = { id: key, mode: "manual", enabled: true, snapshot,
            presentation: authored, series: observation?.content, error: null,
            lastDelivery: deliveryMetadata(delivery, new Date(now()).toISOString()) };
          await save(saved);
          return metadata(saved);
        }
        if (action === "create") {
          if (record?.enabled) throw new Error("Widget already exists. Style it, or stop it before reconfiguring its source.");
          const source = validateSource(params.source, allowedSourceUrls);
          const title = text(params.title, 100, "title");
          const refreshMinutes = integer(params.refreshMinutes ?? 5, 1, 1440, "refreshMinutes");
          const staleAfterMinutes = integer(params.staleAfterMinutes ?? Math.max(5, refreshMinutes * 2),
            5, 10080, "staleAfterMinutes");
          const presentation = await authoredPresentation(undefined, params.presentation, params.artworkFiles);
          await reserve(key);
          const created = { id: key, mode: "polling", enabled: true, title, source, refreshMinutes,
            staleAfterMinutes, presentation };
          await save(created);
          return refresh(created, signal);
        }
        if (!record?.enabled) throw new Error("Create or publish this widget before styling or refreshing it.");
        if (action === "refresh") {
          if (record.mode !== "polling") throw new Error("Manual widgets require a new publish, not source refresh.");
          return refresh(record, signal);
        }
        if (!record.snapshot || (params.presentation === undefined && params.artworkFiles === undefined))
          throw new Error("style requires a successful observation and a new presentation or artworkFiles.");
        const presentation = await authoredPresentation(record, params.presentation, params.artworkFiles);
        if (!presentation) throw new Error("style requires a complete presentation.");
        const snapshot = { ...record.snapshot, presentation: record.mode === "polling" || record.series
          ? sourcePresentation(presentation, { content: record.series ?? record.snapshot.presentation.data.source })
          : presentation };
        validateSnapshot(snapshot, now());
        const delivery = await send("widgets.upsert", snapshot, signal);
        const saved = { ...record, snapshot, presentation,
          lastDelivery: deliveryMetadata(delivery, new Date(now()).toISOString()) };
        await save(saved);
        return metadata(saved);
      });
    },
    tick(signal) {
      return serialize(async () => {
        const failures = [];
        for (const record of await list()) {
          if (!record.enabled || record.mode !== "polling") continue;
          if (Number.isFinite(Date.parse(record.attemptedAt)) &&
            now() - Date.parse(record.attemptedAt) < record.refreshMinutes * 60000) continue;
          try { await refresh(record, signal); }
          catch (error) { failures.push(error); }
          if (signal?.aborted) break;
        }
        if (failures.length) throw new AggregateError(failures, "One or more widget refreshes failed. Inspect status.");
      });
    },
    drain() { return queue; },
  };
}
