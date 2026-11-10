"""Literal document/encoding and receipt controls. None is observed native output."""
import copy,hashlib,io,json,os,re,subprocess,sys,tempfile,unittest
from pathlib import Path
from unittest import mock
import project_candidates as p
import validate_candidates as v
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'pr-ci-procdump-help-format'))
import test_format as oldtests
ROOT=Path(__file__).resolve().parent
GOLDEN=(ROOT.parent/'pr-ci-capture-preparation/fixtures/procdump-12.02-doc-contract.txt').read_bytes()
# Literal independent fixtures use the retained published document, not production constants.
TEXT=GOLDEN.decode('utf-8')
VERSION='12.2.0.0'

def observe(raw,err=b''):
    with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
        a=Path(tmp)/'stdout';b=Path(tmp)/'stderr';a.write_bytes(raw);b.write_bytes(err)
        result=p.observe(a,b,VERSION);v.validate_observation(result);return result

def receipt(raw=None,err=b''):
    if raw is None:raw=TEXT.encode('utf-16-le')
    out=oldtests.sample();out.update(mode='ToolHelpCandidates',transportApproved=False,formatEvidence=copy.deepcopy(p.FORMAT_REFERENCE),observation=observe(raw,err))
    out['tool'].update(stdoutSha256=hashlib.sha256(raw).hexdigest(),stderrSha256=hashlib.sha256(err).hexdigest())
    return out

def pick(o,id,stream='stdout'):return next(c for c in o[stream]['candidates'] if c['id']==id)

def literal_stream_owned_candidate(identifier,state):
    # Independent finite receipt fixture, not the production initializer or decision.
    kinds={'NUL':False,'ESC':False,'FF':False,'otherCc':False,'Cf':False,'Cs':False,'Co':False,'Cn':False}
    clauses={name:'unobserved' for name in ('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')}
    phases=dict(versionCount='unobserved',versionState='unobserved',major=None,minor=None,build=None,revision=None,versionAgreement=None,usageCount='unobserved',usageForm='unobserved',clauses=clauses)
    analysis=dict(scope='derived-analysis-input',encoding='utf8',inputSha256=None,inputBytes=None,status='unobserved',reason='none')
    return dict(id=identifier,state=state,bomPolicy='unobserved',decode='unobserved',roundTrip='unobserved',kinds=kinds,occurrences='unobserved',limits='unobserved',phases=phases,analysis=analysis,conformant=False)

class CandidateTests(unittest.TestCase):
    def reject(self,r):
        with self.assertRaises((ValueError,TypeError)):v.validate(r)
    def test_utf8_literal_document_unique_not_approval(self):
        r=receipt(GOLDEN);v.validate(r);self.assertEqual(r['observation']['stdout']['conformance'],{'count':'unique','id':'utf8'});self.assertFalse(r['transportApproved']);self.assertFalse(r['qualificationPassed'])
    def test_no_bom_little_endian_literal_document(self):
        r=receipt();v.validate(r);o=r['observation'];self.assertEqual(o['stdout']['conformance'],{'count':'unique','id':'utf16le'});self.assertEqual(o['stdout']['bom'],'none');self.assertEqual(o['baseline']['semantic']['reason'],'control-character');self.assertEqual(pick(o,'utf16le')['bomPolicy'],'no-bom-hypothesis')
    def test_no_bom_big_endian_literal_document(self):
        o=observe(TEXT.encode('utf-16-be'));self.assertEqual(o['stdout']['conformance'],{'count':'unique','id':'utf16be'});self.assertEqual(o['baseline']['semantic']['encoding'],'utf8')
    def test_utf8_embedded_nul_not_stripped(self):
        o=observe(GOLDEN+b'\0');c=pick(o,'utf8');self.assertEqual(c['state'],'control-rejected');self.assertTrue(c['kinds']['NUL']);self.assertFalse(c['conformant']);self.assertEqual(o['stdout']['conformance']['count'],'none')
    def test_every_matching_explicit_bom_roundtrips(self):
        for id,bom,codec in [('utf8',b'\xef\xbb\xbf','utf-8'),('utf16le',b'\xff\xfe','utf-16-le'),('utf16be',b'\xfe\xff','utf-16-be')]:
            raw=bom+TEXT.encode(codec);o=observe(raw);c=pick(o,id);self.assertTrue(c['conformant']);self.assertEqual(c['roundTrip'],'passed');self.assertEqual(c['bomPolicy'],'explicit-matching');self.assertEqual(o['stdout']['inputSha256'],hashlib.sha256(raw).hexdigest())
    def test_conflicting_boms_ineligible_not_fallback(self):
        for raw in [b'\xef\xbb\xbfX',b'\xff\xfeX\0',b'\xfe\xff\0X']:
            o=observe(raw)
            for c in o['stdout']['candidates']:
                if c['id']!=o['stdout']['bom']:self.assertEqual(c['state'],'bom-conflict');self.assertEqual(c['decode'],'unobserved')
    def test_odd_length_utf16_refuses_before_decode(self):
        o=observe(b'ABC');self.assertEqual(pick(o,'utf16le')['state'],'odd-length');self.assertEqual(pick(o,'utf16be')['state'],'odd-length')
    def test_truncated_matching_bom_payload(self):
        for raw,id in [(b'\xff\xfeX','utf16le'),(b'\xfe\xffX','utf16be')]:self.assertEqual(pick(observe(raw),id)['state'],'odd-length')
    def test_unpaired_surrogates_fail_strict_decode(self):
        for raw,id in [(b'\x00\xd8','utf16le'),(b'\x00\xdc','utf16le'),(b'\xd8\x00','utf16be'),(b'\xdc\x00','utf16be'),(b'\x00\xd8A\0','utf16le')]:self.assertEqual(pick(observe(raw),id)['state'],'decode-failed')
    def test_valid_surrogate_pair_roundtrip_not_contract(self):
        for codec,id in [('utf-16-le','utf16le'),('utf-16-be','utf16be')]:
            c=pick(observe('\U0001f600'.encode(codec)),id);self.assertEqual(c['roundTrip'],'passed');self.assertFalse(c['conformant'])
    def test_invalid_utf8_not_replacement_decoded(self):
        for raw in [b'\xff',b'\xc0\x80',b'\xed\xa0\x80',b'\xf0\x9f']:
            self.assertEqual(pick(observe(raw),'utf8')['state'],'decode-failed')
    def test_both_endian_roundtrip_does_not_make_both_conform(self):
        o=observe(TEXT.encode('utf-16-le'));self.assertEqual(pick(o,'utf16le')['roundTrip'],'passed');self.assertEqual(pick(o,'utf16be')['roundTrip'],'passed');self.assertTrue(pick(o,'utf16le')['conformant']);self.assertFalse(pick(o,'utf16be')['conformant'])
    def test_all_control_categories_are_finite_only(self):
        for char,kind in [('\0','NUL'),('\x1b','ESC'),('\x0c','FF'),('\x0b','otherCc'),('\u202e','Cf'),('\ue000','Co'),('\u0378','Cn')]:
            c=pick(observe(('X'+char).encode()),'utf8');self.assertEqual(c['state'],'control-rejected');self.assertEqual(c['kinds'],{k:k==kind for k in p.KINDS});self.assertEqual(c['occurrences'],'one')
    def test_no_terminator_or_whitespace_control_trim(self):
        for suffix in ['\0','\x0c','\x1b']:
            c=pick(observe((TEXT+suffix).encode('utf-16-le')),'utf16le');self.assertEqual(c['state'],'control-rejected')
    def test_empty_and_none_conformance(self):
        o=observe(b'');self.assertEqual(o['stdout']['inputBytes'],0);self.assertEqual(o['stdout']['conformance'],{'count':'none','id':None});self.assertEqual(pick(o,'utf8')['phases']['versionCount'],'zero')
    def test_summary_unique_multiple_none_without_selection(self):
        # Projection seam controls, not claims that one real stream encodes two valid contracts.
        for values,expected in [([False,False,False],{'count':'none','id':None}),([False,True,False],{'count':'unique','id':'utf16le'}),([True,True,False],{'count':'multiple','id':None})]:
            cs=[dict(id=id,conformant=value) for id,value in zip(('utf8','utf16le','utf16be'),values)];self.assertEqual(p.summarize(cs),expected)
    def test_missing_banner_preserves_clause_observations(self):
        no_banner=TEXT.split('\n',1)[1];c=pick(observe(no_banner.encode()),'utf8');self.assertEqual(c['phases']['versionState'],'absent');self.assertEqual(c['phases']['usageForm'],'valid');self.assertEqual(set(c['phases']['clauses'].values()),{'recognized'});self.assertFalse(c['conformant']);self.assertEqual(c['analysis']['reason'],'missing-version')
    def test_banner_on_stderr_never_combined(self):
        banner,rest=TEXT.split('\n',1);o=observe(rest.encode(),(banner+'\n').encode());self.assertEqual(pick(o,'utf8')['phases']['versionState'],'absent');self.assertEqual(pick(o,'utf8','stderr')['phases']['versionState'],'expected');self.assertEqual(pick(o,'utf8','stderr')['phases']['usageCount'],'zero');self.assertTrue(all(s['conformance']['count']=='none' for s in [o['stdout'],o['stderr']]))
    def test_complete_each_stream_independent_no_global_selection(self):
        r=receipt(GOLDEN,TEXT.encode('utf-16-be'));v.validate(r);o=r['observation'];self.assertEqual(o['stdout']['conformance']['id'],'utf8');self.assertEqual(o['stderr']['conformance']['id'],'utf16be');self.assertNotIn('selected',o);self.assertFalse(o['transportApproved'])
    def test_missing_usage_still_has_nonusage_clauses(self):
        text=TEXT.replace(next(line for line in TEXT.splitlines() if line.startswith('procdump.exe [')),'').replace('Capture Usage:','');c=pick(observe(text.encode()),'utf8');self.assertEqual(c['phases']['usageCount'],'zero');self.assertEqual(c['phases']['clauses']['acceptEula'],'recognized');self.assertFalse(c['conformant'])
    def test_duplicate_malformed_version_declarations_never_vanish(self):
        for extra,count,state in [('ProcDump v13.bad\n','multiple','ambiguous'),('ProcDump v12.02\n','multiple','ambiguous')]:
            c=p.candidate((extra+TEXT).encode(),'utf8',VERSION);self.assertEqual(c['phases']['versionCount'],count);self.assertEqual(c['phases']['versionState'],state);self.assertFalse(c['conformant'])
        c=p.candidate(TEXT.replace('ProcDump v12.02','ProcDump v13.bad').encode(),'utf8',VERSION);self.assertEqual(c['phases']['versionCount'],'one');self.assertEqual(c['phases']['versionState'],'malformed')
    def test_two_version_tokens_on_one_line_ambiguous(self):
        c=pick(observe(TEXT.replace('ProcDump v12.02','ProcDump v12.02 ProcDump v13.bad').encode()),'utf8');self.assertEqual(c['phases']['versionCount'],'multiple');self.assertFalse(c['conformant'])
    def test_numeric_version_mismatch_and_unsupported(self):
        for version,state,agree in [('13.02','mismatch',False),('12.02.0','unsupported',True),('99999.02','unsupported',None)]:
            c=pick(observe(TEXT.replace('12.02',version,1).encode()),'utf8');self.assertEqual(c['phases']['versionState'],state);self.assertEqual(c['phases']['versionAgreement'],agree);self.assertFalse(c['conformant'])
    def test_duplicate_capture_and_malformed_tokens_preserve_guards(self):
        for bad in [TEXT.replace('Dump Types:','Usage: procdump.exe [-ma] [-e] [-n <Count>] [-at <Timeout>] <PID>\nDump Types:'),TEXT.replace('[-g]','[-g]WRONG',1),TEXT.replace('Capture Usage:','Capture Usage WRONG:')]:
            c=pick(observe(bad.encode()),'utf8');self.assertIn(c['phases']['usageForm'],{'invalid','ambiguous'});self.assertFalse(c['conformant'])
    def test_unknown_clause_wording_and_duplicates_not_conformant(self):
        for bad in [TEXT.replace('Number of dumps to write before exiting.','Some other words.'),TEXT+'\n-n Number of dumps to write before exiting.\n']:
            c=pick(observe(bad.encode()),'utf8');self.assertNotEqual(c['phases']['clauses']['dumpCount'],'recognized');self.assertFalse(c['conformant'])
    def test_phase_clause_owner_is_exact_extraction(self):
        owner=(ROOT.parent/'pr-ci-capture-preparation/project_procdump_help.py').read_text();start=owner.index('    stripped=[normal(s) for s in lines]\n',owner.index('def project('));block=owner[start:owner.index('    return result\n',start)]
        self.assertIn(block,(ROOT/'phases.py').read_text())
        for text in [TEXT,TEXT.replace('[-g]','[-g]WRONG'),TEXT.replace('Number of dumps to write before exiting.','unknown')]:self.assertEqual(p.phases.inspect(text,VERSION)['clauses'],p.semantic.project(text.encode(),VERSION)['clauses'])
    def test_raw_and_derived_identity_are_explicit(self):
        raw=b'\xff\xfe'+TEXT.encode('utf-16-le');o=observe(raw);c=pick(o,'utf16le');self.assertEqual(o['stdout']['inputSha256'],hashlib.sha256(raw).hexdigest());self.assertEqual(c['analysis']['inputSha256'],hashlib.sha256(GOLDEN).hexdigest());self.assertNotEqual(c['analysis']['inputSha256'],o['stdout']['inputSha256']);self.assertEqual(c['analysis']['scope'],'derived-analysis-input');self.assertEqual(o['stdout']['bom'],'utf16le')
    def test_input_limit_no_semantic_processing(self):
        o=observe(b'x'*2097153);self.assertEqual(o['stdout']['inputBytes'],2097153);self.assertTrue(all(c['state']=='input-limit' for c in o['stdout']['candidates']));self.assertEqual(o['stdout']['inputSha256'],hashlib.sha256(b'x'*2097153).hexdigest())
    def test_line_and_line_count_bounds(self):
        for raw in [b'x'*1025,b'x\n'*8193]:self.assertEqual(pick(observe(raw),'utf8')['state'],'line-limit')
    def test_derived_expansion_refuses_not_raised_cap(self):
        text=('\u4e00'*1000+'\n')*750;raw=text.encode('utf-16-le');self.assertLess(len(raw),2097152);self.assertGreater(len(text.encode()),2097152);c=pick(observe(raw),'utf16le');self.assertEqual(c['state'],'derived-limit');self.assertIsNone(c['analysis']['inputSha256'])
    def test_full_file_limit_no_partial_digest(self):
        # Bounded read seam exercises the real cap without writing 64MiB on disk.
        class Stream:
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self,n):return b'x'*65536
        with mock.patch.object(Path,'open',return_value=Stream()):r=p.stream_file('unused',VERSION)
        self.assertEqual(r,p.empty_stream('file-limit'))
    def test_read_failure_finite_no_path(self):
        r=p.observe(ROOT/'PRIVATE-ABSENT',ROOT/'PRIVATE-ERR',VERSION);v.validate_observation(r);self.assertEqual(r['stdout']['state'],'read-failed');self.assertNotIn('PRIVATE',json.dumps(r))
    def test_input_changed_snapshot_cannot_bind_old_digest(self):
        original=p.baseline.observe
        def changed(*args):
            r=original(*args);r['semantic']['inputSha256']='a'*64;return r
        with mock.patch.object(p.baseline,'observe',side_effect=changed):o=observe(b'X')
        self.assertEqual(o['stdout']['state'],'input-changed')
    def test_baseline_verdict_exact_for_all_interpretations(self):
        for raw in [GOLDEN,TEXT.encode('utf-16-le'),TEXT.encode('utf-16-be'),b'\xef\xbb\xbf'+GOLDEN,b'X\0',b'\xff']:
            o=observe(raw);self.assertEqual(o['baseline']['semantic'],p.semantic.project(raw,VERSION))
    def test_unobserved_initial_schema(self):v.validate_observation(p.initial())
    def test_candidate_ids_exact_three_no_unknown_interpretation(self):
        for action in ('remove','append','rename'):
            r=receipt();cs=r['observation']['stdout']['candidates']
            if action=='remove':cs.pop()
            elif action=='append':cs.append(copy.deepcopy(cs[0]))
            else:cs[0]['id']='ansi'
            self.reject(r)
    def test_unknown_fields_no_content_export(self):
        for path in [(),('observation',),('observation','stdout'),('observation','stdout','candidates',1),('observation','stdout','candidates',1,'phases'),('observation','stdout','candidates',1,'analysis'),('observation','stderr')]:
            r=receipt();item=r
            for key in path:item=item[key]
            item['rawText']='PRIVATE';self.reject(r)
    def test_derived_metadata_type_and_scope_mutants(self):
        for key,value in [('scope','original-output'),('inputBytes',2097153),('inputSha256','PRIVATE'),('encoding','utf16le')]:
            r=receipt();r['observation']['stdout']['candidates'][1]['analysis'][key]=value;self.reject(r)
    def test_conformance_summary_cannot_be_forged(self):
        for value in [{'count':'multiple','id':'utf16le'},{'count':'unique','id':'utf8'},{'count':'none','id':None}]:
            r=receipt();r['observation']['stdout']['conformance']=value;self.reject(r)
    def test_roundtrip_only_cannot_claim_conformance(self):
        r=receipt();c=r['observation']['stdout']['candidates'][2];c['conformant']=True;r['observation']['stdout']['conformance']={'count':'multiple','id':None};self.reject(r)
    def test_complete_phase_guards_unmasked(self):
        self.assertTrue(json.loads(v.validate(receipt()))['observationComplete'])
        for key,value in [('versionState','malformed'),('usageForm','invalid'),('versionAgreement',False)]:
            r=receipt();r['observation']['stdout']['candidates'][1]['phases'][key]=value;self.reject(r)
        r=receipt();r['observation']['stdout']['candidates'][1]['phases']['clauses']['dumpCount']='unrecognized';self.reject(r)
    def test_lifecycle_success_guards_unmasked(self):
        self.assertTrue(json.loads(v.validate(receipt()))['observationComplete'])
        for group,key,value in [('tool','lifecycleSucceeded',False),('tool','identityStable',False),('cleanup','ownedProcessesJoined',False),('cleanup','privateRootDeleted',False),('privacy','privateAclVerified',False),('privacy','outsideArtifactRoot',False),('privacy','explicitUploadOnly',False)]:
            r=receipt();r[group][key]=value;self.reject(r)
        for key,value in [('dumpFiles',1),('stage','project')]:r=receipt();r[key]=value;self.reject(r)
    def test_two_stream_tool_digest_binding(self):
        for name in ('stdout','stderr'):
            r=receipt();r['tool'][name+'Sha256']='a'*64;self.reject(r)
    def test_unknown_states_types_and_numeric_bounds(self):
        for key,value in [('state','PRIVATE'),('decode',True),('roundTrip','ok'),('limits','ignored'),('occurrences',2),('conformant',1)]:
            r=receipt();r['observation']['stdout']['candidates'][1][key]=value;self.reject(r)
        r=receipt();r['observation']['stdout']['candidates'][1]['phases']['major']=65536;self.reject(r)
    def test_admission_flags_never_become_true(self):
        for key in ['captureEnabled','qualificationPassed','transportApproved']:
            r=receipt();r[key]=True;self.reject(r)
        for key in ['transportApproved','behaviorVerified']:
            r=receipt();r['observation'][key]=True;self.reject(r)
    def test_prior_evidence_only_immutable_references(self):
        for key in ['priorEvidence','formatEvidence']:
            r=receipt();r[key]['kind']='fresh-pass';self.reject(r)
        r=receipt();r['cases']=[];self.reject(r)
    def test_native_exit_not_assumed_success_convention(self):
        for exit,expected in [(-1,-1),(0,1),(17,17)]:
            r=receipt();r['tool']['nativeExitCode']=exit;r['requestedWrapperExitCode']=expected;self.assertEqual(json.loads(v.validate(r))['requestedWrapperExitCode'],expected)
        r=receipt();r['requestedWrapperExitCode']=0;self.reject(r)
    def test_public_budget_and_no_sample_contents(self):
        r=receipt(GOLDEN,GOLDEN);data=v.validate(r);self.assertLessEqual(len(data),32768);self.assertNotIn(b'Write a dump',data);self.assertNotIn(b'ProcDump v12',data);self.assertNotIn(b'PRIVATE',data)
    def test_pipeline_and_private_shape_remain_single_no_target(self):
        root=ROOT.parents[1];workflow=(root/'.github/workflows/inbox-procdump-help-candidates.yml').read_text();entry=(ROOT/'Invoke-ToolHelpCandidates.ps1').read_text()
        for name in ['inbox-procdump-help-format.yml','inbox-capture-preparation.yml','inbox-pr-observe-only.yml']:self.assertFalse((root/'.github/workflows'/name).exists())
        self.assertNotIn('workflow_dispatch',workflow);self.assertIn('path: tool-help-candidates-safe/safe-report.json',workflow);self.assertEqual(entry.count("Run $proc (Get-NoTargetArguments 'procdump')"),1)
        for forbidden in ['SkipValidation','RunBarrier','Get-Command dotnet',"Get-NoTargetArguments 'cdb'","'-e'","'-ma'","'-cancel'","'-z'"]:self.assertNotIn(forbidden,entry)
        self.assertIn("$child['DOTNET_DbgEnableMiniDump']='0';$child['COMPlus_DbgEnableMiniDump']='0'",entry)
        self.assertIn("$r=Run $proc (Get-NoTargetArguments 'procdump') $PreparationToolSeconds -AllowFailure",entry)
        self.assertIn('ReadBounded $projection 32768',entry)
    def test_cli_projection_receipt_and_wrong_run_refusal(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            d=Path(tmp);a=d/'out';b=d/'err';a.write_bytes(TEXT.encode('utf-16-le'));b.write_bytes(b'PRIVATE');projection=d/'projection'
            r=subprocess.run([sys.executable,str(ROOT/'project_candidates.py'),str(a),str(b),VERSION,str(projection)],capture_output=True,text=True);self.assertEqual(r.returncode,0,r.stderr);self.assertEqual(r.stdout.strip(),'tool-help-candidates-projected');self.assertEqual(r.stderr,'');self.assertNotIn('PRIVATE',projection.read_text())
            pending=d/'pending';safe=d/'safe';pending.write_text(json.dumps(receipt()));env=dict(os.environ,GITHUB_SHA='a'*40,GITHUB_RUN_ID='1234',GITHUB_RUN_ATTEMPT='1')
            r=subprocess.run([sys.executable,str(ROOT/'validate_candidates.py'),str(pending),str(safe)],capture_output=True,text=True,env=env);self.assertEqual(r.returncode,0,r.stdout);self.assertEqual(r.stderr,'')
            env['GITHUB_RUN_ID']='999';r=subprocess.run([sys.executable,str(ROOT/'validate_candidates.py'),'--check',str(safe)],capture_output=True,text=True,env=env);self.assertEqual(r.returncode,1);self.assertEqual(r.stdout.strip(),'tool-help-candidates-receipt-rejected');self.assertEqual(r.stderr,'')
    def test_initial_powershell_projection_matches_schema(self):
        entry=(ROOT/'Invoke-ToolHelpCandidates.ps1').read_text();match=re.search(r"function New-CandidateObservation \{\s+return \('(.*?)' \| ConvertFrom-Json -AsHashtable\)",entry);self.assertIsNotNone(match);self.assertEqual(json.loads(match[1]),p.initial())
    def test_profile_and_pins_not_approval(self):
        self.assertEqual((ROOT/'identity.json').read_bytes(),(ROOT.parent/'pr-ci-procdump-help-format/identity.json').read_bytes());profile=json.loads((ROOT.parent/'pr-ci-capture-preparation/procdump-contract.json').read_text());self.assertFalse(profile['reviewed']);self.assertTrue(all(profile[k] is None for k in ['version','sha256','helpSha256','helpExitCode']))

    def test_completion_flag_is_boolean_not_integer(self):
        r=receipt();r['observationComplete']=1;self.reject(r)
    def test_stderr_presence_cannot_contradict_candidate_bytes(self):
        r=receipt();r['observation']['baseline']['stderr']['presence']='nonempty';self.reject(r)
    def test_incomplete_digest_refusal_is_exportable(self):
        r=receipt();r.update(observationComplete=False,failure='observation-incomplete');r['observation']['stderr']=p.empty_stream('input-changed');actual=json.loads(v.validate(r));self.assertEqual(actual['tool']['nativeExitCode'],-1);self.assertFalse(actual['observationComplete'])
    def test_analysis_refusal_is_observation_not_feature_absence(self):
        r=receipt(b'X\0',b'Y');actual=json.loads(v.validate(r));self.assertTrue(actual['observationComplete']);self.assertFalse(actual['qualificationPassed']);self.assertEqual(actual['observation']['stdout']['candidates'][0]['phases']['clauses']['fullMemory'],'unobserved')
    def test_no_plaintext_in_source_serialized_buffers(self):
        for path in ROOT.glob('*.py'):
            self.assertFalse(any(ord(c)<32 and c not in '\n\r\t' for c in path.read_text()))
    def test_same_response_requires_original_baseline_digest(self):
        r=receipt();r['observation']['baseline']['responseComparison']='same-response';self.reject(r)
    def test_bom_is_not_relabelled_as_derived(self):
        r=receipt();c=r['observation']['stdout']['candidates'][1];c['bomPolicy']='explicit-matching';self.reject(r)
    def test_combined_output_bound_not_two_independent_caps(self):
        # Receipt-shape seam: otherwise valid bounded-file observations, not native byte evidence.
        r=receipt(b'x'*2097153,b'y'*2097153)
        r['observation']['stdout']['inputBytes']=40*1024*1024;r['observation']['baseline']['semantic']['inputBytes']=40*1024*1024
        r['observation']['stderr']['inputBytes']=20*1024*1024
        v.validate(r) # 60MiB is otherwise valid; isolate only the combined cap below.
        r['observation']['stderr']['inputBytes']=30*1024*1024
        self.reject(r)

    def test_unobserved_candidate_booleans_are_not_integer_zero(self):
        for state in ['unobserved','read-failed','file-limit','input-changed']:
            o=p.initial();o['stdout']=p.empty_stream(state);v.validate_observation(o)
            o['stdout']['candidates'][0]['conformant']=0
            with self.assertRaises((ValueError,TypeError)):v.validate_observation(o)
    def test_unobserved_kind_booleans_are_not_integer_zero(self):
        o=p.initial();v.validate_observation(o);o['stderr']['candidates'][2]['kinds']['NUL']=0
        with self.assertRaises((ValueError,TypeError)):v.validate_observation(o)

    def test_observed_stream_rejects_individual_stream_owned_candidate_states(self):
        base=receipt(GOLDEN,GOLDEN)
        self.assertTrue(json.loads(v.validate(base))['observationComplete'])
        for stream in ('stdout','stderr'):
            for state in ('unobserved','read-failed','file-limit','input-changed'):
                for index,identifier in enumerate(('utf8','utf16le','utf16be')):
                    with self.subTest(stream=stream,state=state,candidate=identifier):
                        r=copy.deepcopy(base);s=r['observation'][stream]
                        s['candidates'][index]=literal_stream_owned_candidate(identifier,state)
                        s['conformance']=dict(count='none',id=None) if index==0 else dict(count='unique',id='utf8')
                        self.reject(r)
    def test_observed_stream_rejects_all_stream_owned_candidate_states(self):
        base=receipt(GOLDEN,GOLDEN)
        self.assertTrue(json.loads(v.validate(base))['observationComplete'])
        for stream in ('stdout','stderr'):
            for state in ('unobserved','read-failed','file-limit','input-changed'):
                with self.subTest(stream=stream,state=state):
                    r=copy.deepcopy(base);s=r['observation'][stream]
                    s['candidates']=[literal_stream_owned_candidate(identifier,state) for identifier in ('utf8','utf16le','utf16be')]
                    s['conformance']=dict(count='none',id=None)
                    self.reject(r)
    def test_matching_incomplete_stream_states_remain_exportable(self):
        for stream in ('stdout','stderr'):
            for state in ('unobserved','read-failed','file-limit','input-changed'):
                with self.subTest(stream=stream,state=state):
                    r=receipt(GOLDEN,GOLDEN);r.update(observationComplete=False,failure='observation-incomplete')
                    r['observation'][stream]=dict(state=state,inputSha256=None,inputBytes=None,bom='unobserved',candidates=[literal_stream_owned_candidate(identifier,state) for identifier in ('utf8','utf16le','utf16be')],conformance=dict(count='none',id=None))
                    actual=json.loads(v.validate(r));self.assertFalse(actual['observationComplete']);self.assertEqual(actual['tool']['nativeExitCode'],-1);self.assertEqual(actual['requestedWrapperExitCode'],-1)
                    self.assertFalse(actual['qualificationPassed']);self.assertFalse(actual['captureEnabled']);self.assertFalse(actual['transportApproved'])
    def test_observed_nonconformant_outcomes_remain_exportable(self):
        expanded=('\u4e00'*1000+'\n')*750
        for raw,identifier,state in [(b'x'*2097153,'utf8','input-limit'),(b'\xff\xfeX\0','utf8','bom-conflict'),(b'X','utf16le','odd-length'),(b'\xff','utf8','decode-failed'),(b'X\0','utf8','control-rejected'),(b'x'*1025,'utf8','line-limit'),(expanded.encode('utf-16-le'),'utf16le','derived-limit'),(b'X','utf8','inspected')]:
            with self.subTest(state=state):
                actual=json.loads(v.validate(receipt(raw,raw)))
                self.assertTrue(actual['observationComplete'])
                for stream in ('stdout','stderr'):
                    c=pick(actual['observation'],identifier,stream);self.assertEqual(c['state'],state);self.assertFalse(c['conformant'])
                self.assertFalse(actual['transportApproved'])
    def test_observed_roundtrip_failure_projection_remains_exportable(self):
        # Receipt projection only. Strict standard codecs do not naturally produce this mismatch.
        r=receipt(b'X',b'X');c=literal_stream_owned_candidate('utf8','roundtrip-failed')
        c.update(bomPolicy='no-bom-hypothesis',decode='passed',roundTrip='failed')
        r['observation']['stdout']['candidates'][0]=c
        actual=json.loads(v.validate(r));self.assertTrue(actual['observationComplete']);self.assertFalse(actual['observation']['stdout']['candidates'][0]['conformant'])

if __name__=='__main__':unittest.main(verbosity=2)
