"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const zlib = require("node:zlib");
const monitor = require("./upstream-gateway-protocol.cjs");
const publisher = require("./upstream-gateway-publish.cjs");
const local = require("./gateway-capabilities.cjs").readLocal(path.resolve(__dirname, "../.."));
let cachedCapabilities;

const sha = "a".repeat(40);
const otherSha = "b".repeat(40);
const integrity = `sha512-${Buffer.alloc(64).toString("base64")}`;
const core = { info() {} };
const context = {
  repo: { owner: "openclaw", repo: "openclaw-windows-node" }, runId: 123,
  eventName: "schedule", ref: "refs/heads/main",
  payload: { repository: { default_branch: "main" } },
};
const paths = [
  "packages/gateway-protocol/src/schema/protocol-schemas.ts",
  "src/gateway/methods/registry.ts",
  "src/gateway/server-chat.ts",
  "src/infra/agent-events.ts",
  "ui/src/app/question-prompt.ts",
];

function schema() {
  return {
    definitions: Object.fromEntries(["ConnectParams", "RequestFrame", "ResponseFrame", "EventFrame"]
      .map((name) => [name, { type: "object", properties: {}, required: [] }])),
    methods: { "chat.send": { scope: "operator.write" } },
  };
}

function report() {
  const files = monitor.sourceInventory({
    truncated: false, tree: paths.map((file) => ({ path: file, sha, type: "blob" })),
  });
  const doc = schema();
  doc.methods["question.list"] = { scope: "operator.questions" };
  const value = {
    version: 2, observedAt: "2026-10-09T00:00:00Z",
    baseline: null, changes: { main: [], released: null, schema: [] }, publicationProvenance: null,
    main: { commit: sha, files }, released: {
      commit: null, files: null, sourceCoverage: "unverified", workflowSha: sha, declaredSourceCommit: null,
      package: { name: "openclaw", version: "2026.9.9", integrity },
      attestationHash: "0".repeat(64),
    },
    protocol: {
      name: "@openclaw/gateway-protocol", version: "2026.9.9", integrity: tarball(doc).integrity, schema: doc,
      schemaHash: monitor.hash(doc), tarballHash: monitor.hash(tarball(doc).compressed),
    },
  };
  cachedCapabilities ??= monitor.assessLocal(value, local);
  value.capabilities = structuredClone(cachedCapabilities);
  value.fingerprint = monitor.fingerprint(value);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  return value;
}

function issue(overrides = {}) {
  return {
    number: 42, body: `${publisher.marker(report().fingerprint)}\n<!-- upstream-source-coverage:v2 -->\nreview`, state: "open",
    labels: [{ name: publisher.LABEL }], assignees: [],
    created_at: new Date().toISOString(), ...overrides,
  };
}

function mock(routes) {
  routes = { "GET /repos/{owner}/{repo}/pulls": [], ...routes };
  const calls = [];
  return {
    calls,
    async request(route, args) {
      calls.push({ route, args });
      assert.ok(Object.hasOwn(routes, route), `Unexpected API request: ${route}`);
      const handler = routes[route];
      return { data: typeof handler === "function" ? await handler(args) : handler };
    },
  };
}

test("notification readiness warns without writes when the sender has not delivered", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs": { workflow_runs: [] },
  });
  const result = await publisher.notificationReadiness({ github, context });
  assert.equal(result.status, "unverified");
  assert.match(result.message, /sender must be merged/);
  assert.match(result.message, /not a Gateway compatibility failure/);
  assert.equal(github.calls.length, 1);
  assert.equal(github.calls[0].args.event, "repository_dispatch");
  assert.equal(github.calls[0].args.branch, "main");
  assert.equal(github.calls[0].args.per_page, 10);
});

test("current validated publication observation establishes receipt without querying history", async () => {
  const github = mock({});
  const result = await publisher.notificationReadiness({
    github, context: { ...context, eventName: "repository_dispatch",
      payload: { ...context.payload, action: "gateway-protocol-published" } },
  });
  assert.equal(result.status, "received");
  assert.equal(result.runUrl, "https://github.com/openclaw/openclaw-windows-node/actions/runs/123");
  assert.match(result.message, /not sender identity, continued delivery or package-source binding/);
  assert.equal(github.calls.length, 0);
});

test("notification receipt uses the observation job, not later remediation failure", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs": {
      workflow_runs: [{ id: 12, event: "repository_dispatch", head_branch: "main",
        path: ".github/workflows/upstream-gateway-protocol.yml", conclusion: "failure" }],
    },
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs": {
      jobs: [{ name: "observe", conclusion: "success" }, { name: "publish", conclusion: "failure" }],
    },
  });
  const result = await publisher.notificationReadiness({ github, context });
  assert.equal(result.status, "received");
  assert.match(result.runUrl, /\/12$/);
  assert.equal(github.calls[1].args.filter, "latest");
});

test("untrusted, skipped and failed observations do not prove notification readiness", async () => {
  const run = { id: 12, event: "repository_dispatch", head_branch: "main",
    path: ".github/workflows/upstream-gateway-protocol.yml" };
  const github = mock({
    "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs": {
      workflow_runs: [
        { ...run, event: "schedule" }, { ...run, head_branch: "feature" },
        { ...run, path: ".github/workflows/other.yml" }, { ...run, id: "untrusted" }, run,
      ],
    },
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs": {
      jobs: [{ name: "observe", conclusion: "failure" }, { name: "observe", conclusion: "skipped" },
        { name: "publish", conclusion: "success" }],
    },
  });
  const result = await publisher.notificationReadiness({ github, context });
  assert.equal(result.status, "unverified");
  assert.equal(github.calls.length, 2);
});

test("notification history failures are explicit warnings rather than polling failures", async () => {
  for (const response of [
    () => { throw Object.assign(new Error("private upstream error"), { status: 403 }); },
    {},
  ]) {
    const github = mock({
      "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs": response,
    });
    const result = await publisher.notificationReadiness({ github, context });
    assert.equal(result.status, "unknown");
    assert.match(result.message, /lookup failed/);
    assert.doesNotMatch(result.message, /private upstream error/);
  }
});

test("notification readiness history is bounded even if GitHub returns extra runs", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs": {
      workflow_runs: Array.from({ length: 15 }, (_, index) => ({
        id: index + 1, event: "repository_dispatch", head_branch: "main",
        path: ".github/workflows/upstream-gateway-protocol.yml",
      })),
    },
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs": { jobs: [] },
  });
  assert.equal((await publisher.notificationReadiness({ github, context })).status, "unverified");
  assert.equal(github.calls.length, 11);
});

test("workflow preserves notification warnings in report artifacts after validated collection", () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8");
  const start = workflow.indexOf("- name: Report notification readiness");
  const end = workflow.indexOf("- name: Summarize observation");
  assert.ok(start > workflow.indexOf("run: node .github/scripts/upstream-gateway-protocol.cjs"));
  assert.ok(end > start);
  const step = workflow.slice(start, end);
  assert.match(step, /core\.warning\(readiness\.message\)/);
  assert.doesNotMatch(step, /setFailed|exit 1|continue-on-error/);
  assert.match(step, /appendFileSync[\s\S]*"report\.md"/);
  assert.match(step, /"notification-readiness\.json"/);
});

function tarball(document, member = "package/protocol.schema.json", type = "0") {
  const content = Buffer.from(JSON.stringify(document));
  const header = Buffer.alloc(512);
  header.write(member, 0);
  header.write(content.length.toString(8).padStart(11, "0") + "\0", 124);
  header.write(type, 156);
  const compressed = zlib.gzipSync(Buffer.concat([
    header, content, Buffer.alloc((512 - content.length % 512) % 512), Buffer.alloc(1024),
  ]));
  return { compressed, integrity: `sha512-${crypto.createHash("sha512").update(compressed).digest("base64")}` };
}

test("inventory covers schema, authorization, behavior, producers and reference clients, excluding tests", () => {
  const files = monitor.sourceInventory({
    truncated: false, tree: [...paths, "src/gateway/foo.test.ts", "ui/src/fixtures/chat.ts"]
      .map((file) => ({ path: file, sha, type: "blob" })),
  });
  assert.deepEqual(Object.keys(files).sort(), paths.sort());
  assert.equal(monitor.watchGroup("src/gateway/server-methods/new-feature.ts"), "gateway");
  assert.equal(monitor.watchGroup("src/auto-reply/reply/new-stream.ts"), "agent-producers");
});

test("truncated trees, vanished groups and unexpected paths fail instead of reporting compatibility", () => {
  assert.throws(() => monitor.sourceInventory({ truncated: true, tree: [] }), /Incomplete/);
  assert.throws(() => monitor.sourceInventory({ truncated: false, tree: [] }), /disappeared/);
  assert.throws(() => monitor.sourceInventory({
    truncated: false, tree: [{ path: "src/gateway/../../escape.ts", sha, type: "blob" }],
  }), /path/);
});

test("source diffs retain added, removed and changed producers even without schema changes", () => {
  const before = { a: { group: "gateway", sha }, b: { group: "schema", sha } };
  const after = { a: { group: "gateway", sha: otherSha }, c: { group: "gateway", sha } };
  assert.deepEqual(monitor.sourceChanges(before, after).map((change) => change.kind), ["changed", "removed", "added"]);
  assert.ok(monitor.sourceChanges(undefined, after).every((change) => change.kind === "baseline-review"));
});

test("optional field additions are informational, required additions need compatibility review", () => {
  const before = { type: "object", properties: { id: { type: "string" } }, required: ["id"] };
  const after = structuredClone(before);
  after.properties.optional = { type: "object", required: ["nested"], properties: { nested: { type: "string" } } };
  assert.deepEqual(monitor.schemaChanges(before, after), [
    { kind: "additive-field", path: "/properties/optional", status: "informational" },
  ]);
  after.required.push("optional");
  assert.ok(monitor.schemaChanges(before, after).every((change) => change.status === "pending"));
  assert.ok(monitor.schemaChanges(before, after).some((change) => change.path === "/required"));
});

test("schema removal, enum narrowing, auth changes and new question methods stay pending, never silently supported", () => {
  const before = schema();
  before.definitions.EventFrame.properties.state = { enum: ["active", "done"] };
  const after = structuredClone(before);
  after.definitions.EventFrame.properties.state.enum = ["done"];
  after.methods["chat.send"].scope = "operator.admin";
  after.methods["question.resolve"] = { scope: "operator.questions" };
  delete after.definitions.RequestFrame;
  const changes = monitor.schemaChanges(before, after);
  assert.equal(changes.length, 4);
  assert.ok(changes.every((change) => change.status === "pending"));
  assert.ok(changes.some((change) => change.kind === "feature-review"));
});

test("schema annotation-only changes do not claim compatibility breakage", () => {
  assert.deepEqual(monitor.schemaChanges({ type: "string", description: "old" }, { type: "string", description: "new" }), []);
  assert.equal(monitor.schemaChanges(null, schema())[0].kind, "baseline-review");
});

test("schema annotation-named wire fields remain classified", () => {
  const before = { type: "object", description: "old prose", properties: {
    description: { type: "string" }, title: { type: "string" },
  } };
  const after = structuredClone(before);
  after.description = "new prose";
  after.properties.description.type = "number";
  delete after.properties.title;
  assert.deepEqual(monitor.schemaChanges(before, after).map((item) => item.path),
    ["/properties/description/type", "/properties/title"]);
});

test("package tar is digest verified and read in memory; links, missing schema and corruption fail", () => {
  const good = tarball(schema());
  assert.deepEqual(monitor.readSchemaTarball(good.compressed, good.integrity), schema());
  assert.throws(() => monitor.readSchemaTarball(good.compressed, integrity), /integrity/);
  for (const candidate of [tarball(schema(), "../protocol.schema.json"), tarball(schema(), "package/protocol.schema.json", "2")]) {
    assert.throws(() => monitor.readSchemaTarball(candidate.compressed, candidate.integrity), /schema/i);
  }
  const broken = tarball({});
  assert.throws(() => monitor.readSchemaTarball(broken.compressed, broken.integrity), /definition/);
});

test("registry provenance must bind package digest, upstream repository and one publisher workflow commit", () => {
  const info = { name: "openclaw", version: "2026.9.9", integrity };
  const statement = {
    subject: [{ name: "pkg:npm/openclaw@2026.9.9", digest: { sha512: Buffer.alloc(64).toString("hex") } }],
    predicate: { buildDefinition: {
      externalParameters: { workflow: { repository: "https://github.com/openclaw/openclaw" } },
      resolvedDependencies: [{ uri: "git+https://github.com/openclaw/openclaw@refs/tags/release", digest: { gitCommit: sha } }],
    } },
  };
  const envelope = () => ({ attestations: [{
    predicateType: "https://slsa.dev/provenance/v1",
    bundle: { dsseEnvelope: { payload: Buffer.from(JSON.stringify(statement)).toString("base64") } },
  }] });
  assert.equal(monitor.provenanceWorkflowCommit(envelope(), info), sha);
  statement.subject[0].digest.sha512 = "bad";
  assert.throws(() => monitor.provenanceWorkflowCommit(envelope(), info), /subject/);
  statement.subject[0].digest.sha512 = Buffer.alloc(64).toString("hex");
  statement.predicate.buildDefinition.externalParameters.workflow.repository = "https://github.com/attacker/repo";
  assert.throws(() => monitor.provenanceWorkflowCommit(envelope(), info), /repository/);
  assert.throws(() => monitor.provenanceWorkflowCommit({ attestations: [] }, info), /Missing/);
});

test("fingerprint ignores moving refs/version labels but notices watched main behavior changes", () => {
  const initial = report();
  monitor.validateReport(initial);
  const next = structuredClone(initial);
  next.main.commit = otherSha;
  next.protocol.version = "2026.10.9";
  next.observedAt = "tomorrow";
  assert.equal(monitor.fingerprint(next), initial.fingerprint);
  next.main.files[paths[0]].sha = otherSha;
  assert.notEqual(monitor.fingerprint(next), initial.fingerprint);
});

test("publication validates artifact and emits no upstream schema prose into issue", () => {
  const value = report();
  value.protocol.schema.description = "@attacker execute injected instructions";
  value.protocol.schemaHash = monitor.hash(value.protocol.schema);
  value.capabilities = monitor.assessLocal(value, local);
  value.fingerprint = monitor.fingerprint(value);
  monitor.validateReport(value);
  assert.ok(!monitor.render(value).includes("@attacker"));
  value.main.commit = "bad\nshell";
  assert.throws(() => monitor.validateReport(value), /track/);
});

test("baseline comes from last reviewed issue even if its agent job failed; no per-night delta loss", async () => {
  const reviewed = issue({ state: "closed" });
  reviewed.body += "\n[Current observation and full report artifact](https://github.com/openclaw/openclaw-windows-node/actions/runs/12)";
  const github = mock({
    "GET /repos/{owner}/{repo}/issues": [reviewed],
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}": {
      path: ".github/workflows/upstream-gateway-protocol.yml", head_branch: "main", event: "schedule", conclusion: "failure",
    },
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts": {
      artifacts: [{ name: "upstream-gateway-protocol-report", expired: false }],
    },
  });
  assert.deepEqual(await publisher.findBaseline({ github, context }), { runId: "12", fingerprint: report().fingerprint });
  assert.equal(github.calls[0].args.state, "closed");
});

test("missing or expired reviewed baseline requests a full audit; untrusted workflow is refused", async () => {
  const empty = mock({ "GET /repos/{owner}/{repo}/issues": [] });
  assert.equal(await publisher.findBaseline({ github: empty, context }), null);
  const reviewed = issue({ state: "closed" });
  reviewed.body += "\n[Current observation and full report artifact](https://github.com/openclaw/openclaw-windows-node/actions/runs/12)";
  const routes = {
    "GET /repos/{owner}/{repo}/issues": [reviewed],
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}": {
      path: ".github/workflows/upstream-gateway-protocol.yml", head_branch: "main", event: "schedule",
    },
    "GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts": {
      artifacts: [{ name: "upstream-gateway-protocol-report", expired: true }],
    },
  };
  assert.equal(await publisher.findBaseline({ github: mock(routes), context }), null);
  routes["GET /repos/{owner}/{repo}/actions/runs/{run_id}"].event = "pull_request";
  await assert.rejects(publisher.findBaseline({ github: mock(routes), context }), /Untrusted/);
});

test("closing pending Windows gaps cannot advance the reviewed upstream baseline", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/issues": [issue({
      state: "closed", body: `${publisher.marker(report().fingerprint)}\n<!-- upstream-source-coverage:v2 -->\n<!-- windows-capability-pending -->`,
    })],
  });

  assert.equal(await publisher.findBaseline({ github, context }), null);
  assert.equal(github.calls.length, 1);
});

test("changed or malformed agent-facing deltas cannot pass artifact validation", () => {
  const value = report();
  value.changes.main.push({ path: "bad", kind: "supported" });
  assert.throws(() => monitor.validateReport(value), /evidence mismatch/);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  assert.throws(() => monitor.validateReport(value), /source delta/);
});

test("publisher refuses stale or forged local support and legacy upstream-only artifacts", async () => {
  const value = report();
  value.capabilities.assessments[0].status = "supported";
  const { assessmentHash: ignored, ...assessment } = value.capabilities;
  value.capabilities.assessmentHash = monitor.hash({ ...assessment, localHead: null, dirty: null });
  value.fingerprint = monitor.fingerprint(value);
  await assert.rejects(publisher.publish({ github: mock({}), context, core, report: value }), /evidence changed/);
  delete value.capabilities;
  value.fingerprint = monitor.fingerprint(value);
  await assert.rejects(publisher.publish({ github: mock({}), context, core, report: value }), /current Windows/);
});

test("capability evidence must bind the observed upstream tracks", () => {
  const value = report();
  value.capabilities.upstreamHash = "a".repeat(64);
  const { assessmentHash: ignored, ...assessment } = value.capabilities;
  value.capabilities.assessmentHash = monitor.hash({ ...assessment, localHead: null, dirty: null });
  value.fingerprint = monitor.fingerprint(value);
  assert.throws(() => monitor.validateReport(value), /Capability upstream evidence mismatch/);
});

test("released source is unverified and omitted, never a tooling inventory or zero-delta success", () => {
  const value = report();
  value.released.workflowSha = "2b988ee83444f08ccaf37aa5e98f370726c75f6e";
  monitor.validateReport(value);
  const text = monitor.render(value);
  assert.match(text, /Package-source binding: \*\*unverified\*\*/);
  assert.match(text, /publisher\/workflow commit/);
  assert.match(text, /source comparison omitted/);
  assert.doesNotMatch(text, /Released compatibility:.*source/);
  assert.equal(value.changes.released, null);
  for (const change of [
    { sourceCoverage: "verified" },
    { commit: value.released.workflowSha },
    { files: value.main.files },
  ]) assert.throws(() => monitor.validateReport({ ...value, released: { ...value.released, ...change } }),
    /package-source binding/);
  value.changes.released = [];
  assert.throws(() => monitor.validateReport(value), /package-source binding/);
});

test("legacy tooling-as-release observations cannot supply a reviewed source baseline or publish", async () => {
  const legacy = report();
  legacy.version = 1;
  legacy.released.commit = legacy.released.workflowSha;
  legacy.released.files = legacy.main.files;
  assert.throws(() => monitor.validateReport(legacy), /legacy source coverage/);
  const github = mock({
    "GET /repos/{owner}/{repo}/issues": [issue({ state: "closed", body: `${publisher.marker("a".repeat(64))}\nlegacy review` })],
  });
  assert.equal(await publisher.findBaseline({ github, context }), null);
  assert.equal(github.calls.length, 1);
});

test("live-shaped tooling/source discrepancy collects partial evidence without requesting a tooling tree", async () => {
  const tooling = "2b988ee83444f08ccaf37aa5e98f370726c75f6e";
  const candidate = "bcfc88812a35243893585dbeca87ca41b48272ca";
  const artifact = tarball(schema());
  const gateway = { name: "openclaw", version: "2026.9.9", dist: { integrity } };
  const protocol = { name: "@openclaw/gateway-protocol", version: "2026.9.9",
    dist: { integrity: artifact.integrity,
      tarball: "https://registry.npmjs.org/@openclaw/gateway-protocol/-/gateway-protocol-2026.9.9.tgz" } };
  const statement = {
    subject: [{ name: "pkg:npm/openclaw@2026.9.9", digest: { sha512: Buffer.alloc(64).toString("hex") } }],
    predicate: { buildDefinition: {
      externalParameters: { workflow: { repository: "https://github.com/openclaw/openclaw" } },
      resolvedDependencies: [{ uri: `git+https://github.com/openclaw/openclaw@refs/tags/release-publish/${tooling}`,
        digest: { gitCommit: tooling } }],
    } },
  };
  const envelope = { attestations: [{
    predicateType: "https://slsa.dev/provenance/v1",
    bundle: { dsseEnvelope: { payload: Buffer.from(JSON.stringify(statement)).toString("base64") } },
  }] };
  const routes = {
    "https://api.github.com/repos/openclaw/openclaw/commits/main": { sha },
    [`https://api.github.com/repos/openclaw/openclaw/commits/${candidate}`]: { sha: candidate },
    [`https://api.github.com/repos/openclaw/openclaw/git/trees/${sha}?recursive=1`]: {
      truncated: false, tree: paths.map((file) => ({ path: file, sha, type: "blob" })),
    },
    "https://registry.npmjs.org/openclaw/2026.9.9": gateway,
    "https://registry.npmjs.org/%40openclaw%2Fgateway-protocol/latest": protocol,
    "https://registry.npmjs.org/-/npm/v1/attestations/openclaw@2026.9.9": envelope,
    [protocol.dist.tarball]: artifact.compressed,
  };
  const calls = [];
  const saved = global.fetch;
  const outputDir = fs.mkdtempSync(path.join(require("node:os").tmpdir(), "gateway-source-proof-"));
  try {
    global.fetch = async (url) => {
      const key = String(url);
      calls.push(key);
      assert.ok(Object.hasOwn(routes, key), `Unexpected network call: ${key}`);
      return new Response(Buffer.isBuffer(routes[key]) ? routes[key] : JSON.stringify(routes[key]));
    };
    const publication = { schema_version: 1, package_name: "openclaw", package_version: "2026.9.9",
      package_integrity: integrity, source_repository: "openclaw/openclaw", source_commit: candidate };
    const legacy = { ...report(), version: 1 };
    const result = await monitor.collect({ outputDir, publication, previous: legacy });
    assert.equal(result.baseline, null);
    assert.equal(result.released.workflowSha, tooling);
    assert.equal(result.released.declaredSourceCommit, candidate);
    assert.equal(result.released.sourceCoverage, "unverified");
    assert.equal(result.released.files, null);
    assert.equal(result.changes.released, null);
    assert.ok(result.capabilities.assessments.length > 0);
    assert.equal(calls.filter((url) => url.includes("/git/trees/")).length, 1);
    assert.match(monitor.render(result), /Investigation only/);
    const malformed = structuredClone(result);
    malformed.publicationProvenance.declaredSourceCommit = sha;
    assert.throws(() => monitor.validateReport(malformed), /publication provenance mismatch/);
    const unannounced = structuredClone(result);
    unannounced.publication = { ...publication, package_name: "@openclaw/gateway-protocol",
      package_integrity: artifact.integrity };
    assert.throws(() => monitor.validateReport(unannounced), /Unannounced Gateway source/);
    const withoutEvent = structuredClone(result);
    withoutEvent.publication = null;
    assert.throws(() => monitor.validateReport(withoutEvent), /Unexpected source declaration/);
  } finally {
    global.fetch = saved;
    fs.rmSync(outputDir, { recursive: true });
  }
});


// Exercise the real public publisher and handoff, including immutable main and
// exact npm tarball validation. Only external transports are replaced.
function boundary(t, value = report()) {
  const artifact = tarball(value.protocol.schema);
  const state = { issues: [], pulls: [], tasks: [], comments: [], calls: [], override: null,
    tracker: { number: 1829, title: "Gateway capability tracking", state: "open", labels: [], assignees: [],
      body: "<!-- upstream-gateway-tracker:v1 -->\n<!-- upstream-gateway-owned:begin -->\nNot activated.\n<!-- upstream-gateway-owned:end -->" },
  };
  t.mock.method(global, "fetch", async (url, options) => {
    const key = String(url);
    if (key.startsWith("https://api.individual.githubcopilot.com/agents/sessions/")) {
      assert.equal(options.headers.Authorization, "Bearer personal-test-token");
      assert.equal(options.headers["Copilot-Integration-Id"], "copilot-4-cli");
      return new Response(state.resultLogs);
    }
    if (key.endsWith(".tgz")) return new Response(artifact.compressed);
    assert.ok(key.startsWith("https://registry.npmjs.org/%40openclaw%2Fgateway-protocol/"));
    const version = key.split("/").at(-1);
    return new Response(JSON.stringify({ name: "@openclaw/gateway-protocol", version,
      dist: { integrity: artifact.integrity,
        tarball: "https://registry.npmjs.org/@openclaw/gateway-protocol/-/gateway-protocol-" + version + ".tgz" } }));
  });
  const transport = async (route, args) => {
    state.calls.push({ route, args: structuredClone(args) });
    if (state.override) { const result = await state.override(route, args); if (result) return result; }
    let data;
    let status = 200;
    switch (route) {
      case "GET /repos/{owner}/{repo}/compare/{basehead}":
        assert.equal(args.repo, "openclaw");
        data = { status: "ahead", base_commit: { sha: value.main.commit } }; break;
      case "GET /repos/{owner}/{repo}/git/trees/{tree_sha}":
        data = { truncated: false, tree: Object.entries(value.main.files).map(([path, entry]) => ({ path, sha: entry.sha, type: "blob" })) }; break;
      case "GET /repos/{owner}/{repo}/issues":
        data = [...state.issues, state.tracker].filter((item) => (args.state === "all" || item.state === args.state)
          && (!args.labels || item.labels.some((label) => label.name === args.labels))); break;
      case "GET /repos/{owner}/{repo}/pulls": data = state.pulls.filter((item) => item.state === "open"); break;
      case "GET /repos/{owner}/{repo}/issues/{issue_number}":
        data = args.issue_number === 1829 ? state.tracker : state.issues.find((item) => item.number === args.issue_number); break;
      case "GET /repos/{owner}/{repo}/issues/{issue_number}/comments": data = state.comments; break;
      case "GET /repos/{owner}/{repo}/pulls/{pull_number}": data = state.pulls.find((item) => item.number === args.pull_number); break;
      case "GET /repos/{owner}/{repo}/labels/{name}": data = {}; break;
      case "POST /repos/{owner}/{repo}/issues":
        data = { number: state.issues.length + 1, title: args.title, body: args.body,
          state: "open", labels: args.labels.map((name) => ({ name })), assignees: [] };
        state.issues.push(data); status = 201; break;
      case "PATCH /repos/{owner}/{repo}/issues/{issue_number}":
        assert.equal(args.headers?.authorization, undefined, "issue writes must use the workflow token");
        data = args.issue_number === 1829 ? state.tracker : state.issues.find((item) => item.number === args.issue_number);
        if (args.body !== undefined) data.body = args.body;
        if (args.state !== undefined) data.state = args.state;
        break;
      case "GET /repos/{owner}/{repo}": data = { default_branch: "main" }; break;
      case "GET /agents/repos/{owner}/{repo}/tasks":
        assert.equal(args.headers["X-GitHub-Api-Version"], "2026-03-10");
        data = { tasks: state.tasks.filter((task) => args.state.split(",").includes(task.state)) }; break;
      case "GET /agents/repos/{owner}/{repo}/tasks/{task_id}": data = state.tasks.find((task) => task.id === args.task_id); break;
      case "POST /agents/repos/{owner}/{repo}/tasks":
        assert.equal(args.create_pull_request, false);
        assert.equal(args.base_ref, "main");
        assert.equal(args.headers.authorization, undefined, "auth belongs to the client, never a per-request header");
        assert.match(args.prompt, /independently|Independently/);
        assert.match(args.prompt, /Do not create any PR for supported/);
        data = { id: "d7832e67-83d9-4b96-93c0-59f0392494dc", state: "queued", created_at: "2026-10-22T00:00:00.123456789Z",
          artifacts: [{ provider: "github", type: "pull", data: { id: 4751512507, global_id: "" } }] };
        state.tasks.push(data); status = 201; break;
      default: assert.fail("Unexpected API request: " + route);
    }
    return { data: structuredClone(data), status };
  };
  const github = { request(route, args) {
    assert.ok(!route.includes("/agents/"), "workflow-token client must never call Agent Tasks API");
    return transport(route, args);
  } };
  const copilot = { async auth() { return { type: "token", token: "personal-test-token" }; }, request(route, args) {
    assert.ok(route.includes("/agents/") || route === "POST /graphql", "personal-token client must never read/write repository issues");
    return transport(route, args);
  } };
  return { state, github, copilot,
    publish: (reportValue = value, extra = {}) => publisher.publish({ github, context, core, report: reportValue, ...extra }),
    handoff: (issueNumber = 1829, extra = {}) => publisher.handoff({ github, copilot, context, core, report: value, issueNumber, now: Date.parse("2026-10-22T01:00:00Z"), ...extra }),
  };
}
const writes = (state) => state.calls.filter((call) => !call.route.startsWith("GET "));

const trackerState = (h) => JSON.parse(h.state.tracker.body.match(/<!-- upstream-gateway-state:v2:(.*) -->/)[1]);
function editState(h, edit) {
  const state = trackerState(h);
  edit(state);
  h.state.tracker.body = h.state.tracker.body.replace(/<!-- upstream-gateway-state:v2:.* -->/,
    "<!-- upstream-gateway-state:v2:" + JSON.stringify(state) + " -->");
}
function dispositionComment(h, result = "supported", extra = {}) {
  return { user: { id: 198982749 }, author_association: "NONE",
    body: "Evidence reviewed. <!-- upstream-gateway-disposition:v1:" + trackerState(h).current.candidateHash + ":" + result + " -->", ...extra };
}

test("public workflow uses only #1829 and separate task auth; repeated metadata is quiet", async (t) => {
  const value = report(), h = boundary(t, value);
  const first = await h.publish();
  assert.equal(first.issueNumber, 1829);
  assert.equal(first.trackerNumber, 1829);
  assert.equal((await h.handoff()).state, "queued");
  await h.publish(value, { createWork: false });
  const count = writes(h.state).length;
  const next = structuredClone(value);
  next.protocol.version = "2026.10.10";
  next.observedAt = "2026-10-23T05:41:00Z";
  next.fingerprint = monitor.fingerprint(next);
  await h.publish(next, { context: { ...context, runId: 456 } });
  await h.handoff();
  assert.equal(writes(h.state).length, count);
  assert.equal(h.state.tasks.length, 1);
  assert.equal(h.state.issues.length, 0);
  assert.ok(writes(h.state).filter((call) => call.route.includes("/issues")).every((call) => call.args.issue_number === 1829));
  const prompt = h.state.calls.find((call) => call.route.startsWith("POST /agents")).args.prompt;
  assert.match(prompt, /Refs #1829, never Closes/);
  assert.match(prompt, /Never close the permanent tracker/);
  assert.match(prompt, /runs\/123/);
});

test("fixed tracker preserves human prose and ignores copied markers and title changes", async (t) => {
  const h = boundary(t);
  h.state.tracker.title = "Renamed by a maintainer";
  h.state.tracker.body = "Human intro\n" + h.state.tracker.body + "\nHuman conclusion";
  h.state.issues.push({ ...structuredClone(h.state.tracker), number: 80 });
  const copy = h.state.issues[0].body;
  assert.equal((await h.publish()).trackerNumber, 1829);
  assert.match(h.state.tracker.body, /^Human intro/);
  assert.match(h.state.tracker.body, /Human conclusion$/);
  assert.equal(h.state.issues[0].body, copy);
  assert.equal(h.state.calls.filter((call) => call.route === "POST /repos/{owner}/{repo}/issues").length, 0);
});

test("unavailable or malformed tracker stops without creating a replacement", async (t) => {
  const h = boundary(t);
  const original = h.state.tracker.body;
  for (const mutate of [
    () => { h.state.tracker.state = "closed"; },
    () => { h.state.tracker.state = "open"; h.state.tracker.pull_request = {}; },
    () => { delete h.state.tracker.pull_request; h.state.tracker.body = "Marker removed"; },
    () => { h.state.tracker.body = original.replace("Not activated.", '<!-- upstream-gateway-state:v2:{} -->'); },
  ]) {
    mutate();
    await assert.rejects(h.publish(), /Tracker #1829|Invalid tracker/);
    assert.equal(writes(h.state).length, 0);
  }
});

test("legacy acknowledged fingerprints stay quiet; migration closures are not acknowledgments", async (t) => {
  const h = boundary(t);
  h.state.issues = [issue({ state: "closed", body: "<!-- upstream-gateway-candidate:v1:question-answer:"
    + report().capabilities.families[0].candidateHash + ' -->\n<!-- upstream-gateway-task:v1:{"state":"ready","reservation":null,"taskId":null} -->' })];
  assert.equal((await h.publish()).issueNumber, undefined);
  assert.equal(h.state.issues.length, 1);
  h.state.issues[0].body += "\n<!-- upstream-gateway-migrated:v1 -->";
  editState(h, (state) => { state.acknowledged = []; });
  assert.equal((await h.publish()).issueNumber, 1829);
});

test("active legacy monitor work and Copilot assignments prevent duplicate investigation", async (t) => {
  const h = boundary(t);
  for (const item of [{ labels: [{ name: publisher.LABEL }] }, { assignees: [{ id: 198982749 }] }]) {
    h.state.issues = [issue({ number: 80, body: "", labels: [], assignees: [], ...item })];
    assert.equal((await h.publish()).action, "deferred-existing-work");
    assert.equal(trackerState(h).current, null);
  }
});

test("related work informs investigation without suppressing it; marked implementation PR blocks", async (t) => {
  const h = boundary(t);
  h.state.issues = [issue({ number: 80, labels: [], title: "Support question", body: "question.resolve" })];
  h.state.pulls = [{ number: 100, state: "open", title: "interactive Gateway fixture", body: "" }];
  await h.publish();
  assert.equal((await h.handoff()).state, "queued");
  const prompt = h.state.calls.find((call) => call.route.startsWith("POST /agents")).args.prompt;
  assert.match(prompt, /Possible overlap, not verified coverage/);
  assert.match(prompt, /- #80\n- #100/);
  const h2 = boundary(t);
  await h2.publish();
  h2.state.pulls = [{ number: 100, state: "open", body: "<!-- upstream-gateway-candidate:v1:question-answer:" + "a".repeat(64) + " -->" }];
  assert.equal((await h2.handoff()).state, "deferred-existing-work");
  assert.equal(h2.state.tasks.length, 0);
});

test("active task and changed evidence keep the current reservation instead of duplicating work", async (t) => {
  const h = boundary(t);
  await h.publish(); await h.handoff();
  editState(h, (state) => { state.current.candidateHash = "f".repeat(64); });
  await h.publish();
  assert.equal(trackerState(h).current.candidateHash, "f".repeat(64));
  await h.handoff();
  assert.equal(h.state.tasks.length, 1);
});

test("active agent task defers, invalid state or failed lookup does not write a reservation", async (t) => {
  const h = boundary(t);
  await h.publish();
  h.state.tasks = [{ id: "other-task", state: "in_progress", created_at: "2026-10-22T00:00:00Z" }];
  assert.equal((await h.handoff()).state, "deferred-active-task");
  assert.equal(trackerState(h).current.task.reservation, null);
  h.state.calls = [];
  h.state.override = (route) => route === "GET /agents/repos/{owner}/{repo}/tasks"
    ? { data: { tasks: [{ id: "other-task", state: "new-state", created_at: "2026-10-22T00:00:00Z" }] } } : null;
  await assert.rejects(h.handoff(), /Unknown or invalid/);
  h.state.override = (route) => { if (route === "GET /agents/repos/{owner}/{repo}/tasks") throw new Error("query failed"); };
  await assert.rejects(h.handoff(), /query failed/);
  assert.equal(writes(h.state).length, 0);
});

test("uncertain task POST reserves once and never replays", async (t) => {
  const h = boundary(t);
  await h.publish();
  h.state.override = (route) => { if (route === "POST /agents/repos/{owner}/{repo}/tasks") throw new Error("response lost"); };
  await assert.rejects(h.handoff(), /response lost/);
  assert.equal(trackerState(h).current.task.state, "reserved");
  await assert.rejects(h.handoff(), /no automatic replay/);
  await h.publish();
  await assert.rejects(h.handoff(), /no automatic replay/);
  assert.equal(h.state.calls.filter((call) => call.route === "POST /agents/repos/{owner}/{repo}/tasks").length, 1);
});

test("completed task needs a trusted fingerprint-bound disposition and never restarts unchanged", async (t) => {
  const h = boundary(t);
  await h.publish(); await h.handoff();
  await assert.rejects(h.handoff(1829, { now: Date.parse("2026-10-24T01:00:00Z") }), /stalled/);
  h.state.tasks[0].state = "completed";
  h.state.comments = [dispositionComment(h, "supported", { user: { id: 12 }, author_association: "NONE" })];
  await assert.rejects(h.handoff(), /needs-disposition/);
  h.state.comments.push(dispositionComment(h, "existing-work"));
  assert.equal((await h.handoff()).state, "completed");
  await h.publish(); await h.handoff(); await h.publish();
  assert.equal(h.state.tasks.length, 1);
  assert.deepEqual(trackerState(h).acknowledged, [report().capabilities.families[0].candidateHash]);
  assert.equal(h.state.tracker.state, "open");
});

test("source collection and report validation accept growth beyond ten thousand watched files", () => {
  const value = report();
  const tree = paths.map((path) => ({ path, sha, type: "blob" }));
  for (let i = 0; i < 49995; i++) tree.push({ path: `src/gateway/growth-${i}.ts`, sha, type: "blob" });
  value.main.files = monitor.sourceInventory({ truncated: false, tree });
  assert.equal(Object.keys(value.main.files).length, 50000);
  value.capabilities = monitor.assessLocal(value, local);
  value.fingerprint = monitor.fingerprint(value);
  value.changes.main = monitor.sourceChanges(null, value.main.files);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  assert.equal(value.changes.main.length, 50000);
  monitor.validateReport(value);
  const before = Object.fromEntries(paths.map((path) => [path, { group: monitor.watchGroup(path), sha: otherSha }]));
  for (let i = 0; i < 49995; i++) before[`src/gateway/old-${i}.ts`] = { group: "gateway", sha: otherSha };
  value.baseline = { fingerprint: "a".repeat(64), mainCommit: otherSha, releasedCommit: null };
  value.changes.main = monitor.sourceChanges(before, value.main.files);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  assert.equal(value.changes.main.length, 99995);
  monitor.validateReport(value);
  value.changes.main = Array(100001).fill(value.changes.main[0]);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  assert.throws(() => monitor.validateReport(value), /Invalid source deltas/);
  assert.throws(() => monitor.sourceInventory({ truncated: true, tree }), /Incomplete upstream tree/);
  tree.push({ path: "src/gateway/one-too-many.ts", sha, type: "blob" });
  assert.throws(() => monitor.sourceInventory({ truncated: false, tree }), /exceeds 50000 files/);
});

async function recoveryBoundary(t) {
  const h = boundary(t);
  await h.publish();
  const task = { id: "recorded-task", state: "completed", created_at: "2026-10-22T00:00:00Z",
    repository: { id: 123 }, session_count: 1,
    sessions: [{ id: "00e29dc1-0a8e-4559-97d4-681b4c112703", task_id: "recorded-task",
      repository: { id: 123 }, state: "completed" }] };
  h.state.tasks.push(task);
  editState(h, (state) => {
    state.current.task = { state: "completed", reservation: "a".repeat(64), taskId: task.id, attention: "needs-disposition" };
    state.health = 456;
  });
  h.state.resultLogs = "data: " + JSON.stringify({ id: "final", object: "chat.completion.chunk", choices: [
    { delta: { role: "assistant", content:
      "Existing work owns the question implementation. Scope negotiation and real Gateway tests remain outstanding.\n"
      + dispositionComment(h, "existing-work").body }, finish_reason: "stop" },
  ] }) + "\n";
  h.state.override = (route, args) => {
    if (route === "POST /graphql") return { data: { data: { viewer: {
      copilotEndpoints: { api: "https://api.individual.githubcopilot.com" },
    } } } };
    if (route === "POST /repos/{owner}/{repo}/issues/{issue_number}/comments") {
      const comment = { id: 99, body: args.body, user: { id: 41898282 }, author_association: "CONTRIBUTOR" };
      h.state.comments.push(comment);
      return { data: comment, status: 201 };
    }
  };
  h.state.calls.length = 0;
  const recoveryContext = { ...context, eventName: "workflow_dispatch",
    payload: { ...context.payload, inputs: { report_only: false, recovery_only: true, recovery_task_id: task.id } } };
  return { h, task, recoveryContext, recover: (extra = {}) => h.handoff(1829, {
    context: recoveryContext, recoveryOnly: true, expectedTaskId: task.id, ...extra,
  }) };
}

test("recovery-only recovers a lost comment response and acknowledges only the pinned task once", async (t) => {
  const { h, task, recover } = await recoveryBoundary(t);
  const transport = h.state.override;
  let loseResponse = true;
  h.state.override = async (route, args) => {
    const result = await transport(route, args);
    if (route === "POST /repos/{owner}/{repo}/issues/{issue_number}/comments" && loseResponse) {
      loseResponse = false;
      throw new Error("comment response lost");
    }
    return result;
  };
  await assert.rejects(recover(), /comment response lost/);
  assert.equal(trackerState(h).current.task.disposition, undefined);
  assert.deepEqual(trackerState(h).acknowledged, []);
  assert.deepEqual(await recover(), { state: "completed", taskId: task.id });
  const firstWrites = writes(h.state).length;
  assert.equal((await recover()).state, "completed");
  assert.equal(writes(h.state).length, firstWrites);
  assert.equal(h.state.comments.length, 1);
  assert.equal(h.state.calls.filter((call) => call.route === "POST /repos/{owner}/{repo}/issues/{issue_number}/comments").length, 1);
  assert.equal(h.state.tasks.length, 1);
  assert.equal(trackerState(h).health, null);
  assert.equal(trackerState(h).current.task.disposition, "existing-work");
  assert.deepEqual(trackerState(h).acknowledged, [report().capabilities.families[0].candidateHash]);
  assert.equal(h.state.calls.filter((call) => call.route.includes("/tasks") && !call.route.startsWith("GET ")).length, 0);
  assert.equal(h.state.calls.filter((call) => call.route === "GET /agents/repos/{owner}/{repo}/tasks").length, 0);
});

test("recovery-only rejects missing, mismatched and unfinished tasks before any write", async (t) => {
  const { h, task, recover } = await recoveryBoundary(t);
  const initial = h.state.tracker.body;
  for (const state of ["queued", "in_progress", "failed", "cancelled"]) {
    task.state = state;
    await assert.rejects(recover(), /requires a completed/);
    assert.equal(h.state.tracker.body, initial);
  }
  task.state = "completed";
  await assert.rejects(recover({ expectedTaskId: null }), /pinned task ID/);
  await assert.rejects(recover({ expectedTaskId: "another-task" }), /pinned task ID/);
  editState(h, (state) => { state.current.task = { state: "ready", taskId: null, reservation: null }; });
  await assert.rejects(recover(), /does not match the recorded task/);
  editState(h, (state) => { state.current = null; });
  await assert.rejects(recover(), /Tracker has no candidate/);
  assert.equal(writes(h.state).length, 0);
  assert.equal(h.state.comments.length, 0);
  assert.equal(h.state.tasks.length, 1);
});

test("recovery-only requires matching manual inputs, main and a dedicated credential", async (t) => {
  const { h, recoveryContext, recover } = await recoveryBoundary(t);
  for (const change of [
    { eventName: "schedule" }, { ref: "refs/heads/feature" },
    { repo: { owner: "someone", repo: "openclaw-windows-node" } },
    { payload: { ...recoveryContext.payload, inputs: { ...recoveryContext.payload.inputs, report_only: true } } },
    { payload: { ...recoveryContext.payload, inputs: { ...recoveryContext.payload.inputs, report_only: undefined } } },
    { payload: { ...recoveryContext.payload, inputs: { ...recoveryContext.payload.inputs, recovery_only: false } } },
    { payload: { ...recoveryContext.payload, inputs: { ...recoveryContext.payload.inputs, recovery_task_id: "another-task" } } },
  ]) await assert.rejects(recover({ context: { ...recoveryContext, ...change } }), /Dispatch context|Recovery requires/);
  await assert.rejects(recover({ recoveryOnly: false }), /Recovery requires/);
  await assert.rejects(recover({ copilot: null }), /recovery made no changes/);
  await assert.rejects(h.publish(undefined, { context: recoveryContext }), /Recovery cannot select candidates/);
  assert.equal(writes(h.state).length, 0);
  assert.equal(h.state.comments.length, 0);
});

test("completed task result is recovered with personal auth, published once, and acknowledged without another task", async (t) => {
  const h = boundary(t);
  await h.publish(); await h.handoff();
  const task = h.state.tasks[0];
  task.state = "completed";
  task.repository = { id: 1144728715 };
  task.session_count = 1;
  task.sessions = [{ id: "00e29dc1-0a8e-4559-97d4-681b4c112703", task_id: task.id,
    repository: task.repository, state: "completed" }];
  const final = "Existing PR #1757 owns this implementation. Scope and real Gateway validation remain outstanding.\n"
    + dispositionComment(h, "existing-work").body;
  h.state.resultLogs = 'data: ' + JSON.stringify({ id: "turn-31", object: "chat.completion.chunk",
    choices: [{ index: 0, delta: { role: "assistant", content: final }, finish_reason: "stop" }] }) + '\n';
  let loseCommentResponse = true;
  h.state.override = (route, args) => {
    if (route === "POST /graphql") return { data: { data: { viewer: { copilotEndpoints: { api: "https://api.individual.githubcopilot.com" } } } } };
    if (route === "POST /repos/{owner}/{repo}/issues/{issue_number}/comments") {
      const comment = { id: 99, body: args.body, user: { id: 41898282 }, author_association: "CONTRIBUTOR" };
      h.state.comments.push(comment);
      if (loseCommentResponse) { loseCommentResponse = false; throw new Error("comment response lost"); }
      return { data: comment, status: 201 };
    }
  };
  await assert.rejects(h.handoff(), /comment response lost/);
  assert.equal(trackerState(h).current.task.disposition, undefined);
  assert.equal((await h.handoff()).state, "completed");
  await h.publish(); await h.handoff(); await h.publish();
  assert.equal(h.state.tasks.length, 1);
  assert.equal(h.state.comments.length, 1);
  assert.match(h.state.comments[0].body, /Existing PR #1757/);
  assert.equal(trackerState(h).current.task.disposition, "existing-work");
  assert.deepEqual(trackerState(h).acknowledged, [report().capabilities.families[0].candidateHash]);
});

test("result recovery accepts only the bound session's final assistant disposition", async (t) => {
  const { readTaskResult } = require("./copilot-task-result.cjs");
  const candidateHash = "a".repeat(64);
  const marker = `<!-- upstream-gateway-disposition:v1:${candidateHash}:existing-work -->`;
  const evidence = "Existing PR #1757 owns the implementation. Requested scope and real Gateway validation remain outstanding.\n";
  const final = evidence + marker;
  const session = { id: "00e29dc1-0a8e-4559-97d4-681b4c112703", task_id: "task-1", state: "completed", repository: { id: 123 } };
  const task = { id: "task-1", state: "completed", session_count: 1, repository: { id: 123 }, sessions: [session] };
  const event = (delta, finish_reason = "stop", id = "turn-31") => "data: " + JSON.stringify({ id,
    object: "chat.completion.chunk", choices: [{ index: 0, delta, finish_reason }] }) + "\n";
  const answer = (content) => event({ role: "assistant", content });
  let currentLog;
  t.mock.method(global, "fetch", async (url, options) => {
    assert.equal(url, `https://api.individual.githubcopilot.com/agents/sessions/${session.id}/logs`);
    assert.equal(options.headers.Authorization, "Bearer result-test-token");
    assert.equal(options.redirect, "error");
    return new Response(currentLog);
  });
  const run = async (log, value = task, endpoint = "https://api.individual.githubcopilot.com") => {
    currentLog = log;
    const copilot = { async auth() { return { type: "token", token: "result-test-token" }; }, async request(route) {
      assert.equal(route, "POST /graphql");
      return { data: { data: { viewer: { copilotEndpoints: { api: endpoint } } } } };
    } };
    return readTaskResult(copilot, value, candidateHash);
  };
  const split = event({ role: "assistant", content: evidence }, null) + event({ content: marker });
  assert.equal((await run(split)).disposition, "existing-work");
  const usage = "data: " + JSON.stringify({ id: "turn-31", object: "chat.completion.chunk",
    choices: [], usage: { total_tokens: 123 } }) + "\n";
  assert.equal((await run(split + usage + "data: [DONE]\n")).text, final);
  const unindexedTools = 'data: ' + JSON.stringify({ id: "tool-turn", object: "chat.completion.chunk", choices: [
    { delta: { role: "assistant", content: "Investigating." }, finish_reason: "tool_calls" },
    { delta: { tool_calls: [{ id: "tool" }], content: final }, finish_reason: "tool_calls" },
  ] }) + '\n';
  assert.equal((await run(unindexedTools + answer(final).replace('"index":0,', ''))).disposition, "existing-work");
  for (const log of [
    answer(final.replace(candidateHash, "b".repeat(64))),
    answer(final + "\n" + marker),
    answer(marker),
    event({ role: "tool", content: final }),
    event({ role: "assistant", content: final, tool_calls: [{ id: "tool" }] }),
    event({ role: "assistant", content: final }, null),
    answer(final) + answer("I cannot establish a disposition."),
    answer(final) + event({ role: "assistant", content: "Still investigating." }, null) + usage,
    event({ role: "assistant", reasoning_text: final }),
  ]) assert.equal(await run(log), null);
  await assert.rejects(run(answer(final), { ...task, sessions: [{ ...session, task_id: "other-task" }] }), /identity mismatch/);
  await assert.rejects(run(answer(final), { ...task, sessions: [{ ...session, repository: { id: 456 } }] }), /identity mismatch/);
  await assert.rejects(run(answer(final), { ...task, session_count: 2 }), /one completed session/);
  await assert.rejects(run(answer(final), task, "https://api.individual.githubcopilot.com.evil.example"), /Unapproved/);
  await assert.rejects(run("unexpected format"), /Unexpected.*format/);
  await assert.rejects(run(answer("x".repeat(16001))), /final answer exceeds/);
});

test("new evidence can replace a completed acknowledged candidate, preserving its fingerprint", async (t) => {
  const h = boundary(t);
  await h.publish(); await h.handoff();
  editState(h, (state) => {
    state.current.candidateHash = "f".repeat(64);
    state.current.task.state = "completed";
    state.current.task.disposition = "supported";
  });
  await h.publish();
  assert.equal(trackerState(h).current.task.state, "ready");
  assert.deepEqual(trackerState(h).acknowledged, ["f".repeat(64)]);
});

test("missing token, assigned tracker and revoked reservation never create a task", async (t) => {
  for (const revoke of [
    (issue) => { issue.state = "closed"; },
    (issue) => { issue.body = issue.body.replace('"taskId":null', '"taskId":"already-started"'); },
    (issue) => { issue.assignees = [{ id: 12 }]; },
  ]) {
    const h = boundary(t);
    await h.publish();
    await assert.rejects(h.handoff(1829, { copilot: null }), /no new agent task started/);
    let lists = 0;
    h.state.override = (route) => {
      if (route === "GET /agents/repos/{owner}/{repo}/tasks" && ++lists === 2) revoke(h.state.tracker);
    };
    await assert.rejects(h.handoff(), /authority changed before task|Tracker #1829/);
    assert.equal(h.state.tasks.length, 0);
  }
});

test("non-default or read-only dispatch cannot authorize a task", async (t) => {
  const h = boundary(t);
  await h.publish(); h.state.calls = [];
  for (const changed of [
    { ref: "refs/heads/feature" }, { eventName: "pull_request" },
    { eventName: "workflow_dispatch", payload: { ...context.payload, inputs: { report_only: "true" } } },
  ]) await assert.rejects(h.handoff(1829, { context: { ...context, ...changed } }), /Dispatch context/);
  assert.equal(h.state.calls.length, 0);
});

test("invalid evidence and incomplete work queries fail closed before publication writes", async (t) => {
  const h = boundary(t);
  const bad = report(); bad.main.commit = "not-a-commit";
  await assert.rejects(h.publish(bad), /track/);
  h.state.override = (route) => route.includes("/compare/") ? { data: { status: "diverged", base_commit: { sha } } } : null;
  await assert.rejects(h.publish(), /main ancestry/);
  h.state.override = (route) => { if (route === "GET /repos/{owner}/{repo}/pulls") throw new Error("query failed"); };
  await assert.rejects(h.publish(), /query failed/);
  h.state.override = (route) => route === "GET /repos/{owner}/{repo}/issues" ? { data: Array(100).fill(issue()) } : null;
  await assert.rejects(h.publish(), /exceeds 2000/);
  assert.equal(writes(h.state).length, 0);
});

test("collection failures stay on #1829, preserve task state and human notes, and clear on recovery", async (t) => {
  const h = boundary(t);
  await h.publish(); await h.handoff();
  h.state.tracker.body += "\nMaintainer notes";
  const task = trackerState(h).current.task;
  assert.equal(await publisher.publishFailure({ github: h.github, context }), 1829);
  await publisher.publishFailure({ github: h.github, context: { ...context, runId: 456 } });
  assert.equal(h.state.issues.length, 0);
  assert.match(h.state.tracker.body, /Maintainer notes$/);
  assert.match(h.state.tracker.body, /runs\/456/);
  assert.deepEqual(trackerState(h).current.task, task);
  await h.publish();
  assert.equal(trackerState(h).health, null);
});

test("workflow runs public paths with separate tokens and read-only report mode", () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8");
  assert.doesNotMatch(workflow, /^\s+(push|pull_request|pull_request_target|workflow_run):/m);
  assert.match(workflow, /cancel-in-progress: false/);
  assert.match(workflow, /github.ref_name == github.event.repository.default_branch/);
  const [observe, health, publish, implement] = workflow.split(/\n  (?:observation-failure|publish|implement):/);
  assert.doesNotMatch(observe, /secrets\.|: write/);
  assert.doesNotMatch(health + publish, /COPILOT_GITHUB_TOKEN/);
  assert.match(implement, /issues: write/);
  assert.doesNotMatch(implement, /github-token:.*COPILOT/);
  assert.match(implement, /getOctokit\(process.env.COPILOT_GITHUB_TOKEN\)/);
  assert.match(implement, /handoff\(\{ github, copilot,/);
  assert.match(implement, /createWork: false/);
  assert.equal((workflow.match(/!inputs.report_only/g) ?? []).length, 2);
  assert.equal((workflow.match(/vars.UPSTREAM_GATEWAY_MONITOR_ENABLED == 'true'/g) ?? []).length, 3);
  assert.match(implement, /retries: 0/);
  assert.match(workflow, /await publish\(\{ github, context, core, report/);
});

test("workflow constructs an independently authenticated task client instead of overriding a header", async () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8");
  const step = workflow.split("- name: Reconcile or start investigation without an automatic PR")[1].split("- name: Refresh tracker")[0];
  const script = step.split(/script: \|\r?\n/)[1].split(/\r?\n/).map((line) => line.replace(/^ {12}/, "")).join("\n");
  // Model the real Octokit auth hook: client auth replaces request headers.
  class AuthClient {
    constructor({ auth, retry = { enabled: true } }) { this.auth = auth; this.retry = retry; }
    async request(route, options = {}) { return { route, authorization: this.auth, ...options, headers: { authorization: this.auth } }; }
  }
  const github = new AuthClient({ auth: "fixture-actions-token" });
  let invoked = false;
  const imports = (name) => name === "node:fs" ? { readFileSync: () => "{}" } : {
    handoff: async ({ github: issueClient, copilot }) => {
      assert.ok(copilot && copilot !== issueClient, "task calls require a distinct client");
      assert.equal(copilot.retry.enabled, false, "task POSTs must not be retried");
      assert.equal((await copilot.request("GET /agents/repos/o/r/tasks")).headers.authorization, "fixture-personal-token");
      assert.equal((await issueClient.request("PATCH /repos/o/r/issues/1829")).headers.authorization, "fixture-actions-token");
      invoked = true;
      return { state: "queued" };
    },
  };
  const summary = { addRaw() { return this; }, async write() {} };
  const run = new (Object.getPrototypeOf(async function () {}).constructor)("github", "context", "core", "process", "require", "getOctokit", script);
  await run(github, context, { summary }, { env: { COPILOT_GITHUB_TOKEN: "fixture-personal-token", ISSUE_NUMBER: "1829", REPORT: "unused" } }, imports, (token) => new AuthClient({ auth: token, retry: { enabled: false } }));
  assert.equal(invoked, true);
});

test("manual recovery workflow pins the task and excludes all normal publication jobs", async () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8").replace(/\r\n/g, "\n");
  const recovery = workflow.split("\n  recover:")[1].split("\n  welcome-copy:")[0];
  assert.match(recovery, /needs: observe/);
  assert.match(recovery, /github\.event_name == 'workflow_dispatch' && inputs\.recovery_only/);
  assert.match(recovery, /vars\.UPSTREAM_GATEWAY_MONITOR_ENABLED == 'true'/);
  assert.match(recovery, /retries: 0/);
  assert.doesNotMatch(recovery, /publish\(|createWork|pull-requests: write/);
  for (const job of ["observation-failure", "publish", "welcome-copy"]) {
    const header = workflow.split(`\n  ${job}:`)[1].split("runs-on:")[0];
    assert.match(header, /!inputs\.recovery_only/);
  }
  assert.match(workflow.split("\n  implement:")[1].split("runs-on:")[0], /needs: publish/);
  assert.match(workflow.split("\n  observe:")[1].split("runs-on:")[0],
    /github\.ref_name == github\.event\.repository\.default_branch/);
  const script = recovery.split("script: |\n")[1].split("\n")
    .map((line) => line.replace(/^ {12}/, "")).join("\n");
  const token = "fixture-personal-token", github = {}, copilot = {};
  let invoked = false;
  const imports = (name) => name === "node:fs" ? { readFileSync: () => "{}" } : {
    handoff: async (args) => {
      assert.equal(args.github, github);
      assert.equal(args.copilot, copilot);
      assert.equal(args.recoveryOnly, true);
      assert.equal(args.expectedTaskId, "recorded-task");
      assert.equal(args.issueNumber, 1829);
      invoked = true;
      return { state: "completed" };
    },
  };
  const summary = { addRaw() { return this; }, async write() {} };
  const run = new (Object.getPrototypeOf(async function () {}).constructor)(
    "github", "context", "core", "process", "require", "getOctokit", script);
  await run(github, context, { summary }, {
    env: { COPILOT_GITHUB_TOKEN: token, RECOVERY_TASK_ID: "recorded-task", REPORT: "unused" },
  }, imports, (auth) => { assert.equal(auth, token); return copilot; });
  assert.equal(invoked, true);
});


test("closed legacy reservations still block a new task, including migration closures", async (t) => {
  const h = boundary(t);
  for (const state of ["reserved", "uncertain", "queued", "in_progress"]) {
    h.state.issues = [issue({ state: "closed", body: "<!-- upstream-gateway-candidate:v1:question-answer:"
      + report().capabilities.families[0].candidateHash + " -->\n<!-- upstream-gateway-migrated:v1 -->\n"
      + '<!-- upstream-gateway-task:v1:' + JSON.stringify({ state, reservation: "a".repeat(64), taskId: null }) + " -->" })];
    assert.equal((await h.publish()).action, "deferred-existing-work");
    assert.equal(trackerState(h).current, null);
    assert.deepEqual(trackerState(h).acknowledged, []);
  }
  assert.equal(h.state.tasks.length, 0);
});

test("changed candidate replaces only an unreserved ready attempt", async (t) => {
  const h = boundary(t);
  await h.publish();
  editState(h, (state) => { state.current.candidateHash = "f".repeat(64); });
  await h.publish();
  assert.equal(trackerState(h).current.candidateHash, report().capabilities.families[0].candidateHash);
  assert.equal((await h.handoff()).state, "queued");
});


test("legacy closing-reference PR remains a blocker after a ready candidate is migration-closed", async (t) => {
  const h = boundary(t);
  h.state.issues = [issue({ number: 1845, state: "closed", body: "<!-- upstream-gateway-candidate:v1:question-answer:"
    + report().capabilities.families[0].candidateHash + " -->\n<!-- upstream-gateway-migrated:v1 -->\n"
    + '<!-- upstream-gateway-task:v1:{"state":"ready","reservation":null,"taskId":null} -->' })];
  h.state.pulls = [{ number: 100, state: "open", draft: true, body: "Closes #1845", user: { id: 12 }, assignees: [] }];
  assert.equal((await h.publish()).action, "deferred-existing-work");
  assert.equal(trackerState(h).current, null);
  h.state.pulls[0].body = "Closes #18450";
  assert.equal((await h.publish()).issueNumber, 1829);
  h.state.pulls[0].body = "Closes https://github.com/openclaw/openclaw-windows-node/issues/1845";
  assert.equal((await h.handoff()).state, "deferred-existing-work");
  assert.equal(h.state.tasks.length, 0);
});
