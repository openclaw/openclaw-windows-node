using OpenClawObserve;
internal static class BarrierGateTests {
    public static int Run(string parent) {
        var root=Path.Combine(parent,"barrier-policy");if(Directory.Exists(root))throw new InvalidOperationException("barrier-test-root");Directory.CreateDirectory(root);
        int cases=0,number=0;string nonce=new('a',32),hash=new('b',64),job="Local\\OpenClawObserve-"+new string('c',32);
        void Check(bool ok){if(!ok)throw new InvalidOperationException("barrier-policy-selftest");}
        void Case(Action a){a();cases++;}
        (BarrierGate Gate,string Root) New(string mode){var p=Path.Combine(root,(++number).ToString());Directory.CreateDirectory(p);var g=new BarrierGate(p,nonce,hash,hash,hash,hash,mode);g.BindJob(job);return(g,p);}
        string Entry(string n=null,string source=null,string jobValue=null) => string.Join("|",n??nonce,"42","123",source??hash,hash,hash,hash,jobValue??job);
        void Enter((BarrierGate Gate,string Root) t){BarrierFiles.Write(t.Root,"entered",Entry());}
        try {
            Case(()=>{var t=New("async");BarrierFiles.Write(t.Root,"body",nonce+"|42|false");Check(t.Gate.Observe()==BarrierDecision.Continue&&!t.Gate.Released);Enter(t);Check(t.Gate.Observe()==BarrierDecision.Continue&&t.Gate.Released);BarrierFiles.Write(t.Root,"ack-ready",nonce);Check(t.Gate.ValidateAfterJoin(0,true,false));});
            Case(()=>{var t=New("sync");Enter(t);Check(t.Gate.Observe()==BarrierDecision.Continue&&t.Gate.Released);BarrierFiles.Write(t.Root,"ack-ready",nonce);BarrierFiles.Write(t.Root,"body",nonce+"|42|true");Check(t.Gate.ValidateAfterJoin(0,true,false));});
            Case(()=>{var t=New("deny");Enter(t);Check(t.Gate.Observe()==BarrierDecision.Continue);BarrierFiles.Write(t.Root,"denied",nonce);Check(t.Gate.Observe()==BarrierDecision.Continue);Check(BarrierFiles.Read(t.Root,"hold-challenge")==nonce);BarrierFiles.Write(t.Root,"hold-ack",nonce);Check(t.Gate.Observe()==BarrierDecision.AbortExpectedDenial);Check(t.Gate.ValidateAfterJoin(126,true,true));Check(!t.Gate.ValidateAfterJoin(17,true,true));Check(!t.Gate.ValidateAfterJoin(null,true,true));Check(!t.Gate.ValidateAfterJoin(126,false,true));});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"body",nonce+"|42|false");Enter(t);Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("deny");Enter(t);t.Gate.Observe();BarrierFiles.Write(t.Root,"denied",nonce);BarrierFiles.Write(t.Root,"body",nonce+"|42|false");Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("async");Enter(t);BarrierFiles.Write(t.Root,"body",nonce+"|42|true");Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"entered",Entry(new string('d',32)));Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"entered",Entry(source:new string('d',64)));Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"entered",Entry(jobValue:"Local\\OpenClawObserve-"+new string('d',32)));Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("async");Enter(t);BarrierFiles.Write(t.Root,"body",nonce+"|99|false");Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("deny");Enter(t);BarrierFiles.Write(t.Root,"release",nonce);Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"failed",nonce);Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("sync");Check(!t.Gate.ValidateAfterJoin(0,true,false));});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"release",nonce);bool failed=false;try{BarrierFiles.Write(t.Root,"release",nonce);}catch(IOException){failed=true;}Check(failed);});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"release",nonce);BarrierFiles.WaitFor(t.Root,nonce,"release");Check(true);});
            Case(()=>{var t=New("sync");BarrierFiles.Write(t.Root,"release",new string('d',32));bool failed=false;try{BarrierFiles.WaitFor(t.Root,nonce,"release");}catch(InvalidOperationException){failed=true;}Check(failed);});
            Case(()=>{var t=New("deny");bool failed=false;try{t.Gate.BindJob(job);}catch(InvalidOperationException){failed=true;}Check(failed);});
            Case(()=>{var t=New("sync");File.WriteAllText(Path.Combine(t.Root,"entered"),new string('x',1025));Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);});
            Case(()=>{var t=New("deny");Enter(t);t.Gate.Observe();File.WriteAllText(Path.Combine(t.Root,"body.pending"),nonce);Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);Check(!t.Gate.ValidateAfterJoin(126,true,true));});
        } finally {Directory.Delete(root,true);}
        return cases;
    }
}
