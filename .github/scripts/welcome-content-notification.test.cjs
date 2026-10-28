"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const { notifyWelcomeChange } = require("./upstream-gateway-publish.cjs");

const before = "a".repeat(40);
const after = "b".repeat(40);
const trackerBody = "<!-- upstream-gateway-tracker:v1 -->\nHuman notes\n"
  + "<!-- upstream-gateway-owned:begin -->\n"
  + '<!-- upstream-gateway-state:v2:{"current":null,"acknowledged":[],"health":null} -->\n'
  + "<!-- upstream-gateway-owned:end -->";
const context = {
  repo: { owner: "openclaw", repo: "openclaw-windows-node" }, runId: 123,
  eventName: "schedule", ref: "refs/heads/main",
  payload: { repository: { default_branch: "main" } },
};

function sources({ hint = "Choose a workspace.", recent = "Recent chats", french = "Conversations récentes" } = {}) {
  return {
    "locales/en.ts": `export const en = {
  newSession: { hint: ${JSON.stringify(hint)}, title: "New session" },
  chat: {
    welcome: { suggestions: { whatCanYouDo: "What can you do?" } },
  },
};`,
    "locales/en-new-session-setup.ts": `const enNewSessionSetup = {
  newSession: {
    hint: en.newSession.hint,
  },
};`,
    "locales/en-command-palette.ts": `const enCommandPalette = {
  chat: {
    welcome: {
      recentSessions: ${JSON.stringify(recent)},
      suggestions: {
        whatCanYouDo: en.chat.welcome.suggestions.whatCanYouDo,
        summarizeRecentSessions: "Summarize my recent sessions",
        configureChannel: "Help me configure a channel",
        checkSystemHealth: "Check system health",
      },
    },
  },
};`,
    ".i18n/fr.tm.jsonl": JSON.stringify({ cache_key: "recent", segment_id: "chat.welcome.recentSessions",
      text_hash: crypto.createHash("sha256").update("Recent chats").digest("hex"), translated: french }) + "\n",
    ".i18n/nl.tm.jsonl": "", ".i18n/pt-BR.tm.jsonl": "", ".i18n/zh-CN.tm.jsonl": "", ".i18n/zh-TW.tm.jsonl": "",
  };
}

function receiver() {
  const state = { head: before, sources: { [before]: sources(), [after]: sources() }, comments: [], writes: [], calls: [],
    tracker: { number: 1829, state: "open", body: trackerBody },
    trackerReads: 0, closeAtEffect: false, uncertainPost: false };
  const github = { async request(route, args) {
    state.calls.push({ route, args });
    assert.equal(args.owner, "openclaw");
    if (route === "GET /repos/{owner}/{repo}/commits/main") {
      assert.equal(args.repo, "openclaw");
      return { data: { sha: state.head } };
    }
    if (route === "GET /repos/{owner}/{repo}/contents/{path}") {
      assert.equal(args.repo, "openclaw");
      assert.equal(args.headers.accept, "application/vnd.github.raw+json");
      const file = args.path.replace(/^ui\/src\/i18n\//, "");
      assert.ok(Object.hasOwn(state.sources[args.ref], file), "Missing pinned source " + file);
      return { data: state.sources[args.ref][file] };
    }
    assert.equal(args.repo, "openclaw-windows-node");
    assert.equal(args.issue_number, 1829);
    if (route === "GET /repos/{owner}/{repo}/issues/{issue_number}") {
      if (++state.trackerReads > 1 && state.closeAtEffect) state.tracker.state = "closed";
      return { data: state.tracker };
    }
    if (route === "GET /repos/{owner}/{repo}/issues/{issue_number}/comments") return { data: state.comments };
    if (route === "POST /repos/{owner}/{repo}/issues/{issue_number}/comments") {
      state.writes.push(args);
      state.comments.push({ id: state.comments.length + 1, body: args.body, user: { id: 41898282 } });
      if (state.uncertainPost) throw new Error("Connection lost after GitHub accepted the comment");
      return { data: { id: state.comments.length } };
    }
    assert.fail("Unexpected API effect: " + route);
  } };
  return { state, send: (options = {}) => notifyWelcomeChange({ github, context, reportOnly: false, ...options }) };
}

test("nightly monitoring records one baseline, stays quiet for unchanged content, and preserves tracker notes", async () => {
  const h = receiver();
  assert.equal((await h.send()).action, "baseline-recorded");
  assert.match(h.state.writes[0].body, /not a claim that Windows wording already matches/);
  h.state.head = after;
  h.state.sources[after]["locales/en.ts"] += "\n// Unrelated source edit\n";
  h.state.sources[after][".i18n/fr.tm.jsonl"] += JSON.stringify({ cache_key: "other",
    segment_id: "unrelated", text_hash: "other", translated: "Autre texte" }) + "\n";
  assert.equal((await h.send()).action, "unchanged");
  assert.equal(h.state.writes.length, 1);
  assert.equal(h.state.tracker.body, trackerBody);
});

test("translation-only changes post one actionable native-copy notice; copied human markers cannot suppress it", async () => {
  const h = receiver();
  await h.send();
  h.state.comments.push({ ...h.state.comments[0], user: { id: 7 } });
  h.state.head = after;
  h.state.sources[after] = sources({ french: "Chats récents" });
  const result = await h.send();
  assert.equal(result.action, "notified");
  assert.deepEqual(result.changedKeys, ["fr:chat.welcome.recentSessions"]);
  const body = h.state.writes[1].body;
  assert.ok(body.includes("https://github.com/openclaw/openclaw/compare/" + before + "..." + after));
  assert.match(body, /Strings\/\*\/Resources\.resw/);
  assert.match(body, /all six languages/);
  assert.match(body, /does not start an agent or change the app/);
  assert.equal((await h.send()).action, "unchanged");
  assert.equal(h.state.writes.length, 2);
});

test("English changes update all six effective locales and stale translations fall back", async () => {
  const h = receiver();
  await h.send();
  h.state.head = after;
  h.state.sources[after] = sources({ recent: "Your recent chats" });
  assert.deepEqual((await h.send()).changedKeys, [
    "en:chat.welcome.recentSessions", "fr:chat.welcome.recentSessions", "nl:chat.welcome.recentSessions",
    "pt-BR:chat.welcome.recentSessions", "zh-CN:chat.welcome.recentSessions", "zh-TW:chat.welcome.recentSessions",
  ]);
});

test("hint literals, added and removed welcome keys, and translation segment aliases are observed", async () => {
  const h = receiver();
  const entry = JSON.parse(h.state.sources[before][".i18n/fr.tm.jsonl"]);
  entry.segment_ids = [entry.segment_id];
  entry.segment_id = "unrelated";
  h.state.sources[before][".i18n/fr.tm.jsonl"] = JSON.stringify(entry) + "\n";
  await h.send();
  h.state.head = after;
  h.state.sources[after]["locales/en-new-session-setup.ts"] = h.state.sources[after]["locales/en-new-session-setup.ts"]
    .replace("en.newSession.hint", '"Start here."');
  h.state.sources[after]["locales/en-command-palette.ts"] = h.state.sources[after]["locales/en-command-palette.ts"]
    .replace('        checkSystemHealth: "Check system health",', '        planToday: "Plan today",');
  const result = await h.send();
  assert.equal(result.changedKeys.length, 18);
  assert.ok(result.changedKeys.includes("en:chat.welcome.suggestions.checkSystemHealth"));
  assert.ok(result.changedKeys.includes("fr:chat.welcome.suggestions.planToday"));
  assert.ok(result.changedKeys.includes("zh-TW:newSession.hint"));
  assert.ok(!result.changedKeys.includes("fr:chat.welcome.recentSessions"));
});

test("report-only and disabled-write observations never establish or advance a notification baseline", async () => {
  const h = receiver();
  assert.equal((await h.send({ reportOnly: true })).action, "baseline-needed");
  assert.equal(h.state.writes.length, 0);
  await h.send();
  h.state.head = after;
  h.state.sources[after] = sources({ french: "Chats récents" });
  for (const options of [
    { reportOnly: true }, { reportOnly: undefined },
    { context: { ...context, eventName: "workflow_dispatch", payload: { ...context.payload, inputs: { report_only: true } } } },
  ]) assert.equal((await h.send(options)).action, "would-notify");
  assert.equal(h.state.writes.length, 1);
  assert.equal((await h.send()).action, "notified");
});

test("foreign, non-default and unexpected event contexts cannot invoke the monitor", async () => {
  for (const changed of [
    { ...context, repo: { owner: "elsewhere", repo: "openclaw-windows-node" } },
    { ...context, ref: "refs/heads/feature" }, { ...context, eventName: "pull_request" },
    { ...context, eventName: "repository_dispatch", payload: { ...context.payload, action: "unexpected" } },
  ]) {
    const h = receiver();
    await assert.rejects(h.send({ context: changed }), /Invalid welcome observation/);
    assert.equal(h.state.calls.length, 0);
  }
});

test("damaged source, baseline and tracker state fail visibly before any comment", async () => {
  for (const alter of [
    (state) => { state.tracker.state = "closed"; },
    (state) => { state.tracker.body = "No marker"; },
    (state) => { state.closeAtEffect = true; },
    (state) => { state.sources[before]["locales/en.ts"] = "No welcome catalog"; },
    (state) => { state.sources[before]["locales/en-new-session-setup.ts"] = "No hint"; },
    (state) => { state.sources[before][".i18n/fr.tm.jsonl"] = "Not JSON"; },
    (state) => { state.comments.push({ user: { id: 41898282 }, body: "<!-- upstream-welcome-copy:v1:damaged -->" }); },
    (state) => { state.sources[before]["locales/en-command-palette.ts"] = state.sources[before]["locales/en-command-palette.ts"]
      .replace('"Recent chats"', '(() => { throw new Error("must not execute"); })()'); },
  ]) {
    const h = receiver();
    alter(h.state);
    await assert.rejects(h.send());
    assert.equal(h.state.writes.length, 0);
  }
});

test("an uncertain accepted POST is reconciled on the next observation without a duplicate", async () => {
  const h = receiver();
  h.state.uncertainPost = true;
  await assert.rejects(h.send(), /Connection lost/);
  assert.equal(h.state.writes.length, 1);
  assert.equal((await h.send()).action, "unchanged");
  assert.equal(h.state.writes.length, 1);
});

test("welcome collection runs with the existing nightly triggers, read-only switch and health reporting", () => {
  const workflow = fs.readFileSync(path.join(__dirname, "../workflows/upstream-gateway-protocol.yml"), "utf8");
  const welcome = workflow.split("\n  welcome-copy:")[1];
  assert.match(workflow, /cron: "41 5 \* \* \*"/);
  assert.match(workflow, /needs: \[observe, publish, implement, welcome-copy\]/);
  assert.match(workflow, /needs\.welcome-copy\.result == 'failure'/);
  assert.match(welcome, /vars\.UPSTREAM_GATEWAY_MONITOR_ENABLED != 'true'/);
  assert.match(welcome, /github\.event_name == 'workflow_dispatch' && inputs\.report_only/);
  assert.match(welcome, /reportOnly: process\.env\.REPORT_ONLY !== "false"/);
  assert.match(welcome, /retries: 0/);
  assert.doesNotMatch(welcome, /COPILOT_GITHUB_TOKEN|needs:|client_payload/);
  const ci = fs.readFileSync(path.join(__dirname, "../workflows/ci.yml"), "utf8");
  assert.match(ci, /node --test \.github\/scripts\/welcome-content-notification\.test\.cjs/);
});
