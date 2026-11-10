"""Apply only the reviewed issue-action manifest, with per-effect receipts and no retries."""
import json, subprocess, sys, time
from datetime import datetime, timezone
from pathlib import Path
root=Path(__file__).parent
manifest=json.loads((root/'approved-actions.json').read_text())
allowed={1701,1330,1421,1442,1564,1706,1754,1764,1821,1843,1846,1882,1887,1902,1975}
assert len(manifest)==len(allowed) and {a['number'] for a in manifest}==allowed
assert sum(a['action']=='close' for a in manifest)==7
assert all(a['action'] in ['comment','close'] and 'roboclaw-issue-cleanup:2026-11-10:' in a['body'] for a in manifest)
if sys.argv[1:]!=['--apply']:
 print(json.dumps({'validated':True,'actions':len(manifest),'closures':7}));sys.exit(0)
journal_path=root/'action-journal.json'
if journal_path.exists(): raise SystemExit('Existing journal: reconcile it before any continuation; automatic replay refused.')
journal=[]
def save():
 temp=journal_path.with_suffix('.pending.json');temp.write_text(json.dumps(journal,ensure_ascii=False,indent=2));temp.replace(journal_path)
def api(path,method='GET',payload=None):
 args=['gh','api','--method',method,path]
 if payload is not None: args+=['--input','-']
 r=subprocess.run(args,input=json.dumps(payload) if payload is not None else None,capture_output=True,text=True,timeout=120)
 if r.returncode: raise RuntimeError(r.stderr[:1500])
 return json.loads(r.stdout) if r.stdout.strip() else None
for action in manifest:
 n=action['number']; endpoint=f'repos/openclaw/openclaw-windows-node/issues/{n}'
 entry={'number':n,'action':action['action'],'startedAt':datetime.now(timezone.utc).isoformat(),'status':'checking'};journal.append(entry);save()
 try:
  requirement=action.get('requiresCommentOn')
  if requirement and not any(e['number']==requirement and e['status']=='verified' for e in journal):
   entry.update(status='skipped',reason='Canonical issue comment not verified');save();continue
  before=api(endpoint)
  entry['before']={k:before.get(k) for k in ['state','updated_at','comments','state_reason']}
  if before['state']!='open' or before['updated_at']!=action['expectedUpdatedAt'] or before['comments']!=action['expectedComments']:
   entry.update(status='skipped',reason='Live issue changed since reviewed snapshot');save();print(json.dumps(entry),flush=True);continue
  entry.update(status='comment-requested',commentBody=action['body']);save()
  comment=api(endpoint+'/comments','POST',{'body':action['body']})
  entry.update(commentId=comment['id'],commentUrl=comment['html_url'],commentAuthor=comment['user']['login'],status='comment-returned');save()
  if comment['user']['login']!='roboclaw-bot':raise RuntimeError('Unexpected comment author; stop before additional effects')
  readback=api(f"repos/openclaw/openclaw-windows-node/issues/comments/{comment['id']}")
  if readback['body']!=action['body']:raise RuntimeError('Comment readback mismatch')
  entry['commentVerified']=True;save()
  if action['action']=='close':
   current=api(endpoint)
   if current['state']!='open' or current['comments']!=before['comments']+1 or current['body']!=before['body']:
    entry.update(status='comment-only',reason='Issue changed before close; needs reconciliation');save();print(json.dumps({'number':n,'status':entry['status'],'url':entry['commentUrl']}),flush=True);continue
   entry['status']='close-requested';save()
   result=api(endpoint,'PATCH',{'state':'closed','state_reason':action['stateReason']})
   entry.update(status='close-returned',returnedState=result['state']);save()
   after=api(endpoint)
   if after['state']!='closed' or after['state_reason']!=action['stateReason']:raise RuntimeError('Close readback mismatch')
   entry['after']={k:after.get(k) for k in ['state','state_reason','updated_at','comments','closed_at']}
  else:
   after=api(endpoint);entry['after']={k:after.get(k) for k in ['state','updated_at','comments']}
  entry['status']='verified';save();print(json.dumps({'number':n,'action':action['action'],'status':'verified','url':entry['commentUrl'],'state':entry['after']['state']}),flush=True)
  time.sleep(2)
 except Exception as error:
  entry.update(status='uncertain-or-failed',error=str(error));save();print(json.dumps(entry),flush=True);raise
print(json.dumps({'verified':sum(e['status']=='verified' for e in journal),'closed':sum(e['action']=='close' and e['status']=='verified' for e in journal),'skipped':sum(e['status']=='skipped' for e in journal)}),flush=True)
