import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import {snapshotBrowserState,describeLegacySnapshotBounds} from './BrowserNativeStateSnapshot.mjs';

async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'browser-state-proof-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  await fs.writeFile(path.join(root,'openclaw.json'),'{}');return root;
}
test('large generic runtime database explains old byte bound without weakening browser bounds',async t=>{
  const root=await fixture(t);await fs.mkdir(path.join(root,'state'));
  const db=await fs.open(path.join(root,'state','openclaw.sqlite'),'w');await db.truncate(2*1048576);await db.close();
  const old=await describeLegacySnapshotBounds(root);
  assert.equal(old.fileBytesExceeded,true);assert.equal(old.entryCountExceeded,false);
  assert.deepEqual(old.oversizedRoles,['runtime_database']);assert.equal(old.maxFileBytes,2*1048576);
  const before=await snapshotBrowserState(root);
  await fs.mkdir(path.join(root,'logs'));await fs.writeFile(path.join(root,'logs','runtime.log'),'unrelated runtime event');
  assert.equal((await snapshotBrowserState(root)).value,before.value);
  assert.equal(before.scope,'config-credentials-browser');assert.equal(before.bytes,2);
});
test('credential creation, mutation, removal and config backups remain observable',async t=>{
  const root=await fixture(t);const empty=await snapshotBrowserState(root);
  await fs.mkdir(path.join(root,'credentials'));const file=path.join(root,'credentials','browser-extension-relay.secret');
  await fs.writeFile(file,'first');const first=await snapshotBrowserState(root);assert.notEqual(first.value,empty.value);
  await fs.writeFile(file,'second');const second=await snapshotBrowserState(root);assert.notEqual(second.value,first.value);
  await fs.rm(file);assert.notEqual((await snapshotBrowserState(root)).value,second.value);
  const prior=await snapshotBrowserState(root);await fs.writeFile(path.join(root,'openclaw.json.bak'),'{}');
  assert.notEqual((await snapshotBrowserState(root)).value,prior.value);
  const withBackup=await snapshotBrowserState(root);await fs.writeFile(path.join(root,'.openclaw.json.pending'),'{}');
  assert.notEqual((await snapshotBrowserState(root)).value,withBackup.value);
});
test('browser files are streamed and content hashes remain exact',async t=>{
  const root=await fixture(t);await fs.mkdir(path.join(root,'browser'));const bytes=Buffer.alloc(524288,7);
  await fs.writeFile(path.join(root,'browser','asset.bin'),bytes);const before=await snapshotBrowserState(root);
  assert.equal(before.bytes,524290);assert.ok(before.value.includes(crypto.createHash('sha256').update(bytes).digest('hex')));
  bytes[200000]=8;await fs.writeFile(path.join(root,'browser','asset.bin'),bytes);
  assert.notEqual((await snapshotBrowserState(root)).value,before.value);
});
test('intended files still fail closed on byte and entry overflow',async t=>{
  const root=await fixture(t);await fs.mkdir(path.join(root,'credentials'));
  await fs.writeFile(path.join(root,'credentials','oversized'),Buffer.alloc(1048576));
  await assert.rejects(snapshotBrowserState(root),{code:'snapshot_byte_bound'});
  await fs.rm(path.join(root,'credentials','oversized'));
  for(let n=0;n<256;n++)await fs.writeFile(path.join(root,'credentials',String(n)),'');
  await assert.rejects(snapshotBrowserState(root),{code:'snapshot_entry_bound'});
});
test('symlinked authority roots are rejected without reading their target',async t=>{
  const root=await fixture(t),outside=await fixture(t);
  await fs.symlink(outside,path.join(root,'credentials'),process.platform==='win32'?'junction':'dir');
  await assert.rejects(snapshotBrowserState(root),{code:'snapshot_link'});
});
test('legacy count diagnostic does not export filenames or secret content',async t=>{
  const root=await fixture(t);await fs.mkdir(path.join(root,'logs'));
  for(let n=0;n<257;n++)await fs.writeFile(path.join(root,'logs','private-'+n),'private-value');
  const value=await describeLegacySnapshotBounds(root);
  assert.equal(value.entryCountExceeded,true);assert.equal(value.fileBytesExceeded,false);
  assert.equal(JSON.stringify(value).includes('private'),false);
});
