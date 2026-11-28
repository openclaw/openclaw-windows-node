// Renders actual timestamped samples to a bounded, self-contained PNG, not a remote chart service.
// Usage: seriesArtwork(source.content) returns the source-series asset for an ordinary native Image.
import { crc32, deflateSync } from "node:zlib";

const WIDTH = 600;
const HEIGHT = 200;
const INSET = 12;
const PLOT_SURFACE = 255;
const PLOT_INK = 32;
const PLOT_GRID = 208;

export function seriesArtwork({ points, windowStart, windowEnd, gapMinutes }) {
  const start = Date.parse(windowStart), end = Date.parse(windowEnd);
  if (!Array.isArray(points) || !points.length || points.length > 96 ||
    !Number.isFinite(start) || !Number.isFinite(end) || start >= end ||
    !Number.isInteger(gapMinutes) || gapMinutes < 1 || gapMinutes > 60)
    throw new Error("Series artwork requires a bounded, timestamped observation window.");
  const samples = points.map(point => ({ time: Date.parse(point.x), value: point.y }));
  if (samples.some((point, index) => !Number.isFinite(point.time) || point.time < start || point.time > end ||
    typeof point.value !== "number" || !Number.isFinite(point.value) ||
    (index && point.time <= samples[index - 1].time)))
    throw new Error("Series artwork requires finite values in strictly increasing timestamp order.");
  const low = Math.min(...samples.map(point => point.value));
  const high = Math.max(...samples.map(point => point.value));
  const span = high - low || 1;
  if (!Number.isFinite(span)) throw new Error("Series range must remain finite.");
  const pixels = Buffer.alloc(WIDTH * HEIGHT, PLOT_SURFACE);
  const pixel = (x, y, color) => {
    if (x >= 0 && x < WIDTH && y >= 0 && y < HEIGHT) pixels[y * WIDTH + x] = color;
  };
  for (const y of [INSET, Math.floor(HEIGHT / 2), HEIGHT - INSET - 1])
    for (let x = INSET; x < WIDTH - INSET; x++) pixel(x, y, PLOT_GRID);
  const position = sample => ({
    x: Math.round(INSET + (sample.time - start) / (end - start) * (WIDTH - INSET * 2 - 1)),
    y: Math.round(HEIGHT - INSET - 1 - (high === low ? 0.5 : (sample.value - low) / span) *
      (HEIGHT - INSET * 2 - 1)),
  });
  for (let index = 0; index < samples.length; index++) {
    const point = position(samples[index]);
    if (index && samples[index].time - samples[index - 1].time <= gapMinutes * 60_000) {
      const previous = position(samples[index - 1]);
      const steps = Math.max(Math.abs(point.x - previous.x), Math.abs(point.y - previous.y));
      for (let step = 0; step <= steps; step++) {
        const x = Math.round(previous.x + (point.x - previous.x) * step / Math.max(steps, 1));
        const y = Math.round(previous.y + (point.y - previous.y) * step / Math.max(steps, 1));
        pixel(x, y, PLOT_INK);
        pixel(x, y + 1, PLOT_INK);
      }
    }
    for (let dy = -2; dy <= 2; dy++)
      for (let dx = -2; dx <= 2; dx++)
        if (dx * dx + dy * dy <= 4) pixel(point.x + dx, point.y + dy, PLOT_INK);
  }
  const scanlines = Buffer.alloc((WIDTH + 1) * HEIGHT);
  for (let y = 0; y < HEIGHT; y++)
    pixels.copy(scanlines, y * (WIDTH + 1) + 1, y * WIDTH, (y + 1) * WIDTH);
  function chunk(type, data) {
    const name = Buffer.from(type, "ascii");
    const result = Buffer.alloc(data.length + 12);
    result.writeUInt32BE(data.length);
    name.copy(result, 4);
    data.copy(result, 8);
    result.writeUInt32BE(crc32(Buffer.concat([name, data])), data.length + 8);
    return result;
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(WIDTH);
  header.writeUInt32BE(HEIGHT, 4);
  header[8] = 8; // Eight-bit grayscale artwork, with native text labels outside the image.
  return { base64: Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk("IHDR", header), chunk("IDAT", deflateSync(scanlines)), chunk("IEND", Buffer.alloc(0)),
  ]).toString("base64") };
}
