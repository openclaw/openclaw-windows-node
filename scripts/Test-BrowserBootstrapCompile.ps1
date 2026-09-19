# Compile the real Pascal include before slow app builds. Never execute an installer.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The Inno compile gate requires Windows.' }
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup 6 compiler is unavailable.' }
$include = (Resolve-Path (Join-Path $PSScriptRoot 'BrowserBootstrapManagement.iss')).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ('openclaw-browser-compile-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $script = @"
[Setup]
AppName=OpenClaw Browser Transport Compile Proof
AppVersion=0.0.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
Output=no
[Code]
#include "$include"
"@
    $file = Join-Path $directory 'compile.iss'
    [IO.File]::WriteAllText($file, $script, [Text.UTF8Encoding]::new($true))
    & $compiler $file
    if ($LASTEXITCODE -ne 0) { throw 'Browser management Pascal include failed to compile.' }
    Write-Host 'BROWSER_MANAGEMENT_COMPILE_OK'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
