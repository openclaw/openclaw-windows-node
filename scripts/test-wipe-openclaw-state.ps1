# Regression coverage for scripts/wipe_openclaw_state.py pure selection and preservation logic.
# Reviews of PR #1734 flagged zero tests for this script; each case below protects one
# concrete safety contract: which packages count as the native Gateway, how the
# dev-patch alias is derived, which processes/paths deletion owns, which WSL storage
# blocks a wipe, and which model files survive removal.
[CmdletBinding()]
param([string]$RepoRoot = $(if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { (Get-Location).Path }))
$ErrorActionPreference = 'Stop'
$script = Join-Path $RepoRoot 'scripts\wipe_openclaw_state.py'
if (-not (Test-Path -LiteralPath $script)) { throw "wipe script missing: $script" }

$driver = @'
import importlib.util, json, os, sys, tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("wipe", sys.argv[1])
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
created_alias = None
try:
    created_alias = alias_dir / patched_pkg.family / "clawctl-beta1.exe"
    created_alias.parent.mkdir(parents=True, exist_ok=True)
    created_alias.touch()
    check("patched alias derived from package name", wipe.clawctl_path(patched_pkg), created_alias)
finally:
    if created_alias is not None and created_alias.exists():
        created_alias.unlink()
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
check("no conflict without WSL", wipe.wsl_storage_conflicts(targets, None), [])

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

# PID identity: only exact path+creation-time match authorizes termination.
class Proc:
    pass
proc = Proc()
proc.pid = 1234
proc.name = "OpenClaw.Tray.WinUI.exe"
proc.path = r"C:\Users\pedro\AppData\Local\Programs\OpenClaw\OpenClaw.Tray.WinUI.exe"
proc.created = "20261022120000.000000+000"
original = wipe.run_ps
try:
    wipe.run_ps = lambda script: [f"{proc.path}|{proc.created}"]
    check("identical identity accepted", wipe.process_identity_unchanged(proc), True)
    wipe.run_ps = lambda script: [f"{proc.path}|20261022090500.000000+000"]
    check("reused PID creation time rejected", wipe.process_identity_unchanged(proc), False)
    wipe.run_ps = lambda script: [rf"{proc.path}\other.exe|{proc.created}"]
    check("reused PID path rejected", wipe.process_identity_unchanged(proc), False)
    wipe.run_ps = lambda script: []
    check("exited PID rejected", wipe.process_identity_unchanged(proc), False)
finally:
    wipe.run_ps = original

# Model preservation: remove_path keeps *.gguf files and their parent directories.
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
created_session = None
try:
    session.parent.mkdir(parents=True, exist_ok=True)
    session.write_text(json.dumps({"agentUserSid": "S-1-5-21-1-2-3-4"}), encoding="utf-8")
    created_session = session
    orphans = wipe.find_orphan_native_sessions([])  # no installed packages: session is orphaned
    check("dev identity orphan detected", any(str(o).startswith(str(packages_root / dev_family)) for o in orphans), True)
    installed = [P()]
    installed[0].name = "OpenClaw.Gateway"
    installed[0].family = dev_family
    check("installed dev package not orphan", all(str(o) != str(session) for o in wipe.find_orphan_native_sessions(installed)), True)
finally:
    if created_session is not None and created_session.exists():
        created_session.unlink()
    try:
        (packages_root / dev_family / "LocalState" / "OpenClawGatewayMSIX").rmdir()
        (packages_root / dev_family / "LocalState").rmdir()
        (packages_root / dev_family).rmdir()
    except OSError:
        pass

print(json.dumps({"checks": checks, "failures": failures}))
sys.exit(1 if failures else 0)
'@

$root = Join-Path $RepoRoot "TestResults\wipe-state-fixture-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $driverPath = Join-Path $root 'driver.py'
    Set-Content -LiteralPath $driverPath -Value $driver -Encoding UTF8
    $output = & python $driverPath $script 2>&1
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
