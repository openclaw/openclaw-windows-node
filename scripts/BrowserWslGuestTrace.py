#!/usr/bin/python3
"""Disposable-distro component fixture, never a production CLI or cancellation fix.
Only numeric process identities, fixed provenance booleans and synthetic data leave it.
"""
import json
import os
import pathlib
import pwd
import signal
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent


def identity(pid):
    try:
        fields = pathlib.Path(f"/proc/{pid}/stat").read_text().rsplit(") ", 1)[1].split()
        return {"pid": pid, "start": int(fields[19]), "group": int(fields[2]), "state": fields[0]}
    except FileNotFoundError:
        return None


def save(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value))
    temporary.replace(path)


def current_case():
    case = (ROOT / "case").read_text().strip()
    if case not in ("normal", "ipc_eof", "client_cancel", "deadline", "forced_client_exit", "pipe_retirement"):
        raise RuntimeError("invalid fixture case")
    return ROOT / case


def wait_release(folder):
    # A final independent bound on fixture work, not a production deadline change.
    end = time.monotonic() + 60
    while not (folder / "release").exists() and time.monotonic() < end:
        time.sleep(0.025)


def run(role):
    folder = current_case()
    own = identity(os.getpid())
    save(folder / (role + ".json"), own)
    child = None
    if role != "leaf":
        next_role = "worker" if role == "owner" else "leaf"
        child = subprocess.Popen([sys.executable, str(pathlib.Path(__file__).resolve()), next_role])
    if role == "owner":
        expected = ["browser", "extension", "pair", "--json", "--local-gateway"]
        checks = {
            "argvExact": sys.argv[2:] == expected,
            "managedUser": pwd.getpwuid(os.getuid()).pw_name == "openclaw",
            "homeDefault": os.environ.get("HOME") == "/home/openclaw",
            "stateDefault": os.environ.get("OPENCLAW_STATE_DIR") == "/home/openclaw/.openclaw",
            "configDefault": os.environ.get("OPENCLAW_CONFIG_PATH") == "/home/openclaw/.openclaw/openclaw.json",
            "pathExact": os.environ.get("PATH") == "/home/openclaw/.openclaw/tools/node/bin:/usr/local/bin:/usr/bin:/bin",
            "overridesAbsent": all(k not in os.environ for k in ("OPENCLAW_PROFILE", "OPENCLAW_HOME", "NODE_OPTIONS", "NODE_PATH")),
        }
        save(folder / "provenance.json", checks)
        if not all(checks.values()):
            raise RuntimeError("fixture provenance mismatch")
    wait_release(folder)
    if child is not None:
        child.wait(timeout=5)
    save(folder / (role + "-end.json"), {"monotonicNs": time.monotonic_ns()})
    if role == "owner":
        # Deliberately NOT a real credential. The trace handler discards this payload.
        print(json.dumps({"componentFixture": True}), flush=True)


def observe():
    folder = current_case()
    records = []
    for role in ("owner", "worker", "leaf"):
        path = folder / (role + ".json")
        if not path.exists():
            continue
        original = json.loads(path.read_text())
        now = identity(original["pid"])
        matches = now is not None and now["start"] == original["start"]
        records.append({"role": role, **original, "identityPresent": matches,
                        "running": matches and now["state"] not in ("Z", "X"),
                        "observedState": now["state"] if matches else "absent"})
    provenance = folder / "provenance.json"
    print(json.dumps({"records": records, "provenance": json.loads(provenance.read_text()) if provenance.exists() else None}), flush=True)


def cleanup():
    # Kill ONLY positively matched, request-owned PIDs. Never kill a group/distro/gateway.
    folder = current_case()
    for role in ("owner", "worker", "leaf"):
        path = folder / (role + ".json")
        if path.exists():
            original = json.loads(path.read_text())
            try:
                descriptor = os.pidfd_open(original["pid"])
            except ProcessLookupError:
                continue
            try:
                now = identity(original["pid"])
                if now is not None and now["start"] == original["start"] and now["state"] not in ("Z", "X"):
                    try:
                        signal.pidfd_send_signal(descriptor, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
            finally:
                os.close(descriptor)
    observe()


if __name__ == "__main__":
    mode = sys.argv[1]
    if mode in ("owner", "worker", "leaf"):
        run(mode)
    elif mode == "observe":
        observe()
    elif mode == "cleanup":
        cleanup()
    else:
        raise RuntimeError("invalid fixture operation")
