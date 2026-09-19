'use strict';
// TEST-ONLY prototype. Never selected by the production pairing owner.
const fs = require('node:fs');
const crypto = require('node:crypto');
const { spawn, execFileSync } = require('node:child_process');
const { TextDecoder } = require('node:util');
const CONTROL = 2048, RESULT = 90000, CLI_BYTES = 65536, CLI_CHARS = 16384;
const decoder = new TextDecoder('utf-8', { fatal: true });
const keys = {
  permit: ['v','type','requestId','invocationId','challenge'],
  cancel: ['v','type','requestId','invocationId'],
  ready: ['v','type','requestId','invocationId','challenge','unit','bootId','pid','start','cgroup','environmentClean'],
  result: ['v','type','requestId','invocationId','payload'],
  settled: ['v','type','requestId','invocationId','outcome','startSealed','gateExited','cgroupEmpty','startJobSettled']
};
function parse(bytes) {
  if(bytes.length>=3&&bytes[0]===0xef&&bytes[1]===0xbb&&bytes[2]===0xbf)throw Error("protocol_bom");
  const text = decoder.decode(bytes), value = JSON.parse(text);
  if (!value || value.v !== 1 || !keys[value.type] || JSON.stringify(value) !== text ||
      Object.keys(value).sort().join() !== [...keys[value.type]].sort().join() ||
      !/^[a-f0-9]{32}$/.test(value.requestId) || !/^[a-f0-9]{32}$/.test(value.invocationId)) throw Error('protocol_schema');
  if (value.type === 'permit' && !/^[a-f0-9]{32}$/.test(value.challenge)) throw Error('protocol_challenge');
  return value;
}
function frame(value) {
  const data = Buffer.from(JSON.stringify(value));
  if (data.length > (value.type === 'result' ? RESULT : CONTROL)) throw Error('protocol_bound');
  const h = Buffer.alloc(4); h.writeUInt32LE(data.length); return Buffer.concat([h,data]);
}
class Reader {
  constructor(stream, onFrame, onGone) {
    let data = Buffer.alloc(0), count = 0, failed = false;
    const fail = () => { if (!failed) { failed = true; onGone('invalid'); } };
    stream.on('data', part => {
      if (failed) return;
      try {
        if (data.length + part.length > RESULT + 4) throw Error('protocol_bound');
        data = Buffer.concat([data,part]);
        while (data.length >= 4) {
          const size = data.readUInt32LE();
          if (!size || size > RESULT) throw Error('protocol_bound');
          if (data.length < size + 4) break;
          const message = parse(data.subarray(4, size + 4));
          if (size > (message.type === 'result' ? RESULT : CONTROL) || ++count > 4) throw Error('protocol_bound');
          data = data.subarray(size + 4); onFrame(message);
        }
      } catch { fail(); }
    });
    stream.on('end', () => { if (!failed) { failed = true; onGone(data.length ? 'partial' : 'eof'); } });
    stream.on('error', fail);
  }
}
function identity(pid) {
  try { const a=fs.readFileSync('/proc/'+pid+'/stat','utf8').split(') ').at(-1).split(' '); return {pid,start:Number(a[19]),state:a[0]}; }
  catch (e) { if(e.code==='ENOENT')return null; throw e; }
}
function show(unit) {
  const text=execFileSync('/usr/bin/systemctl',['--user','show',unit,'-p','Id','-p','LoadState','-p','ActiveState','-p','SubState','-p','InvocationID','-p','ControlGroup','-p','MainPID','-p','Description'],{encoding:'utf8',timeout:2000,maxBuffer:8192,stdio:['ignore','pipe','pipe']});
  return Object.fromEntries(text.trim().split('\n').map(l=>{const p=l.indexOf('=');return [l.slice(0,p),l.slice(p+1)];}));
}
function empty(cgroup) {
  if (!cgroup.startsWith('/user.slice/') || cgroup.includes('..')) throw Error('cgroup_binding');
  try { return /^populated 0$/m.test(fs.readFileSync('/sys/fs/cgroup'+cgroup+'/cgroup.events','utf8')); }
  catch(e){if(e.code==='ENOENT')return true;throw e;}
}
function emit(value) { process.stdout.write(frame(value)); }
function binding(value, ready) {
  if(value.requestId!==ready.requestId || value.invocationId!==ready.invocationId)throw Error('protocol_binding');
}
function cliPayload(bytes) {
  if(bytes.length>CLI_BYTES)throw Error('cli_bound');
  const text=decoder.decode(bytes);
  if(text.length>CLI_CHARS)throw Error('cli_bound');
  return bytes.toString('base64');
}
async function gate(s) {
  const mine=identity(process.pid), unit=show(s.unit);
  const cgroup=fs.readFileSync('/proc/self/cgroup','utf8').trim().split('\n').find(l=>l.startsWith('0::'))?.slice(3);
  if(unit.Id!==s.unit || unit.Description!==s.description || Number(unit.MainPID)!==process.pid || unit.ControlGroup!==cgroup || !/^[a-f0-9]{32}$/.test(unit.InvocationID))throw Error('gate_binding');
  const clean=process.env.HOME==='/home/openclaw' && process.env.OPENCLAW_STATE_DIR==='/home/openclaw/.openclaw' && process.env.OPENCLAW_CONFIG_PATH==='/home/openclaw/.openclaw/openclaw.json' &&
    ['OPENCLAW_PROFILE','OPENCLAW_HOME','NODE_OPTIONS','NODE_PATH'].every(k=>!(k in process.env));
  if(!clean)throw Error('gate_environment');
  const ready={v:1,type:'ready',requestId:s.requestId,invocationId:unit.InvocationID,challenge:crypto.randomBytes(16).toString('hex'),unit:s.unit,
    bootId:fs.readFileSync('/proc/sys/kernel/random/boot_id','utf8').trim(),pid:mine.pid,start:mine.start,cgroup,environmentClean:clean};
  let phase='waiting', child, stopped=false;
  const revoke=()=>{
    if(stopped)return;stopped=true;phase='sealed';
    // A gate cannot wait for its own stop job. Queue only its positively bound unit.
    try { const current=show(s.unit);if(current.InvocationID!==ready.invocationId)throw Error('epoch');
      execFileSync('/usr/bin/systemctl',['--user','stop','--no-block',s.unit],{timeout:2000,stdio:'ignore'});
    } catch { process.exitCode=42; }
  };
  new Reader(process.stdin, value=>{
    binding(value,ready);
    if(value.type==='cancel'){revoke();return;}
    if(value.type!=='permit'||phase!=='waiting'||value.challenge!==ready.challenge)throw Error('permit_state');
    phase='running';
    child=spawn(s.command[0],s.command.slice(1),{stdio:['ignore','pipe','pipe'],env:process.env});
    let out=Buffer.alloc(0),errorBytes=0,failed=false;
    child.stdout.on('data',part=>{if(out.length+part.length>CLI_BYTES){failed=true;revoke();}else out=Buffer.concat([out,part]);});
    child.stderr.on('data',part=>{errorBytes+=part.length;if(errorBytes>CLI_BYTES){failed=true;revoke();}});
    child.on('error',revoke);
    child.on('close',code=>{
      if(stopped)return;
      phase='sealed';
      if(code!==0||failed){revoke();return;}
      try { emit({v:1,type:'result',requestId:s.requestId,invocationId:ready.invocationId,payload:cliPayload(out)}); }
      catch { revoke();return; }
      process.stdin.destroy();process.stdout.end(()=>process.exit(0));
    });
  },revoke);
  emit(ready);
}
async function broker(s, source) {
  if(!/^[a-f0-9]{32}$/.test(s.requestId))throw Error('request_id');
  // Single writer: each request uses a fresh name exactly once; no participant/recovery restarts it.
  // Show/StopUnit is not an atomic invocation-conditional API. An actor controlling this same
  // user-manager and trusted CLI/config could replace any unit between commands; that actor is
  // outside the existing trusted-UID model. Refuse observed drift, never claim an atomic fence.
  const unit='openclaw-browser-prototype-'+s.requestId+'.service';
  const description='OpenClaw browser prototype '+s.requestId;
  const uid=process.getuid(),runtime='/run/user/'+uid;
  const state={...s,mode:'gate',unit,description};
  const program='const settings='+JSON.stringify(state)+';const prototypeSource='+JSON.stringify(source)+';'+Buffer.from(source,'base64').toString();
  if(Buffer.byteLength(program)>100000)throw Error('inline_bound');
  const args=['--user','--quiet','--pipe','--service-type=exec','--unit='+unit,'--property=Description='+description,
    '--property=ExitType=cgroup','--property=KillMode=control-group','--property=Restart=no','--property=RemainAfterExit=yes','--property=TimeoutStopSec=2s',
    '/usr/bin/env','-i','HOME=/home/openclaw','PATH=/home/openclaw/.openclaw/tools/node/bin:/usr/local/bin:/usr/bin:/bin',
    'OPENCLAW_STATE_DIR=/home/openclaw/.openclaw','OPENCLAW_CONFIG_PATH=/home/openclaw/.openclaw/openclaw.json',
    'XDG_RUNTIME_DIR='+runtime,'DBUS_SESSION_BUS_ADDRESS=unix:path='+runtime+'/bus',s.node,'-e',program];
  let ready, result, cancelled=false, permit=false, runnerClosed=false, runnerCode, finished=false, stopping, stopFailed=false;
  if(s.startDelayMs)args.splice(args.indexOf("/usr/bin/env"),0,"--property=ExecStartPre=/bin/sleep 1");
  const runner=spawn('/usr/bin/systemd-run',args,{stdio:['pipe','pipe','pipe']});
  runner.stdin.on('error',()=>{});
  let stderr=0;
  runner.stderr.on('data',p=>{stderr+=p.length;if(stderr>8192)cancelled=true;});
  const closed=new Promise(resolve=>{runner.once('error',()=>{runnerClosed=true;runnerCode=-1;resolve();});runner.once('close',code=>{runnerClosed=true;runnerCode=code;resolve();});});
  const stop=()=>{
    cancelled=true;
    if(ready && !stopping) {
      stopping=(async()=>{
        if(s.stopDelayMs)await new Promise(resolve=>setTimeout(resolve,s.stopDelayMs));
        if(fs.readFileSync("/proc/sys/kernel/random/boot_id","utf8").trim()!==ready.bootId)throw Error("stop_epoch");
        const now=show(unit);
        if(now.LoadState==='not-found')return; // NOT a settlement decision; bound cgroup and runner must also finish.
        if(now.InvocationID!==ready.invocationId||now.Description!==description)throw Error('stop_binding');
        await new Promise((resolve,reject)=>{
          const p=spawn('/usr/bin/systemctl',['--user','stop',unit],{stdio:'ignore'});
          p.on('error',reject);p.on('close',code=>code===0?resolve():reject(Error('stop_failed')));
        });
      })();
      stopping.catch(()=>{stopFailed=true;});
    }
  };
  new Reader(process.stdin,value=>{
    if(!ready)throw Error('before_ready');binding(value,ready);
    if(value.type==='cancel'){stop();return;}
    if(value.type!=='permit'||permit||cancelled||value.challenge!==ready.challenge)throw Error('permit_state');
    permit=true;runner.stdin.write(frame(value));
  },stop);
  new Reader(runner.stdout,value=>{
    if(value.type==='ready') {
      if(ready||value.requestId!==s.requestId||value.unit!==unit||!value.environmentClean)throw Error('ready_state');
      const now=show(unit), actual=identity(value.pid);
      if(now.InvocationID!==value.invocationId||now.ControlGroup!==value.cgroup||now.Description!==description||Number(now.MainPID)!==value.pid||actual?.start!==value.start)throw Error('ready_binding');
      ready=value;
      if(cancelled)stop();
      else if(s.readyDelayMs)setTimeout(()=>{if(!cancelled)emit(ready);},s.readyDelayMs);
      else emit(ready);
      return;
    }
    if(!ready)throw Error('before_ready');binding(value,ready);
    if(value.type!=='result'||result||!permit||cancelled)throw Error('result_state');
    const payload=Buffer.from(value.payload,'base64');if(payload.toString('base64')!==value.payload)throw Error('result_encoding');cliPayload(payload);
    result=value; // Only the broker can forward RESULT, and it still cannot imply SETTLED.
  },()=>{if(!result)stop();});
  let checking=false;
  const interval=setInterval(async()=>{
    if(finished||checking)return;
    checking=true;
    try {
      if(!ready){if(runnerClosed){finished=true;clearInterval(interval);process.exit(43);}return;}
      if(stopFailed)throw Error("stop_refused");
      if(!result&&!cancelled)return;
      if(!stopping){
        // Seal successful work too: detached descendants may outlive a successful CLI.
        // Stop only the matched unit, THEN wait for its cgroup to empty.
        if(fs.readFileSync("/proc/sys/kernel/random/boot_id","utf8").trim()!==ready.bootId)throw Error("stop_epoch");
        const current=show(unit);
        if(current.LoadState!=="not-found" && (current.InvocationID!==ready.invocationId||current.Description!==description))throw Error("stop_binding");
        stopping=(async()=>{await new Promise((resolve,reject)=>{
          const p=spawn('/usr/bin/systemctl',['--user','stop',unit],{stdio:'ignore'});
          p.on('error',reject);p.on('close',code=>code===0?resolve():reject(Error('stop_failed')));
        });})();stopping.catch(()=>{stopFailed=true;});
      }
      if(!empty(ready.cgroup))return;
      await stopping;await closed;
      if(!empty(ready.cgroup)||!runnerClosed)throw Error('not_settled');
      finished=true;clearInterval(interval);
      if(result&&!cancelled)emit(result);
      if(s.settledDelayMs)await new Promise(resolve=>setTimeout(resolve,s.settledDelayMs));
      emit({v:1,type:'settled',requestId:s.requestId,invocationId:ready.invocationId,outcome:cancelled?'cancelled':'completed',startSealed:true,gateExited:true,cgroupEmpty:true,startJobSettled:true});
      process.stdin.destroy();process.stdout.end(()=>process.exit(0));
    } catch { finished=true;clearInterval(interval);process.exit(44); }
    finally { checking=false; }
  },20);
}
module.exports={parse,frame,Reader,cliPayload,CONTROL,RESULT};
if(typeof settings!=='undefined') {
  process.stdout.on('error',()=>{process.exitCode=45;});
  (settings.mode==='gate'?gate(settings):broker(settings,prototypeSource)).catch(()=>{process.stderr.write('prototype_failed\n');process.exit(46);});
}
