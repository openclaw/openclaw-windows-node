// Disposable hosted Windows acceptance through the real canonical CLI and producer.
// Dependencies below are the existing real Windows proof helpers, not mocked adapters.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
// Return only assertion class and fixture coordinates, never messages or actual/expected values.
export function savedProfileFailure(error) {
  const location=error instanceof Error?error.stack?.match(/Test-BrowserNativeSavedProfile\.mjs:(\d+):(\d+)/):undefined;
  return {kind:error?.code==='ERR_ASSERTION'?'assertion_failed':'execution_failed',
    ...(location?{line:Number(location[1]),column:Number(location[2])}:{})};
}
export async function savedProfileAcceptance(h) {
  const {fixture,context,executable,baseEnv,exchange,manage,powershell,frame,response,origin,nonce,check,receipt}=h;
  assert.equal(process.platform,'win32');assert.equal(process.env.RUNNER_ENVIRONMENT,'github-hosted');
  const stateDir=await fs.realpath(await fs.mkdtemp(path.join(fixture,'saved-work-')));
  const configPath=path.join(stateDir,'openclaw.json');
  const config={gateway:{mode:'local',port:18789},browser:{enabled:true,profiles:{chrome:{driver:'extension'},work:{driver:'extension',cdpPort:19444}}}};
  const configBytes=JSON.stringify(config);await fs.writeFile(configPath,configBytes,{flag:'wx'});
  let current={...context,stateDir,configPath:await fs.realpath(configPath),browserProfile:'work'};
  const env=()=>({...baseEnv,OPENCLAW_STATE_DIR:current.stateDir,OPENCLAW_CONFIG_PATH:current.configPath,OPENCLAW_NO_RESPAWN:'1'});
  async function cli(args,overrides={}) {
    const result=await exchange(overrides.node??current.nodePath,[current.cliPath,'browser','extension',...args,'--native-host-executable',executable,'--json'],Buffer.alloc(0),{end:true,env:{...env(),...overrides.env},onSpawn:overrides.onSpawn});
    let body;try{body=JSON.parse(result.out.toString('utf8'));}catch{}
    return {code:result.code,body}; // Raw stderr and secret-bearing stdout never reach receipts.
  }
  async function descriptor() {
    const manifest=JSON.parse(powershell("$k=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\\Google\\Chrome\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap');try{[Console]::Out.Write((ConvertTo-Json -Compress -InputObject ([string]$k.GetValue(''))))}finally{$k.Dispose()}"));
    assert.ok(typeof manifest==='string'&&path.isAbsolute(manifest));
    const dir=path.dirname(manifest);
    const binding=JSON.parse(await fs.readFile(path.join(dir,'OpenClaw.BrowserBootstrap.binding.json'),'utf8'));
    assert.equal(binding.mode,'native-windows-cli');
    assert.equal(binding.nativeWindows.stateDir.toLowerCase(),stateDir.toLowerCase());
    assert.equal(binding.nativeWindows.configPath.toLowerCase(),current.configPath.toLowerCase());
    const request={v:1,action:'inspect',mode:'native-windows-cli',context:{...current,browserProfile:binding.nativeWindows.browserProfile},expectedOrigins:binding.expectedOrigins,store:'preserve'};
    const inspected=await manage(request);assert.equal(inspected.ok,true);assert.equal(inspected.registration,'owned');
    assert.equal(inspected.installation.manifestPath.toLowerCase(),manifest.toLowerCase());
    return {binding,request,inspected,root:path.dirname(dir)};
  }
  const bootstrap=async d=>response(await exchange(d.inspected.installation.launcherPath,[origin],frame({v:1,op:'bootstrap',nonce})));
  let installed=false,primaryFailed=false;
  let stage='seed';
  try {
    // The seed uses the canonical CLI, so normal origin/runtime selection is preserved.
    installed=true;
    const seed=await cli(['install','--browser-profile','work','--no-store','--wait-ms','0']);
    assert.ok(seed.body?.registrations.some(r=>r.state==='owned'&&r.browserProfile==='work'));
    let active=await descriptor();assert.equal(active.inspected.store,'missing');
    stage='initial_pairing';
    const initialPair=await bootstrap(active);assert.equal(initialPair.ok,true);
    const pairingUrl=new URL(initialPair.pairingString);
    assert.equal(pairingUrl.port,'18789');
    assert.equal(pairingUrl.pathname,'/browser/extension');
    assert.equal(pairingUrl.searchParams.get('profile'),'work');
    for(const action of ['inspect','verify','install']) {
      stage='selector_free_'+action;
      const before=await descriptor(),dirs=new Set(await fs.readdir(before.root));
      const result=await cli(['setup','--action',action,'--wait-ms','0']);
      assert.equal(result.code,0);assert.equal(result.body.target.profile,'work');assert.equal(result.body.target.relayPort,19444);
      assert.equal(result.body.installation.nativeHostRegistered,true);assert.equal(result.body.installation.installRequested,false);
      active=await descriptor();assert.equal(active.binding.nativeWindows.browserProfile,'work');assert.equal(active.inspected.store,'missing');
      const added=(await fs.readdir(before.root)).filter(x=>!dirs.has(x));
      assert.equal(added.length,action==='install'?1:0);
      assert.equal((await bootstrap(active)).pairingString,initialPair.pairingString);
    }
    check('canonical_cli_saved_work_profile_recovered_without_pairing_or_optout_changes');
    async function unchangedFailure(name,args,overrides={}) {
      stage=name;
      const before=await descriptor(),dirs=JSON.stringify((await fs.readdir(before.root)).sort());
      const result=await cli(args,overrides);assert.ok(result.code!==0||result.body?.phase==='blocked',name);
      const after=await descriptor();assert.equal(after.inspected.installation.generation,before.inspected.installation.generation,name);
      assert.equal(JSON.stringify((await fs.readdir(before.root)).sort()),dirs,name);
      assert.equal((await bootstrap(after)).pairingString,initialPair.pairingString,name);
    }
    await unchangedFailure('explicit_other_profile',['setup','--action','install','--browser-profile','chrome','--wait-ms','0']);
    const otherState=await fs.mkdtemp(path.join(fixture,'other-work-state-'));
    await unchangedFailure('different_state',['setup','--action','install','--wait-ms','0'],{env:{OPENCLAW_STATE_DIR:otherState}});
    const otherConfig=path.join(stateDir,'other-config.json');await fs.writeFile(otherConfig,configBytes);
    await unchangedFailure('different_config',['setup','--action','install','--wait-ms','0'],{env:{OPENCLAW_CONFIG_PATH:otherConfig}});
    const alternateRuntimeDirectory=await fs.mkdtemp(path.join(fixture,'alternate-runtime-'));
    const otherNode=path.join(alternateRuntimeDirectory,'node.exe');await fs.copyFile(current.nodePath,otherNode,fs.constants.COPYFILE_EXCL);
    await unchangedFailure('runtime_drift',['setup','--action','install','--wait-ms','0'],{node:otherNode});
    stage='wrong_mode';
    active=await descriptor();const wrongMode=await manage({...active.request,action:'install',mode:'companion-managed-wsl',context:null,expectedOrigins:[origin]});
    assert.equal(wrongMode.ok,false);assert.equal((await descriptor()).inspected.installation.generation,active.inspected.installation.generation);
    check('saved_profile_explicit_context_mode_and_runtime_drift_fail_closed');
    stage='absent_saved_profile';
    const before=await descriptor(),dirs=JSON.stringify((await fs.readdir(before.root)).sort());
    try {
      await fs.writeFile(configPath,JSON.stringify({...config,browser:{...config.browser,profiles:{chrome:config.browser.profiles.chrome}}}));
      const noSaved=await cli(['setup','--action','install','--wait-ms','0']);assert.notEqual(noSaved.code,0);
      assert.equal(JSON.stringify((await fs.readdir(before.root)).sort()),dirs);
    } finally { await fs.writeFile(configPath,configBytes); }
    assert.equal((await descriptor()).inspected.installation.generation,before.inspected.installation.generation);
    check('absent_configured_saved_profile_does_not_fall_back_or_mutate');
    stage='unverified_binding';
    // Corrupt only the task-owned immutable binding and restore exact bytes; no production bypass.
    active=await descriptor();
    const bindingPath=active.inspected.installation.bindingPath;
    const bindingBytes=await fs.readFile(bindingPath),generationBefore=active.inspected.installation.generation;
    const generationsBefore=JSON.stringify((await fs.readdir(active.root)).sort());
    try {
      await fs.writeFile(bindingPath,Buffer.concat([bindingBytes,Buffer.from(' ')]));
      const refused=await cli(['setup','--action','install','--wait-ms','0']);
      assert.ok(refused.code!==0||refused.body?.phase==='blocked');
      assert.equal(JSON.stringify((await fs.readdir(active.root)).sort()),generationsBefore);
    } finally { await fs.writeFile(bindingPath,bindingBytes); }
    assert.equal((await descriptor()).inspected.installation.generation,generationBefore);
    assert.equal((await bootstrap(await descriptor())).pairingString,initialPair.pairingString);
    check('unverified_binding_never_authorizes_automatic_repair');
    stage='unknown_busy_inventory';
    if(!h.withFrozenNative)throw Error('Actual native ownership fixture required for unknown/cancel proof');
    active=await descriptor();
    await h.withFrozenNative(active.inspected.installation,frame({v:1,op:'bootstrap',nonce}),fixture,async control=>{
      const blocked=await cli(['setup','--action','install','--wait-ms','0']);
      assert.ok(blocked.code!==0||blocked.body?.phase==='blocked');
      assert.equal(JSON.stringify((await fs.readdir(active.root)).sort()),generationsBefore);
      await control.resume();assert.equal(response(await control.work).ok,true);
    });
    assert.equal((await descriptor()).inspected.installation.generation,generationBefore);
    check('unknown_busy_inventory_cannot_select_profile_or_mutate');
    active=await descriptor();
    stage='mixed_generations';
    let retained;
    for(const name of await fs.readdir(active.root)) {
      const file=path.join(active.root,name,'OpenClaw.BrowserBootstrap.binding.json');
      try { const b=JSON.parse(await fs.readFile(file,'utf8'));if(b.nativeWindows?.stateDir.toLowerCase()===stateDir.toLowerCase()&&b.manifestPath!==active.inspected.installation.manifestPath){retained=b.manifestPath;break;} } catch {}
    }
    assert.ok(retained,'retained owned generation fixture required');
    function replaceChromium(expected,next) {
      const payload=Buffer.from(JSON.stringify({expected,next})).toString('base64');
      powershell("$ErrorActionPreference='Stop';$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+payload+"'))|ConvertFrom-Json;$k=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software/Chromium/NativeMessagingHosts/ai.openclaw.browser_bootstrap'.Replace('/',[IO.Path]::DirectorySeparatorChar),$true);try{if([string]$k.GetValue('')-cne $p.expected){throw 'fixture ownership changed'};$k.SetValue('',$p.next,[Microsoft.Win32.RegistryValueKind]::String)}finally{$k.Dispose()}");
    }
    const currentManifest=active.inspected.installation.manifestPath;
    try {
      replaceChromium(currentManifest,retained);
      const mixed=await cli(['setup','--action','install','--wait-ms','0']);assert.ok(mixed.code!==0||mixed.body?.phase==='blocked');
      assert.equal(JSON.stringify((await fs.readdir(active.root)).sort()),generationsBefore);
    } finally { replaceChromium(retained,currentManifest); }
    assert.equal((await descriptor()).inspected.installation.generation,generationBefore);
    check('mixed_registered_generations_fail_closed_without_automatic_mutation');
    stage='cancelled_setup';
    await h.withFrozenNative(active.inspected.installation,frame({v:1,op:'bootstrap',nonce}),fixture,async control=>{
      const cancelled=await cli(['setup','--action','install','--wait-ms','0'],{onSpawn:async child=>{
        const payload=Buffer.from(JSON.stringify({pid:child.pid,node:current.nodePath,helper:executable})).toString('base64');
        powershell("$ErrorActionPreference='Stop';$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+payload+"'))|ConvertFrom-Json;$parent=[Diagnostics.Process]::GetProcessById([int]$p.pid);$null=$parent.Handle;try{if($parent.MainModule.FileName-cne $p.node){throw 'CLI identity'};$end=[DateTime]::UtcNow.AddSeconds(12);$owned=$null;while($null-eq $owned){if($parent.HasExited-or[DateTime]::UtcNow-gt$end){throw 'management child not observed'};foreach($c in @(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$parent.Id))){if($c.ExecutablePath-ceq$p.helper){$owned=[Diagnostics.Process]::GetProcessById([int]$c.ProcessId);$null=$owned.Handle;if($owned.StartTime.ToUniversalTime()-lt$parent.StartTime.ToUniversalTime()-or$owned.MainModule.FileName-cne$p.helper){throw 'child identity'};break}};if($null-eq$owned){Start-Sleep -Milliseconds 10}};try{& (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $parent.Id /T /F >$null;if(!$parent.WaitForExit(5000)-or!$owned.WaitForExit(5000)){throw 'owned tree not joined'}}finally{$owned.Dispose()}}finally{$parent.Dispose()}");
      }});
      assert.notEqual(cancelled.code,0);assert.equal(JSON.stringify((await fs.readdir(active.root)).sort()),generationsBefore);
      await control.resume();assert.equal(response(await control.work).ok,true);
    });
    assert.equal((await descriptor()).inspected.installation.generation,generationBefore);
    check('cancelled_selector_free_setup_joins_owned_management_child_without_mutation');
    stage='store_preservation';
    // Explicit Store request is separate from the opt-out case; repair must preserve it.
    await cli(['install','--browser-profile','work','--wait-ms','0']);
    assert.equal((await descriptor()).inspected.store,'requested');
    const preserve=await cli(['setup','--action','install','--wait-ms','0']);
    assert.equal(preserve.body.target.profile,'work');assert.equal(preserve.body.installation.installRequested,true);
    assert.equal((await bootstrap(await descriptor())).pairingString,initialPair.pairingString);
    check('selector_free_repair_preserves_explicit_store_request');
    stage='retired_profile_selection';
    const retireWork=await cli(['uninstall-host','--browser-profile','work','--remove-store']);
    assert.equal(retireWork.code,0);assert.ok(Array.isArray(retireWork.body?.refused));assert.equal(retireWork.body.refused.length,0);
    current={...current,browserProfile:'chrome'};
    await cli(['install','--browser-profile','chrome','--no-store','--wait-ms','0']);
    const currentChrome=await descriptor();assert.equal(currentChrome.binding.nativeWindows.browserProfile,'chrome');
    const select=await cli(['setup','--action','inspect','--wait-ms','0']);
    assert.equal(select.body.target.profile,'chrome');
    assert.equal((await descriptor()).inspected.installation.generation,currentChrome.inspected.installation.generation);
    check('retained_work_generations_never_override_current_chrome_selection');
    receipt.savedProfile={workRelay19444:true,pairingPreserved:true,optOutPreserved:true,requestedStorePreserved:true,retiredSelectionRefused:true};
  } catch(error) {
    primaryFailed=true;
    receipt.savedProfileFailure={stage,...savedProfileFailure(error)};
    throw error;
  } finally {
    if(installed) {
      try {
      const cleanup=await cli(['uninstall-host','--browser-profile',current.browserProfile,'--remove-store']);
      assert.equal(cleanup.code,0,'saved_profile_cleanup_exit');
      assert.ok(Array.isArray(cleanup.body?.refused),'saved_profile_cleanup_receipt');
      assert.equal(cleanup.body.refused.length,0,'saved_profile_owned_cleanup');
      const after=await manage({v:1,action:'inspect',mode:'native-windows-cli',context:current,expectedOrigins:[origin],store:'preserve'});
      assert.equal(after.ok,true);assert.equal(after.registration,'missing');assert.equal(after.store,'missing');
      receipt.savedProfileCleanup={registrationMissing:true,storeMissing:true};
      } catch(error) {
        receipt.savedProfileCleanup={failed:true,...savedProfileFailure(error)};
        // Preserve the initial failure when cleanup also fails; both remain in the receipt.
        if(!primaryFailed)throw error;
      }
    }
  }
}
