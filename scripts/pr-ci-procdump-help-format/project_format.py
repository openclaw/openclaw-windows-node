"""Non-content format observations. Delegates semantic verdict unchanged; never normalizes or executes a tool."""
import hashlib,json,sys,unicodedata
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'pr-ci-capture-preparation'))
import project_procdump_help as semantic
IDENTITY=json.loads((Path(__file__).with_name('identity.json')).read_text(encoding='utf-8'))
KINDS=('NUL','ESC','FF','otherCc','Cf','Cs','Co','Cn')
STDERR_CAP=2*1024*1024

def empty_format(state='unobserved',reason='none'):
    return dict(state=state,reason=reason,bom='unobserved',kinds=dict.fromkeys(KINDS,False),occurrences='unobserved')
def initial():
    return dict(schema=1,semantic=semantic.initial(),format=empty_format(),stderr=dict(state='unobserved',presence='unobserved'),responseComparison='unobserved')
def bom_kind(raw):
    if raw.startswith(b'\xef\xbb\xbf'):return 'utf8'
    if raw.startswith(b'\xff\xfe'):return 'utf16le'
    if raw.startswith(b'\xfe\xff'):return 'utf16be'
    return 'none'
def classify_text(text):
    # The only disclosed information is this fixed Boolean set and a saturated occurrence bucket.
    kinds=dict.fromkeys(KINDS,False);count=0
    for c in text:
        category=unicodedata.category(c)
        if not category.startswith('C') or c in '\r\n\t':continue
        key='NUL' if c=='\x00' else 'ESC' if c=='\x1b' else 'FF' if c=='\x0c' else 'otherCc' if category=='Cc' else category
        kinds[key]=True;count=min(2,count+1)
    return kinds,('none' if count==0 else 'one' if count==1 else 'multiple')
def format_bytes(raw,total_bytes=None):
    # Match the published parser's decoder selection and guard order, without changing its result.
    result=empty_format();result['bom']=bom_kind(raw)
    if (len(raw) if total_bytes is None else total_bytes)>semantic.MAX_INPUT:
        result.update(state='limit',reason='input-limit');return result
    try:
        if result['bom']=='utf16le':text=raw[2:].decode('utf-16-le','strict')
        elif result['bom']=='utf16be':text=raw[2:].decode('utf-16-be','strict')
        else:text=raw.decode('utf-8-sig','strict')
    except UnicodeError:
        result.update(state='encoding-failed',reason='encoding');return result
    kinds,bucket=classify_text(text)
    # Published semantics checks control categories before line limits. Do not reorder or waive that guard.
    lines=text.splitlines()
    if text.strip() and not any(kinds.values()) and (len(lines)>semantic.MAX_LINES or any(len(line)>semantic.MAX_LINE for line in lines)):
        result.update(state='limit',reason='line-limit');return result
    result.update(state='observed',reason='none',kinds=kinds,occurrences=bucket);return result

def format_file(path):
    try:
        with Path(path).open('rb') as f:
            total=0;digest=hashlib.sha256();prefix=bytearray()
            while True:
                chunk=f.read(65536)
                if not chunk:break
                total+=len(chunk)
                if total>semantic.MAX_FILE:return empty_format('limit','input-limit'),None,None
                digest.update(chunk)
                if len(prefix)<=semantic.MAX_INPUT:prefix.extend(chunk[:semantic.MAX_INPUT+1-len(prefix)])
        return format_bytes(bytes(prefix),total),digest.hexdigest(),total
    except (OSError,ValueError):return empty_format('read-failed','read-failed'),None,None

def stderr_observation(path):
    try:
        with Path(path).open('rb') as f:
            f.seek(0,2);size=f.tell()
            if size>STDERR_CAP:return dict(state='limit',presence='unobserved')
            f.seek(0);return dict(state='observed',presence='nonempty' if f.read(1) else 'empty')
    except (OSError,ValueError):return dict(state='read-failed',presence='unobserved')

def observe(stdout,stderr,file_version):
    # Call the exact preserved published owner, rather than replacing/relaxing its decoding or semantics.
    strict=semantic.project_file(stdout,file_version)
    profile,digest,size=format_file(stdout)
    comparable=digest is not None and digest==strict['inputSha256'] and size==strict['inputBytes']
    if digest is not None and not comparable:profile=empty_format('read-failed','input-changed')
    comparison='unobserved' if not comparable else 'same-response' if digest==IDENTITY['previousStdoutSha256'] else 'new-response'
    return dict(schema=1,semantic=strict,format=profile,stderr=stderr_observation(stderr),responseComparison=comparison)

def main():
    try:
        if len(sys.argv)!=5:raise ValueError()
        value=observe(sys.argv[1],sys.argv[2],sys.argv[3])
        from validate_format import validate_observation
        validate_observation(value)
        data=json.dumps(value,sort_keys=True,separators=(',',':'),ensure_ascii=True,allow_nan=False).encode()+b'\n'
        if len(data)>8192:raise ValueError()
        with Path(sys.argv[4]).open('xb') as f:f.write(data)
        print('tool-help-format-projected');return 0
    except Exception:print('tool-help-format-projection-failed');return 1
if __name__=='__main__':sys.exit(main())
