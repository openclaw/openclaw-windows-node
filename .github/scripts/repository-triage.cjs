"use strict";

const fs = require("node:fs");
const path = require("node:path");

const ACTIVE_OWNERSHIP_LABEL = "status: 🚢 actively landing";
const ACTIVE_OWNERSHIP_EXPIRY_DAYS = 7;
const CLEANUP_OPERATION = "remove-expired-active-ownership";
const TRUSTED_ASSOCIATIONS = new Set(["OWNER", "MEMBER", "COLLABORATOR"]);
const CLEANUP_EXEMPT_LABELS = new Set([
  "P0",
  "clawsweeper:needs-security-review",
  "impact:security",
  "merge-risk: 🚨 security-boundary",
  "no-stale",
  "security",
]);

function labelsOf(item) {
  const labels = item.labels?.nodes ?? item.labels ?? [];
  return labels
    .map((label) => (typeof label === "string" ? label : label?.name))
    .filter(Boolean);
}

function assigneesOf(item) {
  const assignees = item.assignees?.nodes ?? item.assignees ?? [];
  return assignees
    .map((assignee) => assignee?.login ?? assignee)
    .filter(Boolean);
}

function authorType(item) {
  const login = String(item.author?.login ?? item.user?.login ?? "").toLowerCase();
  const labels = labelsOf(item).map((label) => label.toLowerCase());
  const isRepoAssist = labels.includes("repo-assist");

  if (isRepoAssist) {
    return "repo-assist";
  }

  if (
    item.author?.__typename === "Bot" ||
    item.user?.type === "Bot" ||
    login.endsWith("[bot]") ||
    ["dependabot", "github-actions", "copilot"].some((name) => login.includes(name))
  ) {
    return "bot";
  }

  return "human";
}

function isDocumentationPath(filePath) {
  const normalized = filePath.toLowerCase();
  return (
    normalized.startsWith("docs/") ||
    normalized === "readme.md" ||
    normalized === "agents.md" ||
    normalized.endsWith(".md") ||
    normalized.endsWith(".adoc") ||
    normalized.startsWith(".github/issue_template/") ||
    normalized === ".github/pull_request_template.md"
  );
}

function isDependencyPath(filePath) {
  const normalized = filePath.toLowerCase();
  return (
    /^\.github\/workflows\/.+\.ya?ml$/.test(normalized) ||
    normalized.endsWith(".csproj") ||
    normalized.endsWith(".props") ||
    normalized.endsWith(".targets") ||
    normalized.endsWith("packages.lock.json") ||
    normalized.endsWith("package.json") ||
    normalized.endsWith("package-lock.json") ||
    normalized === ".github/dependabot.yml"
  );
}

function dependencyLane(pr, files, labels) {
  const title = String(pr.title ?? "");
  const dependencySignal =
    labels.includes("dependencies") ||
    authorType(pr) === "bot" ||
    /\bdeps?\b|\bdependenc/i.test(title);

  if (!dependencySignal || !files.some(isDependencyPath)) {
    return null;
  }

  const versionMatch = title.match(
    /\bfrom\s+v?(\d+)(?:\.(\d+))?(?:\.(\d+))?\s+to\s+v?(\d+)(?:\.(\d+))?(?:\.(\d+))?/i,
  );
  if (!versionMatch) {
    return "dependency-update";
  }

  const oldMajor = Number(versionMatch[1]);
  const newMajor = Number(versionMatch[4]);
  return newMajor > oldMajor ? "dependency-major" : "dependency-patch-minor";
}

function classifyPullRequest(pr) {
  const files = (pr.files?.nodes ?? pr.files ?? [])
    .map((file) => file?.path ?? file)
    .filter(Boolean);
  const labels = labelsOf(pr);
  const labelsLower = labels.map((label) => label.toLowerCase());
  const searchable = [pr.title, ...files].filter(Boolean).join(" ").toLowerCase();
  const lanes = [];

  if (
    files.length > 0 &&
    files.length === pr.changedFiles &&
    files.every(isDocumentationPath)
  ) {
    lanes.push("docs-only");
  }

  const dependency = dependencyLane(pr, files, labelsLower);
  if (dependency) {
    lanes.push(dependency);
  }

  if (authorType(pr) === "repo-assist" || labelsLower.includes("repo-assist")) {
    lanes.push("bot/repo-assist");
  } else if (authorType(pr) === "bot") {
    lanes.push("bot");
  }

  if (/\b(windows|winui|wsl|msix|inno|macos|linux)\b|installer\.iss/i.test(searchable)) {
    lanes.push("platform-specific");
  }

  if (
    /\b(release|package|packaging|installer|signing|msix)\b/i.test(searchable) ||
    files.some((file) =>
      /(^|\/)(installer\.iss|directory\.packages\.props)$|\.github\/workflows\/.*release/i.test(
        file,
      ),
    )
  ) {
    lanes.push("release/package");
  }

  return lanes.length > 0 ? [...new Set(lanes)] : ["general"];
}

function classifyIssue(issue) {
  const labels = labelsOf(issue).map((label) => label.toLowerCase());
  const searchable = [issue.title, issue.body, ...labels].filter(Boolean).join(" ");
  const lanes = [];

  if (
    labels.includes("stale") ||
    labels.includes("clawsweeper:not-repro-on-main") ||
    /\b(stale|no repro|not repro|cannot reproduce)\b/i.test(searchable)
  ) {
    lanes.push("stale/no-repro");
  }

  if (/\b(windows|winui|wsl|msix|inno|macos|linux)\b/i.test(searchable)) {
    lanes.push("platform-specific");
  }

  if (/\b(release|package|packaging|installer|signing|msix)\b/i.test(searchable)) {
    lanes.push("release/package");
  }

  return lanes.length > 0 ? [...new Set(lanes)] : ["general"];
}

function summarizeChecks(pr) {
  const rollup = pr.statusCheckRollup;
  if (!rollup) {
    return { state: "NONE", passed: 0, failed: 0, pending: 0, skipped: 0 };
  }

  const summary = {
    state: rollup.state ?? "UNKNOWN",
    passed: 0,
    failed: 0,
    pending: 0,
    skipped: 0,
  };
  const contexts = rollup.contexts?.nodes ?? [];

  for (const context of contexts) {
    if (context.__typename === "StatusContext") {
      if (context.state === "SUCCESS") summary.passed += 1;
      else if (["FAILURE", "ERROR"].includes(context.state)) summary.failed += 1;
      else if (["PENDING", "EXPECTED"].includes(context.state)) summary.pending += 1;
      continue;
    }

    if (context.status !== "COMPLETED") {
      summary.pending += 1;
    } else if (context.conclusion === "SUCCESS") {
      summary.passed += 1;
    } else if (
      [
        "ACTION_REQUIRED",
        "CANCELLED",
        "FAILURE",
        "STALE",
        "STARTUP_FAILURE",
        "TIMED_OUT",
      ].includes(context.conclusion)
    ) {
      summary.failed += 1;
    } else {
      summary.skipped += 1;
    }
  }

  return summary;
}

function proofLabels(pr) {
  return labelsOf(pr).filter((label) => {
    const normalized = label.toLowerCase();
    return normalized.startsWith("proof:") || normalized === "status: 📣 needs proof";
  });
}

function extractReferencedIssueNumbers(body, repository) {
  const text = String(body ?? "");
  const escapedRepository = repository.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const patterns = [
    /\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|related(?:\s+to)?)\s*:?\s*#(\d+)/gi,
    new RegExp(
      `\\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|related(?:\\s+to)?)\\s*:?\\s*${escapedRepository}#(\\d+)`,
      "gi",
    ),
  ];
  const numbers = new Set();

  for (const pattern of patterns) {
    for (const match of text.matchAll(pattern)) {
      numbers.add(Number(match[1]));
    }
  }

  return [...numbers].sort((left, right) => left - right);
}

function eventDate(event) {
  return event.created_at ?? event.submitted_at ?? null;
}

function eventActor(event) {
  return event.actor ?? event.user ?? event.author ?? null;
}

function isTrustedHumanActivity(event) {
  const eventName = String(event.event ?? "").toLowerCase();
  if (
    [
      "assigned",
      "committed",
      "head_ref_force_pushed",
      "head_ref_restored",
      "ready_for_review",
      "unassigned",
    ].includes(eventName)
  ) {
    return true;
  }

  const actor = eventActor(event);
  if (!actor || actor.type === "Bot" || String(actor.login ?? "").endsWith("[bot]")) {
    return false;
  }

  return (
    ["commented", "reviewed"].includes(eventName) &&
    TRUSTED_ASSOCIATIONS.has(event.author_association)
  );
}

function evaluateActiveOwnership(item, timeline, now = new Date()) {
  const labels = labelsOf(item);
  if (!labels.includes(ACTIVE_OWNERSHIP_LABEL)) {
    return {
      present: false,
      expired: false,
      removable: false,
      owner: assigneesOf(item).join(", ") || "none",
      reason: "active ownership label is absent",
    };
  }

  const labelEvents = timeline
    .filter(
      (event) =>
        event.event === "labeled" &&
        event.label?.name === ACTIVE_OWNERSHIP_LABEL &&
        eventDate(event),
    )
    .sort((left, right) => Date.parse(eventDate(right)) - Date.parse(eventDate(left)));
  const applied = labelEvents[0];
  const appliedIndex = timeline.indexOf(applied);
  const assignees = assigneesOf(item);
  const owner = assignees.join(", ") || applied?.actor?.login || "unknown";

  if (!applied) {
    return {
      present: true,
      expired: false,
      removable: false,
      owner,
      reason: "label application history is unavailable",
    };
  }

  const appliedAt = new Date(eventDate(applied));
  const activities = timeline
    .map((event, index) => ({ event, index }))
    .filter(({ event, index }) => {
      const timestamp = Date.parse(eventDate(event) ?? "");
      const occurredAfterLabel = Number.isFinite(timestamp)
        ? timestamp > appliedAt.getTime()
        : event.event === "committed" && index > appliedIndex;
      return occurredAfterLabel && isTrustedHumanActivity(event);
    });
  const latestDatedActivity = activities
    .map(({ event }) => eventDate(event))
    .filter(Boolean)
    .map((timestamp) => new Date(timestamp))
    .sort((left, right) => right.getTime() - left.getTime())[0];
  const inactivityBaseline =
    latestDatedActivity && latestDatedActivity > appliedAt
      ? latestDatedActivity
      : appliedAt;
  const ageDays =
    (now.getTime() - inactivityBaseline.getTime()) / (24 * 60 * 60 * 1000);
  const expired = ageDays >= ACTIVE_OWNERSHIP_EXPIRY_DAYS;
  if (!expired) {
    return {
      present: true,
      expired: false,
      removable: false,
      owner,
      appliedAt: appliedAt.toISOString(),
      ageDays,
      reason: `trusted ownership activity is younger than ${ACTIVE_OWNERSHIP_EXPIRY_DAYS} days`,
    };
  }

  const undatedActivity = activities.find(({ event }) => !eventDate(event))?.event;

  const blockers = [];
  if (assignees.length > 0) blockers.push("item still has an assignee");
  if (undatedActivity) {
    blockers.push("trusted activity has no server timestamp");
  }
  if (
    eventActor(applied)?.type === "Bot" ||
    String(eventActor(applied)?.login ?? "").endsWith("[bot]")
  ) {
    blockers.push("label was applied by a bot");
  }
  if (!eventActor(applied)?.login) blockers.push("label actor is unavailable");
  for (const label of labels) {
    if (CLEANUP_EXEMPT_LABELS.has(label)) blockers.push(`exempt label '${label}' is present`);
  }

  return {
    present: true,
    expired: true,
    removable: blockers.length === 0,
    owner,
    appliedAt: appliedAt.toISOString(),
    ageDays,
    reason: blockers.length > 0 ? blockers.join("; ") : "expired with no cleanup safeguards blocking removal",
  };
}

function staleBaseState(pr, defaultBranchName) {
  if (pr.baseRefName !== defaultBranchName) {
    return "n/a";
  }
  if (pr.mergeStateStatus === "BEHIND") {
    return "yes";
  }
  if (["DIRTY", "UNKNOWN", null, undefined].includes(pr.mergeStateStatus)) {
    return "unknown";
  }
  return "no";
}

function escapeMarkdownCell(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replace(/([\\`*_[\]{}()#+\-.!|])/g, "\\$1")
    .replaceAll("\n", " ");
}

function renderReport(data, options = {}) {
  const now = options.now ?? new Date();
  const repository = data.repository;
  const timelineByNumber = data.timelineByNumber ?? new Map();
  const linkedPrsByIssue = data.linkedPrsByIssue ?? new Map();
  const lines = [
    "# Repository triage report",
    "",
    `Repository: \`${repository}\``,
    `Generated: ${now.toISOString()}`,
    `Mode: ${options.operation ?? "report-only"}`,
    "",
    `Active ownership expires after ${ACTIVE_OWNERSHIP_EXPIRY_DAYS} full days without trusted human activity. Scheduled runs only report.`,
    "",
    "## Pull requests",
    "",
    "| PR | Mergeable | Stale base | Checks | Proof | Author | Lanes | Active owner | Linked issues |",
    "|---|---|---|---|---|---|---|---|---|",
  ];

  for (const pr of data.pullRequests) {
    const checks = summarizeChecks(pr);
    const ownership = evaluateActiveOwnership(pr, timelineByNumber.get(`pr:${pr.number}`) ?? [], now);
    const bodyLinks = extractReferencedIssueNumbers(pr.body, repository);
    const checkText = `${checks.state} (${checks.passed} pass, ${checks.failed} fail, ${checks.pending} pending)`;
    const ownerText = ownership.present
      ? `${ownership.owner}${ownership.expired ? `; expired; ${ownership.reason}` : ""}`
      : ownership.owner;
    lines.push(
      `| [#${pr.number}](${pr.url}) ${escapeMarkdownCell(pr.title)} | ${pr.mergeable ?? "UNKNOWN"} | ${staleBaseState(pr, data.defaultBranchName)} | ${checkText} | ${escapeMarkdownCell(proofLabels(pr).join(", ") || "none")} | ${authorType(pr)} | ${classifyPullRequest(pr).join(", ")} | ${escapeMarkdownCell(ownerText)} | ${bodyLinks.map((number) => `#${number}`).join(", ") || "none"} |`,
    );
  }

  if (data.pullRequests.length === 0) {
    lines.push("| none | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a |");
  }

  lines.push(
    "",
    "## Issues",
    "",
    "| Issue | Lanes | Active owner | Open PR ownership | Routing |",
    "|---|---|---|---|---|",
  );
  for (const issue of data.issues) {
    const ownership = evaluateActiveOwnership(
      issue,
      timelineByNumber.get(`issue:${issue.number}`) ?? [],
      now,
    );
    const linkedPrs = linkedPrsByIssue.get(issue.number) ?? [];
    const ownerText = ownership.present
      ? `${ownership.owner}${ownership.expired ? `; expired; ${ownership.reason}` : ""}`
      : ownership.owner;
    const routing =
      linkedPrs.length > 0
        ? `Existing open PR ${linkedPrs.map((number) => `#${number}`).join(", ")}. Avoid duplicate fixes.`
        : "No open PR owner detected.";
    lines.push(
      `| [#${issue.number}](${issue.url}) ${escapeMarkdownCell(issue.title)} | ${classifyIssue(issue).join(", ")} | ${escapeMarkdownCell(ownerText)} | ${linkedPrs.map((number) => `#${number}`).join(", ") || "none"} | ${routing} |`,
    );
  }

  if (data.issues.length === 0) {
    lines.push("| none | n/a | n/a | n/a | n/a |");
  }

  if (options.audit?.length) {
    lines.push("", "## Cleanup audit", "");
    for (const entry of options.audit) {
      lines.push(`- ${entry}`);
    }
  }

  lines.push(
    "",
    "## Safeguards",
    "",
    "- This automation never merges pull requests and never closes issues or pull requests.",
    `- The only mutable label is \`${ACTIVE_OWNERSHIP_LABEL}\`.`,
    `- Removal requires the manual \`${CLEANUP_OPERATION}\` operation and a fresh API re-check.`,
    "- Any assignee, trusted recent activity, bot-applied ownership, missing provenance, or exempt label blocks removal.",
    "",
  );
  return lines.join("\n");
}

async function fetchPullRequests(github, owner, repo) {
  const query = `
    query($owner: String!, $repo: String!, $cursor: String) {
      repository(owner: $owner, name: $repo) {
        pullRequests(states: OPEN, first: 100, after: $cursor, orderBy: {field: UPDATED_AT, direction: DESC}) {
          pageInfo { hasNextPage endCursor }
          nodes {
            number title url body createdAt updatedAt isDraft additions deletions changedFiles
            baseRefName baseRefOid headRefName headRefOid mergeable mergeStateStatus authorAssociation
            author { __typename login }
            labels(first: 100) { nodes { name } }
            assignees(first: 20) { nodes { login } }
            files(first: 100) {
              totalCount
              pageInfo { hasNextPage endCursor }
              nodes { path }
            }
            statusCheckRollup {
              state
              contexts(first: 100) {
                totalCount
                pageInfo { hasNextPage endCursor }
                nodes {
                  __typename
                  ... on CheckRun { name status conclusion }
                  ... on StatusContext { context state }
                }
              }
            }
          }
        }
      }
    }`;
  const pullRequests = [];
  let cursor = null;

  do {
    const result = await github.graphql(query, { owner, repo, cursor });
    const connection = result.repository.pullRequests;
    pullRequests.push(...connection.nodes);
    cursor = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (cursor);

  for (const pr of pullRequests) {
    if (pr.files.pageInfo.hasNextPage) {
      pr.files.nodes.push(
        ...(await fetchRemainingPullRequestFiles(
          github,
          owner,
          repo,
          pr.number,
          pr.files.pageInfo.endCursor,
        )),
      );
    }
    const contexts = pr.statusCheckRollup?.contexts;
    if (contexts?.pageInfo.hasNextPage) {
      contexts.nodes.push(
        ...(await fetchRemainingCheckContexts(
          github,
          owner,
          repo,
          pr.number,
          contexts.pageInfo.endCursor,
        )),
      );
    }
  }

  return pullRequests;
}

async function fetchRemainingPullRequestFiles(github, owner, repo, number, initialCursor) {
  const query = `
    query($owner: String!, $repo: String!, $number: Int!, $cursor: String!) {
      repository(owner: $owner, name: $repo) {
        pullRequest(number: $number) {
          files(first: 100, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes { path }
          }
        }
      }
    }`;
  const files = [];
  let cursor = initialCursor;
  do {
    const result = await github.graphql(query, { owner, repo, number, cursor });
    const connection = result.repository.pullRequest.files;
    files.push(...connection.nodes);
    cursor = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (cursor);
  return files;
}

async function fetchRemainingCheckContexts(github, owner, repo, number, initialCursor) {
  const query = `
    query($owner: String!, $repo: String!, $number: Int!, $cursor: String!) {
      repository(owner: $owner, name: $repo) {
        pullRequest(number: $number) {
          statusCheckRollup {
            contexts(first: 100, after: $cursor) {
              pageInfo { hasNextPage endCursor }
              nodes {
                __typename
                ... on CheckRun { name status conclusion }
                ... on StatusContext { context state }
              }
            }
          }
        }
      }
    }`;
  const contexts = [];
  let cursor = initialCursor;
  do {
    const result = await github.graphql(query, { owner, repo, number, cursor });
    const connection = result.repository.pullRequest.statusCheckRollup.contexts;
    contexts.push(...connection.nodes);
    cursor = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (cursor);
  return contexts;
}

async function fetchIssues(github, owner, repo) {
  const query = `
    query($owner: String!, $repo: String!, $cursor: String) {
      repository(owner: $owner, name: $repo) {
        issues(states: OPEN, first: 100, after: $cursor, orderBy: {field: UPDATED_AT, direction: DESC}) {
          pageInfo { hasNextPage endCursor }
          nodes {
            number title url body createdAt updatedAt authorAssociation
            author { __typename login }
            labels(first: 100) { nodes { name } }
            assignees(first: 20) { nodes { login } }
          }
        }
      }
    }`;
  const issues = [];
  let cursor = null;

  do {
    const result = await github.graphql(query, { owner, repo, cursor });
    const connection = result.repository.issues;
    issues.push(...connection.nodes);
    cursor = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (cursor);

  return issues;
}

async function fetchTimeline(github, owner, repo, issueNumber) {
  return github.paginate(github.rest.issues.listEventsForTimeline, {
    owner,
    repo,
    issue_number: issueNumber,
    per_page: 100,
  });
}

function linkedPullRequestNumber(event, openPullRequestNumbers, repository, issueNumber) {
  if (event.event !== "cross-referenced") {
    return null;
  }
  const source = event.source?.issue;
  const sourceRepository = String(source?.repository_url ?? "")
    .toLowerCase()
    .replace(/^https:\/\/api\.github\.com\/repos\//, "");
  if (
    !source?.pull_request ||
    sourceRepository !== repository.toLowerCase() ||
    !openPullRequestNumbers.has(source.number) ||
    !extractReferencedIssueNumbers(source.body, repository).includes(issueNumber)
  ) {
    return null;
  }
  return source.number;
}

async function collectRepositoryData({ github, owner, repo }) {
  const metaQuery = `
    query($owner: String!, $repo: String!) {
      repository(owner: $owner, name: $repo) {
        defaultBranchRef { name }
      }
    }`;
  const [meta, pullRequests, issues] = await Promise.all([
    github.graphql(metaQuery, { owner, repo }),
    fetchPullRequests(github, owner, repo),
    fetchIssues(github, owner, repo),
  ]);
  const openPullRequestNumbers = new Set(pullRequests.map((pr) => pr.number));
  const timelineByNumber = new Map();
  const linkedPrsByIssue = new Map();

  for (const issue of issues) {
    const timeline = await fetchTimeline(github, owner, repo, issue.number);
    timelineByNumber.set(`issue:${issue.number}`, timeline);
    const linked = new Set(
      timeline
        .map((event) =>
          linkedPullRequestNumber(
            event,
            openPullRequestNumbers,
            `${owner}/${repo}`,
            issue.number,
          ),
        )
        .filter(Boolean),
    );
    linkedPrsByIssue.set(issue.number, linked);
  }

  for (const pr of pullRequests) {
    for (const issueNumber of extractReferencedIssueNumbers(pr.body, `${owner}/${repo}`)) {
      if (!linkedPrsByIssue.has(issueNumber)) linkedPrsByIssue.set(issueNumber, new Set());
      linkedPrsByIssue.get(issueNumber).add(pr.number);
    }

    if (labelsOf(pr).includes(ACTIVE_OWNERSHIP_LABEL)) {
      timelineByNumber.set(`pr:${pr.number}`, await fetchTimeline(github, owner, repo, pr.number));
    }
  }

  return {
    repository: `${owner}/${repo}`,
    defaultBranchName: meta.repository.defaultBranchRef?.name ?? null,
    pullRequests,
    issues,
    timelineByNumber,
    linkedPrsByIssue: new Map(
      [...linkedPrsByIssue].map(([number, linked]) => [
        number,
        [...linked].sort((left, right) => left - right),
      ]),
    ),
  };
}

async function removeExpiredActiveOwnership({ github, owner, repo, data, now }) {
  const audit = [];
  const candidates = [
    ...data.issues.map((item) => ({ item, kind: "issue" })),
    ...data.pullRequests.map((item) => ({ item, kind: "pr" })),
  ].filter(({ item, kind }) => {
    const timeline = data.timelineByNumber.get(`${kind}:${item.number}`) ?? [];
    return evaluateActiveOwnership(item, timeline, now).removable;
  });

  for (const candidate of candidates) {
    const currentResponse = await github.rest.issues.get({
      owner,
      repo,
      issue_number: candidate.item.number,
    });
    const current = currentResponse.data;
    const currentLabels = labelsOf(current);
    if (current.state !== "open" || !currentLabels.includes(ACTIVE_OWNERSHIP_LABEL)) {
      removeActiveOwnershipLabelLocally(candidate.item);
      audit.push(`#${candidate.item.number}: skipped because the item closed or the label was already absent.`);
      continue;
    }

    const timeline = await fetchTimeline(github, owner, repo, candidate.item.number);
    const ownership = evaluateActiveOwnership(current, timeline, now);
    if (!ownership.removable) {
      audit.push(`#${candidate.item.number}: skipped after fresh re-check (${ownership.reason}).`);
      continue;
    }

    try {
      await github.rest.issues.removeLabel({
        owner,
        repo,
        issue_number: candidate.item.number,
        name: ACTIVE_OWNERSHIP_LABEL,
      });
      removeActiveOwnershipLabelLocally(candidate.item);
      audit.push(`#${candidate.item.number}: removed \`${ACTIVE_OWNERSHIP_LABEL}\` (${ownership.reason}).`);
    } catch (error) {
      if (error.status === 404) {
        removeActiveOwnershipLabelLocally(candidate.item);
        audit.push(`#${candidate.item.number}: label was already absent during removal.`);
        continue;
      }
      throw error;
    }
  }

  if (candidates.length === 0) {
    audit.push("No expired active ownership labels passed every removal safeguard.");
  }
  return audit;
}

function removeActiveOwnershipLabelLocally(item) {
  if (item.labels?.nodes) {
    item.labels.nodes = item.labels.nodes.filter(
      (label) => label.name !== ACTIVE_OWNERSHIP_LABEL,
    );
    return;
  }
  if (Array.isArray(item.labels)) {
    item.labels = item.labels.filter(
      (label) =>
        (typeof label === "string" ? label : label?.name) !==
        ACTIVE_OWNERSHIP_LABEL,
    );
  }
}

async function run({ github, context, core, outputDir, operation = "report-only", now = new Date() }) {
  if (!["report-only", CLEANUP_OPERATION].includes(operation)) {
    throw new Error(`Unsupported triage operation '${operation}'.`);
  }

  const { owner, repo } = context.repo;
  const data = await collectRepositoryData({ github, owner, repo });
  let audit = [];
  if (operation === CLEANUP_OPERATION) {
    audit = await removeExpiredActiveOwnership({ github, owner, repo, data, now });
  }

  const report = renderReport(data, { now, operation, audit });
  fs.mkdirSync(outputDir, { recursive: true });
  const suffix = operation === CLEANUP_OPERATION ? "cleanup" : "report";
  const markdownPath = path.join(outputDir, `repository-triage-${suffix}.md`);
  const jsonPath = path.join(outputDir, `repository-triage-${suffix}.json`);
  fs.writeFileSync(markdownPath, report, "utf8");
  fs.writeFileSync(
    jsonPath,
    JSON.stringify(
      {
        repository: data.repository,
        generatedAt: now.toISOString(),
        operation,
        pullRequests: data.pullRequests.map((pr) => ({
          number: pr.number,
          mergeable: pr.mergeable,
          staleBase: staleBaseState(pr, data.defaultBranchName),
          checks: summarizeChecks(pr),
          proofLabels: proofLabels(pr),
          authorType: authorType(pr),
          lanes: classifyPullRequest(pr),
        })),
        issues: data.issues.map((issue) => ({
          number: issue.number,
          lanes: classifyIssue(issue),
          openPullRequests: data.linkedPrsByIssue.get(issue.number) ?? [],
        })),
        audit,
      },
      null,
      2,
    ),
    "utf8",
  );
  await core.summary.addRaw(report).write();
  core.info(`Wrote ${markdownPath} and ${jsonPath}.`);
  return { report, audit, markdownPath, jsonPath };
}

module.exports = {
  ACTIVE_OWNERSHIP_EXPIRY_DAYS,
  ACTIVE_OWNERSHIP_LABEL,
  CLEANUP_OPERATION,
  authorType,
  classifyIssue,
  classifyPullRequest,
  collectRepositoryData,
  evaluateActiveOwnership,
  extractReferencedIssueNumbers,
  linkedPullRequestNumber,
  proofLabels,
  removeExpiredActiveOwnership,
  renderReport,
  run,
  staleBaseState,
  summarizeChecks,
};
