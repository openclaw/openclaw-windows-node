// Proof-only browser mutation inventory. Generic CLI database/log/cache churn is not browser state.
import fs from 'node:fs/promises';
import crypto from 'node:crypto';
import path from 'node:path';

export const browserStateScope='config-credentials-browser';
const maxEntries=256,maxBytes=1048576,chunkBytes=65536;
const fail=code=>{const error=new Error(code);error.code=code;throw error;};
const same=(a,b)=>a.dev===b.dev&&a.ino===b.ino&&a.size===b.size&&a.mtimeMs===b.mtimeMs&&a.ctimeMs===b.ctimeMs;

export async function snapshotBrowserState(root) {
  const entries=[];let bytes=0;
  const add=value=>{if(entries.length>=maxEntries)fail('snapshot_entry_bound');entries.push(value);};
  async function visit(relative,missingAllowed=false) {
    const file=path.join(root,relative);let before;
    try {before=await fs.lstat(file);} catch(error) {
      if(missingAllowed&&error.code==='ENOENT'){add([relative,'missing']);return;}
      throw error;
    }
    if(before.isSymbolicLink())fail('snapshot_link');
    if(before.isDirectory()) {
      add([relative,'directory']);
      const names=[];
      for await(const entry of await fs.opendir(file)) {
        if(names.length>=maxEntries)fail('snapshot_entry_bound');
        names.push(entry.name);
      }
      for(const name of names.sort())await visit(path.join(relative,name));
      const after=await fs.lstat(file);
      if(after.isSymbolicLink()||!after.isDirectory()||!same(before,after))fail('snapshot_changed');
      return;
    }
    if(!before.isFile()||before.nlink!==1)fail('snapshot_file_type');
    if(before.size>maxBytes-bytes)fail('snapshot_byte_bound');
    const handle=await fs.open(file,'r');
    try {
      if(!same(before,await handle.stat()))fail('snapshot_changed');
      const hash=crypto.createHash('sha256'),buffer=Buffer.alloc(chunkBytes);let count=0;
      for(;;) {
        const read=await handle.read(buffer,0,buffer.length,null);
        if(read.bytesRead===0)break;
        count+=read.bytesRead;bytes+=read.bytesRead;
        if(bytes>maxBytes)fail('snapshot_byte_bound');
        hash.update(buffer.subarray(0,read.bytesRead));
      }
      const after=await fs.lstat(file);
      if(after.isSymbolicLink()||!same(before,after)||!same(before,await handle.stat())||count!==before.size)fail('snapshot_changed');
      add([relative,hash.digest('hex')]);
    } finally {await handle.close();}
  }
  const rootStat=await fs.lstat(root);
  if(rootStat.isSymbolicLink()||!rootStat.isDirectory())fail('snapshot_link');
  // The immutable consumer persists relay material in credentials/, installs assets in
  // browser/, and consumes this exact config plus its atomic-write/backup siblings.
  // Keep absence in the baseline so first-time creation is detected, not silently ignored.
  const configs=new Set(['openclaw.json']);let scanned=0;
  for await(const entry of await fs.opendir(root)) {
    if(++scanned>4096)fail('snapshot_root_inventory_bound');
    if(entry.name.startsWith('openclaw.json.')||entry.name.startsWith('.openclaw.json.'))configs.add(entry.name);
  }
  for(const config of [...configs].sort())await visit(config,true);
  await visit('credentials',true);await visit('browser',true);
  return {value:JSON.stringify(entries),entries:entries.length,bytes,scope:browserStateScope};
}

// Explain the old whole-tree oracle safely: metadata only, no file names or contents
// in the result. A truncated diagnostic is not a successful mutation snapshot.
export async function describeLegacySnapshotBounds(root) {
  let entries=0,maxFileBytes=0,fileBytesExceeded=false,truncated=false;
  const roles=new Set();
  const role=relative=>relative==='openclaw.json'||relative.startsWith('openclaw.json.')||relative.startsWith('.openclaw.json.')?'config':
    relative.startsWith('credentials/')?'credentials':relative.startsWith('browser/')?'browser':
    ['state/openclaw.sqlite','state/openclaw.sqlite-wal','state/openclaw.sqlite-shm'].includes(relative)?'runtime_database':
    relative.startsWith('logs/')?'logs':'other';
  async function walk(dir) {
    for await(const entry of await fs.opendir(dir)) {
      if(entries>=1024){truncated=true;return;}
      entries++;
      const file=path.join(dir,entry.name),stat=await fs.lstat(file);
      if(stat.isSymbolicLink())continue;
      if(stat.isDirectory())await walk(file);
      else if(stat.isFile()) {
        maxFileBytes=Math.max(maxFileBytes,stat.size);
        if(stat.size>maxBytes){fileBytesExceeded=true;roles.add(role(path.relative(root,file).split(path.sep).join('/')));}
      }
      if(truncated)return;
    }
  }
  await walk(root);
  return {entries,maxFileBytes,fileBytesExceeded,entryCountExceeded:entries>maxEntries,truncated,oversizedRoles:[...roles].sort()};
}
