"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
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

test("report separates pending surface types and wiring evidence without claiming support", () => {
  const doc = structuredClone(schema);
  Object.assign(doc.methods, { health: {}, connect: {}, "future.answer": {} });
  doc.definitions.FutureEvent = {};
  const report = assess({ schema: doc });
  const before = structuredClone(report);
  const text = capability.renderAssessment(report);
  assert.match(text, /Reviewed decisions: supported: 0; intentionally unsupported: 2/);
  assert.match(text, /Awaiting assessment: \*\*10 protocol entries\*\*/);
  assert.match(text, /\| RPC methods \| 1 \| 5 \| 6 \|/);
  assert.match(text, /\| Explicit lifecycle events \| 0 \| 2 \| 2 \|/);
  assert.match(text, /\| Event schema definitions \| 0 \| 2 \| 2 \|/);
  assert.match(text, /Examples with recognized wiring: `health`/);
  assert.match(text, /Examples without recognized wiring: `connect`, `future.answer`, `question.get`/);
  assert.match(text, /recognized wiring is not proof of reachable or correct behavior/i);
  assert.match(text, /5 pending method\/event entries belong to explicitly tracked families/);
  assert.match(text, /5 other pending entries remain outside that candidate selection/);
  assert.match(text, /question-answer.*pending/);
  assert.match(text, /selected by .*gateway-capability-policy\.json.*not discovered by ranking/i);
  assert.match(text, /green workflow means collection succeeded, not Windows compatibility/i);
  assert.deepEqual(report, before, "presentation must not change evidence hashes, decisions or candidate identity");
});

test("RPC namespace table includes reviewed decisions and pending wiring without counting events", () => {
  const doc = structuredClone(schema);
  Object.assign(doc.methods, {
    health: {}, connect: {}, "last-heartbeat": {}, "health.check": {},
    "sessions.list": {}, "sessions.groups.list": {},
  });
  doc.definitions.HealthEvent = {};
  const report = assess({ schema: doc, local: { ...local, files: {
    ...local.files,
    "src/SessionClient.cs": 'Task Groups() => SendTrackedRequestAsync("sessions.groups.list");',
  } } });
  const before = structuredClone(report);
  const text = capability.renderAssessment(report);
  assert.match(text, /### RPC namespace breakdown/);
  assert.match(text, /4 RPC namespaces/);
  assert.match(text, /\| `health` \| 0 \| 0 \| 0 \| 1 \| 1 \|/);
  assert.match(text, /\| `question` \| 0 \| 2 \| 0 \| 3 \| 5 \|/);
  assert.match(text, /\| `sessions` \| 0 \| 0 \| 1 \| 1 \| 2 \|/);
  assert.match(text, /\| `top-level` \| 0 \| 0 \| 1 \| 2 \| 3 \|/);
  assert.ok(text.indexOf("| `health` |") < text.indexOf("| `question` |"));
  assert.ok(text.indexOf("| `question` |") < text.indexOf("| `sessions` |"));
  assert.ok(text.indexOf("| `sessions` |") < text.indexOf("| `top-level` |"));
  assert.match(text, /wire-name prefixes, not feature-completion claims/);
  assert.deepEqual(report, before, "namespace presentation cannot change support or candidate identity");
});

test("RPC namespace labels cannot inject Markdown columns, HTML or mentions", () => {
  const report = assess({ schema: { methods: { "bad|<@team>.get": {} }, definitions: {} } });
  const text = capability.renderAssessment(report);
  assert.match(text, /\| `bad  \(at\)team ` \| 0 \| 0 \| 0 \| 1 \| 1 \|/);
  assert.doesNotMatch(text, /bad\|<@team>/);
});

test("untracked unknowns remain a visible coverage gap, including when no family is selected", () => {
  const report = assess({ schema: { methods: { "future.answer": {} }, definitions: { FutureEvent: {} } } });
  const text = capability.renderAssessment(report);
  assert.match(text, /Tracked gap candidates: 0/);
  assert.match(text, /0 pending method\/event entries belong to explicitly tracked families/);
  assert.match(text, /2 other pending entries remain outside that candidate selection/);
  assert.match(text, /Coverage incomplete/);
  assert.match(text, /unknowns, not accepted exclusions/);
  assert.match(text, /No explicitly tracked capability families are present/);
  assert.doesNotMatch(text, /<!-- windows-capability-pending -->/);
});

test("large unreviewed inventories have bounded examples and do not create automatic candidates", () => {
  const report = assess({ schema: {
    methods: Object.fromEntries(Array.from({ length: 600 }, (_, index) => [`future.method${index}`, {}])),
    definitions: {},
  } });
  const text = capability.renderAssessment(report);
  assert.match(text, /\| RPC methods \| 0 \| 600 \| 600 \|/);
  assert.match(text, /Examples without recognized wiring: `future.method0`, `future.method1`, `future.method10`/);
  assert.doesNotMatch(text, /`future.method100`/);
  assert.ok(text.length < 5000, "full surface inventories belong in report.json, not issue bodies");
  assert.deepEqual(report.pendingGaps, []);
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

for (const newline of ["\n", "\r\n"]) {
  test(`recognized Gateway request helpers retain dispatch evidence (${JSON.stringify(newline)})`, () => {
    const wizard = [
      "class Client {",
      '  Task Start() => client.SendWizardRequestAsync(',
      '    "channels.start", new { channel = "test" });',
      '  // SendWizardRequestAsync("fake.comment");',
      '  string example = """ SendWizardRequestAsync("fake.literal") """;',
      "}",
    ].join(newline);
    const placement = [
      "class Placement {",
      '  Task Create() => RequestAsync("sessions.create", new { });',
      '  Task Projects() => RequestAsync("projects.list", new { });',
      '  Task Http() => unrelated.RequestAsync("fake.http", new { });',
      "}",
    ].join(newline);
    const evidence = capability.wiringCandidates({
      "src/Client.cs": wizard,
      "src/OpenClaw.Shared/Sessions/ChatPlacement.cs": placement,
      "src/Unrelated.cs": placement,
      "tests/ClientTests.cs": wizard,
    });
    assert.deepEqual(evidence.map(({ name, kind, file, line }) => ({ name, kind, file, line })), [
      { name: "channels.start", kind: "dispatch", file: "src/Client.cs", line: 2 },
      { name: "sessions.create", kind: "dispatch", file: "src/OpenClaw.Shared/Sessions/ChatPlacement.cs", line: 2 },
      { name: "projects.list", kind: "dispatch", file: "src/OpenClaw.Shared/Sessions/ChatPlacement.cs", line: 3 },
    ]);
    assert.ok(evidence.every((item) => /^[a-f0-9]{64}$/.test(item.sha256)));
  });
}

for (const newline of ["\n", "\r\n"]) {
  test(`owner-scoped response, category and captured-transport helpers retain dispatch evidence (${JSON.stringify(newline)})`, () => {
    const owners = [
      ["src/OpenClaw.Shared/OpenClawGatewayClient.cs", [
        ["SendResponseRequestAsync", "config.apply"],
        ["SendPairingDecisionAsync", "device.pair.reject"],
        ["SendPairingDecisionAsync", "node.pair.reject"],
        ["SendCronMutationAsync", "cron.add"],
      ]],
      ["src/OpenClaw.Shared/OpenClawGatewayClient.Protocol.cs", [
        ["SendResponseRequestAsync", "sessions.delete"],
      ]],
      ["src/OpenClaw.Shared/CronDetails.cs", [
        ["SendCronDetailsRequestAsync", "cron.get"],
        ["SendCronDetailsRequestAsync", "cron.scratch.get"],
        ["SendCronDetailsRequestAsync", "cron.scratch.set"],
        ["SendCronDetailsRequestAsync", "cron.history"],
      ]],
      ["src/OpenClaw.Shared/Models/ModelSignInSession.cs", [
        ["RequestAsync", "models.authStatus"],
        ["RequestAsync", "models.authLogin"],
        ["RequestAsync", "wizard.next"],
        ["RequestAsync", "wizard.cancel"],
      ]],
      ["src/OpenClaw.Shared/ChannelPairing.cs", [
        ["_request", "channels.pairing.list"],
      ]],
    ];
    const files = {};
    const expected = [];
    for (const [file, calls] of owners) {
      const body = ["class Client {"];
      for (const [index, [helper, name]] of calls.entries()) {
        body.push(`  Task Call${index}() => ${helper}(`, `    "${name}", parameters);`);
        expected.push({ name, kind: "dispatch", file, line: 2 + index * 2 });
      }
      body.push("}");
      files[file] = body.join(newline);
    }
    const evidence = capability.wiringCandidates(files);
    assert.deepEqual(evidence.map(({ name, kind, file, line }) => ({ name, kind, file, line })), expected);
    for (const item of evidence)
      assert.equal(item.sha256, crypto.createHash("sha256").update(files[item.file]).digest("hex"));
    const report = assess({
      schema: { methods: { ...schema.methods, ...Object.fromEntries(expected.map(({ name }) => [name, {}])) },
        definitions: {} },
      local: { ...local, files },
    });
    for (const { name } of expected)
      assert.equal(report.assessments.find((item) => item.name === name).status, "pending");
    assert.deepEqual(report.pendingGaps, ["question-answer"], "recognition must not add policy-selected candidates");
  });

  test(`owner-specific helpers reject wrong files, receivers and non-code decoys (${JSON.stringify(newline)})`, () => {
    const owners = [
      ["src/OpenClaw.Shared/OpenClawGatewayClient.cs", "SendResponseRequestAsync", "RequestAsync"],
      ["src/OpenClaw.Shared/OpenClawGatewayClient.cs", "SendPairingDecisionAsync", "RequestAsync"],
      ["src/OpenClaw.Shared/OpenClawGatewayClient.cs", "SendCronMutationAsync", "RequestAsync"],
      ["src/OpenClaw.Shared/OpenClawGatewayClient.Protocol.cs", "SendResponseRequestAsync", "RequestAsync"],
      ["src/OpenClaw.Shared/CronDetails.cs", "SendCronDetailsRequestAsync", "RequestAsync"],
      ["src/OpenClaw.Shared/Models/ModelSignInSession.cs", "RequestAsync", "SendCronDetailsRequestAsync"],
      ["src/OpenClaw.Shared/ChannelPairing.cs", "_request", "SendCronDetailsRequestAsync"],
    ];
    for (const [file, helper, foreignHelper] of owners) {
      const decoys = [
        "class Client {",
        `  Task Receiver() => other.${helper}("fake.receiver", parameters);`,
        "  Task SpacedReceiver() => other.",
        `    /* receiver */ ${helper}("fake.spacedReceiver", parameters);`,
        `  Task WrongOwner() => ${foreignHelper}("fake.owner", parameters);`,
        `  Task UnknownHelper() => UntracedRequestAsync("fake.untraced", parameters);`,
        `  Task Variable() => ${helper}(method, parameters);`,
        '  bool Advertised() => Supports("fake.advertised");',
        `  // ${helper}("fake.comment", parameters);`,
        `  /* ${helper}("fake.blockComment", parameters); */`,
        `  string example = """ ${helper}("fake.literal", parameters) """;`,
        "}",
      ].join(newline);
      assert.deepEqual(capability.wiringCandidates({ [file]: decoys }), [], `${file}: ${helper}`);
      const realCall = `class Client { Task Call() => ${helper}("fake.wrongFile", parameters); }`;
      assert.deepEqual(capability.wiringCandidates({
        [`src/Unrelated/${path.posix.basename(file)}`]: realCall,
        [`tests/${file}`]: realCall,
      }), [], `${helper} must not become a global Gateway helper`);
    }
  });
}

for (const newline of ["\n", "\r\n"]) {
  test(`owner-scoped setup requests and serialized approval frames retain evidence (${JSON.stringify(newline)})`, () => {
    const setup = [
      "class Setup {",
      "  Task Detect() => ExecuteAsync<SetupDetection>(",
      '    "openclaw.setup.detect", route, timeout, ct);',
      '  Task Verify() => ExecuteAsync<SetupVerification>("openclaw.setup.verify", route, timeout, ct);',
      '  Task Poll() => ExecuteAsync<WizardResult>("wizard.next", route, timeout, ct);',
      '  bool Advertised() => HasMethod("fake.advertised");',
      '  Task Http() => other.ExecuteAsync<SetupDetection>("fake.http", route, timeout, ct);',
      '  // ExecuteAsync<SetupDetection>("fake.comment", route, timeout, ct);',
      '  string example = """ ExecuteAsync<SetupDetection>("fake.literal", route, timeout, ct) """;',
      '  string quotedExample = """"',
      '    """ ExecuteAsync<SetupDetection>("fake.longDelimiter", route, timeout, ct) """',
      '    """";',
      "}",
    ].join(newline);
    const approval = [
      "class Client {",
      "  Task Resolve() => SendRawAsync(",
      "    SerializeRequest(",
      "      requestId,",
      '      "exec.approval.resolve", payload), generation, ct);',
      '  string Other() => other.SerializeRequest(requestId, "fake.receiver", payload);',
      '  // SerializeRequest(requestId, "fake.comment", payload);',
      '  string example = """ SerializeRequest(requestId, "fake.literal", payload) """;',
      "}",
    ].join(newline);
    const files = {
      "src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs": setup,
      "src/OpenClaw.Shared/OpenClawGatewayClient.cs": approval,
      "src/UnrelatedSetup.cs": setup,
      "src/UnrelatedClient.cs": approval,
      "tests/GatewayAiSetupClientTests.cs": setup,
      "tests/ApprovalTests.cs": approval,
    };
    const evidence = capability.wiringCandidates(files);
    assert.deepEqual(evidence.map(({ name, kind, file, line }) => ({ name, kind, file, line })), [
      { name: "openclaw.setup.detect", kind: "dispatch", file: "src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs", line: 2 },
      { name: "openclaw.setup.verify", kind: "dispatch", file: "src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs", line: 4 },
      { name: "wizard.next", kind: "dispatch", file: "src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs", line: 5 },
      { name: "exec.approval.resolve", kind: "construction", file: "src/OpenClaw.Shared/OpenClawGatewayClient.cs", line: 3 },
    ]);
    for (const item of evidence)
      assert.equal(item.sha256, crypto.createHash("sha256").update(files[item.file]).digest("hex"));
    const report = assess({ local: { ...local, files } });
    assert.ok(report.assessments.every((item) => item.status !== "supported"));
  });
}

for (const newline of ["\n", "\r\n"]) {
  test(`handler evidence requires the recognized protocol owner and switch context (${JSON.stringify(newline)})`, () => {
    const file = [
      "class Client {",
      "  private bool HandleKnownResponse(string method, JsonElement payload) {",
      '    var braces = "} switch (method) { case \\"fake.string\\":";',
      '    // switch (method) { case "fake.comment": }',
      "    switch (method) {",
      '      case "sessions.list":',
      "      {",
      '        switch (action) { case "status": break; }',
      '        return true;',
      "      }",
      '      case "config.get":',
      '      case "config.schema": return true;',
      "    }",
      '    switch (action) { case "fake.action": break; }',
      "    return false;",
      "  }",
      "  private void HandleRequestError(string? method, JsonElement root, long generation) {",
      "    if (IsUnknownMethodError()) {",
      '      switch (method) { case "usage.status": break; }',
      "    }",
      "  }",
      '  private void OnTrayAction(string method) { switch (method) { case "status": break; } }',
      "}",
    ].join(newline);
    const evidence = capability.wiringCandidates({
      "src/OpenClaw.Shared/OpenClawGatewayClient.cs": file,
      "src/Unrelated.cs": file,
      "tests/OpenClawGatewayClient.cs": file,
    });
    assert.deepEqual(evidence.map(({ name, kind, surfaceKind, file, line }) => ({ name, kind, surfaceKind, file, line })), [
      ["sessions.list", 6], ["config.get", 11], ["config.schema", 12], ["usage.status", 19],
    ].map(([name, line]) => ({
      name, line, kind: "handler", surfaceKind: "method", file: "src/OpenClaw.Shared/OpenClawGatewayClient.cs",
    })));
    assert.ok(evidence.every((item) => /^[a-f0-9]{64}$/.test(item.sha256)));
  });
}

test("event handlers cannot supply evidence to same-named RPCs or definitions", () => {
  const file = [
    "class Client {",
    '  public Task Health() { return SendTrackedRequestAsync("health"); }',
    "  private void HandleEventForConnection(JsonElement root, int size, long generation) {",
    "    switch (eventType) {",
    '      case "agent": break;',
    '      case "health": break;',
    '      case "question.requested": break;',
    '      case "FutureEvent": break;',
    "    }",
    "  }",
    '  private void OnAction(string action) { switch (action) { case "status": break; } }',
    "}",
  ].join("\n");
  const supportPolicy = structuredClone(capability.policy);
  supportPolicy.families[0].events = ["agent", "health", "question.requested"];
  const report = assess({
    schema: {
      methods: { agent: {}, status: {}, health: {}, "question.list": {}, "question.requested": {} },
      definitions: { FutureEvent: {} },
    },
    local: { ...local, files: { "src/OpenClaw.Shared/OpenClawGatewayClient.cs": file } },
    supportPolicy,
  });
  for (const id of ["method:agent", "method:status", "method:question.requested", "definition:FutureEvent"])
    assert.deepEqual(report.assessments.find((item) => item.id === id).evidence, [], id);
  assert.deepEqual(report.assessments.find((item) => item.id === "method:health").evidence.map((item) => item.kind), ["dispatch"]);
  for (const name of ["agent", "health", "question.requested"]) {
    const evidence = report.assessments.find((item) => item.id === `event:${name}`).evidence;
    assert.equal(evidence.length, 1);
    assert.equal(evidence[0].kind, "handler");
    assert.equal(evidence[0].surfaceKind, "event");
  }
  assert.ok(report.assessments.every((item) => item.status === "pending"));
});

test("hyphenated Gateway method names retain dispatch, construction and handler evidence", () => {
  const file = [
    "class Client {",
    '  Task Read() => SendTrackedRequestAsync("last-heartbeat");',
    '  Task Enable() => SendWizardRequestAsync("set-heartbeats", new { });',
    '  Task Wake() => SendRawAsync(SerializeRequest(id, "system-event", payload), generation, ct);',
    '  object Frame() => new { method = "system-event" };',
    "  private bool HandleKnownResponse(string method, JsonElement payload) {",
    '    switch (method) { case "system-presence": return true; }',
    "    return false;",
    "  }",
    "}",
  ].join("\n");
  const evidence = capability.wiringCandidates({ "src/OpenClaw.Shared/OpenClawGatewayClient.cs": file });
  assert.deepEqual(evidence.map(({ name, kind, line }) => ({ name, kind, line })), [
    { name: "last-heartbeat", kind: "dispatch", line: 2 },
    { name: "set-heartbeats", kind: "dispatch", line: 3 },
    { name: "system-event", kind: "construction", line: 5 },
    { name: "system-event", kind: "construction", line: 4 },
    { name: "system-presence", kind: "handler", line: 7 },
  ]);
});

test("current protocol owners retain RPC and event handlers without tray or deep-link collisions", () => {
  const files = Object.fromEntries([
    "src/OpenClaw.Shared/OpenClawGatewayClient.cs",
    "src/OpenClaw.Shared/OpenClawGatewayClient.Protocol.cs",
    "src/OpenClaw.Shared/Sessions/ChatPlacement.cs",
    "src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs",
    "src/OpenClaw.Shared/WindowsNodeClient.cs",
    "src/OpenClaw.Tray.WinUI/App.xaml.cs",
    "src/OpenClaw.Tray.WinUI/Services/DeepLinkHandler.cs",
  ].map((file) => [file, fs.readFileSync(path.join(__dirname, "../..", file), "utf8")]));
  const evidence = capability.wiringCandidates(files);
  const handlers = evidence.filter((item) => item.kind === "handler");
  for (const name of ["sessions.list", "config.get", "cron.add", "node.invoke", "usage.status"])
    assert.ok(handlers.some((item) => item.name === name && item.surfaceKind === "method"), name);
  for (const name of ["agent", "health", "node.invoke.request"])
    assert.ok(handlers.some((item) => item.name === name && item.surfaceKind === "event"), name);
  assert.ok(!handlers.some((item) => item.name === "agent" && item.surfaceKind === "method"));
  assert.ok(!evidence.some((item) => item.name === "status"));
  assert.ok(!handlers.some((item) => item.file.includes("OpenClaw.Tray.WinUI")));
  assert.ok(evidence.some((item) => item.name === "chat.send" && item.kind === "construction"));
  for (const name of ["channels.start", "chat.abort", "sessions.create", "sessions.dispatch", "projects.list", "fs.listDir"])
    assert.ok(evidence.some((item) => item.name === name && item.kind === "dispatch"), name);
  for (const name of ["openclaw.setup.detect", "openclaw.setup.verify"])
    assert.ok(evidence.some((item) => item.name === name && item.kind === "dispatch"), name);
  assert.ok(evidence.some((item) => item.name === "exec.approval.resolve" && item.kind === "construction"));
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
  assert.match(capability.renderAssessment(resolved), /Reviewed decisions: supported: 1; intentionally unsupported: 2/);
  assert.match(capability.renderAssessment(resolved), /\| method:question.resolve \| supported \| Reviewed reachable/);
  assert.match(capability.renderAssessment(resolved), /\| `question` \| 1 \| 2 \| 0 \| 2 \| 5 \|/);
  for (const change of [
    { local: { ...local, files: { ...local.files, "src/Client.cs": "changed" } } },
    { local: { ...local, files: { ...local.files, "tests/ClientTests.cs": "changed" } } },
    { upstreamHash: "c".repeat(64) },
  ]) assert.equal(assess({ supportPolicy, ...change }).assessments.find((item) => item.name === "question.resolve").status, "pending");
  supportPolicy.decisions.at(-1).evidence.pop();
  const stale = assess({ supportPolicy });
  assert.equal(stale.assessments.find((item) => item.name === "question.resolve").status, "pending");
  assert.match(capability.renderAssessment(stale), /Reviewed decisions: supported: 0; intentionally unsupported: 2/);
  assert.match(capability.renderAssessment(stale), /Awaiting assessment: \*\*6 protocol entries\*\*/);
  assert.match(capability.renderAssessment(stale), /\| `question` \| 0 \| 2 \| 0 \| 3 \| 5 \|/);
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

test("exact publication separates publisher provenance from declared source existence", async () => {
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
  const verified = await monitor.verifyPublication(publication, metadata, undefined, load);
  assert.deepEqual(verified, { workflowSha: head, declaredSourceCommit: head, sourceCoverage: "unverified" });
  assert.equal(calls.length, 2);
  assert.ok(calls.every((url) => !url.includes("/latest")));
  await assert.rejects(monitor.verifyPublication(publication, { ...metadata, version: "2026.9.10" }, undefined, load), /registry/);
  const sourceCommit = "bcfc88812a35243893585dbeca87ca41b48272ca";
  const toolingCommit = "2b988ee83444f08ccaf37aa5e98f370726c75f6e";
  statement.predicate.buildDefinition.resolvedDependencies[0].digest.gitCommit = toolingCommit;
  const discrepancy = await monitor.verifyPublication({ ...publication, source_commit: sourceCommit }, metadata, undefined,
    async (url) => url.startsWith("https://api.github.com/") ? { sha: sourceCommit } : load(url));
  assert.deepEqual(discrepancy, { workflowSha: toolingCommit, declaredSourceCommit: sourceCommit, sourceCoverage: "unverified" });
  await assert.rejects(monitor.verifyPublication(publication, metadata, undefined,
    async (url) => url.startsWith("https://api.github.com/") ? { sha: "c".repeat(40) } : load(url)), /commit mismatch/);
  statement.subject[0].name = "pkg:npm/%40openclaw/gateway-protocol@2026.9.9";
  await monitor.verifyPublication({ ...publication, package_name: "@openclaw/gateway-protocol" },
    { ...metadata, name: "@openclaw/gateway-protocol" }, undefined, load);
  statement.subject[0].name = "pkg:npm/%40attacker/gateway-protocol@2026.9.9";
  await assert.rejects(monitor.verifyPublication({ ...publication, package_name: "@openclaw/gateway-protocol" },
    { ...metadata, name: "@openclaw/gateway-protocol" }, undefined, load), /subject/);
});

test("family identity follows relevant contract and references, not daily observation churn", () => {
  const doc = structuredClone(schema);
  doc.definitions.QuestionResolvedEvent = { properties: { answer: { $ref: "#/definitions/Answer" } } };
  doc.definitions.Answer = { type: "string" };
  const initial = assess({ schema: doc }).families[0].candidateHash;
  assert.equal(assess({ schema: doc, upstreamHash: "c".repeat(64),
    local: { ...local, head: "d".repeat(40), files: { ...local.files, "src/Unrelated.cs": "changed" } } }).families[0].candidateHash, initial);
  doc.definitions.UnrelatedEvent = { type: "object" };
  doc.definitions.Answer.description = "changed annotation";
  assert.equal(assess({ schema: doc }).families[0].candidateHash, initial);
  doc.definitions.Answer.enum = ["yes", "no"];
  assert.notEqual(assess({ schema: doc }).families[0].candidateHash, initial);
  delete doc.definitions.Answer.enum;
  doc.definitions.QuestionResolvedEvent.properties.description = { type: "string" };
  assert.notEqual(assess({ schema: doc }).families[0].candidateHash, initial, "a property named description is not an annotation");
  delete doc.definitions.Answer;
  assert.throws(() => assess({ schema: doc }), /Missing referenced/);
  const policy = structuredClone(capability.policy);
  policy.families[0].obligations.push("New reviewed scope");
  assert.notEqual(assess({ supportPolicy: policy }).families[0].candidateHash, assess().families[0].candidateHash);
});

test("publication and report-only paths preserve read/write separation and no payload shell interpolation", () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8");
  assert.match(workflow, /repository_dispatch:\s+types: \[gateway-protocol-published\]/);
  assert.doesNotMatch(workflow, /\$\{\{\s*github\.event\.client_payload/);
  const observe = workflow.split("\n  observation-failure:")[0];
  assert.doesNotMatch(observe, /secrets\.|: write/);
  assert.equal((workflow.match(/!inputs.report_only/g) ?? []).length, 2);
  const collector = fs.readFileSync(path.join(__dirname, "upstream-gateway-protocol.cjs"), "utf8");
  assert.doesNotMatch(collector, /COPILOT_GITHUB_TOKEN/);
  const requests = [...collector.matchAll(/github[.]request[(]"([^"]+)"/g)].map((match) => match[1]);
  assert.ok(requests.length > 0);
  assert.ok(requests.every((route) => route.startsWith("GET ")), "collector GitHub requests must be read-only");
});
