"""Pure tests for the actual public reader and fixed draft safety boundaries. No native execution."""
import copy
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import validate_report as v

ROOT = Path(__file__).resolve().parent

def sample():
    # Literal independent expected receipt. It is test data, not a claimed native result.
    roles = ["console", "build-task", "ui-assembly", "testhost-assembly", "object-model", "observer", "collector-corelib", "collector-coreclr", "host-image", "blame"]
    return dict(schema=2, failureWitness=None, phase="ObserveOnly", stage="complete", captureEnabled=False, captureAdmission="not-reviewed",
        productCommit="e56258fd0a835969b417f8d0236baf86760e3139", productTree="22ba737e3122d589787701a16ee2e2bbd234bdb0",
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
    def test_previous_product_identity_is_rejected(self):
        # Current literal fixture must first pass. Each old-source mutation fails at identity admission.
        self.assertTrue(json.loads(v.validate(sample()))["observePassed"])
        for previous in [
            {"productCommit":"3bc3c0ccc71939fb2b760569bf438960e95767a7"},
            {"productTree":"b109f409607bdf05acbc0089d8c912f99e1c2429"},
            {"productCommit":"3bc3c0ccc71939fb2b760569bf438960e95767a7","productTree":"b109f409607bdf05acbc0089d8c912f99e1c2429"},
        ]:
            with self.subTest(previous=previous):
                receipt=sample();receipt.update(previous);self.reject(receipt)
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
            self.assertEqual(good.returncode,0);self.assertEqual(json.loads(target.read_text(encoding="utf-8"))["productCommit"],sample()["productCommit"])
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

    def test_witness_requires_exact_enums_fields_and_error_availability(self):
        base=sample();base.update(observePassed=False,failure="preflight-incomplete",stage="source-binding")
        base["failureWitness"]=dict(sourceStep="ProductHead",nativePhase="ProcessCreate",category="NativeApi",win32ErrorAvailable=True,win32Error=5)
        self.assertEqual(json.loads(v.validate(base))["failureWitness"]["win32Error"],5)
        for key,value in [("sourceStep","PRIVATE path"),("nativePhase","PRIVATE command"),("category","PRIVATE exception"),("win32ErrorAvailable",1),("win32Error",True),("win32Error","5"),("win32Error",0),("win32Error",None),("win32Error",2**31),("win32Error",float("nan"))]:
            with self.subTest(key=key,value=value):
                r=copy.deepcopy(base);r["failureWitness"][key]=value;self.reject(r)
        r=copy.deepcopy(base);r["failureWitness"]["message"]="PRIVATE";self.reject(r)
        r=copy.deepcopy(base);r["failureWitness"]["nativePhase"]="Preparation";self.reject(r)
        r=copy.deepcopy(base);r["failureWitness"]["category"]="Argument";self.reject(r)
        r=copy.deepcopy(base);r["observePassed"]=True;r["failure"]="none";r["stage"]="complete";self.reject(r)

    def test_unknown_and_managed_error_do_not_invent_native_error(self):
        for category,phase in [("NativeApi","ProcessCreate"),("Argument","Preparation"),("MethodBinding","None"),("Other","None")]:
            r=sample();r.update(observePassed=False,failure="preflight-incomplete",stage="source-binding")
            r["failureWitness"]=dict(sourceStep="ProductHead",nativePhase=phase,category=category,win32ErrorAvailable=False,win32Error=None)
            self.assertIsNone(json.loads(v.validate(r))["failureWitness"]["win32Error"])
            r["failureWitness"]["win32Error"]=87;self.reject(r)

    def test_witness_keeps_17_zero_and_unknown_exit_distinct(self):
        for code in [17,0,None]:
            r=sample();r.update(observePassed=False,failure="preflight-incomplete",stage="source-binding")
            r["probe"]["nativeExitCode"]=code;r["setupExitCode"]=code
            r["failureWitness"]=dict(sourceStep="ProductHead",nativePhase="ProcessCreate",category="NativeApi",win32ErrorAvailable=True,win32Error=5)
            actual=json.loads(v.validate(r));self.assertEqual(actual["probe"]["nativeExitCode"],code)
            self.assertEqual(actual["setupExitCode"],code);self.assertEqual(actual["failureWitness"]["win32Error"],5)
        r=sample();r.pop("failureWitness");r["schema"]=1;self.reject(r)

class DraftBoundaries(unittest.TestCase):
    def test_current_product_pins_are_exact(self):
        commit="e56258fd0a835969b417f8d0236baf86760e3139"
        tree="22ba737e3122d589787701a16ee2e2bbd234bdb0"
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        workflow=(ROOT.parents[1]/'.github/workflows/inbox-pr-observe-only.yml').read_text(encoding="utf-8")
        self.assertIn("$ProductCommit = '"+commit+"'",entry)
        self.assertIn("$ProductTree = '"+tree+"'",entry)
        self.assertIn('          ref: '+commit,workflow)
        self.assertEqual((v.COMMIT,v.TREE),(commit,tree))
        self.assertEqual((sample()["productCommit"],sample()["productTree"]),(commit,tree))
    def test_hard_no_capture_collector(self):
        s=(ROOT/'Observer/Observer.cs').read_text(encoding="utf-8");b=(ROOT/'Observer/Binding.cs').read_text(encoding="utf-8")
        self.assertIn('CaptureEnabled = false',b)
        for forbidden in ['Process.Start','MiniDumpWriteDump','ReadProcessMemory','DebugActiveProcess','ITestExecutionEnvironmentSpecifier','SendFileAsync','logger.Log','TestCaseName','CodeFilePath']:
            self.assertNotIn(forbidden,s)
        self.assertIn('QuerySynchronize = 0x00101000',s);self.assertIn('e.TestHostProcessId',s);self.assertIn('e.TestElement?.Source',s)
        self.assertIn('FileMode.CreateNew',s);self.assertIn('IsProcessInJob',s);self.assertIn('GetProcessTimes',s)
    def test_launch_is_suspended_and_owned_before_resume(self):
        s=(ROOT/'OwnedCommand.cs').read_text(encoding="utf-8")
        self.assertLess(s.index('witness.RequireNative(CreateProcess('),s.index('witness.RequireNative(AssignProcessToJobObject('))
        self.assertLess(s.index('witness.RequireNative(AssignProcessToJobObject('),s.index('witness.RequireNative(ResumeThread('))
        self.assertIn('KillOnClose=0x2000',s);self.assertIn('0x20002',s);self.assertIn('a.ActiveProcesses==0',s)
        self.assertNotIn('GetProcessesByName',s);self.assertIn('RawWithinBounds',s)
    def test_exactly_one_product_fact_no_replay_or_debugger(self):
        s=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        self.assertEqual(s.count('/TestCaseFilter:'),1)
        self.assertIn('FixtureLaunchPathsAllUseTheIsolationHelper',s)
        self.assertIn('TestTimeout=300s;HangDumpType=None',s)
        self.assertNotIn('Category!=Accessibility',s);self.assertNotIn('--blame-crash',s)
        self.assertNotIn('d3ad564ba5fd7c9d05828475332dc15c0bf63926',s)
        self.assertLess(s.index("DiscoverTool 'winapp'"),s.index("@('--version') 30;$sdk"))
        self.assertNotIn('Run $t.path',s);self.assertNotIn('-accepteula',s)
    def test_exit_precedence_and_no_raw_console_fallback(self):
        s=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        self.assertIn('exit (Get-ObserveExitCode -Outcomes $nativeOutcomes -AdmissionPassed $report.observePassed)',s)
        policy=(ROOT/'ProbeAdmission.ps1').read_text(encoding="utf-8")
        self.assertLess(policy.index('$Outcomes[$key] = $Result.ExitCode'),policy.index('if ($Result.LifecycleSucceeded'))
        self.assertLess(policy.index('return $Outcomes.probe'),policy.index('return $Outcomes.setup'))
        self.assertNotIn('Write-Host $_',s);self.assertNotIn('Exception.Message',s)
        self.assertIn('CleanupPrivate',s);self.assertIn('safe-report.pending',s)

    def test_known_exit_tail_and_finally_cannot_throw_cleanup(self):
        s=(ROOT/'OwnedCommand.cs').read_text(encoding="utf-8")
        self.assertIn('public int? ExitCode { get; private set; }',s)
        self.assertNotIn('ExitCode=125',s)
        tail=s[s.index('result.RecordExit(unchecked((int)exit));'):s.index('    static bool RawWithinBounds')]
        self.assertIn('result.AttemptCleanup(() => {',tail)
        self.assertIn('CleanupFailed=true; try {witness?.RecordManaged(error);} catch { }',s)
        self.assertIn('ExitCode.HasValue && Joined && !LimitHit && !CleanupFailed',s)
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        run=entry[entry.index('function Run('):entry.index('function DiscoverTool')]
        self.assertLess(run.index('$script:nativeOutcomes[$key]=$null'),run.index('[OpenClawObserve.OwnedCommand]::Run('))
        self.assertIn('Receive-ObserveCommandResult -Result $r -Outcomes $script:nativeOutcomes',run)
        self.assertIn('$report.probe.nativeExitCode=$nativeOutcomes.probe',entry)
        self.assertIn('$report.setupExitCode=$nativeOutcomes.setup',entry)

    def test_actual_pinned_result_contract_and_real_start_are_used(self):
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8");policy=(ROOT/'ProbeAdmission.ps1').read_text(encoding="utf-8")
        self.assertIn(". (Join-Path $ProductRoot 'scripts/TestResultContract.ps1')",policy)
        self.assertIn('Read-TestResultContract -Path $Path -StartedUtc $StartedUtc -ExitCode $ExitCode',policy)
        self.assertNotIn('-AllowSkipped',policy)
        self.assertNotIn('local-name()',policy+entry)
        self.assertLess(entry.index('$probeStartedUtc=[DateTime]::UtcNow'),entry.index("$r=Run $dotnet @('exec',$console"))
        self.assertIn('-StartedUtc $probeStartedUtc -ExitCode $probeExit -ExpectedFact $Fact',entry)
        self.assertIn("'/t:TestRun/t:Results//t:UnitTestResult[not(t:InnerResults)]'",policy)
        self.assertIn("$rows[0].GetAttribute('testName') -cne $ExpectedFact",policy)

    def test_native_negative_controls_are_wired_before_product_fact(self):
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8");test=(ROOT/'Test-ProbeAdmission.ps1').read_text(encoding="utf-8")
        self.assertIn('$self.checks -ne 93',entry)
        self.assertIn('$admissionSelftest.policyCases -ne 5',entry)
        self.assertIn('$admissionSelftest.trxCases -ne 14',entry)
        self.assertLess(entry.index("'Test-ProbeAdmission.ps1'"),entry.index('$report.probe.attempts=1'))
        for case in ['wrong-root','wrong-namespace','nonzero-skips','nonzero-timeout','nonzero-error','stale-start','future-finish','reversed-times','missing-execution-id','actual-skip','wrong-fact','native-nonzero','native-unknown']:
            self.assertIn("name='"+case+"'",test)
        self.assertIn('$_.Exception.Message -ceq $case.prefix',test)

    def test_failure_witness_is_passive_and_captured_before_cleanup(self):
        s=(ROOT/'OwnedCommand.cs').read_text(encoding="utf-8");entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        self.assertIn('int error=Marshal.GetLastPInvokeError();',s)
        self.assertIn('Win32Error=error==0 ? (int?)null : error;',s)
        managed=s[s.index('public void RecordManaged('):s.index('public sealed class CommandResult')]
        self.assertNotIn('GetLastPInvokeError',managed)
        for prohibited in ['.Message','.Data','.StackTrace','.HResult','.InnerException']:
            self.assertNotIn(prohibited,managed)
        self.assertIn('if(status==uint.MaxValue) witness.NoteNative(false);',s)
        self.assertIn('return status;',s)
        self.assertIn('catch(Exception failure) {try {witness.RecordManaged(failure);} catch { } throw;} finally',s)
        self.assertIn('$private,$Seconds,$script:commandWitness)',entry)
        self.assertIn('$report.failureWitness=@{sourceStep=$sourceStep;',entry)
        self.assertIn('catch { } # Evidence failure must not replace',entry)
        for step in ['GitResolve','ProductHead','PipelineHead','ProductClean','PipelineClean','ProductTree','DotnetResolve','DotnetIdentity','DotnetVersion']:
            self.assertIn("'"+step+"'",entry)

    def test_application_selection_validates_before_string_coercion(self):
        policy=(ROOT/'ApplicationResolution.ps1').read_text(encoding="utf-8")
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        self.assertIn('param($Candidates,',policy)
        self.assertLess(policy.index('$items.Count -ne 1'),policy.index('$application = $items[0]'))
        self.assertLess(policy.index('-isnot [System.Management.Automation.ApplicationInfo]'),policy.index('$source = $application.Source'))
        self.assertLess(policy.index('$source -isnot [string]'),policy.index('[IO.Path]::IsPathFullyQualified($source)'))
        self.assertLess(policy.index('[IO.File]::Exists($source)'),policy.index('& $CanonicalPath $source'))
        self.assertIn('return $source',policy)
        self.assertNotIn('$source.Trim',policy)
        for name in ('git','dotnet'):
            self.assertIn('$'+name+'=Select-ObserveApplicationSource -Candidates @(Get-Command '+name+' -CommandType Application -TotalCount 1 -ErrorAction Stop)',entry)
            self.assertNotIn('(Get-Command '+name+' -CommandType Application -ErrorAction Stop).Source',entry)
        self.assertNotIn('[OpenClawObserve.OwnedCommand]::Run',policy)

    def test_resolver_controls_precede_winapp_and_owned_launch(self):
        entry=(ROOT/'Invoke-ObserveOnly.ps1').read_text(encoding="utf-8")
        start=entry.index("$resolverSelftest=@(& (Join-Path $here 'Test-ApplicationResolution.ps1')")
        self.assertLess(entry.index('PrivateAcl $private'),start)
        self.assertLess(start,entry.index("$win=DiscoverTool 'winapp'"))
        self.assertLess(start,entry.index('$r=Run $win.path'))
        self.assertIn('$resolverSelftest.Count -ne 1',entry)
        self.assertIn('$resolverSelftest[0].cases -ne 26',entry)
        self.assertIn('$resolverSelftest[0].nativeProcessesOpened -ne 0',entry)
        self.assertIn("Write-Host 'observe-resolver-controls-passed:26'",entry)

    def test_resolver_fixture_is_source_bound_and_nonlaunching(self):
        controls=(ROOT/'Test-ApplicationResolution.ps1').read_text(encoding="utf-8")
        self.assertIn('Get-Content -LiteralPath $EntrypointPath -Raw',controls)
        self.assertIn("$parameters -ceq '[string]$Exe,[string[]]$Arguments,[int]$Seconds=900,[switch]$AllowFailure'",controls)
        self.assertIn("'param(' + $parameters",controls)
        self.assertIn("param($capture,$git,$pair,$ProductRoot) & $capture ",controls)
        self.assertIn(".Substring('Run '.Length)",controls)
        self.assertIn('$calls[0].Groups[1].Value',controls)
        self.assertIn('$result[0].argv.Count -eq 4',controls)
        self.assertIn('$result[0].cwd -ceq $ProductRoot',controls)
        self.assertIn('$arraySource.Source -is [array]',controls)
        self.assertIn('$audit.calls -eq $prior',controls)
        self.assertIn('LegacyScalar @($first, $second)',controls)
        self.assertNotRegex(controls,r'(?m)^\s*(?:\$\w+\s*=\s*)?\[OpenClawObserve\.OwnedCommand\]::Run\(')
        for forbidden in ('Process.Start(', 'Start-Process', 'Invoke-Expression', 'SetEnvironmentVariable', '$env:'):
            self.assertNotIn(forbidden,controls)
        self.assertIn('nativeProcessesOpened = 0',controls)
        self.assertIn('finally {\n    Remove-Item -LiteralPath $PrivateRoot -Recurse -Force',controls)

    def test_resolver_quote_golden_literals_and_separate_count(self):
        controls=(ROOT/'Test-ApplicationResolution.ps1').read_text(encoding="utf-8")
        actual=re.findall(r"@\{ value = '((?:''|[^'])*)'; expected = '((?:''|[^'])*)' \}",controls)
        expected=[('', '""'),('plain','"plain"'),('C:\\Program Files\\Git\\cmd\\git.exe','"C:\\Program Files\\Git\\cmd\\git.exe"'),('a"b','"a\\"b"'),('C:\\Tools\\git.exe','"C:\\Tools\\git.exe"'),('C:\\with space\\','"C:\\with space\\\\"'),('a\\"b','"a\\\\\\"b"'),('C:\\工具\\git.exe','"C:\\工具\\git.exe"')]
        self.assertEqual(actual,expected)
        self.assertEqual(len(re.findall(r'^    Case \{',controls,re.M))+len(actual),26)
        self.assertIn('[OpenClawObserve.OwnedCommand]::Quote($case.value)',controls)


    def test_unicode_source_is_utf8_even_with_legacy_default(self):
        # The file boundary supplies a Windows-style legacy default, not expected text.
        # Exercise the actual golden-source reader and its unchanged literal expectations.
        original_read = Path.read_text
        observed = []
        def legacy_default(path, *args, **kwargs):
            observed.append(kwargs.get("encoding"))
            kwargs.setdefault("encoding", "cp1252")
            return original_read(path, *args, **kwargs)
        with mock.patch.object(Path, "read_text", legacy_default):
            self.test_resolver_quote_golden_literals_and_separate_count()
        self.assertEqual(observed, ["utf-8"])

if __name__=='__main__':
    unittest.main(verbosity=2)
