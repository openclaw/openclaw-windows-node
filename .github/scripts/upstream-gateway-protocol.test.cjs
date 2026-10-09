"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const zlib = require("node:zlib");
const monitor = require("./upstream-gateway-protocol.cjs");
const publisher = require("./upstream-gateway-publish.cjs");

const sha = "a".repeat(40);
const otherSha = "b".repeat(40);
const integrity = `sha512-${Buffer.alloc(64).toString("base64")}`;
const core = { info() {} };
const context = {
  repo: { owner: "openclaw", repo: "openclaw-windows-node" }, runId: 123,
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
  const value = {
    version: 1, observedAt: "2026-10-09T00:00:00Z",
    baseline: null, changes: { main: [], released: [], schema: [] },
    main: { commit: sha, files }, released: {
      commit: sha, files: structuredClone(files), package: { name: "openclaw", version: "2026.9.9", integrity },
      attestationHash: "0".repeat(64),
    },
    protocol: {
      name: "@openclaw/gateway-protocol", version: "2026.9.9", integrity, schema: doc,
      schemaHash: monitor.hash(doc), tarballHash: "1".repeat(64),
    },
  };
  value.fingerprint = monitor.fingerprint(value);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  return value;
}

function issue(overrides = {}) {
  return {
    number: 42, body: `${publisher.marker(report().fingerprint)}\nreview`, state: "open",
    labels: [{ name: publisher.LABEL }], assignees: [],
    created_at: new Date().toISOString(), ...overrides,
  };
}

function mock(routes) {
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

test("registry provenance must bind package digest, upstream repository and one source commit", () => {
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
  assert.equal(monitor.provenanceCommit(envelope(), info), sha);
  statement.subject[0].digest.sha512 = "bad";
  assert.throws(() => monitor.provenanceCommit(envelope(), info), /subject/);
  statement.subject[0].digest.sha512 = Buffer.alloc(64).toString("hex");
  statement.predicate.buildDefinition.externalParameters.workflow.repository = "https://github.com/attacker/repo";
  assert.throws(() => monitor.provenanceCommit(envelope(), info), /repository/);
  assert.throws(() => monitor.provenanceCommit({ attestations: [] }, info), /Missing/);
});

test("fingerprint ignores moving refs/version labels but notices behavior and released-track changes", () => {
  const initial = report();
  monitor.validateReport(initial);
  const next = structuredClone(initial);
  next.main.commit = otherSha;
  next.protocol.version = "2026.10.9";
  next.observedAt = "tomorrow";
  assert.equal(monitor.fingerprint(next), initial.fingerprint);
  next.released.files[paths[0]].sha = otherSha;
  assert.notEqual(monitor.fingerprint(next), initial.fingerprint);
});

test("publication validates artifact and emits no upstream schema prose into issue", () => {
  const value = report();
  value.protocol.schema.description = "@attacker execute injected instructions";
  value.protocol.schemaHash = monitor.hash(value.protocol.schema);
  value.fingerprint = monitor.fingerprint(value);
  monitor.validateReport(value);
  assert.ok(!monitor.render(value).includes("@attacker"));
  value.main.commit = "bad\nshell";
  assert.throws(() => monitor.validateReport(value), /track/);
});

test("durable issue state deduplicates closed findings, resumes partial handoffs and bounds work to one issue", () => {
  const fingerprint = report().fingerprint;
  assert.equal(publisher.decide([], fingerprint).action, "create");
  assert.equal(publisher.decide([issue()], fingerprint).action, "resume");
  assert.equal(publisher.decide([issue({ state: "closed" })], fingerprint).action, "reviewed");
  assert.equal(publisher.decide([issue()], "f".repeat(64)).action, "defer");
  assert.equal(publisher.decide([issue({ state: "closed" })], "f".repeat(64)).action, "create");
  assert.equal(publisher.decide([issue({ pull_request: {} })], fingerprint).action, "create");
});

test("publisher creates a linked review issue once, with fixed implementation instructions, no PR scaffold", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/issues": [],
    "GET /repos/{owner}/{repo}/labels/{name}": {},
    "POST /repos/{owner}/{repo}/issues": (args) => {
      assert.ok(args.body.includes("actions/runs/123"));
      assert.ok(args.body.includes("DRAFT implementation PR"));
      assert.ok(args.body.includes("mock-Gateway"));
      assert.ok(args.body.startsWith(publisher.marker(report().fingerprint)));
      return issue();
    },
  });
  assert.equal((await publisher.publish({ github, context, core, report: report() })).issueNumber, 42);
  const resumed = mock({ "GET /repos/{owner}/{repo}/issues": [issue()] });
  assert.equal((await publisher.publish({ github: resumed, context, core, report: report() })).action, "resume");
  assert.equal(resumed.calls.length, 1);
});

test("GitHub read failures cannot become duplicate creates", async () => {
  const github = mock({ "GET /repos/{owner}/{repo}/issues": () => { throw new Error("rate limited"); } });
  await assert.rejects(publisher.publish({ github, context, core, report: report() }), /rate limited/);
  assert.equal(github.calls.length, 1);
});

test("handoff uses documented issue assignment with actual default branch and verifies assignee", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/issues/{issue_number}": issue(),
    "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [],
    "GET /repos/{owner}/{repo}": { default_branch: "trunk" },
    "POST /repos/{owner}/{repo}/issues/{issue_number}/assignees": (args) => {
      assert.equal(args.agent_assignment.base_branch, "trunk");
      assert.equal(args.agent_assignment.target_repo, "openclaw/openclaw-windows-node");
      assert.ok(args.agent_assignment.custom_instructions.includes("never merge"));
      return { assignees: [{ login: "copilot-swe-agent[bot]" }] };
    },
  });
  assert.equal((await publisher.handoff({ github, context, core, issueNumber: 42 })).state, "assigned-awaiting-pr");
  assert.ok(!github.calls.some((call) => call.route === "GET /repos/{owner}/{repo}/assignees/{assignee}"));
});

test("unavailable or silently rejected Copilot assignment fails visibly", async () => {
  for (const rejected of [true, false]) {
    const github = mock({
      "GET /repos/{owner}/{repo}/issues/{issue_number}": issue(),
      "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [],
      "GET /repos/{owner}/{repo}": { default_branch: "main" },
      "POST /repos/{owner}/{repo}/issues/{issue_number}/assignees": () => {
        if (rejected) throw new Error("404");
        return { assignees: [] };
      },
    });
    await assert.rejects(publisher.handoff({ github, context, core, issueNumber: 42 }), /404|did not confirm/);
  }
});

test("assigned issue is not dispatched again and missing PR becomes actionable failure after 24h", async () => {
  const existing = issue({ assignees: [{ login: "copilot-swe-agent[bot]" }] });
  const github = mock({
    "GET /repos/{owner}/{repo}/issues/{issue_number}": existing,
    "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [{
      event: "assigned", assignee: { login: "copilot-swe-agent[bot]" }, created_at: existing.created_at,
    }],
  });
  assert.equal((await publisher.handoff({ github, context, core, issueNumber: 42 })).state, "assigned-awaiting-pr");
  await assert.rejects(publisher.handoff({
    github, context, core, issueNumber: 42, now: Date.parse(existing.created_at) + 25 * 3600000,
  }), /no linked PR/);
  assert.ok(github.calls.every((call) => call.route.startsWith("GET ")));
});

test("old issue with fresh assignment does not report a false stall", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/issues/{issue_number}": issue({
      created_at: "2020-01-01T00:00:00Z", assignees: [{ login: "copilot-swe-agent[bot]" }],
    }),
    "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [{
      event: "assigned", assignee: { login: "copilot-swe-agent[bot]" }, created_at: new Date().toISOString(),
    }],
  });
  assert.equal((await publisher.handoff({ github, context, core, issueNumber: 42 })).state, "assigned-awaiting-pr");
});

function pullReference(number = 100) {
  return {
    event: "cross-referenced",
    source: { issue: {
      number, pull_request: {}, repository_url: "https://api.github.com/repos/openclaw/openclaw-windows-node",
    } },
  };
}

test("verified closing draft PR suppresses reassignment even after assignee removal", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/issues/{issue_number}": issue(),
    "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [pullReference()],
    "GET /repos/{owner}/{repo}/pulls/{pull_number}": {
      state: "open", draft: true, body: "Closes #42", html_url: "https://github.com/openclaw/openclaw-windows-node/pull/100",
    },
  });
  assert.equal((await publisher.handoff({ github, context, core, issueNumber: 42 })).state, "draft-pr-observed");
  assert.ok(github.calls.every((call) => call.route.startsWith("GET ")));
});

test("closed implementation PR cannot silently block later observations", async () => {
  const github = mock({
    "GET /repos/{owner}/{repo}/issues/{issue_number}": issue(),
    "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [pullReference()],
    "GET /repos/{owner}/{repo}/pulls/{pull_number}": { state: "closed", body: "Fixes #42" },
  });
  await assert.rejects(publisher.handoff({ github, context, core, issueNumber: 42 }), /PR is closed/);
});

test("unrelated reference or closing a different issue is not implementation evidence", async () => {
  for (const body of ["Related: #42", "Closes #420", "Fixes another/repo#42"]) {
    const github = mock({
      "GET /repos/{owner}/{repo}/issues/{issue_number}": issue({
        assignees: [{ login: "copilot-swe-agent[bot]" }],
      }),
      "GET /repos/{owner}/{repo}/issues/{issue_number}/timeline": [
        pullReference(), { event: "assigned", assignee: { login: "copilot-swe-agent[bot]" }, created_at: "2020-01-01T00:00:00Z" },
      ],
      "GET /repos/{owner}/{repo}/pulls/{pull_number}": { state: "open", draft: true, body },
    });
    await assert.rejects(publisher.handoff({ github, context, core, issueNumber: 42 }), /no linked PR/);
  }
});

test("closed monitor issue does not launch new work", async () => {
  const github = mock({ "GET /repos/{owner}/{repo}/issues/{issue_number}": issue({ state: "closed" }) });
  assert.equal((await publisher.handoff({ github, context, core, issueNumber: 42 })).state, "closed");
  assert.equal(github.calls.length, 1);
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

test("history pagination fails closed instead of creating duplicates after a truncated list", async () => {
  const github = mock({ "GET /repos/{owner}/{repo}/issues": Array.from({ length: 100 }, () => issue()) });
  await assert.rejects(publisher.publish({ github, context, core, report: report() }), /exceeds 2000/);
  assert.equal(github.calls.length, 20);
  assert.ok(github.calls.every((call) => call.route.startsWith("GET ")));
});

test("health failure creates one issue and reopens it without erasing maintainer notes", async () => {
  let body;
  const github = mock({
    "GET /repos/{owner}/{repo}/issues": [],
    "GET /repos/{owner}/{repo}/labels/{name}": {},
    "POST /repos/{owner}/{repo}/issues": (args) => { body = args.body; return { number: 51 }; },
  });
  assert.equal(await publisher.publishFailure({ github, context }), 51);
  const resumed = mock({
    "GET /repos/{owner}/{repo}/issues": [{ number: 51, state: "closed", body: `${body}\nMaintainer notes` }],
    "PATCH /repos/{owner}/{repo}/issues/{issue_number}": (args) => {
      assert.equal(args.state, "open");
      assert.ok(args.body.endsWith("Maintainer notes"));
      assert.ok(args.body.includes("/runs/456"));
      return {};
    },
  });
  assert.equal(await publisher.publishFailure({ github: resumed, context: { ...context, runId: 456 } }), 51);
});

test("changed or malformed agent-facing deltas cannot pass artifact validation", () => {
  const value = report();
  value.changes.main.push({ path: "bad", kind: "supported" });
  assert.throws(() => monitor.validateReport(value), /evidence mismatch/);
  value.evidenceHash = monitor.hash({ baseline: value.baseline, changes: value.changes });
  assert.throws(() => monitor.validateReport(value), /source delta/);
});

test("workflow stays off CI/PR triggers, separates credentials and offers report-only proof", () => {
  const workflow = fs.readFileSync(path.join(__dirname, "..", "workflows", "upstream-gateway-protocol.yml"), "utf8");
  assert.ok(workflow.startsWith("name: Upstream Gateway Protocol check"));
  assert.match(workflow, /schedule:[\s\S]*cron: "41 5 \* \* \*"/);
  assert.match(workflow, /workflow_dispatch:/);
  assert.doesNotMatch(workflow, /^\s+(pull_request|push|workflow_run|pull_request_target):/m);
  assert.match(workflow, /permissions: \{\}/);
  assert.match(workflow, /cancel-in-progress: false/);
  assert.match(workflow, /github.ref_name == github.event.repository.default_branch/);
  const [observe, health, publish, implement] = workflow.split(/\n  (?:observation-failure|publish|implement):/);
  assert.doesNotMatch(observe, /secrets\.|issues: write/);
  assert.doesNotMatch(publish, /secrets\./);
  assert.doesNotMatch(health, /secrets\./);
  assert.match(implement, /secrets.UPSTREAM_GATEWAY_COPILOT_TOKEN/);
  assert.doesNotMatch(workflow, /contents: write|pull-requests: write|npm (?:install|ci)|pnpm/);
});
