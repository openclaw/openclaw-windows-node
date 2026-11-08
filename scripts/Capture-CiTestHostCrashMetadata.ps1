[CmdletBinding()]
param(
    [string]$StartedUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-CiTestHostCrashMetadata {
    param(
        [Parameter(Mandatory)][string]$EventXml,
        [Parameter(Mandatory)][int]$EventId,
        [Parameter(Mandatory)][DateTimeOffset]$TimeCreated
    )

    [xml]$document = $EventXml
    $values = @{}
    foreach ($field in @($document.Event.EventData.Data)) {
        if ($null -ne $field -and -not [string]::IsNullOrWhiteSpace([string]$field.Name)) {
            $values[[string]$field.Name] = [string]$field.InnerText
        }
    }

    $application = @($values["AppName"], $values["AppPath"]) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [System.IO.Path]::GetFileName($_) } |
        Where-Object { $_ -match '^(?i:(?:testhost|dotnet|OpenClaw\.Tray\.UITests)(?:\.exe)?)$' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($application)) {
        return $null
    }

    [pscustomobject]@{
        eventId = $EventId
        timeUtc = $TimeCreated.ToUniversalTime().ToString("o")
        application = $application
        faultingModule = [System.IO.Path]::GetFileName($values["ModuleName"])
        exceptionCode = $values["ExceptionCode"]
        faultingOffset = $values["FaultingOffset"]
    }
}

if ($MyInvocation.InvocationName -eq ".") {
    return
}

if ([string]::IsNullOrWhiteSpace($StartedUtc)) {
    throw "StartedUtc is required to bound the Windows Application Error query."
}

$started = [DateTimeOffset]::Parse(
    $StartedUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal)
$records = @()
try {
    $records = @(Get-WinEvent -FilterHashtable @{
        LogName = "Application"
        Id = 1000
        StartTime = $started.UtcDateTime
    } -ErrorAction Stop)
}
catch {
    if ($_.FullyQualifiedErrorId -like "NoMatchingEventsFound*") {
        Write-Host "No Windows Application Error events were recorded in the Tray UI test window."
        exit 0
    }

    Write-Warning "Could not read Windows Application Error metadata (error type: $($_.Exception.GetType().Name))."
    exit 0
}

$metadata = @(
    $records | ForEach-Object {
        ConvertTo-CiTestHostCrashMetadata `
            -EventXml $_.ToXml() `
            -EventId $_.Id `
            -TimeCreated ([DateTimeOffset]$_.TimeCreated)
    } | Where-Object { $null -ne $_ }
)

if ($metadata.Count -eq 0) {
    Write-Host "No matching Windows Application Error events for testhost/dotnet were found in the Tray UI test window."
    exit 0
}

Write-Host "Tray UI testhost crash metadata (event fields only; no dump, paths, or event message):"
Write-Output ($metadata | ConvertTo-Json -Compress)
Write-Host "Windows Application Error events do not include a call stack; no process memory was captured."
