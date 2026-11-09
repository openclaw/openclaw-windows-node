"""Pure data controls, not fake native UI/lifetime proof. python test_voice_pair_evidence.py"""
import json
from pathlib import Path
import tempfile
import unittest
import voice_pair_evidence as v

START = '2026-11-09T21:00:00+00:00'
FINISH = '2026-11-09T21:01:00+00:00'
RUN = 'a' * 32

def sample_trx(passed=True):
    outcome = 'Passed' if passed else 'Failed'
    return (f'<TestRun xmlns="{v.NS}"><Times start="{START}" finish="{FINISH}"/>'
        f'<Results><UnitTestResult testName="{v.FACT}" executionId="11111111-1111-1111-1111-111111111111" '
        f'testId="22222222-2222-2222-2222-222222222222" outcome="{outcome}" startTime="{START}" endTime="{FINISH}" duration="00:01:00.0000000">'
        '<Output><StdOut>SECRET-PROFILE-STDOUT</StdOut><ErrorInfo><Message>SECRET-ERROR</Message><StackTrace>SECRET-STACK</StackTrace></ErrorInfo></Output>'
        f'<ResultFiles><ResultFile path="SECRET-PATH"/></ResultFiles></UnitTestResult></Results><ResultSummary outcome="{outcome}"><Counters total="1" executed="1" passed="{int(passed)}" failed="{int(not passed)}" notExecuted="0" error="0"/></ResultSummary></TestRun>').encode()

def sample_voice():
    return {'History': {'ObservedAtUtc': START, 'ReadSucceeded': True, 'ComposeTargetMatches': False,
        'HistoryLoaded': True, 'ReturnedEntryCount': 30, 'ExpectedContentMatchCount': 30, 'FinalEntryMatches': True},
        'PollCount': 115, 'LastPoll': {'StartedAtUtc': START, 'CompletedAtUtc': FINISH, 'ComposerPresent': True,
        'SelectedThreadMatches': True, 'RenderConsumedHistory': True, 'TimelinePresent': True,
        'FinalElementPresent': False, 'FinalOffscreen': None, 'FinalHasPositiveBounds': None,
        'FinalWithinVerticalViewport': None, 'FinalOverlapsHorizontalViewport': None, 'FinalVisible': False},
        'WaitSucceeded': False, 'WaitFinishedAtUtc': FINISH}

def sample_lifecycle():
    return {'runId': RUN, 'outcome': 'failed', 'failure': 'SECRET-ORIGINAL-ASSERTION', 'stages': [
        'release:completed', 'capture:timeout', 'cancel-capture:completed', 'stop:completed', 'drain:completed', 'dispose:completed']}

class EvidenceControls(unittest.TestCase):
    def test_pass_keeps_exact_case_and_discards_raw_output(self):
        result, sanitized = v.trx(sample_trx(), START, FINISH, 0)
        self.assertEqual((result['total'], result['passed'], result['failed']), (1, 1, 0))
        self.assertIn(v.FACT.encode(), sanitized)
        self.assertNotIn(b'SECRET', sanitized)
        self.assertNotIn(b'StdOut', sanitized)

    def test_failure_stays_failure_not_zero_exit_success(self):
        result, sanitized = v.trx(sample_trx(False), START, FINISH, 1)
        self.assertEqual((result['outcome'], result['passed'], result['failed']), ('Failed', 0, 1))
        self.assertIn(b'outcome="Failed"', sanitized)
        self.assertNotIn(b'SECRET', sanitized)
        with self.assertRaises(ValueError): v.trx(sample_trx(False), START, FINISH, 0)

    def test_skip_is_not_pair_evidence(self):
        with self.assertRaises(ValueError): v.trx(sample_trx().replace(b'outcome="Passed"', b'outcome="NotExecuted"'), START, FINISH, 0)

    def test_zero_or_extra_case_rejected(self):
        raw = sample_trx(); a = raw.index(b'<UnitTestResult'); b = raw.index(b'</UnitTestResult>') + len(b'</UnitTestResult>')
        for replacement in (b'', raw[a:b] * 2):
            with self.assertRaises(ValueError): v.trx(raw[:a] + replacement + raw[b:], START, FINISH, 0)

    def test_other_fact_cannot_satisfy_filter(self):
        with self.assertRaises(ValueError): v.trx(sample_trx().replace(v.FACT.encode(), b'OpenClaw.Other.Fact'), START, FINISH, 0)

    def test_stale_result_rejected(self):
        with self.assertRaises(ValueError): v.trx(sample_trx(), '2026-11-09T22:00:00Z', '2026-11-09T22:01:00Z', 0)

    def test_counter_mismatch_rejected(self):
        with self.assertRaises(ValueError): v.trx(sample_trx().replace(b'total="1"', b'total="2"'), START, FINISH, 0)

    def test_dtd_rejected_without_entity_evaluation(self):
        with self.assertRaises(ValueError): v.trx(b'<!DOCTYPE TestRun [<!ENTITY private "SECRET">]>' + sample_trx(), START, FINISH, 0)

    def test_voice_null_is_unknown_not_false(self):
        projected = v.voice(sample_voice())
        self.assertIsNone(projected['LastPoll']['FinalOffscreen'])
        self.assertEqual(projected['History']['ExpectedContentMatchCount'], 30)

    def test_voice_unknown_field_and_text_value_rejected(self):
        value = sample_voice(); value['History']['Message'] = 'SECRET'
        with self.assertRaises(ValueError): v.voice(value)
        value = sample_voice(); value['LastPoll']['FinalVisible'] = 'SECRET'
        with self.assertRaises(ValueError): v.voice(value)

    def test_bool_is_not_integer_counter(self):
        value = sample_voice(); value['PollCount'] = True
        with self.assertRaises(ValueError): v.voice(value)

    def test_qualified_failure_cleanup_discards_exception(self):
        projected = v.lifecycle(sample_lifecycle(), RUN)
        self.assertEqual(projected[-3:], ['stop:completed', 'drain:completed', 'dispose:completed'])
        self.assertNotIn('SECRET', json.dumps(projected))

    def test_unknown_stop_drain_or_disposal_blocks_next_arm(self):
        for i in (0, 2, 3, 4, 5):
            value = sample_lifecycle(); value['stages'][i] = value['stages'][i].replace('completed', 'timeout')
            with self.assertRaises(ValueError): v.lifecycle(value, RUN)
        value = sample_lifecycle(); value['stages'][-1] = 'dispose:error:IOException'
        with self.assertRaises(ValueError): v.lifecycle(value, RUN)

    def test_lifecycle_other_run_rejected(self):
        with self.assertRaises(ValueError): v.lifecycle(sample_lifecycle(), 'b' * 32)

    def test_numeric_warning_is_bound_to_logger_producer(self):
        line = '[2026-11-09 21:00:00.123] [WARN] ' + v.MARKERS[0] + ' frames=74 retries=3 itemCount=30 tailIndex=29 proposedOffset=2410 completedOffset=0\n'
        result = v.tail_warnings(line.encode())
        self.assertEqual(result[0]['fields']['proposedOffset'], '2410')
        self.assertFalse(result[0]['timezoneVerified'])
        self.assertEqual(v.tail_warnings(('STDERR embedded ' + line).encode()), [])

    def test_warning_extra_or_secret_scalar_rejected(self):
        prefix = '[2026-11-09 21:00:00.123] [WARN] ' + v.MARKERS[0]
        for suffix in (' message=SECRET', ' frames=SECRET', ' frames=1 frames=2'):
            with self.assertRaises(ValueError): v.tail_warnings((prefix + suffix).encode())

    def test_json_duplicate_or_nonfinite_rejected(self):
        with self.assertRaises(ValueError): json.loads('{"a":1,"a":2}', object_pairs_hook=v.unique)
        with self.assertRaises(ValueError): v.digest('SECRET')

    def test_whole_failed_arm_preserves_original_outcome_and_private_paths(self):
        test_root = Path(__file__).parent / '.pure-test-temp'; test_root.mkdir(exist_ok=True)
        try:
            with tempfile.TemporaryDirectory(dir=test_root) as temp:
                arm = Path(temp)
                (arm/'results'/'c').mkdir(parents=True); (arm/'fixture'/RUN).mkdir(parents=True)
                (arm/'temp').mkdir()
                source = arm/'source'; source.mkdir()
                app = source/'OpenClaw.Tray.WinUI.exe'
                invocation = dict(arm='main', platform='x64', sourceCommit=v.PINS['main'][0], sourceTree=v.PINS['main'][1], pipelineCommit='c'*40,
                    appPath=str(app), processId=11, processStartUtc=START, startedUtc=START, finishedUtc=FINISH, exitCode=1, timedOut=False,
                    appSha256='d'*64, appAssemblySha256='e'*64, testAssemblySha256='f'*64)
                profile = arm/'temp'/'openclaw-gateway-fixture-1234abcd'
                run = dict(RunId=RUN, scenario='fractional-voice-history', architecture='X64', outcome='failed', appPath=str(app),
                    appSha256='d'*64, appAssemblySha256='e'*64, appProcessId=12, appProcessStartUtc=START, fixtureProcessId=13,
                    fixtureProcessStartUtc=START, recordedAt=FINISH, profileDirectory=str(profile/'profile'), roamingRoot=str(profile/'roaming'),
                    localRoot=str(profile/'local'), setupDirectory=str(profile/'local'/'OpenClawTray'), error='SECRET', appStatus={'private':'SECRET'})
                for path, obj in [(arm/'invocation.json', invocation), (arm/'fixture'/RUN/'run.json', run),
                    (arm/'fixture'/RUN/'failure-lifecycle.json', sample_lifecycle()),
                    (arm/'fixture'/RUN/'fractional-voice-tail-observation.json', sample_voice())]:
                    path.write_text(json.dumps(obj))
                (arm/'results'/'c'/'voice.trx').write_bytes(sample_trx(False))
                (arm/'fixture'/RUN/'openclaw-tray.log').write_text('[ignored unrelated log SECRET]\n')
                v.curate(arm)
                safe = json.loads((arm/'candidate.json').read_text())
                self.assertEqual(safe['platform'], 'x64')
                self.assertEqual(safe['result']['failed'], 1)
                self.assertFalse(safe['cleanupQualified']) # OS observations still required by PowerShell owner.
                self.assertNotIn('SECRET', json.dumps(safe))
                self.assertNotIn(str(profile), json.dumps(safe))
                self.assertEqual(json.loads((arm/'ownership.json').read_text())['appProcessId'], 12)
                invocation['exitCode'] = 0; run['outcome'] = 'passed'
                (arm/'invocation.json').write_text(json.dumps(invocation))
                (arm/'fixture'/RUN/'run.json').write_text(json.dumps(run))
                (arm/'results'/'c'/'voice.trx').write_bytes(sample_trx())
                observed = sample_voice(); observed['WaitSucceeded'] = True
                (arm/'fixture'/RUN/'fractional-voice-tail-observation.json').write_text(json.dumps(observed))
                v.curate(arm)
                self.assertEqual(json.loads((arm/'candidate.json').read_text())['result']['passed'], 1)
                invocation['platform'] = 'AnyCPU'; (arm/'invocation.json').write_text(json.dumps(invocation))
                with self.assertRaises(ValueError): v.curate(arm)
                invocation['platform'] = 'x64'; (arm/'invocation.json').write_text(json.dumps(invocation))
                run['appProcessId'] = None; (arm/'fixture'/RUN/'run.json').write_text(json.dumps(run))
                with self.assertRaises(ValueError): v.curate(arm)
        finally:
            test_root.rmdir()

if __name__ == '__main__':
    unittest.main()
