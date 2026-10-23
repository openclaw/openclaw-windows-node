[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LeaseId,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [switch] $GuestAuthorized
)
$ErrorActionPreference = 'Stop'
if (!$GuestAuthorized -or $LeaseId -notmatch '^cbx_[a-f0-9]+$' -or (Test-Path $OutputDirectory)) {
    throw 'Only an explicitly authorized new guest and fresh output may be probed.'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory
$self = [Diagnostics.Process]::GetCurrentProcess()
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$sessions = @(foreach ($explorer in Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -Property Handle,ProcessId,SessionId,Name) {
    $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwnerSid
    if ($owner.ReturnValue -eq 0) {
        [pscustomobject]@{ session = $explorer.SessionId; pid = $explorer.ProcessId
            sid = $owner.Sid }
    }
})
$providers = @(foreach ($name in @('Microsoft-Windows-Win32k','Microsoft-Windows-Kernel-Process')) {
    $provider = Get-WinEvent -ListProvider $name -ErrorAction Stop
    [pscustomobject]@{
        name = $name; id = [string]$provider.Id
        metadataExit = 0; metadataError = $null
        keywords = @($provider.Keywords | ForEach-Object {
            $bits = [BitConverter]::ToUInt64([BitConverter]::GetBytes([long]$_.Value),0)
            @{ name = $_.Name; mask = ('0x{0:X}' -f $bits) }
        })
        events = @($provider.Events | ForEach-Object {
            @{ id = $_.Id; version = $_.Version; keywords = [string]$_.Keywords
                task = [string]$_.Task; opcode = [string]$_.Opcode; template = $_.Template }
        })
    }
})
$tools = @(foreach ($name in @('wpr.exe','logman.exe','tracerpt.exe','dotnet.exe','pwsh.exe','cl.exe','git.exe')) {
    $tool = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue
    if ($tool) { @{ name = $name; path = $tool.Source; sha256 = (Get-FileHash $tool.Source).Hash } }
})
$wpr = @($tools | Where-Object name -eq 'wpr.exe')
if ($wpr.Count -ne 1) { throw 'Installed guest WPR missing. No tracing permitted.' }
& $wpr[0].path -help *> (Join-Path $OutputDirectory 'wpr-help-private.txt')
$helpExit = $LASTEXITCODE
& logman.exe query providers Microsoft-Windows-Win32k *> (Join-Path $OutputDirectory 'win32k-provider-private.txt')
$providerExit = $LASTEXITCODE
[ordered]@{
    completed = $true; computer = $env:COMPUTERNAME; leaseId = $LeaseId
    os = [Environment]::OSVersion.VersionString; pointerBytes = [IntPtr]::Size
    actorSid = $identity.User.Value; actorSession = $self.SessionId; desktopCandidates = $sessions
    tools = $tools; providers = $providers; helpExitCode = $helpExit; providerExitCode = $providerExit
    traceStarted = $false; traceStartsCounter = 0
    privateMetadataOnly = $true
} | ConvertTo-Json -Depth 16 | Set-Content (Join-Path $OutputDirectory 'qualification-private.json') -Encoding UTF8
[pscustomobject]@{
    completed = $true; computer = $env:COMPUTERNAME; leaseId = $LeaseId
    traceStartsCounter = 0; desktopCandidateCount = $sessions.Count
    receiptSha256 = (Get-FileHash (Join-Path $OutputDirectory 'qualification-private.json')).Hash
} | ConvertTo-Json
