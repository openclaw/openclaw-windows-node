import { execFile, execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer } from "node:http";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import {
    CanvasError,
    createCanvas,
    joinSession,
} from "@github/copilot-sdk/extension";
import {
    applyAdversarialReview,
    mergeLiveState,
    normalizeTriageInput,
    reconcileOpenInventory,
} from "./triage-state.mjs";
import {
    exactLookupResultIsValid,
    selectExactLookupItems,
} from "./triage-live.mjs";
import {
    requestHostMatches,
    requestItemAction,
    requestTokenMatches,
} from "./triage-actions.mjs";
import { renderDashboardHtml } from "./triage-ui.mjs";

const execFileAsync = promisify(execFile);
const instances = new Map();
const instanceStarts = new Map();
const extensionDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = dirname(dirname(dirname(extensionDirectory)));
let copilotSession;
let ghPath;

function pathIsInside(candidate, parent) {
    const child = resolve(candidate);
    const root = resolve(parent);
    const relativePath = relative(root, child);
    return relativePath === "" || (!relativePath.startsWith("..") && !isAbsolute(relativePath));
}

function resolveGhPath() {
    const candidates = [
        process.env.ProgramFiles && join(process.env.ProgramFiles, "GitHub CLI", "gh.exe"),
        process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, "Programs", "GitHub CLI", "gh.exe"),
    ].filter(Boolean);

    const windowsDirectory = process.env.WINDIR ?? "C:\\Windows";
    const wherePath = join(windowsDirectory, "System32", "where.exe");
    if (existsSync(wherePath)) {
        try {
            const output = execFileSync(wherePath, ["gh.exe"], {
                cwd: homedir(),
                encoding: "utf8",
                stdio: ["ignore", "pipe", "ignore"],
                windowsHide: true,
            });
            candidates.push(...output.split(/\r?\n/).map((entry) => entry.trim()).filter(Boolean));
        } catch {
            // Fall through to the fixed installation candidates.
        }
    }

    const ghPath = candidates.find((candidate) =>
        existsSync(candidate) && !pathIsInside(candidate, repositoryRoot));
    if (!ghPath) {
        throw new Error("GitHub CLI was not found outside the repository");
    }
    return ghPath;
}

function getGhPath() {
    ghPath ??= resolveGhPath();
    return ghPath;
}

async function runGhJson(argumentsList) {
    let lastError;
    for (let attempt = 0; attempt < 2; attempt += 1) {
        try {
            const { stdout } = await execFileAsync(getGhPath(), argumentsList, {
                cwd: homedir(),
                encoding: "utf8",
                maxBuffer: 16 * 1024 * 1024,
                timeout: 45_000,
                windowsHide: true,
            });
            return JSON.parse(stdout);
        } catch (error) {
            lastError = error;
            const message = error instanceof Error ? error.message : String(error);
            if (attempt === 0 && /HTTP 50[234]|ETIMEDOUT|timed out/i.test(message)) {
                await new Promise((resolveDelay) => setTimeout(resolveDelay, 1_500));
                continue;
            }
            throw error;
        }
    }
    throw lastError;
}

async function collectLiveState(repo, items) {
    const [pullRequests, issues] = await Promise.all([
        runGhJson([
            "pr", "list",
            "--repo", repo,
            "--state", "open",
            "--limit", "1000",
            "--json",
            "number,title,url,state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid,updatedAt,author,labels,statusCheckRollup",
        ]),
        runGhJson([
            "issue", "list",
            "--repo", repo,
            "--state", "open",
            "--limit", "1000",
            "--json",
            "number,title,url,state,stateReason,updatedAt,author,labels",
        ]),
    ]);
    const exactLookupItems = selectExactLookupItems(items, pullRequests, issues);
    const exactLookupResults = await Promise.allSettled(exactLookupItems.map(async (item) => {
        const fields = item.type === "pr"
            ? "number,title,url,state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid,updatedAt,author,labels,statusCheckRollup"
            : "number,title,url,state,stateReason,updatedAt,author,labels";
        const value = await runGhJson([
            item.type, "view",
            String(item.number),
            "--repo", repo,
            "--json", fields,
        ]);
        if (!exactLookupResultIsValid(item, value)) {
            throw new Error(`GitHub returned incomplete live state for ${item.type} #${item.number}`);
        }
        return { type: item.type, value };
    }));
    for (const [resultIndex, result] of exactLookupResults.entries()) {
        const lookupItem = exactLookupItems[resultIndex];
        const collection = lookupItem.type === "pr" ? pullRequests : issues;
        if (result.status === "fulfilled") {
            const index = collection.findIndex((item) => item.number === result.value.value.number);
            if (index >= 0) {
                collection[index] = result.value.value;
            } else {
                collection.push(result.value.value);
            }
        } else {
            const index = collection.findIndex((item) => item.number === lookupItem.number);
            if (index >= 0) collection.splice(index, 1);
        }
    }
    const failedLookups = exactLookupResults
        .map((result, index) => result.status === "rejected" ? exactLookupItems[index] : null)
        .filter(Boolean)
        .map((item) => `${item.type.toUpperCase()} #${item.number}`);
    return {
        pullRequests,
        issues,
        refreshWarning: failedLookups.length > 0
            ? `Live lookup failed for ${failedLookups.join(", ")}. Other items are current.`
            : "",
    };
}

function safeErrorMessage(error) {
    const message = error instanceof Error ? error.message : String(error);
    const usefulLine = message
        .split(/\r?\n/)
        .map((line) => line.trim())
        .find((line) => /^HTTP \d{3}:|timed out|ETIMEDOUT/i.test(line));
    return (usefulLine ?? "GitHub refresh failed. Use Refresh now to retry.")
        .replaceAll(repositoryRoot, "<repo>")
        .slice(0, 500);
}

function clientErrorMessage(error) {
    return (error instanceof Error ? error.message : String(error))
        .replaceAll(repositoryRoot, "<repo>")
        .slice(0, 500);
}

function sendState(entry) {
    const payload = `event: state\ndata: ${JSON.stringify(entry.state)}\n\n`;
    for (const client of entry.eventClients) {
        try {
            client.write(payload);
        } catch {
            entry.eventClients.delete(client);
        }
    }
}

function readSessionData() {
    const databasePath = copilotSession?.workspacePath
        ? join(copilotSession.workspacePath, "session.db")
        : "";
    if (!databasePath || !existsSync(databasePath)) {
        return { adversarialReviews: [], error: "", tasks: [] };
    }

    try {
        const database = new DatabaseSync(databasePath, { readOnly: true });
        try {
            const tableExists = (name) => Boolean(database.prepare(
                "SELECT 1 AS present FROM sqlite_master WHERE type = 'table' AND name = ?",
            ).get(name));
            const tasks = tableExists("todos")
                ? database.prepare(
                    `SELECT id, title, status
                     FROM todos
                     ORDER BY created_at, id`,
                ).all().map((task) => ({
                    id: String(task.id),
                    status: String(task.status),
                    title: String(task.title),
                }))
                : [];
            const findings = tableExists("review_findings")
                ? database.prepare(
                    `SELECT id, pr_number, issue, opus_severity, codex_severity,
                            consensus, fix_confidence, disposition
                     FROM review_findings
                     ORDER BY pr_number, id`,
                ).all()
                : [];
            const findingsByPullRequest = new Map();
            for (const finding of findings) {
                const number = Number(finding.pr_number);
                const entries = findingsByPullRequest.get(number) ?? [];
                entries.push({
                    codexSeverity: String(finding.codex_severity ?? ""),
                    consensus: String(finding.consensus ?? ""),
                    disposition: String(finding.disposition ?? ""),
                    fixConfidence: Number(finding.fix_confidence),
                    id: String(finding.id),
                    issue: String(finding.issue),
                    opusSeverity: String(finding.opus_severity ?? ""),
                });
                findingsByPullRequest.set(number, entries);
            }
            const reviewColumns = tableExists("adversarial_reviews")
                ? new Set(database.prepare("PRAGMA table_info(adversarial_reviews)")
                    .all()
                    .map((column) => String(column.name)))
                : new Set();
            const finalVerdictColumns = [
                "final_decision",
                "take_confidence",
                "recommendation_confidence",
                "next_action",
            ];
            const hasFinalVerdictColumns = finalVerdictColumns.every((column) =>
                reviewColumns.has(column));
            const reviewProjection = hasFinalVerdictColumns
                ? `pr_number, reviewed_head_sha, status, opus_status, codex_status,
                   final_decision, take_confidence, recommendation_confidence,
                   next_action, summary, updated_at`
                : `pr_number, reviewed_head_sha, status, opus_status, codex_status,
                   summary, updated_at`;
            const adversarialReviews = reviewColumns.size > 0
                ? database.prepare(
                    `SELECT ${reviewProjection}
                     FROM adversarial_reviews
                     ORDER BY pr_number DESC`,
                ).all().map((review) => {
                    const number = Number(review.pr_number);
                    const reviewFindings = findingsByPullRequest.get(number) ?? [];
                    return {
                        acceptedCount: reviewFindings.filter((finding) =>
                            finding.disposition.startsWith("accepted")).length,
                        codexStatus: String(review.codex_status),
                        finalDecision: review.final_decision,
                        findings: reviewFindings,
                        nextAction: review.next_action,
                        opusStatus: String(review.opus_status),
                        prNumber: number,
                        recommendationConfidence: review.recommendation_confidence,
                        rejectedCount: reviewFindings.filter((finding) =>
                            finding.disposition.startsWith("rejected")).length,
                        reviewedHeadSha: String(review.reviewed_head_sha),
                        status: String(review.status),
                        summary: String(review.summary),
                        takeConfidence: review.take_confidence,
                        updatedAt: String(review.updated_at),
                    };
                })
                : [];
            return { adversarialReviews, error: "", tasks };
        } finally {
            database.close();
        }
    } catch (error) {
        return {
            adversarialReviews: [],
            error: `Session data unavailable: ${clientErrorMessage(error)}`,
            tasks: [],
        };
    }
}

function mergeSessionData(state) {
    const sessionData = readSessionData();
    const reviewByNumber = new Map(sessionData.adversarialReviews
        .map((review) => [review.prNumber, review]));
    const items = state.items.map((item) => {
        const adversarialReview = item.type === "pr"
            ? reviewByNumber.get(item.number) ?? null
            : null;
        return applyAdversarialReview(item, adversarialReview);
    });
    return {
        ...state,
        adversarialReviews: items
            .map((item) => item.adversarialReview)
            .filter(Boolean),
        items,
        sessionDataError: sessionData.error,
        sessionTasks: sessionData.tasks,
    };
}

function refreshSessionData(entry) {
    const nextState = mergeSessionData({
        ...mergeLiveState(
            entry.triage,
            entry.pullRequests,
            entry.issues,
            entry.state.refreshError,
        ),
        refreshWarning: entry.state.refreshWarning,
    });
    if (JSON.stringify(nextState.sessionTasks) === JSON.stringify(entry.state.sessionTasks) &&
        JSON.stringify(nextState.adversarialReviews) === JSON.stringify(entry.state.adversarialReviews) &&
        nextState.sessionDataError === entry.state.sessionDataError) {
        return;
    }
    entry.state = nextState;
    sendState(entry);
}

async function refreshEntry(entry, force = false) {
    if (entry.refreshPromise) {
        if (!force) {
            return entry.refreshPromise;
        }
        await entry.refreshPromise;
        if (entry.refreshPromise) {
            return refreshEntry(entry, true);
        }
    }
    const refreshPromise = (async () => {
        try {
            const live = await collectLiveState(entry.triage.repo, entry.triage.items);
            entry.pullRequests = live.pullRequests;
            entry.issues = live.issues;
            entry.triage = reconcileOpenInventory(entry.triage, entry.pullRequests, entry.issues);
            entry.state = mergeSessionData({
                ...mergeLiveState(entry.triage, entry.pullRequests, entry.issues),
                refreshWarning: live.refreshWarning,
            });
        } catch (error) {
            entry.triage = reconcileOpenInventory(entry.triage, entry.pullRequests, entry.issues);
            entry.state = mergeSessionData({
                ...mergeLiveState(
                    entry.triage,
                    entry.pullRequests,
                    entry.issues,
                    safeErrorMessage(error),
                ),
                refreshWarning: "",
            });
        }
        sendState(entry);
        return entry.state;
    })();
    entry.refreshPromise = refreshPromise;
    try {
        return await refreshPromise;
    } finally {
        if (entry.refreshPromise === refreshPromise) {
            entry.refreshPromise = null;
        }
    }
}

async function readJsonBody(request) {
    let body = "";
    for await (const chunk of request) {
        body += chunk;
        if (body.length > 8_192) {
            throw new Error("Request body is too large");
        }
    }
    return body ? JSON.parse(body) : {};
}

function writeJson(response, statusCode, value) {
    response.writeHead(statusCode, {
        "Cache-Control": "no-store",
        "Content-Type": "application/json; charset=utf-8",
    });
    response.end(JSON.stringify(value));
}

async function handleRequest(entry, request, response) {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    if (!requestHostMatches(request, entry.host)) {
        writeJson(response, 403, { error: "Request host rejected" });
        return;
    }
    if (request.method === "GET" && url.pathname === "/") {
        response.writeHead(200, {
            "Cache-Control": "no-store",
            "Content-Security-Policy":
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                "connect-src 'self'; base-uri 'none'; form-action 'none'",
            "Content-Type": "text/html; charset=utf-8",
            "X-Content-Type-Options": "nosniff",
        });
        response.end(renderDashboardHtml());
        return;
    }

    if (request.method === "GET" && url.pathname === "/state") {
        if (!requestTokenMatches(request, url, entry.actionToken)) {
            writeJson(response, 403, { error: "Action token rejected" });
            return;
        }
        writeJson(response, 200, entry.state);
        return;
    }

    if (request.method === "GET" && url.pathname === "/events") {
        if (!requestTokenMatches(request, url, entry.actionToken)) {
            writeJson(response, 403, { error: "Action token rejected" });
            return;
        }
        response.writeHead(200, {
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "Content-Type": "text/event-stream",
            "X-Accel-Buffering": "no",
        });
        response.write(`event: state\ndata: ${JSON.stringify(entry.state)}\n\n`);
        entry.eventClients.add(response);
        request.on("close", () => entry.eventClients.delete(response));
        return;
    }

    if (request.method === "POST" && !requestTokenMatches(request, url, entry.actionToken)) {
        writeJson(response, 403, { error: "Action token rejected" });
        return;
    }

    try {
        if (request.method === "POST" && url.pathname === "/refresh") {
            await readJsonBody(request);
            writeJson(response, 200, { state: await refreshEntry(entry) });
            return;
        }
        if (request.method === "POST" && url.pathname === "/action") {
            const input = await readJsonBody(request);
            const result = await requestItemAction(entry, input.action, input, {
                createError: (code, message) => new CanvasError(code, message),
                refresh: () => refreshEntry(entry, true),
                send: (message) => copilotSession.send(message),
            });
            writeJson(response, 202, result);
            return;
        }
    } catch (error) {
        const statusCode = error instanceof CanvasError ? 409 : 400;
        writeJson(response, statusCode, { error: clientErrorMessage(error) });
        return;
    }

    writeJson(response, 404, { error: "Not found" });
}

async function startInstance(instanceId, triage) {
    const { randomBytes } = await import("node:crypto");
    const entry = {
        actionToken: randomBytes(24).toString("hex"),
        eventClients: new Set(),
        host: "",
        issues: [],
        pullRequests: [],
        refreshPromise: null,
        server: null,
        state: mergeSessionData(mergeLiveState(triage, [], [])),
        taskTimer: null,
        timer: null,
        triage,
        url: "",
    };
    const server = createServer((request, response) => {
        handleRequest(entry, request, response).catch((error) => {
            if (!response.headersSent) {
                writeJson(response, 500, { error: safeErrorMessage(error) });
            } else {
                response.destroy();
            }
        });
    });
    await new Promise((resolveListen, rejectListen) => {
        server.once("error", rejectListen);
        server.listen(0, "127.0.0.1", resolveListen);
    });
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    entry.server = server;
    entry.host = `127.0.0.1:${port}`;
    entry.url = `http://${entry.host}/#token=${entry.actionToken}`;
    instances.set(instanceId, entry);
    return entry;
}

async function getOrStartInstance(instanceId, triage) {
    const existing = instances.get(instanceId);
    if (existing) {
        return existing;
    }
    let pending = instanceStarts.get(instanceId);
    if (!pending) {
        pending = startInstance(instanceId, triage);
        instanceStarts.set(instanceId, pending);
    }
    try {
        return await pending;
    } finally {
        if (instanceStarts.get(instanceId) === pending) {
            instanceStarts.delete(instanceId);
        }
    }
}

function reconfigureInstance(entry, triage) {
    entry.triage = reconcileOpenInventory(triage, entry.pullRequests, entry.issues);
    entry.state = mergeSessionData(mergeLiveState(entry.triage, entry.pullRequests, entry.issues));
    clearInterval(entry.timer);
    clearInterval(entry.taskTimer);
    entry.timer = setInterval(() => {
        refreshEntry(entry).catch(() => {});
    }, triage.refreshSeconds * 1_000);
    entry.timer.unref();
    entry.taskTimer = setInterval(() => refreshSessionData(entry), 2_000);
    entry.taskTimer.unref();
    refreshEntry(entry, true).catch(() => {});
}

async function closeInstance(instanceId) {
    const entry = instances.get(instanceId);
    if (!entry) {
        return;
    }
    instances.delete(instanceId);
    clearInterval(entry.timer);
    clearInterval(entry.taskTimer);
    for (const client of entry.eventClients) {
        client.end();
    }
    await new Promise((resolveClose) => entry.server.close(resolveClose));
}

const inputSchema = {
    type: "object",
    required: ["schemaVersion", "repo", "title", "scope", "generatedAt", "items"],
    properties: {
        schemaVersion: { const: 1 },
        repo: { type: "string" },
        title: { type: "string" },
        scope: { type: "string" },
        generatedAt: { type: "string" },
        refreshSeconds: { type: "integer", minimum: 30, maximum: 300 },
        items: { type: "array", minItems: 1, maxItems: 1000 },
        plan: { type: "array", maxItems: 1000 },
        report: {
            type: "object",
            properties: {
                changes: { type: "array", maxItems: 100 },
                executiveQueue: { type: "array", maxItems: 100 },
                ownership: { type: "array", maxItems: 100 },
                reviews: { type: "array", maxItems: 100 },
                dayPlan: { type: "array", maxItems: 100 },
                automation: { type: "array", maxItems: 100 },
            },
            additionalProperties: false,
        },
    },
    additionalProperties: false,
};

const itemActionSchema = {
    type: "object",
    required: ["number"],
    properties: {
        number: { type: "integer", minimum: 1 },
    },
    additionalProperties: false,
};

const dashboard = createCanvas({
    id: "openclaw-triage-dashboard",
    displayName: "OpenClaw triage",
    description: "Shows live OpenClaw triage checks, execution-plan progress, proof gates, and guarded actions.",
    inputSchema,
    actions: [
        {
            name: "refresh",
            description: "Refresh GitHub checks and item state now.",
            handler: async (context) => {
                const entry = instances.get(context.instanceId);
                if (!entry) throw new CanvasError("instance_not_found", "Triage canvas is not open");
                return refreshEntry(entry);
            },
        },
        {
            name: "request_next_action",
            description: "Route a guarded next-step request to the item's dedicated child session.",
            inputSchema: itemActionSchema,
            handler: async (context) => {
                const entry = instances.get(context.instanceId);
                if (!entry) throw new CanvasError("instance_not_found", "Triage canvas is not open");
                return requestItemAction(entry, "request_next_action", context.input, {
                    createError: (code, message) => new CanvasError(code, message),
                    refresh: () => refreshEntry(entry, true),
                    send: (message) => copilotSession.send(message),
                });
            },
        },
        {
            name: "request_merge",
            description: "Route fresh merge verification to the item's dedicated child session.",
            inputSchema: {
                type: "object",
                required: ["number", "headSha"],
                properties: {
                    number: { type: "integer", minimum: 1 },
                    headSha: { type: "string", pattern: "^[0-9a-fA-F]{7,64}$" },
                },
                additionalProperties: false,
            },
            handler: async (context) => {
                const entry = instances.get(context.instanceId);
                if (!entry) throw new CanvasError("instance_not_found", "Triage canvas is not open");
                return requestItemAction(entry, "request_merge", context.input, {
                    createError: (code, message) => new CanvasError(code, message),
                    refresh: () => refreshEntry(entry, true),
                    send: (message) => copilotSession.send(message),
                });
            },
        },
    ],
    open: async (context) => {
        const triage = normalizeTriageInput(context.input);
        const entry = await getOrStartInstance(context.instanceId, triage);
        reconfigureInstance(entry, triage);
        return {
            title: triage.title,
            status: `Live checks every ${triage.refreshSeconds}s`,
            url: entry.url,
        };
    },
    onClose: async (context) => {
        await closeInstance(context.instanceId);
    },
});

copilotSession = await joinSession({ canvases: [dashboard] });
