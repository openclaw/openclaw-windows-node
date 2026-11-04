"use strict";

const fs = require("node:fs");
const net = require("node:net");
const dgram = require("node:dgram");
const dns = require("node:dns");
const tls = require("node:tls");
const childProcess = require("node:child_process");
const { syncBuiltinESMExports } = require("node:module");

const marker = "OPENCLAW_STARTUP_COMPARISON=";
const startedUtc = new Date(Date.now() - performance.now()).toISOString();
const blocked = { child: 0, network: 0 };
const spans = {};
let sessionConfirmed = false;
let outcome = "early-exit";
let lastPhase = null;
let errorKind = null;

function deny(kind) {
  return () => {
    blocked[kind] += 1;
    const error = new Error("Blocked by the startup comparison safety guard");
    error.code = "OPENCLAW_COMPARISON_BLOCKED";
    throw error;
  };
}

for (const name of ["spawn", "spawnSync", "exec", "execSync", "execFile", "execFileSync", "fork"]) {
  childProcess[name] = deny("child");
}
net.connect = deny("network");
net.createConnection = deny("network");
net.Server.prototype.listen = deny("network");
net.Socket.prototype.connect = deny("network");
dgram.createSocket = deny("network");
tls.connect = deny("network");
for (const name of Object.keys(dns)) {
  if (name === "lookup" || name === "lookupService" || name.startsWith("resolve")) {
    dns[name] = deny("network");
  }
}
if (dns.promises) {
  for (const name of Object.keys(dns.promises)) {
    if (name === "lookup" || name === "lookupService" || name.startsWith("resolve")) {
      dns.promises[name] = deny("network");
    }
  }
}
for (const Resolver of [dns.Resolver, dns.promises?.Resolver]) {
  if (!Resolver?.prototype) {
    continue;
  }
  for (const name of Object.getOwnPropertyNames(Resolver.prototype)) {
    if (name === "reverse" || name.startsWith("resolve")) {
      Resolver.prototype[name] = deny("network");
    }
  }
}
globalThis.fetch = async () => deny("network")();
syncBuiltinESMExports();

function discard(_chunk, encoding, callback) {
  const done = typeof encoding === "function" ? encoding : callback;
  if (typeof done === "function") {
    process.nextTick(done);
  }
  return true;
}

process.stdout.write = discard;
process.stderr.write = function capture(chunk, encoding, callback) {
  const text = Buffer.isBuffer(chunk) ? chunk.toString("utf8") : String(chunk);
  const phase = text.match(/cli\.bootstrap\.admission\.([a-z0-9-]+)(?:\s+starting|\s+[0-9.]+ms)/);
  if (phase) {
    lastPhase = phase[1];
  }
  const completed = text.match(
    /cli\.bootstrap\.admission\.(session-runtime-import|plugin-config)\s+([0-9.]+)ms\s+total=([0-9.]+)ms\s+start=([0-9.]+)ms/,
  );
  if (completed) {
    spans[completed[1]] = {
      elapsed_ms: Number(completed[2]),
      total_ms: Number(completed[3]),
      start_ms: Number(completed[4]),
    };
  }
  if (
    spans["session-runtime-import"] &&
    text.includes("cli.bootstrap.admission.agent-inventory starting")
  ) {
    sessionConfirmed = true;
  }
  if (
    sessionConfirmed &&
    spans["plugin-config"] &&
    text.includes("cli.bootstrap.admission.config-recovery starting")
  ) {
    outcome = "captured";
    process.exit(0);
  }
  return discard(chunk, encoding, callback);
};

process.on("uncaughtExceptionMonitor", (error) => {
  const candidate = typeof error?.code === "string" ? error.code : error?.name;
  errorKind = /^[A-Za-z0-9_.-]{1,64}$/.test(candidate ?? "") ? candidate : "Error";
});

process.once("exit", (exitCode) => {
  const cpu = process.cpuUsage();
  const result = {
    outcome,
    exit_code: exitCode,
    node_version: process.versions.node,
    architecture: process.arch,
    pid: process.pid,
    started_utc: startedUtc,
    cutoff_utc: new Date().toISOString(),
    elapsed_ms: Number(performance.now().toFixed(1)),
    startup_to_first_phase_ms: spans["session-runtime-import"]?.start_ms ?? null,
    cpu_user_ms: Number((cpu.user / 1000).toFixed(1)),
    cpu_system_ms: Number((cpu.system / 1000).toFixed(1)),
    session_confirmed: sessionConfirmed,
    spans,
    last_phase: lastPhase,
    blocked,
    error_kind: errorKind,
  };
  fs.writeSync(1, marker + JSON.stringify(result) + "\n");
});
