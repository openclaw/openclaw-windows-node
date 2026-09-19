param([Parameter(Mandatory)][string]$Consumer, [Parameter(Mandatory)][string]$Receipt)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$result = [ordered]@{ status='failed'; stage='preflight'; consumerSha='57f78c49d47f445b85d4859f038e8447e08983ba' }
try {
    $result.host = @{ windows=[bool]$IsWindows; githubActions=($env:GITHUB_ACTIONS -ceq 'true'); githubHosted=($env:RUNNER_ENVIRONMENT -ceq 'github-hosted') }
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or !$IsWindows) { throw 'Disposable Windows required' }
    $result.stage = 'audit-helper-compilation'
    Add-Type -Path (Join-Path $PSScriptRoot 'BrowserNativePathAudit.cs')
    $result.stage = 'node-discovery'
    # Use the same installed launcher used by setup-node and the prior working harness.
    $node = (& node --print process.execPath)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($node)) { throw 'Installed Node discovery failed' }
    $result.stage = 'canonical-node'
    $node = (& $node -e 'process.stdout.write(require("node:fs").realpathSync(process.execPath))')
    if ($LASTEXITCODE -ne 0) { throw 'Node canonicalization failed' }
    $result.nodeVersion = (& $node --version)
    if ($LASTEXITCODE -ne 0 -or $result.nodeVersion -cne 'v24.16.0') { throw 'Installed Node version mismatch' }
    $result.stage = 'canonical-cli'
    $cli = (& $node -e 'process.stdout.write(require("node:fs").realpathSync(process.argv[1]))' (Join-Path $Consumer 'openclaw.mjs'))
    if ($LASTEXITCODE -ne 0) { throw 'CLI canonicalization failed' }
    $result.stage = 'original-path-audit'
    $result.original = @([BrowserNativePathAudit]::Inspect('global-node',$node,$false,$false,$false)) + @([BrowserNativePathAudit]::Inspect('checkout-cli',$cli,$false,$false,$false))
    if ((git -C $Consumer rev-parse HEAD).Trim() -cne $result.consumerSha -or $LASTEXITCODE -ne 0) { throw 'Consumer identity mismatch' }
    $result.stage = 'private-installation'
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $root = Join-Path $local ('OpenClawComposed-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $root) { throw 'Unexpected existing fixture' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($sid)
    foreach ($s in @($sid.Value,'S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($s),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    # This is the only ACL mutation: a new empty, task-owned private root. No global paths are modified.
    $null = New-Item -ItemType Directory -Path $root
    Set-Acl -LiteralPath $root -AclObject $acl
    $nodeDir = Join-Path $root 'node'
    $null = New-Item -ItemType Directory -Path $nodeDir
    $privateNode = Join-Path $nodeDir 'node.exe'
    Copy-Item -LiteralPath $node -Destination $privateNode
    $result.nodeSha256 = (Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath $privateNode -Algorithm SHA256).Hash.ToLowerInvariant() -cne $result.nodeSha256) { throw 'Node copy identity mismatch' }
    $privateCore = Join-Path $root 'consumer'
    # A full exact checkout, not a shim or junction back into a globally writable checkout.
    git -c core.longpaths=true clone --quiet --no-hardlinks --no-checkout -- $Consumer $privateCore
    if ($LASTEXITCODE -ne 0) { throw 'Private checkout failed' }
    git -C $privateCore -c core.longpaths=true checkout --quiet --detach $result.consumerSha
    if ($LASTEXITCODE -ne 0 -or (git -C $privateCore rev-parse HEAD).Trim() -cne $result.consumerSha) { throw 'Private checkout identity mismatch' }
    $result.accepted = @([BrowserNativePathAudit]::Inspect('private-node',$privateNode,$false,$false,$false)) + @([BrowserNativePathAudit]::Inspect('private-cli',(Join-Path $privateCore 'openclaw.mjs'),$false,$false,$false))
    if (@($result.accepted | Where-Object { !$_.Accepted }).Count -ne 0) { throw 'Private runtime admission failed' }
    "COMPOSED_PRIVATE_ROOT=$root" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    "COMPOSED_PRIVATE_CORE=$privateCore" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    "COMPOSED_PRIVATE_NODE=$privateNode" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    $nodeDir | Out-File -FilePath $env:GITHUB_PATH -Append -Encoding utf8
    $result.status = 'passed'
} catch {
    $result.failure = 'private_runtime_preparation_failed'
    $result.exceptionType = $_.Exception.GetType().Name
    $result.errorId = if ($_.FullyQualifiedErrorId -cmatch '^[A-Za-z_][A-Za-z0-9_.]*(,[A-Za-z_][A-Za-z0-9_.]*)?$') { $_.FullyQualifiedErrorId } else { 'withheld' }
    $result.compilerCodes = @([regex]::Matches($_.Exception.Message,'\bCS[0-9]{4}\b') | ForEach-Object { $_.Value } | Select-Object -Unique)
    # Exception messages, invocation details and private paths/SIDs remain withheld.
} finally {
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Receipt -Encoding utf8
    # Receipt contains roles, ancestor distances, rule names and permission bits, never paths or SIDs.
    $result | ConvertTo-Json -Depth 8 -Compress | Write-Output
}
if ($result.status -ne 'passed') { exit 1 }
