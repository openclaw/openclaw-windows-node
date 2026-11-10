"""Finish verified comment-only actions using the wrapper's documented live-read mode."""
import json, os, subprocess, time
from pathlib import Path
root=Path(__file__).parent
original=json.loads((root/'action-journal.json').read_text())
manifest={a['number']:a for a in json.loads((root/'approved-actions.json').read_text())}
selected=[e for e in original if e['action']=='close' and e['status']=='comment-only']
assert {e['number'] for e in selected}=={1330,1754,1764,1821,1843,1887,1975}
path=root/'closure-journal.json'
if path.exists():raise SystemExit('Existing closure receipt: reconcile rather than replay')
journal=[]
env=dict(os.environ,OCTOPOOL_FRESH='1')
def save():
 temp=path.with_suffix('.pending.json');temp.write_text(json.dumps(journal,indent=2));temp.replace(path)
def api(endpoint,method='GET',payload=None):
 args=['gh','api','--method',method,endpoint]
 if payload is not None:args+=['--input','-']
 r=subprocess.run(args,input=json.dumps(payload) if payload is not None else None,capture_output=True,text=True,env=env,timeout=120)
 if r.returncode:raise RuntimeError(r.stderr[:1500])
 return json.loads(r.stdout)
for prior in selected:
 n=prior['number'];a=manifest[n];endpoint=f'repos/openclaw/openclaw-windows-node/issues/{n}';e={'number':n,'status':'checking','commentUrl':prior['commentUrl'],'commentId':prior['commentId']};journal.append(e);save()
 try:
  live=api(endpoint);comment=api(f"repos/openclaw/openclaw-windows-node/issues/comments/{prior['commentId']}")
  e['before']={k:live.get(k) for k in ['state','comments','updated_at','state_reason']};save()
  assert comment['user']['login']=='roboclaw-bot' and comment['body']==a['body'], 'Explanatory comment differs'
  if live['state']=='closed':e.update(status='already-closed',after=e['before']);save();continue
  expected=json.loads((root/f'fresh-issue-{n}.json').read_text())
  if live['comments']!=expected['comments']+1 or live['body']!=expected['body']:
   e.update(status='held',reason='New discussion or issue body requires review');save();print(json.dumps(e),flush=True);continue
  if n==1975:
   canonical=api('repos/openclaw/openclaw-windows-node/issues/1701')
   assert canonical['state']=='open','Canonical issue is no longer open'
   keep=next(x for x in original if x['number']==1701)
   cc=api(f"repos/openclaw/openclaw-windows-node/issues/comments/{keep['commentId']}")
   assert cc['body']==manifest[1701]['body'],'Canonical evidence link changed'
  e['status']='close-requested';save()
  result=api(endpoint,'PATCH',{'state':'closed','state_reason':a['stateReason']});e['returnedState']=result['state'];e['status']='close-returned';save()
  live=api(endpoint);e['after']={k:live.get(k) for k in ['state','state_reason','comments','updated_at','closed_at']}
  assert live['state']=='closed' and live['state_reason']==a['stateReason'],'Live close readback differs'
  e['status']='verified';save();print(json.dumps(e),flush=True);time.sleep(2)
 except Exception as ex:
  e.update(status='uncertain-or-failed',error=str(ex));save();raise
print(json.dumps({'verifiedClosures':sum(e['status']=='verified' for e in journal),'other':[e for e in journal if e['status']!='verified']}),flush=True)
