// Windows-only temporary command owner. Start suspended, assign job, then resume. Never selects a process by name.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace OpenClawObserve {
// Per-command evidence only. First failure is sticky across cleanup; no exception content is retained.
public enum CommandPhase { None, Preparation, JobCreate, JobConfigure, StandardInput, StandardOutput, StandardError,
    AttributeList, AttributeInitialize, AttributeUpdate, Environment, ProcessCreate, JobAssign, Resume, Wait, OutputCheck, ExitRead, JobDrain, JobTerminate, Cleanup, Completed }
public enum CommandFailure { None, NativeApi, CommandResolution, ParameterBinding, MethodBinding, Invocation,
    PowerShell, Argument, Io, Access, InvalidOperation, Interop, Other }
public sealed class CommandWitness {
    public CommandPhase Phase { get; private set; }
    public CommandFailure Category { get; private set; }
    public bool Failed { get; private set; }
    public int? Win32Error { get; private set; }
    public bool Win32ErrorAvailable => Win32Error.HasValue;
    public void Enter(CommandPhase phase) { if(!Failed) {Phase=phase;Win32Error=null;} }
    public void NoteNative(bool succeeded) {
        if(succeeded) return;
        // First operation after a failed SetLastError P/Invoke. Never read this cache for managed failures.
        int error=Marshal.GetLastPInvokeError();
        if(Failed) return;
        Win32Error=error==0 ? (int?)null : error; Category=CommandFailure.NativeApi; Failed=true;
    }
    public void RequireNative(bool succeeded) {
        NoteNative(succeeded);
        if(!succeeded) throw new InvalidOperationException("owned-command-failed");
    }
    public void RecordManaged(Exception error) {
        if(Failed) return;
        // Exact names become fixed categories. No Message, Data, stack, HResult or inner payload is read.
        Category=error.GetType().FullName switch {
            "System.Management.Automation.CommandNotFoundException" => CommandFailure.CommandResolution,
            "System.Management.Automation.ParameterBindingException" => CommandFailure.ParameterBinding,
            "System.Management.Automation.MethodException" => CommandFailure.MethodBinding,
            "System.Management.Automation.MethodInvocationException" => CommandFailure.Invocation,
            "System.Management.Automation.RuntimeException" => CommandFailure.PowerShell,
            "System.ArgumentException" or "System.ArgumentNullException" or "System.ArgumentOutOfRangeException" => CommandFailure.Argument,
            "System.IO.IOException" or "System.IO.FileNotFoundException" or "System.IO.DirectoryNotFoundException" => CommandFailure.Io,
            "System.UnauthorizedAccessException" => CommandFailure.Access,
            "System.InvalidOperationException" => CommandFailure.InvalidOperation,
            "System.DllNotFoundException" or "System.EntryPointNotFoundException" or "System.Runtime.InteropServices.MarshalDirectiveException" => CommandFailure.Interop,
            _ => CommandFailure.Other
        };
        Win32Error=null;Failed=true;
    }
}
public sealed class CommandResult {
    public int? ExitCode { get; private set; }
    public bool CleanupFailed { get; private set; }
    public bool Joined; public bool LimitHit; public string JobName;
    public bool BarrierExpectedAbort;
    public bool LifecycleSucceeded => ExitCode.HasValue && Joined && !LimitHit && !CleanupFailed;
    public void RecordExit(int exitCode) { ExitCode=exitCode; }
    // Cleanup is secondary to an observed exit. Keep failure sticky even if a later join succeeds.
    public void AttemptCleanup(Action action, CommandWitness witness=null) { try { action(); } catch (Exception error) { CleanupFailed=true; try {witness?.RecordManaged(error);} catch { } } }
    public string Stdout; public string Stderr;
}
public static class OwnedCommand {
    const uint KillOnClose=0x2000, Suspended=4, UnicodeEnvironment=0x400, ExtendedStartup=0x80000, NoWindow=0x8000000;
    const long OutputCap=64L*1024*1024;
    public static string Quote(string value) {
        if (value.IndexOf('\0')>=0) throw new InvalidOperationException("argument-invalid");
        var b=new StringBuilder("\""); int slashes=0;
        foreach(char c in value) {
            if(c=='\\') {slashes++;continue;}
            if(c=='\"') b.Append('\\',slashes*2+1).Append(c); else b.Append('\\',slashes).Append(c);
            slashes=0;
        }
        return b.Append('\\',slashes*2).Append('"').ToString();
    }
    public static CommandResult Run(string executable,string[] arguments,string cwd,IDictionary<string,string> environment,string privateRoot,int seconds,CommandWitness witness=null) {
        witness ??= new CommandWitness();
        witness.Enter(CommandPhase.Preparation);
        try {
            var result=RunCore(executable,arguments,cwd,environment,privateRoot,seconds,witness);
            witness.Enter(CommandPhase.Completed);
            return result;
        } catch(Exception error) {try {witness.RecordManaged(error);} catch { } throw;}
    }
    public static CommandResult RunBarrier(string executable,string[] arguments,string cwd,IDictionary<string,string> environment,string privateRoot,int seconds,BarrierGate barrier,CommandWitness witness=null) {
        if(barrier==null)throw new InvalidOperationException("barrier-required");
        witness ??= new CommandWitness();witness.Enter(CommandPhase.Preparation);
        try {var r=RunCore(executable,arguments,cwd,environment,privateRoot,seconds,witness,barrier);witness.Enter(CommandPhase.Completed);return r;}
        catch(Exception e){try{witness.RecordManaged(e);}catch{}throw;}
    }
    private static CommandResult RunCore(string executable,string[] arguments,string cwd,IDictionary<string,string> environment,string privateRoot,int seconds,CommandWitness witness,BarrierGate barrier=null) {
        if(seconds<1 || seconds>900 || !Path.IsPathFullyQualified(executable)) throw new InvalidOperationException("launch-contract");
        string id=Guid.NewGuid().ToString("N"), name="Local\\OpenClawObserve-"+id;
        string jobs=Path.Combine(privateRoot,"jobs"); Directory.CreateDirectory(jobs);
        if(Directory.EnumerateFiles(jobs,"*.job").Take(40).Count()>=40) throw new InvalidOperationException("command-limit");
        var result=new CommandResult { JobName=name,Stdout=Path.Combine(jobs,id+".stdout"),Stderr=Path.Combine(jobs,id+".stderr") };
        File.WriteAllText(Path.Combine(jobs,id+".job"),name);
        IntPtr job=IntPtr.Zero,attrs=IntPtr.Zero,list=IntPtr.Zero,env=IntPtr.Zero;
        IntPtr input=IntPtr.Zero,output=IntPtr.Zero,error=IntPtr.Zero; PROCESS_INFORMATION pi=new PROCESS_INFORMATION(); bool assigned=false,started=false,attributesReady=false,unassignedJoined=true;
        try {
            witness.Enter(CommandPhase.JobCreate);
            job=CreateJobObject(IntPtr.Zero,name); witness.RequireNative(job!=IntPtr.Zero);
            var limits=new EXTENDED_LIMITS(); limits.Basic.LimitFlags=KillOnClose;
            witness.Enter(CommandPhase.JobConfigure);
            witness.RequireNative(SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<EXTENDED_LIMITS>()));
            var sa=new SECURITY_ATTRIBUTES {length=Marshal.SizeOf<SECURITY_ATTRIBUTES>(),inherit=true};
            witness.Enter(CommandPhase.StandardInput);
            input=CreateFile("NUL",0x80000000,3,ref sa,3,0x80,IntPtr.Zero); witness.NoteNative(Valid(input));
            witness.Enter(CommandPhase.StandardOutput);
            output=CreateFile(result.Stdout,0x40000000,1,ref sa,1,0x80,IntPtr.Zero); witness.NoteNative(Valid(output));
            witness.Enter(CommandPhase.StandardError);
            error=CreateFile(result.Stderr,0x40000000,1,ref sa,1,0x80,IntPtr.Zero); witness.NoteNative(Valid(error));
            Check(Valid(input)&&Valid(output)&&Valid(error));
            witness.Enter(CommandPhase.AttributeList);
            // The null-list size probe is expected to fail. It is not a failure witness.
            IntPtr size=IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
            attrs=Marshal.AllocHGlobal(size);
            witness.Enter(CommandPhase.AttributeInitialize);
            witness.RequireNative(InitializeProcThreadAttributeList(attrs,1,0,ref size)); attributesReady=true;
            list=Marshal.AllocHGlobal(3*IntPtr.Size); Marshal.Copy(new[]{input,output,error},0,list,3);
            witness.Enter(CommandPhase.AttributeUpdate);
            witness.RequireNative(UpdateProcThreadAttribute(attrs,0,(IntPtr)0x20002,list,(IntPtr)(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
            var si=new STARTUPINFOEX(); si.Startup.cb=Marshal.SizeOf<STARTUPINFOEX>(); si.Startup.flags=0x100;
            si.Startup.input=input; si.Startup.output=output; si.Startup.error=error; si.attributes=attrs;
            witness.Enter(CommandPhase.Environment);
            var child=new SortedDictionary<string,string>(environment,StringComparer.OrdinalIgnoreCase);
            child["OPENCLAW_OBSERVE_JOB_NAME"]=name;
            var block=string.Join("\0",child.Select(k=>k.Key+"="+k.Value))+"\0\0";
            env=Marshal.StringToHGlobalUni(block);
            var command=new StringBuilder(string.Join(" ",new[]{Quote(executable)}.Concat(arguments.Select(Quote))));
            witness.Enter(CommandPhase.ProcessCreate);
            witness.RequireNative(CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,true,Suspended|UnicodeEnvironment|ExtendedStartup|NoWindow,env,cwd,ref si,out pi)); started=true;
            witness.Enter(CommandPhase.JobAssign);
            witness.RequireNative(AssignProcessToJobObject(job,pi.process)); assigned=true;
            if(barrier!=null)barrier.BindJob(name);
            witness.Enter(CommandPhase.Resume);
            witness.RequireNative(ResumeThread(pi.thread)!=uint.MaxValue);
            var clock=Stopwatch.StartNew();
            while(ObserveWait(pi.process,100,witness)==258) {
                witness.Enter(CommandPhase.OutputCheck);
                witness.RequireNative(GetFileSizeEx(output,out long a)); witness.RequireNative(GetFileSizeEx(error,out long b));
                if(clock.Elapsed.TotalSeconds>seconds || a+b>OutputCap || !RawWithinBounds(privateRoot)) {result.LimitHit=true;witness.Enter(CommandPhase.JobTerminate);witness.RequireNative(TerminateJobObject(job,124));break;}
                if(barrier!=null) {
                    var action=barrier.Observe();
                    if(action!=BarrierDecision.Continue) {result.BarrierExpectedAbort=action==BarrierDecision.AbortExpectedDenial;result.LimitHit=!result.BarrierExpectedAbort;witness.Enter(CommandPhase.JobTerminate);witness.RequireNative(TerminateJobObject(job,(uint)(result.BarrierExpectedAbort?BarrierGate.ExpectedAbortExit:125)));break;}
                }
            }
            Check(ObserveWait(pi.process,30000,witness)==0);
            witness.Enter(CommandPhase.ExitRead);
            witness.RequireNative(GetExitCodeProcess(pi.process,out uint exit)); result.RecordExit(unchecked((int)exit));
            result.AttemptCleanup(() => {
                // Includes descendants (testhost and collector). Never treats root exit alone as cleanup.
                result.Joined=Drain(job,30000,witness);
                if(!result.Joined) {witness.Enter(CommandPhase.JobTerminate);witness.RequireNative(TerminateJobObject(job,125));result.Joined=Drain(job,30000,witness);result.LimitHit=true;}
                witness.Enter(CommandPhase.OutputCheck);
                bool outKnown=GetFileSizeEx(output,out long finalOut); witness.NoteNative(outKnown);
                bool errKnown=GetFileSizeEx(error,out long finalErr); witness.NoteNative(errKnown);
                if(!outKnown || !errKnown || finalOut+finalErr>OutputCap || !RawWithinBounds(privateRoot)) result.LimitHit=true;
            }, witness);
            return result;
        } catch(Exception failure) {try {witness.RecordManaged(failure);} catch { } throw;} finally {
            witness.Enter(CommandPhase.Cleanup);
            // No cleanup error may replace an observed exit or the original launch exception.
            if(started && !assigned) {
                unassignedJoined=false;
                result.AttemptCleanup(() => { witness.RequireNative(TerminateProcess(pi.process,125)); unassignedJoined=ObserveWait(pi.process,30000,witness)==0; }, witness);
            }
            result.AttemptCleanup(() => {
                if(job!=IntPtr.Zero && !result.Joined) { witness.Enter(CommandPhase.JobTerminate);witness.RequireNative(TerminateJobObject(job,125)); result.Joined=Drain(job,30000,witness); }
            }, witness);
            witness.Enter(CommandPhase.Cleanup);
            if(pi.thread!=IntPtr.Zero) result.AttemptCleanup(() => witness.RequireNative(CloseHandle(pi.thread)), witness);
            if(pi.process!=IntPtr.Zero) result.AttemptCleanup(() => witness.RequireNative(CloseHandle(pi.process)), witness);
            if(attrs!=IntPtr.Zero) {
                if(attributesReady) result.AttemptCleanup(() => DeleteProcThreadAttributeList(attrs), witness);
                result.AttemptCleanup(() => Marshal.FreeHGlobal(attrs), witness);
            }
            if(list!=IntPtr.Zero) result.AttemptCleanup(() => Marshal.FreeHGlobal(list), witness);
            if(env!=IntPtr.Zero) result.AttemptCleanup(() => Marshal.FreeHGlobal(env), witness);
            foreach(var h in new[]{input,output,error}) if(Valid(h)) result.AttemptCleanup(() => witness.RequireNative(CloseHandle(h)), witness);
            if(job!=IntPtr.Zero) result.AttemptCleanup(() => witness.RequireNative(CloseHandle(job)), witness);
            result.Joined &= unassignedJoined;
            result.AttemptCleanup(() => {if(result.Joined) File.WriteAllText(Path.Combine(jobs,id+".joined"),"joined");}, witness);
        }
    }
    static bool RawWithinBounds(string root) {
        try {
            string raw=Path.Combine(root,"raw"); if(!Directory.Exists(raw)) return true;
            long bytes=0; int count=0;
            foreach(string f in Directory.EnumerateFiles(raw,"*",SearchOption.AllDirectories)) {
                var info=new FileInfo(f); if(++count>256 || info.Length>16L*1024*1024) return false;
                bytes+=info.Length; if(bytes>128L*1024*1024) return false;
            }
            return true;
        } catch {return false;}
    }
    public static bool ExistingJobEmpty(string name) {
        IntPtr job=OpenJobObject(4,false,name); if(job==IntPtr.Zero) return false; // Missing is unknown without a prior joined receipt.
        try {return Drain(job,30000);} finally {CloseHandle(job);}
    }
    static uint ObserveWait(IntPtr process,uint timeout,CommandWitness witness) {
        witness.Enter(CommandPhase.Wait);
        uint status=WaitForSingleObject(process,timeout);
        // WAIT_TIMEOUT is not a Win32 failure. Preserve the existing caller's wait/control flow.
        if(status==uint.MaxValue) witness.NoteNative(false);
        return status;
    }
    static bool Drain(IntPtr job,int timeout,CommandWitness witness=null) {
        var clock=Stopwatch.StartNew(); do {
            witness?.Enter(CommandPhase.JobDrain);
            bool queried=QueryInformationJobObject(job,1,out ACCOUNTING a,(uint)Marshal.SizeOf<ACCOUNTING>(),IntPtr.Zero);
            if(!queried) {witness?.NoteNative(false);return false;}
            if(a.ActiveProcesses==0) return true;
            Thread.Sleep(50); // Bounded process-join polling, not a test/UI readiness delay.
        } while(clock.ElapsedMilliseconds<timeout); return false;
    }
    static bool Valid(IntPtr h) {return h!=IntPtr.Zero && h!=(IntPtr)(-1);}
    static void Check(bool condition) {if(!condition) throw new InvalidOperationException("owned-command-failed");}
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES {public int length;public IntPtr descriptor;[MarshalAs(UnmanagedType.Bool)]public bool inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct STARTUPINFO {public int cb;public string reserved,desktop,title;public int x,y,width,height,xchars,ychars,fill,flags;public short show,reservedSize;public IntPtr reservedPointer,input,output,error;}
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX {public STARTUPINFO Startup;public IntPtr attributes;}
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION {public IntPtr process,thread;public uint processId,threadId;}
    [StructLayout(LayoutKind.Sequential)] struct BASIC_LIMITS {public long processTime,jobTime;public uint LimitFlags;public UIntPtr minWork,maxWork;public uint activeLimit;public UIntPtr affinity;public uint priority,scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS {public ulong readOps,writeOps,otherOps,readBytes,writeBytes,otherBytes;}
    [StructLayout(LayoutKind.Sequential)] struct EXTENDED_LIMITS {public BASIC_LIMITS Basic;public IO_COUNTERS io;public UIntPtr processMem,jobMem,peakProcess,peakJob;}
    [StructLayout(LayoutKind.Sequential)] struct ACCOUNTING {public long user,kernel,periodUser,periodKernel;public uint pageFaults,totalProcesses,ActiveProcesses,terminated;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr sa,string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr OpenJobObject(uint access,bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int type,ref EXTENDED_LIMITS info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int type,out ACCOUNTING info,uint length,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string app,StringBuilder command,IntPtr psa,IntPtr tsa,bool inherit,uint flags,IntPtr environment,string cwd,ref STARTUPINFOEX startup,out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateFile(string path,uint access,uint share,ref SECURITY_ATTRIBUTES sa,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileSizeEx(IntPtr file,out long size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]static extern void DeleteProcThreadAttributeList(IntPtr list);
}
}
