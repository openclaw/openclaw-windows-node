// Collects explicitly approved JSON sources without evaluating source content or following redirects.
// Usage: collectSource(definition, { allowedSourceUrls, signal }) returns bounded display facts.
import { lookup } from "node:dns/promises";
import { request } from "node:https";
import { BlockList, isIP } from "node:net";
import { validatePublicUrl } from "./presentation.mjs";

export const SOURCE_TIMEOUT_MS = 15_000;
export const MAX_SOURCE_BYTES = 262_144;
const MAX_SOURCE_ENTRIES = 2048;
const MAX_POINTS = 96;
const MG_DL_PER_MMOL = 18;
const object = value => value !== null && typeof value === "object" && !Array.isArray(value);
const forbidden = new Set(["__proto__", "prototype", "constructor"]);
const blocked = new BlockList();
for (const [network, bits] of [
  ["0.0.0.0", 8], ["10.0.0.0", 8], ["100.64.0.0", 10], ["127.0.0.0", 8],
  ["169.254.0.0", 16], ["172.16.0.0", 12], ["192.0.0.0", 24], ["192.0.2.0", 24],
  ["192.168.0.0", 16], ["198.18.0.0", 15], ["198.51.100.0", 24],
  ["203.0.113.0", 24], ["224.0.0.0", 3],
]) blocked.addSubnet(network, bits, "ipv4");
const globalV6 = new BlockList();
globalV6.addSubnet("2000::", 3, "ipv6");
blocked.addSubnet("2001::", 23, "ipv6");
blocked.addSubnet("2001:db8::", 32, "ipv6");
blocked.addSubnet("2002::", 16, "ipv6");

export class WidgetSourceError extends Error {
  constructor(code) { super(`Widget source failed: ${code}.`); this.code = code; }
}

export function isPublicAddress(address) {
  const family = isIP(address);
  return family === 4 ? !blocked.check(address, "ipv4")
    : family === 6 && globalV6.check(address, "ipv6") && !blocked.check(address, "ipv6");
}

function keys(value, accepted, name) {
  if (!object(value) || Object.keys(value).some(key => !accepted.includes(key)))
    throw new Error(`Unknown or invalid ${name} fields.`);
}

export function readPointer(document, pointer) {
  if (typeof pointer !== "string" || pointer.length > 256 || !pointer.startsWith("/") ||
    /~(?![01])/u.test(pointer))
    throw new Error("Source fields require JSON Pointers such as /current/value.");
  const parts = pointer.slice(1).split("/").map(key => key.replaceAll("~1", "/").replaceAll("~0", "~"));
  if (parts.length > 8 || parts.some(key => forbidden.has(key)))
    throw new Error("Source pointer exceeds the supported depth or names a reserved key.");
  let value = document;
  for (const key of parts) {
    if (value === null || typeof value !== "object" || !Object.hasOwn(value, key))
      throw new WidgetSourceError("missing-field");
    value = value[key];
  }
  return value;
}

function pointer(pointerValue) {
  try { readPointer({}, pointerValue); }
  catch (error) { if (!(error instanceof WidgetSourceError)) throw error; }
}

export function validateSource(source, allowedSourceUrls = []) {
  if (!object(source) || !["json", "nightscout"].includes(source.kind))
    throw new Error("Source kind must be json or nightscout.");
  validatePublicUrl(source.url);
  if (new URL(source.url).hash || !allowedSourceUrls.includes(source.url))
    throw new Error("Approve this exact source URL in plugin config allowedSourceUrls before collecting it.");
  if (source.kind === "json") {
    keys(source, ["kind", "url", "valuePointer", "observedAtPointer", "fields", "valueMap"], "JSON source");
    pointer(source.valuePointer);
    if (source.observedAtPointer !== undefined) pointer(source.observedAtPointer);
    if (source.fields !== undefined && (!object(source.fields) || Object.keys(source.fields).length > 16))
      throw new Error("Source fields must contain at most 16 named scalar projections.");
    for (const [name, path] of Object.entries(source.fields ?? {})) {
      if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(name) || forbidden.has(name))
        throw new Error("Source field name is invalid.");
      pointer(path);
    }
    if (source.valueMap !== undefined && (!object(source.valueMap) ||
      Object.keys(source.valueMap).length > 16 ||
      Object.values(source.valueMap).some(value => typeof value !== "string" || !value.trim() || value.length > 120)))
      throw new Error("valueMap must contain at most 16 literal display strings of 1-120 characters.");
  } else {
    keys(source, ["kind", "url", "units", "windowMinutes", "maxPoints", "gapMinutes"], "Nightscout source");
    if (!["mg/dL", "mmol/L"].includes(source.units))
      throw new Error("Select explicit Nightscout display units: mg/dL or mmol/L.");
    for (const [key, minimum, maximum, fallback] of [
      ["windowMinutes", 15, 1440, 240], ["maxPoints", 2, MAX_POINTS, 96], ["gapMinutes", 1, 60, 15],
    ]) {
      if (!Number.isInteger(source[key] ?? fallback) || (source[key] ?? fallback) < minimum ||
        (source[key] ?? fallback) > maximum)
        throw new Error(`Nightscout ${key} must be an integer between ${minimum} and ${maximum}.`);
    }
  }
  return structuredClone(source);
}

/** Resolves once and pins TLS to a public address. Never follows redirects, proxies, or source-supplied URLs. */
export async function fetchSourceJson(url, { signal, resolve = lookup, requestImpl = request } = {}) {
  validatePublicUrl(url);
  const deadline = AbortSignal.timeout(SOURCE_TIMEOUT_MS);
  const combined = signal ? AbortSignal.any([signal, deadline]) : deadline;
  try {
    combined.throwIfAborted();
    let abortLookup;
    const aborted = new Promise((_, reject) => {
      abortLookup = () => reject(new WidgetSourceError("timeout-or-cancelled"));
      combined.addEventListener("abort", abortLookup, { once: true });
    });
    let addresses;
    try {
      addresses = await Promise.race([
        resolve(new URL(url).hostname, { all: true, verbatim: true }), aborted,
      ]);
    } finally { combined.removeEventListener("abort", abortLookup); }
    combined.throwIfAborted();
    if (!addresses.length || addresses.some(entry => !isPublicAddress(entry.address)))
      throw new WidgetSourceError("nonpublic-address");
    const pinned = addresses[0];
    return await new Promise((accept, reject) => {
      const req = requestImpl(url, {
        method: "GET", signal: combined, agent: false,
        headers: { Accept: "application/json", "User-Agent": "OpenClaw-Windows-Widgets-PoC/0.1" },
        lookup(_host, options, callback) {
          callback(null, options.all ? [{ address: pinned.address, family: pinned.family }] : pinned.address,
            pinned.family);
        },
      }, response => {
        if (response.statusCode !== 200 ||
          response.headers["content-type"]?.split(";")[0].trim().toLowerCase() !== "application/json") {
          response.destroy();
          reject(new WidgetSourceError(response.statusCode !== 200 ? "http-status" : "not-json"));
          return;
        }
        const chunks = [];
        let length = 0;
        response.on("data", chunk => {
          length += chunk.length;
          if (length > MAX_SOURCE_BYTES) {
            reject(new WidgetSourceError("response-too-large"));
            response.destroy();
            return;
          }
          chunks.push(chunk);
        });
        response.on("error", () => reject(new WidgetSourceError("transport")));
        response.on("aborted", () => reject(new WidgetSourceError("transport")));
        response.on("end", () => {
          try { accept(JSON.parse(Buffer.concat(chunks).toString("utf8"))); }
          catch { reject(new WidgetSourceError("invalid-json")); }
        });
      });
      req.on("error", () => reject(new WidgetSourceError(combined.aborted ? "timeout-or-cancelled" : "transport")));
      req.end();
    });
  } catch (error) {
    if (error instanceof WidgetSourceError) throw error;
    throw new WidgetSourceError(combined.aborted ? "timeout-or-cancelled" : "transport");
  }
}

function scalar(value) {
  if (value === null || !["string", "number", "boolean"].includes(typeof value) ||
    (typeof value === "number" && !Number.isFinite(value)) || String(value).length > 600)
    throw new WidgetSourceError("invalid-scalar");
  return value;
}

export function normalizeObservationTime(value, now) {
  const iso = typeof value === "string" &&
    /^\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|[+-](?:0\d|1[0-3]):[0-5]\d|[+-]14:00)$/u.test(value);
  const time = iso ? Date.parse(value) : NaN;
  const daysInMonth = iso ? new Date(Date.UTC(Number(value.slice(0, 4)), Number(value.slice(5, 7)), 0)).getUTCDate() : 0;
  if (!Number.isFinite(time) || Number(value.slice(8, 10)) > daysInMonth || time <= 0 || time > now + 300_000)
    throw new WidgetSourceError("invalid-observation-time");
  return new Date(time).toISOString();
}

function seriesObservation(points, units, start, end, gapMinutes, detail = "") {
  const latest = points.at(-1).y;
  const minimum = Math.min(...points.map(point => point.y));
  const maximum = Math.max(...points.map(point => point.y));
  const windowStart = new Date(start).toISOString();
  const windowEnd = new Date(end).toISOString();
  return {
    value: `${latest} ${units}`, detail, observedAt: windowEnd,
    content: { points, latest, units, minimum, maximum, windowStart, windowEnd, gapMinutes,
      rangeLabel: `${minimum}-${maximum} ${units} | ${windowStart.slice(11, 16)}-${windowEnd.slice(11, 16)} UTC`,
      graphAlt: `${points.length} samples; ${minimum}-${maximum} ${units}. Gaps are not connected.`,
      graphUrl: "openclaw-asset:source-series" },
  };
}

/** Projects caller-collected history without fetching, scheduling, unit conversion or invented samples. */
export function projectSeries(series, now = Date.now()) {
  keys(series, ["points", "units", "windowMinutes", "gapMinutes"], "series");
  if (typeof series.units !== "string" || !series.units.trim() || series.units.length > 32 ||
    /[\u0000-\u001f\u007f]/u.test(series.units))
    throw new Error("Series requires explicit display units of 1-32 characters.");
  const windowMinutes = series.windowMinutes === undefined ? 240 : series.windowMinutes;
  const gapMinutes = series.gapMinutes === undefined ? 15 : series.gapMinutes;
  if (!Number.isInteger(windowMinutes) || windowMinutes < 15 || windowMinutes > 1440 ||
    !Number.isInteger(gapMinutes) || gapMinutes < 1 || gapMinutes > 60)
    throw new Error("Series windowMinutes must be 15-1440 and gapMinutes must be 1-60.");
  if (!Array.isArray(series.points) || !series.points.length || series.points.length > MAX_POINTS)
    throw new Error("Series requires 1-96 actual timestamped points.");
  const samples = new Map();
  for (const point of series.points) {
    keys(point, ["x", "y"], "series point");
    const time = Date.parse(normalizeObservationTime(point.x, now));
    if (typeof point.y !== "number" || !Number.isFinite(point.y))
      throw new Error("Series values must be finite numbers in the declared units.");
    if (samples.has(time) && samples.get(time) !== point.y)
      throw new Error("Series contains conflicting samples for one timestamp.");
    samples.set(time, point.y);
  }
  const sorted = [...samples].sort(([left], [right]) => left - right);
  const end = sorted.at(-1)[0], start = end - windowMinutes * 60_000;
  const points = sorted.filter(([time]) => time >= start)
    .map(([time, y]) => ({ x: new Date(time).toISOString(), y }));
  return seriesObservation(points, series.units, start, end, gapMinutes);
}

export function projectSource(source, document, now = Date.now()) {
  if (source.kind === "json") {
    const raw = scalar(readPointer(document, source.valuePointer));
    const value = source.valueMap === undefined ? String(raw)
      : Object.hasOwn(source.valueMap, String(raw)) ? source.valueMap[String(raw)] : undefined;
    if (typeof value !== "string" || !value.trim() || value.length > 120)
      throw new WidgetSourceError("unmapped-or-oversized-value");
    const content = Object.fromEntries(Object.entries(source.fields ?? {})
      .map(([key, path]) => [key, scalar(readPointer(document, path))]));
    return { value, detail: "", observedAt: source.observedAtPointer === undefined
      ? new Date(now).toISOString() : normalizeObservationTime(readPointer(document, source.observedAtPointer), now), content };
  }
  if (!Array.isArray(document) || document.length > MAX_SOURCE_ENTRIES)
    throw new WidgetSourceError("invalid-entries");
  const samples = new Map();
  for (const entry of document) {
    if (!object(entry) || entry.type !== "sgv") continue;
    if (!Number.isSafeInteger(entry.date) || entry.date <= 0 || entry.date > now + 300_000 ||
      typeof entry.sgv !== "number" || !Number.isFinite(entry.sgv) || entry.sgv <= 0)
      throw new WidgetSourceError("invalid-sample");
    if (samples.has(entry.date) && samples.get(entry.date) !== entry.sgv)
      throw new WidgetSourceError("conflicting-sample");
    samples.set(entry.date, entry.sgv);
  }
  if (!samples.size) throw new WidgetSourceError("no-sensor-samples");
  const sorted = [...samples].sort(([left], [right]) => left - right);
  const end = sorted.at(-1)[0];
  const start = end - (source.windowMinutes ?? 240) * 60_000;
  const points = sorted.filter(([time]) => time >= start).slice(-(source.maxPoints ?? MAX_POINTS))
    .map(([time, value]) => ({ x: new Date(time).toISOString(),
      y: source.units === "mmol/L" ? Math.round(value / MG_DL_PER_MMOL * 10) / 10 : value }));
  return seriesObservation(points, source.units, start, end, source.gapMinutes ?? 15,
    "Sensor history from the selected source. Not for treatment decisions.");
}

export async function collectSource(source, { allowedSourceUrls, signal, now = Date.now(), fetchJson = fetchSourceJson }) {
  validateSource(source, allowedSourceUrls);
  return projectSource(source, await fetchJson(source.url, { signal }), now);
}
