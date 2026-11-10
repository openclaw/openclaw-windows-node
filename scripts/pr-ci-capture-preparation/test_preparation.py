"""Portable safe-output and fixed source-boundary controls; not native barrier proof."""
import copy,json,os,re,subprocess,sys,tempfile,unittest
from pathlib import Path
import validate_preparation as v
ROOT=Path(__file__).resolve().parent
SHARED=ROOT.parent/'pr-ci-observe-only'
def recognized_help():
    names=('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')
    return dict(schema=1,status='recognized',reason='none',grammarId='procdump-help-12.02-d46b5bfa',scope='documented-help-forms',behaviorVerified=False,inputSha256='1'*64,inputBytes=2108,encoding='utf8',helpMajor=12,helpMinor=2,versionAgreement=True,capabilities={k:True for k in names},clauses={k:'recognized' for k in names})
def sample():
    return dict(schema=2,mode='PrepareAndBarrier',captureEnabled=False,captureAdmission='not-reviewed',productCommit='e56258fd0a835969b417f8d0236baf86760e3139',productTree='22ba737e3122d589787701a16ee2e2bbd234bdb0',pipelineCommit='a'*40,runId='1234',runAttempt='1',stage='complete',failure='none',preparePassed=True,sdkVersion='10.0.403',producerContract='18.7',fixtureSha256='b'*64,observerSha256='c'*64,adapterSha256='d'*64,adapterVersion='3.1.5.0',winapp='missing',tools=[dict(role='procdump',status='contract-reviewed',sha256='e'*64,version='12.2.0.0',archiveSha256='f'*64,helpSha256='1'*64,nativeExitCode=0,signatureVerified=True,architectureMatched=True,helpProjection=recognized_help()),dict(role='cdb',status='cli-verified',sha256='2'*64,version='10.0.26100.9457',archiveSha256=None,helpSha256='3'*64,nativeExitCode=0,signatureVerified=True,architectureMatched=True,helpProjection=None)],cases=[dict(mode=mode,attempted=True,sourceBound=True,bodySawAck=False if mode=='async' else True if mode=='sync' else None,callbackHeld=mode=='deny',expectedAbort=mode=='deny',nativeExitCode=126 if mode=='deny' else 0,joined=True,executed=None if mode=='deny' else 1,passedCount=None if mode=='deny' else 1,skipped=None if mode=='deny' else 0,passed=True) for mode in ['async','sync','deny']],controls=dict(baselineManaged=True,baselinePowerShell=True,barrierPolicy=True,archivePolicy=True),privacy=dict(privateAclVerified=True,outsideArtifactRoot=True,explicitUploadOnly=True),cleanup=dict(ownedProcessesJoined=True,privateRootDeleted=True),dumpFiles=0,setupExitCode=0,failureWitness=None)
class PreparationTests(unittest.TestCase):
    def reject(self,r):
        with self.assertRaises((ValueError,TypeError)):v.validate(r)
    def test_literal_valid_report_never_admits_capture(self):
        r=json.loads(v.validate(sample()));self.assertTrue(r['preparePassed']);self.assertFalse(r['captureEnabled']);self.assertEqual(r['captureAdmission'],'not-reviewed')
    def test_unknown_fields_private_content_rejected(self):
        for route in [(),('cases',0),('tools',0),('controls',),('privacy',),('cleanup',)]:
            r=sample();p=r
            for key in route:p=p[key]
            p['privatePath']='PRIVATE TOKEN';self.reject(r)
    def test_exact_product_run_and_mode(self):
        for k,value in [('mode','Capture'),('captureEnabled',True),('captureAdmission','ready'),('productCommit','a'*40),('productTree','b'*40),('runAttempt','2'),('sdkVersion','10.0.404'),('producerContract','18.6'),('adapterVersion','3.1.6.0'),('runId','PRIVATE')]:
            r=sample();r[k]=value;self.reject(r)
    def test_unknown_tool_contract_cannot_pass(self):
        self.assertTrue(json.loads(v.validate(sample()))['preparePassed'])
        r=sample();r['tools'][0]['status']='unreviewed-contract';self.reject(r)
        r['preparePassed']=False;r['failure']='tool-contract-unreviewed';self.assertFalse(json.loads(v.validate(r))['preparePassed'])
    def test_each_barrier_property_is_independent_success_gate(self):
        self.assertTrue(json.loads(v.validate(sample()))['preparePassed'])
        for index,key,value in [(0,'bodySawAck',True),(1,'bodySawAck',False),(2,'bodySawAck',False),(2,'callbackHeld',False),(2,'expectedAbort',False),(2,'nativeExitCode',0),(2,'nativeExitCode',17),(2,'executed',0),(0,'sourceBound',False),(1,'joined',False),(0,'passedCount',0),(0,'skipped',1)]:
            r=sample();r['cases'][index][key]=value;self.reject(r)
    def test_case_count_order_and_attempt_are_bound(self):
        r=sample();r['cases'].reverse();self.reject(r)
        r=sample();r['cases'].append(r['cases'][0]);self.reject(r)
        r=sample();r['cases'][1]['attempted']=False;self.reject(r)
    def test_no_case_false_success_or_noop(self):
        for i in range(3):
            r=sample();r['cases'][i]['passed']=False;self.reject(r)
    def test_cleanup_dumps_and_controls_gate_success(self):
        for group in ['controls','privacy','cleanup']:
            for k in sample()[group]:
                r=sample();r[group][k]=False;self.reject(r)
        r=sample();r['dumpFiles']=1;self.reject(r)
    def test_failure_keeps_native17_zero_unknown_distinct(self):
        for code in [17,0,None]:
            r=sample();r.update(preparePassed=False,failure='preparation-incomplete');r['cases'][2].update(passed=False,nativeExitCode=code);r['setupExitCode']=code
            actual=json.loads(v.validate(r));self.assertEqual(actual['cases'][2]['nativeExitCode'],code);self.assertEqual(actual['setupExitCode'],code)
    def test_failure_witness_is_fixed_and_not_raw(self):
        r=sample();r.update(preparePassed=False,failure='preparation-incomplete');r['failureWitness']=dict(sourceStep='None',nativePhase='JobTerminate',category='NativeApi',win32ErrorAvailable=True,win32Error=5)
        self.assertEqual(json.loads(v.validate(r))['failureWitness']['win32Error'],5)
        for k,value in [('category','PRIVATE'),('win32Error',None),('nativePhase','Completed')]:
            n=copy.deepcopy(r);n['failureWitness'][k]=value;self.reject(n)
        r['failureWitness']['message']='PRIVATE';self.reject(r)
    def test_boolean_numeric_caps_and_tool_identity(self):
        for k,value in [('signatureVerified',1),('sha256','PRIVATE'),('version','PRIVATE'),('helpSha256',None),('nativeExitCode',True)]:
            r=sample();r['tools'][0][k]=value;self.reject(r)
        for b in [b'x'*32769,b'{"schema":1,"schema":1}',b'{"x":NaN}']:
            with self.assertRaises((ValueError,TypeError)):v.decode(b)
    def test_actual_cli_only_outputs_fixed_failure(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            p=Path(tmp);source=p/'input';target=p/'out';source.write_text(json.dumps(sample()),encoding='utf-8');env=dict(os.environ,GITHUB_SHA='a'*40,GITHUB_RUN_ID='1234',GITHUB_RUN_ATTEMPT='1')
            args=[sys.executable,str(ROOT/'validate_preparation.py')]
            ok=subprocess.run(args+[str(source),str(target)],env=env,text=True,capture_output=True);self.assertEqual(ok.returncode,0);self.assertTrue(json.loads(target.read_text())['preparePassed'])
            env['GITHUB_RUN_ID']='999';bad=subprocess.run(args+['--check',str(target)],env=env,text=True,capture_output=True);self.assertNotEqual(bad.returncode,0);self.assertEqual(bad.stdout.strip(),'preparation-receipt-rejected');self.assertEqual(bad.stderr,'')
    def test_only_new_workflow_with_exact_safe_upload(self):
        root=ROOT.parents[1];self.assertFalse((root/'.github/workflows/inbox-pr-observe-only.yml').exists())
        s=(root/'.github/workflows/inbox-capture-preparation.yml').read_text();self.assertIn('ref: e56258fd0a835969b417f8d0236baf86760e3139',s);self.assertNotIn('setup-dotnet',s);self.assertIn('10.0.403 SDK',s);self.assertIn('path: preparation-safe/safe-report.json',s);self.assertNotIn('workflow_dispatch',s);self.assertNotIn('TestResults',s);self.assertNotIn('matrix:',s)
    def test_no_target_tool_arguments_and_no_monitor(self):
        module=(ROOT/'PrivateTools.ps1').read_text();entry=(ROOT/'Invoke-CapturePreparation.ps1').read_text()
        self.assertIn("return ,@('-accepteula','-?')",module);self.assertIn("return ,@('-version')",module)
        self.assertEqual(entry.count("Run $proc (Get-NoTargetArguments 'procdump')"),1);self.assertEqual(entry.count("Run $cdb.path (Get-NoTargetArguments 'cdb')"),1)
        for forbidden in ["@('-e'","@('-ma'","@('-p'","@('-z'","'-cancel'","'-i'","'-t'","'-e1'","ReadProcessMemory","MiniDumpWriteDump","DebugActiveProcess"]:self.assertNotIn(forbidden,module+entry)
        self.assertIn("'DOTNET_DbgEnableMiniDump']='0'",entry);self.assertIn("'COMPlus_DbgEnableMiniDump']='0'",entry)
    def test_archive_has_bounded_single_official_origin(self):
        s=(ROOT/'PrivateTools.ps1').read_text();self.assertIn('https://download.sysinternals.com/files/Procdump.zip',s);self.assertIn('$handler.AllowAutoRedirect=$false',s);self.assertIn('$PreparationDownloadBytes=16MB',s);self.assertIn('$PreparationExpandedBytes=32MB',s);self.assertIn('$PreparationArchiveMembers=8',s);self.assertIn('StringComparer]::OrdinalIgnoreCase',s);self.assertIn('$mode -notin @(0,0x8000)',s);self.assertNotIn('ExtractToDirectory',s)
        self.assertLess(s.index('$plan=Get-PreparationArchivePlan'),s.index('foreach($entry in $plan)'))
    def test_fixture_body_does_not_supply_the_expected_timing(self):
        s=(ROOT/'Fixture/BarrierFact.cs').read_text();self.assertNotIn('OPENCLAW_BARRIER_MODE',s);self.assertNotIn('Sleep',s);self.assertIn('BarrierFiles.Read(root,"ack-ready")',s);self.assertIn('BarrierFiles.Write(root,"body"',s)
        entry=(ROOT/'Invoke-CapturePreparation.ps1').read_text();self.assertIn("if($case.mode -ne 'async')",entry);self.assertIn("CreateElement('ParallelizeTestCollections')",entry)
        project=(ROOT/'Fixture/Fixture.csproj').read_text();self.assertIn('xunit" Version="2.9.3"',project);self.assertIn('xunit.runner.visualstudio" Version="3.1.5"',project);self.assertIn('Microsoft.NET.Test.Sdk" Version="18.6.0"',project)
    def test_denial_requires_live_held_callback_not_just_error_marker(self):
        collector=(ROOT/'Collector/Observer.cs').read_text();gate=(SHARED/'BarrierGate.cs').read_text();owner=(SHARED/'OwnedCommand.cs').read_text()
        self.assertIn('catch (BarrierDeniedException)',collector);self.assertIn('BarrierFiles.WaitFor(barrierRoot,invocation,"hold-challenge")',collector);self.assertIn('BarrierFiles.Write(barrierRoot,"hold-ack",invocation)',collector);self.assertIn('BarrierFiles.HoldUntilOwnedAbort()',collector)
        self.assertIn('if(Token("hold-ack")) {deadline.Remaining();HoldAcknowledged=true;return BarrierDecision.AbortExpectedDenial;}',gate)
        self.assertIn('expectedAbort && exitCode==ExpectedAbortExit',gate);self.assertIn('!BarrierFiles.Started(root,"body")',gate)
        self.assertIn('TerminateJobObject(job,(uint)(result.BarrierExpectedAbort?BarrierGate.ExpectedAbortExit:125))',owner);self.assertNotIn('GetProcessesByName',owner+gate);self.assertNotIn('DllImport',gate);self.assertIn('p+\".pending\"',gate);self.assertLess(owner.index('if(clock.Elapsed.TotalSeconds>seconds'),owner.index('var action=barrier.Observe()'))
    def test_case_matrix_and_native_bounds_are_fixed(self):
        s=(ROOT/'Invoke-CapturePreparation.ps1').read_text();self.assertIn("cases=@((CaseRecord 'async'),(CaseRecord 'sync'),(CaseRecord 'deny'))",s);self.assertEqual(s.count('::RunBarrier('),1);self.assertIn('$BarrierCommandSeconds=120',s);self.assertIn('$private,$BarrierCommandSeconds,$gate,$commandWitness)',s);self.assertIn('TestTimeout=300s;HangDumpType=None',s);self.assertNotIn('Category!=Accessibility',s);self.assertNotIn('FixtureLaunchPathsAllUseTheIsolationHelper',s)
        self.assertIn('const int HandshakeSeconds=30, ExpectedAbortExit=126',(SHARED/'BarrierGate.cs').read_text())
    def test_contract_is_unreviewed_not_guessed(self):
        p=json.loads((ROOT/'procdump-contract.json').read_text());self.assertFalse(p['reviewed']);self.assertIsNone(p['version']);self.assertIsNone(p['sha256']);self.assertIsNone(p['helpSha256']);self.assertIsNone(p['helpExitCode'])
        s=(ROOT/'Invoke-CapturePreparation.ps1').read_text();self.assertIn("'tool-contract-unreviewed'",s);self.assertIn('$allCases -and $toolContract -and $cdbStarted',s)
    def test_preserved_control_gates_remain_separate(self):
        s=(ROOT/'Invoke-CapturePreparation.ps1').read_text()
        for n in ['$resolver[0].cases -ne 26','$self.checks -ne 93','$self.configurationCases -ne 24','$self.custodyCases -ne 12','$self.blameIdentityCases -ne 30','$control.policyCases -ne 5','$control.trxCases -ne 14','$control[0].cases -ne 18','$control[0].blameCases -ne 6','$self.barrierPolicyCases -ne 19','$archiveControls[0].cases -ne 26']:self.assertIn(n,s)
    def test_tool_cleanup_failure_retains_known_exit_before_receipt(self):
        for code in [17,0,None]:
            r=sample();r.update(preparePassed=False,failure='preparation-incomplete',stage='tools');r['tools'][0].update(status='unreviewed-contract',nativeExitCode=code,helpSha256=None,helpProjection=__import__('project_procdump_help').initial())
            self.assertEqual(json.loads(v.validate(r))['tools'][0]['nativeExitCode'],code)
        entry=(ROOT/'Invoke-CapturePreparation.ps1').read_text()
        self.assertIn('Save-PreparationToolFailure -Tool $activeTool -Outcomes $nativeOutcomes',entry)
        self.assertLess(entry.index('$activeTool=$tool\n    $r=Run $proc'),entry.index("$tool.nativeExitCode=$r.ExitCode"))
        self.assertIn('if($null -ne $firstUnexpectedExit){exit $firstUnexpectedExit}',entry)
        tests=(ROOT/'Test-PreparationPolicies.ps1').read_text();self.assertEqual(len(re.findall(r'^Case \{',tests,re.M)),26)

if __name__=='__main__':unittest.main(verbosity=2)
