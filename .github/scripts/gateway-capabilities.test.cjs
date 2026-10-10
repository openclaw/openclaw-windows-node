"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const capability = require("./gateway-capabilities.cjs");
const monitor = require("./upstream-gateway-protocol.cjs");
const publisher = require("./upstream-gateway-publish.cjs");
const head = "a".repeat(40);
const integrity = `sha512-${Buffer.alloc(64).toString("base64")}`;
const schema = {
  methods: Object.fromEntries(["list", "get", "resolve", "request", "waitAnswer"]
    .map((verb) => [`question.${verb}`, { scope: "operator.questions" }])),
  definitions: { QuestionResolvedEvent: {} },
};
const local = { head, dirty: false, files: {
  "src/Client.cs": 'public Task Health() { return SendTrackedRequestAsync("health"); }',
  "tests/ClientTests.cs": "[Fact] public void Health() { Assert.True(client.IsConnected); }",
} };
const assess = (options = {}) => capability.assess({
  schema, local, upstreamHash: "b".repeat(64), hash: monitor.hash, ...options,
});
const publication = {
  schema_version: 1, package_name: "openclaw", package_version: "2026.9.9",
  package_integrity: integrity, source_repository: "openclaw/openclaw", source_commit: head,
};

test("first-run missing Q&A is explicit without baseline or upstream delta", () => {
  const report = assess();
  assert.equal(report.families[0].id, "question-answer");
  assert.equal(report.families[0].status, "pending");
  assert.deepEqual(report.pendingGaps, ["question-answer"]);
  for (const name of ["list", "get", "resolve"])
    assert.equal(report.assessments.find((item) => item.name === `question.${name}`).status, "pending");
  assert.ok(report.families[0].obligations.some((item) => item.includes("multiselect")));
  assert.match(capability.renderAssessment(report), /question-answer.*pending/);
  assert.match(capability.renderAssessment(report), /not confirmed defects/);
  assert.match(publisher.INSTRUCTIONS, /at most ONE/);
  assert.deepEqual(assess(), report, "unchanged upstream must retain the same pending findings");
});

test("producer methods have scoped intentional-unsupported rationale, not blanket question exemption", () => {
  const report = assess();
  for (const name of ["request", "waitAnswer"]) {
    const item = report.assessments.find((item) => item.name === `question.${name}`);
    assert.equal(item.status, "intentionally-unsupported");
    assert.match(item.reason, /producer/i);
    assert.match(item.reason, /Fallback:/);
  }
  const changed = structuredClone(schema);
  changed.methods["question.request"].scope = "operator.admin";
  assert.equal(assess({ schema: changed }).assessments.find((item) => item.name === "question.request").status, "pending");
});

test("new unknown methods and event definitions are pending, not automatically required or supported", () => {
  const changed = structuredClone(schema);
  changed.methods["future.answer"] = { scope: "operator.future" };
  changed.definitions.FutureRequestedEvent = {};
  const report = assess({ schema: changed });
  assert.equal(report.assessments.find((item) => item.name === "future.answer").status, "pending");
  assert.equal(report.assessments.find((item) => item.name === "FutureRequestedEvent").status, "pending");
  assert.match(report.assessments.find((item) => item.name === "future.answer").reason, /Unknown relevance/);
  const unknownOnly = assess({ schema: { methods: { "future.answer": {} }, definitions: { FutureEvent: {} } } });
  assert.deepEqual(unknownOnly.pendingGaps, []);
  assert.doesNotMatch(capability.renderAssessment(unknownOnly), /<!-- windows-capability-pending -->/);
});

test("comments, dead strings, DTOs and even call candidates cannot establish supported behavior", () => {
  const file = [
    '// SendTrackedRequestAsync("question.list");',
    '/* case "question.requested": */',
    'var dead = """ SendTrackedRequestAsync("question.get") """;',
    'var dead2 = "case \\"question.resolved\\":";',
    'private Task NeverCalled() { return SendTrackedRequestAsync("question.resolve"); }',
    'class QuestionAnswers { public string Answer { get; set; } }',
  ].join("\n");
  const evidence = capability.wiringCandidates({ "src/Client.cs": file });
  assert.deepEqual(evidence.map((item) => item.name), ["question.resolve"]);
  assert.ok(assess({ local: { ...local, files: { "src/Client.cs": file } } }).assessments
    .every((item) => item.status !== "supported"));
});

test("reviewed source/test evidence resolves a surface, and any evidence change invalidates support", () => {
  const initial = assess();
  const supportPolicy = structuredClone(capability.policy);
  supportPolicy.decisions.push({
    surface: "method:question.resolve", status: "supported", inputHash: initial.inputHash,
    rationale: "Reviewed reachable dispatch, handler, answer construction and real-client behavior.",
    evidence: ["dispatch", "handler", "construction", "behavioral-test"].map((kind) => {
      const file = kind === "behavioral-test" ? "tests/ClientTests.cs" : "src/Client.cs";
      return { kind, file, sha256: initial.inventory[file] };
    }),
  });
  const resolved = assess({ supportPolicy });
  assert.equal(resolved.assessments.find((item) => item.name === "question.resolve").status, "supported");
  assert.equal(resolved.families[0].status, "pending", "one reviewed RPC cannot imply full feature support");
  for (const change of [
    { local: { ...local, files: { ...local.files, "src/Client.cs": "changed" } } },
    { local: { ...local, files: { ...local.files, "tests/ClientTests.cs": "changed" } } },
    { upstreamHash: "c".repeat(64) },
  ]) assert.equal(assess({ supportPolicy, ...change }).assessments.find((item) => item.name === "question.resolve").status, "pending");
  supportPolicy.decisions.at(-1).evidence.pop();
  assert.equal(assess({ supportPolicy }).assessments.find((item) => item.name === "question.resolve").status, "pending");
});

test("closing a still-pending finding reopens the same issue without bypassing active backpressure", () => {
  const fingerprint = "d".repeat(64);
  const closed = { number: 1, state: "closed", body: publisher.marker(fingerprint) + "\nreview" };
  assert.equal(publisher.decide([closed], fingerprint, true).action, "reopen");
  assert.equal(publisher.decide([closed], fingerprint, false).action, "reviewed");
  const active = { number: 2, state: "open", body: publisher.marker("e".repeat(64)) + "\nreview" };
  assert.equal(publisher.decide([closed, active], fingerprint, true).action, "defer");
});

test("publication payload rejects moving tags, unsafe versions, unknown fields, repositories and digests", () => {
  assert.deepEqual(monitor.validatePublication(publication), publication);
  for (const invalid of [
    { package_version: "latest" }, { package_version: "2026.9.9/../../latest" },
    { package_name: "attacker" }, { source_repository: "attacker/openclaw" },
    { source_commit: "main" }, { package_integrity: "sha1-bad" }, { schema_version: 2 },
    { url: "https://attacker.example" },
  ]) assert.throws(() => monitor.validatePublication({ ...publication, ...invalid }));
});

test("exact publication pins only its own package; other track remains independently latest", () => {
  for (const name of ["openclaw", "@openclaw/gateway-protocol"]) {
    const payload = { ...publication, package_name: name };
    assert.ok(monitor.metadataUrl(name, payload).endsWith("/2026.9.9"));
    const other = name === "openclaw" ? "@openclaw/gateway-protocol" : "openclaw";
    assert.ok(monitor.metadataUrl(other, payload).endsWith("/latest"));
  }
});

test("exact publication independently binds registry integrity, provenance and GitHub commit", async () => {
  const metadata = { name: "openclaw", version: "2026.9.9", dist: { integrity } };
  const statement = {
    subject: [{ name: "pkg:npm/openclaw@2026.9.9", digest: { sha512: Buffer.alloc(64).toString("hex") } }],
    predicate: { buildDefinition: {
      externalParameters: { workflow: { repository: "https://github.com/openclaw/openclaw" } },
      resolvedDependencies: [{ uri: "git+https://github.com/openclaw/openclaw@release", digest: { gitCommit: head } }],
    } },
  };
  const calls = [];
  const load = async (url) => {
    calls.push(url);
    return url.startsWith("https://api.github.com/") ? { sha: head } : { attestations: [{
      predicateType: "https://slsa.dev/provenance/v1",
      bundle: { dsseEnvelope: { payload: Buffer.from(JSON.stringify(statement)).toString("base64") } },
    }] };
  };
  await monitor.verifyPublication(publication, metadata, undefined, load);
  assert.equal(calls.length, 2);
  assert.ok(calls.every((url) => !url.includes("/latest")));
  await assert.rejects(monitor.verifyPublication(publication, { ...metadata, version: "2026.9.10" }, undefined, load), /registry/);
  await assert.rejects(monitor.verifyPublication({ ...publication, source_commit: "c".repeat(40) }, metadata, undefined, load), /provenance/);
  await assert.rejects(monitor.verifyPublication(publication, metadata, undefined,
    async (url) => url.startsWith("https://api.github.com/") ? { sha: "c".repeat(40) } : load(url)), /commit mismatch/);
  statement.subject[0].name = "pkg:npm/%40openclaw/gateway-protocol@2026.9.9";
  await monitor.verifyPublication({ ...publication, package_name: "@openclaw/gateway-protocol" },
    { ...metadata, name: "@openclaw/gateway-protocol" }, undefined, load);
  statement.subject[0].name = "pkg:npm/%40attacker/gateway-protocol@2026.9.9";
  await assert.rejects(monitor.verifyPublication({ ...publication, package_name: "@openclaw/gateway-protocol" },
    { ...metadata, name: "@openclaw/gateway-protocol" }, undefined, load), /subject/);
});

test("local content and exact publication participate in dedup, but unrelated local head changes do not", () => {
  const report = { main: { files: {} }, released: { files: {} }, protocol: { schema }, publication };
  report.capabilities = monitor.assessLocal(report, local);
  const initial = monitor.fingerprint(report);
  assert.equal(monitor.fingerprint(structuredClone(report)), initial);
  const next = structuredClone(report);
  next.capabilities = monitor.assessLocal(next, { ...local, head: "c".repeat(40) });
  assert.equal(monitor.fingerprint(next), initial);
  next.capabilities = monitor.assessLocal(next, { ...local, files: { ...local.files, "src/Client.cs": "changed" } });
  assert.notEqual(monitor.fingerprint(next), initial);
  const event = structuredClone(report);
  event.publication.package_version = "2026.9.10";
  assert.notEqual(monitor.fingerprint(event), initial);
});

test("publication and report-only paths preserve read/write separation and no payload shell interpolation", () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8");
  assert.match(workflow, /repository_dispatch:\s+types: \[gateway-protocol-published\]/);
  assert.doesNotMatch(workflow, /\$\{\{\s*github\.event\.client_payload/);
  const observe = workflow.split("\n  observation-failure:")[0];
  assert.doesNotMatch(observe, /secrets\.|: write/);
  assert.equal((workflow.match(/!inputs.report_only/g) ?? []).length, 2);
  const collector = fs.readFileSync(path.join(__dirname, "upstream-gateway-protocol.cjs"), "utf8");
  assert.doesNotMatch(collector, /github\.request|COPILOT_GITHUB_TOKEN/);
});
