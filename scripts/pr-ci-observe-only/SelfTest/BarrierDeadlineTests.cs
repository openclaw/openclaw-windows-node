using OpenClawObserve;
internal static class BarrierDeadlineTests {
    public static int Run(string parent) {
        string root=Path.Combine(parent,"barrier-deadline-policy");if(Directory.Exists(root))throw new InvalidOperationException("deadline-test-root");Directory.CreateDirectory(root);
        int cases=0,number=0;string nonce=new('a',32),hash=new('b',64),job="Local\\OpenClawObserve-"+new string('c',32);
        void Check(bool ok){if(!ok)throw new InvalidOperationException("deadline-selftest");}
        void Case(Action action){action();cases++;}
        string Fresh(){var p=Path.Combine(root,(++number).ToString());Directory.CreateDirectory(p);return p;}
        (BarrierGate Gate,string Root) New(string mode,Func<long> clock){var p=Fresh();var g=new BarrierGate(p,nonce,hash,hash,hash,hash,mode,clock);g.BindJob(job);BarrierFiles.Write(p,"entered",string.Join("|",nonce,"42","123",hash,hash,hash,hash,job));Check(g.Observe()==BarrierDecision.Continue);return(g,p);}
        void Denial(long elapsed,bool accepted){long now=0;var t=New("deny",()=>now);BarrierFiles.Write(t.Root,"denied",nonce);Check(t.Gate.Observe()==BarrierDecision.Continue);BarrierFiles.Write(t.Root,"hold-ack",nonce);now=elapsed;
            Check(t.Gate.Observe()==(accepted?BarrierDecision.AbortExpectedDenial:BarrierDecision.AbortInvalid));Check(t.Gate.HoldAcknowledged==accepted);
            Check(t.Gate.ValidateAfterJoin(126,true,true)==accepted);
            if(!accepted){now=0;Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);Check(!t.Gate.ValidateAfterJoin(126,true,true));}}
        void Async(long elapsed,bool accepted){long now=0;var t=New("async",()=>now);BarrierFiles.Write(t.Root,"body",nonce+"|42|false");now=elapsed;
            Check(t.Gate.Observe()==(accepted?BarrierDecision.Continue:BarrierDecision.AbortInvalid));Check(t.Gate.Released==accepted);Check(BarrierFiles.Started(t.Root,"release")==accepted);
            if(!accepted){now=0;Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);Check(!BarrierFiles.Started(t.Root,"release"));}}
        void VisibleAfterWait(long elapsed,bool accepted){var p=Fresh();long now=0;int waits=0;bool expired=false;
            try {BarrierFiles.WaitFor(p,nonce,()=>now,remaining=>{Check(remaining==30000);waits++;now=elapsed;BarrierFiles.Write(p,"release",nonce);},"release");}
            catch(TimeoutException){expired=true;}Check(expired!=accepted);Check(waits==1);Check(BarrierFiles.Read(p,"release")==nonce);}
        try {
            Case(()=>Denial(29999,true));Case(()=>Denial(30000,false));Case(()=>Denial(30001,false));
            Case(()=>Async(29999,true));Case(()=>Async(30000,false));Case(()=>Async(30001,false));
            Case(()=>VisibleAfterWait(29999,true));Case(()=>VisibleAfterWait(30000,false));Case(()=>VisibleAfterWait(30001,false));
            Case(()=>{var p=Fresh();BarrierFiles.Write(p,"release",nonce);int reads=0;bool expired=false;try{BarrierFiles.WaitFor(p,nonce,()=>++reads<3?0:30000,_=>throw new InvalidOperationException("must-not-wait"),"release");}catch(TimeoutException){expired=true;}Check(expired);});
            Case(()=>{long now=0;var t=New("deny",()=>now);now=30000;Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);BarrierFiles.Write(t.Root,"denied",nonce);BarrierFiles.Write(t.Root,"hold-ack",nonce);now=1;Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);Check(!t.Gate.HoldAcknowledged);});
            Case(()=>{long now=0;var t=New("async",()=>now);now=30001;Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);BarrierFiles.Write(t.Root,"body",nonce+"|42|false");now=1;Check(t.Gate.Observe()==BarrierDecision.AbortInvalid);Check(!t.Gate.Released);});
            Case(()=>{long now=10;var d=new BarrierDeadline(()=>now);d.Start();now=9;bool expired=false;try{d.Remaining();}catch(TimeoutException){expired=true;}Check(expired);now=11;expired=false;try{d.Remaining();}catch(TimeoutException){expired=true;}Check(expired);});
            Case(()=>{long now=0;var d=new BarrierDeadline(()=>now);d.Start();Check(d.Remaining()==30000);now=29999;Check(d.Remaining()==1);now=30000;bool expired=false;try{d.Remaining();}catch(TimeoutException){expired=true;}Check(expired);});
        } finally {Directory.Delete(root,true);}
        return cases;
    }
}
