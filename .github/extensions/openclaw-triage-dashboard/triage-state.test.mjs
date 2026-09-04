import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
    canRequestMerge,
    mergeLiveState,
    KNOWN_PROOF_POOLS,
    normalizeTriageInput,
    summarizeChecks,
} from "./triage-state.mjs";
import { buildSubsessionRoutingPrompt } from "./triage-actions.mjs";
import {
    buildPlanLanes,
    limitLaneLevels,
    limitPlanLanes,
    limitPlanRows,
} from "./triage-plan.mjs";
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

test("rejects duplicate item numbers across item types", () => {
    assert.throws(() => normalizeTriageInput({
        schemaVersion: 1,
        repo: "openclaw/openclaw-windows-node",
        title: "Global triage",
        scope: "All open work",
        generatedAt: "2026-09-03T22:00:00Z",
        items: [
            inputItem({ type: "pr", number: 42 }),
            inputItem({ type: "issue", number: 42 }),
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

test("summarizes failed, pending, skipped, and missing checks", () => {
    const result = summarizeChecks([
        { name: "test", status: "COMPLETED", conclusion: "FAILURE" },
        { name: "build", status: "IN_PROGRESS", conclusion: "" },
        { name: "optional", status: "COMPLETED", conclusion: "SKIPPED" },
    ], ["test", "build", "security"]);

    assert.equal(result.failed, 1);
    assert.equal(result.pending, 1);
    assert.deepEqual(result.missing, ["security"]);
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
    assert.match(html, /take"/);
    assert.match(html, /Depends on/);
    assert.match(html, /No linked action/);
    assert.doesNotMatch(html, /Compact view/);
    assert.match(html, /Show next/);
    assert.match(html, /plan rows/);
    assert.match(html, /limitPlanRows/);
    assert.match(html, /limitLaneLevels/);
    assert.match(html, /plan-node-decision/);
    assert.match(html, /Can run in parallel/);
    assert.match(html, /data-tab="automation"/);
    assert.match(html, /Request next step/);
    assert.match(html, /Prepare merge/);
    assert.match(html, /function createItemActions/);
    assert.equal(html.match(/createItemActions\(/g)?.length, 3);
    assert.match(html, /plan-button-groups/);
    assert.match(html, /aria-describedby/);
    assert.match(html, /Why merge is blocked for/);
    assert.match(html, /Request next step for/);
    assert.match(html, /if \(item\.type === "pr"\)/);
    assert.match(html, /Complete dependencies first/);
    assert.match(html, /EventSource/);
});

test("item actions route to one reusable child session", () => {
    const routing = buildSubsessionRoutingPrompt(
        "openclaw/openclaw-windows-node",
        { type: "pr", number: 1158 },
        "Refresh the evidence.",
    );

    assert.equal(routing.sessionName, "Triage PR #1158");
    assert.match(routing.prompt, /list_projects/);
    assert.match(routing.prompt, /project_id/);
    assert.match(routing.prompt, /list_sessions_and_chats/);
    assert.match(routing.prompt, /send_session_message/);
    assert.match(routing.prompt, /create_session/);
    assert.match(routing.prompt, /Do not create a duplicate session/);
    assert.match(routing.prompt, /Refresh the evidence/);
});

test("issue actions use a distinct stable child session name", () => {
    const routing = buildSubsessionRoutingPrompt(
        "openclaw/openclaw-windows-node",
        { type: "issue", number: 42 },
        "Assess the issue.",
    );

    assert.equal(routing.sessionName, "Triage Issue #42");
    assert.match(routing.prompt, /openclaw\/openclaw-windows-node Issue #42/);
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
    assert.equal(source.match(/copilotSession\.send/g)?.length, 1);
});
