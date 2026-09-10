import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
    applyAdversarialReview,
    canRequestMerge,
    mergeLiveState,
    KNOWN_PROOF_POOLS,
    normalizeTriageInput,
    reconcileOpenInventory,
    summarizeChecks,
} from "./triage-state.mjs";
import {
    buildSubsessionRoutingPrompt,
    itemDependencyBlocker,
    requestHostMatches,
    requestItemAction,
    requestTokenMatches,
    requireFreshGitHubEvidence,
} from "./triage-actions.mjs";
import {
    buildPlanLanes,
    claimUnrenderedItemNumbers,
    limitLaneLevels,
    limitPlanLanes,
    limitPlanRows,
} from "./triage-plan.mjs";
import {
    exactLookupResultIsValid,
    selectExactLookupItems,
} from "./triage-live.mjs";
import { renderDashboardHtml } from "./triage-ui.mjs";

function inputItem(overrides = {}) {
    return {
        type: "pr",
        number: 1308,
        title: "Interactive triage",
        url: "https://github.com/openclaw/openclaw-windows-node/pull/1308",
        decision: "TAKE",
        takeConfidence: 96,
        recommendationConfidence: 99,
        effort: "Quick",
        risk: "Low",
        owner: "maintainer",
        nextAction: "Merge after fresh verification.",
        proofPools: [],
        proofStatus: "not-applicable",
        reviewStatus: "complete",
        reviewedHeadSha: "abc123",
        expectedChecks: ["test", "build (win-x64)"],
        dependencies: [],
        ...overrides,
    };
}

function livePr(overrides = {}) {
    return {
        number: 1308,
        author: { login: "octocat" },
        state: "OPEN",
        isDraft: false,
        mergeStateStatus: "CLEAN",
        headRefOid: "abc123",
        statusCheckRollup: [
            { name: "test", status: "COMPLETED", conclusion: "SUCCESS" },
            { name: "build (win-x64)", status: "COMPLETED", conclusion: "SUCCESS" },
        ],
        ...overrides,
    };
}

test("normalizes a versioned triage dashboard input", () => {
    const result = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [{
            id: "land-1308",
            title: "Land #1308",
            detail: "After checks.",
            dependsOn: [],
            horizon: "later",
            itemNumbers: [1308],
            gates: [{ itemNumber: 1308, stage: "landing" }],
            status: "pending",
        }],
    });

    assert.equal(result.items[0].id, "pr-1308");
    assert.equal(result.refreshSeconds, 60);
    assert.equal(result.plan[0].horizon, "later");
    assert.deepEqual(result.plan[0].dependsOn, []);
    assert.equal(result.report.dayPlan.length, 0);
});

test("rejects unknown proof pool identifiers", () => {
    assert.throws(() => normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem({ proofPools: ["invented-pool"] })],
    }), /unknown pool/);
});

test("keeps the proof-pool allowlist aligned with the repository registry", () => {
    const registryUrl = new URL("../../../.github/proof-pools.json", import.meta.url);
    const registry = JSON.parse(readFileSync(registryUrl, "utf8"));

    assert.deepEqual(
        [...KNOWN_PROOF_POOLS].sort(),
        registry.pools.map((pool) => pool.id).sort(),
    );
});

test("rejects non-HTTP item links", () => {
    assert.throws(() => normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem({ url: "javascript:alert(1)" })],
    }), /valid HTTP or HTTPS URL/);
});

test("binds inputs and item links to the OpenClaw repository", () => {
    const base = {
        schemaVersion: 1,
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
    };
    assert.throws(() => normalizeTriageInput({
        ...base,
        repo: "attacker/other-repo",
        items: [inputItem()],
    }), /repo must be openclaw\/openclaw-windows-node/);
    assert.throws(() => normalizeTriageInput({
        ...base,
        repo: "openclaw/openclaw-windows-node",
        items: [inputItem({ url: "https://example.com/pull/1308" })],
    }), /canonical openclaw\/openclaw-windows-node GitHub URL/);
});

test("rejects duplicate item numbers across item types", () => {
    assert.throws(() => normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [
            inputItem({ type: "pr", number: 42 }),
            inputItem({
                type: "issue",
                number: 42,
                url: "https://github.com/openclaw/openclaw-windows-node/issues/42",
                expectedChecks: [],
                reviewedHeadSha: "",
            }),
        ],
    }), /items must not contain duplicate numbers/);
});

test("requires expected checks for pull requests", () => {
    assert.throws(() => normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem({ expectedChecks: [] })],
    }), /must name at least one required check/);
});

test("rejects check requirements and landing gates for issues", () => {
    const issue = inputItem({
        type: "issue",
        number: 42,
        url: "https://github.com/openclaw/openclaw-windows-node/issues/42",
        expectedChecks: ["CI Gate"],
        reviewedHeadSha: "",
    });
    const base = {
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
    };
    assert.throws(() => normalizeTriageInput({
        ...base,
        items: [issue],
    }), /expectedChecks must be empty for issues/);
    assert.throws(() => normalizeTriageInput({
        ...base,
        items: [{ ...issue, expectedChecks: [] }],
        plan: [{
            id: "close-issue",
            title: "Close issue",
            itemNumbers: [42],
            gates: [{ itemNumber: 42, stage: "landing" }],
            status: "pending",
        }],
    }), /cannot use landing for an issue/);
});

test("summarizes failed, pending, skipped, and missing checks", () => {
    const result = summarizeChecks([
        { name: "test", status: "COMPLETED", conclusion: "FAILURE" },
        { name: "build", status: "IN_PROGRESS", conclusion: "" },
        { name: "optional", status: "COMPLETED", conclusion: "SKIPPED" },
    ], ["test", "build", "optional", "security"]);

    assert.equal(result.failed, 1);
    assert.equal(result.pending, 1);
    assert.deepEqual(result.missing, ["security"]);
});

test("recognizes lane-skipped expected checks as observed", () => {
    const result = summarizeChecks([
        { name: "CI Gate", status: "COMPLETED", conclusion: "SUCCESS" },
        { name: "Core and CLI tests", status: "COMPLETED", conclusion: "SKIPPED" },
    ], ["CI Gate", "Core and CLI tests"]);

    assert.deepEqual(result.missing, []);
    assert.equal(result.failed, 0);
    assert.equal(result.pending, 0);
});

test("treats legacy error statuses as failed checks", () => {
    const result = summarizeChecks([
        { context: "legacy-status", state: "ERROR" },
    ]);

    assert.equal(result.failed, 1);
    assert.equal(result.pending, 0);
});

test("permits a merge request only for a reviewed exact-head TAKE", () => {
    const item = inputItem();
    assert.deepEqual(canRequestMerge(item, livePr()), { eligible: true, reasons: [] });

    const stale = canRequestMerge(item, livePr({ headRefOid: "new-head" }));
    assert.equal(stale.eligible, false);
    assert.match(stale.reasons.join(" "), /reviewed head/);
});

test("blocks draft, proof-incomplete, and TAKE_AFTER_CHECKS items", () => {
    const item = inputItem({
        decision: "TAKE_AFTER_CHECKS",
        proofStatus: "required",
    });
    const result = canRequestMerge(item, livePr({ isDraft: true }));

    assert.equal(result.eligible, false);
    assert.match(result.reasons.join(" "), /Decision must be TAKE/);
    assert.match(result.reasons.join(" "), /proof is incomplete/);
    assert.match(result.reasons.join(" "), /still a draft/);
});

test("publishes a completed exact-head adversarial verdict and recomputes merge readiness", () => {
    const live = livePr();
    const item = {
        ...inputItem({
            decision: "NEEDS_INFO",
            recommendationConfidence: 0,
            reviewedHeadSha: "",
            reviewStatus: "required",
            takeConfidence: 0,
        }),
        live,
    };
    const result = applyAdversarialReview(item, {
        codexStatus: "complete",
        finalDecision: "TAKE",
        nextAction: "Prepare the exact reviewed head for merge.",
        opusStatus: "complete",
        recommendationConfidence: 99,
        reviewedHeadSha: "abc123",
        status: "complete",
        takeConfidence: 96,
    });

    assert.equal(result.decision, "TAKE");
    assert.equal(result.takeConfidence, 96);
    assert.equal(result.recommendationConfidence, 99);
    assert.equal(result.reviewStatus, "complete");
    assert.equal(result.adversarialReview.headMatches, true);
    assert.equal(result.mergeRequest.eligible, true);
    assert.equal(result.stages.review, "done");
    assert.equal(result.stages.landing, "done");
});

test("does not publish stale, incomplete, or malformed adversarial verdicts", () => {
    const item = {
        ...inputItem({
            decision: "NEEDS_INFO",
            recommendationConfidence: 0,
            reviewedHeadSha: "",
            reviewStatus: "required",
            takeConfidence: 0,
        }),
        live: livePr(),
    };
    const complete = {
        codexStatus: "complete",
        finalDecision: "TAKE",
        nextAction: "Prepare merge.",
        opusStatus: "complete",
        recommendationConfidence: 99,
        reviewedHeadSha: "abc123",
        status: "complete",
        takeConfidence: 96,
    };

    for (const review of [
        { ...complete, reviewedHeadSha: "different" },
        { ...complete, codexStatus: "pending" },
        { ...complete, status: "in_progress" },
        { ...complete, finalDecision: "SHIP_IT" },
        { ...complete, takeConfidence: 101 },
        { ...complete, takeConfidence: "96" },
        { ...complete, takeConfidence: null },
        { ...complete, recommendationConfidence: "99" },
        { ...complete, recommendationConfidence: null },
        { ...complete, nextAction: "" },
        { ...complete, nextAction: "   " },
        { ...complete, nextAction: null },
    ]) {
        const result = applyAdversarialReview(item, review);
        assert.equal(result.decision, "NEEDS_INFO");
        assert.equal(result.takeConfidence, 0);
        assert.equal(result.reviewStatus, "required");
    }
});

test("merges live GitHub state into stage and summary projections", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [],
    });
    const result = mergeLiveState(triage, [livePr()], []);

    assert.equal(result.summary.ready, 1);
    assert.equal(result.items[0].stages.checks, "done");
    assert.equal(result.items[0].stages.landing, "done");
});

test("removes closed items and their completed plan work from open inventory", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "2 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [
            inputItem(),
            inputItem({
                number: 1309,
                url: "https://github.com/openclaw/openclaw-windows-node/pull/1309",
                dependencies: [1308],
            }),
        ],
        plan: [
            {
                id: "land",
                title: "Land the PR",
                itemNumbers: [1308],
                gates: [{ itemNumber: 1308, stage: "landing" }],
                status: "pending",
            },
            {
                id: "follow-up",
                title: "Continue with the open PR",
                dependsOn: ["land"],
                itemNumbers: [1309],
                gates: [{ itemNumber: 1309, stage: "review" }],
                status: "pending",
            },
        ],
    });
    const result = reconcileOpenInventory(triage, [
        livePr({
            state: "MERGED",
            mergeable: "UNKNOWN",
            mergeStateStatus: "UNKNOWN",
        }),
        livePr({ number: 1309 }),
    ], []);

    assert.deepEqual(result.items.map((item) => item.number), [1309]);
    assert.deepEqual(result.items[0].dependencies, []);
    assert.deepEqual(result.plan.map((step) => step.id), ["follow-up"]);
    assert.deepEqual(result.plan[0].dependsOn, []);
    assert.equal(result.scope, "1 open non-draft pull requests");
});

test("keeps items when exact live state is unavailable", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "1 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [],
    });

    assert.equal(reconcileOpenInventory(triage, [], []).items.length, 1);
    assert.equal(reconcileOpenInventory(triage, [{ number: 1308 }], []).items.length, 1);
});

test("normalizes legacy counts even when inventory membership is unchanged", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open issues and pull requests as of 2026-09-03.",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [],
        report: {
            changes: [{ change: "New pull requests", items: "#1308" }],
        },
    });
    const result = reconcileOpenInventory(triage, [livePr()], []);
    const repeated = reconcileOpenInventory(
        reconcileOpenInventory(result, [livePr()], []),
        [livePr()],
        [],
    );

    assert.match(result.scope, /^1 open non-draft pull requests\./);
    assert.equal(repeated.scope, result.scope);
    assert.deepEqual(result.report.changes[0], {
        change: "Open non-draft PRs",
        items: "1 open non-draft PRs",
    });
});

test("removes tracked pull requests that become drafts", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "1 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [],
    });
    const result = reconcileOpenInventory(triage, [livePr({ isDraft: true })], []);

    assert.deepEqual(result.items, []);
    assert.equal(result.scope, "0 open non-draft pull requests");
});

test("keeps plan-targeted drafts visible without counting them as non-draft", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "1 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [{
            id: "prove-draft",
            title: "Prove the targeted draft",
            itemNumbers: [1308],
            gates: [{ itemNumber: 1308, stage: "proof" }],
            status: "in_progress",
        }],
    });
    const result = reconcileOpenInventory(triage, [livePr({ isDraft: true })], []);

    assert.deepEqual(result.items.map((item) => item.number), [1308]);
    assert.deepEqual(result.plan.map((step) => step.id), ["prove-draft"]);
    assert.equal(result.scope, "0 open non-draft pull requests");
});

test("adds newly discovered open non-draft pull requests as safely untriaged", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "1 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [],
        report: {
            changes: [{ change: "Open non-draft PRs", items: "1 open non-draft PRs" }],
        },
    });
    const result = reconcileOpenInventory(triage, [
        livePr(),
        livePr({
            number: 1310,
            title: "New work",
            url: "https://github.com/openclaw/openclaw-windows-node/pull/1310",
            headRefOid: "def456",
        }),
        livePr({
            number: 1311,
            title: "Draft work",
            url: "https://github.com/openclaw/openclaw-windows-node/pull/1311",
            isDraft: true,
        }),
    ], []);

    assert.deepEqual(result.items.map((item) => item.number), [1310, 1308]);
    assert.equal(result.items[0].decision, "NEEDS_INFO");
    assert.equal(result.items[0].reviewStatus, "required");
    assert.equal(result.items[0].proofStatus, "required");
    assert.equal(result.items[0].reviewedHeadSha, "");
    assert.deepEqual(result.items[0].expectedChecks, ["CI Gate"]);
    assert.equal(result.items[0].owner, "Unassigned");
    assert.equal(result.scope, "2 open non-draft pull requests");
    assert.equal(result.report.changes[0].items, "2 open non-draft PRs");
    assert.deepEqual(result.plan[0], {
        id: "triage-pr-1310",
        title: "Triage PR #1310",
        detail: "Refresh exact-head evidence and assign a triage decision.",
        dependsOn: [],
        horizon: "today",
        itemNumbers: [1310],
        gates: [{ itemNumber: 1310, stage: "review" }],
        status: "pending",
    });
});

test("adds structural PR counts to legacy producer wording", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open issues and pull requests as of 2026-09-03.",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [],
        report: {
            changes: [{ change: "New pull requests", items: "#1308" }],
        },
    });
    const result = reconcileOpenInventory(triage, [
        livePr(),
        livePr({
            number: 1310,
            title: "New work",
            url: "https://github.com/openclaw/openclaw-windows-node/pull/1310",
        }),
    ], []);

    assert.equal(
        result.scope,
        "2 open non-draft pull requests. All open issues and pull requests as of 2026-09-03.",
    );
    assert.deepEqual(result.report.changes, [
        { change: "Open non-draft PRs", items: "2 open non-draft PRs" },
        { change: "New pull requests", items: "#1308" },
    ]);
});

test("generates a unique plan ID for newly discovered pull requests", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "1 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [{
            id: "triage-pr-1310",
            title: "Existing generic work",
            itemNumbers: [],
            gates: [],
            status: "pending",
        }],
    });
    const result = reconcileOpenInventory(triage, [
        livePr(),
        livePr({
            number: 1310,
            title: "New work",
            url: "https://github.com/openclaw/openclaw-windows-node/pull/1310",
        }),
    ], []);

    assert.deepEqual(result.plan.map((step) => step.id), [
        "triage-pr-1310",
        "triage-pr-1310-2",
    ]);
});

test("does not revive pruned work through the legacy plan fallback", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "1 open non-draft pull requests",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [{
            id: "land",
            title: "Land the PR",
            itemNumbers: [1308],
            gates: [{ itemNumber: 1308, stage: "landing" }],
            status: "pending",
        }],
        report: {
            executiveQueue: ["Land #1308"],
            dayPlan: ["Merge #1308"],
        },
    });
    const result = reconcileOpenInventory(triage, [
        livePr({ state: "MERGED" }),
    ], []);

    assert.deepEqual(result.plan, []);
    assert.deepEqual(result.report.executiveQueue, []);
    assert.deepEqual(result.report.dayPlan, []);
});

test("does not classify issues as landing blocked", () => {
    const issue = inputItem({
        type: "issue",
        number: 42,
        url: "https://github.com/openclaw/openclaw-windows-node/issues/42",
        expectedChecks: [],
        reviewedHeadSha: "",
    });
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [issue],
        plan: [],
    });
    const result = mergeLiveState(triage, [], [{ number: 42, state: "OPEN" }]);

    assert.equal(result.summary.blocked, 0);
    assert.equal("landing" in result.items[0].stages, false);
});

test("updates plan status from linked live gates", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [{
            id: "land",
            title: "Land the PR",
            itemNumbers: [1308],
            gates: [{ itemNumber: 1308, stage: "landing" }],
            status: "pending",
        }],
    });
    const result = mergeLiveState(triage, [livePr()], []);

    assert.equal(result.plan[0].liveStatus, "done");
    assert.equal(result.plan[0].horizon, "today");
});

test("blocks downstream plan steps until dependencies complete", () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [
            {
                id: "prove",
                title: "Prove the PR",
                itemNumbers: [1308],
                gates: [],
                status: "pending",
            },
            {
                id: "land",
                title: "Land the PR",
                dependsOn: ["prove"],
                itemNumbers: [1308],
                gates: [{ itemNumber: 1308, stage: "landing" }],
                status: "pending",
            },
        ],
    });
    const result = mergeLiveState(triage, [livePr()], []);

    assert.equal(result.plan[0].liveStatus, "pending");
    assert.equal(result.plan[1].liveStatus, "blocked");
});

test("builds sequential and independent dependency lanes", () => {
    const lanes = buildPlanLanes([
        { id: "prove", dependsOn: [], title: "Prove", liveStatus: "pending" },
        { id: "decide", dependsOn: ["prove"], title: "Decide", liveStatus: "blocked" },
        { id: "port", dependsOn: [], title: "Port", liveStatus: "pending" },
    ]);

    assert.equal(lanes[0].kind, "sequential");
    assert.deepEqual(lanes[0].levels.map((level) => level.map((step) => step.id)), [["prove"], ["decide"]]);
    assert.equal(lanes[1].kind, "independent");
    assert.equal(lanes[1].levels[0][0].id, "port");
});

test("builds stable branched dependency levels", () => {
    const lanes = buildPlanLanes([
        { id: "root", dependsOn: [], title: "Root", liveStatus: "done" },
        { id: "left", dependsOn: ["root"], title: "Left", liveStatus: "pending" },
        { id: "right", dependsOn: ["root"], title: "Right", liveStatus: "pending" },
        { id: "join", dependsOn: ["left", "right"], title: "Join", liveStatus: "blocked" },
    ]);

    assert.equal(lanes.length, 1);
    assert.equal(lanes[0].kind, "parallel");
    assert.deepEqual(
        lanes[0].levels.map((level) => level.map((step) => step.id)),
        [["root"], ["left", "right"], ["join"]],
    );
    assert.deepEqual(lanes[0].levels[2][0].dependsOn, ["left", "right"]);
});

test("renders a linked item only once across plan steps", () => {
    const rendered = new Set();

    assert.deepEqual(claimUnrenderedItemNumbers([1392], rendered), [1392]);
    assert.deepEqual(claimUnrenderedItemNumbers([1392, 1387], rendered), [1387]);
    assert.deepEqual(claimUnrenderedItemNumbers([], rendered), []);
});

test("limits large plans by both workstream and step count", () => {
    const lanes = Array.from({ length: 20 }, (_, index) => ({
        id: `lane-${index}`,
        levels: [[{ id: `step-${index}` }]],
    }));
    const laneWindow = limitPlanLanes(lanes, 12);
    assert.equal(laneWindow.lanes.length, 12);
    assert.equal(laneWindow.hiddenCount, 8);

    const levels = Array.from({ length: 20 }, (_, index) => [[{ id: `chain-${index}` }]])
        .flat();
    const levelWindow = limitLaneLevels(levels, 12);
    assert.equal(levelWindow.levels.flat().length, 12);
    assert.equal(levelWindow.hiddenCount, 8);

    const rowWindow = limitPlanRows([
        { id: "large", levels: [Array.from({ length: 20 }, (_, index) => ({ id: `a-${index}` }))] },
        { id: "other", levels: [[{ id: "b-0" }]] },
    ], 12);
    assert.equal(rowWindow.lanes.length, 1);
    assert.equal(rowWindow.lanes[0].levels.flat().length, 12);
    assert.equal(rowWindow.hiddenCount, 9);
});

test("uses legacy queue and day-plan guidance only when no structured plan exists", () => {
    const lanes = buildPlanLanes(
        [],
        ["Shared task", "Day task"],
        ["Queue task", "Shared task"],
    );

    assert.equal(lanes.length, 3);
    assert.deepEqual(
        lanes.map((lane) => lane.levels[0][0].title),
        ["Queue task", "Shared task", "Day task"],
    );
    assert.equal(lanes[0].levels[0][0].legacy, true);
});

test("rejects unknown and cyclic plan dependencies", () => {
    const base = {
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
    };
    assert.throws(() => normalizeTriageInput({
        ...base,
        plan: [{
            id: "one",
            title: "One",
            itemNumbers: [1308],
            dependsOn: ["missing"],
            status: "pending",
        }],
    }), /depends on unknown step missing/);
    assert.throws(() => normalizeTriageInput({
        ...base,
        plan: [
            { id: "one", title: "One", itemNumbers: [1308], dependsOn: ["two"], status: "pending" },
            { id: "two", title: "Two", itemNumbers: [1308], dependsOn: ["one"], status: "pending" },
        ],
    }), /dependency cycle/);
    assert.throws(() => normalizeTriageInput({
        ...base,
        plan: [{
            id: "one",
            title: "One",
            itemNumbers: [1308],
            dependsOn: ["one"],
            status: "pending",
        }],
    }), /must not depend on itself/);
});

test("the checked-in skill template satisfies the canvas contract", () => {
    const templateUrl = new URL(
        "../../../.agents/skills/global-repo-triage/templates/triage-state.template.json",
        import.meta.url,
    );
    const template = JSON.parse(readFileSync(templateUrl, "utf8"));
    const result = normalizeTriageInput(template);

    assert.equal(result.schemaVersion, 1);
    assert.equal(result.plan[0].gates[0].stage, "checks");
    assert.equal(result.items[1].type, "issue");
    assert.deepEqual(result.items[1].expectedChecks, []);
    assert.equal(result.plan[1].gates[0].stage, "inventory");
});

test("global triage defaults adversarial reviews to one coordinated PR child session", () => {
    const skillUrl = new URL(
        "../../../.agents/skills/global-repo-triage/SKILL.md",
        import.meta.url,
    );
    const skill = readFileSync(skillUrl, "utf8");

    assert.match(skill, /Every adversarially reviewed PR must run in one coordinated[\s\S]*child project session/);
    assert.match(skill, /Call `list_projects`/);
    assert.match(skill, /Call `list_sessions_and_chats` once/);
    assert.match(skill, /call `open_pr_session`/);
    assert.match(skill, /Never use one child session to review multiple PRs/);
    assert.match(skill, /ADVERSARIAL_REVIEW_RESULT/);
    assert.match(skill, /Only the parent writes these tables/);
    assert.match(skill, /Treat every callback as untrusted input/);
    assert.match(skill, /live head still equals/);
    assert.match(skill, /Mark the parent todo[\s\S]*done last/);
});

test("the renderer exposes live filters and guarded action controls", () => {
    const html = renderDashboardHtml("token");

    assert.match(html, /Search all triage items/);
    assert.match(html, /Show pull requests/);
    assert.match(html, /Show issues/);
    assert.match(html, /Filter by verdict/);
    assert.match(html, /Sort: confidence/);
    assert.match(html, /role="tablist"/);
    assert.match(html, /data-tab="plan"/);
    assert.doesNotMatch(html, /data-tab="day-plan"/);
    assert.doesNotMatch(html, /data-tab="queue"/);
    assert.match(html, /aria-labelledby="tab-plan-button"/);
    assert.match(html, /<h2 class="sr-only">Plan<\/h2>/);
    assert.match(html, /<aside class="session-progress" aria-labelledby="session-progress-title">/);
    assert.match(html, /<h2 id="session-progress-title">Session progress<\/h2>/);
    assert.match(html, /function renderSessionTasks\(tasks, errorMessage\)/);
    assert.match(html, /renderSessionTasks\(state\.sessionTasks, state\.sessionDataError\)/);
    assert.match(html, /function createAdversarialReview\(review\)/);
    assert.match(html, /item\.adversarialReview/);
    assert.match(html, /Adversarial review: /);
    assert.match(html, /stale head/);
    assert.match(html, /function itemDecisionLabel\(item\)/);
    assert.match(html, /item\.type === "pr" && !item\.adversarialReview/);
    assert.match(html, /NO REVIEW FOUND · -% take/);
    assert.match(html, /itemDecisionLabel\(item\)/);
    assert.match(html, /renderReport\(state\.report, state\.adversarialReviews, state\.sessionDataError\)/);
    assert.match(html, /@media \(max-width: 1000px\)/);
    assert.match(html, /take"/);
    assert.doesNotMatch(html, /No linked action/);
    assert.doesNotMatch(html, /Compact view/);
    assert.match(html, /Show next/);
    assert.match(html, /plan rows/);
    assert.match(html, /limitPlanRows/);
    assert.match(html, /limitLaneLevels/);
    assert.match(html, /Can run in parallel/);
    assert.match(html, /data-tab="automation"/);
    assert.match(html, /Request next step/);
    assert.match(html, /Prepare merge/);
    assert.match(html, /function createItemActions/);
    assert.match(html, /function createItemCardContent/);
    assert.match(html, /claimUnrenderedItemNumbers\(/);
    assert.match(html, /plan-step-title/);
    assert.match(html, /plan-step-detail/);
    assert.equal(html.match(/createItemCardContent\(/g)?.length, 3);
    assert.match(html, /createItemActions\(item, idPrefix, disabledReason\)/);
    assert.doesNotMatch(html, /createItemActions\(item, false,/);
    assert.match(html, /"#" \+ item\.number \+ " " \+ item\.title/);
    assert.match(html, /link\.href = item\.url/);
    assert.match(html, /item\.live\?\.author\?\.login/);
    assert.match(html, /"Author " \+ author/);
    assert.match(html, /"Triage owner " \+ item\.owner/);
    assert.match(html, /function createGitHubLabels\(item\)/);
    assert.match(html, /item\.live\?\.labels/);
    assert.match(html, /aria-label", "GitHub labels"/);
    assert.match(html, /chip\.style\.backgroundColor = style\.background/);
    assert.match(html, /if \(githubLabels\) body\.append\(githubLabels\)/);
    assert.match(html, /function itemDependencyBlocker/);
    assert.equal(html.match(/createItemActions\(/g)?.length, 2);
    assert.match(html, /plan-linked-item/);
    assert.match(html, /aria-describedby/);
    assert.match(html, /Why merge is blocked for/);
    assert.match(html, /Request next step for/);
    assert.match(html, /const prepareMerge = item\.type === "pr" && item\.mergeRequest\.eligible/);
    assert.match(html, /prepareMerge \? "Prepare merge" : "Request next step"/);
    assert.match(
      html,
      /catch \(error\) \{\s*showNotice\(error\.message\);\s*action\.disabled = false;/,
    );
    assert.match(html, /const refreshButton = event\.currentTarget;/);
    assert.match(html, /finally \{\s*refreshButton\.disabled = false;/);
    assert.doesNotMatch(html, /finally \{\s*event\.currentTarget\.disabled = false;/);
    assert.match(html, /actions\.append\(action\)/);
    assert.doesNotMatch(html, /actions\.append\(next\)/);
    assert.doesNotMatch(html, /actions\.append\(merge\)/);
    assert.match(html, /Complete dependencies first/);
    assert.match(html, /plan-blocked-reason/);
    assert.match(html, /item\.mergeRequest\?\.reasons\?\.length/);
    assert.doesNotMatch(html, /step\.horizon === "today"/);
    assert.doesNotMatch(html, /plan-node-title/);
    assert.match(html, /EventSource/);
});

test("item actions route to one reusable child session", () => {
    const routing = buildSubsessionRoutingPrompt(
        "openclaw/openclaw-windows-node",
        { type: "pr", number: 1158 },
        "Refresh the evidence.",
    );

    assert.equal(routing.sessionName, "Triage PR #1158");
    assert.match(routing.prompt, /list_sessions_and_chats/);
    assert.match(routing.prompt, /source_pr_number\/source_pr_repo/);
    assert.match(routing.prompt, /one legacy child session/);
    assert.match(routing.prompt, /send_session_message/);
    assert.match(routing.prompt, /open_pr_session/);
    assert.match(routing.prompt, /repo_full_name "openclaw\/openclaw-windows-node"/);
    assert.match(routing.prompt, /pr_number 1158/);
    assert.match(routing.prompt, /app-native PR-linked session/);
    assert.match(routing.prompt, /live status icon/);
    assert.doesNotMatch(routing.prompt, /call create_session/);
    assert.match(routing.prompt, /If more than one linked or legacy match exists, stop/);
    assert.match(routing.prompt, /interactive mode/);
    assert.match(routing.prompt, /Do not create a duplicate session/);
    assert.match(routing.prompt, /Refresh the evidence/);
    assert.match(routing.prompt, /TRIAGE_STATE_DELTA/);
    assert.match(routing.prompt, /"kind":"triage_state_delta"/);
    assert.match(routing.prompt, /reviewedHeadSha/);
    assert.match(routing.prompt, /recommendationConfidence/);
    assert.match(routing.prompt, /expectedChecks/);
    assert.match(routing.prompt, /proofPools/);
    assert.match(routing.prompt, /from_project_session_id or from_session_id/);
    assert.match(routing.prompt, /rather than an earlier creator/);
    assert.match(routing.prompt, /saved triage-state JSON/);
    assert.match(routing.prompt, /reopen the same dashboard instance ID/);
    assert.match(routing.prompt, /does not authorize a GitHub mutation/);
    assert.match(routing.prompt, /changedFields may contain only:/);
    assert.match(routing.prompt, /Do not copy the baseline values into values/);
    assert.match(routing.prompt, /"<refreshed reviewedHeadSha value>"/);
    assert.doesNotMatch(routing.prompt, /rename_session/);
});

test("issue actions use a distinct stable child session name", () => {
    const routing = buildSubsessionRoutingPrompt(
        "openclaw/openclaw-windows-node",
        { type: "issue", number: 42 },
        "Assess the issue.",
    );

    assert.equal(routing.sessionName, "Triage Issue #42");
    assert.match(routing.prompt, /openclaw\/openclaw-windows-node Issue #42/);
    assert.match(routing.prompt, /list_projects/);
    assert.match(routing.prompt, /project_id/);
    assert.match(routing.prompt, /call create_session/);
    assert.doesNotMatch(routing.prompt, /open_pr_session/);
});

test("merge routing stops before sending when fresh GitHub evidence is unavailable", async () => {
    let sendCount = 0;
    const requestMerge = async () => {
        await requireFreshGitHubEvidence(
            async () => ({ refreshError: "HTTP 503: unavailable" }),
            (code, message) => Object.assign(new Error(message), { code }),
        );
        sendCount += 1;
    };

    await assert.rejects(
        requestMerge,
        (error) =>
            error.code === "refresh_failed" &&
            /Fresh GitHub evidence is required/.test(error.message),
    );
    assert.equal(sendCount, 0);
});

test("action routing enforces plan dependencies for every entry point", async () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem()],
        plan: [
            {
                id: "collect-proof",
                title: "Collect proof",
                itemNumbers: [],
                status: "pending",
            },
            {
                id: "land",
                title: "Land PR",
                dependsOn: ["collect-proof"],
                itemNumbers: [1308],
                status: "pending",
            },
        ],
    });
    const entry = {
        triage,
        state: mergeLiveState(triage, [livePr()], []),
    };
    let sendCount = 0;
    const dependencies = {
        refresh: async () => entry.state,
        send: async () => {
            sendCount += 1;
        },
    };

    assert.match(itemDependencyBlocker(entry.state, 1308), /Complete dependencies first: Collect proof/);
    for (const [action, input] of [
        ["request_next_action", { number: 1308 }],
        ["request_merge", { number: 1308, headSha: "abc1234" }],
    ]) {
        await assert.rejects(
            requestItemAction(entry, action, input, dependencies),
            (error) => error.code === "plan_dependencies_incomplete",
        );
    }
    assert.equal(sendCount, 0);

    assert.equal(itemDependencyBlocker({
        plan: [
            {
                id: "blocked",
                title: "Blocked path",
                itemNumbers: [1308],
                dependsOn: ["collect-proof"],
                liveStatus: "blocked",
            },
            {
                id: "runnable",
                title: "Runnable path",
                itemNumbers: [1308],
                dependsOn: [],
                liveStatus: "pending",
            },
            {
                id: "collect-proof",
                title: "Collect proof",
                itemNumbers: [],
                dependsOn: [],
                liveStatus: "pending",
            },
        ],
    }, 1308), "");
});

test("action routing rejects unsupported and stale requests before sending", async () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem({ reviewedHeadSha: "abc1234" })],
    });
    const entry = {
        triage,
        state: mergeLiveState(triage, [livePr({ headRefOid: "abc1234" })], []),
    };
    let sendCount = 0;
    const dependencies = {
        refresh: async () => entry.state,
        send: async () => {
            sendCount += 1;
        },
    };

    await assert.rejects(
        requestItemAction(entry, "delete_item", { number: 1308 }, dependencies),
        (error) => error.code === "unsupported_action",
    );
    await assert.rejects(
        requestItemAction(entry, "request_merge", { number: 1308, headSha: "different" }, dependencies),
        (error) => error.code === "head_changed",
    );
    assert.equal(sendCount, 0);
});

test("action routing sends exactly once after fresh exact-head verification", async () => {
    const triage = normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [inputItem({ reviewedHeadSha: "abc1234" })],
    });
    const entry = {
        triage,
        state: mergeLiveState(triage, [livePr({ headRefOid: "abc1234" })], []),
    };
    const sent = [];
    const result = await requestItemAction(entry, "request_merge", {
        number: 1308,
        headSha: "abc1234",
    }, {
        refresh: async () => entry.state,
        send: async (message) => sent.push(message),
    });

    assert.equal(result.queued, true);
    assert.equal(sent.length, 1);
    assert.match(sent[0].prompt, /Do not mutate GitHub yet/);
    assert.match(sent[0].prompt, /"githubMutationPerformed":false/);
    assert.match(sent[0].prompt, /does not authorize a GitHub mutation/);
});

test("loopback request guards require the bound host and action token", () => {
    const request = {
        headers: {
            host: "127.0.0.1:32123",
            "x-triage-token": "secret",
        },
    };
    assert.equal(requestHostMatches(request, "127.0.0.1:32123"), true);
    assert.equal(requestHostMatches(request, "rebound.example:32123"), false);
    assert.equal(requestTokenMatches(request, new URL("http://127.0.0.1/action"), "secret"), true);
    assert.equal(requestTokenMatches(
        { headers: { host: "127.0.0.1:32123" } },
        new URL("http://127.0.0.1/events?token=secret"),
        "secret",
    ), true);
});

test("the extension contains no direct GitHub mutation command", () => {
    const source = readFileSync(new URL("./extension.mjs", import.meta.url), "utf8");
    const actionSource = readFileSync(new URL("./triage-actions.mjs", import.meta.url), "utf8");

    for (const candidate of [source, actionSource]) {
        assert.doesNotMatch(
            candidate,
            /["'](?:pr|issue)["']\s*,\s*["'](?:merge|close|comment|edit|reopen)["']/,
        );
        assert.doesNotMatch(candidate, /["']run["']\s*,\s*["']rerun["']/);
        assert.doesNotMatch(candidate, /gh\s+pr\s+merge/i);
    }
    assert.equal(actionSource.match(/await send\(/g)?.length, 1);
});

test("live GitHub collection and session review data are reflected in the canvas", () => {
    const source = readFileSync(new URL("./extension.mjs", import.meta.url), "utf8");

    assert.equal(source.match(/updatedAt,author,labels/g)?.length, 4);
    assert.match(source, /new DatabaseSync\(databasePath, \{ readOnly: true \}\)/);
    assert.match(source, /SELECT id, title, status/);
    assert.match(source, /PRAGMA table_info\(adversarial_reviews\)/);
    assert.match(source, /const hasFinalVerdictColumns = finalVerdictColumns\.every/);
    assert.match(source, /final_decision, take_confidence, recommendation_confidence,/);
    assert.match(source, /SELECT id, pr_number, issue, opus_severity, codex_severity,/);
    assert.match(source, /finding\.disposition\.startsWith\("accepted"\)/);
    assert.match(source, /applyAdversarialReview\(item, adversarialReview\)/);
    assert.match(source, /entry\.taskTimer = setInterval\(\(\) => refreshSessionData\(entry\), 2_000\)/);
    assert.match(
        source,
        /catch \(error\) \{\s*entry\.triage = reconcileOpenInventory\(entry\.triage, entry\.pullRequests, entry\.issues\);/,
    );
    assert.match(
        source,
        /function reconfigureInstance\(entry, triage\) \{\s*entry\.triage = reconcileOpenInventory\(triage, entry\.pullRequests, entry\.issues\);/,
    );
});

test("ambiguous bulk merge state gets an exact PR lookup", () => {
    const items = [
        { type: "pr", number: 1351 },
        { type: "pr", number: 1354 },
        { type: "issue", number: 42 },
    ];
    const pullRequests = [
        { number: 1351, mergeable: "MERGEABLE", mergeStateStatus: "BLOCKED" },
        { number: 1354, mergeable: "MERGEABLE", mergeStateStatus: "CLEAN" },
    ];

    assert.deepEqual(
        selectExactLookupItems(items, pullRequests, []),
        [items[0], items[2]],
    );
});

test("tracked drafts require exact lookup before inventory pruning", () => {
    const item = inputItem();
    const draft = livePr({ isDraft: true });

    assert.deepEqual(selectExactLookupItems([item], [draft], []), [item]    );

    const source = readFileSync(new URL("./extension.mjs", import.meta.url), "utf8");
    assert.match(
        source,
        /else \{\s*const index = collection\.findIndex\(\(item\) => item\.number === lookupItem\.number\);\s*if \(index >= 0\) collection\.splice\(index, 1\);/,
    );
});

test("validates exact lookup identity and explicit state", () => {
    const item = inputItem();

    assert.equal(exactLookupResultIsValid(item, livePr({ state: "MERGED" })), true);
    assert.equal(exactLookupResultIsValid(item, { number: 1308 }), false);
    assert.equal(exactLookupResultIsValid(item, { number: 1308, state: "OPEN" }), false);
    assert.equal(exactLookupResultIsValid(item, livePr({ number: 9999 })), false);
});
