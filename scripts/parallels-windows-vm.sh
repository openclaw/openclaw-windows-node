#!/usr/bin/env bash
set -euo pipefail

VM_NAME="Windows 11"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TODAY="$(date +%F)"
CLEAN_SNAPSHOT="windows-11-clean-os-${TODAY}"
BASELINE_SNAPSHOT="pre-openclaw-native-e2e-${TODAY}"
GUEST_PROFILE=""
GUEST_REPO=""
GUEST_PROFILE_PS=""
GUEST_REPO_PS=""
GUEST_ARCH=""
WINGET_EXPECTED_HASH=""
REPO_URL="https://github.com/openclaw/openclaw-windows-node.git"
REPO_REF=""
SNAPSHOT=""
SKIP_RESTORE=0
SECURE_STAGE_DIR="C:/ProgramData/OpenClawPrerequisiteInstallers"
COMMAND="${1:-help}"
if [[ $# -gt 0 ]]; then
  shift
fi

say() {
  printf '[parallels-windows] %s\n' "$*"
}

die() {
  printf '[parallels-windows] error: %s\n' "$*" >&2
  exit 1
}

usage() {
  cat <<'EOF'
Usage: scripts/parallels-windows-vm.sh <command> [options]

Commands:
  inventory   List the VM, hardware facts, and snapshots.
  prepare     Create a clean snapshot, provision the reusable baseline, and snapshot it.
  verify      Verify prerequisites and prove the guest contains no OpenClaw product state.
  restore     Restore a snapshot by exact name or id.
  run-tests   Restore the baseline, optionally check out a ref, and run required validation.

Options:
  --vm <name>                 Parallels VM name. Default: Windows 11
  --clean-snapshot <name>     Clean-OS snapshot name.
  --baseline-snapshot <name>  Reusable E2E snapshot name.
  --snapshot <name-or-id>     Snapshot for restore/run-tests.
  --guest-repo <path>         Guest checkout path; defaults under the detected user's profile.
  --repo-url <url>            Windows app repository URL.
  --ref <git-ref>             Fetch and detach at this ref before run-tests.
  --no-restore                Run tests in the current guest checkout without restoring first.
  -h, --help                  Show this help.

prepare assumes Parallels Desktop is installed/activated and a Windows 11 VM already exists.
It installs only reusable prerequisites: WSL platform/package, Git, Node/npm, .NET 10,
Windows SDK 10.0.26100, WebView2, and a clean developer checkout. It refuses to create the
baseline when the guest contains an OpenClaw CLI, app package, process, tray state, or WSL distro.

run-tests is destructive to post-snapshot guest changes because it restores the selected snapshot.
Use --no-restore only after deliberately syncing an unpushed local ref into the guest.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --vm)
      VM_NAME="${2:?missing value for --vm}"
      shift 2
      ;;
    --clean-snapshot)
      CLEAN_SNAPSHOT="${2:?missing value for --clean-snapshot}"
      shift 2
      ;;
    --baseline-snapshot)
      BASELINE_SNAPSHOT="${2:?missing value for --baseline-snapshot}"
      shift 2
      ;;
    --snapshot)
      SNAPSHOT="${2:?missing value for --snapshot}"
      shift 2
      ;;
    --guest-repo)
      GUEST_REPO="${2:?missing value for --guest-repo}"
      shift 2
      ;;
    --repo-url)
      REPO_URL="${2:?missing value for --repo-url}"
      shift 2
      ;;
    --ref)
      REPO_REF="${2:?missing value for --ref}"
      shift 2
      ;;
    --no-restore)
      SKIP_RESTORE=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      die "unknown option: $1"
      ;;
  esac
done

require_host_tools() {
  local tool
  for tool in prlctl curl python3 ruby; do
    command -v "$tool" >/dev/null 2>&1 || die "missing host tool: $tool"
  done
}

vm_exists() {
  prlctl status "$VM_NAME" >/dev/null 2>&1
}

vm_state() {
  prlctl status "$VM_NAME" 2>/dev/null | awk '{print $NF}'
}

ensure_vm_running() {
  local state
  state="$(vm_state)"
  case "$state" in
    running)
      ;;
    suspended|paused)
      say "Resuming VM: $VM_NAME"
      run_bounded 120 prlctl resume "$VM_NAME" >/dev/null || die "could not resume VM within 120 seconds"
      ;;
    stopped)
      say "Starting VM: $VM_NAME"
      run_bounded 120 prlctl start "$VM_NAME" >/dev/null || die "could not start VM within 120 seconds"
      ;;
    *)
      die "unsupported VM state for $VM_NAME: ${state:-unknown}"
      ;;
  esac
}

guest_user_cmd() {
  prlctl exec "$VM_NAME" --current-user cmd.exe /d /s /c "$1"
}

guest_user_ps() {
  prlctl exec "$VM_NAME" --current-user powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$1"
}

guest_system_ps() {
  prlctl exec "$VM_NAME" powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$1"
}

run_bounded() {
  local timeout_seconds="$1"
  shift
  python3 - "$timeout_seconds" "$@" <<'PY'
import subprocess
import sys

timeout = float(sys.argv[1])
try:
    completed = subprocess.run(sys.argv[2:], timeout=timeout)
except subprocess.TimeoutExpired:
    raise SystemExit(124)
raise SystemExit(completed.returncode)
PY
}

run_windows_installer() {
  local exit_code=0
  "$@" || exit_code=$?
  # Windows success-with-reboot codes cross the POSIX boundary modulo 256.
  # Accept 1641/3010 only for explicit DISM/installer calls; preserve all other failures.
  case "$exit_code" in
    0|105|194) return 0 ;;
    *) return "$exit_code" ;;
  esac
}

powershell_literal_content() {
  python3 -c 'import sys; print(sys.argv[1].replace("\x27", "\x27\x27"))' "$1"
}

reset_secure_stage_dir() {
  guest_system_ps "
    \$stageDir = '${SECURE_STAGE_DIR}'
    if (Test-Path -LiteralPath \$stageDir) {
      \$item = Get-Item -LiteralPath \$stageDir -Force
      if (\$item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing reparse-point installer staging directory' }
      Remove-Item -LiteralPath \$stageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path \$stageDir | Out-Null
    & icacls.exe \$stageDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if (\$LASTEXITCODE -ne 0) { throw 'Could not protect installer staging directory' }
    \$allowed = @('S-1-5-18', 'S-1-5-32-544')
    \$unexpected = (Get-Acl -LiteralPath \$stageDir).Access | Where-Object { \$allowed -notcontains \$_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
    if (\$unexpected) { throw 'Installer staging directory contains an unexpected access rule' }
  "
}

guest_user_ps_bounded() {
  local timeout_seconds="$1"
  shift
  run_bounded "$timeout_seconds" prlctl exec "$VM_NAME" --current-user powershell.exe \
    -NoProfile -ExecutionPolicy Bypass -Command "$1"
}

guest_user_cmd_bounded() {
  local timeout_seconds="$1"
  shift
  run_bounded "$timeout_seconds" prlctl exec "$VM_NAME" --current-user cmd.exe /d /s /c "$1"
}

wait_for_guest() {
  local attempt
  for attempt in $(seq 1 20); do
    if guest_user_cmd_bounded 10 'echo ready' >/dev/null 2>&1; then
      return 0
    fi
    sleep 3
  done
  die "desktop user did not become available in $VM_NAME within about 260 seconds"
}

restart_guest() {
  say "Restarting VM"
  run_bounded 180 prlctl restart "$VM_NAME" >/dev/null || die "could not restart VM within 180 seconds"
  wait_for_guest
}

set_guest_paths() {
  if [[ -z "$GUEST_PROFILE" ]]; then
    GUEST_PROFILE="$(guest_user_cmd 'echo %USERPROFILE%' | tr -d '\r' | tail -n 1)"
    GUEST_PROFILE="${GUEST_PROFILE//\\//}"
  fi
  [[ -n "$GUEST_PROFILE" ]] || die "could not detect the current Windows profile"
  if [[ -z "$GUEST_REPO" ]]; then
    GUEST_REPO="${GUEST_PROFILE}/github/openclaw-windows-node"
  fi
  if [[ -z "$GUEST_ARCH" ]]; then
    GUEST_ARCH="$(guest_user_cmd 'echo %PROCESSOR_ARCHITECTURE%' | tr -d '\r' | tail -n 1 | tr '[:upper:]' '[:lower:]')"
    [[ "$GUEST_ARCH" == "amd64" ]] && GUEST_ARCH="x64"
  fi
  GUEST_PROFILE_PS="$(powershell_literal_content "$GUEST_PROFILE")"
  GUEST_REPO_PS="$(powershell_literal_content "$GUEST_REPO")"
}

snapshot_json() {
  prlctl snapshot-list "$VM_NAME" --json 2>/dev/null || true
}

snapshot_id() {
  local selector="$1"
  snapshot_json | python3 -c '
import json, sys
requested = sys.argv[1]
selector = requested.strip("{}")
raw = sys.stdin.read().strip()
data = json.loads(raw) if raw else {}
for snapshot_id, item in data.items():
    if snapshot_id.strip("{}") == selector or item.get("name") == requested:
        print(snapshot_id)
        raise SystemExit(0)
prefixes = {"clean": "windows-11-clean-os-", "e2e": "pre-openclaw-native-e2e-"}
prefix = prefixes.get(requested.lower())
if prefix:
    matches = [
        (item.get("date", ""), snapshot_id)
        for snapshot_id, item in data.items()
        if item.get("name", "").startswith(prefix)
    ]
    if matches:
        print(max(matches)[1])
        raise SystemExit(0)
raise SystemExit(1)
' "$selector"
}

snapshot_exists() {
  snapshot_id "$1" >/dev/null 2>&1
}

create_snapshot() {
  local name="$1"
  local description="$2"
  if snapshot_exists "$name"; then
    say "Snapshot already exists: $name"
    return
  fi
  say "Creating snapshot: $name"
  run_bounded 900 prlctl snapshot "$VM_NAME" --name "$name" --description "$description" || die "snapshot creation exceeded 15 minutes"
}

create_clean_snapshot_if_raw() {
  if snapshot_exists "$CLEAN_SNAPSHOT"; then
    say "Snapshot already exists: $CLEAN_SNAPSHOT"
    return
  fi
  if guest_user_cmd 'where git.exe >nul 2>nul || where node.exe >nul 2>nul || where dotnet.exe >nul 2>nul || wsl.exe --version >nul 2>nul' >/dev/null 2>&1; then
    say "Skipping clean-OS snapshot because reusable prerequisites are already installed"
    return
  fi
  create_snapshot "$CLEAN_SNAPSHOT" "Clean Windows baseline before OpenClaw development prerequisites."
}

restore_snapshot() {
  local selector="$1"
  local id
  id="$(snapshot_id "$selector")" || die "snapshot not found: $selector"
  say "Restoring snapshot: $selector ($id)"
  run_bounded 900 prlctl snapshot-switch "$VM_NAME" --id "$id" || die "snapshot restore exceeded 15 minutes"
  ensure_vm_running
  wait_for_guest
}

inventory() {
  prlctl list -a
  printf '\n'
  prlctl list -i "$VM_NAME" | grep -E '^(Name|State|OS|GuestTools|  Nested virtualization|  cpu |  memory )' || true
  printf '\nSnapshots:\n'
  prlctl snapshot-list "$VM_NAME" --tree
  printf '\n'
  snapshot_json
}

clean_state_script() {
  cat <<'PS'
$dirty = [System.Collections.Generic.List[string]]::new()
if (Get-Command openclaw.cmd -ErrorAction SilentlyContinue) { $dirty.Add('openclaw.cmd on PATH') }
if (Test-Path (Join-Path $env:APPDATA 'OpenClawTray')) { $dirty.Add('OpenClawTray AppData exists') }
if (Test-Path (Join-Path $env:APPDATA 'OpenClawTray-Dev')) { $dirty.Add('OpenClawTray-Dev AppData exists') }
if (Test-Path (Join-Path $env:LOCALAPPDATA 'OpenClawTray')) { $dirty.Add('OpenClaw Companion install/state directory exists') }
if (Test-Path (Join-Path $env:LOCALAPPDATA 'OpenClawTray-Dev')) { $dirty.Add('OpenClaw Companion dev install/state directory exists') }
if (Get-AppxPackage -Name '*OpenClaw*' -ErrorAction SilentlyContinue) { $dirty.Add('OpenClaw app package installed') }
if (Get-Process -Name '*OpenClaw*' -ErrorAction SilentlyContinue) { $dirty.Add('OpenClaw process running') }
$uninstallRoots = @(
  'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*',
  'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*',
  'HKLM:/Software/WOW6432Node/Microsoft/Windows/CurrentVersion/Uninstall/*'
)
if (Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'OpenClaw Companion*' }) {
  $dirty.Add('OpenClaw Companion uninstall registration exists')
}
$distros = @(wsl.exe -l -q 2>$null | Where-Object { $_.Trim() })
if ($distros.Count -gt 0) { $dirty.Add('WSL distro exists: ' + ($distros -join ', ')) }
if ($dirty.Count -gt 0) {
  $dirty | ForEach-Object { Write-Error $_ }
  exit 1
}
Write-Host 'clean product state: yes'
PS
}

assert_clean_product_state() {
  guest_user_ps "$(clean_state_script)"
}

pending_reboot_script() {
  cat <<'PS'
$pending = @(
  (Test-Path 'HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Component Based Servicing/RebootPending'),
  (Test-Path 'HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/WindowsUpdate/Auto Update/RebootRequired'),
  [bool](Get-ItemProperty 'HKLM:/SYSTEM/CurrentControlSet/Control/Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue)
)
if ($pending -contains $true) { Write-Error 'Windows reports a pending reboot'; exit 1 }
Write-Host 'pending reboot: no'
PS
}

assert_no_pending_reboot() {
  guest_system_ps "$(pending_reboot_script)"
}

feature_state() {
  guest_system_ps "(Get-WindowsOptionalFeature -Online -FeatureName '$1').State" | tr -d '\r' | tail -n 1
}

ensure_wsl_features() {
  local changed=0
  if [[ "$(feature_state Microsoft-Windows-Subsystem-Linux)" != "Enabled" ]]; then
    say "Enabling Microsoft-Windows-Subsystem-Linux"
    run_windows_installer prlctl exec "$VM_NAME" dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart
    changed=1
  fi
  if [[ "$(feature_state VirtualMachinePlatform)" != "Enabled" ]]; then
    say "Enabling VirtualMachinePlatform"
    run_windows_installer prlctl exec "$VM_NAME" dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart
    changed=1
  fi
  if [[ "$changed" == "1" ]]; then
    restart_guest
  fi
}

resolve_wsl_msi_url() {
  local arch="$1"
  curl -fsSL https://api.github.com/repos/microsoft/WSL/releases/latest | python3 -c '
import json, re, sys
arch = sys.argv[1].lower()
data = json.load(sys.stdin)
pattern = re.compile(rf"^wsl\..*\.{re.escape(arch)}\.msi$", re.I)
for asset in data.get("assets", []):
    if pattern.match(asset.get("name", "")):
        print(asset["browser_download_url"])
        raise SystemExit(0)
raise SystemExit("No matching signed WSL MSI asset found")
' "$arch"
}

ensure_wsl_package() {
  if guest_user_cmd 'wsl.exe --version' >/dev/null 2>&1; then
    guest_user_cmd 'wsl.exe --set-default-version 2' >/dev/null
    return
  fi
  local guest_arch asset_arch url signature wsl_msi
  guest_arch="$(guest_user_cmd 'echo %PROCESSOR_ARCHITECTURE%' | tr -d '\r' | tail -n 1)"
  case "$(printf '%s' "$guest_arch" | tr '[:lower:]' '[:upper:]')" in
    ARM64) asset_arch="arm64" ;;
    AMD64) asset_arch="x64" ;;
    *) die "unsupported Windows architecture for WSL package: $guest_arch" ;;
  esac
  url="$(resolve_wsl_msi_url "$asset_arch")"
  say "Installing signed Microsoft WSL package for $guest_arch"
  wsl_msi="${SECURE_STAGE_DIR}/WSL.msi"
  reset_secure_stage_dir
  prlctl exec "$VM_NAME" curl.exe -fL --connect-timeout 20 --max-time 600 "$url" -o "$wsl_msi"
  signature="$(guest_system_ps "\$signature = Get-AuthenticodeSignature '${wsl_msi}'; if (\$signature.Status -eq 'Valid' -and \$signature.SignerCertificate.Subject -match 'Microsoft Corporation') { 'Valid' } else { \$signature.Status.ToString() + ': ' + \$signature.SignerCertificate.Subject }" | tr -d '\r' | tail -n 1)"
  [[ "$signature" == "Valid" ]] || die "WSL MSI signature was not valid Microsoft code: $signature"
  run_windows_installer prlctl exec "$VM_NAME" msiexec.exe /i 'C:\ProgramData\OpenClawPrerequisiteInstallers\WSL.msi' /qn /norestart '/L*v' 'C:\Windows\Temp\openclaw-wsl-install.log'
  guest_user_cmd 'wsl.exe --version' >/dev/null || {
    guest_system_ps "Get-Content 'C:/Windows/Temp/openclaw-wsl-install.log' -Tail 80" >&2 || true
    die "WSL package install did not produce a working wsl.exe"
  }
  guest_user_cmd 'wsl.exe --set-default-version 2' >/dev/null
  guest_system_ps "Remove-Item -LiteralPath '${wsl_msi}','C:/Windows/Temp/openclaw-wsl-install.log' -Force -ErrorAction SilentlyContinue"
}

resolve_winget_manifest() {
  local package_id="$1"
  local package_path versions_json version version_json installer_url
  package_path="$(python3 -c 'import sys; package=sys.argv[1]; print(package[0].lower() + "/" + package.replace(".", "/"))' "$package_id")"
  versions_json="$(curl -fsSL "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/${package_path}")"
  version="$(python3 -c '
import json, re, sys
items = json.load(sys.stdin)
versions = [item["name"] for item in items if item.get("type") == "dir"]
def key(value):
    return tuple((0, int(part)) if part.isdigit() else (1, part.lower()) for part in re.split(r"[._+-]", value))
print(max(versions, key=key))
' <<<"$versions_json")"
  version_json="$(curl -fsSL "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/${package_path}/${version}")"
  installer_url="$(python3 -c '
import json, sys
for item in json.load(sys.stdin):
    if item.get("name", "").endswith(".installer.yaml"):
        print(item["download_url"])
        raise SystemExit(0)
raise SystemExit("installer manifest not found")
' <<<"$version_json")"
  curl -fsSL "$installer_url" | ruby -ryaml -rdate -e '
manifest = YAML.safe_load(STDIN.read, permitted_classes: [Date], aliases: false)
arch = ARGV[0]
installers = manifest.fetch("Installers")
preferred = installers.select { |item| item["Architecture"] == arch && [nil, "machine"].include?(item["Scope"]) }
fallback_arches = arch == "arm64" ? ["arm64", "neutral", "x64", "x86"] : [arch, "neutral", "x86"]
preferred = installers.select { |item| fallback_arches.include?(item["Architecture"]) } if preferred.empty?
installer = preferred.find { |item| item["Scope"] == "machine" } || preferred.first
abort "matching machine installer not found" unless installer
puts [manifest.fetch("PackageVersion"), installer.fetch("InstallerSha256")].join("|")
' "$GUEST_ARCH"
}

winget_download() {
  local package_id="$1"
  local manifest_fact version
  manifest_fact="$(resolve_winget_manifest "$package_id")"
  version="${manifest_fact%%|*}"
  WINGET_EXPECTED_HASH="${manifest_fact#*|}"
  local download_dir="${GUEST_PROFILE//\//\\}\\Downloads\\OpenClawPrereqs"
  guest_user_ps "Remove-Item -LiteralPath '${GUEST_PROFILE_PS}/Downloads/OpenClawPrereqs' -Recurse -Force -ErrorAction SilentlyContinue"
  guest_user_cmd "if not exist \"${download_dir}\" mkdir \"${download_dir}\" & winget.exe download --id ${package_id} -e --version \"${version}\" --scope machine --download-directory \"${download_dir}\" --accept-source-agreements --accept-package-agreements --disable-interactivity"
}

downloaded_installer() {
  local pattern="$1"
  guest_user_ps "Get-ChildItem -LiteralPath '${GUEST_PROFILE_PS}/Downloads/OpenClawPrereqs' -File | Where-Object { \$_.Name -like '${pattern}' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName" | tr -d '\r' | tail -n 1
}

stage_installer() {
  local source_path="$1"
  local package_name="$2"
  local signer_pattern="$3"
  local expected_hash="$4"
  local source_base64
  source_base64="$(python3 -c 'import base64, sys; print(base64.b64encode(sys.argv[1].encode()).decode())' "$source_path")"
  reset_secure_stage_dir
  # Winget verifies its manifest hash, but the download directory stays user-writable. Recheck the
  # expected signer and hash after copying into an ACL-restricted directory before SYSTEM execution.
  guest_system_ps "
    \$source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${source_base64}'))
    \$stageDir = '${SECURE_STAGE_DIR}'
    \$sourceSignature = Get-AuthenticodeSignature -LiteralPath \$source
    if (\$sourceSignature.Status -ne 'Valid' -or \$sourceSignature.SignerCertificate.Subject -notmatch '${signer_pattern}') {
      throw 'Unexpected or invalid Authenticode signer for ${package_name}: ' + \$sourceSignature.SignerCertificate.Subject
    }
    \$sourceHash = (Get-FileHash -LiteralPath \$source -Algorithm SHA256).Hash
    if (\$sourceHash -ne '${expected_hash}') { throw 'WinGet manifest hash mismatch for ${package_name}' }
    \$destination = Join-Path \$stageDir ('${package_name}' + [IO.Path]::GetExtension(\$source))
    Copy-Item -LiteralPath \$source -Destination \$destination -Force
    \$destinationHash = (Get-FileHash -LiteralPath \$destination -Algorithm SHA256).Hash
    \$destinationSignature = Get-AuthenticodeSignature -LiteralPath \$destination
    if (\$sourceHash -ne \$destinationHash -or \$destinationSignature.Status -ne 'Valid' -or \$destinationSignature.SignerCertificate.Subject -notmatch '${signer_pattern}') {
      Remove-Item -LiteralPath \$destination -Force -ErrorAction SilentlyContinue
      throw 'Staged installer verification failed for ${package_name}'
    }
    Write-Output \$destination
  " | tr -d '\r' | tail -n 1
}

wait_for_check() {
  local label="$1"
  local command="$2"
  local attempt
  for attempt in $(seq 1 120); do
    if guest_user_cmd "$command" >/dev/null 2>&1; then
      say "$label ready"
      return
    fi
    sleep 3
  done
  die "$label did not become ready within 360 seconds"
}

ensure_git() {
  if guest_user_cmd 'where git.exe' >/dev/null 2>&1; then
    return
  fi
  winget_download Git.Git
  local installer
  installer="$(downloaded_installer 'Git_*_inno_*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the Git installer"
  installer="$(stage_installer "$installer" Git 'Johannes Schindelin|Open Source Developer|Git for Windows' "$WINGET_EXPECTED_HASH")"
  say "Installing Git"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /VERYSILENT /NORESTART /SP- /ALLUSERS
  wait_for_check Git 'where git.exe'
}

ensure_node() {
  if guest_user_cmd 'where node.exe' >/dev/null 2>&1; then
    return
  fi
  winget_download OpenJS.NodeJS.LTS
  local installer
  installer="$(downloaded_installer 'Node.js*Machine*.msi')"
  [[ -n "$installer" ]] || die "winget did not download the Node.js installer"
  installer="$(stage_installer "$installer" NodeJS 'OpenJS Foundation' "$WINGET_EXPECTED_HASH")"
  say "Installing Node.js LTS"
  run_windows_installer prlctl exec "$VM_NAME" msiexec.exe /i "$installer" /qn /norestart
  wait_for_check Node.js 'where node.exe'
}

ensure_dotnet() {
  if guest_user_cmd 'dotnet.exe --list-sdks | findstr /B 10.' >/dev/null 2>&1; then
    return
  fi
  winget_download Microsoft.DotNet.SDK.10
  local installer
  installer="$(downloaded_installer 'Microsoft .NET SDK 10.0*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the .NET SDK installer"
  installer="$(stage_installer "$installer" DotNetSDK 'Microsoft Corporation' "$WINGET_EXPECTED_HASH")"
  say "Installing .NET 10 SDK"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /install /quiet /norestart
  wait_for_check '.NET 10 SDK' 'dotnet.exe --list-sdks | findstr /B 10.'
}

ensure_windows_sdk() {
  if guest_user_ps "Test-Path 'C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um'" | grep -q True; then
    return
  fi
  winget_download Microsoft.WindowsSDK.10.0.26100
  local installer
  installer="$(downloaded_installer 'Windows Software Development Kit*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the Windows SDK installer"
  installer="$(stage_installer "$installer" WindowsSDK 'Microsoft Corporation' "$WINGET_EXPECTED_HASH")"
  say "Installing Windows SDK 10.0.26100"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /quiet /norestart /ceip off
  wait_for_check 'Windows SDK' 'if exist "C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um" exit /b 0 else exit /b 1'
}

ensure_webview2() {
  if guest_system_ps "Test-Path 'HKLM:/SOFTWARE/WOW6432Node/Microsoft/EdgeUpdate/Clients/{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'" | grep -q True; then
    return
  fi
  winget_download Microsoft.EdgeWebView2Runtime
  local installer
  installer="$(downloaded_installer '*WebView2*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the WebView2 installer"
  installer="$(stage_installer "$installer" WebView2 'Microsoft Corporation' "$WINGET_EXPECTED_HASH")"
  say "Installing WebView2 Runtime"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /silent /install
  wait_for_check WebView2 'reg.exe query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv'
}

cleanup_installers() {
  guest_user_ps "Remove-Item -LiteralPath '${GUEST_PROFILE_PS}/Downloads/OpenClawPrereqs' -Recurse -Force -ErrorAction SilentlyContinue"
  guest_system_ps "Remove-Item -LiteralPath '${SECURE_STAGE_DIR}' -Recurse -Force -ErrorAction SilentlyContinue"
}

ensure_guest_checkout() {
  if ! guest_user_ps "Test-Path '${GUEST_REPO_PS}/.git'" | grep -q True; then
    say "Cloning Windows app repository"
    guest_user_cmd "git.exe clone ${REPO_URL} \"${GUEST_REPO//\//\\}\""
  fi
  say "Running repo-native prerequisite check"
  guest_user_ps "Set-Location '${GUEST_REPO_PS}'; & './scripts/setup-dev.ps1' -CheckOnly"
}

verify_baseline() {
  set_guest_paths
  guest_user_cmd 'git --version & node --version & npm --version & dotnet --list-sdks'
  guest_user_cmd 'wsl.exe --version' || die "MSI-backed WSL is unavailable"
  guest_user_cmd 'wsl.exe --status' || die "WSL status failed"
  local wsl_default
  wsl_default="$(guest_user_ps "Get-ItemPropertyValue 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Lxss' -Name DefaultVersion -ErrorAction SilentlyContinue" | tr -d '\r' | tail -n 1)"
  [[ "$wsl_default" == "2" ]] || die "WSL default version is ${wsl_default:-unset}, expected 2"
  guest_user_ps "Get-ChildItem -Name 'C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0'"
  assert_clean_product_state
  assert_no_pending_reboot
  if guest_user_ps "Test-Path '${GUEST_REPO_PS}/scripts/setup-dev.ps1'" | grep -q True; then
    guest_user_ps "Set-Location '${GUEST_REPO_PS}'; & './scripts/setup-dev.ps1' -CheckOnly"
  else
    die "guest checkout missing: $GUEST_REPO"
  fi
}

prepare() {
  ensure_vm_running
  wait_for_guest
  inventory
  set_guest_paths
  if snapshot_exists "$BASELINE_SNAPSHOT"; then
    say "Reusable baseline already exists: $BASELINE_SNAPSHOT"
    return
  fi
  assert_clean_product_state
  create_clean_snapshot_if_raw
  ensure_wsl_features
  ensure_wsl_package
  ensure_git
  ensure_node
  ensure_dotnet
  ensure_windows_sdk
  ensure_webview2
  cleanup_installers
  ensure_guest_checkout
  restart_guest
  guest_user_cmd 'wsl.exe --set-default-version 2' >/dev/null
  verify_baseline
  create_snapshot "$BASELINE_SNAPSHOT" "E2E-ready Windows baseline with WSL platform, Git, Node/npm, .NET 10, Windows SDK, WebView2, and no OpenClaw product state."
  say "Baseline ready: $BASELINE_SNAPSHOT"
}

run_tests() {
  local selector="${SNAPSHOT:-e2e}"
  if [[ "$SKIP_RESTORE" == "0" ]]; then
    restore_snapshot "$selector"
  else
    say "Skipping snapshot restore; validating the current guest checkout"
    ensure_vm_running
    wait_for_guest
  fi
  set_guest_paths
  if [[ -n "$REPO_REF" ]]; then
    local fetch_ref="$REPO_REF"
    fetch_ref="${fetch_ref#origin/}"
    say "Fetching guest ref: $REPO_REF"
    guest_user_cmd_bounded 600 "cd /d \"${GUEST_REPO//\//\\}\" && git fetch --tags origin \"${fetch_ref}\" && git checkout --detach FETCH_HEAD" || die "guest ref fetch exceeded 10 minutes"
  fi
  local host_helper="${SCRIPT_DIR}/parallels-run-validation.ps1"
  [[ -f "$host_helper" ]] || die "validation helper missing beside controller: $host_helper"
  local helper_base64
  helper_base64="$(python3 -c 'import base64, pathlib, sys; print(base64.b64encode(pathlib.Path(sys.argv[1]).read_bytes()).decode())' "$host_helper")"
  local run_id helper log_path done_path pid_path helper_ps log_path_ps done_path_ps pid_path_ps start now last_line exit_code
  run_id="$(date +%Y%m%d-%H%M%S)"
  helper="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-parallels-validation-${run_id}.ps1"
  log_path="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-parallels-validation-${run_id}.log"
  done_path="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-parallels-validation-${run_id}.done"
  pid_path="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-parallels-validation-${run_id}.pid"
  helper_ps="$(powershell_literal_content "$helper")"
  log_path_ps="$(powershell_literal_content "$log_path")"
  done_path_ps="$(powershell_literal_content "$done_path")"
  pid_path_ps="$(powershell_literal_content "$pid_path")"
  guest_user_ps_bounded 30 "[IO.File]::WriteAllBytes('${helper_ps}', [Convert]::FromBase64String('${helper_base64}'))" || die "could not materialize the validation helper in the guest"
  say "Launching required Windows validation"
  guest_user_ps_bounded 30 "Remove-Item -LiteralPath '${log_path_ps}','${done_path_ps}','${pid_path_ps}' -Force -ErrorAction SilentlyContinue; Start-Process powershell.exe -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"${helper_ps}\" -RepoRoot \"${GUEST_REPO_PS}\" -LogPath \"${log_path_ps}\" -DonePath \"${done_path_ps}\" -PidPath \"${pid_path_ps}\"' -WindowStyle Hidden" || die "could not launch Windows validation"
  start="$(date +%s)"
  while true; do
    if guest_user_ps_bounded 20 "Test-Path '${done_path_ps}'" 2>/dev/null | grep -q True; then
      break
    fi
    now="$(date +%s)"
    if (( now - start > 5400 )); then
      guest_user_ps_bounded 20 "if (Test-Path '${pid_path_ps}') { \$workerPid = [int](Get-Content '${pid_path_ps}' -Raw); & taskkill.exe /PID \$workerPid /T /F 2>\$null | Out-Null }; if (Test-Path '${log_path_ps}') { Get-Content '${log_path_ps}' -Tail 120 }" || true
      die "Windows validation exceeded 90 minutes"
    fi
    last_line="$(guest_user_ps_bounded 20 "if (Test-Path '${log_path_ps}') { Get-Content '${log_path_ps}' -Tail 1 } else { 'waiting for first log line' }" 2>/dev/null | tr -d '\r' | tail -n 1)" || last_line="guest transport unavailable; retrying"
    say "validation running: ${last_line}"
    sleep 10
  done
  exit_code="$(guest_user_ps_bounded 20 "Get-Content '${done_path_ps}' -Raw" | tr -d '\r\n ')" || die "could not read Windows validation result"
  guest_user_ps_bounded 20 "Get-Content '${log_path_ps}' -Tail 160"
  [[ "$exit_code" == "0" ]] || die "Windows validation failed with exit code ${exit_code}; log: ${log_path}"
  say "Windows validation passed; log: $log_path"
}

case "$COMMAND" in
  help|-h|--help)
    usage
    exit 0
    ;;
  inventory|prepare|verify|restore|run-tests)
    ;;
  *)
    usage >&2
    die "unknown command: $COMMAND"
    ;;
esac

require_host_tools
vm_exists || die "Parallels VM not found: $VM_NAME"

case "$COMMAND" in
  inventory)
    inventory
    ;;
  prepare)
    prepare
    ;;
  verify)
    ensure_vm_running
    wait_for_guest
    verify_baseline
    ;;
  restore)
    [[ -n "$SNAPSHOT" ]] || die "restore requires --snapshot <name-or-id>"
    restore_snapshot "$SNAPSHOT"
    ;;
  run-tests)
    run_tests
    ;;
esac
