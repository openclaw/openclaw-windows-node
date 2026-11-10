import concurrent.futures, json, subprocess
from pathlib import Path
root = Path(__file__).parent
repo = 'repos/openclaw/openclaw-windows-node'
requests = [(f'closure-pr-{n}', f'{repo}/pulls/{n}') for n in [1549,1824,1914,1782,1726]]
requests += [('closure-release', f'{repo}/releases/tags/v2026.11.1')]
requests += [(f'fresh-issue-{n}',f'{repo}/issues/{n}') for n in [1330,1843,1975,1701,1754,1821,1887,1764]]
def fetch(item):
 name, endpoint = item
 r = subprocess.run(['gh','api',endpoint],capture_output=True,text=True)
 if r.returncode: return {'name':name,'error':r.stderr[:1000]}
 value=json.loads(r.stdout)
 (root/f'{name}.json').write_text(json.dumps(value,indent=2))
 return {'name':name, **{k:value.get(k) for k in ['number','title','state','merged_at','merge_commit_sha','updated_at','comments','tag_name','published_at'] if k in value}}
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
 for result in pool.map(fetch,requests): print(json.dumps(result),flush=True)
