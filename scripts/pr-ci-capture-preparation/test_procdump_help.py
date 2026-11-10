"""Documentation-derived independent fixtures. No claim these bytes are actual native help."""
import copy,hashlib,json,os,subprocess,sys,tempfile,unittest
from pathlib import Path
import project_procdump_help as p
import validate_preparation as v
from test_preparation import sample
ROOT=Path(__file__).resolve().parent
RAW=(ROOT/'fixtures/procdump-12.02-doc-contract.txt').read_bytes()
KEYS=('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')
class HelpTests(unittest.TestCase):
    def parse(self,raw=RAW,version='12.2.0.0'):
        result=p.project(raw,version);v.validate_help_projection(result,result['inputSha256'],version);return result
    def test_documented_semantics_not_flag_presence(self):
        result=self.parse();self.assertEqual(result['status'],'recognized');self.assertEqual(result['grammarId'],'procdump-help-12.02-d46b5bfa')
        self.assertEqual(result['capabilities'],dict.fromkeys(KEYS,True));self.assertEqual(result['clauses'],dict.fromkeys(KEYS,'recognized'));self.assertFalse(result['behaviorVerified']);self.assertEqual(result['inputSha256'],hashlib.sha256(RAW).hexdigest())
    def test_bare_flags_do_not_qualify(self):
        result=self.parse(b'ProcDump v12.02\n-ma -e -g -n -at -cancel -accepteula -?\n');self.assertEqual(result['status'],'unrecognized');self.assertFalse(any(result['capabilities'].values()))
    def test_changed_required_semantics_not_accepted(self):
        for old,new,key in [(b'unhandled exception.',b'handled exception.','unhandledException'),(b'no interop',b'with interop','nativeDebugger'),(b'Includes all memory',b'Includes some memory','fullMemory'),(b'Number of dumps to write before exiting.',b'Number of processes to debug.','dumpCount'),(b'Cancel the trigger\'s collection',b'Start the trigger\'s collection','captureTimeout'),(b'process is resumed if a capture is active',b'process is killed if a capture is active','cancelGraceful'),(b'automatically accept',b'automatically reject','acceptEula')]:
            with self.subTest(key=key):
                result=self.parse(RAW.replace(old,new));self.assertEqual(result['status'],'unrecognized');self.assertFalse(result['capabilities'][key]);self.assertEqual(result['clauses'][key],'unrecognized')
    def test_option_forms_are_required(self):
        for old,new,key in [(b'[-n <Count>]',b'[-n]','dumpCount'),(b'[-at <Timeout>]',b'[-at]','captureTimeout'),(b'[-e [1] [-g]',b'[-e [2] [-g]','unhandledException'),(b'-cancel <Target Process PID>',b'-cancel <Process Name>','cancelGraceful'),(b'procdump -? -e',b'procdump -? <PID>','helpNoTarget')]:
            with self.subTest(key=key):r=self.parse(RAW.replace(old,new));self.assertFalse(r['capabilities'][key]);self.assertEqual(r['status'],'unrecognized')
    def test_missing_and_unknown_wording_are_distinct(self):
        r=self.parse(RAW.replace(b'-g Run as a native debugger in a managed process (no interop).\n',b''));self.assertEqual(r['clauses']['nativeDebugger'],'missing')
        r=self.parse(RAW.replace(b'Run as a native debugger in a managed process (no interop).',b'Native debugging is supported.'));self.assertEqual(r['clauses']['nativeDebugger'],'unrecognized')
    def test_duplicate_definitions_rejected(self):
        line=b'-g Run as a native debugger in a managed process (no interop).\n';r=self.parse(RAW.replace(line,line+line));self.assertEqual(r['reason'],'ambiguous-clause');self.assertEqual(r['clauses']['nativeDebugger'],'ambiguous')
    def test_duplicate_usage_forms_rejected(self):
        r=self.parse(RAW.replace(b'[-n <Count>]',b'[-n <Count>] [-n <Count>]'));self.assertEqual(r['clauses']['dumpCount'],'ambiguous')
    def test_malformed_usage_not_recognized(self):
        r=self.parse(RAW.replace(b'[-n <Count>]',b'[-n <Count>'));self.assertEqual(r['status'],'unrecognized');self.assertFalse(r['capabilities']['unhandledException'])
    def test_extra_definition_text_cannot_hide_contradiction(self):
        r=self.parse(RAW.replace(b'-g Run as a native debugger in a managed process (no interop).',b'-g Run as a native debugger in a managed process (no interop). It captures every process.'));self.assertEqual(r['clauses']['nativeDebugger'],'unrecognized')
    def test_duplicate_versions_even_equal_are_ambiguous(self):
        r=self.parse(b'ProcDump v12.02\n'+RAW);self.assertEqual(r['reason'],'ambiguous-version');self.assertFalse(any(r['capabilities'].values()))
    def test_unsupported_family_and_display_shape(self):
        for raw,version in [(RAW.replace(b'v12.02',b'v13.00'),'13.0.0.0'),(RAW.replace(b'v12.02',b'v12.02.0'),'12.2.0.0')]:
            r=self.parse(raw,version);self.assertEqual(r['status'],'unsupported');self.assertFalse(any(r['capabilities'].values()));self.assertIsNone(r['grammarId'])
    def test_version_mismatch_and_missing(self):
        r=self.parse(RAW,'12.0.2.0');self.assertEqual(r['reason'],'version-mismatch');self.assertFalse(r['versionAgreement'])
        r=self.parse(RAW.replace(b'ProcDump v12.02\n',b''));self.assertEqual(r['reason'],'missing-version')
    def test_canonical_version_numeric_agreement(self):
        r=self.parse(RAW.replace(b'v12.02',b'v12.2'),'12.2.123.456');self.assertEqual(r['status'],'recognized');self.assertTrue(r['versionAgreement'])
    def test_invalid_file_version_refused(self):
        for version in [None,'12.2','PRIVATE','12.2.0.0\n','12.99999.0.0']:
            r=p.project(RAW,version);self.assertEqual(r['reason'],'file-version');self.assertFalse(any(r['capabilities'].values()))
    def test_empty_and_whitespace(self):
        for raw in [b'',b' \r\n\t']:
            r=self.parse(raw);self.assertEqual(r['status'],'empty');self.assertEqual(r['clauses'],dict.fromkeys(KEYS,'missing'))
    def test_encodings_are_explicit(self):
        for raw,encoding in [(RAW,'utf8'),(b'\xef\xbb\xbf'+RAW,'utf8'),(b'\xff\xfe'+RAW.decode().encode('utf-16-le'),'utf16le'),(b'\xfe\xff'+RAW.decode().encode('utf-16-be'),'utf16be')]:
            r=self.parse(raw);self.assertEqual(r['encoding'],encoding);self.assertEqual(r['status'],'recognized')
    def test_unknown_encoding_and_controls_rejected(self):
        for raw,reason in [(b'\xffBAD','encoding'),(RAW+b'\x00PRIVATE','control-character'),(RAW+'\u202ePRIVATE'.encode(),'control-character'),(b'\xff\xfe\x00','encoding')]:
            r=self.parse(raw);self.assertEqual(r['reason'],reason);self.assertFalse(any(r['capabilities'].values()))
    def test_oversize_and_line_limits(self):
        for raw,reason in [(b'x'*(2097152+1),'input-limit'),(b'x'*1025,'line-limit'),(b'\n'*8193+b'x','line-limit')]:
            r=self.parse(raw);self.assertEqual(r['reason'],reason)
    def test_unknown_private_text_is_never_projected(self):
        raw=RAW.replace(b'ProcDump v12.02',b'ProcDump v12.02 - PRIVATE_TOKEN C:\\private\\user')
        encoded=json.dumps(self.parse(raw));self.assertNotIn('PRIVATE',encoded);self.assertNotIn('C:\\',encoded)
    def test_file_projection_keeps_full_oversize_digest(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            path=Path(tmp)/'help';raw=b'x'*(2097152+99);path.write_bytes(raw);result=p.project_file(path,'12.2.0.0');v.validate_help_projection(result,hashlib.sha256(raw).hexdigest(),'12.2.0.0');self.assertEqual(result['inputBytes'],len(raw));self.assertEqual(result['reason'],'input-limit')
    def test_reader_rejects_private_fields_and_wrong_digest(self):
        r=self.parse();r['raw']='PRIVATE'
        with self.assertRaises(ValueError):v.validate_help_projection(r,r['inputSha256'],'12.2.0.0')
        r=self.parse()
        with self.assertRaises(ValueError):v.validate_help_projection(r,'a'*64,'12.2.0.0')
    def test_reader_never_promotes_semantics_to_behavior(self):
        r=self.parse();r['behaviorVerified']=True
        with self.assertRaises(ValueError):v.validate_help_projection(r,r['inputSha256'],'12.2.0.0')
    def test_reader_rejects_inconsistent_recognized_capability(self):
        for key in KEYS:
            r=self.parse();r['capabilities'][key]=False
            with self.assertRaises(ValueError):v.validate_help_projection(r,r['inputSha256'],'12.2.0.0')
    def test_valid_partial_semantics_cannot_pass_reviewed_success(self):
        # Partial projection itself is valid. Only reviewed-tool success must reject it.
        r=sample();self.assertTrue(json.loads(v.validate(r))['preparePassed']);h=r['tools'][0]['helpProjection'];h.update(status='unrecognized',reason='unrecognized-clause');h['clauses']['nativeDebugger']='unrecognized';h['capabilities']['nativeDebugger']=False
        v.validate_help_projection(h,r['tools'][0]['helpSha256'],r['tools'][0]['version'])
        with self.assertRaises(ValueError):v.validate(r)
        r.update(preparePassed=False,failure='tool-contract-unreviewed');r['tools'][0]['status']='unreviewed-contract';self.assertFalse(json.loads(v.validate(r))['preparePassed'])
    def test_schema1_not_silently_accepted(self):
        r=sample();r['schema']=1
        with self.assertRaises(ValueError):v.validate(r)
    def test_projection_failure_keeps_native_exit(self):
        for code in [17,0,None]:
            r=sample();r.update(preparePassed=False,failure='preparation-incomplete');t=r['tools'][0];t.update(status='unreviewed-contract',nativeExitCode=code,helpProjection=p.initial())
            actual=json.loads(v.validate(r));self.assertEqual(actual['tools'][0]['nativeExitCode'],code)
    def test_cli_owns_only_typed_private_projection(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            raw=Path(tmp)/'input';out=Path(tmp)/'projection';raw.write_bytes(RAW)
            result=subprocess.run([sys.executable,str(ROOT/'project_procdump_help.py'),str(raw),'12.2.0.0',str(out)],capture_output=True,text=True)
            self.assertEqual(result.returncode,0);self.assertEqual(result.stdout.strip(),'procdump-help-projected');self.assertEqual(result.stderr,'');self.assertEqual(json.loads(out.read_text())['status'],'recognized')
            again=subprocess.run([sys.executable,str(ROOT/'project_procdump_help.py'),str(raw),'12.2.0.0',str(out)],capture_output=True,text=True);self.assertNotEqual(again.returncode,0);self.assertEqual(again.stdout.strip(),'procdump-help-projection-failed');self.assertEqual(again.stderr,'')
    def test_repeated_usage_blocks_are_ambiguous_not_quadratic(self):
        usage=b'    procdump.exe [-ma] [-e [1] [-g]] [-n <Count>] [-at <Timeout>] <PID>\n'
        r=self.parse(b'ProcDump v12.02\n'+usage*2048+b'Dump Types:'+RAW.split(b'Dump Types:',1)[1])
        self.assertEqual(r['reason'],'ambiguous-clause');self.assertEqual(r['clauses']['dumpCount'],'ambiguous')
    def test_read_failure_has_no_raw_path_or_error(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as tmp:
            r=p.project_file(Path(tmp)/'PRIVATE_MISSING','12.2.0.0');v.validate_help_projection(r,None,'12.2.0.0')
            self.assertEqual(r['reason'],'read-failed');self.assertNotIn('PRIVATE',json.dumps(r))
    def test_recognized_projection_requires_digest_provenance(self):
        r=self.parse();r.update(inputSha256=None,inputBytes=None)
        with self.assertRaises(ValueError):v.validate_help_projection(r,None,'12.2.0.0')

    def nonrecognized(self,raw):
        result=self.parse(raw) # Schema/provenance must still pass; rejection is semantic, not setup failure.
        self.assertNotEqual(result['status'],'recognized')
        self.assertFalse(all(result['capabilities'].values()))
        self.assertFalse(result['behaviorVerified'])
        return result
    def test_prep2_second_capture_without_optional_e_is_not_ignored(self):
        self.assertEqual(self.parse()['status'],'recognized')
        extra=b'Usage: procdump.exe [-ma] [-e] [-n <Count>] [-at <Timeout>] <PID>\n'
        result=self.nonrecognized(RAW.replace(b'Dump Types:',extra+b'Dump Types:'))
        self.assertEqual(result['reason'],'ambiguous-clause')
    def test_prep2_g_form_with_trailing_word_is_not_a_token(self):
        self.assertEqual(self.parse()['status'],'recognized')
        result=self.nonrecognized(RAW.replace(b'[-g]',b'[-g]WRONG',1))
        self.assertEqual(result['clauses']['nativeDebugger'],'unrecognized')
    def test_prep2_malformed_second_version_counts_before_validation(self):
        self.assertEqual(self.parse()['status'],'recognized')
        result=self.nonrecognized(RAW.replace(b'Capture Usage:',b'ProcDump v13.bad\nCapture Usage:'))
        self.assertEqual(result['reason'],'ambiguous-version')
    def test_required_usage_boundaries_reject_sibling_junk(self):
        for old,new in [(b'[-g]',b'WRONG[-g]'),(b'[-ma]',b'[-ma]WRONG'),(b'[-n <Count>]',b'[-n <Count>]WRONG'),(b'[-at <Timeout>]',b'[-at <Timeout>]WRONG'),(b'[-e [1]',b'[-e [1]WRONG')]:
            with self.subTest(new=new):self.nonrecognized(RAW.replace(old,new,1))
    def test_additional_malformed_usage_declarations_are_not_filtered(self):
        for extra in [b'Usage: procdump.exe [-n <Count>]\n',b'Usage: PRIVATE\n',b'Usage procdump.exe [-ma] [-e]\n',b'procdump.exe [-ma] [-e]\n',b'procdump.exeWRONG [-ma] [-e]\n',b'Capture Usage:\n',b'Examples:\nprocdump.exe [-ma] [-e] [-n <Count>]\n']:
            with self.subTest(extra=extra):self.nonrecognized(RAW.replace(b'Dump Types:',extra+b'Dump Types:'))
    def test_one_malformed_version_is_not_a_missing_candidate_escape(self):
        for version in [b'ProcDump v13.bad',b'ProcDump v12.02WRONG',b'ProcDump v',b'ProcDump version12.02']:
            with self.subTest(version=version):self.nonrecognized(RAW.replace(b'ProcDump v12.02',version,1))
    def test_same_line_multiple_version_declarations_are_ambiguous(self):
        result=self.nonrecognized(RAW.replace(b'ProcDump v12.02',b'ProcDump v12.02 - ProcDump v13.bad',1));self.assertEqual(result['reason'],'ambiguous-version')
    def test_explicit_legitimate_install_uninstall_and_examples_are_distinct(self):
        other=(b'Install Usage:\nprocdump.exe -i [Dump_Folder]\n    [-mm] [-ma] [-mt] [-mp] [-mc <Mask>] [-md <Callback_DLL>] [-mk]\n    [-r] [-at <Timeout>] [-k] [-wer]\nUninstall Usage:\nprocdump.exe -u\n')
        raw=RAW.replace(b'Dump Types:',other+b'Dump Types:')+b'\nExamples:\nprocdump -ma 4572\nC:\\>procdump -? -e\n'
        self.assertEqual(self.parse(raw)['status'],'recognized')
    def test_install_or_uninstall_heading_cannot_hide_bad_capture_syntax(self):
        for other in [b'Install Usage:\nprocdump.exe [-ma] [-e]\n',b'Uninstall Usage:\nprocdump.exe [-ma] [-e]\n',b'Install Usage: PRIVATE\n']:
            with self.subTest(other=other):self.nonrecognized(RAW.replace(b'Dump Types:',other+b'Dump Types:'))
    def test_pinned_complete_usage_retains_whitespace_and_program_alias(self):
        raw=RAW.replace(b'procdump.exe [-mm]',b'procdump64.exe    [-mm]',1).replace(b'            [',b'\t  [')
        self.assertEqual(self.parse(raw)['status'],'recognized')

    def test_wrapping_cannot_hide_example_declaration(self):
        self.assertEqual(self.parse()['status'],'recognized')
        for declaration in [b'procdump.exe [-ma] [-e]',b'procdump.exe\n    [-ma] [-e]']:
            result=self.nonrecognized(RAW.replace(b'Dump Types:',b'Examples:\n'+declaration+b'\nDump Types:',1))
            self.assertEqual(result['reason'],'ambiguous-clause')
    def test_wrapping_cannot_hide_other_section_declaration(self):
        self.assertEqual(self.parse()['status'],'recognized')
        for declaration in [b'procdump.exe [-ma] [-e]',b'procdump.exe\n    [-ma] [-e]']:
            result=self.nonrecognized(RAW.replace(b'Dump Types:',b'Dump Types:\n'+declaration+b'\nDump Types:',1))
            self.assertEqual(result['reason'],'ambiguous-clause')
    def test_extra_declaration_refusal_is_invariant_at_supported_wraps(self):
        declaration=b'procdump.exe [-ma] [-e] [-n <Count>] [-at <Timeout>] <PID>'
        for heading in [b'Examples:',b'Dump Types:']:
            for index,character in enumerate(declaration):
                if character!=32:continue
                for indent in [b' ',b'    ',b'\t']:
                    with self.subTest(heading=heading,index=index,indent=indent):
                        wrapped=declaration[:index]+b'\n'+indent+declaration[index+1:]
                        self.nonrecognized(RAW.replace(b'Dump Types:',heading+b'\n'+wrapped+b'\nDump Types:',1))
    def test_wrapped_malformed_alias_and_placeholder_are_not_concrete_examples(self):
        for heading in [b'Examples:',b'Dump Types:']:
            for declaration in [b'procdump.exeWRONG\n    [-ma] [-e]',b'procdump.exe\n    WRONG[-ma] [-e]',b'procdump.exe\n    <PID>']:
                with self.subTest(heading=heading,declaration=declaration):
                    self.nonrecognized(RAW.replace(b'Dump Types:',heading+b'\n'+declaration+b'\nDump Types:',1))
    def test_wrapped_concrete_example_keeps_positive_contract(self):
        for example in [b'procdump -ma 4572',b'procdump -ma\n    4572']:
            self.assertEqual(self.parse(RAW+b'\nExamples:\n'+example+b'\n')['status'],'recognized')

    def test_option_led_wrap_cannot_hide_x_declaration_in_examples(self):
        self.assertEqual(self.parse()['status'],'recognized')
        for declaration in [b'procdump.exe -x <Image>',b'procdump.exe\n    -x <Image>']:
            raw=RAW.replace(b'Dump Types:',b'Examples:\n'+declaration+b'\nDump Types:',1)
            result=self.nonrecognized(raw)
            self.assertEqual(result['reason'],'ambiguous-clause')
            self.assertEqual(len(p.capture_declarations(raw.decode().splitlines())),2)
    def test_option_led_wrap_cannot_hide_w_declaration_in_other_section(self):
        self.assertEqual(self.parse()['status'],'recognized')
        for declaration in [b'procdump.exe -w <PID>',b'procdump.exe\n    -w <PID>']:
            raw=RAW.replace(b'Dump Types:',b'Dump Types:\n'+declaration+b'\nDump Types:',1)
            result=self.nonrecognized(raw)
            self.assertEqual(result['reason'],'ambiguous-clause')
            self.assertEqual(len(p.capture_declarations(raw.decode().splitlines())),2)
    def test_option_led_declaration_wrap_matrix_is_semantically_invariant(self):
        for declaration in [b'procdump.exe -x <Image>',b'procdump64.exe -w <PID>',b'procdump.exe -e <PID>',b'procdump.exeWRONG -x <Image>']:
            for heading in [b'Examples:',b'Dump Types:']:
                for index,character in enumerate(declaration):
                    if character!=32:continue
                    for indent in [b' ',b'    ',b'\t']:
                        with self.subTest(declaration=declaration,heading=heading,index=index,indent=indent):
                            wrapped=declaration[:index]+b'\n'+indent+declaration[index+1:]
                            raw=RAW.replace(b'Dump Types:',heading+b'\n'+wrapped+b'\nDump Types:',1)
                            self.nonrecognized(raw)
                            self.assertEqual(len(p.capture_declarations(raw.decode().splitlines())),2)
    def test_concrete_example_options_are_not_semantic_definition_rows(self):
        for example in [b'procdump.exe -ma 4572',b'procdump.exe\n    -ma 4572',b'procdump.exe\n    -e\n    -g\n    4572']:
            with self.subTest(example=example):
                self.assertEqual(self.parse(RAW+b'\nExamples:\n'+example+b'\n')['status'],'recognized')
    def test_indented_definitions_after_section_boundary_remain_semantic(self):
        raw=RAW.replace(b'Dump Types:\n-ma ',b'Dump Types:\n    -ma ').replace(b'Conditions:\n-e ',b'Conditions:\n    -e ')
        self.assertEqual(self.parse(raw)['status'],'recognized')
    def test_noncommand_prose_does_not_swallow_duplicate_definition(self):
        extra=b'Examples:\nProcDump explains:\n    -g Run as a native debugger in a managed process (no interop).\n'
        self.nonrecognized(RAW.replace(b'Dump Types:',extra+b'Dump Types:',1))

if __name__=='__main__':unittest.main(verbosity=2)
