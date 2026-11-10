"""Three diagnostic interpretations per independent stream. No codec adoption or content export."""
import hashlib,json,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'pr-ci-procdump-help-format'))
import project_format as baseline
semantic=baseline.semantic
import phases
IDS=('utf8','utf16le','utf16be')
CODECS={'utf8':'utf-8','utf16le':'utf-16-le','utf16be':'utf-16-be'}
BOMS={'utf8':b'\xef\xbb\xbf','utf16le':b'\xff\xfe','utf16be':b'\xfe\xff'}
KINDS=baseline.KINDS
FORMAT_REFERENCE=dict(kind='reference',runId='42757425327',receiptSha256='2f487e663ecad79e3314af3fd90834eb5e4768d6a04d041de83ff0e7999945e4')

def candidate_initial(identifier,state='unobserved'):
    return dict(id=identifier,state=state,bomPolicy='unobserved',decode='unobserved',roundTrip='unobserved',kinds=dict.fromkeys(KINDS,False),occurrences='unobserved',limits='unobserved',phases=phases.empty(),analysis=dict(scope='derived-analysis-input',encoding='utf8',inputSha256=None,inputBytes=None,status='unobserved',reason='none'),conformant=False)

def summarize(candidates):
    # Only complete semantic conformance counts. Round-tripping is never admission.
    conformant=[c['id'] for c in candidates if c['conformant']]
    return dict(count='none' if not conformant else 'unique' if len(conformant)==1 else 'multiple',id=conformant[0] if len(conformant)==1 else None)

def derived_utf8(text):
    # Never materialize an expanded analysis buffer above the semantic cap.
    output=bytearray()
    for i in range(0,len(text),4096):
        chunk=text[i:i+4096].encode('utf-8','strict')
        if len(output)+len(chunk)>semantic.MAX_INPUT:return None
        output.extend(chunk)
    return bytes(output)

def candidate(raw,identifier,file_version):
    out=candidate_initial(identifier)
    if len(raw)>semantic.MAX_INPUT:out.update(state='input-limit',limits='input-limit');return out
    bom=baseline.bom_kind(raw)
    # Explicit byte-order markers are authoritative only for candidate eligibility.
    # No marker means hypotheses, not an inferred or approved transport.
    if bom!='none' and bom!=identifier:out['state']='bom-conflict';return out
    prefix=BOMS[identifier] if bom==identifier else b''
    payload=raw[len(prefix):]
    out['bomPolicy']='explicit-matching' if prefix else 'no-bom-hypothesis'
    if identifier!='utf8' and len(payload)%2:out['state']='odd-length';return out
    try:text=payload.decode(CODECS[identifier],'strict')
    except UnicodeError:out.update(state='decode-failed',decode='failed');return out
    out['decode']='passed'
    if prefix+text.encode(CODECS[identifier],'strict')!=raw:
        out.update(state='roundtrip-failed',roundTrip='failed');return out
    out['roundTrip']='passed'
    out['kinds'],out['occurrences']=baseline.classify_text(text)
    if any(out['kinds'].values()):out['state']='control-rejected';return out
    lines=text.splitlines()
    if len(lines)>semantic.MAX_LINES or any(len(s)>semantic.MAX_LINE for s in lines):
        out.update(state='line-limit',limits='line-limit');return out
    # UTF-8 is a DERIVED analysis buffer, never the original response identity/BOM.
    derived=derived_utf8(text)
    if derived is None:out.update(state='derived-limit',limits='derived-limit');return out
    out.update(state='inspected',limits='passed')
    out['phases']=phases.inspect(text,file_version)
    result=semantic.project(derived,file_version)
    out['analysis'].update(inputSha256=result['inputSha256'],inputBytes=result['inputBytes'],status=result['status'],reason=result['reason'])
    p=out['phases']
    out['conformant']=(result['status']=='recognized' and p['versionState']=='expected' and p['usageForm']=='valid' and all(v=='recognized' for v in p['clauses'].values()))
    return out

def empty_stream(state='unobserved'):
    candidates=[candidate_initial(identifier,state) for identifier in IDS]
    return dict(state=state,inputSha256=None,inputBytes=None,bom='unobserved',candidates=candidates,conformance=summarize(candidates))

def stream_file(path,file_version):
    try:
        with Path(path).open('rb') as f:
            total=0;digest=hashlib.sha256();prefix=bytearray()
            while True:
                chunk=f.read(65536)
                if not chunk:break
                total+=len(chunk)
                if total>semantic.MAX_FILE:return empty_stream('file-limit')
                digest.update(chunk)
                if len(prefix)<=semantic.MAX_INPUT:prefix.extend(chunk[:semantic.MAX_INPUT+1-len(prefix)])
        raw=bytes(prefix)
        candidates=[candidate(raw,identifier,file_version) for identifier in IDS]
        return dict(state='observed',inputSha256=digest.hexdigest(),inputBytes=total,bom=baseline.bom_kind(raw),candidates=candidates,conformance=summarize(candidates))
    except (OSError,ValueError):return empty_stream('read-failed')

def initial():
    return dict(schema=1,transportApproved=False,behaviorVerified=False,baseline=baseline.initial(),stdout=empty_stream(),stderr=empty_stream())

def observe(stdout,stderr,file_version):
    if file_version!=baseline.IDENTITY['fileVersion']:raise ValueError('file-version')
    out=initial();out['baseline']=baseline.observe(stdout,stderr,file_version)
    out['stdout']=stream_file(stdout,file_version);out['stderr']=stream_file(stderr,file_version)
    strict=out['baseline']['semantic'];stream=out['stdout']
    if stream['inputSha256'] is not None and (stream['inputSha256']!=strict['inputSha256'] or stream['inputBytes']!=strict['inputBytes']):
        out['stdout']=empty_stream('input-changed')
    return out

def main():
    try:
        if len(sys.argv)!=5:raise ValueError()
        result=observe(sys.argv[1],sys.argv[2],sys.argv[3])
        from validate_candidates import validate_observation
        validate_observation(result)
        data=json.dumps(result,sort_keys=True,separators=(',',':'),ensure_ascii=True,allow_nan=False).encode()+b'\n'
        if len(data)>32768:raise ValueError()
        with Path(sys.argv[4]).open('xb') as f:f.write(data)
        print('tool-help-candidates-projected');return 0
    except Exception:print('tool-help-candidates-projection-failed');return 1
if __name__=='__main__':sys.exit(main())
