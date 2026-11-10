"""Independent literal diagnostics/schema controls, never a native tool observation."""
import copy,hashlib,json,os,subprocess,sys,tempfile,unittest
from pathlib import Path
from unittest import mock
import project_format as f
import validate_format as v
ROOT=Path(__file__).resolve().parent
KINDS=('NUL','ESC','FF','otherCc','Cf','Cs','Co','Cn')
OLD_HASH='3543e973a357d949d68135ad96fe9d9da63f62d96664258a88d7ea4dad70411a'
GOLDEN=(ROOT.parent/'pr-ci-capture-preparation/fixtures/procdump-12.02-doc-contract.txt').read_bytes()

def sample():
    sem=f.semantic.project(b'X\x00','12.2.0.0')
    observation=dict(schema=1,semantic=sem,format=dict(state='observed',reason='none',bom='none',kinds={k:k=='NUL' for k in KINDS},occurrences='one'),stderr=dict(state='observed',presence='empty'),responseComparison='new-response')
    return dict(schema=1,mode='ToolHelpFormat',captureEnabled=False,captureAdmission='not-reviewed',qualificationPassed=False,priorEvidence=dict(kind='reference',runId='42753908952',receiptSha256='293ac5cab0771e5c6db6b3e84565f9e42ebbe49fa8158f9acad9a2cba56e0485'),productCommit='e56258fd0a835969b417f8d0236baf86760e3139',productTree='22ba737e3122d589787701a16ee2e2bbd234bdb0',pipelineCommit='a'*40,runId='1234',runAttempt='1',stage='complete',failure='qualification-unreviewed',observationComplete=True,tool=dict(attempted=True,identityState='matched',identityStable=True,archiveSha256='5fe68e81632f13188930affff20a9a9910170bae654360850d3108cead708a2d',sha256='22c2a2b09ade73a342a18dab838354385f73fc98e324b9903fd61fff949c5124',version='12.2.0.0',signatureVerified=True,architectureMatched=True,nativeExitCode=-1,lifecycleSucceeded=True,stdoutSha256=hashlib.sha256(b'X\x00').hexdigest()),observation=observation,setupExitCode=0,requestedWrapperExitCode=-1,failureWitness=None,privacy=dict(privateAclVerified=True,outsideArtifactRoot=True,explicitUploadOnly=True),cleanup=dict(ownedProcessesJoined=True,privateRootDeleted=True),dumpFiles=0)

class FormatTests(unittest.TestCase):
    def reject(self,value):
        with self.assertRaises((ValueError,TypeError)):v.validate(value)
    def observe(self,raw,stderr=b''):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            out=Path(tmp)/'stdout';err=Path(tmp)/'stderr';out.write_bytes(raw);err.write_bytes(stderr)
            result=f.observe(out,err,'12.2.0.0')
            self.assertEqual(result['semantic'],f.semantic.project_file(out,'12.2.0.0'))
            v.validate_observation(result);return result
    def test_literal_observation_is_not_qualification(self):
        r=json.loads(v.validate(sample()));self.assertTrue(r['observationComplete']);self.assertFalse(r['qualificationPassed']);self.assertFalse(r['captureEnabled']);self.assertEqual(r['requestedWrapperExitCode'],-1)
    def test_structural_control_kinds_are_exact(self):
        for char,key in [('\0','NUL'),('\x1b','ESC'),('\x0c','FF'),('\x0b','otherCc'),('\x7f','otherCc'),('\u0085','otherCc'),('\u202e','Cf'),('\ue000','Co'),('\u0378','Cn')]:
            with self.subTest(key=key):
                result=self.observe(('X'+char).encode());self.assertEqual(result['format']['kinds'],{k:k==key for k in KINDS});self.assertEqual(result['format']['occurrences'],'one');self.assertEqual(result['semantic']['reason'],'control-character')
    def test_surrogate_mapping_does_not_bypass_strict_decoder(self):
        kinds,bucket=f.classify_text('\ud800');self.assertEqual(kinds,{k:k=='Cs' for k in KINDS});self.assertEqual(bucket,'one')
        r=self.observe(('X\ud800').encode('utf-8','surrogatepass'));self.assertEqual(r['format']['state'],'encoding-failed');self.assertFalse(any(r['format']['kinds'].values()));self.assertEqual(r['semantic']['reason'],'encoding')
    def test_allowed_cr_lf_tab_not_reported_as_prohibited(self):
        r=self.observe(b'X\r\n\t');self.assertEqual(r['format']['occurrences'],'none');self.assertFalse(any(r['format']['kinds'].values()));self.assertNotEqual(r['semantic']['reason'],'control-character')
    def test_occurrence_bucket_saturates_without_counts_or_positions(self):
        r=self.observe(b'X\0\0\x1b\x0c');self.assertEqual(r['format']['occurrences'],'multiple');self.assertTrue(r['format']['kinds']['NUL']);self.assertTrue(r['format']['kinds']['ESC']);self.assertTrue(r['format']['kinds']['FF'])
    def test_all_bom_kinds_preserve_selected_decoder(self):
        for raw,bom,decoder in [(b'X\0','none','utf8'),(b'\xef\xbb\xbfX\0','utf8','utf8'),(b'\xff\xfe'+('X\0').encode('utf-16-le'),'utf16le','utf16le'),(b'\xfe\xff'+('X\0').encode('utf-16-be'),'utf16be','utf16be')]:
            r=self.observe(raw);self.assertEqual(r['format']['bom'],bom);self.assertEqual(r['semantic']['encoding'],decoder);self.assertTrue(r['format']['kinds']['NUL'])
    def test_no_bom_encoding_is_not_guessed(self):
        raw='X'.encode('utf-16-le');r=self.observe(raw);self.assertEqual(r['format']['bom'],'none');self.assertEqual(r['semantic']['encoding'],'utf8');self.assertTrue(r['format']['kinds']['NUL']);self.assertEqual(r['semantic']['reason'],'control-character')
    def test_encoding_failure_is_not_observed_none(self):
        for raw,bom in [(b'\xffBAD','none'),(b'\xff\xfe\0','utf16le'),(b'\xfe\xff\0','utf16be')]:
            r=self.observe(raw);self.assertEqual(r['format']['state'],'encoding-failed');self.assertEqual(r['format']['bom'],bom);self.assertEqual(r['format']['occurrences'],'unobserved')
    def test_empty_and_whitespace_only_guard_order_unchanged(self):
        for raw in [b'',b' \t\r\n',b'\x0c']:
            r=self.observe(raw);self.assertEqual(r['semantic']['status'],'empty');self.assertEqual(r['format']['state'],'observed')
        self.assertTrue(self.observe(b'\x0c')['format']['kinds']['FF'])
    def test_input_and_line_limits_remain_effective(self):
        for raw,reason in [(b'x'*(2097152+1),'input-limit'),(b'x'*1025,'line-limit'),(b'\n'*8193+b'x','line-limit')]:
            r=self.observe(raw);self.assertEqual(r['format']['state'],'limit');self.assertEqual(r['format']['reason'],reason);self.assertEqual(r['format']['occurrences'],'unobserved');self.assertEqual(r['semantic']['reason'],reason)
    def test_control_before_line_limit_is_not_reordered(self):
        r=self.observe(b'x'*1025+b'\0');self.assertEqual(r['semantic']['reason'],'control-character');self.assertTrue(r['format']['kinds']['NUL'])
    def test_stderr_only_presence_not_contents(self):
        for data,expected in [(b'','empty'),(b'PRIVATE TOKEN C:\\user','nonempty')]:
            r=self.observe(b'X',data);self.assertEqual(r['stderr'],dict(state='observed',presence=expected));self.assertNotIn('PRIVATE',json.dumps(r))
        r=self.observe(b'X',b'x'*2097153);self.assertEqual(r['stderr'],dict(state='limit',presence='unobserved'))
    def test_read_failures_are_finite_unobserved(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            r=f.observe(Path(tmp)/'PRIVATE_ABSENT',Path(tmp)/'PRIVATE_ERR','12.2.0.0');v.validate_observation(r);self.assertEqual(r['format']['state'],'read-failed');self.assertEqual(r['stderr']['state'],'read-failed');self.assertEqual(r['responseComparison'],'unobserved');self.assertNotIn('PRIVATE',json.dumps(r))
    def test_same_new_response_comparison_is_digest_only(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            out=Path(tmp)/'out';err=Path(tmp)/'err';raw=b'X\0';out.write_bytes(raw);err.write_bytes(b'')
            r=f.observe(out,err,'12.2.0.0');self.assertEqual(r['responseComparison'],'new-response')
            # This is a synthetic reference for the comparison branch, not the actual old response.
            with mock.patch.dict(f.IDENTITY,previousStdoutSha256=hashlib.sha256(raw).hexdigest()):
                r=f.observe(out,err,'12.2.0.0');v.validate_observation(r);self.assertEqual(r['responseComparison'],'same-response')
        self.assertEqual(f.IDENTITY['previousStdoutSha256'],OLD_HASH)
    def test_changed_read_snapshot_cannot_attribute_old_stream(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            out=Path(tmp)/'out';err=Path(tmp)/'err';out.write_bytes(b'X');err.write_bytes(b'')
            fake=f.format_bytes(b'Y')
            with mock.patch.object(f,'format_file',return_value=(fake,hashlib.sha256(b'Y').hexdigest(),1)):
                r=f.observe(out,err,'12.2.0.0');v.validate_observation(r);self.assertEqual(r['format']['reason'],'input-changed');self.assertEqual(r['responseComparison'],'unobserved')
    def test_golden_semantic_result_is_preserved_exactly(self):
        r=self.observe(GOLDEN);self.assertEqual(r['semantic']['status'],'recognized');self.assertFalse(r['semantic']['behaviorVerified']);self.assertEqual(r['format']['occurrences'],'none')
    def test_unobserved_is_not_empty(self):
        r=f.initial();v.validate_observation(r);self.assertEqual(r['format']['bom'],'unobserved');self.assertEqual(r['stderr']['presence'],'unobserved');self.assertNotEqual(r['format']['occurrences'],'none')
    def test_unknown_fields_or_codepoints_never_public(self):
        for route in [(),('tool',),('priorEvidence',),('observation',),('observation','format'),('observation','format','kinds'),('observation','stderr')]:
            r=sample();obj=r
            for part in route:obj=obj[part]
            obj['rawCodepointOrPath']='PRIVATE';self.reject(r)
    def test_format_state_types_are_strict(self):
        for key,value in [('state','PRIVATE'),('bom','ansi'),('occurrences',2),('reason','PRIVATE')]:
            r=sample();r['observation']['format'][key]=value;self.reject(r)
        r=sample();r['observation']['format']['kinds']['NUL']=1;self.reject(r)
    def test_inconsistent_none_or_multiple_kinds_rejected(self):
        r=sample();r['observation']['format']['occurrences']='none';self.reject(r)
        r=sample();r['observation']['format']['kinds']['ESC']=True;self.reject(r)
    def test_behavior_and_semantic_refusal_never_changed(self):
        r=sample();r['observation']['semantic']['behaviorVerified']=True;self.reject(r)
        r=sample();r['observation']['format']['kinds']['NUL']=False;r['observation']['format']['occurrences']='none';self.reject(r)
    def test_observation_success_guards_are_unmasked(self):
        self.assertTrue(json.loads(v.validate(sample()))['observationComplete'])
        for group,key,value in [('tool','lifecycleSucceeded',False),('tool','identityStable',False),('cleanup','ownedProcessesJoined',False),('privacy','privateAclVerified',False)]:
            r=sample();r[group][key]=value;self.reject(r)
        r=sample();r['observation']['responseComparison']='unobserved';self.reject(r)
    def test_identity_mismatch_cannot_be_attempted(self):
        for key,value in [('archiveSha256','a'*64),('sha256','b'*64),('version','12.3.0.0'),('signatureVerified',False),('architectureMatched',False)]:
            r=sample();r['tool'][key]=value;self.reject(r)
    def test_same_response_flag_cannot_be_forged(self):
        r=sample();r['observation']['responseComparison']='same-response';self.reject(r)
    def test_native_exit_and_requested_wrapper_remain_distinct(self):
        for native,setup,requested in [(-1,0,-1),(17,0,17),(0,0,1),(0,19,19),(None,None,1)]:
            r=sample();r['observationComplete']=False;r['failure']='observation-incomplete';r['tool']['nativeExitCode']=native;r['tool']['lifecycleSucceeded']=native is not None;r['setupExitCode']=setup;r['requestedWrapperExitCode']=requested
            actual=json.loads(v.validate(r));self.assertEqual(actual['tool']['nativeExitCode'],native);self.assertEqual(actual['requestedWrapperExitCode'],requested)
        r=sample();r['requestedWrapperExitCode']=0;self.reject(r)
    def test_never_qualifies_or_copies_fresh_barrier_cases(self):
        for key,value in [('qualificationPassed',True),('captureEnabled',True),('mode','PrepareAndBarrier')]:
            r=sample();r[key]=value;self.reject(r)
        r=sample();r['cases']=[];self.reject(r)
        r=sample();r['priorEvidence']['kind']='fresh-pass';self.reject(r)
    def test_digest_is_full_original_bytes(self):
        raw=b'\xef\xbb\xbfX\x00';r=self.observe(raw);self.assertEqual(r['semantic']['inputSha256'],hashlib.sha256(raw).hexdigest());self.assertNotEqual(r['semantic']['inputSha256'],hashlib.sha256(b'X').hexdigest())
    def test_projector_and_validator_clis_do_not_leak(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            root=Path(tmp);out=root/'stdout';err=root/'stderr';projection=root/'projection';out.write_bytes(b'X\0');err.write_bytes(b'PRIVATE')
            result=subprocess.run([sys.executable,str(ROOT/'project_format.py'),str(out),str(err),'12.2.0.0',str(projection)],capture_output=True,text=True);self.assertEqual(result.returncode,0);self.assertEqual(result.stdout.strip(),'tool-help-format-projected');self.assertEqual(result.stderr,'');self.assertNotIn('PRIVATE',projection.read_text())
            receipt=root/'receipt';safe=root/'safe';receipt.write_text(json.dumps(sample()));env=dict(os.environ,GITHUB_SHA='a'*40,GITHUB_RUN_ID='1234',GITHUB_RUN_ATTEMPT='1')
            result=subprocess.run([sys.executable,str(ROOT/'validate_format.py'),str(receipt),str(safe)],capture_output=True,text=True,env=env);self.assertEqual(result.returncode,0)
            env['GITHUB_RUN_ID']='999';bad=subprocess.run([sys.executable,str(ROOT/'validate_format.py'),'--check',str(safe)],capture_output=True,text=True,env=env);self.assertNotEqual(bad.returncode,0);self.assertEqual(bad.stdout.strip(),'tool-help-format-receipt-rejected');self.assertEqual(bad.stderr,'')
    def test_exact_identity_pins_are_not_human_approval(self):
        expected={'archiveSha256':'5fe68e81632f13188930affff20a9a9910170bae654360850d3108cead708a2d','executableSha256':'22c2a2b09ade73a342a18dab838354385f73fc98e324b9903fd61fff949c5124','fileVersion':'12.2.0.0','previousStdoutSha256':OLD_HASH,'priorRunId':'42753908952','priorReceiptSha256':'293ac5cab0771e5c6db6b3e84565f9e42ebbe49fa8158f9acad9a2cba56e0485'}
        for k,value in expected.items():self.assertEqual(f.IDENTITY[k],value)
        profile=json.loads((ROOT.parent/'pr-ci-capture-preparation/procdump-contract.json').read_text());self.assertFalse(profile['reviewed']);self.assertTrue(all(profile[k] is None for k in ['version','sha256','helpSha256','helpExitCode']))
    def test_workflow_and_entrypoint_are_help_only(self):
        base=ROOT.parents[1];self.assertFalse((base/'.github/workflows/inbox-capture-preparation.yml').exists());self.assertFalse((base/'.github/workflows/inbox-pr-observe-only.yml').exists())
        workflow=(base/'.github/workflows/inbox-procdump-help-candidates.yml').read_text();self.assertIn('path: tool-help-candidates-safe/safe-report.json',workflow);self.assertNotIn('workflow_dispatch',workflow);self.assertNotIn('setup-dotnet',workflow)
        entry=(ROOT.parent/'pr-ci-procdump-help-candidates/Invoke-ToolHelpCandidates.ps1').read_text();self.assertEqual(entry.count("Run $proc (Get-NoTargetArguments 'procdump')"),1)
        for forbidden in ['RunBarrier','Get-Command dotnet','TestCaseFilter','SelfTest','DiscoverTool',"Get-NoTargetArguments 'cdb'",'SkipValidation',"'-e'","'-ma'","'-cancel'","'-z'"]:self.assertNotIn(forbidden,entry)
        for guard in ["$tool.archiveSha256 -cne $pins.archiveSha256","$tool.sha256 -cne $pins.executableSha256","$tool.version -cne $pins.fileVersion",'MicrosoftSigned $proc','IsX64 $proc']:
            self.assertLess(entry.index(guard),entry.index("Run $proc (Get-NoTargetArguments 'procdump')"))
    def test_different_original_and_parser_snapshots_export_only_refusal(self):
        r=sample();r['tool']['stdoutSha256']='a'*64;self.reject(r)
        r.update(observationComplete=False,failure='observation-incomplete');r['observation']['format']=f.empty_format('read-failed','input-changed');r['observation']['responseComparison']='unobserved'
        actual=json.loads(v.validate(r));self.assertEqual(actual['tool']['stdoutSha256'],'a'*64);self.assertEqual(actual['observation']['semantic'],r['observation']['semantic']);self.assertFalse(actual['qualificationPassed'])
        r['observationComplete']=True;self.reject(r)

if __name__=='__main__':unittest.main(verbosity=2)
