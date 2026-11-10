"""Strict finite diagnostic schema. No transport, help-exit, or capture approval."""
import copy,json,os,sys
from pathlib import Path
import project_candidates as p
import validate_format as old
from validate_report import require,keys,integer,boolean,text,decode,CAP

def validate_phases(v):
    keys(v,'versionCount versionState major minor build revision versionAgreement usageCount usageForm clauses')
    require(v['versionCount'] in {'unobserved','zero','one','multiple'})
    require(v['versionState'] in {'unobserved','absent','ambiguous','malformed','unsupported','mismatch','expected'})
    for k in ('major','minor','build','revision'):integer(v[k],0,65535,True)
    if v['versionAgreement'] is not None:boolean(v['versionAgreement'])
    if v['versionCount'] in {'unobserved','zero','multiple'}:
        require(v['versionState']=={'unobserved':'unobserved','zero':'absent','multiple':'ambiguous'}[v['versionCount']])
        require(all(v[k] is None for k in ('major','minor','build','revision','versionAgreement')))
    else:
        require(v['versionState'] in {'malformed','unsupported','mismatch','expected'})
        if v['major'] is None:
            require(v['versionState'] in {'malformed','unsupported'} and all(v[k] is None for k in ('minor','build','revision','versionAgreement')))
        else:
            require(v['minor'] is not None and v['versionState']!='malformed')
            require(v['revision'] is None or v['build'] is not None)
            numbers=[v['major'],v['minor']]+([] if v['build'] is None else [v['build']])+([] if v['revision'] is None else [v['revision']])
            agrees=numbers==[12,2,0,0][:len(numbers)]
            require(v['versionAgreement'] is agrees)
            require(v['versionState']==('mismatch' if not agrees else 'expected' if numbers==[12,2] else 'unsupported'))
    require(v['usageCount'] in {'unobserved','zero','one','multiple'} and v['usageForm'] in {'unobserved','absent','valid','invalid','ambiguous'})
    if v['usageCount']=='one':require(v['usageForm'] in {'valid','invalid'})
    else:require(v['usageForm']=={'unobserved':'unobserved','zero':'absent','multiple':'ambiguous'}[v['usageCount']])
    keys(v['clauses'],' '.join(p.semantic.CLAUSES))
    for value in v['clauses'].values():require(value in {'unobserved','recognized','missing','unrecognized','ambiguous'})

def validate_candidate(c,stream):
    keys(c,'id state bomPolicy decode roundTrip kinds occurrences limits phases analysis conformant')
    require(c['id'] in p.IDS)
    require(c['state'] in {'unobserved','read-failed','file-limit','input-changed','input-limit','bom-conflict','odd-length','decode-failed','roundtrip-failed','control-rejected','line-limit','derived-limit','inspected'})
    require(c['bomPolicy'] in {'unobserved','explicit-matching','no-bom-hypothesis'})
    require(c['decode'] in {'unobserved','passed','failed'} and c['roundTrip'] in {'unobserved','passed','failed'})
    require(c['limits'] in {'unobserved','input-limit','line-limit','derived-limit','passed'})
    keys(c['kinds'],' '.join(p.KINDS))
    for value in c['kinds'].values():boolean(value)
    require(c['occurrences'] in {'unobserved','none','one','multiple'})
    validate_phases(c['phases']);boolean(c['conformant'])
    a=c['analysis'];keys(a,'scope encoding inputSha256 inputBytes status reason')
    require(a['scope']=='derived-analysis-input' and a['encoding']=='utf8')
    text(a['inputSha256'],r'[a-f0-9]{64}',True);integer(a['inputBytes'],0,p.semantic.MAX_INPUT,True)
    require(a['status'] in {'unobserved','recognized','unrecognized','empty','unsupported'} and a['reason'] in p.semantic.REASONS)
    s=c['state'];early={'unobserved','read-failed','file-limit','input-changed','input-limit','bom-conflict'}
    if s in early:
        if s in {'unobserved','read-failed','file-limit','input-changed'}:require(s==stream['state'])
        else:require(stream['state']=='observed')
        expected=p.candidate_initial(c['id'],s)
        if s=='input-limit':expected['limits']='input-limit'
        require(c==expected)
        if s=='input-limit':require(stream['inputBytes']>p.semantic.MAX_INPUT)
        if s=='bom-conflict':require(stream['bom'] not in {'none','unobserved',c['id']})
        return
    require(stream['state']=='observed' and stream['inputBytes']<=p.semantic.MAX_INPUT)
    require(stream['bom'] in {'none',c['id']})
    require(c['bomPolicy']==('no-bom-hypothesis' if stream['bom']=='none' else 'explicit-matching'))
    if s=='odd-length':require(c['id']!='utf8' and stream['inputBytes']%2==1)
    if s in {'odd-length','decode-failed'}:
        require(c['decode']==('failed' if s=='decode-failed' else 'unobserved') and c['roundTrip']=='unobserved')
    else:
        require(c['decode']=='passed')
        require(c['roundTrip']==('failed' if s=='roundtrip-failed' else 'passed'))
    if s in {'odd-length','decode-failed','roundtrip-failed'}:
        require(c['occurrences']=='unobserved' and not any(c['kinds'].values()) and c['limits']=='unobserved')
    else:
        require(c['occurrences'] in {'none','one','multiple'})
        require(any(c['kinds'].values())==(c['occurrences'] in {'one','multiple'}))
        if c['occurrences']=='one':require(sum(c['kinds'].values())==1)
        require((s=='control-rejected')==any(c['kinds'].values()))
        require(c['limits']=={'control-rejected':'unobserved','line-limit':'line-limit','derived-limit':'derived-limit','inspected':'passed'}[s])
    if s!='inspected':
        require(c['phases']==p.phases.empty() and a==p.candidate_initial(c['id'])['analysis'] and c['conformant'] is False);return
    require(a['inputSha256'] is not None and a['inputBytes'] is not None and a['status']!='unobserved')
    q=c['phases'];require(q['versionCount']!='unobserved' and q['usageCount']!='unobserved' and 'unobserved' not in q['clauses'].values())
    if a['status']=='empty':
        require(a['reason']=='empty' and q['versionState']=='absent' and q['usageCount']=='zero' and set(q['clauses'].values())=={'missing'})
    else:
        states=set(q['clauses'].values())
        if q['versionState'] in {'absent','malformed'}:expected_status,reason='unrecognized','missing-version'
        elif q['versionState']=='ambiguous':expected_status,reason='unrecognized','ambiguous-version'
        elif q['versionState']=='mismatch':expected_status,reason='unrecognized','version-mismatch'
        elif q['versionState']=='unsupported':expected_status,reason='unsupported','unsupported-version'
        elif states=={'recognized'}:expected_status,reason='recognized','none'
        elif 'ambiguous' in states:expected_status,reason='unrecognized','ambiguous-clause'
        elif 'unrecognized' in states:expected_status,reason='unrecognized','unrecognized-clause'
        else:expected_status,reason='unrecognized','missing-clause'
        require(a['status']==expected_status and a['reason']==reason)
    expected=(a['status']=='recognized' and q['versionState']=='expected' and q['usageForm']=='valid' and all(v=='recognized' for v in q['clauses'].values()))
    require(c['conformant'] is expected)
    if a['status']=='recognized':require(expected)

def validate_stream(s):
    keys(s,'state inputSha256 inputBytes bom candidates conformance')
    require(s['state'] in {'unobserved','observed','read-failed','file-limit','input-changed'})
    text(s['inputSha256'],r'[a-f0-9]{64}',True);integer(s['inputBytes'],0,p.semantic.MAX_FILE,True)
    require(s['bom'] in {'unobserved','none','utf8','utf16le','utf16be'})
    require(type(s['candidates']) is list and len(s['candidates'])==3)
    require([c.get('id') for c in s['candidates']]==list(p.IDS))
    for c in s['candidates']:validate_candidate(c,s)
    if s['state']!='observed':require(s==p.empty_stream(s['state']));return
    require(s['inputSha256'] is not None and s['inputBytes'] is not None and s['bom']!='unobserved')
    keys(s['conformance'],'count id');require(s['conformance']==p.summarize(s['candidates']))

def validate_observation(o):
    keys(o,'schema transportApproved behaviorVerified baseline stdout stderr');integer(o['schema'],1,1)
    require(o['transportApproved'] is False and o['behaviorVerified'] is False)
    old.validate_observation(o['baseline']);validate_stream(o['stdout']);validate_stream(o['stderr'])
    s=o['stdout'];b=o['baseline']['semantic']
    if s['state']=='observed':require(s['inputSha256']==b['inputSha256'] and s['inputBytes']==b['inputBytes'])
    e=o['stderr'];presence=o['baseline']['stderr']
    if e['state']=='observed' and presence['state']=='observed':
        require(e['inputBytes']<=p.baseline.STDERR_CAP and presence['presence']==('empty' if e['inputBytes']==0 else 'nonempty'))
    if e['state']=='observed' and presence['state']=='limit':require(e['inputBytes']>p.baseline.STDERR_CAP)
    if s['state']=='observed' and e['state']=='observed':require(s['inputBytes']+e['inputBytes']<=p.semantic.MAX_FILE)
    # A candidate refusal cannot erase or upgrade the unchanged baseline.

def validate(r):
    keys(r,'schema mode captureEnabled captureAdmission qualificationPassed transportApproved priorEvidence formatEvidence productCommit productTree pipelineCommit runId runAttempt stage failure observationComplete tool observation setupExitCode requestedWrapperExitCode failureWitness privacy cleanup dumpFiles')
    integer(r['schema'],1,1);boolean(r['observationComplete']);require(r['mode']=='ToolHelpCandidates' and r['transportApproved'] is False and r['formatEvidence']==p.FORMAT_REFERENCE)
    keys(r['tool'],'attempted identityState identityStable archiveSha256 sha256 version signatureVerified architectureMatched nativeExitCode lifecycleSucceeded stdoutSha256 stderrSha256')
    text(r['tool']['stderrSha256'],r'[a-f0-9]{64}',True)
    validate_observation(r['observation'])
    # Reuse the existing exact source/tool/exit/custody/refusal envelope policy.
    # Its baseline-format completion gate is intentionally not the new mode's completion predicate.
    legacy=copy.deepcopy(r);del legacy['transportApproved'];del legacy['formatEvidence'];del legacy['tool']['stderrSha256']
    legacy['mode']='ToolHelpFormat';legacy['observation']=r['observation']['baseline'];legacy['observationComplete']=False
    old.validate(legacy)
    if not r['tool']['attempted']:require(r['tool']['stderrSha256'] is None)
    for name in ('stdout','stderr'):
        s=r['observation'][name]
        if s['state']=='observed':require(r['tool']['attempted'] and s['inputSha256']==r['tool'][name+'Sha256'])
    if r['observationComplete']:
        require(r['stage']=='complete' and r['failure']=='qualification-unreviewed' and r['failureWitness'] is None)
        require(r['tool']['attempted'] and r['tool']['lifecycleSucceeded'] and r['tool']['identityStable'] and r['tool']['nativeExitCode'] is not None)
        require(all(r['privacy'].values()) and all(r['cleanup'].values()) and r['dumpFiles']==0)
        require(all(r['observation'][name]['state']=='observed' for name in ('stdout','stderr')))
        require(r['observation']['baseline']['responseComparison']!='unobserved')
    data=json.dumps(r,sort_keys=True,separators=(',',':'),ensure_ascii=True,allow_nan=False).encode()+b'\n';require(len(data)<=CAP);return data

def main():
    try:
        require(len(sys.argv)==3);checking=sys.argv[1]=='--check';source=Path(sys.argv[2] if checking else sys.argv[1]);target=None if checking else Path(sys.argv[2])
        if target is not None:require(source.resolve()!=target.resolve() and not target.exists())
        with source.open('rb') as f:r=decode(f.read(CAP+1))
        for key,env in [('pipelineCommit','GITHUB_SHA'),('runId','GITHUB_RUN_ID'),('runAttempt','GITHUB_RUN_ATTEMPT')]:require(r[key]==os.environ.get(env))
        data=validate(r)
        if target is not None:
            with target.open('xb') as f:f.write(data)
        print('tool-help-candidates-receipt-validated');return 0
    except Exception:print('tool-help-candidates-receipt-rejected');return 1
if __name__=='__main__':sys.exit(main())
