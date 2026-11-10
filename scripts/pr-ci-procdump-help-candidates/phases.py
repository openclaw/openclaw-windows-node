"""Phase-only inspection. Never supplies a banner or grants aggregate conformance.
Clause block is an exact extraction of the unchanged pinned semantic owner.
Keeping that owner unchanged preserves its early-return baseline contract.
Tests compare this extraction byte-for-byte and its output at the real owner boundary.
"""
import re
from project_procdump_help import (initial,normal,version_candidate,capture_declarations,
    balanced_usage,block_text,DEFINITIONS,CAPTURE_USAGE,HEADINGS,HELP,EULA,CLAUSES)

def clauses(lines):
    result=initial()
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
    return result['clauses']

def empty():
    return dict(versionCount='unobserved',versionState='unobserved',major=None,minor=None,build=None,revision=None,versionAgreement=None,usageCount='unobserved',usageForm='unobserved',clauses=dict.fromkeys(CLAUSES,'unobserved'))

def bucket(count):return 'zero' if count==0 else 'one' if count==1 else 'multiple'

def inspect(text,file_version):
    lines=text.splitlines();out=empty()
    declarations=[line for line in lines if version_candidate(line)]
    count=len(declarations)
    if count==1 and len(re.findall(r'\bProcDump\s+v',declarations[0],re.I))!=1:count=2
    out['versionCount']=bucket(count)
    out['versionState']='absent' if count==0 else 'ambiguous' if count>1 else 'malformed'
    if count==1:
        match=re.fullmatch(r'ProcDump v([0-9]{1,5}(?:\.[0-9]{1,5}){1,3})(?:\s+.*)?',normal(declarations[0]))
        if match:
            parts=tuple(map(int,match[1].split('.')))
            if any(x>65535 for x in parts):out['versionState']='unsupported'
            else:
                for key,value in zip(('major','minor','build','revision'),parts):out[key]=value
                file_parts=tuple(map(int,file_version.split('.')))
                out['versionAgreement']=parts==file_parts[:len(parts)]
                out['versionState']='mismatch' if not out['versionAgreement'] else 'expected' if parts==(12,2) else 'unsupported'
    usages=capture_declarations(lines)
    out['usageCount']=bucket(len(usages))
    out['usageForm']='absent' if not usages else 'ambiguous' if len(usages)>1 else 'valid' if usages[0]==CAPTURE_USAGE and balanced_usage(usages[0]) else 'invalid'
    out['clauses']=clauses(lines)
    return out
