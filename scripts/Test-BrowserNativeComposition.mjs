// Disposable hosted Windows only. No mock pairing, registry writer, or TS source patch.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { recordCompletion } from './BrowserNativeProofTiming.mjs';

const producerSha = process.env.GITHUB_SHA;
const consumerSha = '57f78c49d47f445b85d4859f038e8447e08983ba';
const origin = 'chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/';
const nonce = 'AAAAAAAAAAAAAAAAAAAAAA';
const fixtureConfig = { gateway: { mode: 'local', port: 18789 }, browser: { enabled: true, profiles: { chrome: { driver: 'extension' } } } };
const [artifact, core, receiptFile] = process.argv.slice(2);
const receipt = { producerSha, consumerSha, syntheticPairing: false, cases: [], status: 'failed' };
let stage = 'preflight';
let context;
let executable;
let attemptedInstall = false;
const ps = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const baseEnv = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(OPENCLAW_|NODE_)/i.test(key)));
function powershell(script) {
  return execFileSync(ps, ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from("$ProgressPreference='SilentlyContinue';" + script, 'utf16le').toString('base64')],
    { encoding: 'utf8', timeout: 30000, maxBuffer: 262144, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
}
function check(name) { receipt.cases.push(name); console.log('COMPOSED_CASE_OK ' + name); }
function request(action, selected = context) {
  return { v: 1, action, mode: 'native-windows-cli', context: selected, expectedOrigins: [origin], store: 'preserve' };
}
async function exchange(file, args, input, { end = false, timeout = 65000, env = baseEnv, onSpawn } = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(file, args, { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let out = Buffer.alloc(0), errBytes = 0, finished = false, firstFrameAt = null;
    function terminateOwnedTree() {
      if(child.pid){try{execFileSync(path.join(process.env.SystemRoot,'System32','taskkill.exe'),['/PID',String(child.pid),'/T','/F'],{stdio:'ignore',timeout:5000});}catch{}}
    }
    child.once('spawn',async()=>{
      try {
        if(onSpawn)await onSpawn(child);
        if(finished)return;
        child.stdin.write(input);
        if(end)child.stdin.end();
      } catch {
        terminateOwnedTree();finish(new Error('owned_control_failed'));
      }
    });
    const timer = setTimeout(() => {
      // Owned PID only, never a process-name/global kill. No caller command interpolation.
      terminateOwnedTree();
      finish(new Error('bounded_process_timeout'));
    }, timeout);
    function finish(error, value) { if (finished) return; finished = true; clearTimeout(timer); error ? reject(error) : resolve(value); }
    child.once('error', () => finish(new Error('process_start_failed')));
    child.stdout.on('data', (part) => { out = Buffer.concat([out, part]); if(firstFrameAt===null && out.length>=4 && out.length>=4+out.readUInt32LE(0))firstFrameAt=performance.now(); if (out.length > 1048576) { terminateOwnedTree(); finish(new Error('output_bound')); } });
    child.stderr.on('data', (part) => { errBytes += part.length; if (errBytes > 32768) { terminateOwnedTree(); finish(new Error('stderr_bound')); } });
    child.once('close', (code) => finish(null, { code, out, errBytes, firstFrameAt }));
    child.stdin.on('error', () => {});
  });
}
async function withFrozenNative(installation, packet, fixture, operation) {
  const dir=await fs.mkdtemp(path.join(fixture,'owned-control-'));
  const helper=spawn(ps,['-NoLogo','-NoProfile','-NonInteractive','-File',fileURLToPath(new URL('./Control-BrowserNativeProofChild.ps1',import.meta.url)),'-ControlDirectory',dir],{env:baseEnv,windowsHide:true,stdio:['ignore','ignore','ignore']});
  const helperTimer=setTimeout(()=>{if(helper.pid){try{execFileSync(path.join(process.env.SystemRoot,'System32','taskkill.exe'),['/PID',String(helper.pid),'/T','/F'],{stdio:'ignore',timeout:5000});}catch{}}},40000);
  const helperDone=new Promise(resolve=>{helper.once('error',()=>{clearTimeout(helperTimer);resolve(-1);});helper.once('close',code=>{clearTimeout(helperTimer);resolve(code);});});
  let helperCode;void helperDone.then(code=>{helperCode=code;});
  const marker=async name=>{try{await fs.access(path.join(dir,name));return true;}catch{return false;}};
  async function waitMarker(name) {
    const until=performance.now()+15000;
    while(!await marker(name)) {
      if(await marker('failed') || helperCode!==undefined || performance.now()>until)throw new Error('owned_child_control_failed');
      await new Promise(resolve=>setTimeout(resolve,20));
    }
  }
  let native,work;
  try {
    await waitMarker('ready');
    work=exchange(installation.launcherPath,[origin],packet,{onSpawn:async child=>{
      native=child;
      await fs.writeFile(path.join(dir,'input.tmp'),JSON.stringify({nativePid:child.pid,launcher:installation.launcherPath,node:context.nodePath}));
      await fs.rename(path.join(dir,'input.tmp'),path.join(dir,'input.json'));
      await waitMarker('watching');
    }});
    void work.catch(()=>{});
    await waitMarker('suspended');
    return await operation({work,resume:()=>fs.writeFile(path.join(dir,'resume'),'1'),disconnect:()=>native.stdin.end()});
  } finally {
    await fs.writeFile(path.join(dir,'resume'),'1');
    if(work)await work.catch(()=>{});
    const code=await helperDone;
    assert.equal(code,0,'owned_child_control_cleanup');
  }
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

function auditPaths(roles) {
  const auditFile = fileURLToPath(new URL('./BrowserNativePathAudit.cs', import.meta.url));
  const encoded = Buffer.from(JSON.stringify({ auditFile, roles })).toString('base64');
  return JSON.parse(powershell("$ErrorActionPreference='Stop';$r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'))|ConvertFrom-Json;Add-Type -Path $r.auditFile;$result=@(foreach($p in $r.roles){$rows=@([BrowserNativePathAudit]::Inspect($p.role,$p.target,[bool]$p.privacy,[bool]$p.directory,[bool]$p.allowMissing));$bad=@($rows|Where-Object{!$_.Accepted});[ordered]@{role=$p.role;accepted=($bad.Count-eq 0);components=$rows.Count;rejections=$bad}});[Console]::Out.Write((ConvertTo-Json -InputObject $result -Depth 8 -Compress))"));
}
async function stateSnapshot(root) {
  const entries = [];
  async function walk(dir) {
    for (const name of (await fs.readdir(dir)).sort()) {
      const file = path.join(dir, name), stat = await fs.lstat(file);
      assert.ok(!stat.isSymbolicLink(), 'fixture_state_symlink');
      if (stat.isDirectory()) { assert.ok(entries.length < 256, 'fixture_state_bound'); entries.push([path.relative(root,file),'directory']); await walk(file); }
      else { assert.ok(stat.size <= 1048576 && entries.length < 256, 'fixture_state_bound'); entries.push([path.relative(root,file),crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex')]); }
    }
  }
  await walk(root); return JSON.stringify(entries); // Held privately; only equality booleans reach receipts.
}
async function isolatedContext(root, name) {
  const stateDir = path.join(root,name); await fs.mkdir(stateDir);
  const configPath = path.join(stateDir,'openclaw.json');
  await fs.writeFile(configPath,JSON.stringify(fixtureConfig),{flag:'wx'});
  return { ...context, stateDir: await fs.realpath(stateDir), configPath: await fs.realpath(configPath) };
}
async function observePriorGeneration(installation, priorContext, packet) {
  const before = await stateSnapshot(priorContext.stateDir);
  const observed = { responseKind: 'execution_failed', responseOk: false, explicitlyRefused: false, pairingEmitted: false, code: null, stateChanged: false };
  let result;
  try { result = await exchange(installation.launcherPath,[origin],packet); }
  catch { /* Preserve post-launch state evidence even on timeout or transport failure. */ }
  finally { observed.stateChanged = before !== await stateSnapshot(priorContext.stateDir); }
  if (!result) return observed;
  observed.responseKind = result.out.length === 0 ? 'no_response' : 'invalid_response';
  observed.exitCode = result.code;
  try {
    const body = response(result);
    observed.responseOk = body?.ok === true;
    observed.pairingEmitted = typeof body?.pairingString === 'string';
    observed.code = typeof body?.code === 'string' && /^[a-z_]+$/.test(body.code) ? body.code : null;
    observed.explicitlyRefused = body?.v === 1 && body?.ok === false && observed.code !== null && Object.keys(body).sort().join(',') === 'code,ok,v';
    observed.responseKind = observed.explicitlyRefused ? 'typed_refusal' : observed.responseOk ? 'success' : 'invalid_response';
  } catch { /* Only classifications/booleans, never raw responses, enter the receipt. */ }
  return observed;
}

try {
  assert.equal(process.platform, 'win32', 'native_Windows_required');
  assert.equal(process.env.GITHUB_ACTIONS, 'true', 'disposable_GitHub_runner_required');
  assert.equal(process.env.RUNNER_ENVIRONMENT, 'github-hosted', 'self_hosted_runner_refused');
  assert.ok(artifact && core && receiptFile && process.env.COMPOSED_PRIVATE_ROOT, 'explicit_private_artifact_core_receipt_required');
  assert.equal(execFileSync('git', ['-C', core, 'rev-parse', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim(), consumerSha, 'consumer_revision');
  const platform = await fs.readFile(path.join(core, 'extensions/browser/src/browser/extension-windows-platform.ts'), 'utf8');
  assert.ok(platform.includes('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'), 'pinned_TrustedInstaller_ancestor_rule');
  // Refuse all existing product registration/generations. Do not adopt or erase them.
  const known = JSON.parse(powershell(
    "$ErrorActionPreference='Stop'; $local=[Environment]::GetFolderPath('LocalApplicationData'); " +
    "$paths=@('Software\\Google\\Chrome\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap','Software\\Chromium\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap','Software\\Google\\Chrome\\Extensions\\kcdjddhmeafeomebliikmbpblkmkfoig'); " +
    "foreach($h in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)){foreach($v in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)){$r=[Microsoft.Win32.RegistryKey]::OpenBaseKey($h,$v);try{foreach($p in $paths){$k=$r.OpenSubKey($p);if($null-ne $k){$k.Dispose();throw 'Existing product registration'}}}finally{$r.Dispose()}}}; " +
    "$root=Join-Path $local 'OpenClawTray\\browser-native\\generations';if(Test-Path -LiteralPath $root){throw 'Existing product generations'}; " +
    "[Console]::Out.Write((ConvertTo-Json -Compress @{local=$local;sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}))"));
  assert.match(producerSha ?? '', /^[0-9a-f]{40}$/, 'producer_revision_required');
  const producerSource = JSON.parse(await fs.readFile(path.join(artifact,'producer-source.json'),'utf8'));
  assert.equal(producerSource.producerSha,producerSha,'new_producer_source_mismatch');
  assert.equal(execFileSync('git',['-C',fileURLToPath(new URL('../',import.meta.url)),'rev-parse','HEAD'],{encoding:'utf8'}).trim(),producerSha,'producer_checkout_mismatch');
  stage = 'private-fixture';
  // Windows TEMP may contain an 8.3 alias. The production contract requires canonical paths.
  const fixture = await fs.realpath(await fs.mkdtemp(path.join(process.env.COMPOSED_PRIVATE_ROOT, 'fixture-')));
  const encoded = Buffer.from(fixture).toString('base64');
  powershell("$ErrorActionPreference='Stop';$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'));$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($sid);foreach($s in @($sid.Value,'S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($s),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))};[IO.Directory]::SetAccessControl($p,$acl)");
  executable = path.join(fixture, 'OpenClaw.BrowserBootstrap.exe');
  const source = path.join(artifact, 'tools', 'browser-bootstrap', 'OpenClaw.BrowserBootstrap.exe');
  await fs.copyFile(source, executable, fs.constants.COPYFILE_EXCL);
  const hash = async (file) => crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex');
  receipt.producerExecutableSha256 = await hash(source);
  assert.equal(receipt.producerExecutableSha256,producerSource.executableSha256,'new_producer_image_mismatch');
  assert.equal(await hash(executable), receipt.producerExecutableSha256, 'producer_copy_hash');
  const state = path.join(fixture, 'state'); await fs.mkdir(state);
  const configPath = path.join(state, 'openclaw.json');
  await fs.writeFile(configPath, JSON.stringify(fixtureConfig), { flag: 'wx' });
  context = { nodePath: await fs.realpath(process.execPath), cliPath: await fs.realpath(path.join(core, 'openclaw.mjs')), stateDir: await fs.realpath(state), configPath: await fs.realpath(configPath), browserProfile: 'chrome' };
  // Read-only diagnostics distinguish fixture aliases/reparse ancestors from product failures.
  receipt.pathChecks = {};
  for (const [role, target] of Object.entries({ producer: executable, node: context.nodePath, cli: context.cliPath, state: context.stateDir, config: context.configPath })) {
    const chain = [];
    for (let cursor = target; ; cursor = path.dirname(cursor)) { chain.push(cursor); if (path.dirname(cursor) === cursor) break; }
    const checks = await Promise.all(chain.map(async (entry) => ({ canonical: (await fs.realpath(entry)).toLowerCase() === entry.toLowerCase(), symbolic: (await fs.lstat(entry)).isSymbolicLink() })));
    receipt.pathChecks[role] = { canonical: checks.every((entry) => entry.canonical), symbolicAncestors: checks.some((entry) => entry.symbolic) };
  }
  assert.ok(Object.values(receipt.pathChecks).every((entry) => entry.canonical && !entry.symbolicAncestors), 'fixture_path_admission');
  stage = 'win32-path-audit';
  receipt.admission = auditPaths([
    {role:'producer',target:executable}, {role:'node',target:context.nodePath}, {role:'cli',target:context.cliPath},
    {role:'state',target:context.stateDir,privacy:true,directory:true}, {role:'config',target:context.configPath,privacy:true},
    {role:'generation-root',target:path.join(known.local,'OpenClawTray','browser-native','generations'),privacy:true,directory:true,allowMissing:true},
  ]);
  assert.ok(receipt.admission.every((entry) => entry.accepted), 'win32_admission_failed');
  stage = 'canonical-config-validation';
  const configValidationScript = 'const {readConfigFileSnapshot}=await import("openclaw/plugin-sdk/health");const s=await readConfigFileSnapshot({observe:false,pluginValidation:"core-only"});process.stdout.write(JSON.stringify({valid:s.valid}));';
  receipt.configValidation = JSON.parse(execFileSync(context.nodePath, ['--input-type=module', '-e', configValidationScript], { cwd: core, env: { ...baseEnv, OPENCLAW_STATE_DIR: context.stateDir, OPENCLAW_CONFIG_PATH: context.configPath }, encoding: 'utf8', timeout: 30000, maxBuffer: 32768, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }));
  assert.equal(receipt.configValidation.valid, true, 'fixture_config_invalid');
  check('canonical_read_only_config_validation');
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
  receipt.bootstrapResponse = { versionValid: paired.v === 1, ok: paired.ok === true, nonceMatches: paired.nonce === nonce, pairingPresent: typeof paired.pairingString === 'string', code: ['manifest_invalid','pairing_unavailable','manual_required','invalid_request','origin_forbidden','invalid_frame','relay_unavailable'].includes(paired.code) ? paired.code : null };
  assert.equal(paired.v, 1, 'native_response_version');
  assert.equal(paired.ok, true, 'canonical_pairing_failed'); assert.equal(paired.nonce, nonce);
  stage = 'pairing-response-validation';
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
  const env = { ...baseEnv, OPENCLAW_STATE_DIR: context.stateDir, OPENCLAW_CONFIG_PATH: context.configPath, OPENCLAW_NO_RESPAWN: '1' };
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
  // Investigate registry lifecycle authority without modifying generation files or adding a bypass flag.
  stage = 'revoked-generation-investigation';
  const removedOriginal = await manage(request('uninstall')); assert.equal(removedOriginal.ok,true); assert.equal(removedOriginal.registration,'missing');
  context = await isolatedContext(fixture,'revoked-state');
  const revokedFresh = await stateSnapshot(context.stateDir);
  const revokeInstall = await manage(request('install')); assert.equal(revokeInstall.ok,true);
  receipt.revocationPrepublicationStateChanged = revokedFresh !== await stateSnapshot(context.stateDir);
  const removedRevoked = await manage(request('uninstall')); assert.equal(removedRevoked.ok,true); assert.equal(removedRevoked.registration,'missing');
  receipt.revokedGeneration = await observePriorGeneration(revokeInstall.installation,context,packet);
  const revokedPostState = await manage(request('inspect'));
  receipt.revokedGeneration.registrationRemainsMissing = revokedPostState.ok && revokedPostState.registration === 'missing';
  assert.ok(receipt.revokedGeneration.registrationRemainsMissing, 'revoked_registration_reappeared');
  stage = 'reassigned-generation-investigation';
  context = await isolatedContext(fixture,'previous-state');
  const previousContext = context;
  const previous = await manage(request('install')); assert.equal(previous.ok,true);
  const removedPrevious = await manage(request('uninstall')); assert.equal(removedPrevious.ok,true); assert.equal(removedPrevious.registration,'missing');
  context = await isolatedContext(fixture,'replacement-state');
  const replacement = await manage(request('install')); assert.equal(replacement.ok,true);
  assert.notEqual(replacement.installation.generation,previous.installation.generation);
  const replacementBefore = await stateSnapshot(context.stateDir);
  receipt.reassignedGeneration = await observePriorGeneration(previous.installation,previousContext,packet);
  receipt.reassignedGeneration.replacementStateChanged = replacementBefore !== await stateSnapshot(context.stateDir);
  const current = await manage(request('inspect'));
  assert.equal(current.ok,true); assert.equal(current.installation.generation,replacement.installation.generation);
  receipt.reassignedGeneration.activeRegistrationPreserved = true;
  // A positive prior-launcher result is evidence requiring agreed-contract review, not a new revocation policy.
  const rejectedWithoutEffects = (entry) => entry.explicitlyRefused && !entry.pairingEmitted && !entry.stateChanged;
  if (receipt.revocationPrepublicationStateChanged || !rejectedWithoutEffects(receipt.revokedGeneration) || !rejectedWithoutEffects(receipt.reassignedGeneration) || receipt.reassignedGeneration.replacementStateChanged) {
    receipt.status = 'authority_review_required'; process.exitCode = 1;
  } else {
    check('revoked_and_reassigned_generations_rejected_without_state_effects');
    stage='real-inflight-retirement';
    await withFrozenNative(replacement.installation,packet,fixture,async control=>{
      const [inspection,retirement]=await Promise.all([manage(request('inspect')),manage(request('uninstall'))]);
      for(const value of [inspection,retirement]) {
        refused(value,'busy');
        assert.equal(value.registration,null);assert.equal(value.installation,null);
      }
      receipt.inflight={inspectionBusy:true,retirementBusy:true};
      // The child is proved live and paused. Try retirement concurrently with release;
      // a bounded busy result is explicitly not counted as completed retirement.
      const pendingRetirement=recordCompletion(manage(request('uninstall')));
      await control.resume();
      const result=await control.work;
      assert.equal(response(result).ok,true,'admitted_operation_not_settled');
      let completion=await pendingRetirement;
      let removed=completion.value;
      if(!removed.ok) {
        refused(removed,'busy');
        const stillActive=await manage(request('inspect'));assert.equal(stillActive.installation.generation,replacement.installation.generation);
        completion=await recordCompletion(manage(request('uninstall')));removed=completion.value;
      }
      const retirementComplete=completion.completedAt;
      assert.equal(removed.ok,true);assert.equal(removed.registration,'missing');
      assert.ok(result.firstFrameAt!==null && result.firstFrameAt<=retirementComplete,'credential_after_completed_retirement');
      receipt.inflight.responseBeforeCompletedRetirement=true;
    });
    check('real_inflight_owner_serializes_inspection_retirement_and_response');
    stage='real-inflight-eof';
    context=await isolatedContext(fixture,'inflight-eof-state');
    const eofBefore=await stateSnapshot(context.stateDir);
    const eofInstalled=await manage(request('install'));assert.equal(eofInstalled.ok,true);
    assert.equal(await stateSnapshot(context.stateDir),eofBefore,'prepublication_state_effect');
    await withFrozenNative(eofInstalled.installation,packet,fixture,async control=>{
      assert.equal(await stateSnapshot(context.stateDir),eofBefore,'eof_fixture_already_effectful');
      control.disconnect();
      const cancelled=await control.work;
      assert.equal(cancelled.code,0);assert.equal(cancelled.out.length,0);assert.equal(cancelled.errBytes,0);
    });
    assert.equal(await stateSnapshot(context.stateDir),eofBefore,'state_effect_after_inflight_eof');
    const eofRemoved=await manage(request('uninstall'));assert.equal(eofRemoved.ok,true);assert.equal(eofRemoved.registration,'missing');
    check('real_inflight_eof_joins_owned_node_without_pairing_or_state_effect');
    stage='retired-parser-variants';
    for(const raw of [JSON.stringify({v:1,op:'bootstrap',nonce,extra:true}),JSON.stringify({v:1,op:'bootstrap',nonce}).replace(':1,',':1.0,'),JSON.stringify({v:1,op:'bootstrap',nonce}).replace(':1,',':1e0,')]) {
      const payload=Buffer.from(raw),header=Buffer.alloc(4);header.writeUInt32LE(payload.length);
      refused(response(await exchange(eofInstalled.installation.launcherPath,[origin],Buffer.concat([header,payload]))),'manifest_invalid');
    }
    assert.equal(await stateSnapshot(context.stateDir),eofBefore,'retired_variant_state_effect');
    check('retired_valid_nonce_parser_variants_cannot_bypass_activation');
    receipt.status='passed';
  }
} catch (error) {
  // Never serialize native stdout, pairing strings, config contents or a child diagnostic.
  receipt.failureStage = stage;
  const location = error instanceof Error ? error.stack?.match(/Test-BrowserNativeComposition\.mjs:(\d+):(\d+)/) : undefined;
  if (location) receipt.failureLocation = { line: Number(location[1]), column: Number(location[2]) };
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
