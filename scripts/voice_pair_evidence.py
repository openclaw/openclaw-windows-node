"""Fixed one-fact curator. Called only after the owned invocation exits.
Usage: python voice_pair_evidence.py PRIVATE_ARM_ROOT
No process actions, archive/profile copies, network access or arbitrary log export.
"""
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import stat
import sys
import xml.etree.ElementTree as ET

FACT = 'OpenClaw.Tray.UITests.GatewayFixtureUiTests.HistoricalFractionalVoiceConversationRendersWithoutLoadingPlaceholder'
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
PINS = {
    'main': ('1c223f14f6b66cf0928eeb8686f34208b5ea4d51', '807162139a790fd5fb88c29217688db3f7448ab9'),
    'approval': ('1e60d1135ae50d3a37525c7cdc3433b81123b4f3', '22ba737e3122d589787701a16ee2e2bbd234bdb0'),
}

def require(ok):
    if not ok:
        raise ValueError('Fixed voice comparison evidence contract rejected')

def read_bytes(path, limit=2_097_152):
    path = Path(path)
    for parent in [path, *path.parents]:
        info = parent.lstat()
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, 'st_file_attributes', 0) & 0x400)
    require(path.is_file() and 0 < path.stat().st_size <= limit)
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    require(0 < len(data) <= limit)
    return data

def unique(pairs):
    out = {}
    for key, value in pairs:
        require(key not in out)
        out[key] = value
    return out

def read_json(path):
    return json.loads(read_bytes(path).decode('utf-8-sig'), object_pairs_hook=unique,
                      parse_constant=lambda _: require(False))

def stamp(value):
    require(isinstance(value, str) and len(value) <= 40)
    value = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(value.tzinfo is not None)
    return value

def integer(value, low=0, high=2**31-1):
    require(type(value) is int and low <= value <= high)
    return value

def digest(value, size=64):
    require(isinstance(value, str) and re.fullmatch('[a-fA-F0-9]{' + str(size) + '}', value))
    return value.lower()

def trx(data, started, finished, exit_code):
    text = data.decode('utf-8-sig')
    require('<!DOCTYPE' not in text.upper() and '<!ENTITY' not in text.upper())
    root = ET.fromstring(text)
    q = lambda name: '{' + NS + '}' + name
    require(root.tag == q('TestRun'))
    rows = root.findall('./' + q('Results') + '/' + q('UnitTestResult'))
    require(len(rows) == 1 and len(list(root.iter(q('UnitTestResult')))) == 1)
    row = rows[0]
    require(row.get('testName') == FACT and row.get('outcome') in ('Passed', 'Failed'))
    for field in ('executionId', 'testId'):
        require(re.fullmatch(r'[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}', row.get(field, '')) is not None)
    times = root.find(q('Times')); summary = root.find(q('ResultSummary'))
    require(times is not None and summary is not None)
    counts = summary.find(q('Counters')); require(counts is not None)
    passed = row.get('outcome') == 'Passed'
    require(summary.get('outcome') in (('Passed', 'Completed') if passed else ('Failed',)))
    require(exit_code == (0 if passed else 1))
    expected = {'total': 1, 'executed': 1, 'passed': int(passed), 'failed': int(not passed), 'notExecuted': 0}
    require(all(counts.get(k) == str(v) for k, v in expected.items()))
    require(all(k in expected or v == '0' for k, v in counts.attrib.items()))
    low, high = stamp(started) - dt.timedelta(seconds=1), stamp(finished) + dt.timedelta(seconds=1)
    for element, fields in ((times, ('start', 'finish')), (row, ('startTime', 'endTime'))):
        a, b = [stamp(element.get(k)) for k in fields]
        require(low <= a <= b <= high)
    require(re.fullmatch(r'\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?', row.get('duration', '')) is not None)
    # A derivative TRX: exact one-case identity/times/counts/outcome, NO stdout,
    # errors, stack, machine/user/path, attachments or run-settings payloads.
    ET.register_namespace('', NS)
    safe = ET.Element(q('TestRun'))
    ET.SubElement(safe, q('Times'), {k: times.get(k) for k in ('start', 'finish')})
    results = ET.SubElement(safe, q('Results'))
    ET.SubElement(results, q('UnitTestResult'), {k: row.get(k) for k in
        ('testName', 'executionId', 'testId', 'outcome', 'startTime', 'endTime', 'duration')})
    ss = ET.SubElement(safe, q('ResultSummary'), {'outcome': summary.get('outcome')})
    ET.SubElement(ss, q('Counters'), {k: str(v) for k, v in expected.items()})
    return {'method': FACT, 'outcome': row.get('outcome'), 'duration': row.get('duration'), **expected}, ET.tostring(safe, encoding='utf-8', xml_declaration=True)

def voice(value):
    require(set(value) == {'History', 'PollCount', 'LastPoll', 'WaitSucceeded', 'WaitFinishedAtUtc'})
    integer(value['PollCount'], 1, 10000); require(type(value['WaitSucceeded']) is bool)
    stamp(value['WaitFinishedAtUtc'])
    groups = {
        'History': 'ObservedAtUtc ReadSucceeded ComposeTargetMatches HistoryLoaded ReturnedEntryCount ExpectedContentMatchCount FinalEntryMatches',
        'LastPoll': 'StartedAtUtc CompletedAtUtc ComposerPresent SelectedThreadMatches RenderConsumedHistory TimelinePresent FinalElementPresent FinalOffscreen FinalHasPositiveBounds FinalWithinVerticalViewport FinalOverlapsHorizontalViewport FinalVisible',
    }
    for name, fields in groups.items():
        item = value[name]; require(isinstance(item, dict) and set(item) == set(fields.split()))
        for key, scalar in item.items():
            if scalar is None:
                continue
            if key.endswith('Utc'):
                stamp(scalar)
            elif key in ('ReturnedEntryCount', 'ExpectedContentMatchCount'):
                integer(scalar, 0, 10000)
            else:
                require(type(scalar) is bool)
    return value

def lifecycle(value, run_id):
    require(value['runId'] == run_id and value['outcome'] == 'failed')
    stages = value['stages']; require(type(stages) is list and len(stages) == 6)
    require(stages[0] == 'release:completed' and stages[2:] == [
        'cancel-capture:completed', 'stop:completed', 'drain:completed', 'dispose:completed'])
    capture = stages[1]
    require(capture in ('capture:completed', 'capture:timeout', 'capture:skipped-running-body')
            or re.fullmatch(r'capture:error:[A-Za-z][A-Za-z0-9]{0,127}', capture))
    # The existing receipt is written before the receipt phase itself. Completed
    # single-case TRX + returned invocation provide the later completion boundary.
    return [stages[0], 'capture:error' if capture.startswith('capture:error:') else capture, *stages[2:]]

TAIL_FIELDS = set('frames retries settledFrames hasExpectedTarget hasCorrelation bringCompleted attemptActive itemCount tailIndex nativeReadSucceeded offset scrollableHeight viewportHeight state bringReadSucceeded proposedOffset bringOffset bringScrollableHeight targetIndexAvailable targetIndex completionReadSucceeded completedOffset completedScrollableHeight completionSourceReadSucceeded completionSourceItemMatches maxOffsetAvailable maxObservedOffset bringCancelReadSucceeded bringCancelAtCallback bringAnimationDesiredReadSucceeded bringAnimationDesiredAtCallback'.split())
MARKERS = ['Chat initial tail navigation did not land at the tail.', 'Chat tail navigation could not be dispatched.',
           'Chat initial tail navigation retry could not start.', 'Chat initial tail navigation retry could not be dispatched.']

def tail_warnings(data):
    out = []
    for line in data.decode('utf-8-sig').splitlines():
        match = re.fullmatch(r'\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\] \[WARN\] (.*)', line)
        if not match:
            continue
        for index, marker in enumerate(MARKERS):
            if not match[2].startswith(marker):
                continue
            require(len(out) < 32)
            fields = {}
            rest = match[2][len(marker):].strip()
            if index == 0:
                for token in rest.split():
                    key, value = token.split('=', 1)
                    require(key in TAIL_FIELDS and key not in fields)
                    require(re.fullmatch(r'(?:True|False|NaN|[-+]?Infinity|[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?)?', value) is not None)
                    fields[key] = value # Only validated Boolean/numeric/empty literals.
                require({'frames', 'retries', 'tailIndex', 'itemCount'} <= set(fields))
            else:
                require(not rest)
            out.append({'kind': index, 'loggerLocalTime': match[1], 'timezoneVerified': False, 'fields': fields})
    return out

def curate(arm):
    invocation = read_json(arm / 'invocation.json')
    case = invocation['arm']; require(case in PINS)
    sha, tree = PINS[case]
    require(invocation['sourceCommit'] == sha and invocation['sourceTree'] == tree)
    require(invocation['platform'] == 'x64')
    digest(invocation['pipelineCommit'], 40)
    digest(invocation['testAssemblySha256']); integer(invocation['exitCode'], 0, 1)
    integer(invocation['processId'], 1); stamp(invocation['processStartUtc'])
    start, finish = stamp(invocation['startedUtc']), stamp(invocation['finishedUtc'])
    require(start <= stamp(invocation['processStartUtc']) <= finish and invocation['timedOut'] is False)
    reports = list((arm / 'results').glob('*/voice.trx')); require(len(reports) == 1)
    raw = read_bytes(reports[0]); case_result, safe_trx = trx(raw, invocation['startedUtc'], invocation['finishedUtc'], invocation['exitCode'])
    runs = list((arm / 'fixture').glob('*/run.json')); require(len(runs) == 1)
    run = read_json(runs[0]); run_id = run['RunId']
    require(re.fullmatch('[a-f0-9]{32}', run_id) is not None and runs[0].parent.name == run_id)
    require(run['scenario'] == 'fractional-voice-history' and run['architecture'] == 'X64')
    require(run['outcome'] == ('passed' if case_result['passed'] else 'failed'))
    require(Path(run['appPath']).resolve() == Path(invocation['appPath']).resolve())
    for key in ('appSha256', 'appAssemblySha256'):
        require(digest(run[key]) == digest(invocation[key]))
    for kind in ('app', 'fixture'):
        integer(run[kind + 'ProcessId'], 1)
        require(start <= stamp(run[kind + 'ProcessStartUtc']) <= finish)
    require(len({invocation['processId'], run['appProcessId'], run['fixtureProcessId']}) == 3)
    require(start <= stamp(run['recordedAt']) <= finish)
    # A private TEMP per arm confines the fixture's existing TempDirectory owner.
    profile = Path(run['profileDirectory']).parent
    require(profile.parent.resolve() == (arm / 'temp').resolve())
    require(re.fullmatch('openclaw-gateway-fixture-[a-f0-9]{8}', profile.name) is not None)
    require(Path(run['profileDirectory']) == profile / 'profile' and Path(run['roamingRoot']) == profile / 'roaming'
            and Path(run['localRoot']) == profile / 'local' and Path(run['setupDirectory']) == profile / 'local' / 'OpenClawTray')
    owned = {k: run[k] for k in ('appProcessId', 'appProcessStartUtc', 'fixtureProcessId', 'fixtureProcessStartUtc')}
    owned['profileRoot'] = str(profile)
    (arm / 'ownership.json').write_text(json.dumps(owned), encoding='utf-8') # PRIVATE, never uploaded.
    stages = lifecycle(read_json(runs[0].parent / 'failure-lifecycle.json'), run_id) if case_result['failed'] else ['passed-case-returned-through-await-dispose']
    observation = voice(read_json(runs[0].parent / 'fractional-voice-tail-observation.json'))
    for value in (observation['History']['ObservedAtUtc'], observation['LastPoll']['StartedAtUtc'],
                  observation['LastPoll']['CompletedAtUtc'], observation['WaitFinishedAtUtc']):
        if value is not None:
            require(start <= stamp(value) <= finish)
    require(not case_result['passed'] or observation['WaitSucceeded'])
    warning_path = runs[0].parent / 'openclaw-tray.log'
    warnings = tail_warnings(read_bytes(warning_path))
    safe = {k: invocation[k] for k in ('arm', 'sourceCommit', 'sourceTree', 'pipelineCommit', 'platform', 'processId',
            'processStartUtc', 'startedUtc', 'finishedUtc', 'exitCode', 'appSha256', 'appAssemblySha256', 'testAssemblySha256')}
    safe.update(schema='focused-voice-pair-arm-v1', result=case_result, runId=run_id,
                appProcessId=run['appProcessId'], appProcessStartUtc=run['appProcessStartUtc'],
                fixtureProcessId=run['fixtureProcessId'], fixtureProcessStartUtc=run['fixtureProcessStartUtc'],
                rawTrxSha256=hashlib.sha256(raw).hexdigest(), fixtureFailureStages=stages,
                cachedVoice=observation, tailWarnings=warnings, cleanupQualified=False)
    (arm / 'candidate.json').write_text(json.dumps(safe, indent=2), encoding='utf-8')
    (arm / 'sanitized.trx').write_bytes(safe_trx)

if __name__ == '__main__':
    try:
        curate(Path(sys.argv[1]).resolve())
    except Exception:
        # Parser/native errors can quote raw input. Never print traceback/content.
        print('Focused voice evidence admission failed; no raw evidence exported.')
        sys.exit(2)
