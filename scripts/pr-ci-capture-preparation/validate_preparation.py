"""Only operational PrepareAndBarrier JSON may leave the private invocation."""
import json,os,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'pr-ci-observe-only'))
from project_procdump_help import CLAUSES,GRAMMAR,REASONS,MAX_FILE
from validate_report import require,keys,integer,boolean,text,decode,CAP,COMMIT,TREE,SOURCE_STEPS,NATIVE_PHASES,FAILURE_CATEGORIES

def validate_help_projection(h,input_hash,file_version):
    keys(h,'schema status reason grammarId scope behaviorVerified inputSha256 inputBytes encoding helpMajor helpMinor versionAgreement capabilities clauses')
    integer(h['schema'],1,1);require(h['status'] in {'not-observed','recognized','unrecognized','empty','unsupported'} and h['reason'] in REASONS)
    require(h['grammarId'] in {None,GRAMMAR} and h['scope']=='documented-help-forms' and h['behaviorVerified'] is False)
    text(h['inputSha256'],r'[a-f0-9]{64}',True);integer(h['inputBytes'],0,MAX_FILE,True)
    if h['inputSha256'] is not None:require(h['inputSha256']==input_hash and h['inputBytes'] is not None)
    else:require(h['inputBytes'] is None)
    require(h['encoding'] in {'unknown','utf8','utf16le','utf16be'})
    integer(h['helpMajor'],0,65535,True);integer(h['helpMinor'],0,65535,True)
    if h['versionAgreement'] is not None:boolean(h['versionAgreement'])
    if h['helpMajor'] is None:require(h['helpMinor'] is None and h['versionAgreement'] is None)
    else:require(h['helpMinor'] is not None and h['versionAgreement'] is not None)
    if h['versionAgreement']:
        require(type(file_version) is str)
        parts=file_version.split('.')
        require(len(parts)==4 and all(p.isascii() and p.isdigit() for p in parts))
        require(tuple(map(int,parts[:2]))==(h['helpMajor'],h['helpMinor']))
    keys(h['capabilities'],' '.join(CLAUSES));keys(h['clauses'],' '.join(CLAUSES))
    for key in CLAUSES:
        boolean(h['capabilities'][key]);require(h['clauses'][key] in {'unobserved','recognized','missing','unrecognized','ambiguous'})
        require(h['capabilities'][key] is (h['clauses'][key]=='recognized'))
    if h['grammarId'] is not None:
        require(h['helpMajor']==12 and h['helpMinor']==2 and h['versionAgreement'] is True and h['encoding']!='unknown' and h['status'] in {'recognized','unrecognized'})
        require(h['inputSha256'] is not None and 0<h['inputBytes']<=2*1024*1024 and 'unobserved' not in h['clauses'].values())
        if h['status']=='unrecognized':
            states=set(h['clauses'].values());expected='ambiguous-clause' if 'ambiguous' in states else 'unrecognized-clause' if 'unrecognized' in states else 'missing-clause'
            require(h['reason']==expected)
    else:require(not any(h['capabilities'].values()) and set(h['clauses'].values())<={'unobserved','missing'})
    if h['status']=='recognized':require(h['grammarId']==GRAMMAR and h['reason']=='none' and all(h['capabilities'].values()))
    else:require(not all(h['capabilities'].values()))
    if h['status']=='not-observed':require(h['reason']=='none' and h['inputSha256'] is None and h['grammarId'] is None and h['helpMajor'] is None and h['encoding']=='unknown' and set(h['clauses'].values())=={'unobserved'})
    elif h['status']=='empty':require(h['reason']=='empty')
    elif h['status']=='unsupported':require(h['reason']=='unsupported-version' and h['grammarId'] is None)
    elif h['status']=='unrecognized':require(h['reason']!='none')

def validate(r):
    keys(r,'schema mode captureEnabled captureAdmission productCommit productTree pipelineCommit runId runAttempt stage failure preparePassed sdkVersion producerContract fixtureSha256 observerSha256 adapterSha256 adapterVersion tools winapp cases controls privacy cleanup dumpFiles setupExitCode failureWitness')
    integer(r['schema'],2,2);require(r['mode']=='PrepareAndBarrier' and r['captureEnabled'] is False and r['captureAdmission']=='not-reviewed')
    require(r['productCommit']==COMMIT and r['productTree']==TREE);text(r['pipelineCommit'],r'[a-f0-9]{40}');text(r['runId'],r'[1-9][0-9]{0,19}');require(r['runAttempt']=='1')
    require(r['stage'] in {'initialization','winapp','source','policies','build','tools','barrier-async','barrier-sync','barrier-deny','complete'})
    require(r['failure'] in {'none','preparation-incomplete','tool-contract-unreviewed','cdb-unavailable'});boolean(r['preparePassed'])
    text(r['sdkVersion'],r'10\.0\.[0-9]{3}',True);require(r['producerContract'] in {None,'18.7'})
    for k in ['fixtureSha256','observerSha256','adapterSha256']:text(r[k],r'[a-f0-9]{64}',True)
    text(r['adapterVersion'],r'[0-9]{1,10}(\.[0-9]{1,10}){3}',True)
    require(r['winapp'] in {'unobserved','missing','ambiguous','untrusted','verified-file'})
    keys(r['controls'],'baselineManaged baselinePowerShell barrierPolicy archivePolicy')
    keys(r['privacy'],'privateAclVerified outsideArtifactRoot explicitUploadOnly');keys(r['cleanup'],'ownedProcessesJoined privateRootDeleted')
    for group in ('controls','privacy','cleanup'):
        for value in r[group].values():boolean(value)
    integer(r['dumpFiles'],0,256,True);integer(r['setupExitCode'],-2**31,2**31-1,True)
    w=r['failureWitness']
    if w is not None:
        keys(w,'sourceStep nativePhase category win32ErrorAvailable win32Error');require(w['sourceStep'] in SOURCE_STEPS and w['nativePhase'] in NATIVE_PHASES and w['category'] in FAILURE_CATEGORIES);boolean(w['win32ErrorAvailable'])
        if w['win32ErrorAvailable']:
            require(w['category']=='NativeApi' and w['nativePhase'] not in {'None','Preparation','Environment','Completed'});integer(w['win32Error'],-2**31,2**31-1);require(w['win32Error']!=0)
        else:require(w['win32Error'] is None)
    require(type(r['tools']) is list and len(r['tools'])<=2);roles={}
    for t in r['tools']:
        keys(t,'role status sha256 version archiveSha256 helpSha256 nativeExitCode signatureVerified architectureMatched helpProjection')
        require(t['role'] in {'procdump','cdb'} and t['role'] not in roles);roles[t['role']]=t
        require(t['status'] in {'unreviewed-contract','contract-reviewed','missing','ambiguous','untrusted','verified-file','cli-verified','unrecognized-output'})
        for k in ['sha256','archiveSha256','helpSha256']:text(t[k],r'[a-f0-9]{64}',True)
        text(t['version'],r'[0-9]{1,10}(\.[0-9]{1,10}){3}',True);integer(t['nativeExitCode'],-2**31,2**31-1,True);boolean(t['signatureVerified']);boolean(t['architectureMatched'])
        if t['role']=='cdb':require(t['archiveSha256'] is None and t['helpProjection'] is None and t['status'] not in {'unreviewed-contract','contract-reviewed'})
        else:
            require(t['status'] in {'unreviewed-contract','contract-reviewed'} and t['archiveSha256'] is not None)
            validate_help_projection(t['helpProjection'],t['helpSha256'],t['version'])
            if t['status']=='contract-reviewed':require(t['helpProjection']['status']=='recognized')
        if t['status'] in {'contract-reviewed','cli-verified'}:
            require(t['signatureVerified'] and t['architectureMatched'] and all(t[k] is not None for k in ['sha256','version','helpSha256','nativeExitCode']))
            if t['role']=='cdb':require(t['nativeExitCode']==0)
    require(type(r['cases']) is list and len(r['cases'])==3)
    for mode,c in zip(('async','sync','deny'),r['cases']):
        keys(c,'mode attempted sourceBound bodySawAck callbackHeld expectedAbort nativeExitCode joined executed passedCount skipped passed');require(c['mode']==mode)
        for k in ['attempted','sourceBound','callbackHeld','expectedAbort','joined','passed']:boolean(c[k])
        if c['bodySawAck'] is not None:boolean(c['bodySawAck'])
        integer(c['nativeExitCode'],-2**31,2**31-1,True)
        for k in ['executed','passedCount','skipped']:integer(c[k],0,1,True)
        if c['passed']:
            require(c['attempted'] and c['sourceBound'] and c['joined'])
            if mode=='deny':require(c['callbackHeld'] and c['expectedAbort'] and c['nativeExitCode']==126 and c['bodySawAck'] is None and all(c[k] is None for k in ['executed','passedCount','skipped']))
            else:require(not c['callbackHeld'] and not c['expectedAbort'] and c['nativeExitCode']==0 and c['bodySawAck'] is (mode=='sync') and c['executed']==c['passedCount']==1 and c['skipped']==0)
    if r['preparePassed']:
        require(r['stage']=='complete' and r['failure']=='none' and w is None and r['dumpFiles']==0 and r['setupExitCode']==0)
        require(r['sdkVersion']=='10.0.403' and r['producerContract']=='18.7' and all(r[k] is not None for k in ['fixtureSha256','observerSha256','adapterSha256','adapterVersion']) and r['adapterVersion'].startswith('3.1.5.'))
        require(all(all(r[g].values()) for g in ['controls','privacy','cleanup']) and all(c['passed'] for c in r['cases']))
        require(set(roles)=={'procdump','cdb'} and roles['procdump']['status']=='contract-reviewed' and roles['cdb']['status']=='cli-verified')
    output=json.dumps(r,sort_keys=True,separators=(',',':'),ensure_ascii=True,allow_nan=False).encode()+b'\n';require(len(output)<=CAP);return output

def main():
    try:
        require(len(sys.argv)==3);checking=sys.argv[1]=='--check';source=Path(sys.argv[2] if checking else sys.argv[1]);target=None if checking else Path(sys.argv[2])
        if target is not None:require(source.resolve()!=target.resolve() and not target.exists())
        with source.open('rb') as f:r=decode(f.read(CAP+1))
        for k,e in [('pipelineCommit','GITHUB_SHA'),('runId','GITHUB_RUN_ID'),('runAttempt','GITHUB_RUN_ATTEMPT')]:require(os.environ.get(e)==r[k])
        b=validate(r)
        if target is not None:
            with target.open('xb') as f:f.write(b)
        print('preparation-receipt-validated');return 0
    except Exception:print('preparation-receipt-rejected');return 1
if __name__=='__main__':sys.exit(main())
