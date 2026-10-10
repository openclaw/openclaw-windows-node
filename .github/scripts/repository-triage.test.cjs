"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
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
  assert.equal(
    triage.staleBaseState(
      { baseRefName: "main", baseRefOid: "old", mergeStateStatus: "CLEAN" },
      "main",
    ),
    "no",
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
  assert.deepEqual(
    triage.extractReferencedIssueNumbers(
      [
        "Fixes #1",
        "> Fixes #2",
        "```text",
        "Fixes #3",
        "```",
        "`Fixes #4`",
        "    Fixes #5",
        "Not related to #6",
        "Unrelated to openclaw/openclaw-windows-node#7",
        "    - Fixes #8",
        "- Nested ownership",
        "    - Fixes #9",
        "    - Fixes #10",
        "- Earlier list",
        "",
        "```text",
        "example",
        "```",
        "",
        "    - Fixes #11",
        "Does not fix #12",
        "Will not close openclaw/openclaw-windows-node#13",
        "Not resolved by #14",
        "~~~text",
        "Fixes #15",
        "~~~",
        "Does not fix #16 or #17",
        "Not related to #18, #19",
        "Fixes not resolved by #21",
        "#22",
        "Cannot close #23",
        "Does not-fix #24",
        "Fixes",
        "#25",
        "~~~text",
        "~~~still code",
        "~~~",
        "Fixes #26",
        "```lang`oops",
        "Fixes #27",
        "Does fix #28",
        "Will close openclaw/openclaw-windows-node#29",
        "Can resolve #30",
        "Doesn't fix #31",
        "Didn’t close openclaw/openclaw-windows-node#32",
        "Won't resolve #33",
        "Can’t fix #34",
        "Couldn't close #35",
        "Wouldn’t resolve #36",
        "Shouldn't fix #37",
        "No longer fixes #38",
        "~~~text",
        "Fixes #20",
      ].join("\n"),
      "openclaw/openclaw-windows-node",
    ),
    [1, 26, 27, 28, 29, 30],
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
    closedByPullRequestsReferences: { nodes: [{ number: 10, state: "OPEN" }, null] },
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
      return {
        repository: {
          defaultBranchRef: { name: "main", target: { oid: "current-base" } },
        },
      };
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

test("isolates nullable and failed pull request detail collection", async () => {
  const pullRequests = [
    {
      number: 10,
      title: "docs: partial files",
      body: "",
      baseRefName: "main",
      mergeable: "UNKNOWN",
      mergeStateStatus: "UNKNOWN",
      changedFiles: 2,
      author: { __typename: "User", login: "alice" },
      labels: labels(),
      assignees: { nodes: [] },
      files: null,
      statusCheckRollup: null,
    },
    {
      number: 11,
      title: "fix: partial pagination",
      body: "",
      baseRefName: "main",
      mergeable: "MERGEABLE",
      mergeStateStatus: "CLEAN",
      changedFiles: 2,
      author: { __typename: "User", login: "bob" },
      labels: labels(),
      assignees: { nodes: [] },
      files: {
        pageInfo: { hasNextPage: true, endCursor: "next-file" },
        nodes: [null, { path: "docs/ONE.md" }],
      },
      statusCheckRollup: {
        state: "PENDING",
        contexts: {
          pageInfo: { hasNextPage: true, endCursor: "next-check" },
          nodes: [null],
        },
      },
    },
  ];
  const github = {
    graphql: async (query, variables) => {
      if (query.includes("issues(states: OPEN")) {
        return {
          repository: {
            issues: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [],
            },
          },
        };
      }
      if (query.includes("pullRequests(states: OPEN")) {
        return {
          repository: {
            pullRequests: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [...pullRequests, null],
            },
          },
        };
      }
      if (query.includes("pullRequest(number: $number)") && !query.includes("after:")) {
        assert.equal(variables.number, 10);
        return {
          repository: {
            pullRequest: {
              mergeable: "MERGEABLE",
              mergeStateStatus: "CLEAN",
            },
          },
        };
      }
      if (query.includes("after:")) {
        const error = new Error("temporary failure");
        error.status = 502;
        throw error;
      }
      return {
        repository: {
          defaultBranchRef: { name: "main", target: { oid: "current" } },
        },
      };
    },
    rest: { issues: { listEventsForTimeline: async () => ({ data: [] }) } },
    paginate: async () => [],
  };

  const data = await triage.collectRepositoryData({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
  });

  assert.equal(data.pullRequests.length, 2);
  assert.deepEqual(data.pullRequests[0].files.nodes, []);
  assert.equal(data.pullRequests[0].mergeable, "MERGEABLE");
  assert.equal(data.pullRequests[1].fileDataIncomplete, true);
  assert.equal(data.pullRequests[1].checkDataIncomplete, true);
  assert.deepEqual(triage.classifyPullRequest(data.pullRequests[1]), ["general"]);
  assert.equal(triage.summarizeChecks(data.pullRequests[1]).incomplete, true);
  assert.equal(data.warnings.length, 2);
  assert.match(data.warnings.join("\n"), /HTTP 502/);
});

test("isolates active ownership timeline failures and keeps canonical issue ownership", async () => {
  let timelineCalls = 0;
  const activeIssue = {
    number: 20,
    title: "Active issue",
    body: "",
    labels: labels(triage.ACTIVE_OWNERSHIP_LABEL),
    assignees: { nodes: [] },
    closedByPullRequestsReferences: {
      nodes: [{ number: 10, state: "OPEN" }, { number: 9, state: "CLOSED" }],
    },
  };
  const github = {
    graphql: async (query) => {
      if (query.includes("issues(states: OPEN")) {
        return {
          repository: {
            issues: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [activeIssue],
            },
          },
        };
      }
      if (query.includes("pullRequests(states: OPEN")) {
        return {
          repository: {
            pullRequests: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [
                {
                  number: 10,
                  title: "fix: issue",
                  body: "",
                  baseRefName: "main",
                  mergeable: "MERGEABLE",
                  mergeStateStatus: "CLEAN",
                  changedFiles: 1,
                  labels: labels(),
                  assignees: { nodes: [] },
                  files: { pageInfo: { hasNextPage: false }, nodes: [{ path: "src/a.cs" }] },
                },
              ],
            },
          },
        };
      }
      return { repository: { defaultBranchRef: { name: "main", target: { oid: "base" } } } };
    },
    rest: { issues: { listEventsForTimeline: async () => ({ data: [] }) } },
    paginate: async () => {
      timelineCalls += 1;
      const error = new Error("timeline unavailable");
      error.status = 502;
      throw error;
    },
  };

  const data = await triage.collectRepositoryData({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
  });

  assert.equal(timelineCalls, 1);
  assert.deepEqual(data.linkedPrsByIssue.get(20), [10]);
  assert.equal(data.timelineByNumber.has("issue:20"), false);
  assert.match(data.warnings[0], /Issue #20 ownership timeline: HTTP 502/);
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
  assert.match(unattributedCommit.reason, /provenance is unavailable/);

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
    { ...item, labels: labels(triage.ACTIVE_OWNERSHIP_LABEL, "IMPACT:SECURITY") },
    [applied],
    now,
  );
  assert.equal(securityExempt.removable, false);
  assert.match(securityExempt.reason, /IMPACT:SECURITY/);

  for (const event of ["review_requested", "reopened"]) {
    const recentActivity = triage.evaluateActiveOwnership(
      item,
      [
        applied,
        {
          event,
          created_at: "2026-08-25T12:00:00Z",
          actor: { login: "maintainer", type: "User" },
        },
      ],
      now,
    );
    assert.equal(recentActivity.removable, false);
    assert.equal(recentActivity.expired, false);
  }

  for (const event of ["review_requested", "reopened"]) {
    const automatedActivity = triage.evaluateActiveOwnership(
      item,
      [
        applied,
        {
          event,
          created_at: "2026-08-25T12:00:00Z",
          actor: { login: "github-actions[bot]", type: "Bot" },
        },
      ],
      now,
    );
    assert.equal(automatedActivity.removable, true);
    assert.equal(automatedActivity.expired, true);
  }

  const appApplied = triage.evaluateActiveOwnership(
    item,
    [{ ...applied, performed_via_github_app: { id: 123, name: "Automation" } }],
    now,
  );
  assert.equal(appApplied.removable, false);
  assert.match(appApplied.reason, /applied by a bot/);

  const externalForcePush = triage.evaluateActiveOwnership(
    item,
    [
      applied,
      {
        event: "head_ref_force_pushed",
        created_at: "2026-08-25T12:00:00Z",
        actor: { login: "external-author", type: "User" },
        author_association: "CONTRIBUTOR",
      },
    ],
    now,
  );
  assert.equal(externalForcePush.removable, true);
  assert.equal(externalForcePush.expired, true);

  const unidentifiedForcePush = triage.evaluateActiveOwnership(
    item,
    [
      applied,
      {
        event: "head_ref_force_pushed",
        created_at: "2026-08-25T12:00:00Z",
        actor: null,
      },
    ],
    now,
  );
  assert.equal(unidentifiedForcePush.removable, false);
  assert.match(unidentifiedForcePush.reason, /provenance is unavailable/);
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
  const auditSnapshots = [];
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
    onAudit: (entries) => auditSnapshots.push([...entries]),
  });

  assert.equal(removed.length, 1);
  assert.equal(removed[0].name, triage.ACTIVE_OWNERSHIP_LABEL);
  assert.match(audit[0], /removed/);
  assert.match(auditSnapshots.at(-1)[0], /removed/);
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

test("cleanup skips one failed fresh timeline without aborting other candidates", async () => {
  const now = new Date("2026-08-27T12:00:00Z");
  const oldLabel = (number) => ({
    number,
    state: "open",
    labels: labels(triage.ACTIVE_OWNERSHIP_LABEL),
    assignees: { nodes: [] },
  });
  const timeline = (number) => [{
    event: "labeled",
    created_at: "2026-08-19T12:00:00Z",
    label: { name: triage.ACTIVE_OWNERSHIP_LABEL },
    actor: { login: `maintainer-${number}`, type: "User" },
  }];
  const items = [oldLabel(12), oldLabel(13)];
  const removed = [];
  const github = {
    rest: {
      issues: {
        get: async ({ issue_number: number }) => ({ data: items.find((item) => item.number === number) }),
        listEventsForTimeline: async ({ issue_number: number }) => ({ data: timeline(number) }),
        removeLabel: async ({ issue_number: number }) => removed.push(number),
      },
    },
    paginate: async (method, request) => {
      if (request.issue_number === 12) {
        const error = new Error("timeline unavailable");
        error.status = 502;
        throw error;
      }
      return (await method(request)).data;
    },
  };
  const audit = await triage.removeExpiredActiveOwnership({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
    data: {
      issues: items,
      pullRequests: [],
      timelineByNumber: new Map([
        ["issue:12", timeline(12)],
        ["issue:13", timeline(13)],
      ]),
    },
    now,
  });

  assert.deepEqual(removed, [13]);
  assert.match(audit[0], /skipped.*HTTP 502/);
  assert.match(audit[1], /removed/);
});

test("cleanup does not treat verification 404 as an absent label", async () => {
  const now = new Date("2026-08-27T12:00:00Z");
  const item = {
    number: 12,
    state: "open",
    labels: labels(triage.ACTIVE_OWNERSHIP_LABEL),
    assignees: { nodes: [] },
  };
  const timeline = [{
    event: "labeled",
    created_at: "2026-08-19T12:00:00Z",
    label: { name: triage.ACTIVE_OWNERSHIP_LABEL },
    actor: { login: "maintainer", type: "User" },
  }];
  const github = {
    rest: {
      issues: {
        get: async () => {
          const error = new Error("not found");
          error.status = 404;
          throw error;
        },
        listEventsForTimeline: async () => ({ data: timeline }),
        removeLabel: async () => assert.fail("removeLabel must not run"),
      },
    },
    paginate: async () => timeline,
  };

  const audit = await triage.removeExpiredActiveOwnership({
    github,
    owner: "openclaw",
    repo: "openclaw-windows-node",
    data: {
      issues: [item],
      pullRequests: [],
      timelineByNumber: new Map([["issue:12", timeline]]),
    },
    now,
  });

  assert.deepEqual(item.labels, labels(triage.ACTIVE_OWNERSHIP_LABEL));
  assert.match(audit[0], /skipped.*HTTP 404/);
  assert.doesNotMatch(audit[0], /already absent/);
});

test("cleanup preserves caller-owned audit entries when journaling throws", async () => {
  const now = new Date("2026-08-27T12:00:00Z");
  const item = {
    number: 12,
    state: "open",
    labels: labels(triage.ACTIVE_OWNERSHIP_LABEL),
    assignees: { nodes: [] },
  };
  const timeline = [{
    event: "labeled",
    created_at: "2026-08-19T12:00:00Z",
    label: { name: triage.ACTIVE_OWNERSHIP_LABEL },
    actor: { login: "maintainer", type: "User" },
  }];
  const audit = [];
  const github = {
    rest: {
      issues: {
        get: async () => ({ data: item }),
        listEventsForTimeline: async () => ({ data: timeline }),
        removeLabel: async () => {},
      },
    },
    paginate: async () => timeline,
  };

  await assert.rejects(
    triage.removeExpiredActiveOwnership({
      github,
      owner: "openclaw",
      repo: "openclaw-windows-node",
      data: {
        issues: [item],
        pullRequests: [],
        timelineByNumber: new Map([["issue:12", timeline]]),
      },
      now,
      audit,
      onAudit: () => {
        throw new Error("journal unavailable");
      },
    }),
    /persist the cleanup audit journal/,
  );

  assert.equal(audit.length, 1);
  assert.match(audit[0], /#12: removed/);
});

test("run writes failure artifacts and cleanup journal before rethrowing", async (t) => {
  const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), "repository-triage-test-"));
  fs.rmSync(outputDir, { recursive: true, force: true });
  t.after(() => fs.rmSync(outputDir, { recursive: true, force: true }));
  const collectionError = new Error("collection failed");
  collectionError.status = 502;
  const github = {
    graphql: async () => {
      throw collectionError;
    },
  };
  const core = {
    summary: {
      addRaw: () => core.summary,
      write: async () => {},
    },
    info: () => {},
    warning: () => {},
  };

  await assert.rejects(
    triage.run({
      github,
      context: { repo: { owner: "openclaw", repo: "openclaw-windows-node" } },
      core,
      outputDir,
      operation: triage.CLEANUP_OPERATION,
      now: new Date("2026-08-27T12:00:00Z"),
    }),
    collectionError,
  );

  assert.equal(fs.existsSync(outputDir), true);
  assert.equal(fs.existsSync(path.join(outputDir, "repository-triage-failure.md")), true);
  assert.equal(fs.existsSync(path.join(outputDir, "repository-triage-failure.json")), true);
  const journal = JSON.parse(
    fs.readFileSync(path.join(outputDir, "repository-triage-cleanup-audit.json"), "utf8"),
  );
  assert.deepEqual(journal, ["Repository triage failed before completion (HTTP 502)."]);
});

function workflowJob(workflow, jobName) {
  const lines = workflow.split(/\r?\n/);
  const start = lines.findIndex((line) => line === `  ${jobName}:`);
  assert.notEqual(start, -1, `missing workflow job ${jobName}`);
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (/^  [A-Za-z0-9_-]+:$/.test(lines[index])) {
      end = index;
      break;
    }
  }
  return lines.slice(start, end).join("\n");
}

test("workflow is report-only by default and cleanup is manually gated", () => {
  const workflow = fs.readFileSync(
    path.join(__dirname, "..", "workflows", "repository-triage.yml"),
    "utf8",
  );
  const implementation = fs.readFileSync(
    path.join(__dirname, "repository-triage.cjs"),
    "utf8",
  );

  const reportJob = workflowJob(workflow, "report");
  const cleanupJob = workflowJob(workflow, "remove-expired-active-ownership");
  assert.match(workflow, /^  schedule:$/m);
  assert.match(workflow, /^        default: report-only$/m);
  assert.match(
    cleanupJob,
    /^    if: \$\{\{ github\.event_name == 'workflow_dispatch' && inputs\.operation == 'remove-expired-active-ownership' \}\}$/m,
  );
  assert.doesNotMatch(reportJob, /^\s+(issues|pull-requests|contents): write$/m);
  assert.match(reportJob, /^      issues: read$/m);
  assert.match(reportJob, /^      pull-requests: read$/m);
  assert.match(reportJob, /^        if: \$\{\{ always\(\) \}\}$/m);
  assert.match(reportJob, /^          if-no-files-found: warn$/m);
  assert.match(cleanupJob, /^      issues: write$/m);
  assert.match(cleanupJob, /^      pull-requests: write$/m);
  assert.match(cleanupJob, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/);
  assert.match(cleanupJob, /Run deterministic triage tests before mutation/);
  assert.match(cleanupJob, /persist-credentials: false/);
  assert.match(cleanupJob, /^        if: \$\{\{ always\(\) \}\}$/m);
  assert.match(cleanupJob, /^          if-no-files-found: warn$/m);
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
