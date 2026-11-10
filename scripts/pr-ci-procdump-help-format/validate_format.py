"""One strict ToolHelpFormat receipt, never a preparation/capture qualification."""
import json,os,sys
from pathlib import Path
from project_format import IDENTITY,KINDS
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'pr-ci-capture-preparation'))
from validate_preparation import validate_help_projection
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'pr-ci-observe-only'))
from validate_report import require,keys,integer,boolean,text,decode,CAP,COMMIT,TREE,SOURCE_STEPS,NATIVE_PHASES,FAILURE_CATEGORIES

def validate_observation(o):
    keys(o,'schema semantic format stderr responseComparison');integer(o['schema'],1,1)
    sem=o['semantic'];validate_help_projection(sem,sem.get('inputSha256'),IDENTITY['fileVersion'])
    f=o['format'];keys(f,'state reason bom kinds occurrences')
    require(f['state'] in {'unobserved','observed','read-failed','limit','encoding-failed'})
    require(f['reason'] in {'none','read-failed','input-changed','input-limit','line-limit','encoding'})
    require(f['bom'] in {'unobserved','none','utf8','utf16le','utf16be'})
    require(f['occurrences'] in {'unobserved','none','one','multiple'});keys(f['kinds'],' '.join(KINDS))
    for value in f['kinds'].values():boolean(value)
    if f['state']=='observed':
        require(sem['reason'] not in {'encoding','input-limit','line-limit','read-failed'})
        require(f['reason']=='none' and f['bom']!='unobserved' and f['occurrences']!='unobserved' and sem['inputSha256'] is not None)
        require((any(f['kinds'].values()))==(f['occurrences'] in {'one','multiple'}))
        if f['occurrences']=='one':require(sum(f['kinds'].values())==1)
        if any(f['kinds'].values()):require(sem['reason']=='control-character' or sem['status']=='empty')
        else:require(sem['reason']!='control-character')
        if sem['inputBytes']==0:require(f['bom']=='none' and f['occurrences']=='none')
        elif sem['encoding']!='unknown':require(sem['encoding']=={'none':'utf8','utf8':'utf8','utf16le':'utf16le','utf16be':'utf16be'}[f['bom']])
    else:
        require(not any(f['kinds'].values()) and f['occurrences']=='unobserved')
        if f['state']=='unobserved':require(f['reason']=='none' and f['bom']=='unobserved')
        elif f['state']=='read-failed':require(f['reason'] in {'read-failed','input-changed'} and f['bom']=='unobserved')
        elif f['state']=='encoding-failed':require(f['reason']=='encoding' and f['bom']!='unobserved' and sem['reason']=='encoding')
        else:
            require(f['reason'] in {'input-limit','line-limit'})
            require(sem['reason']==f['reason'])
    e=o['stderr'];keys(e,'state presence');require(e['state'] in {'unobserved','observed','read-failed','limit'} and e['presence'] in {'unobserved','empty','nonempty'})
    require((e['state']=='observed')==(e['presence']!='unobserved'))
    require(o['responseComparison'] in {'unobserved','same-response','new-response'})
    if o['responseComparison']!='unobserved':
        require(sem['inputSha256'] is not None and f['state'] not in {'unobserved','read-failed'})
        require((o['responseComparison']=='same-response')==(sem['inputSha256']==IDENTITY['previousStdoutSha256']))

def validate(r):
    keys(r,'schema mode captureEnabled captureAdmission qualificationPassed priorEvidence productCommit productTree pipelineCommit runId runAttempt stage failure observationComplete tool observation setupExitCode requestedWrapperExitCode failureWitness privacy cleanup dumpFiles')
    integer(r['schema'],1,1);require(r['mode']=='ToolHelpFormat' and r['captureEnabled'] is False and r['captureAdmission']=='not-reviewed' and r['qualificationPassed'] is False)
    require(r['productCommit']==COMMIT==IDENTITY['productCommit'] and r['productTree']==TREE==IDENTITY['productTree'])
    text(r['pipelineCommit'],r'[a-f0-9]{40}');text(r['runId'],r'[1-9][0-9]{0,19}');require(r['runAttempt']=='1')
    keys(r['priorEvidence'],'kind runId receiptSha256');require(r['priorEvidence']==dict(kind='reference',runId=IDENTITY['priorRunId'],receiptSha256=IDENTITY['priorReceiptSha256']))
    require(r['stage'] in {'initialization','source','acquire','identity','tool','project','complete'})
    require(r['failure'] in {'observation-incomplete','identity-refused','qualification-unreviewed'});boolean(r['observationComplete'])
    t=r['tool'];keys(t,'attempted identityState identityStable archiveSha256 sha256 version signatureVerified architectureMatched nativeExitCode lifecycleSucceeded stdoutSha256')
    boolean(t['attempted']);boolean(t['identityStable']);boolean(t['signatureVerified']);boolean(t['architectureMatched']);boolean(t['lifecycleSucceeded'])
    require(t['identityState'] in {'unobserved','archive-mismatch','binary-mismatch','version-mismatch','untrusted','matched'})
    for k in ['archiveSha256','sha256','stdoutSha256']:text(t[k],r'[a-f0-9]{64}',True)
    text(t['version'],r'[0-9]{1,10}(\.[0-9]{1,10}){3}',True);integer(t['nativeExitCode'],-2**31,2**31-1,True)
    if t['identityState']=='matched' or t['attempted']:
        require(t['archiveSha256']==IDENTITY['archiveSha256'] and t['sha256']==IDENTITY['executableSha256'] and t['version']==IDENTITY['fileVersion'] and t['signatureVerified'] and t['architectureMatched'] and t['identityState']=='matched')
    if not t['attempted']:require(t['nativeExitCode'] is None and not t['lifecycleSucceeded'] and not t['identityStable'] and t['stdoutSha256'] is None)
    if t['lifecycleSucceeded']:require(t['attempted'] and t['nativeExitCode'] is not None)
    validate_observation(r['observation'])
    h=r['observation']['semantic']['inputSha256']
    if h is not None:
        require(t['attempted'])
        if t['stdoutSha256']!=h:require(not r['observationComplete'] and r['observation']['format']['state']=='read-failed' and r['observation']['format']['reason']=='input-changed' and r['observation']['responseComparison']=='unobserved')
    integer(r['setupExitCode'],-2**31,2**31-1,True);integer(r['requestedWrapperExitCode'],-2**31,2**31-1)
    expected=t['nativeExitCode'] if t['nativeExitCode'] not in {None,0} else r['setupExitCode'] if r['setupExitCode'] not in {None,0} else 1
    require(r['requestedWrapperExitCode']==expected)
    for group,names in [('privacy','privateAclVerified outsideArtifactRoot explicitUploadOnly'),('cleanup','ownedProcessesJoined privateRootDeleted')]:
        keys(r[group],names)
        for value in r[group].values():boolean(value)
    integer(r['dumpFiles'],0,256,True)
    w=r['failureWitness']
    if w is not None:
        keys(w,'sourceStep nativePhase category win32ErrorAvailable win32Error');require(w['sourceStep'] in SOURCE_STEPS and w['nativePhase'] in NATIVE_PHASES and w['category'] in FAILURE_CATEGORIES);boolean(w['win32ErrorAvailable'])
        if w['win32ErrorAvailable']:require(w['category']=='NativeApi');integer(w['win32Error'],-2**31,2**31-1);require(w['win32Error']!=0)
        else:require(w['win32Error'] is None)
    if r['observationComplete']:
        require(r['stage']=='complete' and r['failure']=='qualification-unreviewed' and w is None and t['attempted'] and t['lifecycleSucceeded'] and t['identityStable'] and t['nativeExitCode'] is not None)
        require(all(r['privacy'].values()) and all(r['cleanup'].values()) and r['dumpFiles']==0)
        require(r['observation']['format']['state']=='observed' and r['observation']['stderr']['state']=='observed' and r['observation']['responseComparison']!='unobserved')
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
        print('tool-help-format-receipt-validated');return 0
    except Exception:print('tool-help-format-receipt-rejected');return 1
if __name__=='__main__':sys.exit(main())
