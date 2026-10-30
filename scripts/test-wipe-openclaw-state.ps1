# Regression coverage for scripts/wipe_openclaw_state.py selection and preservation logic.
# Reviews of PR #1734 flagged zero tests for this script; each case below protects one
# concrete safety contract: which packages count as the native Gateway, how the
# dev-patch alias is derived, which processes/paths deletion owns, which WSL storage
# blocks a wipe, which model files survive removal, that excluded descendants survive
# recursive deletion, that a changed process identity is never terminated, and that an
# absent or unreadable Lxss registration is empty inventory or an abort (never absence).
# The driver runs with APPDATA/LOCALAPPDATA/USERPROFILE/TEMP/ProgramData/ProgramFiles
# redirected under the fixture root before the module is imported, so the module's
# computed paths and every fixture it creates are isolated from the real machine.

[CmdletBinding()]
param([string]$RepoRoot = $(if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { (Get-Location).Path }))
$ErrorActionPreference = 'Stop'
$script = Join-Path $RepoRoot 'scripts\wipe_openclaw_state.py'
if (-not (Test-Path -LiteralPath $script)) { throw "wipe script missing: $script" }

$driver = @'
import importlib.util, json, os, subprocess, sys, uuid
from pathlib import Path

script = Path(sys.argv[1])
root = Path(sys.argv[2])

# Isolate every profile path the module computes at import time (and tempfile's TEMP).
env = root / "env"
for name in ("AppData", "LocalAppData", "UserProfile", "Temp", "ProgramData", "ProgramFiles", "ProgramFiles86"):
    (env / name).mkdir(parents=True, exist_ok=True)
os.environ["APPDATA"] = str(env / "AppData")
os.environ["LOCALAPPDATA"] = str(env / "LocalAppData")
os.environ["USERPROFILE"] = str(env / "UserProfile")
os.environ["TEMP"] = str(env / "Temp")
os.environ["TMP"] = str(env / "Temp")
os.environ["ProgramData"] = str(env / "ProgramData")
os.environ["ProgramFiles"] = str(env / "ProgramFiles")
os.environ["ProgramFiles(x86)"] = str(env / "ProgramFiles86")
os.environ["HF_HUB_CACHE"] = str(env / "LocalAppData" / "OpenClawTray" / "hf" / "hub")

spec = importlib.util.spec_from_file_location("wipe", str(script))
wipe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wipe)

failures = []
checks = 0

def check(label, actual, expected):
    global checks
    checks += 1
    if actual != expected:
        failures.append(f"{label}: got {actual!r}, want {expected!r}")

class P:
    pass

def pkg(name):
    p = P(); p.name = name; return p

check("module uses isolated LOCALAPPDATA", Path(wipe.localappdata), env / "LocalAppData")
check("module uses isolated APPDATA", Path(wipe.appdata), env / "AppData")
check("module uses isolated USERPROFILE", Path(wipe.userprofile), env / "UserProfile")
check("module uses isolated TEMP", Path(wipe.temp), env / "Temp")

# Native Gateway identity set (NativeGatewayPackageIdentity: Store, development, Store-<patch>).
check("store gateway", wipe.is_gateway_package(pkg("OpenClawFoundation.OpenClawGateway")), True)
check("retained dev gateway", wipe.is_gateway_package(pkg("OpenClaw.Gateway")), True)
check("patched gateway", wipe.is_gateway_package(pkg("OpenClawFoundation.OpenClawGateway-beta1")), True)
check("companion not gateway", wipe.is_gateway_package(pkg("OpenClawFoundation.OpenClawCompanion")), False)
check("lookalike not gateway", wipe.is_gateway_package(pkg("SomeVendor.OpenClawGatewayClone")), False)
check("empty patch suffix", wipe.is_gateway_package(pkg("OpenClawFoundation.OpenClawGateway-")), False)
check("overlong patch suffix", wipe.is_gateway_package(pkg("OpenClawFoundation.OpenClawGateway-" + "a" * 16)), False)

# Dev-patch extraction is validated and lowercased, so the alias is derived, not assumed.
check("patch extract valid", wipe.gateway_dev_patch("OpenClawFoundation.OpenClawGateway-beta1"), "beta1")
check("patch extract mixed case", wipe.gateway_dev_patch("OpenClawFoundation.OpenClawGateway-Beta1"), "beta1")
check("patch extract invalid", wipe.gateway_dev_patch("OpenClawFoundation.OpenClawGateway-Bad!Name"), None)
check("patch extract non-store", wipe.gateway_dev_patch("OpenClaw.Gateway"), None)

alias_dir = Path(wipe.localappdata) / "Microsoft" / "WindowsApps"
patched_pkg = P()
patched_pkg.name = "OpenClawFoundation.OpenClawGateway-beta1"
patched_pkg.family = "OpenClawFoundation.OpenClawGateway_cw5n1h2txyewy"
alias_dir.mkdir(parents=True, exist_ok=True)
created_alias = alias_dir / patched_pkg.family / "clawctl-beta1.exe"
created_alias.parent.mkdir(parents=True, exist_ok=True)
created_alias.touch()
check("patched alias derived from package name", wipe.clawctl_path(patched_pkg), created_alias)
store_pkg = P()
store_pkg.name = "OpenClawFoundation.OpenClawGateway"
store_pkg.family = "OpenClawFoundation.OpenClawGateway_cw5n1h2txyewy"
check("store alias plain", str(wipe.clawctl_path(store_pkg)).endswith("clawctl.exe"), True)

# Process ownership: executable name, deletion targets, and OpenClaw MSIX package trees.
targets = [Path(r"C:\Users\pedro\AppData\Local\Programs\OpenClaw")]
check("process under deletion target", wipe.owned_process_path(Path(r"C:\Users\pedro\AppData\Local\Programs\OpenClaw\llama-server.exe"), targets), True)
check("deletion target is its own exe", wipe.owned_process_path(targets[0], targets), True)
check("unrelated app not owned", wipe.owned_process_path(Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe"), targets), False)
check("openclaw-username unrelated app not owned", wipe.owned_process_path(Path(r"C:\Users\openclaw\AppData\Local\Programs\Unrelated\app.exe"), targets), False)
check("msix package dir owned", wipe.owned_process_path(Path(r"C:\Program Files\WindowsApps\OpenClawFoundation.OpenClawGateway_1.0_x64__cw5n1h2txyewy\claw.exe"), []), True)
check("msix alias family owned", wipe.owned_process_path(Path(r"C:\Users\pedro\AppData\Local\Microsoft\WindowsApps\OpenClawFoundation.OpenClawGateway_cw5n1h2txyewy\clawctl.exe"), []), True)
check("msix other vendor not owned", wipe.owned_process_path(Path(r"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.0_x64__zpdnekdrzrea0\Spotify.exe"), []), False)

# WSL storage overlap: a custom-named distro under a deletion target blocks the wipe.
registered = {
    "OpenClawGateway": Path(r"C:\Users\pedro\AppData\Local\Temp\OpenClawTray\wsl"),
    "MyCustomDistro": Path(r"C:\Users\pedro\AppData\Local\Temp\OpenClawTray\custom"),
    "Ubuntu": Path(r"C:\Users\pedro\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu\LocalState"),
}
conflicts = wipe.wsl_storage_conflicts(targets + [Path(r"C:\Users\pedro\AppData\Local\Temp\OpenClawTray")], registered)
check("conflict count", len(conflicts), 1)
check("conflict distro", conflicts[0][0] if conflicts else None, "MyCustomDistro")
check("named target distro skipped", all(c[0] != "OpenClawGateway" for c in conflicts), True)
check("no conflict without WSL", wipe.wsl_storage_conflicts(targets, {}), [])

# Hugging Face cache root precedence matches HuggingFaceHubCache.ResolveCacheRoot.
for var in ("HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "HF_HOME", "XDG_CACHE_HOME"):
    os.environ.pop(var, None)
check("hf default root", wipe.huggingface_cache_root(), Path(wipe.userprofile) / ".cache" / "huggingface" / "hub")
os.environ["HF_HOME"] = r"C:\Users\pedro\mymodels"
check("hf home root", wipe.huggingface_cache_root(), Path(r"C:\Users\pedro\mymodels", "hub"))
os.environ["HUGGINGFACE_HUB_CACHE"] = r"C:\Users\pedro\hflegacy"
check("hf legacy env root", wipe.huggingface_cache_root(), Path(r"C:\Users\pedro\hflegacy"))
os.environ["HF_HUB_CACHE"] = r"C:\Users\pedro\hfexplicit"
check("hf explicit env root", wipe.huggingface_cache_root(), Path(r"C:\Users\pedro\hfexplicit"))
# Restore the import-time cache root for the excluded-descendant checks below.
os.environ["HF_HUB_CACHE"] = str(Path(wipe.localappdata) / "OpenClawTray" / "hf" / "hub")

# PID identity: only an exact path+creation-time match on the held handle authorizes termination.
child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
try:
    ident = wipe.process_identity(child.pid)
    check("identity readable", ident is not None, True)
    check("identity path is this python", ident[0], sys.executable)
    stale = P(); stale.pid = child.pid; stale.identity = (ident[0], ident[1] - 1)
    check("reused PID creation time rejected", wipe.terminate_if_unchanged(stale), "changed")
    check("stale-identity process alive", child.poll() is None, True)
    stale.identity = (ident[0] + ".old", ident[1])
    check("reused PID path rejected", wipe.terminate_if_unchanged(stale), "changed")
    check("stale-identity process still alive", child.poll() is None, True)
    proc = P(); proc.pid = child.pid; proc.name = "proof"; proc.path = ident[0]; proc.identity = ident
    import contextlib, io
    with contextlib.redirect_stdout(io.StringIO()):  # keep the JSON summary stdout-clean
        stop_errors = wipe.stop_processes([proc])
    check("matching identity terminates", stop_errors, 0)
    check("exited PID reported gone", wipe.terminate_if_unchanged(proc), "gone")
finally:
    if child.poll() is None:
        child.kill()

# Excluded descendants: recursive deletion never reaches them and keeps their ancestors.
tray = Path(wipe.localappdata) / "OpenClawTray"
blob = tray / "hf" / "hub" / "models--proof--tiny" / "blobs" / "0123abcd"
blob.parent.mkdir(parents=True, exist_ok=True)
blob.write_bytes(b"blob-bytes")
snapshot = tray / "hf" / "hub" / "models--proof--tiny" / "snapshots" / "main" / "config.json"
snapshot.parent.mkdir(parents=True, exist_ok=True)
os.symlink(os.path.relpath(blob, snapshot.parent), snapshot)
(tray / "hf" / "hub" / "x.gguf").write_bytes(b"model-in-excluded")
(tray / "settings.json").write_text("{}")
check("excluded gguf not reported as kept model", wipe.models_in(tray), [])
kept = []
wipe.remove_path(tray, kept)
check("excluded blob survives", blob.is_file(), True)
check("excluded symlink survives", snapshot.exists(), True)
check("unrelated file removed", (tray / "settings.json").exists(), False)
check("kept list records excluded root", str(Path(wipe.huggingface_cache_root())) in {str(k) for k in kept}, True)

# Model preservation: remove_path keeps *.gguf files and their parent directories.
import tempfile
with tempfile.TemporaryDirectory() as tree:
    root = Path(tree)
    (root / "OpenClawTray" / "LocalAI" / "models").mkdir(parents=True)
    (root / "OpenClawTray" / "settings.json").write_text("{}")
    (root / "OpenClawTray" / "LocalAI" / "models" / "keep.gguf").write_bytes(b"model-bytes")
    (root / "OpenClawTray" / "LocalAI" / "models" / "drop.bin").write_bytes(b"other")
    kept = []
    wipe.remove_path(root / "OpenClawTray", kept)
    check("model kept", (root / "OpenClawTray" / "LocalAI" / "models" / "keep.gguf").is_file(), True)
    check("model parent kept", (root / "OpenClawTray" / "LocalAI" / "models").is_dir(), True)
    check("non-model removed", (root / "OpenClawTray" / "settings.json").exists(), False)
    check("kept list records model", kept, [root / "OpenClawTray" / "LocalAI" / "models" / "keep.gguf"])

# Orphan session detection covers the retained development identity, not just the Store prefix.
packages_root = Path(wipe.localappdata) / "Packages"
dev_family = "OpenClaw.Gateway_4tq5h6e8r9k2m"
session = packages_root / dev_family / "LocalState" / "OpenClawGatewayMSIX" / "session.json"
session.parent.mkdir(parents=True, exist_ok=True)
session.write_text(json.dumps({"agentUserSid": "S-1-5-21-1-2-3-4"}), encoding="utf-8")
orphans = wipe.find_orphan_native_sessions([])  # no installed packages: session is orphaned
check("dev identity orphan detected", any(str(o).startswith(str(packages_root / dev_family)) for o in orphans), True)
installed = [P()]
installed[0].name = "OpenClaw.Gateway"
installed[0].family = dev_family
check("installed dev package not orphan", all(str(o) != str(session) for o in wipe.find_orphan_native_sessions(installed)), True)

# WSL inventory through HKCU: an absent Lxss tree is empty inventory; a broken
# registration is unknown state. HKCU is redirected to a scratch key so neither
# case can touch the machine's real WSL registrations.
import ctypes
import winreg

scratch_name = f"WipeHarness-{uuid.uuid4().hex}"
scratch = winreg.CreateKey(winreg.HKEY_CURRENT_USER, scratch_name)
winreg.SetValueEx(scratch, "marker", 0, winreg.REG_SZ, "override")
override = ctypes.windll.advapi32.RegOverridePredefKey(
    ctypes.c_void_p(int(winreg.HKEY_CURRENT_USER)), ctypes.c_void_p(int(scratch)))
try:
    if override != 0:
        failures.append(f"RegOverridePredefKey failed: rc={override}")
    else:
        try:
            active = winreg.QueryValueEx(winreg.HKEY_CURRENT_USER, "marker")[0]
        except OSError:
            active = None
        if active != "override":
            failures.append("HKCU override not active: marker not visible")
        else:
            check("absent lxss key is empty inventory", wipe.registered_wsl_distros(), {})
            lxss_distro = winreg.CreateKey(
                winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Lxss\{proof}")
            winreg.SetValueEx(lxss_distro, "DistributionName", 0, winreg.REG_SZ, "ProofDistro")
            lxss_distro.Close()
            try:
                wipe.registered_wsl_distros()
                failures.append("broken lxss registration did not raise InventoryError")
            except wipe.InventoryError:
                check("broken lxss registration raises", "InventoryError", "InventoryError")
finally:
    restore = ctypes.windll.advapi32.RegOverridePredefKey(
        ctypes.c_void_p(int(winreg.HKEY_CURRENT_USER)), None)
    if restore != 0:
        failures.append(f"HKCU override restore failed: rc={restore}")
    else:
        try:
            winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                           r"Software\Microsoft\Windows\CurrentVersion\Lxss").Close()
        except FileNotFoundError:
            failures.append("real HKCU Lxss not visible after restore")
        else:
            check("real HKCU visible after restore", True, True)
    scratch.Close()
    # DeleteKey refuses keys with children and the override created the proof
    # Lxss tree under the scratch key, so delete depth-first.
    wipe.delete_key_tree(scratch_name)

print(json.dumps({"checks": checks, "failures": failures}))
sys.exit(1 if failures else 0)
'@

$root = Join-Path $RepoRoot "TestResults\wipe-state-fixture-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $driverPath = Join-Path $root 'driver.py'
    Set-Content -LiteralPath $driverPath -Value $driver -Encoding UTF8
    $output = & python $driverPath $script $root 2>&1
    $exit = $LASTEXITCODE
    $text = ($output | Out-String).Trim()
    if ($exit -ne 0) {
        Write-Host $text
        throw "wipe state regression checks failed (exit $exit)"
    }
    $summary = $text | ConvertFrom-Json
    Write-Host "wipe state logic checks passed: $($summary.checks) checks, 0 failures"
    if ($summary.checks -lt 30) { throw "expected at least 30 checks, ran $($summary.checks)" }
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
