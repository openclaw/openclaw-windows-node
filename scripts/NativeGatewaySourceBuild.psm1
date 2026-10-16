Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeGatewaySourceBuildLocked([scriptblock]$Action) {
    # Different roots can still share a checkout or register the same patch.
    try {
        $mutex = [Threading.Mutex]::new($false, 'Global\OpenClaw.NativeGatewaySourceBuild')
    }
    catch [UnauthorizedAccessException] {
        throw 'Another account owns the native Gateway source build lock. Wait for its build or unregister to finish, then retry.'
    }
    $held = $false
    try {
        try {
            $held = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $held = $true
            Write-Warning 'A previous native Gateway build exited without releasing its lock. Checking its files before continuing.'
        }
        if (-not $held) {
            throw 'Another native Gateway source build or unregister is running. Wait for it to finish, then retry.'
        }
        & $Action
    }
    finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Assert-NativeGatewayPathAcl([string]$Path, [switch]$Ancestor, [switch]$AllowReparsePoint) {
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $AllowReparsePoint -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Native Gateway build path '$Path' is a reparse point. Use its physical path."
    }
    $acl = Get-Acl -LiteralPath $Path
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
    if ($null -eq $descriptor.DiscretionaryAcl) {
        throw "Native Gateway build path '$Path' has no discretionary ACL and allows unrestricted access. Use a fresh work root under a trusted parent."
    }
    $trusted = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',       # SYSTEM
        'S-1-5-32-544',   # Administrators
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' # TrustedInstaller
    )
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $trusted) {
        throw "Native Gateway build path '$Path' has an untrusted owner. Use a fresh work root under a trusted parent."
    }
    # Ancestors may allow creating unrelated children (as C:\ does), but not replacing this tree.
    $writeMask = 0x10000000L -bor [long][Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    if ($Ancestor -and $item -is [IO.DirectoryInfo] -and $null -eq $item.Parent) {
        # A volume root cannot itself be deleted or renamed. Delete-child and DACL writes still matter.
        $writeMask = $writeMask -band (-bnot [long][Security.AccessControl.FileSystemRights]::Delete)
    }
    if (-not $Ancestor) {
        $writeMask = $writeMask -bor 0x40000000L -bor [long][Security.AccessControl.FileSystemRights]::Write
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and
            -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            $rule.IdentityReference.Value -notin $trusted -and
            ([long]$rule.FileSystemRights -band $writeMask)) {
            throw "Native Gateway build path '$Path' grants write or replacement access to $($rule.IdentityReference.Value). Use a fresh work root under a trusted parent; existing permissions are not changed."
        }
    }
}

function Assert-NativeGatewayAncestors(
    [string]$Path,
    [Collections.Generic.HashSet[string]]$ValidatedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
) {
    $directory = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $directory) {
        if (-not $ValidatedPaths.Add($directory.FullName)) { break }
        Assert-NativeGatewayPathAcl $directory.FullName -Ancestor
        $directory = $directory.Parent
    }
}

function Initialize-NativeGatewayDirectory([string]$Path) {
    $directory = [IO.DirectoryInfo]::new($Path)
    if ($directory.Exists) {
        Assert-NativeGatewayAncestors $directory.FullName
        Assert-NativeGatewayPathAcl $directory.FullName
        return
    }
    if ($null -eq $directory.Parent) {
        throw "Native Gateway build drive '$Path' does not exist."
    }
    if ($directory.Parent.Exists) {
        Assert-NativeGatewayAncestors $directory.Parent.FullName
    }
    else {
        Initialize-NativeGatewayDirectory $directory.Parent.FullName
    }
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($user)
    $writers = @($user.Value, 'S-1-5-18', 'S-1-5-32-544')
    $readers = @('S-1-5-11', 'S-1-5-32-545', 'S-1-15-2-1', 'S-1-15-2-2')
    foreach ($sid in $writers + $readers) {
        $rights = if ($sid -in $writers) { 'FullControl' } else { 'ReadAndExecute' }
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid),
            $rights, 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    # Supply the DACL during creation, never leave a newly created tree writable while fixing it.
    [IO.FileSystemAclExtensions]::Create($directory, $security)
    Assert-NativeGatewayPathAcl $directory.FullName
}

function Assert-NativeGatewayTreeAcl([string]$Path) {
    $ancestors = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $parent = [IO.DirectoryInfo]::new($Path).Parent
    if ($null -ne $parent) { Assert-NativeGatewayAncestors $parent.FullName $ancestors }
    $pending = [Collections.Generic.Stack[string]]::new()
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        if (-not $visited.Add($current)) { continue }
        $item = Get-Item -LiteralPath $current -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Assert-NativeGatewayPathAcl $current -AllowReparsePoint
            $target = $item.ResolveLinkTarget($true)
            if ($null -eq $target -or -not $target.Exists) {
                throw "Native Gateway build link '$current' has no readable target. Repair or remove the broken link before retrying."
            }
            if ($target -is [IO.FileInfo]) {
                Assert-NativeGatewayAncestors $target.DirectoryName $ancestors
            }
            elseif ($null -ne $target.Parent) {
                Assert-NativeGatewayAncestors $target.Parent.FullName $ancestors
            }
            $pending.Push($target.FullName)
            continue
        }
        Assert-NativeGatewayPathAcl $current
        if ($item -is [IO.DirectoryInfo]) {
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
                $pending.Push($child.FullName)
            }
        }
    }
}

function Read-NativeGatewaySourceMetadata([string]$PackageDirectory) {
    $path = Join-Path $PackageDirectory 'source.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Prebuilt package is missing '$path'. Supply openclaw.tgz and its source.json."
    }
    try {
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
    }
    catch {
        throw "Cannot read prebuilt package metadata '$path': $($_.Exception.Message)"
    }
    if ($metadata -isnot [Collections.IDictionary]) {
        throw "Prebuilt package metadata '$path' must be a JSON object."
    }
    foreach ($field in @('resolvedCommit', 'packageSha256', 'packageVersion', 'nodeVersion')) {
        if ($metadata[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace($metadata[$field])) {
            throw "Prebuilt package metadata '$path' requires a nonempty string '$field'."
        }
    }
    foreach ($entry in @{ resolvedCommit = 40; packageSha256 = 64 }.GetEnumerator()) {
        if ($metadata[$entry.Key] -notmatch "\A[0-9a-fA-F]{$($entry.Value)}\z") {
            throw "Prebuilt package metadata '$path' requires '$($entry.Key)' to contain exactly $($entry.Value) hexadecimal characters."
        }
        $metadata[$entry.Key] = $metadata[$entry.Key].ToLowerInvariant()
    }
    $archive = Join-Path $PackageDirectory 'openclaw.tgz'
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "Prebuilt package is missing '$archive'."
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $metadata.packageSha256) {
        throw "Prebuilt package '$archive' does not match source.json packageSha256."
    }
    return $metadata
}

Export-ModuleMember -Function Invoke-NativeGatewaySourceBuildLocked, Initialize-NativeGatewayDirectory,
    Assert-NativeGatewayTreeAcl, Read-NativeGatewaySourceMetadata
