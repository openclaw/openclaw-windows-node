param([Parameter(Mandatory=$true)][string]$ControlDirectory)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') { throw 'Disposable hosted runner required.' }
# Test-only debugger-style scheduling. Never changes product code, registry, ACLs or privileges.
# SuspendThread/ResumeThread each change the count once; only our acquired handles are resumed.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
public sealed class OwnedNodeFreeze : IDisposable {
    readonly Process process;
    readonly List<IntPtr> handles = new List<IntPtr>();
    public OwnedNodeFreeze(Process process) {
        this.process=process;
        try {
            foreach(ProcessThread thread in process.Threads) {
                IntPtr h=OpenThread(0x0802,false,(uint)thread.Id);
                if(h==IntPtr.Zero)throw new InvalidOperationException("Thread admission failed");
                if(GetProcessIdOfThread(h)!=(uint)process.Id){CloseHandle(h);throw new InvalidOperationException("Thread identity changed");}
                if(SuspendThread(h)==uint.MaxValue){CloseHandle(h);throw new InvalidOperationException("Thread suspension failed");}
                handles.Add(h);
            }
            if(handles.Count==0)throw new InvalidOperationException("No owned threads");
        } catch { Dispose();throw; }
    }
    public void Dispose() {
        bool failed=false;
        foreach(IntPtr h in handles) { if(ResumeThread(h)==uint.MaxValue && !process.HasExited)failed=true;CloseHandle(h); }
        handles.Clear();
        if(failed)throw new InvalidOperationException("Owned resume failed");
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenThread(uint rights,bool inherit,uint id);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
}
'@
function Mark([string]$Name) { [IO.File]::WriteAllText((Join-Path $ControlDirectory $Name),'1') }
$freeze=$null;$native=$null;$node=$null
try {
    $end=[DateTime]::UtcNow.AddSeconds(25)
    Mark 'ready'
    $inputFile=Join-Path $ControlDirectory 'input.json'
    while(!(Test-Path -LiteralPath $inputFile)) { if([DateTime]::UtcNow -gt $end){throw 'Missing owned process'};Start-Sleep -Milliseconds 10 }
    $bound=Get-Content -LiteralPath $inputFile -Raw|ConvertFrom-Json
    $native=[Diagnostics.Process]::GetProcessById([int]$bound.nativePid)
    $null=$native.Handle # Hold the kernel object so exit/PID reuse cannot select another process.
    $nativeStart=$native.StartTime.ToUniversalTime()
    if($native.MainModule.FileName -cne $bound.launcher){throw 'Producer image mismatch'}
    Mark 'watching'
    while($null -eq $node) {
        if($native.HasExited -or [DateTime]::UtcNow -gt $end){throw 'Owned child not observed'}
        $children=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$native.Id))
        foreach($child in $children) {
            if($child.ExecutablePath -ceq $bound.node) {
                $candidate=[Diagnostics.Process]::GetProcessById([int]$child.ProcessId)
                $null=$candidate.Handle
                if($candidate.StartTime.ToUniversalTime() -lt $nativeStart -or $candidate.MainModule.FileName -cne $bound.node){$candidate.Dispose();throw 'Child identity mismatch'}
                $node=$candidate;break
            }
        }
        if($null -eq $node){Start-Sleep -Milliseconds 10}
    }
    $freeze=[OwnedNodeFreeze]::new($node)
    Mark 'suspended'
    while(!(Test-Path -LiteralPath (Join-Path $ControlDirectory 'resume')) -and !$node.HasExited) {
        if([DateTime]::UtcNow -gt $end){throw 'Owned control deadline'}
        Start-Sleep -Milliseconds 10
    }
    $freeze.Dispose();$freeze=$null
    Mark 'settled'
} catch {
    Mark 'failed'
    exit 1
} finally {
    if($null -ne $freeze){$freeze.Dispose()}
    if($null -ne $node){$node.Dispose()}
    if($null -ne $native){$native.Dispose()}
}
