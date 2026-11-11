function ConvertTo-CiNativeFault {
    param([Parameter(Mandatory)][string]$EventXml)

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $text = [IO.StringReader]::new($EventXml)
    $reader = [Xml.XmlReader]::Create($text, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    } finally { $reader.Dispose(); $text.Dispose() }
    $values = @{}
    foreach ($entry in $document.SelectNodes("//*[local-name()='EventData']/*[local-name()='Data']")) {
        $values[$entry.GetAttribute('Name')] = $entry.InnerText
    }
    $application = [IO.Path]::GetFileName([string]$values['AppName'])
    if ($application -notin @('dotnet.exe', 'testhost.exe', 'testhost.x86.exe',
            'dotnet-coverage.exe', 'OpenClaw.Tray.WinUI.exe')) {
        return
    }
    $module = [IO.Path]::GetFileName([string]$values['ModuleName'])
    if ($module -notmatch '\A[A-Za-z0-9_.-]{1,128}\.(?:dll|exe)\z') { $module = $null }
    $code = [string]$values['ExceptionCode']
    if ($code -match '\A(?:0x)?([0-9A-Fa-f]{8})\z') { $code = '0x' + $Matches[1].ToUpperInvariant() }
    else { $code = $null }
    $processId = [string]$values['ProcessId']
    if ($processId -notmatch '\A(?:0x[0-9A-Fa-f]{1,8}|[0-9]{1,10})\z') { $processId = $null }
    $moduleVersion = [string]$values['ModuleVersion']
    if ($moduleVersion -notmatch '\A[0-9]+(?:\.[0-9]+){1,3}\z') { $moduleVersion = $null }
    [pscustomobject][ordered]@{
        application = $application
        processId = $processId
        module = $module
        moduleVersion = $moduleVersion
        exceptionCode = $code
    }
}

function Write-CiNativeFailureMetadata {
    param(
        [Parameter(Mandatory)][DateTime]$StartedUtc,
        [Parameter(Mandatory)][int]$TestCommandExitCode,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $faults = @()
    $faultStatus = 'collected'
    $faultErrorType = $null
    try {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = 'Application'; Id = 1000; StartTime = $StartedUtc.ToUniversalTime()
        } -ErrorAction Stop)
        $faults = @($events | ForEach-Object { ConvertTo-CiNativeFault -EventXml $_.ToXml() })
        if ($faults.Count -eq 0) { $faultStatus = 'no-matching-fault-events' }
    } catch {
        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound,*') {
            $faultStatus = 'no-matching-fault-events'
        } else {
            $faultStatus = 'unavailable'
            $faultErrorType = $_.Exception.GetType().Name
            Write-Warning "Native fault metadata unavailable ($faultErrorType)."
        }
    }
    $packages = @()
    $runtimeStatus = 'collected'
    try {
        $packages = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction Stop |
            Sort-Object Name, Version | ForEach-Object {
                [pscustomobject]@{ name = $_.Name; version = [string]$_.Version; architecture = [string]$_.Architecture }
            })
        if ($packages.Count -eq 0) { $runtimeStatus = 'no-runtime-packages-found' }
    } catch {
        $runtimeStatus = 'unavailable'
        Write-Warning ("Windows App Runtime metadata unavailable ({0})." -f $_.Exception.GetType().Name)
    }
    # Never serialize event messages/XML, paths, command lines, environment values or dumps.
    $summary = [pscustomobject][ordered]@{
        schemaVersion = 1
        startedUtc = $StartedUtc.ToUniversalTime().ToString('o')
        testCommandExitCode = $TestCommandExitCode
        faultCollection = $faultStatus
        faultErrorType = $faultErrorType
        faults = $faults
        runtimeCollection = $runtimeStatus
        runtimePackages = $packages
        memoryDumpsCollected = $false
    }
    [IO.File]::WriteAllText($OutputPath, ($summary | ConvertTo-Json -Depth 6))
    Write-Host "Native failure metadata: $OutputPath (faults=$($faults.Count), collection=$faultStatus)."
}
