"use strict";

const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const policy = require("./gateway-capability-policy.json");
const REPORT_EXAMPLE_LIMIT = 3;

function digest(value) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

function check(ok, message) {
  if (!ok) throw new Error(message);
}

// Like GatewayProtocolDriftTests, keep index-aligned code and masked views.
// These are wiring *candidates*, never a reachability or behavioral-support proof.
function sourceViews(text) {
  const code = text.split("");
  const masked = text.split("");
  const blank = (target, start, end) => {
    for (let i = start; i < end; i++) if (!/[\r\n]/.test(target[i])) target[i] = " ";
  };
  const tokens = /\/\/[^\r\n]*|\/\*[\s\S]*?\*\/|\$*"{3,}[\s\S]*?"{3,}|(?:\$@|@\$|@)"(?:""|[^"])*"|\$?"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'/g;
  for (const match of text.matchAll(tokens)) {
    blank(masked, match.index, match.index + match[0].length);
    if (match[0].startsWith("//") || match[0].startsWith("/*"))
      blank(code, match.index, match.index + match[0].length);
  }
  return { code: code.join(""), masked: masked.join("") };
}

function wiringCandidates(files) {
  const evidence = [];
  for (const [file, text] of Object.entries(files)) {
    if (!file.startsWith("src/") || !file.endsWith(".cs")) continue;
    const views = sourceViews(text);
    const patterns = [
      ["dispatch", /\b(?:SendTrackedRequestAsync|TrySendTrackedRequestAsync|TryRequestPayloadAsync|RequestPayloadAsync|MutateCompactionAsync)\s*\(\s*"([A-Za-z][A-Za-z0-9_.]*)"/g],
      ["handler", /\bcase\s*"([A-Za-z][A-Za-z0-9_.]*)"\s*:/g],
      ["construction", /\bmethod\s*=\s*"([A-Za-z][A-Za-z0-9_.]*)"/g],
    ];
    for (const [kind, regex] of patterns) {
      for (const match of views.code.matchAll(regex)) {
        // An apparent call embedded inside a literal is not executable syntax.
        if (!/\S/.test(views.masked.slice(match.index, match.index + match[0].indexOf('"')))) continue;
        evidence.push({ kind, name: match[1], file,
          line: text.slice(0, match.index).split("\n").length, sha256: digest(text) });
      }
    }
  }
  return evidence;
}

function readLocal(root) {
  const git = (...args) => execFileSync("git", ["--no-optional-locks", "-C", root, ...args],
    { encoding: "utf8", maxBuffer: 16 * 1024 * 1024, timeout: 60000 });
  const head = git("rev-parse", "HEAD").trim();
  const paths = [...new Set(git("ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "src", "tests").split("\0")
    .filter((file) => file.endsWith(".cs")))].sort();
  check(paths.length > 0 && paths.length < 20000, "Missing or excessive local C# evidence");
  const files = {};
  let bytes = 0;
  for (const file of paths) {
    const full = path.join(root, ...file.split("/"));
    check(!fs.lstatSync(full).isSymbolicLink(), "Local evidence must not follow symlinks");
    const text = fs.readFileSync(full, "utf8");
    bytes += Buffer.byteLength(text);
    check(bytes < 128 * 1024 * 1024, "Local evidence exceeds budget");
    files[file] = text;
  }
  return { head, dirty: git("status", "--porcelain", "--", "src", "tests", ".github/scripts").length > 0, files };
}

// Follow references, including shared definitions. Broad source inventories and
// publication metadata remain observation evidence, not new-work identity.
function familyContract(schema, family) {
  const definitions = {};
  const seen = new Set();
  function contract(value, dictionary = false) {
    if (Array.isArray(value)) return value.map((item) => contract(item));
    if (!value || typeof value !== "object") return value;
    const result = {};
    for (const [key, item] of Object.entries(value)) {
      if (!dictionary && ["description", "title", "examples", "$comment"].includes(key)) continue;
      if (!dictionary && key === "$ref") {
        check(typeof item === "string" && item.startsWith("#/definitions/"), "Unsupported family schema reference");
        const name = item.slice("#/definitions/".length).split("/")[0].replace(/~1/g, "/").replace(/~0/g, "~");
        include(name);
      }
      result[key] = !dictionary && ["enum", "const", "default"].includes(key) ? item
        : contract(item, !dictionary && ["properties", "patternProperties", "definitions", "$defs", "dependentSchemas"].includes(key));
    }
    return result;
  }
  function include(name) {
    check(Object.hasOwn(schema.definitions, name), "Missing referenced family definition");
    if (seen.has(name)) return;
    seen.add(name);
    definitions[name] = contract(schema.definitions[name]);
  }
  for (const name of Object.keys(schema.definitions).sort()) {
    if (family.definitionPrefixes.some((prefix) => name.startsWith(prefix))
      || ["ConnectParams", "RequestFrame", "ResponseFrame", "EventFrame"].includes(name)) include(name);
  }
  const methods = Object.fromEntries(family.methods.slice().sort()
    .map((name) => [name, contract(schema.methods[name] ?? null)]));
  return { methods, definitions };
}

function assess({ schema, local, upstreamHash, hash, supportPolicy = policy }) {
  check(supportPolicy.version === 1 && Array.isArray(supportPolicy.decisions)
    && Array.isArray(supportPolicy.families), "Unsupported capability policy");
  const inventory = Object.fromEntries(Object.entries(local.files).sort(([a], [b]) => a.localeCompare(b))
    .map(([file, text]) => [file, digest(text)]));
  const candidates = wiringCandidates(local.files);
  const policyHash = hash(supportPolicy);
  const inputHash = hash({ inventory, upstreamHash, policyVersion: supportPolicy.version });
  const surfaces = Object.entries(schema.methods).sort(([a], [b]) => a.localeCompare(b)).map(([name, metadata]) => ({
    id: `method:${name}`, name, scope: metadata.scope ?? null, kind: "method",
  }));
  for (const family of supportPolicy.families) {
    if (!family.methods.some((method) => Object.hasOwn(schema.methods, method))) continue;
    for (const name of family.events) surfaces.push({ id: `event:${name}`, name, scope: family.scope, kind: "event" });
  }
  for (const name of Object.keys(schema.definitions).filter((name) => name.endsWith("Event"))) {
    surfaces.push({ id: `definition:${name}`, name, scope: null, kind: "definition" });
  }
  const assessments = surfaces.map((surface) => {
    const decision = supportPolicy.decisions.find((entry) => entry.surface === surface.id);
    const evidence = candidates.filter((entry) => entry.name === surface.name);
    let status = "pending";
    let reason = evidence.length
      ? "Wiring candidates found. Reachability, payload semantics, authorization and behavioral tests still require review."
      : "No recognized production wiring. Unknown relevance or missing support requires review, not an automatic defect claim.";
    if (decision?.status === "intentionally-unsupported" && decision.scope === surface.scope
      && decision.rationale && decision.fallback) {
      status = decision.status;
      reason = `${decision.rationale} Fallback: ${decision.fallback}`;
    }
    if (decision?.status === "supported") {
      // Review records are deliberately invalidated by changed source/test/upstream evidence.
      // Presence of a method name, DTO field, mock, or call candidate cannot grant support.
      const witnesses = decision.evidence ?? [];
      const kinds = ["dispatch", "handler", "construction", "behavioral-test"];
      const valid = decision.inputHash === inputHash && decision.rationale
        && kinds.every((kind) => witnesses.some((item) => item.kind === kind))
        && witnesses.every((item) => inventory[item.file] === item.sha256
          && (item.kind === "behavioral-test" ? item.file.startsWith("tests/") : item.file.startsWith("src/")));
      if (valid) {
        status = "supported";
        reason = decision.rationale;
        evidence.push(...witnesses);
      } else reason = "Supported review is stale or lacks dispatch/handler/construction and behavioral-test evidence.";
    }
    return { ...surface, status, reason, evidence };
  });
  const families = supportPolicy.families.filter((family) => family.methods.some((name) => name in schema.methods))
    .map((family) => {
      const members = assessments.filter((item) => family.methods.includes(item.name) || family.events.includes(item.name));
      const status = members.every((item) => item.status === "supported") ? "supported"
        : members.every((item) => item.status === "intentionally-unsupported") ? "intentionally-unsupported" : "pending";
      const surfaces = members.map((item) => item.id);
      const contractHash = hash({ contract: familyContract(schema, family), family,
        decisions: supportPolicy.decisions.filter((item) => surfaces.includes(item.surface)) });
      return { id: family.id, status, contractHash, candidateHash: hash({ family: family.id, contractHash }),
        surfaces: members.map((item) => item.id), obligations: family.obligations,
        reason: "Production support requires every operator method/event and the full behavioral review. Fixture-only work is not production support." };
    });
  const pendingGaps = families.filter((family) => family.status === "pending").map((family) => family.id);
  return { version: 1, localHead: local.head, dirty: local.dirty, inventory, policyHash, upstreamHash, inputHash, pendingGaps,
    assessments, families, limitation: "Conservative source review, not runtime proof. Unknown methods remain pending; open event payloads and new producer behavior also require the upstream source-delta review." };
}

function renderAssessment(value) {
  const counts = {};
  for (const item of value.assessments) counts[item.status] = (counts[item.status] ?? 0) + 1;
  const pending = value.assessments.filter((item) => item.status === "pending");
  const wired = pending.filter((item) => item.evidence.length > 0);
  const unwired = pending.filter((item) => item.evidence.length === 0);
  const tracked = new Set(value.families.flatMap((family) => family.surfaces));
  const trackedPending = pending.filter((item) => tracked.has(item.id)).length;
  const untrackedPending = pending.length - trackedPending;
  const safe = (text) => String(text).replace(/[|`<>\r\n]/g, " ").replace(/@/g, "(at)");
  const examples = (items) => items.slice(0, REPORT_EXAMPLE_LIMIT).map((item) => `\`${safe(item.name)}\``).join(", ") || "none";
  const lines = ["", "## Windows capability assessment", "",
    `Local head: \`${value.localHead}\`; dirty evidence: ${value.dirty}.`,
    `Input evidence: \`${value.inputHash}\`; support policy: \`${value.policyHash}\`.`,
    `Reviewed decisions: supported: ${counts.supported ?? 0}; intentionally unsupported: ${counts["intentionally-unsupported"] ?? 0}.`,
    `Awaiting assessment: **${pending.length} protocol entries**, not a count of missing features.`,
    "",
    "| Surface | Recognized wiring, unreviewed | No recognized wiring, unreviewed | Total awaiting assessment |",
    "| --- | ---: | ---: | ---: |"];
  for (const [kind, label] of [["method", "RPC methods"], ["event", "Explicit lifecycle events"], ["definition", "Event schema definitions"]]) {
    const withWiring = wired.filter((item) => item.kind === kind).length;
    const withoutWiring = unwired.filter((item) => item.kind === kind).length;
    lines.push(`| ${label} | ${withWiring} | ${withoutWiring} | ${withWiring + withoutWiring} |`);
  }
  lines.push("",
    `Examples with recognized wiring: ${examples(wired)}.`,
    `Examples without recognized wiring: ${examples(unwired)}.`,
    `Examples are limited to ${REPORT_EXAMPLE_LIMIT} per evidence bucket; every entry, reason and evidence location is in report.json under capabilities.assessments.`,
    "Recognized wiring is not proof of reachable or correct behavior. No recognized wiring can mean missing support, irrelevant functionality, or a scanner limitation; relevance remains unassessed.",
    "Event schema definitions are data shapes, not additional feature counts. Explicit lifecycle events come from tracked families, not a complete upstream event inventory.",
    "",
    "### Tracked capability candidates",
    "Families are explicitly selected by gateway-capability-policy.json, not discovered by ranking the pending inventory. A pending family is an investigation candidate, not a confirmed missing feature.",
    `Tracked gap candidates: ${value.pendingGaps.length}. Unassessed surfaces are review backlog, not automatic implementation tasks.`,
    `${trackedPending} pending method/event entries belong to explicitly tracked families; ${untrackedPending} other pending entries remain outside that candidate selection.`);
  if (untrackedPending) lines.push(
    "**Coverage incomplete:** untracked pending entries are unknowns, not accepted exclusions. Review Windows relevance and behavior, then record evidence-backed support/exemptions or add a tracked family for an actionable investigation.");
  lines.push(
    "A green workflow means collection succeeded, not Windows compatibility or an acceptable backlog.",
    "Pending counts include unassessed relevance and behavior, not confirmed defects. Implement at most one evidenced production capability gap per PR.",
    "", value.limitation);
  if (value.pendingGaps.length) lines.splice(1, 0, "<!-- windows-capability-pending -->");
  if (value.families.length) lines.push("", "| Capability | Status | Evidence / outstanding obligations |", "| --- | --- | --- |");
  else lines.push("", "No explicitly tracked capability families are present in this observation.");
  for (const family of value.families)
    lines.push(`| ${safe(family.id)} | **${family.status}** | ${family.obligations.map(safe).join("; ")} |`);
  const reviewed = value.assessments.filter((item) => item.status === "supported" || item.status === "intentionally-unsupported");
  if (reviewed.length) lines.push("", "### Reviewed surface decisions", "",
    "| Surface | Status | Evidence / rationale |", "| --- | --- | --- |");
  for (const item of reviewed)
    lines.push(`| ${safe(item.id)} | ${safe(item.status)} | ${safe(item.reason)} |`);
  lines.push("", "All surface classifications and exact production/test file hashes are in report.json. Closing an exact candidate acknowledges that evidence without claiming complete Windows support.",
    "Check existing chat parity and interactive fixture PRs before implementation; link them rather than duplicating work.");
  return lines.join("\n") + "\n";
}

module.exports = { assess, readLocal, sourceViews, wiringCandidates, renderAssessment, policy };
