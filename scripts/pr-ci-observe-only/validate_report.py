"""Strict allowlist for the ONLY uploaded ObserveOnly JSON. Never prints input or exceptions."""
import json
import os
import re
import sys
from pathlib import Path

COMMIT = "e56258fd0a835969b417f8d0236baf86760e3139"
TREE = "22ba737e3122d589787701a16ee2e2bbd234bdb0"
CAP = 32768
BLAME_IDENTITY_STATES = {"unobserved","absent","expected-single","expected-equivalent-copies","conflicting-identity","query-failed"}
PRESENCE = {"unobserved", "absent", "present"}
RECEIPT_STATES = {"not-inspected", "missing", "unreadable", "oversize", "invalid-json", "invalid-shape", "validated"}
OBSERVER_FAILURES = {"none","initialization","host-query","source-query","receipt-write","collector-set","ambiguous-host","host-identity","event-order","ambiguous-source","source-mismatch","probe-failed","host-exited-before-case"}
ROLES = {"console", "build-task", "ui-assembly", "testhost-assembly", "object-model", "observer", "collector-corelib", "collector-coreclr", "host-image", "blame"}
BINDINGS = {"evaluated", "invocation-bound", "loaded", "framework-query"}
SOURCE_STEPS = {"None","GitResolve","ProductHead","PipelineHead","ProductClean","PipelineClean","ProductTree","DotnetResolve","DotnetIdentity","DotnetVersion"}
NATIVE_PHASES = {"None","Preparation","JobCreate","JobConfigure","StandardInput","StandardOutput","StandardError","AttributeList","AttributeInitialize","AttributeUpdate","Environment","ProcessCreate","JobAssign","Resume","Wait","OutputCheck","ExitRead","JobDrain","JobTerminate","Cleanup","Completed"}
FAILURE_CATEGORIES = {"NativeApi","CommandResolution","ParameterBinding","MethodBinding","Invocation","PowerShell","Argument","Io","Access","InvalidOperation","Interop","Other"}
LIMITS = {"capture-not-reviewed", "host-coreclr-not-remotely-observed", "winapp-unavailable-or-untrusted", "cdb-preparation-required", "procdump-preparation-required"}

class Invalid(ValueError):
    pass

def require(ok):
    if not ok:
        raise Invalid("invalid-observe-receipt")

def keys(obj, names):
    require(type(obj) is dict and set(obj) == set(names.split()))

def boolean(value):
    require(type(value) is bool)

def integer(value, low=0, high=1024, nullable=False):
    if nullable and value is None:
        return
    require(type(value) is int and low <= value <= high)

def text(value, pattern, nullable=False):
    if nullable and value is None:
        return
    require(type(value) is str and len(value) <= 128 and re.fullmatch(pattern, value, flags=re.ASCII) is not None)

def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result

def decode(data):
    require(type(data) is bytes and 0 < len(data) <= CAP)
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=no_duplicates,
                      parse_constant=lambda _: (_ for _ in ()).throw(Invalid("invalid-number")))

def validate(r):
    keys(r, "schema collector failureWitness phase stage captureEnabled captureAdmission productCommit productTree pipelineCommit runId runAttempt sdkVersion producerContract targetFrameworkMatched architecture binaries tools probe privacy cleanup dumpFiles observePassed failure setupExitCode limits")
    integer(r["schema"], 4, 4)
    w=r["failureWitness"]
    if w is not None:
        keys(w,"sourceStep nativePhase category win32ErrorAvailable win32Error")
        require(w["sourceStep"] in SOURCE_STEPS and w["nativePhase"] in NATIVE_PHASES and w["category"] in FAILURE_CATEGORIES)
        boolean(w["win32ErrorAvailable"])
        if w["win32ErrorAvailable"]:
            require(w["category"] == "NativeApi")
            integer(w["win32Error"], -(2**31), 2**31-1)
            require(w["win32Error"] != 0)
        else:
            require(w["win32Error"] is None)
        if w["category"] == "NativeApi":
            require(w["nativePhase"] not in {"None","Preparation","Environment","Completed"})
    require(r["stage"] in {"initialization","winapp","source-binding","ui-build","sdk-evaluation","runtime-prerequisite","analysis-tool-files","collector-build","binding-selftest","probe","receipt","complete"})
    require(r["phase"] == "ObserveOnly" and r["captureEnabled"] is False and r["captureAdmission"] == "not-reviewed")
    require(r["productCommit"] == COMMIT and r["productTree"] == TREE)
    text(r["pipelineCommit"], r"[a-f0-9]{40}")
    text(r["runId"], r"[1-9][0-9]{0,19}")
    require(r["runAttempt"] == "1")
    text(r["sdkVersion"], r"10\.0\.[0-9]{3}", nullable=True)
    require(r["producerContract"] in (None, "18.6", "18.7"))
    boolean(r["targetFrameworkMatched"])
    require(r["architecture"] == "x64")
    boolean(r["observePassed"])
    require(r["failure"] in {"none", "initialization", "preflight-incomplete"})
    integer(r["setupExitCode"], -(2**31), 2**31-1, True)
    integer(r["dumpFiles"], 0, 256, True)
    require(type(r["limits"]) is list and len(r["limits"]) <= 5 and len(set(r["limits"])) == len(r["limits"]))
    require(set(r["limits"]) <= LIMITS and {"capture-not-reviewed", "host-coreclr-not-remotely-observed"} <= set(r["limits"]))
    require(type(r["binaries"]) is list and len(r["binaries"]) <= 10)
    roles = set()
    for b in r["binaries"]:
        keys(b, "role sha256 version binding")
        require(b["role"] in ROLES and b["role"] not in roles)
        roles.add(b["role"])
        text(b["sha256"], r"[a-f0-9]{64}")
        text(b["version"], r"[0-9]{1,10}(\.[0-9]{1,10}){3}")
        require(b["binding"] in BINDINGS)
        expected = "evaluated" if b["role"] in {"build-task", "ui-assembly", "testhost-assembly"} else "framework-query" if b["role"] == "host-image" else "loaded"
        require(b["binding"] in {"evaluated", "invocation-bound"} if b["role"] == "console" else b["binding"] == expected)
    require(type(r["tools"]) is list and len(r["tools"]) <= 3)
    tool_roles = set()
    for t in r["tools"]:
        keys(t, "role status sha256 version microsoftSignatureValid architectureMatched cliSchemaSha256 existingTesthostBoundary")
        require(t["role"] in {"winapp", "cdb", "procdump"} and t["role"] not in tool_roles)
        tool_roles.add(t["role"])
        require(t["status"] in {"missing", "ambiguous", "untrusted", "verified-file"})
        text(t["sha256"], r"[a-f0-9]{64}", True)
        text(t["version"], r"[0-9]{1,10}(\.[0-9]{1,10}){3}", True)
        text(t["cliSchemaSha256"], r"[a-f0-9]{64}", True)
        boolean(t["microsoftSignatureValid"]); boolean(t["architectureMatched"])
        require(t["existingTesthostBoundary"] in {"unverified", "unsupported-by-reviewed-contract"})
        if t["status"] == "verified-file":
            require(t["sha256"] is not None and t["version"] is not None and t["microsoftSignatureValid"] and t["architectureMatched"])
        if t["role"] != "winapp":
            require(t["cliSchemaSha256"] is None and t["existingTesthostBoundary"] == "unverified")
    p = r["probe"]
    keys(p, "attempts executed passed skipped nativeExitCode hosts cases sourceHashMatched handleRetained jobMatched")
    integer(p["attempts"], 0, 1)
    for field in ("executed", "passed", "skipped", "hosts", "cases"):
        integer(p[field])
    integer(p["nativeExitCode"], -(2**31), 2**31-1, True)
    for field in ("sourceHashMatched", "handleRetained", "jobMatched"):
        boolean(p[field])
    keys(r["privacy"], "privateAclVerified outsideArtifactRoot explicitUploadOnly")
    keys(r["cleanup"], "ownedProcessesJoined privateRootDeleted")
    for group in ("privacy", "cleanup"):
        for value in r[group].values():
            boolean(value)
    c = r["collector"]
    keys(c, "claim ready invalid final receiptReadStatus failureReason writersJoined blameIdentityState")
    for key in ("claim", "ready", "invalid", "final"):
        require(type(c[key]) is str and c[key] in PRESENCE)
    boolean(c["writersJoined"])
    require(type(c["blameIdentityState"]) is str and c["blameIdentityState"] in BLAME_IDENTITY_STATES)
    require(type(c["receiptReadStatus"]) is str and c["receiptReadStatus"] in RECEIPT_STATES)
    if c["writersJoined"]:
        require(p["attempts"] == 1)
    else:
        require(c["receiptReadStatus"] == "not-inspected")
    if p["attempts"] == 0:
        require(not c["writersJoined"] and all(c[k] == "unobserved" for k in ("claim","ready","invalid","final")))
    if c["receiptReadStatus"] == "validated":
        require(c["writersJoined"] and c["final"] == "present" and type(c["failureReason"]) is str and c["failureReason"] in OBSERVER_FAILURES)
    else:
        require(c["failureReason"] is None and c["blameIdentityState"] == "unobserved")
    if c["receiptReadStatus"] == "missing":
        require(c["final"] == "absent")
    if c["receiptReadStatus"] in {"oversize","invalid-json","invalid-shape"}:
        require(c["final"] == "present")
    if c["receiptReadStatus"] == "unreadable":
        require(c["final"] in {"unobserved","present"})
    if r["observePassed"]:
        require(c["blameIdentityState"] in {"expected-single","expected-equivalent-copies"})
        require(c == dict(claim="present",ready="present",invalid="absent",final="present",receiptReadStatus="validated",failureReason="none",writersJoined=True,blameIdentityState=c["blameIdentityState"]))
        require(w is None)
        require(r["stage"] == "complete" and r["failure"] == "none" and r["dumpFiles"] == 0 and r["setupExitCode"] in (None, 0))
        require(r["targetFrameworkMatched"] and r["sdkVersion"] is not None and r["producerContract"] in {"18.6","18.7"})
        require(roles == ROLES and tool_roles == {"winapp", "cdb", "procdump"})
        require(next(b for b in r["binaries"] if b["role"] == "console")["binding"] == "invocation-bound")
        require(all(r["privacy"].values()) and all(r["cleanup"].values()))
        require(p == dict(attempts=1, executed=1, passed=1, skipped=0, nativeExitCode=0, hosts=1, cases=1,
                          sourceHashMatched=True, handleRetained=True, jobMatched=True))
    data = json.dumps(r, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False).encode()
    require(len(data) + 1 <= CAP)
    return data + b"\n"

def main():
    target = None
    try:
        require(len(sys.argv) == 3)
        check_only = sys.argv[1] == "--check"
        source = Path(sys.argv[2] if check_only else sys.argv[1])
        target = None if check_only else Path(sys.argv[2])
        if target is not None:
            require(source.resolve() != target.resolve() and not target.exists())
        # Read at most CAP+1, not an attacker-controlled unbounded file.
        with source.open("rb") as f:
            receipt = decode(f.read(CAP+1))
        for key, variable in [("pipelineCommit", "GITHUB_SHA"), ("runId", "GITHUB_RUN_ID"), ("runAttempt", "GITHUB_RUN_ATTEMPT")]:
            require(variable in os.environ and receipt.get(key) == os.environ[variable])
        output = validate(receipt)
        if target is not None:
            with target.open("xb") as f:
                f.write(output)
        print("observe-receipt-validated")
        return 0
    except Exception:
        print("observe-receipt-rejected")
        return 1

if __name__ == "__main__":
    sys.exit(main())
