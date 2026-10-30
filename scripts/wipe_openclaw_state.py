#!/usr/bin/env python3
"""Wipe all OpenClaw Companion / Windows node / Gateway state and config from this machine.

Covers the same artifacts cleaned by the uninstall engine's TrayArtifactCleanup
plus everything it intentionally leaves behind (see
src/OpenClaw.SetupEngine/TrayArtifactCleanup.cs in the openclaw-windows-node repo).
Anything whose name contains "openclaw" (case-insensitive) in these places:

  - Files/dirs directly under %APPDATA%, %LOCALAPPDATA%, %LOCALAPPDATA%\\Programs,
    %LOCALAPPDATA%\\Packages, %TEMP%, %ProgramData%, %ProgramFiles[(x86)]%, e.g.
      * OpenClawTray[-Dev|-Test], OpenClawTests (settings, gateways.json,
        device-key identities, exec approvals, Local AI, logs, run.marker)
      * OpenClaw, OpenClawDev (gateway state locks, dev-tool pwsh copy)
      * Packages\\OpenClawFoundation.* (data left behind by uninstalled MSIX packages)
      * %TEMP%\\openclaw-*, OpenClaw.Tray.*, OpenClaw*Tests (test/proof leftovers)
  - Start Menu entries (per-user and all-users), e.g. the empty OpenClawGateway folder
  - *.lnk shortcuts on the user/all-users Desktop and in the Startup folders
  - %USERPROFILE%\\.openclaw and %TEMP%\\oc-uia (OpenClawAgentUia task scripts)
  - Registry (HKCU):
      * Software\\<*openclaw*> (e.g. leftover OpenClawTests test-run keys)
      * Software\\Classes\\<*openclaw*> (openclaw:// / openclaw-dev:// link handlers)
      * SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run values naming or launching OpenClaw
      * Toast registrations for the OpenClaw.Companion[.Dev] AUMIDs
        (Software\\Classes\\AppUserModelId and ...\\Notifications\\Settings)
  - Scheduled tasks (any path/name), e.g. "OpenClaw Companion[ (Dev)]",
    "OpenClaw Gateway OpenClawFoundation.OpenClawGateway*", OpenClawAgentUia
  - Running processes named OpenClaw* / winnode*, or running from an
    OpenClaw-owned directory (the scan roots above or the MSIX package tree);
    killed through a handle opened once, after verifying image path and
    creation time on that same handle, so a reused PID is never killed, and
    one process at a time (no tree kill)
  - Installed apps listed in Settings > Apps > Installed apps ("Add or remove
    programs") whose name contains "OpenClaw":
      * MSIX/Store packages, Gateway before Companion. The native Gateway is first
        torn down with its own package-qualified `clawctl teardown`, which removes
        its sandbox account, profile, and listeners; removing the package first
        would delete clawctl and the only record of that account.
      * Win32 uninstall entries (HKCU/HKLM Uninstall keys), e.g. the Inno
        "OpenClaw Companion[ (Dev)]" installer. Inno uninstallers run silently,
        which also removes their local WSL gateway. An entry whose uninstaller
        file is gone is deleted directly.
  - WSL distros OpenClawGateway and OpenClawGateway-Dev (wsl --unregister:
    permanently destroys their filesystems). Inventory comes from the HKCU Lxss
    registrations, not wsl.exe output: a distro whose name does not match but
    whose disk lives under a deletion target blocks the wipe instead of losing
    its ext4.vhdx with the directory.

Never deleted:
  - *.gguf model files inside removed directories (they and their parent
    directories are kept; files inside MSIX package data are copied out and
    digest-verified first). The Hugging Face cache is never scanned and its
    configured root (HF_HUB_CACHE/HF_HOME/XDG_CACHE_HOME, else
    %USERPROFILE%\\.cache\\huggingface\\hub) is excluded even when it sits
    inside a matched directory: recursive deletion checks exclusions before
    every entry, including links, so protected descendants and their ancestors
    survive, and GGUF files inside an excluded tree are not copied out.
  - The gateway source checkout at %USERPROFILE%\\openclaw (see EXCLUDED_PATHS)

Order with --yes: known inventory blockers (unreadable WSL inventory, orphan
native session, WSL storage overlap) stop the wipe before anything changes;
then processes, stop+disable tasks, native Gateway teardown, MSIX packages,
Win32 uninstallers, WSL distros, delete tasks, then registry and files. A
failed teardown, package removal, uninstall, or WSL step stops the wipe before
any file is deleted, so the remaining state can still be removed by a re-run.

Usage:
  python wipe_openclaw_state.py            # dry run: report, delete nothing
  python wipe_openclaw_state.py --yes      # actually delete
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time
import winreg
from ctypes import wintypes
from pathlib import Path
from types import SimpleNamespace

AUTO_START_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
WSL_DISTROS = ("OpenClawGateway", "OpenClawGateway-Dev")
APP_NAME_MATCH = "openclaw"  # case-insensitive substring of names matched everywhere below
# Authoritative Gateway package identities (NativeGatewayPackageIdentity.cs): the Store
# name, the retained development name, and Store-<dev patch> side-by-side registrations.
GATEWAY_PACKAGE_NAMES = frozenset((
    "openclawfoundation.openclawgateway",  # Store
    "openclaw.gateway",  # retained development identity
))
DEV_PATCH = re.compile(r"\A[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?\z")  # mirrors -Patch in Deploy-LocalPackage.ps1
GATEWAY_PACKAGE_PREFIX = "openclawfoundation.openclawgateway"  # Store name or Store-<dev patch>
LXSS_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss"  # per-user WSL distro registrations (name + BasePath)
NATIVE_SESSION = Path("LocalState", "OpenClawGatewayMSIX", "session.json")
MODEL_SUFFIX = ".gguf"
AUMIDS = ("OpenClaw.Companion", "OpenClaw.Companion.Dev")  # installer.iss MyAppAumid
AUMID_PARENTS = (
    r"Software\Classes\AppUserModelId",
    r"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings",
)
UNINSTALL_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
UNINSTALL_ROOTS = (
    ("HKCU", winreg.HKEY_CURRENT_USER, 0),
    ("HKLM", winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_64KEY),
    ("HKLM32", winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_32KEY),
)
INNO_SILENT_ARGS = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
UNINSTALL_WAIT_SECONDS = 300
MSIX_REMOVE_TIMEOUT_SECONDS = 120
TEARDOWN_TIMEOUT_SECONDS = 240
WSL_TIMEOUT_SECONDS = 300
PROCESS_TERMINATE = 0x0001
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
STILL_ACTIVE = 259
ERROR_INVALID_PARAMETER = 87  # OpenProcess on a PID that no longer exists
KILL_EXIT_CODE = 1

_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
_kernel32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
_kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
_kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD),
]
_kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]

appdata = Path(os.environ["APPDATA"])
localappdata = Path(os.environ["LOCALAPPDATA"])
userprofile = Path(os.environ["USERPROFILE"])
temp = Path(os.environ.get("TEMP", localappdata / "Temp"))
programdata = Path(os.environ.get("ProgramData", r"C:\ProgramData"))
START_MENU = r"Microsoft\Windows\Start Menu\Programs"
PRESERVED_MODEL_DIRECTORY = userprofile / "OpenClawPreservedModels"
CLEAN_REPORT_DIRECTORY = temp / "OpenClawCleanReports"

# Direct children of these roots are matched by name; roots are not recursed.
SCAN_ROOTS = (
    appdata, localappdata, localappdata / "Programs", localappdata / "Packages", temp,
    appdata / START_MENU, programdata / START_MENU, programdata,
    Path(os.environ.get("ProgramFiles", r"C:\Program Files")),
    Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")),
)
# Known folders whose *.lnk children are matched by name (resolved at runtime: may be redirected).
SHORTCUT_FOLDERS = ("Desktop", "CommonDesktopDirectory", "Startup", "CommonStartup")
# OpenClaw-owned paths whose names don't contain "openclaw".
EXTRA_PATHS = (userprofile / ".openclaw", temp / "oc-uia")


def huggingface_cache_root() -> Path:
    """The Hugging Face hub cache root, resolved with the same precedence as the product

    (HuggingFaceHubCache.ResolveCacheRoot): HF_HUB_CACHE, HUGGINGFACE_HUB_CACHE,
    HF_HOME\\hub, XDG_CACHE_HOME\\huggingface\\hub, then %USERPROFILE%\\.cache\\huggingface\\hub.
    """
    explicit = os.environ.get("HF_HUB_CACHE") or os.environ.get("HUGGINGFACE_HUB_CACHE")
    if explicit:
        return Path(os.path.expandvars(explicit))
    hub_home = os.environ.get("HF_HOME")
    if hub_home:
        return Path(os.path.expandvars(hub_home), "hub")
    xdg = os.environ.get("XDG_CACHE_HOME")
    if xdg:
        return Path(os.path.expandvars(xdg), "huggingface", "hub")
    return userprofile / ".cache" / "huggingface" / "hub"


# Never deleted, even if a scan root starts matching them.
EXCLUDED_PATHS = (
    userprofile / "openclaw",  # gateway source checkout
    PRESERVED_MODEL_DIRECTORY,
    CLEAN_REPORT_DIRECTORY,  # evidence retained by clean-uninstall.ps1
    huggingface_cache_root(),  # blobs and snapshots are not OpenClaw state and GGUF rule does not cover them
)


def report(label: str, found: bool, detail: str = "", kind: str = "", **data) -> SimpleNamespace:
    print(f"  {'FOUND' if found else 'absent':5s} {label}" + (f"  ({detail})" if detail else ""))
    return SimpleNamespace(label=label, found=found, kind=kind, **data)


def matches(name: str) -> bool:
    return APP_NAME_MATCH in name.lower()


class InventoryError(RuntimeError):
    """A required inventory provider could not establish the machine state."""


def run_ps(script: str) -> list[str]:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command", script],
        capture_output=True, text=True, check=True,
    ).stdout
    return [line.strip() for line in out.splitlines() if line.strip()]


def find_win32_apps() -> list[SimpleNamespace]:
    """Uninstall-key entries (what "Add or remove programs" lists for Win32 apps)."""
    apps = []
    for hive_name, hive, view in UNINSTALL_ROOTS:
        try:
            root = winreg.OpenKey(hive, UNINSTALL_KEY, 0, winreg.KEY_READ | view)
        except FileNotFoundError:
            continue
        with root:
            for i in range(winreg.QueryInfoKey(root)[0]):
                subkey = winreg.EnumKey(root, i)
                try:
                    with winreg.OpenKey(root, subkey) as key:
                        name = winreg.QueryValueEx(key, "DisplayName")[0]
                        uninstall = winreg.QueryValueEx(key, "UninstallString")[0]
                except OSError:
                    continue
                if matches(name):
                    apps.append(SimpleNamespace(
                        name=name, uninstall=uninstall, hive=hive, view=view,
                        key=f"{UNINSTALL_KEY}\\{subkey}", where=f"{hive_name}\\{UNINSTALL_KEY}\\{subkey}",
                    ))
    return apps


def win32_app_registered(app: SimpleNamespace) -> bool:
    try:
        winreg.OpenKey(app.hive, app.key, 0, winreg.KEY_READ | app.view).Close()
        return True
    except FileNotFoundError:
        return False


def uninstaller_exe(command: str) -> str:
    """Executable part of an UninstallString ("C:\\x\\unins000.exe" /args or unquoted)."""
    command = command.strip()
    if command.startswith('"'):
        return command[1:].split('"', 1)[0]
    end = command.lower().find(".exe")
    return command[:end + 4] if end >= 0 else command.split(" ", 1)[0]


def gateway_dev_patch(name: str) -> str | None:
    """Dev-patch suffix of a Store-<patch> Gateway package name (lowercased), or None."""
    suffix = name[len(GATEWAY_PACKAGE_PREFIX) + 1:] if name.lower().startswith(GATEWAY_PACKAGE_PREFIX) else None
    return suffix.lower() if suffix is not None and DEV_PATCH.fullmatch(suffix.lower()) else None


def is_gateway_package(pkg: SimpleNamespace) -> bool:
    """Store, retained development, or Store-<dev patch> Gateway package (NativeGatewayPackageIdentity)."""
    name = pkg.name.lower()
    return name in GATEWAY_PACKAGE_NAMES or gateway_dev_patch(pkg.name) is not None


def find_msix_apps() -> list[SimpleNamespace]:
    """Installed MSIX/Store packages for the current user, Gateway packages first."""
    rows = run_ps(
        f"Get-AppxPackage | Where-Object {{ $_.Name -like '*{APP_NAME_MATCH}*' }} | "
        "ForEach-Object { $_.Name + '|' + $_.PackageFullName + '|' + $_.PackageFamilyName }"
    )
    packages = [SimpleNamespace(name=n, full_name=f, family=fam) for n, f, fam in (r.split("|") for r in rows)]
    return sorted(packages, key=lambda p: not is_gateway_package(p))


def native_session_path(family: str) -> Path:
    return localappdata / "Packages" / family / NATIVE_SESSION


def family_package_name(family: str) -> str:
    """Package name part of an MSIX family name (Name_PublisherId)."""
    name, sep, suffix = family.rpartition("_")
    return name if sep and re.fullmatch(r"[a-z0-9]{13}", suffix.lower()) else family


def find_orphan_native_sessions(installed: list[SimpleNamespace]) -> list[Path]:
    """Gateway session records whose package is gone: their sandbox account can no longer be torn down."""
    families = {p.family.lower() for p in installed}
    try:
        entries = list(os.scandir(localappdata / "Packages"))
    except OSError as exc:
        raise InventoryError(f"could not scan package data folders: {exc}") from exc
    return [
        native_session_path(e.name) for e in entries
        if is_gateway_package(SimpleNamespace(name=family_package_name(e.name), full_name="", family=e.name))
        and e.name.lower() not in families
        and native_session_path(e.name).exists()
    ]


def clawctl_path(pkg: SimpleNamespace) -> Path:
    """The package-qualified execution alias, never an arbitrary clawctl on PATH.

    Store packages alias "clawctl.exe"; dev-patch packages
    (OpenClawGateway-<patch>) alias "clawctl-<patch>.exe".
    """
    base = localappdata / "Microsoft" / "WindowsApps" / pkg.family
    patch = gateway_dev_patch(pkg.name)
    if patch:
        patched = base / f"clawctl-{patch}.exe"
        if os.path.lexists(patched):  # execution aliases are reparse points; lexists avoids following them
            return patched
    return base / "clawctl.exe"



def native_session_sid(pkg: SimpleNamespace) -> str | None:
    """Sandbox account SID recorded by the native Gateway session, or None if no session exists."""
    session = native_session_path(pkg.family)
    if not session.exists():
        return None
    sid = json.loads(session.read_text(encoding="utf-8-sig")).get("agentUserSid")
    if not isinstance(sid, str) or not re.fullmatch(r"S-1-5-21-\d+-\d+-\d+-\d+", sid):
        raise ValueError(f"cannot verify native session account in {session}")
    return sid


def sid_still_present(sid: str) -> bool:
    """True if a local account or user profile with this SID still exists."""
    count = run_ps(
        f"@(Get-CimInstance Win32_UserAccount -Filter 'LocalAccount = True' | Where-Object SID -eq '{sid}').Count + "
        f"@(Get-CimInstance Win32_UserProfile | Where-Object SID -eq '{sid}').Count"
    )
    return count != ["0"]


def _state(result: dict, section: str):
    value = result.get(section)
    return value.get("state") if isinstance(value, dict) else None


def teardown_native_gateway(pkg: SimpleNamespace) -> str | None:
    """Run the package's own `clawctl teardown`. Returns None on verified success, else the failure."""
    clawctl = clawctl_path(pkg)
    if not os.path.lexists(clawctl):  # execution aliases are reparse points; lexists avoids following them
        return f"teardown CLI missing ({clawctl}); repair the Gateway package, then re-run"
    try:
        sid = native_session_sid(pkg)
    except (OSError, ValueError) as exc:
        return str(exc)
    try:
        proc = subprocess.run(
            [str(clawctl), "teardown", "--force", "--json"],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=TEARDOWN_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        return f"clawctl teardown timed out after {TEARDOWN_TIMEOUT_SECONDS}s"
    except OSError as exc:
        return f"could not run {clawctl}: {exc}"
    try:
        result = json.loads(proc.stdout.lstrip("\ufeff"))
    except ValueError:
        return f"clawctl teardown exit {proc.returncode} with unreadable JSON output"
    if not isinstance(result, dict):
        return f"clawctl teardown exit {proc.returncode} with unexpected JSON output"
    gateway, session = _state(result, "gateway"), _state(result, "session")
    if (proc.returncode != 0 or result.get("ok") is not True or result.get("command") != "teardown"
            or gateway != "records-removed" or session not in ("removed", "not-configured")):
        return f"clawctl teardown exit {proc.returncode}, ok={result.get('ok')}, gateway={gateway}, session={session}"
    if native_session_path(pkg.family).exists():
        return "native session record remains after teardown"
    if sid and sid_still_present(sid):
        return f"sandbox account/profile {sid} remains after teardown"
    return None


def uninstall_win32(app: SimpleNamespace) -> bool:
    """Run the app's own uninstaller and wait for its key to go; delete the entry if the uninstaller is gone."""
    cmd = app.uninstall
    exe = Path(uninstaller_exe(cmd))
    if "msiexec" in cmd.lower():
        cmd = cmd.replace("/I{", "/X{").replace("/i{", "/X{")  # /I opens repair UI
    elif not exe.is_file():
        print(f"  uninstaller missing ({exe}); deleting orphan entry {app.where}")
        try:
            winreg.DeleteKeyEx(app.hive, app.key, app.view or winreg.KEY_WOW64_64KEY)
        except OSError as exc:
            print(f"  could not delete {app.where}: {exc}")
        return not win32_app_registered(app)
    elif re.fullmatch(r"unins\d{3}\.exe", exe.name, re.IGNORECASE):
        cmd = f"{cmd} {INNO_SILENT_ARGS}"  # silent Inno uninstall also removes its WSL gateway (installer.iss)
    print(f"  running uninstaller for {app.name}: {cmd}")
    try:
        subprocess.run(cmd, shell=True, timeout=UNINSTALL_WAIT_SECONDS)
    except subprocess.TimeoutExpired:
        print(f"  uninstaller timed out after {UNINSTALL_WAIT_SECONDS}s")
        return False
    # Inno's unins000.exe hands off to a temp copy, so the launched process can exit early.
    deadline = time.monotonic() + UNINSTALL_WAIT_SECONDS
    while win32_app_registered(app) and time.monotonic() < deadline:
        time.sleep(2)
    return not win32_app_registered(app)


def uninstall_msix(pkg: SimpleNamespace) -> bool:
    try:
        code = subprocess.run(
            ["powershell", "-NoProfile", "-Command", f"Remove-AppxPackage -Package '{pkg.full_name}' -ErrorAction Stop"],
            timeout=MSIX_REMOVE_TIMEOUT_SECONDS,
        ).returncode
    except subprocess.TimeoutExpired:
        print(f"  Remove-AppxPackage timed out after {MSIX_REMOVE_TIMEOUT_SECONDS}s")
        return False
    return code == 0 and all(p.full_name != pkg.full_name for p in find_msix_apps())


def find_processes(targets: list[Path]) -> list[SimpleNamespace]:
    """OpenClaw-owned processes, with the identity needed to revalidate them at kill time.

    Selection is by executable name (OpenClaw*/winnode*) or by running from an
    OpenClaw-owned location: a deletion target discovered by find_paths(), or an
    OpenClaw MSIX package tree (WindowsApps package dirs and the per-family alias
    folder). A raw "openclaw" substring of any path, or a scan root as a whole,
    is not ownership: those would select an openclaw-named user's unrelated apps
    and every other program under %LOCALAPPDATA%.
    Other users' processes (e.g. the native Gateway's sandbox account) report no path
    to a non-elevated caller; clawctl teardown owns those.
    Identity is captured through a handle at discovery and rechecked on the handle
    used for termination, so a reused PID is never killed.
    """
    rows = run_ps(
        "Get-CimInstance Win32_Process | "
        "ForEach-Object { '' + $_.ProcessId + '|' + $_.Name + '|' + $_.ExecutablePath }"
    )
    own = {os.getpid(), os.getppid()}
    procs = []
    for row in rows:
        pid_text, name, path = row.split("|", 2)
        pid = int(pid_text)
        if pid in own:
            continue
        if not (name.lower().startswith(("openclaw", "winnode")) or (path and owned_process_path(Path(path), targets))):
            continue
        if path and is_excluded(Path(path)):
            continue
        procs.append(SimpleNamespace(pid=pid, name=name, path=path, identity=process_identity(pid)))
    return procs


def owned_process_path(path: Path, targets: list[Path]) -> bool:
    """True when the executable runs from a deletion target or an OpenClaw package tree."""
    if any(path.is_relative_to(target) or target.is_relative_to(path) for target in targets):
        return True
    parts = [p.lower() for p in path.parts]
    if "windowsapps" in parts:
        # MSIX package dirs: C:\...\WindowsApps\<NameWithPublisher> and the per-family
        # alias folder C:\...\WindowsApps\<Family>\alias.exe; ownership is the package
        # component name, not the shared WindowsApps root.
        index = parts.index("windowsapps")
        package = path.parts[index + 1] if len(path.parts) > index + 1 else ""
        return matches(package)
    return False


def _handle_identity(handle) -> tuple[str, int] | None:
    """Image path and creation FILETIME of a process handle, or None if unreadable."""
    buffer = ctypes.create_unicode_buffer(32768)
    size = wintypes.DWORD(len(buffer))
    if not _kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
        return None
    created = wintypes.FILETIME()
    null = wintypes.FILETIME()
    if not _kernel32.GetProcessTimes(handle, ctypes.byref(null), ctypes.byref(null), ctypes.byref(null), ctypes.byref(created)):
        return None
    return buffer.value, (created.dwHighDateTime << 32) | created.dwLowDateTime


def process_identity(pid: int) -> tuple[str, int] | None:
    """Image path and creation time of a PID, or None when it cannot be read."""
    handle = _kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        return _handle_identity(handle)
    finally:
        _kernel32.CloseHandle(handle)


def terminate_if_unchanged(proc: SimpleNamespace) -> str:
    """Terminate proc only when the handle still matches its discovery identity.

    The handle is opened once, kept, and terminated through, so the PID stays
    pinned: the identity is verified on the same kernel object that
    TerminateProcess acts on. A process that exited, or whose PID was reused by
    an unrelated process, is never killed (mirrors Stop-CleanProcess).
    """
    handle = _kernel32.OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, False, proc.pid)
    if not handle:
        err = ctypes.get_last_error()
        return "gone" if err == ERROR_INVALID_PARAMETER else f"open failed (WinError {err})"
    try:
        exit_code = wintypes.DWORD()
        if not _kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
            return f"exit code unreadable (WinError {ctypes.get_last_error()})"
        if exit_code.value != STILL_ACTIVE:
            return "gone"  # exited since discovery while the handle kept the PID pinned
        if _handle_identity(handle) != proc.identity:
            return "changed"
        if not _kernel32.TerminateProcess(handle, KILL_EXIT_CODE):
            return f"terminate failed (WinError {ctypes.get_last_error()})"
        return "killed"
    finally:
        _kernel32.CloseHandle(handle)


def stop_processes(procs: list[SimpleNamespace]) -> int:
    """Kill each discovered process; returns the number of failures."""
    errors = 0
    for p in procs:
        if p.identity is None:
            errors += 1
            print(f"  ERROR  PID {p.pid} {p.name}: identity unreadable at discovery "
                  "(another user's process or already exited); not killing")
            continue
        result = terminate_if_unchanged(p)
        if result == "killed":
            print(f"  killed PID {p.pid} {p.name}")
        elif result == "gone":
            print(f"  gone    PID {p.pid} {p.name} (exited)")
        elif result == "changed":
            errors += 1
            print(f"  SKIP   PID {p.pid} {p.name}: identity changed since discovery, not killing")
        else:
            errors += 1
            print(f"  ERROR  kill PID {p.pid} {p.name}: {result}")
    return errors


def find_scheduled_tasks() -> list[str]:
    """Full task names (TaskPath + TaskName) of scheduled tasks matching APP_NAME_MATCH."""
    return run_ps(
        "Get-ScheduledTask -ErrorAction Stop | "
        f"Where-Object {{ ($_.TaskPath + $_.TaskName) -like '*{APP_NAME_MATCH}*' }} | "
        "ForEach-Object { $_.TaskPath + $_.TaskName }"
    )


def task_exists(task: str) -> bool | None:
    """True/False when schtasks can answer, None when the query itself failed."""
    code = subprocess.run(["schtasks", "/Query", "/TN", task], capture_output=True).returncode
    return None if code not in (0, 1) else code == 0


def registered_wsl_distros() -> dict[str, Path]:
    """Distro name to storage location from HKCU Lxss registrations.

    Empty when this user never used WSL (no Lxss tree). The registry is
    authoritative for both membership (names) and storage (BasePath):
    wsl.exe output is localized (a clean machine prints "no installed distributions"
    with a nonzero exit) and drops BasePath, so it can report absence where state
    still exists and cannot protect custom-named distros whose disk lives under a
    directory this wipe selects. An unreadable registration is unknown state, not
    absence: InventoryError aborts the wipe before anything is deleted.
    """
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, LXSS_KEY)
    except FileNotFoundError:
        return {}  # no Lxss tree: WSL was never used on this machine
    except OSError as exc:
        raise InventoryError(f"could not read WSL registrations: {exc}") from exc
    distros: dict[str, Path] = {}
    with key:
        for i in range(winreg.QueryInfoKey(key)[0]):
            subkey_name = winreg.EnumKey(key, i)
            with winreg.OpenKey(key, subkey_name) as subkey:
                try:
                    name = winreg.QueryValueEx(subkey, "DistributionName")[0]
                    base = winreg.QueryValueEx(subkey, "BasePath")[0]
                except OSError as exc:
                    raise InventoryError(f"could not read WSL registration {subkey_name}: {exc}") from exc
            distros[str(name)] = Path(str(base).removeprefix("\\\\?\\"))
    return distros


def unregister_wsl(distro: str) -> bool:
    """Unregister and verify through the registry; a failed or unreadable verification is unknown state."""
    try:
        subprocess.run(["wsl", "--terminate", distro], capture_output=True, timeout=60)
        code = subprocess.run(
            ["wsl", "--unregister", distro], capture_output=True, timeout=WSL_TIMEOUT_SECONDS,
        ).returncode
        remaining = registered_wsl_distros()
    except subprocess.TimeoutExpired:
        print(f"  wsl timed out for {distro}")
        return False
    except OSError as exc:
        print(f"  wsl could not run for {distro}: {exc}")
        return False
    except InventoryError as exc:
        print(f"  wsl verification failed for {distro}: {exc}")
        return False
    return code == 0 and distro not in remaining


def wsl_storage_conflicts(targets: list[Path], registered: dict[str, Path]) -> list[tuple[str, Path]]:
    """Distro names whose disk storage lies under a deletion target, with that target."""
    conflicts = []
    for distro, base in registered.items():
        if distro in WSL_DISTROS:
            continue  # unregistered explicitly by this wipe; their storage goes with them
        norm_base = os.path.normcase(str(base))
        for target in targets:
            if norm_base == os.path.normcase(str(target)) or Path(norm_base).is_relative_to(target):
                conflicts.append((distro, target))
                break
    return sorted(conflicts, key=lambda c: (str(c[1]).lower(), c[0].lower()))

def is_excluded(path: Path) -> bool:
    return any(path == ex or ex in path.parents for ex in EXCLUDED_PATHS)


def shortcut_roots() -> list[Path]:
    return [Path(p) for p in run_ps("; ".join(f"[Environment]::GetFolderPath('{f}')" for f in SHORTCUT_FOLDERS))]


def find_paths() -> list[Path]:
    found: dict[str, Path] = {}

    def scan(root: Path, shortcuts_only: bool) -> None:
        try:
            entries = list(os.scandir(root))
        except FileNotFoundError:
            return
        except OSError as exc:
            raise InventoryError(f"could not scan {root}: {exc}") from exc
        for entry in entries:
            if matches(entry.name) and (not shortcuts_only or entry.name.lower().endswith(".lnk")):
                found[os.path.normcase(entry.path)] = Path(entry.path)

    for root in SCAN_ROOTS:
        scan(root, shortcuts_only=False)
    for root in shortcut_roots():
        scan(root, shortcuts_only=True)
    for path in EXTRA_PATHS:
        if path.exists():
            found[os.path.normcase(path)] = path
    return sorted((p for p in found.values() if not is_excluded(p)), key=lambda p: str(p).lower())


def models_in(path: Path) -> list[Path]:
    """*.gguf files remove_path would keep (symlinks are removed: they are not the model)."""
    if path.suffix.lower() == MODEL_SUFFIX and not path.is_symlink():
        return [path]
    if path.is_dir() and not path.is_symlink() and not path.is_junction():
        return [p for p in path.rglob(f"*{MODEL_SUFFIX}")
                if p.is_file() and not p.is_symlink() and not is_excluded(p)]
    return []

def model_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def preserved_model_path(model: Path) -> Path:
    """Stable collision-free destination outside package and installation trees."""
    source_id = hashlib.sha256(os.path.normcase(str(model)).encode("utf-8")).hexdigest()
    return PRESERVED_MODEL_DIRECTORY / source_id / model.name


def preserve_models(models: list[Path]) -> list[Path]:
    """Copy and digest-verify models before an uninstaller can delete their source."""
    preserved = []
    for model in models:
        destination = preserved_model_path(model)
        source_digest = model_digest(model)
        if destination.is_file() and model_digest(destination) == source_digest:
            preserved.append(destination)
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.with_name(f"{destination.name}.partial")
        try:
            with model.open("rb") as source, temporary.open("wb") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
                target.flush()
                os.fsync(target.fileno())
            if model_digest(temporary) != source_digest:
                raise OSError(f"copied model digest does not match source: {model}")
            os.replace(temporary, destination)
        finally:
            if temporary.exists():
                temporary.unlink()
        preserved.append(destination)
    return preserved

def remove_path(path: Path, kept: list[Path]) -> None:
    """Delete path, keeping *.gguf model files and the directories that hold them.

    Excluded paths are checked before anything is touched, including links, so a
    recursive deletion of an allowed parent never reaches a protected descendant;
    ancestors are only removed when empty, so they are kept too.
    """
    if is_excluded(path):
        kept.append(path)
        return
    if path.is_junction() or (path.is_symlink() and path.is_dir()):
        os.rmdir(path)  # removes the link only, never the target's contents
    elif path.is_symlink():
        path.unlink()
    elif path.is_dir():
        for child in list(path.iterdir()):
            remove_path(child, kept)
        if not any(path.iterdir()):
            os.rmdir(path)
    elif path.suffix.lower() == MODEL_SUFFIX:
        kept.append(path)
    else:
        path.chmod(stat.S_IWRITE)
        path.unlink()


def existing_hkcu_key(subkey: str) -> bool:
    try:
        winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey).Close()
        return True
    except FileNotFoundError:
        return False


def matching_subkeys(parent: str) -> list[str]:
    """HKCU\\<parent>\\<child> paths for direct children whose name matches."""
    try:
        root = winreg.OpenKey(winreg.HKEY_CURRENT_USER, parent)
    except FileNotFoundError:
        return []
    with root:
        names = [winreg.EnumKey(root, i) for i in range(winreg.QueryInfoKey(root)[0])]
    return [f"{parent}\\{name}" for name in names if matches(name)]


def matching_run_values() -> list[str]:
    """HKCU Run value names whose name or command line mentions OpenClaw."""
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTO_START_KEY)
    except FileNotFoundError:
        return []
    with key:
        values = [winreg.EnumValue(key, i) for i in range(winreg.QueryInfoKey(key)[1])]
    return [name for name, data, _ in values if matches(name) or matches(str(data))]


def delete_key_tree(subkey: str) -> None:
    """winreg.DeleteKey refuses keys with children, so delete depth-first."""
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_READ) as key:
        children = [winreg.EnumKey(key, i) for i in range(winreg.QueryInfoKey(key)[0])]
    for child in children:
        delete_key_tree(f"{subkey}\\{child}")
    winreg.DeleteKey(winreg.HKEY_CURRENT_USER, subkey)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--yes", action="store_true", help="actually delete (default: dry run)")
    args = parser.parse_args()
    mode = "WIPE" if args.yes else "DRY RUN"
    found = 0

    print(f"== {mode}: installed apps (Add or remove programs) ==")
    win32_apps = find_win32_apps()
    for app in win32_apps:
        exe = Path(uninstaller_exe(app.uninstall))
        detail = app.where if "msiexec" in app.uninstall.lower() or exe.is_file() else f"{app.where}; uninstaller missing, entry deleted directly"
        report(f"app {app.name}", True, detail)
    msix_apps = find_msix_apps()
    for pkg in msix_apps:
        report(f"app {pkg.full_name}", True, "MSIX package")
    if not win32_apps and not msix_apps:
        report(f"apps matching '{APP_NAME_MATCH}'", False)
    found += len(win32_apps) + len(msix_apps)

    print(f"== {mode}: native Gateway teardown ==")
    gateways = [p for p in msix_apps if is_gateway_package(p)]
    for pkg in gateways:
        try:
            sid = native_session_sid(pkg)
            account = f"sandbox account {sid}" if sid else "no session recorded"
        except (OSError, ValueError) as exc:
            account = f"session unreadable: {exc}"
        cli = "clawctl present" if os.path.lexists(clawctl_path(pkg)) else "clawctl MISSING, teardown will fail"
        report(f"teardown {pkg.family}", True, f"{cli}; {account}")
    orphans = find_orphan_native_sessions(msix_apps)
    for session in orphans:
        print(f"  BLOCK  {session}  (package gone, sandbox account cannot be torn down; "
              "reinstall that Gateway package and re-run)")
    if not gateways and not orphans:
        report("native Gateway packages", False)

    print(f"== {mode}: scheduled tasks ==")
    tasks = find_scheduled_tasks()
    for task in tasks:
        report(f"task {task}", True)
    if not tasks:
        report(f"scheduled tasks matching '{APP_NAME_MATCH}'", False)
    found += len(tasks)

    print(f"== {mode}: files and directories ==")
    paths = find_paths()
    for path in paths:
        models = models_in(path)
        detail = "dir" if path.is_dir() else "file"
        if models:
            detail += f"; keeps {len(models)} model file(s)"
        held = [ex for ex in EXCLUDED_PATHS if path in ex.parents]
        if held:
            detail += f"; keeps excluded {', '.join(map(str, held))}"
        report(str(path), True, detail)
    if not paths:
        report(f"paths matching '{APP_NAME_MATCH}'", False)
    for path in EXCLUDED_PATHS:
        print(f"  keep   {path}  (excluded)")
    found += len(paths)

    print(f"== {mode}: registry (HKCU) ==")
    run_values = matching_run_values()
    for name in run_values:
        report(f"HKCU\\{AUTO_START_KEY}\\{name}", True, "autostart value")
    reg_keys = matching_subkeys("Software") + matching_subkeys(r"Software\Classes")
    reg_keys += [k for k in (f"{parent}\\{aumid}" for parent in AUMID_PARENTS for aumid in AUMIDS) if existing_hkcu_key(k)]
    for subkey in reg_keys:
        report(f"HKCU\\{subkey}", True)
    if not run_values and not reg_keys:
        report(f"registry entries matching '{APP_NAME_MATCH}'", False)
    found += len(run_values) + len(reg_keys)

    print(f"== {mode}: processes ==")
    procs = find_processes(paths)
    for p in procs:
        report(f"process PID {p.pid} {p.name}", True, p.path or "path not visible")
    if not procs:
        report("OpenClaw processes", False)
    found += len(procs)

    print(f"== {mode}: WSL distros (unregistered: filesystem destroyed) ==")
    try:
        registered = registered_wsl_distros()
    except InventoryError as exc:
        registered = {}
        wsl_error = f"could not establish WSL inventory: {exc}"
    else:
        wsl_error = None
    distros = [d for d in WSL_DISTROS if d in registered]
    for distro in WSL_DISTROS:
        if wsl_error:
            print(f"  ERROR  {wsl_error}")
            break
        report(f"distro {distro}", distro in distros)
    conflicts = wsl_storage_conflicts(paths, registered)
    for distro, target in conflicts:
        print(f"  BLOCK  distro {distro} stores its disk under {target}; "
              "unregister it first or exclude that path")
    found += len(distros)

    if not found and not orphans and not conflicts and not wsl_error:
        print("\nNothing to remove. Machine is clean.")
        return 0

    if not args.yes:
        print(f"\nDry run: {found} target(s) would be removed. Re-run with --yes to delete.")
        return 1 if orphans or conflicts or wsl_error else 0


    print("\n== deleting ==")
    errors = 0

    # Known inventory blockers are rejected before anything changes: stopping
    # processes or disabling tasks that a blocked wipe then leaves behind would
    # be mutation with no way for a re-run to undo it.
    def abort(reason: str) -> int:
        print(f"  ERROR  {reason}")
        print("\nStopped before deleting files. Fix the error above and re-run.")
        return 1

    if wsl_error:
        return abort(wsl_error)
    if orphans:
        return abort(f"orphan native session {orphans[0]}: reinstall that Gateway package and re-run")
    if conflicts:
        distro, target = conflicts[0]
        return abort(f"distro {distro} stores its disk under {target}; unregister it first or exclude that path")

    # Processes first: they lock files and package resources. Each process is
    # terminated through a handle whose identity was verified on that same
    # handle (see terminate_if_unchanged); each process is killed individually
    # (no tree kill), so exclusions and identity checks cannot be bypassed.
    errors += stop_processes(procs)

    # Stop and disable tasks so logon/timer triggers can't relaunch anything mid-wipe.
    # They are deleted after the uninstallers, which may own them.
    for task in tasks:
        subprocess.run(["schtasks", "/End", "/TN", task], capture_output=True)
        if subprocess.run(["schtasks", "/Change", "/TN", task, "/DISABLE"], capture_output=True).returncode == 0:
            print(f"  disabled task {task}")
        else:
            print(f"  WARN   could not disable task {task}")

    # Fatal stages: stop before deleting files so remaining state can still be removed by a re-run.
    models_to_preserve = [model for path in paths for model in models_in(path)]
    try:
        preserved_models = preserve_models(models_to_preserve)
    except OSError as exc:
        return abort(f"could not preserve model files: {exc}")
    for model in preserved_models:
        print(f"  preserved model {model}")

    for pkg in gateways:
        failure = teardown_native_gateway(pkg)
        if failure:
            return abort(f"native Gateway teardown {pkg.family}: {failure}")
        print(f"  tore down native Gateway {pkg.family}")
    for pkg in msix_apps:
        if not uninstall_msix(pkg):
            return abort(f"MSIX {pkg.full_name} still installed")
        print(f"  uninstalled {pkg.full_name}")
    for app in win32_apps:
        if not uninstall_win32(app):
            return abort(f"{app.name} still registered at {app.where} (cancelled, failed, or needs admin)")
        print(f"  uninstalled {app.name}")
    try:
        remaining = registered_wsl_distros()
    except InventoryError as exc:
        return abort(f"could not verify WSL removal: {exc}")
    for distro in distros:
        if distro not in remaining:
            print(f"  gone    distro {distro} (removed by uninstaller)")
        elif unregister_wsl(distro):
            print(f"  unregistered distro {distro}")
        else:
            return abort(f"WSL distro {distro} still registered")

    for task in tasks:
        exists = task_exists(task)
        if exists is None:
            errors += 1
            print(f"  ERROR  task {task}: schtasks query failed; not deleted")
            continue
        if not exists:
            print(f"  gone    task {task} (removed by uninstaller)")
            continue
        result = subprocess.run(["schtasks", "/Delete", "/TN", task, "/F"], capture_output=True, text=True)
        if result.returncode == 0:
            print(f"  removed task {task}")
        else:
            errors += 1
            print(f"  ERROR  task {task}: {result.stderr.strip() or f'schtasks exit {result.returncode}'}")

    for name in run_values:
        label = f"HKCU\\{AUTO_START_KEY}\\{name}"
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTO_START_KEY, 0, winreg.KEY_SET_VALUE) as key:
                winreg.DeleteValue(key, name)
            print(f"  removed {label}")
        except FileNotFoundError:
            print(f"  gone    {label} (removed by uninstaller)")
        except OSError as exc:
            errors += 1
            print(f"  ERROR  {label}: {exc}")
    for subkey in reg_keys:
        try:
            delete_key_tree(subkey)
            print(f"  removed HKCU\\{subkey}")
        except FileNotFoundError:
            print(f"  gone    HKCU\\{subkey} (removed by uninstaller)")
        except OSError as exc:
            errors += 1
            print(f"  ERROR  HKCU\\{subkey}: {exc}")

    kept: list[Path] = []
    for path in paths:
        if not path.exists() and not path.is_symlink():
            print(f"  gone    {path} (removed by uninstaller)")
            continue
        try:
            remove_path(path, kept)
            print(f"  {'kept' if path.exists() else 'removed':7s} {path}" + ("  (holds kept files)" if path.exists() else ""))
        except OSError as exc:
            errors += 1
            print(f"  ERROR  {path}: {exc}")
    for item in kept:
        kind = "model" if item.suffix.lower() == MODEL_SUFFIX else "excluded"
        print(f"  kept   {item}  ({kind})")

    if errors:
        print(f"\nCompleted with {errors} error(s); re-run to retry.")
        return 1
    print("\nDone. All located OpenClaw state removed.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (InventoryError, OSError, subprocess.SubprocessError) as exc:
        print(f"ERROR: could not establish complete cleanup inventory: {exc}", file=sys.stderr)
        sys.exit(1)
