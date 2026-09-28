import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import { savedProfileFailure, savedProfileCliObservation, assertSavedProfileGenerationUnchanged, assertMatchedForeignStore } from './Test-BrowserNativeSavedProfile.mjs';

test('native proof preflight and both final-consumer workflows use one immutable pin',()=>{
  const source=fs.readFileSync(new URL('./Test-BrowserNativeComposition.mjs',import.meta.url),'utf8');
  const initializer=fs.readFileSync(new URL('./Initialize-BrowserNativeComposition.ps1',import.meta.url),'utf8');
  const native=fs.readFileSync(new URL('../.github/workflows/browser-native-composed-proof.yml',import.meta.url),'utf8');
  const wsl=fs.readFileSync(new URL('../.github/workflows/browser-wsl-owner-proof.yml',import.meta.url),'utf8');
  const pin=source.match(/const consumerSha = '([a-f0-9]{40})'/)[1];
  assert.equal(initializer.match(/consumerSha='([a-f0-9]{40})'/)[1],pin);
  for(const workflow of [native,wsl]) {
    assert.equal(workflow.match(/ref: ([a-f0-9]{40})/)[1],pin);
    assert.ok(workflow.includes(pin));
  }
  const version=native.match(/node-version: '([^']+)'/)[1];
  assert.ok(initializer.includes("$result.nodeVersion -cne 'v"+version+"'"));
});
test('native proof selects packageManager from the nested consumer checkout',()=>{
  const native=fs.readFileSync(new URL('../.github/workflows/browser-native-composed-proof.yml',import.meta.url),'utf8');
  const setup=native.split('- uses: pnpm/action-setup@v4')[1].split('- uses:')[0];
  assert.match(setup,/package_json_file: consumer\/package\.json/);
  assert.doesNotMatch(setup,/^\s+version:/m);
});
test('every saved-profile CLI wait respects the canonical 1000-120000 ms range',()=>{
  const source=fs.readFileSync(new URL('./Test-BrowserNativeSavedProfile.mjs',import.meta.url),'utf8');
  const waits=[...source.matchAll(/'--wait-ms','([0-9]+)'/g)].map(match=>Number(match[1]));
  assert.ok(waits.length>0);
  assert.equal(waits.length,[...source.matchAll(/'--wait-ms'/g)].length);
  assert.ok(waits.every(wait=>wait>=1000&&wait<=120000));
});
test('idempotent setup keeps the admitted generation and creates no directory',()=>{
  assert.doesNotThrow(()=>assertSavedProfileGenerationUnchanged('generation-a','generation-a',[]));
  assert.throws(()=>assertSavedProfileGenerationUnchanged('generation-a','generation-b',[]));
  assert.throws(()=>assertSavedProfileGenerationUnchanged('generation-a','generation-a',['new-generation']));
});
test('foreign Store proof requires a matching live native descriptor and typed refusal',()=>{
  const matching={ok:false,code:'foreign_registration',registration:'owned',mode:'native-windows-cli',store:'foreign',installation:{generation:'expected'}};
  assert.doesNotThrow(()=>assertMatchedForeignStore(matching,'expected'));
  for(const patch of [{ok:true},{code:'context_conflict'},{registration:'foreign'},{mode:'companion-managed-wsl'},{store:'requested'},{installation:null},{installation:{generation:'other'}}]) {
    assert.throws(()=>assertMatchedForeignStore({...matching,...patch},'expected'));
  }
  assert.throws(()=>assertMatchedForeignStore({...matching,installation:null},undefined));
});
test('snapshot failures retain only an allowlisted bound code',()=>{
  const error=new Error('private-value');error.code='snapshot_byte_bound';
  assert.deepEqual(savedProfileFailure(error),{kind:'execution_failed',snapshotCode:'snapshot_byte_bound'});
  error.code='private-value';assert.deepEqual(savedProfileFailure(error),{kind:'execution_failed'});
});
test('snapshot helper and regressions remain in the frozen native proof graph',()=>{
  for(const file of ['BrowserNativeStateSnapshot.mjs','BrowserNativeStateSnapshot.test.mjs']) {
    const native=fs.readFileSync(new URL('../.github/workflows/browser-native-composed-proof.yml',import.meta.url),'utf8');
    const wsl=fs.readFileSync(new URL('../.github/workflows/browser-wsl-owner-proof.yml',import.meta.url),'utf8');
    assert.ok(native.includes('scripts/'+file));assert.ok(wsl.includes('scripts/'+file));
  }
});
test('CLI projection diagnostics reject missing registrations without masking the boundary',()=>{
  for(const body of [undefined,null,{},[],{error:'private-value'},{target:{kind:'local-host'}}]) {
    const observed=savedProfileCliObservation({code:1,body});
    assert.equal(observed.ownedWorkProfile,false);assert.equal(observed.registrationsPresent,false);
    assert.ok(!JSON.stringify(observed).includes('private-value'));
  }
});
test('CLI observation retains owned work selection and never raw registration fields',()=>{
  const observed=savedProfileCliObservation({code:1,body:{registrations:[{state:'owned',browserProfile:'work',path:'private-path'}],manualSetupRequired:true}});
  assert.equal(observed.ownedWorkProfile,true);assert.equal(observed.registrationCount,1);
  assert.equal(observed.manualSetupRequired,true);assert.ok(!JSON.stringify(observed).includes('private-path'));
});
test('saved-profile diagnostics retain only assertion kind and fixture coordinates',()=>{
  const error=new assert.AssertionError({actual:'private-pairing',expected:'private-config',message:'private-message'});
  error.stack='AssertionError: private-message\n at savedProfileAcceptance (file:///private/root/Test-BrowserNativeSavedProfile.mjs:51:12)';
  assert.deepEqual(savedProfileFailure(error),{kind:'assertion_failed',line:51,column:12});
});
test('execution diagnostics never retain messages, child output or foreign stack paths',()=>{
  const error=Object.assign(new Error('private-message'),{stdout:'private-output',stderr:'private-error',code:'private-code'});
  error.stack='Error: private-message\n at file:///private/other.mjs:9:2';
  assert.deepEqual(savedProfileFailure(error),{kind:'execution_failed'});
});
test('numeric coordinates support Windows stack paths without publishing paths',()=>{
  const error=new Error('private-message');
  error.stack='Error: private-message\n at savedProfileAcceptance (D:\\private\\Test-BrowserNativeSavedProfile.mjs:173:5)';
  assert.deepEqual(savedProfileFailure(error),{kind:'execution_failed',line:173,column:5});
});
test('non-Error throws cannot add diagnostic fields',()=>{
  assert.deepEqual(savedProfileFailure({message:'private-message',stack:'private-stack',actual:'private-output'}),{kind:'execution_failed'});
  assert.deepEqual(savedProfileFailure(null),{kind:'execution_failed'});
});
