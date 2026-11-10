// Preparation-only coordination. No capture, debugger, process lookup, or memory API.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
namespace OpenClawObserve {
public enum BarrierDecision { Continue, AbortExpectedDenial, AbortInvalid }
public sealed class BarrierGate {
    public const int HandshakeSeconds=30, ExpectedAbortExit=126;
    readonly string root,nonce,source,model,collector,blame,mode;
    const string JobPrefix="Local\\OpenClawObserve-";
    readonly BarrierDeadline deadline; string job;
    public bool Entered, Released, Denied, HoldAcknowledged, Faulted;
    public int HostPid; public long HostCreation;
    public bool? BodySawAck;
    public BarrierGate(string root,string nonce,string source,string model,string collector,string blame,string mode)
        : this(root,nonce,source,model,collector,blame,mode,null) { }
    internal BarrierGate(string root,string nonce,string source,string model,string collector,string blame,string mode,Func<long> monotonicMilliseconds) {
        deadline=new BarrierDeadline(monotonicMilliseconds);
        BarrierFiles.Root(root); BarrierFiles.Nonce(nonce);
        foreach(var hash in new[]{source,model,collector,blame}) if(!Hex(hash,64)) throw new InvalidOperationException("barrier-hash");
        if(mode!="async" && mode!="sync" && mode!="deny") throw new InvalidOperationException("barrier-mode");
        this.root=root;this.nonce=nonce;this.source=source;this.model=model;this.collector=collector;this.blame=blame;this.mode=mode;
    }
    static bool Hex(string s,int length) => s!=null && s.Length==length && s.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));
    public void BindJob(string value) { if(job!=null || value==null || !value.StartsWith(JobPrefix,StringComparison.Ordinal) || !Hex(value.Substring(JobPrefix.Length),32)) throw new InvalidOperationException("barrier-job");job=value; }
    bool Token(string name) {var s=BarrierFiles.Read(root,name);if(s==null)return false;if(s!=nonce)throw new InvalidOperationException("barrier-nonce");return true;}
    void RequirePendingDeadline() {if(Entered && !Released && !HoldAcknowledged)deadline.Remaining();}
    public BarrierDecision Observe() {
        if(Faulted)return BarrierDecision.AbortInvalid;
        try {
            RequirePendingDeadline();
            if(job==null) throw new InvalidOperationException("barrier-unbound");
            if(BarrierFiles.Read(root,"failed")!=null || File.Exists(Path.Combine(root,"observer-invalid"))) throw new InvalidOperationException("barrier-invalid");
            var entry=BarrierFiles.Read(root,"entered");
            if(entry!=null) {
                var p=entry.Split('|');int pid;long birth;
                if(p.Length!=8 || p[0]!=nonce || !int.TryParse(p[1],NumberStyles.None,CultureInfo.InvariantCulture,out pid) || pid<=0 || !long.TryParse(p[2],NumberStyles.None,CultureInfo.InvariantCulture,out birth) || birth<=0 || p[3]!=source || p[4]!=model || p[5]!=collector || p[6]!=blame || p[7]!=job) throw new InvalidOperationException("barrier-entry");
                if(Entered && (HostPid!=pid || HostCreation!=birth)) throw new InvalidOperationException("barrier-rebind");
                if(!Entered)deadline.Start();
                Entered=true;HostPid=pid;HostCreation=birth;
            }
            RequirePendingDeadline();
            var body=BarrierFiles.Read(root,"body");
            RequirePendingDeadline();
            if(mode!="async" && !Released && BarrierFiles.Started(root,"body")) throw new InvalidOperationException("body-started-before-release");
            if(body!=null) {
                var p=body.Split('|');int pid;
                if(p.Length!=3 || p[0]!=nonce || !int.TryParse(p[1],NumberStyles.None,CultureInfo.InvariantCulture,out pid) || pid<=0 || (p[2]!="true"&&p[2]!="false") || (Entered && pid!=HostPid)) throw new InvalidOperationException("barrier-body");
                BodySawAck=p[2]=="true";
                if(mode!="async" && !Released) throw new InvalidOperationException("body-before-release");
            }
            if(Entered && !Released && mode=="async" && BodySawAck.HasValue) {
                if(BodySawAck.Value || Token("ack-ready")) throw new InvalidOperationException("async-control-missed");
                deadline.Remaining();BarrierFiles.Write(root,"release",nonce);deadline.Remaining();Released=true;
            } else if(Entered && !Released && mode=="sync") {deadline.Remaining();BarrierFiles.Write(root,"release",nonce);deadline.Remaining();Released=true;}
            if(Entered && mode=="deny") {
                if(BarrierFiles.Started(root,"body") || BarrierFiles.Started(root,"ack-ready") || Token("release")) throw new InvalidOperationException("denial-body");
                if(!Token("deny")) BarrierFiles.Write(root,"deny",nonce);
                if(Token("denied")) {
                    deadline.Remaining();Denied=true;
                    if(!Token("hold-challenge")) {deadline.Remaining();BarrierFiles.Write(root,"hold-challenge",nonce);deadline.Remaining();}
                    if(Token("hold-ack")) {deadline.Remaining();HoldAcknowledged=true;return BarrierDecision.AbortExpectedDenial;}
                }
            }
            RequirePendingDeadline();
            return BarrierDecision.Continue;
        } catch {Faulted=true;return BarrierDecision.AbortInvalid;}
    }
    public bool ValidateAfterJoin(int? exitCode,bool lifecycleSucceeded,bool expectedAbort) {
        if(!lifecycleSucceeded || Faulted || !Entered) return false;
        if(mode=="deny") return expectedAbort && exitCode==ExpectedAbortExit && Denied && HoldAcknowledged && !BarrierFiles.Started(root,"body") && !BarrierFiles.Started(root,"ack-ready");
        if(expectedAbort || exitCode!=0 || !Released) return false;
        if(Observe()!=BarrierDecision.Continue) return false;
        return BodySawAck.HasValue && BodySawAck.Value==(mode=="sync") && Token("ack-ready");
    }
}
public static class BarrierFiles {
    static readonly string[] Names={"entered","body","release","ack-ready","deny","denied","hold-challenge","hold-ack","failed"};
    public static void Root(string root) {
        if(!Path.IsPathFullyQualified(root) || !Directory.Exists(root))throw new InvalidOperationException("barrier-root");
        for(string p=Path.GetFullPath(root);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p)) if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("barrier-reparse");
    }
    public static void Nonce(string n) {if(n==null || n.Length!=32 || n.Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='f'))))throw new InvalidOperationException("barrier-nonce");}
    static string PathFor(string root,string name) {Root(root);if(!Names.Contains(name))throw new InvalidOperationException("barrier-name");return Path.Combine(root,name);}
    public static bool Started(string root,string name) {
        string p=PathFor(root,name);
        foreach(var candidate in new[]{p,p+".pending"}) {
            try {File.GetAttributes(candidate);return true;}
            catch(FileNotFoundException){} catch(DirectoryNotFoundException){}
        }
        return false; // An incomplete atomic write is evidence of started work, never absence.
    }
    public static string Read(string root,string name) {
        string p=PathFor(root,name);FileAttributes attributes;
        try {attributes=File.GetAttributes(p);} catch(FileNotFoundException){return null;} catch(DirectoryNotFoundException){return null;}
        if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new InvalidOperationException("barrier-file");
        using(var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if(f.Length>1024)throw new InvalidOperationException("barrier-limit");
            using(var reader=new StreamReader(f,new UTF8Encoding(false,true))) {var s=reader.ReadToEnd();if(s.Any(c=>c<32||c>126))throw new InvalidOperationException("barrier-content");return s;}
        }
    }
    public static void Write(string root,string name,string value) {
        string p=PathFor(root,name);if(value==null||value.Length>1024||value.Any(c=>c<32||c>126))throw new InvalidOperationException("barrier-content");
        string pending=p+".pending";
        using(var f=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {var b=Encoding.ASCII.GetBytes(value);f.Write(b,0,b.Length);f.Flush(true);}
        File.Move(pending,p); // No overwrite; readers see only a completed fixed record.
    }
    public static void WaitFor(string root,string nonce,params string[] names) {
        Root(root);Nonce(nonce);
        using(var changed=new AutoResetEvent(false)) using(var watcher=new FileSystemWatcher(root)) {
            watcher.NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite;watcher.Created+=(s,e)=>{try{changed.Set();}catch(ObjectDisposedException){}};watcher.Renamed+=(s,e)=>{try{changed.Set();}catch(ObjectDisposedException){}};watcher.Changed+=(s,e)=>{try{changed.Set();}catch(ObjectDisposedException){}};watcher.EnableRaisingEvents=true;
            WaitFor(root,nonce,null,remaining=>{if(!changed.WaitOne(remaining))throw new TimeoutException("barrier-handshake");},names);
        }
    }
    internal static void WaitFor(string root,string nonce,Func<long> monotonicMilliseconds,Action<int> wait,params string[] names) {
        Root(root);Nonce(nonce);var deadline=new BarrierDeadline(monotonicMilliseconds);deadline.Start();
        while(true) {
            deadline.Remaining(); // A signaled/visible record is not permission to consume an expired handshake.
            foreach(var name in names) {
                var value=Read(root,name);
                if(value!=null) {deadline.Remaining();if(value!=nonce)throw new InvalidOperationException("barrier-nonce");return;}
            }
            wait(deadline.Remaining());
        }
    }
    public static void HoldUntilOwnedAbort() {using(var never=new ManualResetEvent(false)) {while(true)never.WaitOne();}}
}
internal sealed class BarrierDeadline {
    readonly Func<long> now;long start,last;bool started,expired;
    internal BarrierDeadline(Func<long> monotonicMilliseconds=null) {
        if(monotonicMilliseconds!=null)now=monotonicMilliseconds;
        else {var clock=Stopwatch.StartNew();now=()=>clock.ElapsedMilliseconds;}
    }
    internal void Start() {
        if(started)throw new InvalidOperationException("barrier-clock-restart");
        start=last=now();started=true;
        if(start<0){expired=true;throw new TimeoutException("barrier-clock");}
    }
    internal int Remaining() {
        if(!started)throw new InvalidOperationException("barrier-clock-unstarted");
        if(expired)throw new TimeoutException("barrier-deadline");
        long value=now();
        if(value<last || value<start || value-start>=BarrierGate.HandshakeSeconds*1000L) {
            expired=true;throw new TimeoutException("barrier-deadline");
        }
        last=value;return (int)(BarrierGate.HandshakeSeconds*1000L-(value-start));
    }
}

}
