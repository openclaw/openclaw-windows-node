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
            itemNumbers: [1308],
            gates: [{ itemNumber: 1308, stage: "landing" }],
            status: "pending",
        }],
    });

    assert.equal(result.items[0].id, "pr-1308");
    assert.equal(result.refreshSeconds, 60);
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
    assert.match(html, /data-tab="automation"/);
    assert.match(html, /Request next step/);
    assert.match(html, /Prepare merge/);
    assert.match(html, /EventSource/);
});

test("the extension contains no direct GitHub mutation command", () => {
    const source = readFileSync(new URL("./extension.mjs", import.meta.url), "utf8");

    assert.doesNotMatch(
        source,
        /["'](?:pr|issue)["']\s*,\s*["'](?:merge|close|comment|edit|reopen)["']/,
    );
    assert.doesNotMatch(source, /["']run["']\s*,\s*["']rerun["']/);
    assert.doesNotMatch(source, /gh\s+pr\s+merge/i);
});
