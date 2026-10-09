"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const UPSTREAM = "openclaw/openclaw";
const MAX_BYTES = 32 * 1024 * 1024;
const SHA = /^[a-f0-9]{40}$/;
const HASH = /^[a-f0-9]{64}$/;
const GROUPS = ["schema", "authorization", "gateway", "agent-producers", "reference-client"];

function check(condition, message) {
  if (!condition) throw new Error(message);
}

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])]));
  }
  return value;
}

function hash(value) {
  return crypto.createHash("sha256").update(
    Buffer.isBuffer(value) ? value : JSON.stringify(canonical(value)),
  ).digest("hex");
}

async function download(url, token) {
  const parsed = new URL(url);
  check(parsed.protocol === "https:" && ["api.github.com", "registry.npmjs.org"].includes(parsed.hostname)
    && !parsed.username && !parsed.password && !parsed.port, "Unapproved upstream URL");
  const response = await fetch(parsed, {
    redirect: "error",
    signal: AbortSignal.timeout(60000),
    headers: {
      "User-Agent": "openclaw-windows-upstream-protocol-monitor",
      ...(parsed.hostname === "api.github.com" && token ? { Authorization: `Bearer ${token}` } : {}),
    },
  });
  check(response.ok, `Upstream request failed: ${parsed.pathname} HTTP ${response.status}`);
  const chunks = [];
  let length = 0;
  for await (const chunk of response.body) {
    length += chunk.length;
    check(length <= MAX_BYTES, "Upstream response exceeds size budget");
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

async function json(url, token) {
  return JSON.parse((await download(url, token)).toString("utf8"));
}

function watchGroup(file) {
  if (/(?:^|\/)(?:__tests__|fixtures?|test-support)\//.test(file)
    || /\.(?:test|spec|suite)\.[cm]?[jt]sx?$/.test(file)) return null;
  if (file.startsWith("packages/gateway-protocol/src/")) return "schema";
  if (file.startsWith("src/gateway/methods/") || file === "src/gateway/method-scopes.ts") return "authorization";
  if (file.startsWith("src/gateway/")) return "gateway";
  if (file.startsWith("src/agents/") || file.startsWith("src/auto-reply/")
    || file.startsWith("src/infra/agent-events")) return "agent-producers";
  if (file.startsWith("ui/src/")) return "reference-client";
  return null;
}

function sourceInventory(tree) {
  check(tree.truncated === false && Array.isArray(tree.tree), "Incomplete upstream tree");
  const files = {};
  const counts = Object.fromEntries(GROUPS.map((group) => [group, 0]));
  for (const entry of tree.tree) {
    if (entry.type !== "blob" || !/\.(?:[cm]?[jt]sx?|svelte|vue)$/.test(entry.path)) continue;
    const group = watchGroup(entry.path);
    if (!group) continue;
    check(SHA.test(entry.sha), "Invalid upstream blob hash");
    check(/^[a-zA-Z0-9_./@()[\]-]+$/.test(entry.path) && !entry.path.split("/").includes(".."),
      "Unsupported upstream source path");
    files[entry.path] = { group, sha: entry.sha };
    counts[group]++;
  }
  check(Object.keys(files).length <= 10000, "Upstream source inventory exceeds budget");
  for (const group of GROUPS) check(counts[group] > 0, `Watch group disappeared: ${group}`);
  check(files["packages/gateway-protocol/src/schema/protocol-schemas.ts"], "Protocol registry moved");
  return canonical(files);
}

async function sourceTrack(commit, token) {
  check(SHA.test(commit), "Invalid upstream commit");
  const tree = await json(`https://api.github.com/repos/${UPSTREAM}/git/trees/${commit}?recursive=1`, token);
  return { commit, files: sourceInventory(tree) };
}

function packageInfo(metadata, name) {
  check(metadata.name === name && /^[0-9][a-zA-Z0-9.+-]{0,99}$/.test(metadata.version),
    `Invalid ${name} package identity`);
  check(/^sha512-[a-zA-Z0-9+/]{86}==$/.test(metadata.dist?.integrity), `Missing ${name} SHA512 integrity`);
  return { name, version: metadata.version, integrity: metadata.dist.integrity };
}

function provenanceCommit(attestation, info) {
  const statements = (attestation.attestations ?? [])
    .filter((item) => item.predicateType === "https://slsa.dev/provenance/v1")
    .map((item) => JSON.parse(Buffer.from(item.bundle.dsseEnvelope.payload, "base64").toString("utf8")));
  const digest = Buffer.from(info.integrity.slice(7), "base64").toString("hex");
  const commits = new Set();
  for (const statement of statements) {
    check(statement.subject?.some((subject) => subject.name === `pkg:npm/${info.name}@${info.version}`
      && subject.digest?.sha512 === digest), "Provenance subject does not match npm package digest");
    const definition = statement.predicate?.buildDefinition;
    check(definition?.externalParameters?.workflow?.repository === `https://github.com/${UPSTREAM}`,
      "Provenance repository mismatch");
    for (const dependency of definition.resolvedDependencies ?? []) {
      if (dependency.uri?.startsWith(`git+https://github.com/${UPSTREAM}@`)
        && SHA.test(dependency.digest?.gitCommit)) commits.add(dependency.digest.gitCommit);
    }
  }
  check(commits.size === 1, "Missing or ambiguous registry provenance commit");
  return [...commits][0];
}

// Read only the named JSON member in memory. Never extract paths or execute package scripts.
function readSchemaTarball(compressed, integrity) {
  check(`sha512-${crypto.createHash("sha512").update(compressed).digest("base64")}` === integrity,
    "Protocol tarball integrity mismatch");
  const tar = zlib.gunzipSync(compressed, { maxOutputLength: MAX_BYTES });
  let schema;
  for (let offset = 0; offset + 512 <= tar.length;) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) break;
    const text = (start, size) => header.subarray(start, start + size).toString("utf8").replace(/\0.*$/s, "");
    const name = text(0, 100);
    const prefix = text(345, 155);
    const sizeText = text(124, 12).trim();
    check(/^[0-7]+$/.test(sizeText), "Invalid tar member size");
    const size = parseInt(sizeText, 8);
    check(size <= MAX_BYTES && offset + 512 + size <= tar.length, "Truncated tar member");
    if (!prefix && name === "package/protocol.schema.json") {
      check(!schema && [0, 48].includes(header[156]), "Duplicate or non-file protocol schema");
      schema = JSON.parse(tar.subarray(offset + 512, offset + 512 + size).toString("utf8"));
    }
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  validateSchema(schema);
  return schema;
}

function validateSchema(schema) {
  check(schema && typeof schema === "object" && !Array.isArray(schema), "Missing protocol schema");
  for (const name of ["ConnectParams", "RequestFrame", "ResponseFrame", "EventFrame"]) {
    check(schema.definitions?.[name], `Missing protocol definition ${name}`);
  }
  check(schema.methods && Object.keys(schema.methods).length > 0, "Missing method authorization metadata");
}

function schemaChanges(before, after) {
  if (!before) return [{ kind: "baseline-review", path: "/", status: "pending" }];
  const changes = [];
  const annotations = new Set(["description", "title", "examples", "$comment"]);
  function visit(old, next, pointer, required = []) {
    if (hash(old ?? null) === hash(next ?? null)) return;
    if (old && next && typeof old === "object" && typeof next === "object"
      && !Array.isArray(old) && !Array.isArray(next)) {
      for (const key of new Set([...Object.keys(old), ...Object.keys(next)])) {
        if (annotations.has(key)) continue;
        const location = `${pointer}/${key.replace(/~/g, "~0").replace(/\//g, "~1")}`;
        if (!Object.hasOwn(next, key)) changes.push({ kind: "compatibility-review", path: location, status: "pending" });
        else if (!Object.hasOwn(old, key)) {
          const optional = pointer.endsWith("/properties") && !required.includes(key);
          changes.push({
            kind: optional ? "additive-field" : "feature-review", path: location,
            status: optional ? "informational" : "pending",
          });
        } else visit(old[key], next[key], location, key === "properties" ? next.required ?? [] : []);
      }
    } else changes.push({ kind: "compatibility-review", path: pointer || "/", status: "pending" });
  }
  visit(before, after, "");
  return changes;
}

function sourceChanges(before, after) {
  return [...new Set([...Object.keys(before ?? {}), ...Object.keys(after)])].sort()
    .filter((file) => before?.[file]?.sha !== after[file]?.sha)
    .map((file) => ({
      path: file, group: (after[file] ?? before[file]).group, status: "pending",
      kind: !before ? "baseline-review" : !before[file] ? "added" : !after[file] ? "removed" : "changed",
      before: before?.[file]?.sha ?? null, after: after[file]?.sha ?? null,
    }));
}

function fingerprint(report) {
  // Commits, timestamps and package versions are provenance, not change triggers.
  return hash({
    main: report.main.files, released: report.released.files,
    schema: report.protocol.schema, policy: 1,
  });
}

async function collect({ token, previous, outputDir }) {
  const [head, gatewayMetadata, protocolMetadata] = await Promise.all([
    json(`https://api.github.com/repos/${UPSTREAM}/commits/main`, token),
    json("https://registry.npmjs.org/openclaw/latest"),
    json("https://registry.npmjs.org/@openclaw%2fgateway-protocol/latest"),
  ]);
  const gateway = packageInfo(gatewayMetadata, "openclaw");
  const protocol = packageInfo(protocolMetadata, "@openclaw/gateway-protocol");
  const attestationUrl = `https://registry.npmjs.org/-/npm/v1/attestations/openclaw@${gateway.version}`;
  const attestation = await json(attestationUrl);
  const releaseCommit = provenanceCommit(attestation, gateway);
  const tarballUrl = protocolMetadata.dist.tarball;
  check(tarballUrl === `https://registry.npmjs.org/@openclaw/gateway-protocol/-/gateway-protocol-${protocol.version}.tgz`,
    "Unexpected protocol tarball URL");
  const [main, released, tarball] = await Promise.all([
    sourceTrack(head.sha, token), sourceTrack(releaseCommit, token), download(tarballUrl),
  ]);
  const schema = readSchemaTarball(tarball, protocol.integrity);
  const report = {
    version: 1, observedAt: new Date().toISOString(),
    baseline: previous ? { fingerprint: previous.fingerprint, mainCommit: previous.main.commit,
      releasedCommit: previous.released.commit } : null,
    main, released: { ...released, package: gateway, attestationUrl, attestationHash: hash(attestation) },
    protocol: { ...protocol, tarballUrl, tarballHash: hash(tarball), schema, schemaHash: hash(schema) },
    changes: {
      main: sourceChanges(previous?.main.files, main.files),
      released: sourceChanges(previous?.released.files, released.files),
      schema: schemaChanges(previous?.protocol.schema, schema),
    },
  };
  report.fingerprint = fingerprint(report);
  report.evidenceHash = hash({ baseline: report.baseline, changes: report.changes });
  validateReport(report);
  fs.mkdirSync(outputDir, { recursive: true });
  fs.writeFileSync(path.join(outputDir, "report.json"), JSON.stringify(report, null, 2));
  fs.writeFileSync(path.join(outputDir, "report.md"), render(report));
  return report;
}

function validateReport(report) {
  check(report.version === 1 && HASH.test(report.fingerprint), "Unsupported monitor report");
  for (const track of [report.main, report.released]) {
    check(SHA.test(track?.commit) && track.files && Object.keys(track.files).length <= 10000,
      "Invalid source track");
    for (const [file, entry] of Object.entries(track.files)) {
      check(/^[a-zA-Z0-9_./@()[\]-]+$/.test(file) && !file.split("/").includes("..")
        && watchGroup(file) === entry.group && GROUPS.includes(entry.group) && SHA.test(entry.sha),
      "Invalid source inventory entry");
    }
    for (const group of GROUPS) check(Object.values(track.files).some((entry) => entry.group === group),
      `Report missing watch group ${group}`);
  }
  for (const [info, name] of [[report.released.package, "openclaw"], [report.protocol, "@openclaw/gateway-protocol"]]) {
    packageInfo({ ...info, dist: { integrity: info.integrity } }, name);
  }
  check(HASH.test(report.released.attestationHash) && HASH.test(report.protocol.tarballHash)
    && report.protocol.schemaHash === hash(report.protocol.schema), "Invalid provenance hashes");
  validateSchema(report.protocol.schema);
  check(report.fingerprint === fingerprint(report), "Report fingerprint mismatch");
  check(report.baseline === null || (HASH.test(report.baseline?.fingerprint)
    && SHA.test(report.baseline.mainCommit) && SHA.test(report.baseline.releasedCommit)), "Invalid review baseline");
  check(report.evidenceHash === hash({ baseline: report.baseline, changes: report.changes }), "Report evidence mismatch");
  for (const track of ["main", "released"]) {
    const changes = report.changes?.[track];
    check(Array.isArray(changes) && changes.length <= 20000, "Invalid source deltas");
    for (const change of changes) {
      check(watchGroup(change.path) === change.group && GROUPS.includes(change.group)
        && ["baseline-review", "added", "removed", "changed"].includes(change.kind)
        && change.status === "pending"
        && (change.before === null || SHA.test(change.before))
        && (change.after === null || SHA.test(change.after))
        && (report[track].files[change.path]?.sha ?? null) === change.after, "Invalid source delta");
    }
  }
  check(Array.isArray(report.changes?.schema) && report.changes.schema.length <= 100000, "Invalid schema deltas");
  for (const change of report.changes.schema) {
    check(typeof change.path === "string" && change.path.startsWith("/") && change.path.length <= 4096
      && ["baseline-review", "compatibility-review", "feature-review", "additive-field"].includes(change.kind)
      && change.status === (change.kind === "additive-field" ? "informational" : "pending"), "Invalid schema delta");
  }
}

function render(report) {
  // Only trusted labels, validated identities and hashes enter write-authorized publication.
  const lines = [
    "## Upstream Gateway protocol observation",
    "",
    `Fingerprint: \`${report.fingerprint}\``,
    `Early warning: openclaw/openclaw main \`${report.main.commit}\`.`,
    `Released compatibility: openclaw@${report.released.package.version}, source \`${report.released.commit}\`.`,
    `Published schema: @openclaw/gateway-protocol@${report.protocol.version}, SHA256 \`${report.protocol.schemaHash}\`.`,
    `Gateway package integrity: \`${report.released.package.integrity}\`.`,
    `Protocol package integrity: \`${report.protocol.integrity}\`.`,
    `Protocol tarball SHA256: \`${report.protocol.tarballHash}\`.`,
    `Registry provenance SHA256: \`${report.released.attestationHash}\`.`,
    `Review baseline: ${report.baseline ? `\`${report.baseline.fingerprint}\`` : "none (full baseline audit required)"}.`,
    "",
    "The schema package version is independent of the wire protocol integer and may differ from the Gateway release.",
    "Source commits come from registry-supplied, digest-bound provenance, not independently verified attestation signatures.",
    "",
    "| Track | Watched sources | Content fingerprint |",
    "| --- | ---: | --- |",
  ];
  for (const name of ["main", "released"]) {
    lines.push(`| ${name} | ${Object.keys(report[name].files).length} | \`${hash(report[name].files)}\` |`);
  }
  lines.push("", "Classification: **pending review**, not a finding of breakage.",
    "New optional fields are not automatically breaking. Open agent stream/data payloads require producer and reference-client review.",
    "Full source inventory, blob hashes and classified deltas are in the workflow artifact.");
  return lines.join("\n") + "\n";
}

module.exports = {
  collect, sourceInventory, sourceChanges, schemaChanges, provenanceCommit, packageInfo,
  readSchemaTarball, validateReport, render, fingerprint, hash, watchGroup,
};

if (require.main === module) {
  const previousPath = process.env.PREVIOUS_REPORT;
  const previous = previousPath && fs.existsSync(previousPath)
    ? JSON.parse(fs.readFileSync(previousPath, "utf8")) : undefined;
  if (previous) {
    validateReport(previous);
    if (process.env.BASELINE_FINGERPRINT) check(previous.fingerprint === process.env.BASELINE_FINGERPRINT,
      "Baseline artifact does not match reviewed issue fingerprint");
  } else check(!process.env.BASELINE_FINGERPRINT, "Reviewed baseline artifact is missing");
  collect({ token: process.env.GH_TOKEN, previous, outputDir: process.env.OUTPUT_DIR ?? "upstream-protocol-report" })
    .then((report) => console.log(render(report)))
    .catch((error) => { console.error(error.message); process.exitCode = 1; });
}
