<#
.SYNOPSIS
    Runs only a minimal, non-uninstallable Inno fixture for the seven MXC cleanup entries.
.DESCRIPTION
    Never compiles or executes installer.iss itself, a product uninstaller, or Gateway cleanup.
    Copies only InstallDelete and its ordinary-path guard into a new fixture identity.
    -Baseline removes Check clauses to observe the engine's existing junction behavior.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CompilerPath,
    [string]$OutputDirectory = 'TestResults\MxcReviewFixes\Inno',
    [switch]$Baseline
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$root = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$source = Get-Content -LiteralPath (Join-Path $repo 'installer.iss') -Raw
$deletes = [regex]::Match($source, '(?ms)^\[InstallDelete\]\s*(.*?)^\[').Groups[1].Value.Trim()
$entries = @($deletes -split '\r?\n' | Where-Object { $_ -match '^Type:' })
if ($entries.Count -ne 7 -or @($entries | Where-Object { $_ -match '^Type: files;' }).Count -ne 4) {
    throw 'Expected exactly four files and three empty-directory entries.'
}
if ($Baseline) { $deletes = $deletes -replace '; Check:.*', '' }
elseif (@($entries | Where-Object { $_ -notmatch '; Check: LegacyMxcPathIsOrdinary\(ExpandConstant\(' }).Count) {
    throw 'Every legacy deletion must check its own expanded path.'
}
$start = $source.IndexOf('function MigrationPathAttributes(')
$end = $source.IndexOf('function InitializeUninstall:', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Ordinary-path helper boundaries not found.' }
$guards = $source.Substring($start, $end - $start)
if ($guards -match '(?i)uninstall|powershell|Exec\(') {
    # Comments may mention uninstall, but no uninstaller hooks or launch code may enter this fixture.
    if ($guards -match '(?im)^(function|procedure)\s+.*Uninstall|Exec\(|powershell') {
        throw 'Unexpected executable code in the fixture helper slice.'
    }
}
$fixture = @"
[Setup]
AppId=MxcCleanupFixture-$([guid]::NewGuid().ToString('N'))
AppName=MXC cleanup fixture
AppVersion=1
DefaultDirName=$root\unused
OutputDir=$root
OutputBaseFilename=fixture
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
CreateAppDir=yes
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
[InstallDelete]
$deletes
[Code]
$guards
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Log('Fixture app path: ' + ExpandConstant('{app}'));
  if Pos('$root\', ExpandConstant('{app}')) <> 1 then
    Result := 'Refusing a destination outside this owned fixture.'
  else
    Result := '';
end;
"@
$iss = Join-Path $root 'fixture.iss'
Set-Content -LiteralPath $iss -Value $fixture -Encoding utf8
& $compiler '/Q' $iss *>&1 | Tee-Object (Join-Path $root 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'Minimal fixture compilation failed.' }
$exe = Join-Path $root 'fixture.exe'
$inventory = @('tools\mxc\x64\wxc-exec.exe', 'tools\mxc\x64\wslcsdk.dll',
    'tools\mxc\arm64\wxc-exec.exe', 'tools\mxc\arm64\wslcsdk.dll')
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Write-Sentinel([string]$Path) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    [IO.File]::WriteAllText($Path, 'synthetic-mxc-preserve')
}
try {
    foreach ($case in @('normal', 'empty', 'absent', 'tools-link', 'mxc-link', 'x64-link', 'arm64-link', 'denied')) {
        $caseRoot = Join-Path $root $case
        $app = Join-Path $caseRoot 'app'
        $victim = Join-Path $caseRoot 'victim'
        New-Item -ItemType Directory -Path $app,$victim -Force | Out-Null
        $link = $null
        $aclPath = $null
        $acl = $null
        $victimFiles = @()
        try {
            if ($case -like '*-link') {
                $relative = switch ($case) {
                    'tools-link' { 'tools' }
                    'mxc-link' { 'tools\mxc' }
                    'x64-link' { 'tools\mxc\x64' }
                    'arm64-link' { 'tools\mxc\arm64' }
                }
                $link = Join-Path $app $relative
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($link)) -Force | Out-Null
                $victimFiles = @($inventory | Where-Object { $_.StartsWith($relative + '\') } |
                    ForEach-Object { Join-Path $victim $_.Substring($relative.Length + 1) })
                foreach ($path in $victimFiles) { Write-Sentinel $path }
                Write-Sentinel (Join-Path $victim 'unknown.txt')
                New-Item -ItemType Junction -Path $link -Target $victim | Out-Null
            } elseif ($case -in @('normal', 'denied')) {
                foreach ($relative in $inventory) { Write-Sentinel (Join-Path $app $relative) }
                Write-Sentinel (Join-Path $app 'tools\mxc\x64\unknown.txt')
            } elseif ($case -eq 'empty') {
                New-Item -ItemType Directory -Path (Join-Path $app 'tools\mxc\x64'),(Join-Path $app 'tools\mxc\arm64') -Force | Out-Null
            }
            Write-Sentinel (Join-Path $app 'wslcsdk.dll')
            if ($case -eq 'denied') {
                $aclPath = Join-Path $app 'tools\mxc'
                $acl = Get-Acl -LiteralPath $aclPath
                $restricted = Get-Acl -LiteralPath $aclPath
                $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
                $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,
                    [Security.AccessControl.FileSystemRights]'ReadData,ReadAttributes,ReadExtendedAttributes,ExecuteFile',
                    [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
                    [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny)
                $restricted.AddAccessRule($rule)
                Set-Acl -LiteralPath $aclPath -AclObject $restricted
            }
            $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',"/DIR=`"$app`"","/LOG=`"$caseRoot\install.log`"")
            $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
            try {
                $processes.Add(@{Pid=$process.Id; StartTimeUtc=$process.StartTime.ToUniversalTime().ToString('o');
                    Executable=$exe; CommandLine=($arguments -join ' '); Case=$case; Ports=@(); DataRoot=$caseRoot})
                $processes | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'processes.json')
                if (-not $process.WaitForExit(60000)) {
                    $process.Kill($true)
                    $process.WaitForExit()
                    throw 'Owned minimal Inno fixture exceeded its deadline.'
                }
                Assert-Condition ($process.ExitCode -eq 0) "Fixture exit $($process.ExitCode): $case"
            } finally { $process.Dispose() }
            if ($acl) { Set-Acl -LiteralPath $aclPath -AclObject $acl; $acl = $null }
            Assert-Condition ([IO.File]::ReadAllText((Join-Path $app 'wslcsdk.dll')) -eq 'synthetic-mxc-preserve') "$case removed new SDK DLL."
            Assert-Condition (-not (Test-Path (Join-Path $app 'unins000.exe'))) 'A fixture must not create an uninstaller.'
            $preservedVictims = @()
            if ($case -like '*-link') {
                foreach ($path in $victimFiles) {
                    if (-not $Baseline) {
                        Assert-Condition (Test-Path -LiteralPath $path) "$case deleted a junction victim."
                    }
                }
                $preservedVictims = @($victimFiles | Where-Object { Test-Path -LiteralPath $_ })
                Assert-Condition (Test-Path (Join-Path $victim 'unknown.txt')) "$case removed unknown victim file."
                if (-not $Baseline) {
                    Assert-Condition ((Get-Content "$caseRoot\install.log" -Raw) -match 'Skipping legacy MXC cleanup') "$case did not log the refusal."
                }
            } elseif ($case -eq 'normal') {
                foreach ($relative in $inventory) { Assert-Condition (-not (Test-Path (Join-Path $app $relative))) 'Ordinary legacy file survived.' }
                Assert-Condition (Test-Path (Join-Path $app 'tools\mxc\x64\unknown.txt')) 'Unknown sibling was deleted.'
                Assert-Condition (-not (Test-Path (Join-Path $app 'tools\mxc\arm64'))) 'Empty ARM directory survived.'
            } elseif ($case -in @('empty', 'absent')) {
                Assert-Condition (-not (Test-Path (Join-Path $app 'tools\mxc'))) 'Empty or absent legacy tree was created or retained.'
            } elseif ($case -eq 'denied' -and -not $Baseline) {
                foreach ($relative in $inventory) { Assert-Condition (Test-Path (Join-Path $app $relative)) 'Unreadable ancestor allowed deletion.' }
                Assert-Condition ((Get-Content "$caseRoot\install.log" -Raw) -match 'Skipping legacy MXC cleanup') 'Access failure was not refused by the Check guard.'
            }
            $checks.Add(@{Case=$case;Passed=$true;Baseline=[bool]$Baseline;VictimFiles=$victimFiles;
                PreservedVictims=$preservedVictims;DeletedVictimCount=($victimFiles.Count - $preservedVictims.Count)})
        } finally {
            if ($acl) { Set-Acl -LiteralPath $aclPath -AclObject $acl }
            if ($link -and (Test-Path -LiteralPath $link)) { [IO.Directory]::Delete($link) }
        }
    }
} finally {
    @{Baseline=[bool]$Baseline;Checks=$checks;RunRoot=$root;FixtureSha256=(Get-FileHash $exe).Hash;
        SourceSha256=(Get-FileHash (Join-Path $repo 'installer.iss')).Hash} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'summary.json')
}
Write-Host "Minimal Inno cleanup fixture passed $($checks.Count) cases. Baseline=$Baseline. Evidence: $root"
