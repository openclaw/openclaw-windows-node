"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const triage = require("./repository-triage.cjs");

function labels(...names) {
  return { nodes: names.map((name) => ({ name })) };
}

test("classifies pull request lanes deterministically", () => {
  const docs = {
    title: "docs: clarify setup",
    author: { __typename: "User", login: "alice" },
    labels: labels(),
    changedFiles: 2,
    files: { nodes: [{ path: "docs/SETUP.md" }, { path: "README.md" }] },
  };
  const dependency = {
    title: "chore(deps): bump Example from 1.2.3 to 1.3.0",
    author: { __typename: "Bot", login: "dependabot[bot]" },
    labels: labels("dependencies"),
    changedFiles: 1,
    files: { nodes: [{ path: "src/Example/Example.csproj" }] },
  };
  const release = {
    title: "fix: repair Windows installer signing",
    author: { __typename: "User", login: "alice" },
    labels: labels(),
    changedFiles: 1,
    files: { nodes: [{ path: "installer.iss" }] },
  };

  assert.deepEqual(triage.classifyPullRequest(docs), ["docs-only"]);
  assert.deepEqual(triage.classifyPullRequest(dependency), ["dependency-patch-minor", "bot"]);
  assert.deepEqual(triage.classifyPullRequest(release), ["platform-specific", "release/package"]);
  assert.deepEqual(
    triage.classifyPullRequest({
      ...dependency,
      title: "chore(deps): bump actions/checkout from 6.0.0 to 6.1.0",
      files: { nodes: [{ path: ".github/workflows/ci.yml" }] },
    }),
    ["dependency-patch-minor", "bot"],
  );
  assert.deepEqual(
    triage.classifyPullRequest({ ...docs, changedFiles: 3 }),
    ["general"],
  );
});

test("reports check totals and stale base", () => {
  const checks = triage.summarizeChecks({
    statusCheckRollup: {
      state: "FAILURE",
      contexts: {
        nodes: [
          { __typename: "CheckRun", status: "COMPLETED", conclusion: "SUCCESS" },
          { __typename: "CheckRun", status: "COMPLETED", conclusion: "FAILURE" },
          { __typename: "CheckRun", status: "COMPLETED", conclusion: "STARTUP_FAILURE" },
          { __typename: "CheckRun", status: "IN_PROGRESS", conclusion: null },
          { __typename: "StatusContext", state: "SUCCESS" },
          { __typename: "StatusContext", state: "EXPECTED" },
        ],
      },
    },
  });

  assert.deepEqual(checks, {
    state: "FAILURE",
    passed: 2,
    failed: 2,
    pending: 2,
    skipped: 0,
  });
  assert.equal(
    triage.staleBaseState({ baseRefName: "main", mergeStateStatus: "BEHIND" }, "main"),
    "yes",
  );
  assert.equal(
    triage.staleBaseState({ baseRefName: "main", mergeStateStatus: "CLEAN" }, "main"),
    "no",
  );
  assert.equal(
    triage.staleBaseState({ baseRefName: "main", mergeStateStatus: "DIRTY" }, "main"),
    "unknown",
  );
  assert.deepEqual(
    triage.proofLabels({
      labels: labels("proof: sufficient", "status: 📣 needs proof", "P2"),
    }),
    ["proof: sufficient", "status: 📣 needs proof"],
  );
});

test("maps closing keywords, explicit references, and cross-references", () => {
  const body = [
    "Fixes #42",
    "Related: openclaw/openclaw-windows-node#44",
    "https://github.com/openclaw/openclaw-windows-node/issues/45",
    "Unrelated to #46",
    "Fixes other/repo#99",
  ].join("\n");
  assert.deepEqual(
    triage.extractReferencedIssueNumbers(body, "openclaw/openclaw-windows-node"),
    [42, 44],
  );
  assert.equal(
    triage.linkedPullRequestNumber(
      {
        event: "cross-referenced",
        source: {
          issue: {
            number: 51,
            pull_request: {},
            repository_url: "https://api.github.com/repos/openclaw/openclaw-windows-node",
            body: "Fixes #42",
          },
        },
      },
      new Set([51]),
      "openclaw/openclaw-windows-node",
      42,
    ),
    51,
  );
  assert.equal(
    triage.linkedPullRequestNumber(
      {
        event: "cross-referenced",
        source: {
          issue: {
            number: 52,
            pull_request: {},
            repository_url: "https://api.github.com/repos/openclaw/openclaw-windows-node",
            body: "Fixes #42",
          },
        },
      },
      new Set([51]),
      "openclaw/openclaw-windows-node",
      42,
    ),
    null,
  );
  assert.equal(
    triage.linkedPullRequestNumber(
      {
        event: "cross-referenced",
        source: {
          issue: {
            number: 51,
            pull_request: {},
            repository_url: "https://api.github.com/repos/other/repository",
            body: "Fixes #42",
          },
        },
      },
      new Set([51]),
      "openclaw/openclaw-windows-node",
      42,
    ),
    null,
  );
  assert.equal(
    triage.linkedPullRequestNumber(
      {
        event: "cross-referenced",
        source: {
          issue: {
            number: 51,
            pull_request: {},
            repository_url: "https://api.github.com/repos/openclaw/openclaw-windows-node",
            body: "Incidental mention with no current issue ownership",
          },
        },
      },
      new Set([51]),
      "openclaw/openclaw-windows-node",
      42,
    ),
    null,
  );
});

test("collects every nested file and check page before reporting", async () => {
  const pullRequest = {
    number: 10,
    title: "docs: update two pages",
    url: "https://github.com/openclaw/openclaw-windows-node/pull/10",
    body: "Fixes #20",
    baseRefName: "main",
    mergeStateStatus: "BEHIND",
    changedFiles: 2,
    author: { __typename: "User", login: "alice" },
    labels: labels(),
    assignees: { nodes: [] },
    files: {
      totalCount: 2,
      pageInfo: { hasNextPage: true, endCursor: "file-page-2" },
      nodes: [{ path: "docs/ONE.md" }],
    },
    statusCheckRollup: {
      state: "FAILURE",
      contexts: {
        totalCount: 2,
        pageInfo: { hasNextPage: true, endCursor: "check-page-2" },
        nodes: [
          {
            __typename: "CheckRun",
            status: "COMPLETED",
            conclusion: "SUCCESS",
          },
        ],
      },
    },
  };
  const issue = {
    number: 20,
    title: "Documentation gap",
    url: "https://github.com/openclaw/openclaw-windows-node/issues/20",
    author: { __typename: "User", login: "bob" },
    labels: labels(),
    assignees: { nodes: [] },
  };
  const github = {
    graphql: async (query) => {
      if (query.includes("pullRequests(states: OPEN")) {
        return {
          repository: {
            pullRequests: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [pullRequest],
            },
          },
        };
      }
      if (query.includes("issues(states: OPEN")) {
        return {
          repository: {
            issues: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [issue],
            },
          },
        };
      }
      if (query.includes("files(first: 100, after:")) {
        return {
          repository: {
            pullRequest: {
              files: {
                pageInfo: { hasNextPage: false, endCursor: null },
                nodes: [{ path: "docs/TWO.md" }],
              },
            },
          },
        };
      }
      if (query.includes("contexts(first: 100, after:")) {
        return {
          repository: {
            pullRequest: {
              statusCheckRollup: {
                contexts: {
                  pageInfo: { hasNextPage: false, endCursor: null },
                  nodes: [
                    {
                      __typename: "CheckRun",
                      status: "COMPLETED",
                      conclusion: "FAILURE",
                    },
                  ],
                },
              },
            },
          },
        };
      }
      return { repository: { defaultBranchRef: { name: "main" } } };
    },
    rest: { issues: { listEventsForTimeline: async () => ({ data: [] }) } },
    paginate: async () => [
      {
        event: "cross-referenced",
        source: {
          issue: {
            number: 10,
            pull_request: {},
            repository_url: "https://api.github.com/repos/openclaw/openclaw-windows-node",
            body: "Fixes #20",
          },
        },
      },
    ],
  };

  const data = await triage.collectRepositoryData({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
  });

  assert.equal(data.pullRequests[0].files.nodes.length, 2);
  assert.equal(data.pullRequests[0].statusCheckRollup.contexts.nodes.length, 2);
  assert.deepEqual(triage.classifyPullRequest(data.pullRequests[0]), ["docs-only"]);
  assert.deepEqual(triage.summarizeChecks(data.pullRequests[0]), {
    state: "FAILURE",
    passed: 1,
    failed: 1,
    pending: 0,
    skipped: 0,
  });
  assert.deepEqual(data.linkedPrsByIssue.get(20), [10]);
});

test("expires active ownership only when every safeguard passes", () => {
  const now = new Date("2026-08-27T12:00:00Z");
  const item = {
    labels: labels(triage.ACTIVE_OWNERSHIP_LABEL),
    assignees: { nodes: [] },
  };
  const applied = {
    event: "labeled",
    created_at: "2026-08-19T12:00:00Z",
    label: { name: triage.ACTIVE_OWNERSHIP_LABEL },
    actor: { login: "maintainer", type: "User" },
  };

  const removable = triage.evaluateActiveOwnership(item, [applied], now);
  assert.equal(removable.expired, true);
  assert.equal(removable.removable, true);
  assert.equal(removable.owner, "maintainer");

  const withActivity = triage.evaluateActiveOwnership(
    item,
    [
      applied,
      {
        event: "commented",
        created_at: "2026-08-25T12:00:00Z",
        actor: { login: "maintainer", type: "User" },
        author_association: "MEMBER",
      },
    ],
    now,
  );
  assert.equal(withActivity.expired, false);
  assert.equal(withActivity.removable, false);
  assert.match(withActivity.reason, /younger than 7 days/);

  const oldActivity = triage.evaluateActiveOwnership(
    item,
    [
      applied,
      {
        event: "commented",
        created_at: "2026-08-20T12:00:00Z",
        actor: { login: "maintainer", type: "User" },
        author_association: "MEMBER",
      },
    ],
    now,
  );
  assert.equal(oldActivity.expired, true);
  assert.equal(oldActivity.removable, true);

  const unattributedCommit = triage.evaluateActiveOwnership(
    item,
    [
      applied,
      {
        event: "committed",
        committer: { date: "2026-08-01T12:00:00Z" },
        actor: null,
      },
    ],
    now,
  );
  assert.equal(unattributedCommit.removable, false);
  assert.match(unattributedCommit.reason, /no server timestamp/);

  const assigned = triage.evaluateActiveOwnership(
    { ...item, assignees: { nodes: [{ login: "maintainer" }] } },
    [applied],
    now,
  );
  assert.equal(assigned.removable, false);
  assert.match(assigned.reason, /assignee/);

  const exempt = triage.evaluateActiveOwnership(
    { ...item, labels: labels(triage.ACTIVE_OWNERSHIP_LABEL, "no-stale") },
    [applied],
    now,
  );
  assert.equal(exempt.removable, false);
  assert.match(exempt.reason, /no-stale/);

  const securityExempt = triage.evaluateActiveOwnership(
    { ...item, labels: labels(triage.ACTIVE_OWNERSHIP_LABEL, "impact:security") },
    [applied],
    now,
  );
  assert.equal(securityExempt.removable, false);
  assert.match(securityExempt.reason, /impact:security/);
});

test("cleanup removes only the allowlisted label and is idempotent", async () => {
  const now = new Date("2026-08-27T12:00:00Z");
  const item = {
    number: 12,
    state: "open",
    labels: labels(triage.ACTIVE_OWNERSHIP_LABEL),
    assignees: { nodes: [] },
  };
  const timeline = [
    {
      event: "labeled",
      created_at: "2026-08-19T12:00:00Z",
      label: { name: triage.ACTIVE_OWNERSHIP_LABEL },
      actor: { login: "maintainer", type: "User" },
    },
  ];
  const removed = [];
  const github = {
    rest: {
      issues: {
        get: async () => ({ data: item }),
        listEventsForTimeline: async () => ({ data: timeline }),
        removeLabel: async (request) => removed.push(request),
      },
    },
    paginate: async (method, request) => (await method(request)).data,
  };
  const data = {
    issues: [item],
    pullRequests: [],
    timelineByNumber: new Map([["issue:12", timeline]]),
  };

  const audit = await triage.removeExpiredActiveOwnership({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
    data,
    now,
  });

  assert.equal(removed.length, 1);
  assert.equal(removed[0].name, triage.ACTIVE_OWNERSHIP_LABEL);
  assert.match(audit[0], /removed/);
  assert.equal(triage.evaluateActiveOwnership(item, timeline, now).present, false);

  const secondAudit = await triage.removeExpiredActiveOwnership({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
    data,
    now,
  });
  assert.equal(removed.length, 1);
  assert.match(secondAudit[0], /No expired active ownership labels/);
});

test("workflow is report-only by default and cleanup is manually gated", () => {
  const workflow = fs.readFileSync(
    path.join(__dirname, "..", "workflows", "repository-triage.yml"),
    "utf8",
  );
  const implementation = fs.readFileSync(
    path.join(__dirname, "repository-triage.cjs"),
    "utf8",
  );

  assert.match(workflow, /schedule:/);
  assert.match(workflow, /default: report-only/);
  assert.match(workflow, /operation == 'remove-expired-active-ownership'/);
  assert.match(workflow, /issues: read/);
  assert.equal(workflow.match(/statuses: read/g)?.length, 2);
  assert.match(workflow, /issues: write/);
  assert.match(workflow, /pull-requests: write/);
  assert.doesNotMatch(implementation, /\.merge\(/);
  assert.doesNotMatch(implementation, /state:\s*["']closed["']/);
  assert.doesNotMatch(implementation, /addLabels|setLabels/);
});

test("report escapes contributor-controlled Markdown and HTML", () => {
  const report = triage.renderReport(
    {
      repository: "openclaw/openclaw-windows-node",
      defaultBranchName: "main",
      pullRequests: [
        {
          number: 1,
          title: "[click](https://attacker.example) <img src=x> | spoof",
          url: "https://github.com/openclaw/openclaw-windows-node/pull/1",
          body: "",
          mergeable: "MERGEABLE",
          baseRefName: "main",
          mergeStateStatus: "CLEAN",
          changedFiles: 1,
          files: { nodes: [{ path: "src/example.cs" }] },
          author: { __typename: "User", login: "alice" },
          labels: labels(),
          assignees: { nodes: [] },
        },
      ],
      issues: [],
      timelineByNumber: new Map(),
      linkedPrsByIssue: new Map(),
    },
    { now: new Date("2026-08-27T12:00:00Z") },
  );

  assert.doesNotMatch(report, /\[click\]\(https:\/\/attacker\.example\)/);
  assert.doesNotMatch(report, /<img/);
  assert.match(report, /\\\[click\\\]\\\(https:\/\/attacker\\\.example\\\)/);
  assert.match(report, /&lt;img src=x&gt;/);
  assert.match(report, /\\\| spoof/);
});
