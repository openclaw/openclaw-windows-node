export const SOURCE_URL = "https://mercuryretrogradeapi.com/";
export const TRACKER_ID = "mercury-retrograde";
export const FETCH_TIMEOUT_MS = 15_000;
export const MAX_SOURCE_BYTES = 256 * 1024;
const STATE_KEY = "mercury";

export function parseMercuryResponse(json) {
  const result = JSON.parse(json);
  if (result === null || typeof result !== "object" || Array.isArray(result)
    || typeof result.is_retrograde !== "boolean")
    throw new Error("Mercury API response must contain a boolean is_retrograde.");
  return result.is_retrograde;
}

export async function fetchMercury({ fetchImpl = fetch, signal } = {}) {
  const deadline = AbortSignal.timeout(FETCH_TIMEOUT_MS);
  const response = await fetchImpl(SOURCE_URL, {
    redirect: "error",
    signal: signal ? AbortSignal.any([signal, deadline]) : deadline,
    headers: { Accept: "application/json", "User-Agent": "OpenClaw-Windows-Widgets-PoC/0.1" },
  });
  if (!response.ok) throw new Error(`Mercury source returned HTTP ${response.status}.`);
  if (response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json")
    throw new Error("Mercury source did not return JSON.");
  if (!response.body) throw new Error("Mercury source returned an empty body.");
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let json = "";
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_SOURCE_BYTES) throw new Error("Mercury source exceeded the response size limit.");
      json += decoder.decode(value, { stream: true });
    }
    json += decoder.decode();
    return parseMercuryResponse(json);
  } finally {
    await reader.cancel();
    reader.releaseLock();
  }
}

// State and node invocation are host capabilities. Tests replace external inputs, not parsing or lifecycle policy.
export function createMercuryTracker({ state, invoke, nodeId, refreshMinutes = 60, fetchImpl, now = Date.now }) {
  if (typeof nodeId !== "string" || !nodeId.trim()) throw new Error("Configure an exact Windows nodeId.");
  if (!Number.isInteger(refreshMinutes) || refreshMinutes < 15 || refreshMinutes > 1440)
    throw new Error("refreshMinutes must be an integer between 15 and 1440.");
  let queue = Promise.resolve();
  const serialize = (operation) => {
    const result = queue.then(operation);
    queue = result.then(() => undefined, () => undefined);
    return result;
  };
  const read = async () => {
    const record = (await state.lookup(STATE_KEY)) ?? { enabled: false };
    if (record.presentation) record.presentation = migratePresentation(record.presentation);
    if (record.snapshot?.presentation)
      record.snapshot = { ...record.snapshot, presentation: migratePresentation(record.snapshot.presentation) };
    return record;
  };
  const save = (record) => state.register(STATE_KEY, record);
  async function send(command, params, signal) {
    const result = await invoke({ nodeId, command, params, timeoutMs: 30_000, signal });
    if (result?.ok === false) throw new Error("Windows node rejected the widget command.");
    return result;
  }

  async function refresh(record, signal) {
    const attemptedAt = new Date(now()).toISOString();
    try {
      const retrograde = await fetchMercury({ fetchImpl, signal });
      signal?.throwIfAborted();
      const previousTime = Date.parse(record.snapshot?.observedAt ?? "");
      const observedAt = Math.max(now(), Number.isFinite(previousTime) ? previousTime + 1 : 0);
      const snapshot = {
        id: TRACKER_ID,
        title: "Mercury retrograde",
        value: retrograde ? "Yes" : "No",
        detail: retrograde ? "The selected source reports Mercury is in retrograde."
          : "The selected source reports Mercury is not in retrograde.",
        sourceUrl: SOURCE_URL,
        observedAt: new Date(observedAt).toISOString(),
        staleAfterMinutes: Math.min(10_080, refreshMinutes * 2),
        ...(record.presentation ? { presentation: record.presentation } : {}),
      };
      const delivery = await send("widgets.upsert", snapshot, signal);
      await save({ ...record, enabled: true, snapshot, attemptedAt, delivery, error: null, presentationError: null });
      return { snapshot, delivery, requiresUserPin: true };
    } catch (error) {
      await save({ ...record, attemptedAt, error: error instanceof Error ? error.message : "Widget refresh failed." });
      throw error;
    }
  }

  return {
    execute(action, { signal, sourceUrl, presentation, artworkFiles } = {}) {
      return serialize(async () => {
        if (sourceUrl !== undefined && sourceUrl !== SOURCE_URL)
          throw new Error(`This proof of concept accepts only ${SOURCE_URL}`);
        if (presentation !== undefined && action !== "create" && action !== "style")
          throw new Error("Widget presentation is supported only with create or style.");
        if (artworkFiles !== undefined && action !== "create" && action !== "style")
          throw new Error("artworkFiles is supported only with create or style.");
        const record = await read();
        const retained =
          presentation !== null && typeof presentation === "object" && !Array.isArray(presentation) &&
          presentation.assets === undefined && record.presentation?.assets
            ? { ...presentation, assets: record.presentation.assets } : presentation;
        const prepared = await loadArtworkFiles(retained, artworkFiles);
        const validated = prepared === undefined ? undefined : validatePresentation(prepared);
        switch (action) {
          case "status":
            return { ...record, nodeId, refreshMinutes, requiresUserPin: true };
          case "create": {
            const enabled = { ...record, enabled: true, ...(validated ? { presentation: validated } : {}) };
            await save(enabled);
            return refresh(enabled, signal);
          }
          case "style": {
            if (!validated) throw new Error("Widget style requires presentation.");
            if (!record.enabled || !record.snapshot) throw new Error("Create the tracker before styling it.");
            const snapshot = { ...record.snapshot, presentation: validated };
            try {
              const delivery = await send("widgets.upsert", snapshot, signal);
              await save({ ...record, presentation: validated, snapshot, delivery, presentationError: null });
              return { snapshot, delivery, requiresUserPin: true };
            } catch (error) {
              await save({ ...record, presentationError: error instanceof Error ? error.message : "Widget styling failed." });
              throw error;
            }
          }
          case "refresh":
            if (!record.enabled) throw new Error("Create the tracker before refreshing it.");
            return refresh(record, signal);
          case "stop":
            await save({ ...record, enabled: false });
            return { stopped: true, delivery: await send("widgets.delete", { id: TRACKER_ID }, signal) };
          default:
            throw new Error("Unknown windows_widgets action.");
        }
      });
    },
    tick(signal) {
      return serialize(async () => {
        const record = await read();
        if (!record.enabled) return;
        const lastAttempt = Date.parse(record.attemptedAt ?? "");
        if (!Number.isFinite(lastAttempt) || now() - lastAttempt >= refreshMinutes * 60_000)
          return refresh(record, signal);
      });
    },
    drain() { return queue; },
  };
}
import { validatePresentation, migratePresentation } from "./presentation.mjs";
import { loadArtworkFiles } from "./artwork.mjs";
