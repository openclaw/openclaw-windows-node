#!/usr/bin/env bash
set -euo pipefail

WINDOWS_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WINDOWS_ROOT="$(cd "$WINDOWS_SCRIPT_DIR/.." && pwd)"
OPENCLAW_REPO="${OPENCLAW_REPO:-$WINDOWS_ROOT/../openclaw}"
BASE_CONTROLLER="$OPENCLAW_REPO/scripts/e2e/parallels-windows-prepare.sh"

COMMAND="${1:-help}"
[[ $# -gt 0 ]] && shift
VM="Windows 11"
TODAY="$(date +%F)"
APP_SNAPSHOT="pre-openclaw-windows-app-e2e-${TODAY}"
APP_SNAPSHOT_EXPLICIT=0
SELECTED_SNAPSHOT=""
GUEST_CHECKOUT=""
REPO_URL="https://github.com/openclaw/openclaw-windows-node.git"
REPO_REF=""
SKIP_RESTORE=0

usage() {
  cat <<'EOF'
Usage: scripts/parallels-windows-vm.sh <command> [options]

Commands:
  inventory   Delegate base VM and snapshot inventory to the OpenClaw controller.
  prepare     Prepare the OpenClaw base, then add Windows app prerequisites and snapshot them.
  verify      Verify both the OpenClaw base and Windows app development prerequisites.
  restore     Restore `clean`, `e2e`, `app`, an exact snapshot name, or an id.
  run-tests   Restore the app snapshot and run the required Windows build and test suites.

Options:
  --vm <name>                 Parallels VM name. Default: Windows 11
  --app-snapshot <name>       Windows app E2E snapshot name.
  --snapshot <name-or-id>     Snapshot for restore/run-tests.
  --guest-repo <path>         Windows app checkout path in the guest.
  --repo-url <url>            Windows app repository URL.
  --ref <git-ref>             Fetch and detach at this ref before run-tests.
  --no-restore                Test the current deliberately synchronized guest checkout.
  -h, --help                  Show this help.

The general Parallels, WSL, Git/Node, snapshot, and transport implementation lives in the sibling
OpenClaw repo. Set OPENCLAW_REPO when the checkouts are not siblings.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --vm) VM="${2:?missing value for --vm}"; shift 2 ;;
    --app-snapshot) APP_SNAPSHOT="${2:?missing value for --app-snapshot}"; APP_SNAPSHOT_EXPLICIT=1; shift 2 ;;
    --snapshot) SELECTED_SNAPSHOT="${2:?missing value for --snapshot}"; shift 2 ;;
    --guest-repo) GUEST_CHECKOUT="${2:?missing value for --guest-repo}"; shift 2 ;;
    --repo-url) REPO_URL="${2:?missing value for --repo-url}"; shift 2 ;;
    --ref) REPO_REF="${2:?missing value for --ref}"; shift 2 ;;
    --no-restore) SKIP_RESTORE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'error: unknown option: %s\n' "$1" >&2; exit 1 ;;
  esac
done

case "$COMMAND" in
  help|-h|--help) usage; exit 0 ;;
  inventory|prepare|verify|restore|run-tests) ;;
  *) usage >&2; printf 'error: unknown command: %s\n' "$COMMAND" >&2; exit 1 ;;
esac

[[ -f "$BASE_CONTROLLER" ]] || {
  printf 'error: OpenClaw Parallels controller not found: %s\n' "$BASE_CONTROLLER" >&2
  printf 'clone https://github.com/openclaw/openclaw beside this repo or set OPENCLAW_REPO\n' >&2
  exit 1
}

base_args=(--vm "$VM")

case "$COMMAND" in
  inventory)
    exec bash "$BASE_CONTROLLER" inventory "${base_args[@]}"
    ;;
esac

# Source only the shared implementation. This repo owns the app-specific functions below.
APP_COMMAND="$COMMAND"
APP_REPO_URL="$REPO_URL"
APP_REPO_REF="$REPO_REF"
APP_SKIP_RESTORE="$SKIP_RESTORE"
APP_GUEST_CHECKOUT="$GUEST_CHECKOUT"
APP_SNAPSHOT_NAME="$APP_SNAPSHOT"
APP_SNAPSHOT_WAS_EXPLICIT="$APP_SNAPSHOT_EXPLICIT"
set --
OPENCLAW_PARALLELS_WINDOWS_LIBRARY_ONLY=1 source "$BASE_CONTROLLER"
[[ "${OPENCLAW_PARALLELS_WINDOWS_API:-0}" == "1" ]] || {
  printf 'error: incompatible OpenClaw Parallels Windows controller API\n' >&2
  exit 1
}
COMMAND="$APP_COMMAND"
VM_NAME="$VM"
BASELINE_SNAPSHOT="pre-openclaw-native-e2e-${TODAY}"
SNAPSHOT="$SELECTED_SNAPSHOT"
REPO_URL="$APP_REPO_URL"
REPO_REF="$APP_REPO_REF"
SKIP_RESTORE="$APP_SKIP_RESTORE"
GUEST_CHECKOUT="$APP_GUEST_CHECKOUT"
APP_SNAPSHOT="$APP_SNAPSHOT_NAME"
APP_SNAPSHOT_EXPLICIT="$APP_SNAPSHOT_WAS_EXPLICIT"

resolve_app_snapshot_selector() {
  local selector="$1"
  if [[ "$selector" != "app" ]]; then
    printf '%s\n' "$selector"
    return
  fi
  snapshot_json | python3 -c '
import json, sys
raw = sys.stdin.read().strip()
data = json.loads(raw) if raw else {}
matches = [
    (item.get("date", ""), snapshot_id)
    for snapshot_id, item in data.items()
    if item.get("name", "").startswith("pre-openclaw-windows-app-e2e-")
]
if not matches:
    raise SystemExit("Windows app snapshot not found; run prepare first")
print(max(matches)[1])
'
}
require_host_tools
vm_exists || die "Parallels VM not found: $VM_NAME"

set_app_paths() {
  set_guest_paths
  if [[ -z "$GUEST_CHECKOUT" ]]; then
    GUEST_CHECKOUT="${GUEST_PROFILE}/github/openclaw-windows-node"
  fi
  GUEST_REPO_PS="$(powershell_literal_content "$GUEST_CHECKOUT")"
}

ensure_dotnet() {
  guest_user_cmd 'dotnet.exe --list-sdks | findstr /B 10.' >/dev/null 2>&1 && return
  winget_download Microsoft.DotNet.SDK.10
  local installer
  installer="$(downloaded_installer 'Microsoft .NET SDK 10.0*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the .NET SDK installer"
  installer="$(stage_installer "$installer" DotNetSDK 'Microsoft Corporation' "$WINGET_EXPECTED_HASH")"
  say "Installing .NET 10 SDK"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /install /quiet /norestart
  finish_installer_reboot
  wait_for_check '.NET 10 SDK' 'dotnet.exe --list-sdks | findstr /B 10.'
}

ensure_windows_sdk() {
  guest_user_ps "Test-Path 'C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um'" | grep -q True && return
  winget_download Microsoft.WindowsSDK.10.0.26100
  local installer
  installer="$(downloaded_installer 'Windows Software Development Kit*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the Windows SDK installer"
  installer="$(stage_installer "$installer" WindowsSDK 'Microsoft Corporation' "$WINGET_EXPECTED_HASH")"
  say "Installing Windows SDK 10.0.26100"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /quiet /norestart /ceip off
  finish_installer_reboot
  wait_for_check 'Windows SDK' 'if exist "C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um" exit /b 0 else exit /b 1'
}

ensure_webview2() {
  guest_system_ps "Test-Path 'HKLM:/SOFTWARE/WOW6432Node/Microsoft/EdgeUpdate/Clients/{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'" | grep -q True && return
  winget_download Microsoft.EdgeWebView2Runtime
  local installer
  installer="$(downloaded_installer '*WebView2*.exe')"
  [[ -n "$installer" ]] || die "winget did not download the WebView2 installer"
  installer="$(stage_installer "$installer" WebView2 'Microsoft Corporation' "$WINGET_EXPECTED_HASH")"
  say "Installing WebView2 Runtime"
  run_windows_installer prlctl exec "$VM_NAME" "$installer" /silent /install
  finish_installer_reboot
  wait_for_check WebView2 'reg.exe query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv'
}

ensure_guest_checkout() {
  if ! guest_user_ps "Test-Path '${GUEST_REPO_PS}/.git'" | grep -q True; then
    say "Cloning Windows app repository"
    guest_user_cmd "git.exe clone ${REPO_URL} \"${GUEST_CHECKOUT//\//\\}\""
  fi
}

verify_app() {
  set_app_paths
  guest_user_cmd 'dotnet.exe --list-sdks | findstr /B 10.' >/dev/null || die ".NET 10 SDK is unavailable"
  guest_user_ps "Test-Path 'C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um'" | grep -q True || die "Windows SDK 10.0.26100 is unavailable"
  guest_system_ps "Test-Path 'HKLM:/SOFTWARE/WOW6432Node/Microsoft/EdgeUpdate/Clients/{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'" | grep -q True || die "WebView2 is unavailable"
  guest_user_ps "Test-Path '${GUEST_REPO_PS}/scripts/setup-dev.ps1'" | grep -q True || die "guest checkout missing: $GUEST_CHECKOUT"
  guest_user_ps "Set-Location '${GUEST_REPO_PS}'; & './scripts/setup-dev.ps1' -CheckOnly"
}

prepare_app() {
  bash "$BASE_CONTROLLER" prepare "${base_args[@]}"
  if snapshot_exists "$APP_SNAPSHOT"; then
    say "Restoring and verifying existing Windows app baseline: $APP_SNAPSHOT"
    restore_snapshot "$APP_SNAPSHOT"
    verify_baseline
    verify_app
    say "Windows app baseline verified: $APP_SNAPSHOT"
    return
  fi
  bash "$BASE_CONTROLLER" restore "${base_args[@]}" --snapshot e2e
  set_app_paths
  assert_clean_product_state
  ensure_dotnet
  ensure_windows_sdk
  ensure_webview2
  cleanup_installers
  ensure_guest_checkout
  restart_guest
  verify_baseline
  verify_app
  create_snapshot "$APP_SNAPSHOT" "Windows app E2E layer with .NET 10, Windows SDK 10.0.26100, WebView2, and a clean companion checkout."
  say "Windows app baseline ready: $APP_SNAPSHOT"
}

run_app_tests() {
  if [[ "$SKIP_RESTORE" == "0" ]]; then
    local selector
    if [[ -n "$SELECTED_SNAPSHOT" ]]; then
      selector="$SELECTED_SNAPSHOT"
    elif [[ "$APP_SNAPSHOT_EXPLICIT" == "1" ]]; then
      selector="$APP_SNAPSHOT"
    else
      selector="app"
    fi
    restore_snapshot "$(resolve_app_snapshot_selector "$selector")"
  else
    ensure_vm_running
    wait_for_guest
  fi
  set_app_paths
  if [[ -n "$REPO_REF" ]]; then
    local fetch_ref="${REPO_REF#origin/}"
    guest_user_cmd_bounded 600 "cd /d \"${GUEST_CHECKOUT//\//\\}\" && git fetch --tags origin \"${fetch_ref}\" && git checkout --detach FETCH_HEAD" || die "guest ref fetch exceeded 10 minutes"
  fi
  local helper_base64 run_id helper log_path done_path pid_path helper_ps log_path_ps done_path_ps pid_path_ps start now last_line exit_code
  helper_base64="$(python3 -c 'import base64, pathlib, sys; print(base64.b64encode(pathlib.Path(sys.argv[1]).read_bytes()).decode())' "$WINDOWS_SCRIPT_DIR/parallels-run-validation.ps1")"
  run_id="$(date +%Y%m%d-%H%M%S)"
  helper="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-windows-validation-${run_id}.ps1"
  log_path="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-windows-validation-${run_id}.log"
  done_path="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-windows-validation-${run_id}.done"
  pid_path="${GUEST_PROFILE}/AppData/Local/Temp/openclaw-windows-validation-${run_id}.pid"
  helper_ps="$(powershell_literal_content "$helper")"
  log_path_ps="$(powershell_literal_content "$log_path")"
  done_path_ps="$(powershell_literal_content "$done_path")"
  pid_path_ps="$(powershell_literal_content "$pid_path")"
  guest_user_ps_bounded 30 "[IO.File]::WriteAllBytes('${helper_ps}', [Convert]::FromBase64String('${helper_base64}'))" || die "could not materialize validation helper"
  guest_user_ps_bounded 30 "Start-Process powershell.exe -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"${helper_ps}\" -RepoRoot \"${GUEST_REPO_PS}\" -LogPath \"${log_path_ps}\" -DonePath \"${done_path_ps}\" -PidPath \"${pid_path_ps}\"' -WindowStyle Hidden" || die "could not launch validation"
  start="$(date +%s)"
  while ! guest_user_ps_bounded 20 "Test-Path '${done_path_ps}'" 2>/dev/null | grep -q True; do
    now="$(date +%s)"
    if (( now - start > 5400 )); then
      guest_user_ps_bounded 20 "if (Test-Path '${pid_path_ps}') { \$workerPid = [int](Get-Content '${pid_path_ps}' -Raw); & taskkill.exe /PID \$workerPid /T /F 2>\$null | Out-Null }" || true
      die "Windows validation exceeded 90 minutes"
    fi
    last_line="$(guest_user_ps_bounded 20 "if (Test-Path '${log_path_ps}') { Get-Content '${log_path_ps}' -Tail 1 } else { 'waiting for first log line' }" 2>/dev/null | tr -d '\r' | tail -n 1)" || last_line="guest transport unavailable; retrying"
    say "validation running: $last_line"
    sleep 10
  done
  exit_code="$(guest_user_ps_bounded 20 "Get-Content '${done_path_ps}' -Raw" | tr -d '\r\n ')"
  guest_user_ps_bounded 20 "Get-Content '${log_path_ps}' -Tail 160"
  [[ "$exit_code" == "0" ]] || die "Windows validation failed with exit code $exit_code; log: $log_path"
  say "Windows validation passed; log: $log_path"
}

case "$COMMAND" in
  prepare) prepare_app ;;
  verify)
    ensure_vm_running
    wait_for_guest
    verify_baseline
    verify_app
    ;;
  restore)
    [[ -n "$SELECTED_SNAPSHOT" ]] || die "restore requires --snapshot"
    restore_snapshot "$(resolve_app_snapshot_selector "$SELECTED_SNAPSHOT")"
    ;;
  run-tests) run_app_tests ;;
esac
