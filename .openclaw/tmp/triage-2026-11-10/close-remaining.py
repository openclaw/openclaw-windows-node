import json, os, subprocess, time
from pathlib import Path
root=Path(__file__).parent
path=root/'remaining-closure-receipts.json'
if path.exists():raise SystemExit('Receipts exist; do not replay')
manifest={a['number']:a for a in json.loads((root/'approved-actions.json').read_text())}
journal=[]
for n in [1764,1821,1843,1887,1975]:
 f=root/f'independent-preclose-{n}.json';proof=json.loads(f.read_text());a=manifest[n]
 assert time.time()-f.stat().st_mtime<300, 'Fresh observation expired'
 assert proof['badge']['label']=='Open' and not proof['commentsTruncated']
 assert proof['comments'][-1]['author']=='roboclaw-bot' and proof['comments'][-1]['body']==a['body']
 e={'number':n,'status':'requested','stateReason':a['stateReason']};journal.append(e);path.write_text(json.dumps(journal,indent=2))
 r=subprocess.run(['gh','api','--method','PATCH',f'repos/openclaw/openclaw-windows-node/issues/{n}','--input','-'],input=json.dumps({'state':'closed','state_reason':a['stateReason']}),capture_output=True,text=True,env=dict(os.environ,OCTOPOOL_FRESH='1'),timeout=120)
 if r.returncode:
  e.update(status='uncertain-or-failed',error=r.stderr[:1500]);path.write_text(json.dumps(journal,indent=2));raise SystemExit(r.returncode)
 result=json.loads(r.stdout);e.update(status='returned',state=result['state'],stateReason=result.get('state_reason'),closedAt=result.get('closed_at'));path.write_text(json.dumps(journal,indent=2));print(json.dumps(e),flush=True);time.sleep(2)
