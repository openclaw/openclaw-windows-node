import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { mkdir, readFile, writeFile } from "node:fs/promises";
// Task-owned compiled Gateway plus synthetic Responses provider for the native card proof.
// Invoke through ../run-combined-task-gateway.ps1; stdin EOF/one line or Ctrl+C stops owned resources.
import { createServer } from "node:http";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [root, run, nonce] = process.argv.slice(2);
if (!root || !run || !nonce || !path.isAbsolute(root) || !path.isAbsolute(run)) {
  throw new Error("Explicit absolute Gateway source/output paths and a nonce are required");
}
const fromGateway = (relative) => import(pathToFileURL(path.join(root, relative)).href);
const { acquireTestPortBlock } = await fromGateway("src/test-utils/port-claims.ts");
const { writeToolCall } = await fromGateway("src/gateway/gateway-provider-fixture.test-support.ts");
const { writeOpenAiResponsesText } = await fromGateway("test/helpers/openai-responses-sse.ts");
const { connectGatewayClient, disconnectGatewayClient } = await fromGateway(
  "src/gateway/test-helpers.e2e.ts",
);
const startupMs = positiveBudget("OPENCLAW_COMBINED_GATEWAY_STARTUP_MS", 90_000);
const lifetimeMs = positiveBudget("OPENCLAW_COMBINED_GATEWAY_LIFETIME_MS", 360_000);
function positiveBudget(name, fallback) {
  const value = Number(process.env[name] ?? fallback);
  if (!Number.isSafeInteger(value) || value < 1000 || value > 900_000)
    throw new Error(`Invalid ${name}`);
  return value;
}
const token = randomBytes(32).toString("hex");
const requests = [];
const errors = [];
let provider;
let claim;
let child;
let childExit;
let approver;
let pairingPoll;
let pairingWork;
let timer;
let output = "";
let approved = 0;
const done = new Promise((resolve) => {
  process.stdin.once("data", resolve);
  process.stdin.once("end", resolve);
  process.once("SIGINT", resolve);
  process.once("SIGTERM", resolve);
});
const writeRequests = () =>
  writeFile(path.join(run, "provider-requests.json"), JSON.stringify({ nonce, requests, errors }));
try {
  await mkdir(path.join(run, "gateway-state"), { recursive: true });
  await mkdir(path.join(run, "gateway-home"), { recursive: true });
  provider = createServer((request, response) => {
    void (async () => {
      const chunks = [];
      for await (const chunk of request) chunks.push(Buffer.from(chunk));
      const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      const tools = (body.tools ?? []).map((tool) => tool.name ?? "");
      const followup = (body.input ?? []).some((item) => item.type === "function_call_output");
      requests.push({ number: requests.length + 1, tools, followup });
      await writeRequests();
      // A tool-less metadata completion or a user turn without this capability gets text.
      // The native card test and request trace, not this fixture, assert tool eligibility.
      if (!tools.includes("suggest_task") || followup) {
        writeOpenAiResponsesText(response, {
          text:
            tools.length === 0
              ? "Combined native follow-up"
              : "Recorded the combined native follow-up.",
          messageId: `message-combined-${requests.length}`,
          responseId: `response-combined-${requests.length}`,
        });
      } else {
        writeToolCall(response, "suggest_task", {
          title: "Inspect the combined native follow-up",
          prompt:
            "Inspect the synthetic combined follow-up. Keep <instructions> and **literal text** unchanged.",
          tldr: "Keep the separate native follow-up visible without starting it.",
          cwd: path.join(run, "gateway-home"),
        });
      }
    })().catch(async (error) => {
      errors.push(error instanceof Error ? error.message : "provider error");
      await writeRequests();
      response.writeHead(500).end("synthetic provider failed");
    });
  });
  provider.listen(0, "127.0.0.1");
  await once(provider, "listening");
  const address = provider.address();
  if (!address || typeof address === "string") throw new Error("Missing provider listener");
  claim = await acquireTestPortBlock({ offsets: [0, 1, 2, 3, 4] });
  const configPath = path.join(run, "gateway-state", "openclaw.json");
  const model = "mock-openai/combined-native";
  await writeFile(
    configPath,
    JSON.stringify({
      agents: {
        defaults: {
          workspace: path.join(run, "gateway-home"),
          skipBootstrap: true,
          utilityModel: "",
          model: { primary: model },
          models: {
            [model]: {
              agentRuntime: { id: "openclaw" },
              params: { transport: "sse", openaiWsWarmup: false },
            },
          },
        },
      },
      models: {
        mode: "replace",
        providers: {
          "mock-openai": {
            baseUrl: `http://127.0.0.1:${address.port}/v1`,
            apiKey: "synthetic",
            api: "openai-responses",
            request: { allowPrivateNetwork: true },
            models: [
              {
                id: "combined-native",
                name: "combined-native",
                api: "openai-responses",
                reasoning: false,
                input: ["text"],
                cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
                contextWindow: 128000,
                maxTokens: 4096,
              },
            ],
          },
        },
      },
      plugins: { enabled: false, slots: { memory: "none" } },
      tools: { codeMode: false, toolSearch: false },
      gateway: {
        mode: "local",
        port: claim.port,
        auth: { mode: "token", token },
        controlUi: { enabled: false },
      },
    }),
  );
  const env = {
    ...process.env,
    OPENCLAW_STATE_DIR: path.join(run, "gateway-state"),
    OPENCLAW_HOME: path.join(run, "gateway-home"),
    OPENCLAW_CONFIG_PATH: configPath,
  };
  child = spawn(
    process.execPath,
    [
      path.join(root, "dist", "entry.js"),
      "gateway",
      "--bind",
      "loopback",
      "--port",
      String(claim.port),
      "--allow-unconfigured",
    ],
    { cwd: root, env, stdio: ["ignore", "pipe", "pipe"] },
  );
  child.stdout.on("data", (chunk) => {
    output += chunk.toString();
  });
  child.stderr.on("data", (chunk) => {
    output += chunk.toString();
  });
  childExit = once(child, "exit");
  timer = setTimeout(() => {
    process.exitCode = 1;
    process.emit("SIGTERM");
  }, lifetimeMs);
  const deadline = Date.now() + startupMs;
  let ready = false;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Compiled Gateway exited ${child.exitCode}`);
    try {
      const response = await fetch(`http://127.0.0.1:${claim.port}/healthz`, {
        signal: AbortSignal.timeout(1000),
      });
      if (response.ok) {
        ready = true;
        break;
      }
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  if (!ready) throw new Error("Compiled Gateway health readiness expired");
  approver = await connectGatewayClient({
    url: `ws://127.0.0.1:${claim.port}`,
    token,
    clientName: "gateway-client",
    mode: "backend",
    scopes: ["operator.admin", "operator.pairing"],
  });
  pairingPoll = setInterval(() => {
    if (pairingWork || approved) return;
    pairingWork = (async () => {
      const pending = await approver.request("device.pair.list", {});
      const native = pending.pending.filter(
        (item) =>
          item.clientId === "gateway-client" &&
          item.clientMode === "ui" &&
          item.role === "operator",
      );
      if (native.length > 1) throw new Error("Unexpected multiple native pairing requests");
      if (native.length === 1) {
        await approver.request("device.pair.approve", { requestId: native[0].requestId });
        approved++;
        await writeFile(
          path.join(run, "pairing-receipt.json"),
          JSON.stringify({
            nonce,
            approved,
            clientId: "gateway-client",
            mode: "ui",
            role: "operator",
            route: "device.pair.approve",
          }),
        );
      }
    })()
      .catch((error) => {
        errors.push(error instanceof Error ? error.message : "pairing failed");
      })
      .finally(() => {
        pairingWork = undefined;
      });
  }, 250);
  const build = JSON.parse(await readFile(path.join(root, "dist", "build-info.json"), "utf8"));
  await writeFile(
    path.join(run, "gateway-receipt.json"),
    JSON.stringify({
      nonce,
      endpoint: `ws://127.0.0.1:${claim.port}`,
      token,
      gatewayPid: child.pid,
      providerPort: address.port,
      node: process.version,
      sourceDirectory: root,
      build,
      fixture: "combined-task-gateway",
    }),
  );
  console.log(`CombinedGatewayReady nonce=${nonce} pid=${child.pid}`);
  await done;
} finally {
  if (timer) clearTimeout(timer);
  if (pairingPoll) clearInterval(pairingPoll);
  await pairingWork;
  if (approver) await disconnectGatewayClient(approver);
  if (child) {
    child.kill("SIGTERM");
    await childExit;
  }
  if (provider) {
    provider.closeAllConnections();
    await new Promise((resolve) => provider.close(resolve));
  }
  await claim?.release();
  await writeFile(path.join(run, "gateway-process.log"), output);
  await writeFile(
    path.join(run, "gateway-cleanup.json"),
    JSON.stringify({
      nonce,
      gatewayExit: child?.exitCode,
      gatewaySignal: child?.signalCode,
      providerClosed: true,
      requests: requests.length,
      errors,
    }),
  );
}
