"use strict";

const crypto = require("node:crypto");

const LOCALES = ["en", "fr", "nl", "pt-BR", "zh-CN", "zh-TW"];
const DIRECTORY = "ui/src/i18n/";
const digest = (text) => crypto.createHash("sha256").update(text).digest("hex");

function check(ok, message) {
  if (!ok) throw new Error(message);
}

// Read static catalog literals and en.* aliases, never execute upstream source.
// A new expression shape fails visibly so the monitor cannot silently miss copy.
function catalogObject(source, property, indent, prefix, references = new Map()) {
  const matches = [...source.matchAll(new RegExp("^" + " ".repeat(indent) + property + ":\\s*(?=\\{)", "gm"))];
  check(matches.length === 1, "Welcome catalog moved or is ambiguous; update its source reader");
  let position = matches[0].index + matches[0][0].length;
  const tokens = /\s+|\/\/[^\r\n]*|\/\*[\s\S]*?\*\/|"(?:\\.|[^"\\])*"|[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*|[{}:,]/y;
  const values = new Map();
  function next() {
    for (;;) {
      tokens.lastIndex = position;
      const token = tokens.exec(source);
      check(token, "Unsupported welcome expression; update the static catalog reader");
      position = tokens.lastIndex;
      if (!/^\s|^\/\//.test(token[0]) && !token[0].startsWith("/*")) return token[0];
    }
  }
  function object(parent) {
    const names = new Set();
    for (let key = next(); key !== "}"; key = next()) {
      check(/^[A-Za-z_][\w]*$/.test(key) && !names.has(key), "Unsupported or duplicate welcome key");
      names.add(key);
      check(next() === ":", "Invalid welcome catalog property");
      const name = parent + "." + key;
      const value = next();
      if (value === "{") object(name);
      else {
        const text = value.startsWith('"') ? JSON.parse(value) : references.get(value.replace(/^en\./, ""));
        check(typeof text === "string" && text.trim() && text.length <= 8192,
          "Missing static welcome text; update the source reader");
        values.set(name, text);
      }
      const end = next();
      if (end === "}") return;
      check(end === ",", "Unsupported welcome catalog expression");
    }
  }
  check(next() === "{", "Missing welcome catalog object");
  object(prefix);
  return values;
}

async function readWelcomeContent(github, commit) {
  check(/^[a-f0-9]{40}$/.test(commit), "Welcome source requires an immutable commit");
  const files = ["locales/en.ts", "locales/en-command-palette.ts", "locales/en-new-session-setup.ts",
    ...LOCALES.slice(1).map((locale) => ".i18n/" + locale + ".tm.jsonl")];
  const source = Object.fromEntries(await Promise.all(files.map(async (file) => {
    const { data } = await github.request("GET /repos/{owner}/{repo}/contents/{path}", {
      owner: "openclaw", repo: "openclaw", path: DIRECTORY + file, ref: commit,
      headers: { accept: "application/vnd.github.raw+json" }, request: { timeout: 60000 },
    });
    check(typeof data === "string" && Buffer.byteLength(data) <= 16 * 1024 * 1024,
      "Welcome source is missing or exceeds its size budget");
    return [file, data];
  })));
  const base = new Map([
    ...catalogObject(source["locales/en.ts"], "newSession", 2, "newSession"),
    ...catalogObject(source["locales/en.ts"], "welcome", 4, "chat.welcome"),
  ]);
  const hint = [...source["locales/en-new-session-setup.ts"].matchAll(
    /^    hint:\s*("(?:\\.|[^"\\])*"|en\.newSession\.hint)\s*,\s*$/gm,
  )];
  check(hint.length === 1, "Welcome hint moved; update its source reader");
  const english = new Map([
    ["newSession.hint", hint[0][1].startsWith('"') ? JSON.parse(hint[0][1]) : base.get("newSession.hint")],
    ...[...base].filter(([key]) => key.startsWith("chat.welcome.")),
    ...catalogObject(source["locales/en-command-palette.ts"], "welcome", 4, "chat.welcome", base),
  ]);
  check(english.size <= 128 && english.has("chat.welcome.recentSessions")
    && [...english.values()].every((text) => typeof text === "string" && text.trim()),
  "Welcome source is missing or empty; review the copy owner");
  // Match the upstream translation-memory contract: latest cache_key wins,
  // segment aliases share translations, stale source hashes fall back to English.
  const sourceHashes = new Map([...english].map(([key, text]) => [key, digest(text.trim().split(/\s+/).join(" "))]));
  const values = {};
  for (const locale of LOCALES) {
    const translated = new Map(english);
    const memory = new Map();
    for (const line of (source[".i18n/" + locale + ".tm.jsonl"] ?? "").split("\n")) {
      if (!line.trim()) continue;
      const entry = JSON.parse(line);
      if (entry.cache_key && typeof entry.translated === "string" && entry.translated.trim()) memory.set(entry.cache_key, entry);
    }
    for (const entry of memory.values()) {
      for (const key of [entry.segment_id, ...(entry.segment_ids ?? [])]) {
        if (sourceHashes.has(key) && entry.text_hash === sourceHashes.get(key)) translated.set(key, entry.translated);
      }
    }
    for (const [key, text] of [...translated].sort(([a], [b]) => a.localeCompare(b))) values[locale + ":" + key] = digest(text);
  }
  return { commit, fingerprint: digest(JSON.stringify(values)), values };
}

module.exports = { readWelcomeContent };
