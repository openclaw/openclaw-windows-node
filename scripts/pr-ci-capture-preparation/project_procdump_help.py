"""Parse documented help FORMS only. Never executes a tool or asserts captured behavior."""
import hashlib,json,re,sys,unicodedata
from pathlib import Path
MAX_INPUT=2*1024*1024
MAX_FILE=64*1024*1024 # Existing owned-command combined output bound; only hashing can reach this bound.
MAX_LINES=8192
MAX_LINE=1024
GRAMMAR='procdump-help-12.02-d46b5bfa'
CLAUSES=('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')
# Literal semantic definitions from MicrosoftDocs/sysinternals d46b5bfa, not observed native help.
DEFINITIONS={
 '-ma':"Write a 'Full' dump file. Includes all memory (Image, Mapped and Private). Includes all metadata (Process, Thread, Module, Handle, Address Space, etc.).",
 '-e':"Write a dump when the process encounters an unhandled exception. Include the 1 to create dump on first chance exceptions. Add -ld to create a dump when a DLL (module) is loaded (filtering applies). Add -ud to create a dump when a DLL (module) is unloaded (filtering applies). Add -ct to create a dump when a thread is created. Add -et to create a dump when a thread exits.",
 '-g':"Run as a native debugger in a managed process (no interop).",
 '-n':"Number of dumps to write before exiting.",
 '-at':"Avoid outage at Timeout. Cancel the trigger's collection at N seconds.",
 '-cancel':"Using this option or setting an event with the name ProcDump-<PID> is the same as typing Ctrl+C to gracefully terminate ProcDump. Graceful termination ensures the process is resumed if a capture is active. The cancellation applies to ALL ProcDump instances monitoring the process.",
}
EULA='Use the -accepteula command line option to automatically accept the Sysinternals license agreement.'
HELP='See a list of example command lines (the examples are listed above):'
HEADINGS={'Dump Types:','Conditions:','License Agreement:','Automated Termination:','Filename:','Examples:','Capture Usage:','Install Usage:','Uninstall Usage:','Usage:'}
REASONS={'none','empty','input-limit','line-limit','encoding','control-character','file-version','missing-version','ambiguous-version','version-mismatch','unsupported-version','missing-clause','unrecognized-clause','ambiguous-clause','read-failed'}
def initial():
    return dict(schema=1,status='not-observed',reason='none',grammarId=None,scope='documented-help-forms',behaviorVerified=False,inputSha256=None,inputBytes=None,encoding='unknown',helpMajor=None,helpMinor=None,versionAgreement=None,capabilities={k:False for k in CLAUSES},clauses={k:'unobserved' for k in CLAUSES})
def normal(s):return ' '.join(s.split())
def balanced_usage(text):
    stack=[];pairs={']':'[','}':'{','>':'<'}
    for c in text:
        if c in '[{<':stack.append(c)
        elif c in ']}>':
            if not stack or stack.pop()!=pairs[c]:return False
    return not stack

# Whole normalized declarations from the pinned12.02 document. This is not a shell parser.
CAPTURE_USAGE="procdump.exe [-mm] [-ma] [-mt] [-mp] [-mc <Mask>] [-md <Callback_DLL>] [-mk] [-n <Count>] [-s <Seconds>] [-c|-cl <CPU_Usage> [-u]] [-m|-ml <Commit_Usage>] [-p|-pl <Counter> <Threshold>] [-h] [-e [1] [-g] [-b] [-ld] [-ud] [-ct] [-et]] [-l] [-t] [-f <Include_Filter>, ...] [-fx <Exclude_Filter>, ...] [-dc <Comment>] [-o] [-r [1..5] [-a]] [-at <Timeout>] [-wer] [-64] { {{[-w] <Process_Name> | <Service_Name> | <PID>} [<Dump_File> | <Dump_Folder>]} | {-x <Dump_Folder> <Image_File> [Argument, ...]} }"
INSTALL_USAGE="procdump.exe -i [Dump_Folder] [-mm] [-ma] [-mt] [-mp] [-mc <Mask>] [-md <Callback_DLL>] [-mk] [-r] [-at <Timeout>] [-k] [-wer]"
UNINSTALL_USAGE="procdump.exe -u"
def version_candidate(line):return bool(re.match(r'^\s*ProcDump\s+v',line,re.I))
def program_candidate(line):return bool(re.match(r'^procdump[^\s]*(?:\s|$)',line,re.I))
def normalized_program(block):
    match=re.fullmatch(r'procdump(?:64)?(?:\.exe)?(?:\s+(.*))?',normal(block),re.I)
    return 'procdump.exe '+(match[1] or '') if match else ''
def capture_declarations(lines,program_lines=None):
    # Count candidates BEFORE checking any -e/form content. Invalid extra declarations cannot disappear.
    stripped=[normal(s) for s in lines];found=[];section='other';pending=False;i=0
    if program_lines is None:program_lines=set()
    def collect(index,first):
        parts=[first];j=index+1
        while j<len(lines) and lines[j][:1].isspace() and stripped[j]:
            line=stripped[j]
            if line in HEADINGS or line==HELP or program_candidate(line) or re.match(r'^(?:Capture |Install |Uninstall )?Usage:',line,re.I):break
            parts.append(line);j+=1
        candidate=normal(' '.join(parts));program=normalized_program(candidate)
        # A continuous indented program block owns its options until a blank/section/program boundary.
        # Do not reinterpret those option lines later as standalone semantic definitions.
        if program and (any(c in candidate for c in '[{<') or re.match(r'^procdump\.exe\s+-[A-Za-z?]',program)):
            program_lines.update(range(index,j))
        return candidate,j
    while i<len(lines) and len(found)<2:
        line=stripped[i]
        if not line or version_candidate(line):i+=1;continue
        header_candidate=re.match(r'^(?:(?:Capture|Install|Uninstall) )?Usage\b',line,re.I)
        heading=re.fullmatch(r'((?:Capture |Install |Uninstall )?Usage):\s*(.*)',line,re.I)
        if header_candidate and not heading:
            if pending:found.append('');pending=False
            found.append('');section='other';i+=1;continue
        if heading:
            if pending:found.append('');pending=False
            kind=heading[1].lower();section='install' if kind=='install usage' else 'uninstall' if kind=='uninstall usage' else 'capture'
            if not heading[2]:pending=section=='capture';i+=1;continue
            candidate,i=collect(i,heading[2]);declaration=normalized_program(candidate)
            if section=='install' and declaration==INSTALL_USAGE:continue
            if section=='uninstall' and declaration==UNINSTALL_USAGE:continue
            found.append(declaration);continue
        if line in HEADINGS or line==HELP:
            if pending:found.append('');pending=False
            section='examples' if line in {'Examples:',HELP} else 'other';i+=1;continue
        if program_candidate(line):
            candidate,next_index=collect(i,line)
            # Classify the whole supported logical declaration before normalizing its program name.
            # A physical line break or malformed alias must not hide declaration metasyntax.
            syntax=any(c in candidate for c in '[{<')
            if section=='examples' and not syntax:i=next_index;continue
            if section=='other' and not syntax and not re.match(r'^procdump[^\s]*\s+[\[-]',candidate,re.I):i=next_index;continue
            declaration=normalized_program(candidate);i=next_index;pending=False
            if section=='install' and declaration==INSTALL_USAGE:continue
            if section=='uninstall' and declaration==UNINSTALL_USAGE:continue
            found.append(declaration);continue
        if pending:found.append('');pending=False
        i+=1
    if pending and len(found)<2:found.append('')
    return found

def block_text(lines):return normal(' '.join(re.sub(r'^\s*-\s+','',s) for s in lines))
def project(raw,file_version):
    result=initial()
    if type(raw) is not bytes:return refuse(result,'unrecognized','read-failed')
    result.update(inputSha256=hashlib.sha256(raw).hexdigest(),inputBytes=len(raw))
    if len(raw)>MAX_INPUT:return refuse(result,'unrecognized','input-limit')
    if not raw:return refuse(result,'empty','empty')
    try:
        if raw.startswith(b'\xff\xfe'):text=raw[2:].decode('utf-16-le','strict');result['encoding']='utf16le'
        elif raw.startswith(b'\xfe\xff'):text=raw[2:].decode('utf-16-be','strict');result['encoding']='utf16be'
        else:text=raw.decode('utf-8-sig','strict');result['encoding']='utf8'
    except UnicodeError:return refuse(result,'unrecognized','encoding')
    if not text.strip():return refuse(result,'empty','empty')
    if any(unicodedata.category(c).startswith('C') and c not in '\r\n\t' for c in text):return refuse(result,'unrecognized','control-character')
    lines=text.splitlines()
    if len(lines)>MAX_LINES or any(len(s)>MAX_LINE for s in lines):return refuse(result,'unrecognized','line-limit')
    if type(file_version) is not str or not re.fullmatch(r'[0-9]{1,5}(?:\.[0-9]{1,5}){3}',file_version):return refuse(result,'unrecognized','file-version')
    file_parts=tuple(map(int,file_version.split('.')))
    if any(x>65535 for x in file_parts):return refuse(result,'unrecognized','file-version')
    declarations=[line for line in lines if version_candidate(line)]
    if not declarations:return refuse(result,'unrecognized','missing-version')
    if len(declarations)!=1 or len(re.findall(r'\bProcDump\s+v',declarations[0],re.I))!=1:return refuse(result,'unrecognized','ambiguous-version')
    match=re.fullmatch(r'ProcDump v([0-9]{1,5}(?:\.[0-9]{1,5}){1,3})(?:\s+.*)?',normal(declarations[0]))
    if not match:return refuse(result,'unrecognized','missing-version')
    version=tuple(map(int,match[1].split('.')))
    if any(x>65535 for x in version):return refuse(result,'unsupported','unsupported-version')
    result.update(helpMajor=version[0],helpMinor=version[1],versionAgreement=(version==file_parts[:len(version)]))
    if not result['versionAgreement']:return refuse(result,'unrecognized','version-mismatch')
    if len(version)!=2 or version!=(12,2):return refuse(result,'unsupported','unsupported-version')
    result['grammarId']=GRAMMAR
    stripped=[normal(s) for s in lines]
    # Distinct install/uninstall/example contexts are excluded only by their explicit context/contracts.
    program_lines=set();usages=capture_declarations(lines,program_lines)
    usage=usages[0] if len(usages)==1 else ''
    form_patterns={
        'fullMemory':r'\[-ma\]',
        'unhandledException':r'\[-e \[1\] \[-g\]',
        'nativeDebugger':r'\[-e \[1\] \[-g\]',
        'dumpCount':r'\[-n <Count>\]',
        'captureTimeout':r'\[-at <Timeout>\]',
    }
    form_counts={k:len(re.findall(pattern,usage)) for k,pattern in form_patterns.items()}
    forms={k:count==1 and balanced_usage(usage) and usage==CAPTURE_USAGE for k,count in form_counts.items()}
    rows={flag:[] for flag in DEFINITIONS}
    for i,line in enumerate(stripped):
        if i in program_lines:continue
        match=re.match(r'^(-[a-z][a-z0-9]*)(?:\s+(.*))?$',line)
        if not match or match[1] not in rows:continue
        if len(rows[match[1]])>=2:continue
        parts=[match[2] or ''];j=i+1
        while j<len(lines):
            nxt=stripped[j]
            if j in program_lines:break
            if nxt in HEADINGS or nxt==HELP or re.match(r'^-[A-Za-z?]',nxt) or re.match(r'^(?:C:\\>)?procdump\b',nxt,re.I):break
            if nxt:parts.append(nxt)
            j+=1
        rows[match[1]].append(block_text(parts))
    mapping={'fullMemory':'-ma','unhandledException':'-e','nativeDebugger':'-g','dumpCount':'-n','captureTimeout':'-at'}
    for key,flag in mapping.items():
        values=rows[flag]
        if len(values)>1 or len(usages)>1 or form_counts[key]>1:state='ambiguous'
        elif not values:state='missing'
        elif forms[key] and values[0]==DEFINITIONS[flag]:state='recognized'
        else:state='unrecognized'
        result['clauses'][key]=state
    values=rows['-cancel']
    expected='<Target Process PID> '+DEFINITIONS['-cancel']
    result['clauses']['cancelGraceful']='ambiguous' if len(values)>1 else 'missing' if not values else 'recognized' if values[0]==expected else 'unrecognized'
    # The pinned page supplies these exact license/help clauses. Unknown native synonyms stay unrecognized.
    eula_starts=[i for i,s in enumerate(stripped) if s=='License Agreement:']
    eulas=[]
    for i in eula_starts[:2]:
        parts=[];j=i+1
        while j<len(lines) and stripped[j] not in HEADINGS and stripped[j]!=HELP:
            if stripped[j]:parts.append(stripped[j])
            j+=1
        eulas.append(normal(' '.join(parts)))
    result['clauses']['acceptEula']='ambiguous' if len(eulas)>1 else 'missing' if not eulas else 'recognized' if eulas[0]==EULA else 'unrecognized'
    helps=[i for i,s in enumerate(stripped) if s==HELP];help_states=[]
    for i in helps[:2]:
        following=next((s for s in stripped[i+1:] if s),'')
        help_states.append(bool(re.fullmatch(r'(?:C:\\>)?procdump -\? -e',following)))
    if len(helps)>1:state='ambiguous'
    elif helps:state='recognized' if help_states[0] else 'unrecognized'
    elif any('-?' in s for s in stripped):state='unrecognized'
    else:state='missing'
    result['clauses']['helpNoTarget']=state
    result['capabilities']={k:result['clauses'][k]=='recognized' for k in CLAUSES}
    states=set(result['clauses'].values())
    if states=={'recognized'}:result.update(status='recognized',reason='none')
    elif 'ambiguous' in states:result.update(status='unrecognized',reason='ambiguous-clause')
    elif 'unrecognized' in states:result.update(status='unrecognized',reason='unrecognized-clause')
    else:result.update(status='unrecognized',reason='missing-clause')
    return result

def refuse(result,status,reason):
    result.update(status=status,reason=reason)
    if status=='empty':result['clauses']={k:'missing' for k in CLAUSES}
    return result

def project_file(path,file_version):
    # Retain a full digest under the EXISTING 64MiB output limit, but no more than 2MiB+1 semantic input.
    try:
        with Path(path).open('rb') as stream:
            total=0;hasher=hashlib.sha256();prefix=bytearray()
            while True:
                chunk=stream.read(65536)
                if not chunk:break
                total+=len(chunk)
                if total>MAX_FILE:return refuse(initial(),'unrecognized','input-limit')
                hasher.update(chunk)
                if len(prefix)<=MAX_INPUT:prefix.extend(chunk[:MAX_INPUT+1-len(prefix)])
        result=project(bytes(prefix),file_version);result.update(inputBytes=total,inputSha256=hasher.hexdigest());return result
    except (OSError,ValueError):return refuse(initial(),'unrecognized','read-failed')

def main():
    try:
        if len(sys.argv)!=4:raise ValueError()
        result=project_file(sys.argv[1],sys.argv[2])
        # Reuse the exact public shape validator before creating the private projection.
        from validate_preparation import validate_help_projection
        validate_help_projection(result,result['inputSha256'],sys.argv[2])
        data=json.dumps(result,sort_keys=True,separators=(',',':'),ensure_ascii=True).encode()+b'\n'
        if len(data)>8192:raise ValueError()
        with Path(sys.argv[3]).open('xb') as output:output.write(data)
        print('procdump-help-projected');return 0
    except Exception:print('procdump-help-projection-failed');return 1
if __name__=='__main__':sys.exit(main())
