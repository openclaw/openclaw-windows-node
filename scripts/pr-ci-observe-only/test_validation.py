"""Pure tests for the actual public reader and fixed draft safety boundaries. No native execution."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import validate_report as v

ROOT = Path(__file__).resolve().parent

def sample():
    # Literal independent expected receipt. It is test data, not a claimed native result.
    roles = ["console", "build-task", "ui-assembly", "testhost-assembly", "object-model", "observer", "collector-corelib", "collector-coreclr", "host-image", "blame"]
    return dict(schema=1, phase="ObserveOnly", stage="complete", captureEnabled=False, captureAdmission="not-reviewed",
        productCommit="3bc3c0ccc71939fb2b760569bf438960e95767a7", productTree="b109f409607bdf05acbc0089d8c912f99e1c2429",
        pipelineCommit="a"*40, runId="1234", runAttempt="1", sdkVersion="10.0.400", producerContract="18.7", targetFrameworkMatched=True, architecture="x64",
        binaries=[dict(role=role, sha256="b"*64, version="18.0.0.0", binding="invocation-bound" if role == "console" else "evaluated" if role in {"build-task","ui-assembly","testhost-assembly"} else "framework-query" if role == "host-image" else "loaded") for role in roles],
        tools=[dict(role=role,status="missing",sha256=None,version=None,microsoftSignatureValid=False,architectureMatched=False,cliSchemaSha256=None,existingTesthostBoundary="unverified") for role in ["winapp","cdb","procdump"]],
        probe=dict(attempts=1,executed=1,passed=1,skipped=0,nativeExitCode=0,hosts=1,cases=1,sourceHashMatched=True,handleRetained=True,jobMatched=True),
        privacy=dict(privateAclVerified=True,outsideArtifactRoot=True,explicitUploadOnly=True),cleanup=dict(ownedProcessesJoined=True,privateRootDeleted=True),dumpFiles=0,observePassed=True,failure="none",setupExitCode=None,
        limits=["capture-not-reviewed","host-coreclr-not-remotely-observed","winapp-unavailable-or-untrusted","cdb-preparation-required","procdump-preparation-required"])

class ReceiptTests(unittest.TestCase):
    def reject(self, receipt):
        with self.assertRaises((v.Invalid,TypeError,ValueError)):
            v.validate(receipt)
    def test_valid_observation_still_cannot_admit_capture(self):
        r=json.loads(v.validate(sample()));self.assertTrue(r["observePassed"]);self.assertFalse(r["captureEnabled"]);self.assertEqual(r["captureAdmission"],"not-reviewed")
    def test_empty_early_failure_remains_honest(self):
        r=sample();r.update(observePassed=False,failure="preflight-incomplete",binaries=[],tools=[],dumpFiles=None,sdkVersion=None)
        r["probe"].update(attempts=0,executed=0,passed=0,nativeExitCode=None,hosts=0,cases=0,sourceHashMatched=False,handleRetained=False,jobMatched=False)
        self.assertFalse(json.loads(v.validate(r))["observePassed"])
    def test_unknown_private_fields_at_every_level_rejected(self):
        for target in [(),("probe",),("privacy",),("cleanup",),("binaries",0),("tools",0)]:
            with self.subTest(target=target):
                r=sample();obj=r
                for key in target:obj=obj[key]
                obj["privateName"]="PRIVATE TOKEN";self.reject(r)
    def test_identity_capture_and_missing_runtime_controls(self):
        for key,value in [("productCommit","d3ad564ba5fd7c9d05828475332dc15c0bf63926"),("productTree","2da8b80e046c78cdc803b48c7965ac2057b11da5"),("pipelineCommit","PRIVATE"),("runId","../../private"),("runAttempt","2"),("phase","Capture"),("stage","PRIVATE"),("stage","probe"),("captureEnabled",True),("captureAdmission","eligible"),("sdkVersion","PRIVATE"),("producerContract","18.8"),("producerContract",None),("targetFrameworkMatched",False),("architecture","arm64"),("dumpFiles",1),("setupExitCode",1),("failure","preflight-incomplete")]:
            with self.subTest(key=key):r=sample();r[key]=value;self.reject(r)
    def test_no_host_source_or_exit_false_success(self):
        for key,value in [("attempts",2),("executed",0),("passed",0),("skipped",1),("nativeExitCode",1),("nativeExitCode",-1073741189),("hosts",0),("hosts",2),("cases",2),("sourceHashMatched",False),("handleRetained",False),("jobMatched",False)]:
            with self.subTest(key=key):r=sample();r["probe"][key]=value;self.reject(r)
    def test_privacy_and_join_are_required(self):
        for group in ["privacy","cleanup"]:
            for key in sample()[group]:
                with self.subTest(group=group,key=key):r=sample();r[group][key]=False;self.reject(r)
    def test_binary_roles_types_digests_and_counts(self):
        for key,value in [("role","PRIVATE"),("sha256","C:\\private"),("version","1.0.0.0 PRIVATE"),("binding","guessed")]:
            r=sample();r["binaries"][0][key]=value;self.reject(r)
        r=sample();r["binaries"]*=2;self.reject(r)
        r=sample();r["binaries"].pop();self.reject(r)
        r=sample();r["binaries"][0]["binding"]="evaluated";self.reject(r)
    def test_tool_file_trust_not_fabricated(self):
        r=sample();r["tools"][0]["status"]="verified-file";self.reject(r)
        r=sample();r["tools"][1]["cliSchemaSha256"]="c"*64;self.reject(r)
        r=sample();r["tools"][0]["existingTesthostBoundary"]="supported-and-reviewed";self.reject(r)
    def test_boolean_and_nonfinite_types_rejected(self):
        for value in [True,1.0,float("nan"),float("inf"),"1",-1,1025]:
            r=sample();r["probe"]["hosts"]=value;self.reject(r)
        r=sample();r["captureEnabled"]=0;self.reject(r)
    def test_json_duplicates_caps_and_non_json_rejected(self):
        for data in [b'{"schema":1,"schema":1}',b'{"x":NaN}',b'x'*(32768+1),b'PRIVATE',b'']:
            with self.subTest(size=len(data)),self.assertRaises((v.Invalid,ValueError)):
                v.decode(data)
    def test_removed_or_unknown_limits_rejected(self):
        for values in [[],["PRIVATE"],["capture-not-reviewed"]]:
            r=sample();r["limits"]=values;self.reject(r)
    def test_cli_current_invocation_and_no_raw_failure_output(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            p=Path(directory);source=p/'in.json';target=p/'out.json';source.write_text(json.dumps(sample()))
            env=dict(os.environ,GITHUB_SHA="a"*40,GITHUB_RUN_ID="1234",GITHUB_RUN_ATTEMPT="1")
            command=[sys.executable,str(ROOT/'validate_report.py')]
            good=subprocess.run(command+[str(source),str(target)],env=env,capture_output=True,text=True)
            self.assertEqual(good.returncode,0);self.assertEqual(json.loads(target.read_text())["productCommit"],sample()["productCommit"])
            checked=subprocess.run(command+["--check",str(target)],env=env,capture_output=True,text=True);self.assertEqual(checked.returncode,0)
            env["GITHUB_RUN_ID"]="5678";bad=subprocess.run(command+["--check",str(target)],env=env,capture_output=True,text=True)
            self.assertNotEqual(bad.returncode,0);self.assertEqual(bad.stdout.strip(),"observe-receipt-rejected");self.assertEqual(bad.stderr,"")
            source.write_text('{"raw":"PRIVATE TOKEN"}');bad=subprocess.run(command+[str(source),str(p/'never.json')],env=env,capture_output=True,text=True)
            self.assertNotEqual(bad.returncode,0);self.assertFalse((p/'never.json').exists());self.assertNotIn("PRIVATE",bad.stdout+bad.stderr)

    def test_failed_cleanup_retains_known_or_unknown_native_exit(self):
        for code in [17,0,None]:
            with self.subTest(code=code):
                r=sample();r.update(observePassed=False,failure="preflight-incomplete",stage="probe")
                r["probe"]["nativeExitCode"]=code
                r["cleanup"].update(ownedProcessesJoined=False,privateRootDeleted=False)
                actual=json.loads(v.validate(r))
                self.assertEqual(actual["probe"]["nativeExitCode"],code)
                self.assertFalse(actual["observePassed"])
                r["observePassed"]=True;self.reject(r)

class DraftBoundaries(unittest.TestCase):
    def test_hard_no_capture_collector(self):
        s=(ROOT/'Observer/Observer.cs').read_text();b=(ROOT/'Observer/Binding.cs').read_text()
        self.assertIn('CaptureEnabled = false',b)
        for forbidden in ['Process.Start','MiniDumpWriteDump','ReadProcessMemory','DebugActiveProcess','ITestExecutionEnvironmentSpecifier','SendFileAsync','logger.Log','TestCaseName','CodeFilePath']:
            self.assertNotIn(forbidden,s)
        self.assertIn('QuerySynchronize = 0x00101000',s);self.assertIn('e.TestHostProcessId',s);self.assertIn('e.TestElement?.Source',s)
        self.assertIn('FileMode.CreateNew',s);self.assertIn('IsProcessInJob',s);self.assertIn('GetProcessTimes',s)
    def test_launch_is_suspended_and_owned_before_resume(self):
        s=(ROOT/'OwnedCommand.cs').read_text()
        self.assertLess(s.index('Check(CreateProcess('),s.index('Check(AssignProcessToJobObject('))
        self.assertLess(s.index('Check(AssignProcessToJobObject('),s.index('Check(ResumeThread('))
        self.assertIn('KillOnClose=0x2000',s);self.assertIn('0x20002',s);self.assertIn('a.ActiveProcesses==0',s)
        self.assertNotIn('GetProcessesByName',s);self.assertIn('RawWithinBounds',s)
    def test_exactly_one_product_fact_no_replay_or_debugger(self):
        s=(ROOT/'Invoke-ObserveOnly.ps1').read_text()
        self.assertEqual(s.count('/TestCaseFilter:'),1)
        self.assertIn('FixtureLaunchPathsAllUseTheIsolationHelper',s)
        self.assertIn('TestTimeout=300s;HangDumpType=None',s)
        self.assertNotIn('Category!=Accessibility',s);self.assertNotIn('--blame-crash',s)
        self.assertNotIn('d3ad564ba5fd7c9d05828475332dc15c0bf63926',s)
        self.assertLess(s.index("DiscoverTool 'winapp'"),s.index("@('--version') 30;$sdk"))
        self.assertNotIn('Run $t.path',s);self.assertNotIn('-accepteula',s)
    def test_exit_precedence_and_no_raw_console_fallback(self):
        s=(ROOT/'Invoke-ObserveOnly.ps1').read_text()
        self.assertIn('exit (Get-ObserveExitCode -Outcomes $nativeOutcomes -AdmissionPassed $report.observePassed)',s)
        policy=(ROOT/'ProbeAdmission.ps1').read_text()
        self.assertLess(policy.index('$Outcomes[$key] = $Result.ExitCode'),policy.index('if ($Result.LifecycleSucceeded'))
        self.assertLess(policy.index('return $Outcomes.probe'),policy.index('return $Outcomes.setup'))
        self.assertNotIn('Write-Host $_',s);self.assertNotIn('Exception.Message',s)
        self.assertIn('CleanupPrivate',s);self.assertIn('safe-report.pending',s)

    def test_known_exit_tail_and_finally_cannot_throw_cleanup(self):
        s=(ROOT/'OwnedCommand.cs').read_text()
        self.assertIn('public int? ExitCode { get; private set; }',s)
        self.assertNotIn('ExitCode=125',s)
        tail=s[s.index('result.RecordExit(unchecked((int)exit));'):s.index('    static bool RawWithinBounds')]
        self.assertIn('result.AttemptCleanup(() => {',tail)
        self.assertIn('catch (Exception) { CleanupFailed=true; }',s)
        self.assertIn('ExitCode.HasValue && Joined && !LimitHit && !CleanupFailed',s)
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text()
        run=entry[entry.index('function Run('):entry.index('function DiscoverTool')]
        self.assertLess(run.index('$script:nativeOutcomes[$key]=$null'),run.index('[OpenClawObserve.OwnedCommand]::Run('))
        self.assertIn('Receive-ObserveCommandResult -Result $r -Outcomes $script:nativeOutcomes',run)
        self.assertIn('$report.probe.nativeExitCode=$nativeOutcomes.probe',entry)
        self.assertIn('$report.setupExitCode=$nativeOutcomes.setup',entry)

    def test_actual_pinned_result_contract_and_real_start_are_used(self):
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text();policy=(ROOT/'ProbeAdmission.ps1').read_text()
        self.assertIn(". (Join-Path $ProductRoot 'scripts/TestResultContract.ps1')",policy)
        self.assertIn('Read-TestResultContract -Path $Path -StartedUtc $StartedUtc -ExitCode $ExitCode',policy)
        self.assertNotIn('-AllowSkipped',policy)
        self.assertNotIn('local-name()',policy+entry)
        self.assertLess(entry.index('$probeStartedUtc=[DateTime]::UtcNow'),entry.index("$r=Run $dotnet @('exec',$console"))
        self.assertIn('-StartedUtc $probeStartedUtc -ExitCode $probeExit -ExpectedFact $Fact',entry)
        self.assertIn("'/t:TestRun/t:Results//t:UnitTestResult[not(t:InnerResults)]'",policy)
        self.assertIn("$rows[0].GetAttribute('testName') -cne $ExpectedFact",policy)

    def test_native_negative_controls_are_wired_before_product_fact(self):
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text();test=(ROOT/'Test-ProbeAdmission.ps1').read_text()
        self.assertIn('$self.checks -ne 55',entry)
        self.assertIn('$admissionSelftest.policyCases -ne 5',entry)
        self.assertIn('$admissionSelftest.trxCases -ne 14',entry)
        self.assertLess(entry.index("'Test-ProbeAdmission.ps1'"),entry.index('$report.probe.attempts=1'))
        for case in ['wrong-root','wrong-namespace','nonzero-skips','nonzero-timeout','nonzero-error','stale-start','future-finish','reversed-times','missing-execution-id','actual-skip','wrong-fact','native-nonzero','native-unknown']:
            self.assertIn("name='"+case+"'",test)
        self.assertIn('$_.Exception.Message -ceq $case.prefix',test)

if __name__=='__main__':
    unittest.main(verbosity=2)
