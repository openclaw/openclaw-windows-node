"use strict";

const MAX_LOG_BYTES = 32 * 1024 * 1024;
const MAX_RESULT_CHARS = 16000;
const REQUEST_TIMEOUT_MS = 60000;
const UUID = /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/;
const ENDPOINTS = new Set([
  "https://api.githubcopilot.com", "https://api.individual.githubcopilot.com",
  "https://api.business.githubcopilot.com", "https://api.enterprise.githubcopilot.com",
]);

// Same endpoint discovery and SSE transport as GitHub CLI's agent-task view.
// Never derive authority from prompts, tool output, reasoning, or a branch file.
async function readTaskResult(copilot, task, candidateHash) {
  if (!task.sessions?.length) return null;
  if (task.state !== "completed" || task.session_count !== 1 || task.sessions.length !== 1)
    throw new Error("Task result requires one completed session; reconcile manually");
  const session = task.sessions[0];
  if (!UUID.test(session.id) || session.task_id !== task.id || session.state !== "completed"
    || !Number.isSafeInteger(task.repository?.id) || session.repository?.id !== task.repository.id)
    throw new Error("Task result session identity mismatch");
  const { data } = await copilot.request("POST /graphql", {
    query: "query { viewer { copilotEndpoints { api } } }",
  });
  const endpoint = data?.data?.viewer?.copilotEndpoints?.api;
  if (!ENDPOINTS.has(endpoint)) throw new Error("Unapproved Copilot result endpoint");
  // CAPI requires Bearer, whereas Octokit's auth hook rewrites it to token.
  // Obtain only this dedicated client's credential, after endpoint validation.
  const auth = await copilot.auth();
  if (auth.type !== "token" || !auth.token) throw new Error("Copilot result requires user token authentication");
  const response = await fetch(`${endpoint}/agents/sessions/${session.id}/logs`, {
    headers: { Authorization: `Bearer ${auth.token}`,
      "Copilot-Integration-Id": "copilot-4-cli", "X-GitHub-Api-Version": "2026-01-09" },
    redirect: "error", signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
  });
  if (!response.ok) throw new Error(`Copilot session log request failed: HTTP ${response.status}`);
  const chunks = [];
  let bytes = 0;
  for await (const chunk of response.body) {
    bytes += chunk.length;
    if (bytes > MAX_LOG_BYTES) throw new Error("Copilot session log exceeds size budget");
    chunks.push(Buffer.from(chunk));
  }
  let pending = null, final = null;
  for (const line of Buffer.concat(chunks).toString("utf8").split("\n")) {
    if (!line.trim()) continue;
    if (!line.startsWith("data: ")) throw new Error("Unexpected Copilot session log format");
    if (line.slice(6).trim() === "[DONE]") continue;
    let entry;
    try { entry = JSON.parse(line.slice(6)); }
    catch { throw new Error("Malformed Copilot session log event"); }
    if (entry.object !== "chat.completion.chunk") continue;
    if (typeof entry.id !== "string" || !entry.id) throw new Error("Missing Copilot result message identity");
    if (Array.isArray(entry.choices) && entry.choices.length === 0) continue;
    final = null;
    for (const choice of entry.choices ?? []) {
      const delta = choice.delta ?? {};
      if (delta.tool_calls?.length || (delta.role && delta.role !== "assistant")
        || (choice.finish_reason && choice.finish_reason !== "stop")) {
        pending = null;
        continue;
      }
      // CAPI sometimes omits indexes, including in GitHub CLI's log fixtures.
      if ((choice.index ?? 0) !== 0 || (choice.index == null && entry.choices.length !== 1))
        throw new Error("Ambiguous Copilot result choice");
      if (delta.role === "assistant" && pending?.id !== entry.id) pending = { id: entry.id, text: "" };
      if (!pending || pending.id !== entry.id) continue;
      if (typeof delta.content === "string") pending.text += delta.content;
      if (pending.text.length > MAX_RESULT_CHARS) throw new Error("Copilot final answer exceeds size budget");
      if (choice.finish_reason === "stop") { final = pending.text.trim(); pending = null; }
    }
  }
  if (!final) return null;
  const markers = final.match(/<!-- upstream-gateway-disposition:v1:[\s\S]*?-->/g) ?? [];
  const match = markers.length === 1 && markers[0].match(new RegExp(
    "^<!-- upstream-gateway-disposition:v1:" + candidateHash
      + ":(supported|intentionally-unsupported|existing-work|confirmed-gap) -->$"));
  if (!match || final.replace(markers[0], "").trim().length < 80) return null;
  return { sessionId: session.id, disposition: match[1], text: final };
}

module.exports = { readTaskResult };
