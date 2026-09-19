// Disposable hosted Windows only. No mock pairing, registry writer, or TS source patch.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';

const producerSha = '6dddf3d2eabf3316d4dcc726109adab7fdccf7ea';
const consumerSha = '2048e9b7fa3edcf9993925f29defb17af2c52ebf';
const origin = 'chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/';
const nonce = 'AAAAAAAAAAAAAAAAAAAAAA';
const [artifact, core, receiptFile] = process.argv.slice(2);
const receipt = { producerSha, consumerSha, syntheticPairing: false, cases: [], status: 'failed' };
let stage = 'preflight';
let context;
let executable;
let attemptedInstall = false;
const ps = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const baseEnv = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(OPENCLAW_|NODE_)/i.test(key)));
function powershell(script) {
  return execFileSync(ps, ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')],
    { encoding: 'utf8', timeout: 30000, maxBuffer: 32768, windowsHide: true });
}
function check(name) { receipt.cases.push(name); console.log('COMPOSED_CASE_OK ' + name); }
function request(action, selected = context) {
  return { v: 1, action, mode: 'native-windows-cli', context: selected, expectedOrigins: [origin], store: 'preserve' };
}
async function exchange(file, args, input, { end = false, timeout = 65000, env = baseEnv } = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(file, args, { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let out = Buffer.alloc(0), errBytes = 0, finished = false;
    const timer = setTimeout(() => {
      // Owned PID only, never a process-name/global kill. No caller command interpolation.
      if (child.pid) { try { execFileSync(path.join(process.env.SystemRoot, 'System32', 'taskkill.exe'), ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore', timeout: 5000 }); } catch {} }
      finish(new Error('bounded_process_timeout'));
    }, timeout);
    function finish(error, value) { if (finished) return; finished = true; clearTimeout(timer); error ? reject(error) : resolve(value); }
    child.once('error', () => finish(new Error('process_start_failed')));
    child.stdout.on('data', (part) => { out = Buffer.concat([out, part]); if (out.length > 1048576) { child.kill(); finish(new Error('output_bound')); } });
    child.stderr.on('data', (part) => { errBytes += part.length; if (errBytes > 32768) { child.kill(); finish(new Error('stderr_bound')); } });
    child.once('close', (code) => finish(null, { code, out, errBytes }));
    child.stdin.on('error', () => {});
    child.stdin.write(input);
    if (end) child.stdin.end();
  });
}
async function manage(value, end = true) {
  const result = await exchange(executable, ['--manage'], Buffer.from(JSON.stringify(value)), { end });
  assert.equal(result.errBytes, 0, 'management_stderr');
  assert.ok(result.out.length <= 32768 && result.out.at(-1) === 10, 'management_receipt_bound');
  const body = JSON.parse(result.out.toString('utf8'));
  assert.equal(result.code, body.ok ? 0 : 1, 'management_exit_mismatch');
  return body;
}
function frame(value) { const bytes = Buffer.from(JSON.stringify(value)); const header = Buffer.alloc(4); header.writeUInt32LE(bytes.length); return Buffer.concat([header, bytes]); }
function response(result) {
  assert.equal(result.code, 0, 'native_exit'); assert.equal(result.errBytes, 0, 'native_stderr');
  assert.ok(result.out.length >= 4, 'native_response_missing');
  assert.equal(result.out.readUInt32LE(0), result.out.length - 4, 'native_response_length');
  return JSON.parse(result.out.subarray(4).toString('utf8'));
}
function refused(value, code) { assert.equal(value.ok, false, 'request_not_refused'); assert.equal(value.code, code, 'refusal_code'); assert.equal(value.pairingString, undefined, 'secret_on_refusal'); }
try {
  assert.equal(process.platform, 'win32', 'native_Windows_required');
  assert.equal(process.env.GITHUB_ACTIONS, 'true', 'disposable_GitHub_runner_required');
  assert.equal(process.env.RUNNER_ENVIRONMENT, 'github-hosted', 'self_hosted_runner_refused');
  assert.ok(artifact && core && receiptFile, 'explicit_artifact_core_receipt_required');
  assert.equal(execFileSync('git', ['-C', core, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), consumerSha, 'consumer_revision');
  const platform = await fs.readFile(path.join(core, 'extensions/browser/src/browser/extension-windows-platform.ts'), 'utf8');
  assert.ok(platform.includes('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'), 'pinned_TrustedInstaller_ancestor_rule');
  // Refuse all existing product registration/generations. Do not adopt or erase them.
  const known = JSON.parse(powershell(
    "$ErrorActionPreference='Stop'; $local=[Environment]::GetFolderPath('LocalApplicationData'); " +
    "$paths=@('Software\\Google\\Chrome\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap','Software\\Chromium\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap','Software\\Google\\Chrome\\Extensions\\kcdjddhmeafeomebliikmbpblkmkfoig'); " +
    "foreach($h in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)){foreach($v in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)){$r=[Microsoft.Win32.RegistryKey]::OpenBaseKey($h,$v);try{foreach($p in $paths){$k=$r.OpenSubKey($p);if($null-ne $k){$k.Dispose();throw 'Existing product registration'}}}finally{$r.Dispose()}}}; " +
    "$root=Join-Path $local 'OpenClawTray\\browser-native\\generations';if(Test-Path -LiteralPath $root){throw 'Existing product generations'}; " +
    "[Console]::Out.Write((ConvertTo-Json -Compress @{local=$local;sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}))"));
  stage = 'private-fixture';
  const fixture = await fs.mkdtemp(path.join(os.tmpdir(), 'OpenClawComposed-'));
  const encoded = Buffer.from(fixture).toString('base64');
  powershell("$ErrorActionPreference='Stop';$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'));$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($sid);foreach($s in @($sid.Value,'S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($s),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))};[IO.Directory]::SetAccessControl($p,$acl)");
  executable = path.join(fixture, 'OpenClaw.BrowserBootstrap.exe');
  const source = path.join(artifact, 'tools', 'browser-bootstrap', 'OpenClaw.BrowserBootstrap.exe');
  await fs.copyFile(source, executable, fs.constants.COPYFILE_EXCL);
  const hash = async (file) => crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex');
  receipt.producerExecutableSha256 = await hash(source);
  assert.equal(await hash(executable), receipt.producerExecutableSha256, 'producer_copy_hash');
  const state = path.join(fixture, 'state'); await fs.mkdir(state);
  const configPath = path.join(state, 'openclaw.json');
  await fs.writeFile(configPath, JSON.stringify({ gateway: { mode: 'local', port: 18789 }, browser: { enabled: true, profiles: { chrome: { driver: 'extension', color: '#0088cc' } } } }), { flag: 'wx' });
  context = { nodePath: await fs.realpath(process.execPath), cliPath: await fs.realpath(path.join(core, 'openclaw.mjs')), stateDir: state, configPath, browserProfile: 'chrome' };
  stage = 'management-before-eof';
  refused(await manage(request('install'), false), 'invalid_request');
  check('management_missing_eof_rejected');
  stage = 'management-install-real-ts-probes'; attemptedInstall = true;
  const installed = await manage(request('install'));
  if (!installed.ok) throw new Error('management_install_' + installed.code);
  assert.equal(installed.registration, 'owned'); assert.equal(installed.mode, 'native-windows-cli');
  assert.equal(installed.store, 'missing');
  const installation = installed.installation;
  receipt.generation = installation.generation;
  check('management_generation_and_real_ts_rejection_probes');
  const packet = frame({ v: 1, op: 'bootstrap', nonce });
  stage = 'composed-bootstrap';
  const paired = response(await exchange(installation.launcherPath, [origin], packet));
  assert.equal(paired.ok, true, 'canonical_pairing_failed'); assert.equal(paired.nonce, nonce);
  const pairing = new URL(paired.pairingString);
  assert.equal(pairing.protocol, 'ws:'); assert.equal(pairing.hostname, '127.0.0.1');
  assert.equal(pairing.pathname, '/browser/extension'); assert.ok(pairing.hash.length >= 33, 'host_local_relay_secret_missing');
  check('producer_to_canonical_ts_native_admission_and_real_pairing');
  stage = 'origin-rejection';
  refused(response(await exchange(installation.launcherPath, ['chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/'], packet)), 'origin_forbidden');
  check('origin_rejection');
  stage = 'management-context-rejection';
  refused(await manage(request('install', { ...context, browserProfile: 'other' })), 'context_conflict');
  refused(await manage(request('install', { ...context, browserProfile: 'Chrome!' })), 'invalid_request');
  check('management_context_and_profile_rejection');
  const nativeArgs = [context.cliPath, 'browser', 'extension', 'native-host', '--manifest', installation.manifestPath, '--launcher', installation.launcherPath, '--expected-origin', origin, '--browser-profile'];
  const env = { ...baseEnv, OPENCLAW_STATE_DIR: state, OPENCLAW_CONFIG_PATH: configPath, OPENCLAW_NO_RESPAWN: '1' };
  stage = 'ts-profile-rejection';
  refused(response(await exchange(context.nodePath, [...nativeArgs, 'other', origin], packet, { env })), 'manifest_invalid');
  check('actual_ts_profile_rejection');
  stage = 'ts-context-rejection';
  const otherState = path.join(fixture, 'other-state'); await fs.mkdir(otherState);
  refused(response(await exchange(context.nodePath, [...nativeArgs, 'chrome', origin], packet, { env: { ...env, OPENCLAW_STATE_DIR: otherState } })), 'manifest_invalid');
  check('actual_ts_state_context_rejection');
  stage = 'chrome-eof-cancellation';
  const cancelled = await exchange(installation.launcherPath, [origin], packet, { end: true, timeout: 10000 });
  assert.equal(cancelled.code, 0); assert.equal(cancelled.out.length, 0, 'pairing_published_after_eof'); assert.equal(cancelled.errBytes, 0);
  check('chrome_eof_cancels_without_pairing');
  stage = 'final-generation-inspection';
  const inspected = await manage(request('inspect')); assert.equal(inspected.installation.generation, installation.generation); assert.equal(inspected.ok, true);
  check('rejections_preserve_original_generation');
  receipt.status = 'passed';
} catch (error) {
  // Never serialize native stdout, pairing strings, config contents or a child diagnostic.
  receipt.failureStage = stage;
  receipt.failure = error instanceof Error && /^management_install_[a-z_]+$/.test(error.message) ? error.message : (error?.code === 'ERR_ASSERTION' ? 'assertion_failed' : 'execution_failed');
  process.exitCode = 1;
} finally {
  if (attemptedInstall) {
    try {
      const removed = await manage(request('uninstall'));
      receipt.cleanup = removed.ok && removed.registration === 'missing' ? 'owned_registration_removed' : 'incomplete';
      if (receipt.cleanup === 'incomplete') { receipt.status = 'failed'; process.exitCode = 1; }
    } catch { receipt.cleanup = 'incomplete'; receipt.status = 'failed'; process.exitCode = 1; }
  }
  if (receiptFile) await fs.writeFile(receiptFile, JSON.stringify(receipt, null, 2) + '\n');
  console.log(JSON.stringify(receipt));
}
