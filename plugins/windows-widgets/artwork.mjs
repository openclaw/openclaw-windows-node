// Transfers explicitly selected Gateway-local PNG files as bounded snapshot data, never native file paths.
// Usage: await loadArtworkFiles(presentation, { backdrop: "/absolute/path/generated.png" }).
import { open, lstat } from "node:fs/promises";
import { isAbsolute } from "node:path";
import { MAX_ARTWORK_BYTES, validatePresentation } from "./presentation.mjs";

/** Encodes supplied artwork files for create/style. Missing or invalid artwork fails before publication. */
export async function loadArtworkFiles(presentation, files) {
  if (files === undefined) return presentation;
  if (files === null || typeof files !== "object" || Array.isArray(files) ||
      presentation === null || typeof presentation !== "object" || Array.isArray(presentation))
    throw new Error("artworkFiles requires a presentation and an object of asset names to absolute PNG paths.");
  if (Object.keys(files).length > 256)
    throw new Error("Widget presentation exceeds 256 objects.");
  if (presentation.assets !== undefined &&
      (presentation.assets === null || typeof presentation.assets !== "object" || Array.isArray(presentation.assets)))
    throw new Error("Widget assets must be a named object of PNG base64 resources.");
  const assets = { ...presentation.assets };
  let total = 0;
  for (const [name, path] of Object.entries(files)) {
    if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(name) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/.test(name) ||
        typeof path !== "string" || !isAbsolute(path))
      throw new Error("Artwork requires a valid asset name and an absolute Gateway-local PNG path.");
    if (!(await lstat(path)).isFile())
      throw new Error("Artwork must be a regular PNG file, not a directory or link.");
    const file = await open(path, "r");
    try {
      const stat = await file.stat();
      if (!stat.isFile() || stat.size > MAX_ARTWORK_BYTES - total)
        throw new Error("Widget artwork exceeds 262144 decoded bytes or is not a regular file.");
      const bytes = Buffer.alloc(MAX_ARTWORK_BYTES - total + 1);
      let length = 0;
      while (length < bytes.length) {
        const { bytesRead } = await file.read(bytes, length, bytes.length - length, null);
        if (bytesRead === 0) break;
        length += bytesRead;
      }
      total += length;
      if (total > MAX_ARTWORK_BYTES)
        throw new Error("Widget artwork exceeds 262144 decoded bytes.");
      assets[name] = { base64: bytes.subarray(0, length).toString("base64") };
    } finally { await file.close(); }
  }
  return validatePresentation({ ...presentation, assets });
}
