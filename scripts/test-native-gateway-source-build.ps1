#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NativeGatewaySourceBuild.psm1') -Force
$modulePath = Join-Path $PSScriptRoot 'NativeGatewaySourceBuild.psm1'
$buildPath = Join-Path $PSScriptRoot 'Build-NativeGatewayFromSource.ps1'
$script:passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}

function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        $script:passed++
        return
    }
    throw "Expected failure containing '$Expected'."
}

function Invoke-LockProbe {
    $escapedModule = $modulePath.Replace("'", "''")
    $command = @"
`$ErrorActionPreference = 'Stop'
Import-Module '$escapedModule'
try { Invoke-NativeGatewaySourceBuildLocked { 'entered' }; exit 0 }
catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 7 }
"@
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    foreach ($argument in @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(20000)) {
            $process.Kill($true)
            throw 'Lock probe timed out.'
        }
        return @{
            ExitCode = $process.ExitCode
            Output = $process.StandardOutput.ReadToEnd()
            Error = $process.StandardError.ReadToEnd()
        }
    }
    finally { $process.Dispose() }
}

function Add-UntrustedWrite([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    $acl = [IO.FileSystemAclExtensions]::GetAccessControl(
        $item, [Security.AccessControl.AccessControlSections]::Access)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-11'), 'Modify', 'Allow'))
    [IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('oc-source-build-test-' + [guid]::NewGuid().ToString('N'))
try {
    Initialize-NativeGatewayDirectory $root
    $acl = Get-Acl -LiteralPath $root
    Assert-True $acl.AreAccessRulesProtected 'New root must not inherit a writable drive ACL.'
    $readerRules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.IdentityReference.Value -eq 'S-1-5-11' })
    Assert-True ($readerRules.Count -eq 1 -and
        ($readerRules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq
        [Security.AccessControl.FileSystemRights]::ReadAndExecute -and
        -not ($readerRules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)) 'Other accounts must have RX, not write.'
    foreach ($sid in @('S-1-5-32-545', 'S-1-15-2-1', 'S-1-15-2-2')) {
        $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
            Where-Object { $_.IdentityReference.Value -eq $sid })
        Assert-True ($rules.Count -eq 1 -and
            ($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq
            [Security.AccessControl.FileSystemRights]::ReadAndExecute -and
            -not ($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)) 'Users and app-container readers must retain RX without write.'
    }
    $child = Join-Path $root 'nested\payload'
    Initialize-NativeGatewayDirectory $child
    Assert-NativeGatewayTreeAcl $root
    Assert-True (Test-Path -LiteralPath $child) 'Missing intermediate directories must be created securely.'

    $file = Join-Path $child 'runtime.js'
    Set-Content -LiteralPath $file -Value 'fixture'
    Assert-NativeGatewayTreeAcl $root
    Add-UntrustedWrite $file
    Assert-Throws { Assert-NativeGatewayTreeAcl $root } 'grants write or replacement access'
    Remove-Item -LiteralPath $file
    $nullDaclFile = Join-Path $root 'null-dacl.js'
    Set-Content -LiteralPath $nullDaclFile -Value 'fixture'
    $nullDacl = [Security.AccessControl.FileSecurity]::new()
    $nullDacl.SetSecurityDescriptorSddlForm('D:NO_ACCESS_CONTROL', [Security.AccessControl.AccessControlSections]::Access)
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($nullDaclFile), $nullDacl)
    Assert-Throws { Assert-NativeGatewayTreeAcl $root } 'allows unrestricted access'
    Remove-Item -LiteralPath $nullDaclFile

    $unsafe = Join-Path $root 'unsafe'
    Initialize-NativeGatewayDirectory $unsafe
    Add-UntrustedWrite $unsafe
    Assert-Throws { Initialize-NativeGatewayDirectory $unsafe } 'grants write or replacement access'
    Assert-Throws { Initialize-NativeGatewayDirectory (Join-Path $unsafe 'new-root') } 'grants write or replacement access'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unsafe 'new-root'))) 'An unsafe ancestor must be rejected before creating files.'

    $link = Join-Path $root 'linked-payload'
    New-Item -ItemType Junction -Path $link -Target $child | Out-Null
    Assert-NativeGatewayTreeAcl $link
    Add-UntrustedWrite $child
    Assert-Throws { Assert-NativeGatewayTreeAcl $link } 'grants write or replacement access'
    Remove-Item -LiteralPath $link

    $package = Join-Path $root 'package'
    Initialize-NativeGatewayDirectory $package
    $metadataPath = Join-Path $package 'source.json'
    Assert-Throws { Read-NativeGatewaySourceMetadata $package } 'missing'
    $archive = Join-Path $package 'openclaw.tgz'
    Set-Content -LiteralPath $archive -Value 'archive fixture'
    $valid = @{
        resolvedCommit = 'A' * 40
        packageSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        packageVersion = '2026.9.7'
        nodeVersion = '24.16.0'
    }
    $valid | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
    $metadata = Read-NativeGatewaySourceMetadata $package
    Assert-True ($metadata.resolvedCommit -ceq ('a' * 40)) 'Commit must normalize before cache lookup.'
    foreach ($field in @('resolvedCommit', 'packageSha256', 'packageVersion', 'nodeVersion')) {
        $invalid = $valid.Clone()
        $invalid.Remove($field)
        $invalid | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
        Assert-Throws { Read-NativeGatewaySourceMetadata $package } "nonempty string '$field'"
    }
    foreach ($field in @('resolvedCommit', 'packageSha256')) {
        $invalid = $valid.Clone()
        $invalid[$field] = '123'
        $invalid | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
        Assert-Throws { Read-NativeGatewaySourceMetadata $package } 'hexadecimal characters'
    }
    foreach ($json in @('null', '[]', '[{}]', '"text"')) {
        Set-Content -LiteralPath $metadataPath -Value $json
        Assert-Throws { Read-NativeGatewaySourceMetadata $package } 'must be a JSON object'
    }
    Set-Content -LiteralPath $metadataPath -Value '{'
    Assert-Throws { Read-NativeGatewaySourceMetadata $package } 'Cannot read prebuilt package metadata'
    $valid | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
    Set-Content -LiteralPath $archive -Value 'changed archive'
    Assert-Throws { Read-NativeGatewaySourceMetadata $package } 'does not match'
    Remove-Item -LiteralPath $archive
    Assert-Throws { Read-NativeGatewaySourceMetadata $package } 'missing'

    Invoke-NativeGatewaySourceBuildLocked {
        $probe = Invoke-LockProbe
        Assert-True ($probe.ExitCode -eq 7 -and $probe.Error -like '*Another native Gateway*' -and
            $probe.Output -notlike '*entered*') 'A concurrent process must fail before running its action.'
    }
    Assert-Throws { Invoke-NativeGatewaySourceBuildLocked { throw 'test action failed' } } 'test action failed'
    $probe = Invoke-LockProbe
    Assert-True ($probe.ExitCode -eq 0 -and $probe.Output -like '*entered*') 'The lock must release after failure.'

    $missingRoot = Join-Path $root 'unregister-must-not-create'
    Assert-Throws { & $buildPath -Unregister -WorkRoot $missingRoot -Patch fixture } 'Deploy-LocalPackage.ps1 was not found'
    Assert-True (-not (Test-Path -LiteralPath $missingRoot)) 'Unregister must not create an unused work root.'

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($buildPath, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) 'Build script must parse.'
    $assignment = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$WorkRoot'
    }, $true)
    Push-Location $root
    try {
        $WorkRoot = '.\relative\..\output'
        . ([scriptblock]::Create($assignment.Extent.Text))
        Assert-True ($WorkRoot -eq (Join-Path $root 'output')) 'Relative roots must resolve against the initial PowerShell location.'
        Push-Location $package
        try {
            Assert-True ([IO.Path]::IsPathFullyQualified($WorkRoot) -and
                (Join-Path $WorkRoot 'packages') -eq (Join-Path $root 'output\packages')) 'Changing directory must not change derived package paths.'
        }
        finally { Pop-Location }
    }
    finally { Pop-Location }
    $lockCall = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq 'Invoke-NativeGatewaySourceBuildLocked'
    }, $true)
    Assert-True ($null -ne $lockCall -and
        $lockCall.Extent.Text.Contains('& $clawctl teardown') -and
        $lockCall.Extent.Text.Contains('Sync-Checkout $packagingUrl') -and
        $lockCall.Extent.Text.Contains('& $deployScript -Patch')) 'The lock must cover unregister, checkout and registration.'
    Assert-True (-not $lockCall.Extent.Text.Contains('Assert-NativeGatewayTreeAcl $WorkRoot')) 'Do not scan unrelated historical payloads and staging directories.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
Write-Host "Native Gateway source build regressions: $script:passed passed."
