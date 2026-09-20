using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using OpenClaw.Connection;

namespace OpenClaw.E2ETests.Setup;
public sealed class BrowserWslOwnerProofFactAttribute : FactAttribute
{
    public BrowserWslOwnerProofFactAttribute()
    {
        if (!E2ETestGate.IsEnabled || Environment.GetEnvironmentVariable("OPENCLAW_BROWSER_WSL_OWNER_PROOF") != "1")
            Skip = "Dedicated actual-owner hosted proof only.";
    }
}
public sealed class BrowserWslProductionOwnerTests
{
    private readonly List<object> _cases=[];
    private readonly List<string> _errors=[];
    private const string Cli="/home/openclaw/.openclaw/bin/openclaw";
    private string _root="";
    private long _gatewayPid;
    [BrowserWslOwnerProofFact]
    public async Task ActualOwner_DoesNotRetireWithoutGuestAcknowledgment()
    {
        Assert.True(OperatingSystem.IsWindows());Assert.Equal("github-hosted",Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        var receipt=Environment.GetEnvironmentVariable("OPENCLAW_WSL_OWNER_RECEIPT")!;
        var fixture=new E2ESetupFixture();var stdout=Console.Out;var stderr=Console.Error;
        bool installed=false,restored=false,disposed=false,gatewayUnchanged=false;
        Console.SetOut(TextWriter.Null);Console.SetError(TextWriter.Null);
        try
        {
            await fixture.InitializeAsync();Assert.Null(fixture.SetupError);await fixture.StopTrayAsync();
            _gatewayPid=long.Parse((await Guest(fixture,"systemctl --user show openclaw-gateway.service -p MainPID --value")).Trim());
            Assert.True(_gatewayPid>1);
            _root="/home/openclaw/.openclaw/owner-proof-"+Guid.NewGuid().ToString("N");
            await EarlyEof(fixture);
            await RunCase(fixture,"real_cli");
            installed=true;
            await Guest(fixture,$"umask 077; mkdir '{_root}'; if test -e '{Cli}' || test -L '{Cli}'; then mv '{Cli}' '{_root}/original'; fi");
            foreach(var name in new[]{"normal_setsid","caller_cancel","deadline","forced_client_exit"})
            {
                try{await RunCase(fixture,name);}catch(Exception e){_errors.Add(name+":"+e.GetType().Name);}
            }
            gatewayUnchanged=(await Guest(fixture,"systemctl --user show openclaw-gateway.service -p MainPID --value")).Trim()==_gatewayPid.ToString();
        }
        catch(Exception e){_errors.Add("fixture:"+e.GetType().Name);}
        finally
        {
            if(installed)try
            {
                await Guest(fixture,$"if test -f '{Cli}' && grep -Fq '{_root}/' '{Cli}'; then rm '{Cli}'; fi; if test -e '{_root}/original' || test -L '{_root}/original'; then test ! -e '{Cli}' && test ! -L '{Cli}'; mv '{_root}/original' '{Cli}'; fi; rm -rf '{_root}'");restored=true;
            }catch(Exception e){_errors.Add("restore:"+e.GetType().Name);}
            else restored=true;
            try{await fixture.DisposeAsync();disposed=true;}catch(Exception e){_errors.Add("dispose:"+e.GetType().Name);}
            Console.SetOut(stdout);Console.SetError(stderr);
            var assembly=typeof(BrowserBootstrapWslCommand).Assembly;
            using var broker=assembly.GetManifestResourceStream("OpenClaw.Connection.BrowserBootstrapWslBroker.cjs")!;
            using var hash=SHA256.Create();
            await File.WriteAllTextAsync(receipt,JsonSerializer.Serialize(new
            {
                schema=1,sourceSha=Environment.GetEnvironmentVariable("GITHUB_SHA"),consumerSha=Environment.GetEnvironmentVariable("OPENCLAW_WSL_PROTOTYPE_CONSUMER_SHA"),
                packageSha256=Environment.GetEnvironmentVariable("OPENCLAW_WSL_PROTOTYPE_PACKAGE_SHA256"),
                productionAssemblySha256=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant(),
                embeddedBrokerSha256=Convert.ToHexString(hash.ComputeHash(broker)).ToLowerInvariant(),
                scope="actual BrowserBootstrapWslCommand plus unchanged source-linked registration owners; synthetic native inventory/real mutex, not shipping Chrome helper or registry proof",
                cases=_cases,errors=_errors,gatewayUnchanged,originalCliRestored=restored,ownedFixtureDisposed=disposed,
                status=_errors.Count==0&&_cases.Count==6&&restored&&disposed&&gatewayUnchanged?"production_owner_proof_passed":"production_owner_proof_failed"
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        Assert.Empty(_errors);Assert.Equal(6,_cases.Count);Assert.True(restored&&disposed&&gatewayUnchanged);
    }
    private async Task EarlyEof(E2ESetupFixture fixture)
    {
        var id=Guid.NewGuid().ToString("N");var state=new BrowserBootstrapWslExchange(id,"openclaw-browser-bootstrap-");
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"wsl.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var a in new[]{"--distribution",fixture.DistroName,"--exec","/bin/bash","--noprofile","--norc","-s"})start.ArgumentList.Add(a);
        start.Environment.Remove("WSLENV");using var p=Process.Start(start)!;
        var error=DrainBoundedError(p.StandardError);
        try
        {
            await p.StandardInput.WriteAsync(BrowserBootstrapWslCommand.BuildInput(id));await p.StandardInput.FlushAsync();p.StandardInput.Close();state.Revoke();
            await state.ReadToEndAsync(p.StandardOutput.BaseStream).WaitAsync(TimeSpan.FromSeconds(17));
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));await error;
            Assert.True(state.Acknowledged);Assert.Throws<InvalidDataException>(()=>state.Result);Assert.Equal(0,p.ExitCode);
            _cases.Add(new{name="early_stdin_eof",actualProductionScript=true,noPermitSent=true,inputClosedBeforeRead=true,positiveSettlement=true,resultReturned=false});
        }
        finally{if(!p.HasExited){p.Kill(entireProcessTree:true);await p.WaitForExitAsync();}}
    }
    private async Task RunCase(E2ESetupFixture fixture,string name)
    {
        var held=name is "caller_cancel" or "deadline" or "forced_client_exit";
        if(name!="real_cli")
        {
            var payload=JsonSerializer.Serialize(new{remote=false,relayPort=19444,pairingString=$"ws://127.0.0.1:{fixture.GatewayPort}/browser/extension?gateway=ws%3A%2F%2F127.0.0.1%3A{fixture.GatewayPort}#"+new string('a',64)});
            var program="const p=require('node:child_process').spawn(process.execPath,['-e','setTimeout(()=>{},60000)'],{detached:true,stdio:'ignore'});p.unref();"+
                (held?"setTimeout(()=>{},60000);":"setTimeout(()=>process.stdout.write("+JsonSerializer.Serialize(payload)+"),1000);");
            var encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(program));
            await Guest(fixture,$"printf '%s' '{encoded}' | base64 -d > '{_root}/case.cjs'; printf '%s\\n' '#!/bin/sh' 'exec /home/openclaw/.openclaw/tools/node/bin/node {_root}/case.cjs' > '{Cli}'; chmod 700 '{Cli}'");
        }
        using var caller=new CancellationTokenSource();var clock=Stopwatch.StartNew();
        // Unknown requests intentionally retain their source-linked activation until this test
        // process exits. They are never disposed/unlocked on a timeout or negative guest query.
        var ownership=new BrowserWslNativeOwnership(clock);long commandEnd=0;string? outcome=null;string? pairing=null;
        var run=ownership.Run(async()=>
        {
            try{pairing=await BrowserBootstrapWslCommand.RunAsync(fixture.DistroName,caller.Token);outcome="returned";}
            catch(OperationCanceledException){outcome="cancelled";}
            catch(Exception e){outcome=e.GetType().Name;}
            finally{Interlocked.Exchange(ref commandEnd,clock.ElapsedTicks);}
        });
        _=run.ContinueWith(t=>_=t.Exception,TaskContinuationOptions.OnlyOnFaulted);
        JsonElement unit=default;bool unknown=false;string stage="unit_observation";int? clientPid=null;bool clientKilled=false;int queryExit=-1,queryMatches=-1;
        ClientLookupObservation? lookup=null;
        try
        {
            unit=await WaitUnit(fixture,p=>p.GetProperty("present").GetBoolean()&&(!held||p.GetProperty("processes").GetArrayLength()>=3),TimeSpan.FromSeconds(12));
            var unitName=unit.GetProperty("unit").GetString()!;
            Assert.StartsWith("openclaw-browser-bootstrap-",unitName);
            var ids=unit.GetProperty("processes").EnumerateArray().Select(p=>new{pid=p.GetProperty("pid").GetInt32(),start=p.GetProperty("start").GetInt64(),session=p.GetProperty("session").GetInt32()}).ToArray();
            if(held)Assert.True(ids.Select(p=>p.session).Distinct().Count()>=2);
            stage="retirement_started";var retirement=ownership.Retire();
            Assert.Equal(0,Interlocked.Read(ref ownership.ManagementMutationTicks));
            if(name=="caller_cancel")caller.Cancel();
            if(name=="forced_client_exit")
            {
                stage="exact_client_selection";
                using var process=await FindProductionClientAsync(fixture.DistroName,observation=>{lookup=observation;queryExit=observation.ExitCode;queryMatches=observation.Matches;});clientPid=process.Id;
                stage="client_termination";process.Kill();await process.WaitForExitAsync();clientKilled=true;
                stage="retirement_outcome";await retirement.WaitAsync(TimeSpan.FromSeconds(40));
                Assert.Equal("busy",ownership.ManagementCode);Assert.False(run.IsCompleted);Assert.Equal(0,Interlocked.Read(ref commandEnd));
                Assert.Equal(0,ownership.ActivationReleasedTicks);Assert.Equal(0,ownership.RuntimeLeaseReleaseTicks);Assert.Equal(0,ownership.ManagementMutationTicks);
                unknown=true;
            }
            else
            {
                await run.WaitAsync(TimeSpan.FromSeconds(20));await retirement.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("ok",ownership.ManagementCode);Assert.True(commandEnd>0&&commandEnd<=ownership.ActivationReleaseBeginTicks);
                Assert.True(ownership.ActivationReleasedTicks<=ownership.ManagementMutationTicks);
                if(held){Assert.Equal("cancelled",outcome);Assert.Null(pairing);}else{Assert.Equal("returned",outcome);_=BrowserBootstrapService.ParsePairing(pairing!,fixture.GatewayPort);}
                if(name=="deadline")Assert.InRange(commandEnd*1000/Stopwatch.Frequency,14500,17000);
            }
            stage="guest_postcheck";var empty=await WaitUnit(fixture,p=>!p.GetProperty("present").GetBoolean()||p.GetProperty("processes").GetArrayLength()==0,TimeSpan.FromSeconds(5),unitName);
            _cases.Add(new{name,identities=ids,commandEndTicks=commandEnd,clockFrequency=Stopwatch.Frequency,outcome,
                ownership.RuntimeLeaseReleaseTicks,ownership.ActivationReleaseBeginTicks,ownership.ActivationReleasedTicks,ownership.ManagementMutationTicks,ownership.ManagementCompletionTicks,ownership.ManagementCode,
                clientPid,clientKilled,queryExit,queryMatches,lookup,actualOwnerExecuted=true,guestAcknowledgmentRequiredByOwner=!unknown,unknownRetainsActivation=unknown,resultReturned=pairing is not null,
                observedPostcheckEmpty=empty.GetProperty("processes").GetArrayLength()==0,canonicalPairingValidated=name=="real_cli",syntheticCli=name!="real_cli"});
        }
        catch(Exception e)
        {
            _cases.Add(new{name,failed=true,stage,errorType=e.GetType().Name,clientPid,clientKilled,queryExit,queryMatches,lookup,outcome,
                commandEndTicks=Interlocked.Read(ref commandEnd),runCompleted=run.IsCompleted,ownership.ManagementCode,
                ownership.RuntimeLeaseReleaseTicks,ownership.ActivationReleasedTicks,ownership.ManagementMutationTicks,ownership.ManagementCompletionTicks});
            throw;
        }
        finally
        {
            caller.Cancel();
            if(!unknown&&run.IsCompleted)ownership.Dispose();
            // A failed case with unsettled work is NOT converted into successful retirement.
        }
    }
    private static async Task DrainBoundedError(StreamReader reader)
    {
        var buffer=new char[1024];var total=0;int count;
        while((count=await reader.ReadAsync(buffer))!=0)if((total+=count)>16384)throw new InvalidDataException("Bounded proof drain");
    }
    private sealed record ClientLookupObservation(string Stage,long ElapsedMilliseconds,bool ScriptEntered,
        bool ProcessExited,bool OutputCompleted,bool ErrorCompleted,int ExitCode,int Matches,bool CleanupFailed=false);
    private static async Task<Process> FindProductionClientAsync(string distro,Action<ClientLookupObservation> report)
    {
        var ps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        Assert.Matches("^OpenClawE2E-[a-f0-9]{8}$",distro);
        var image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"wsl.exe");
        var arguments="--distribution "+distro+" --exec /bin/bash --noprofile --norc -s";
        var expected=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {parent=Environment.ProcessId,quoted="\""+image+"\" "+arguments,unquoted=image+" "+arguments}));
        var script="[Console]::Out.WriteLine('LOOKUP_READY');[Console]::Out.Flush();$e=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+expected+"'))|ConvertFrom-Json;Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$e.parent+' AND Name=\"wsl.exe\"') | Where-Object {$_.CommandLine-ceq$e.quoted-or$_.CommandLine-ceq$e.unquoted} | ForEach-Object {$_.ProcessId}";
        var start=new ProcessStartInfo(ps){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var a in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(a);
        using var process=Process.Start(start)!;
        var clock=Stopwatch.StartNew();bool scriptEntered=false,primaryFailed=false;
        var output=ReadLookupOutput(process.StandardOutput,()=>Volatile.Write(ref scriptEntered,true));var error=DrainBoundedError(process.StandardError);
        string stage="query_and_drains";int matchesCount=-1;ClientLookupObservation? failure=null;
        ClientLookupObservation Snapshot()=>new(stage,clock.ElapsedMilliseconds,Volatile.Read(ref scriptEntered),
            process.HasExited,output.IsCompleted,error.IsCompleted,process.HasExited?process.ExitCode:-1,matchesCount);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(),output,error).WaitAsync(TimeSpan.FromSeconds(5));
            stage="parse_exact_matches";
            var lines=(await output).Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);Assert.Equal("LOOKUP_READY",lines[0]);
            var matches=lines.Skip(1).Select(int.Parse).ToArray();matchesCount=matches.Length;
            report(Snapshot());Assert.Equal(0,process.ExitCode);Assert.Single(matches);
            stage="bind_exact_image";
            var owned=Process.GetProcessById(matches[0]);_=owned.Handle;
            if(!string.Equals(owned.MainModule!.FileName,image,StringComparison.OrdinalIgnoreCase)){owned.Dispose();throw new InvalidDataException("Owned image changed");}
            report(Snapshot());return owned;
        }
        catch
        {
            primaryFailed=true;failure=Snapshot();report(failure);throw;
        }
        finally
        {
            try
            {
                if(!process.HasExited)process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await Task.WhenAll(ObserveLookupDrainAsync(output),ObserveLookupDrainAsync(error)).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch when(primaryFailed)
            {
                // Preserve the primary query failure and separately expose failed cleanup.
                report(failure! with {CleanupFailed=true});
                _=Task.WhenAll(output,error).ContinueWith(t=>_=t.Exception,TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }
    internal static async Task ObserveLookupDrainAsync(Task drain)
    {
        // Cleanup needs settlement, not a second successful result from a failed drain.
        // The primary query path already records/rethrows protocol and bound failures.
        try{await drain.ConfigureAwait(false);}catch{_=drain.Exception;}
    }
    internal static async Task<string> ReadLookupOutput(StreamReader reader,Action entered)
    {
        var result=new StringBuilder();var buffer=new char[128];int count;
        while((count=await reader.ReadAsync(buffer))!=0)
        {
            if(result.Length+count>4096)throw new InvalidDataException("Lookup output bound");
            result.Append(buffer,0,count);
            var text=result.ToString();
            if(text.StartsWith("LOOKUP_READY\r\n",StringComparison.Ordinal)||text.StartsWith("LOOKUP_READY\n",StringComparison.Ordinal))entered();
        }
        return result.ToString();
    }
    private static async Task<string> Guest(E2ESetupFixture f,string script)
    {var r=await f.RunInWslAsync("set -euo pipefail\n"+script,TimeSpan.FromSeconds(15),inputViaStdin:true);Assert.False(r.TimedOut);Assert.Equal(0,r.ExitCode);return r.Stdout;}
    private static async Task<JsonElement> WaitUnit(E2ESetupFixture f,Func<JsonElement,bool> predicate,TimeSpan timeout,string? exact=null)
    {
        var clock=Stopwatch.StartNew();
        do{var p=await Probe(f,exact);if(predicate(p))return p;await Task.Delay(50);}while(clock.Elapsed<timeout);
        throw new TimeoutException("Owned unit observation");
    }
    private static async Task<JsonElement> Probe(E2ESetupFixture f,string? exact)
    {
        var selector=exact??"openclaw-browser-bootstrap-*.service";
        var code="import subprocess,pathlib,json\nnames=subprocess.run(['systemctl','--user','list-units','--all','--plain','--no-legend','"+selector+"'],capture_output=True,text=True).stdout.splitlines()\nnames=[l.split()[0] for l in names if l.strip()]\nassert len(names)<=1\nr={'present':False,'processes':[]}\nif names:\n u=names[0];v=dict(l.split('=',1) for l in subprocess.run(['systemctl','--user','show',u,'-p','ControlGroup','-p','InvocationID','-p','Description'],capture_output=True,text=True).stdout.splitlines() if '=' in l)\n assert v.get('Description','').startswith('OpenClaw browser bootstrap ')\n g=v.get('ControlGroup','');p=[]\n if g.startswith('/user.slice/') and '..' not in g:\n  f=pathlib.Path('/sys/fs/cgroup'+g+'/cgroup.procs')\n  if f.exists():\n   for pid in f.read_text().split():\n    try:\n     a=pathlib.Path('/proc/'+pid+'/stat').read_text().rsplit(') ',1)[1].split();p.append({'pid':int(pid),'start':int(a[19]),'session':int(a[3])})\n    except FileNotFoundError:pass\n r={'present':True,'unit':u,'invocation':v.get('InvocationID'),'processes':p}\nprint(json.dumps(r))";
        var b64=Convert.ToBase64String(Encoding.UTF8.GetBytes(code));using var doc=JsonDocument.Parse(await Guest(f,$"printf '%s' '{b64}' | base64 -d | /usr/bin/python3"));return doc.RootElement.Clone();
    }
}
