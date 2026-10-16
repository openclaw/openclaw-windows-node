[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportsDirectory,
    [Parameter(Mandatory)][string]$ArtifactsDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requirements = @(
    @{Case='C01.NavigationPreservesPreferences';Image='navigation-settings.png';Control='SettingsPageMarker'},
    @{Case='C01.NavigationPreservesPreferences';Image='navigation-restored.png';Control='SettingsPageShowToolCalls'},
    @{Case='C04.PreferenceOffToOnSurvivesRestart';Image='preference-edited.png';Control='SettingsPageShowToolCalls'},
    @{Case='C04.PreferenceOffToOnSurvivesRestart';Image='preference-restarted.png';Control='SettingsPageShowToolCalls'},
    @{Case='C04.PreferenceOnToOffSurvivesRestart';Image='preference-edited.png';Control='SettingsPageShowToolCalls'},
    @{Case='C04.PreferenceOnToOffSurvivesRestart';Image='preference-restarted.png';Control='SettingsPageShowToolCalls'},
    @{Case='C01.McpOnlyStartsWithoutGatewayCredential';Image='mcp-only-startup.png';Control='WorkspaceNavigation'},
    @{Case='C01.UnavailableGatewayStillOpensWorkspace';Image='unavailable-gateway-startup.png';Control='WorkspaceNavigation'},
    @{Case='C03.SendStreamsIntoSelectedConversation';Image='chat-pending.png';Control='ChatComposerPrimaryAction'},
    @{Case='C03.SendStreamsIntoSelectedConversation';Image='chat-streaming.png';Control='ChatComposerPrimaryAction'},
    @{Case='C03.SendStreamsIntoSelectedConversation';Image='chat-final.png';Control='ChatComposerPrimaryAction'},
    @{Case='C03.CancelStopsOutputAndAllowsNextSend';Image='chat-before-cancel.png';Control='ChatComposerPrimaryAction'},
    @{Case='C03.CancelStopsOutputAndAllowsNextSend';Image='chat-cancelled.png';Control='ChatComposerPrimaryAction'},
    @{Case='C03.CancelStopsOutputAndAllowsNextSend';Image='chat-after-cancel.png';Control='ChatComposerPrimaryAction'},
    @{Case='C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer';Image='two-peer-before-switch.png';Control='SavedGatewayConnectAction'},
    @{Case='C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer';Image='two-peer-after-switch.png';Control='WorkspaceNavigation'}
)
$runIds = @{}
foreach ($requirement in $requirements) {
    $case = Get-Content -LiteralPath (Join-Path $ReportsDirectory "$($requirement.Case).json") -Raw | ConvertFrom-Json
    if ($case.RunId -notmatch '^[a-f0-9]{32}$') { throw 'Capture evidence requires a fixture-owned run ID.' }
    if ($runIds.ContainsKey($case.RunId) -and $runIds[$case.RunId] -cne $requirement.Case) {
        throw 'Different journeys cannot reuse the same capture run.'
    }
    $runIds[$case.RunId] = $requirement.Case
    $runRoot = Join-Path $ArtifactsDirectory $case.RunId
    $image = Join-Path $runRoot $requirement.Image
    $metadata = [IO.Path]::ChangeExtension($image, '.json')
    if (-not (Test-Path -LiteralPath $image -PathType Leaf) -or
        -not (Test-Path -LiteralPath $metadata -PathType Leaf)) { throw "Required capture is missing: $($requirement.Case)/$($requirement.Image)" }
    foreach($path in @($runRoot,$image,$metadata)) {
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Capture evidence cannot cross reparse points.'
        }
    }
    $proof = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
    if ($proof.RunId -cne $case.RunId -or
        $proof.sha256 -cne (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash) {
        throw 'Capture identity or image hash does not match its case.'
    }
    if ($proof.sampledContentColors -lt 16 -or $proof.bounds.Width -lt 400 -or $proof.bounds.Height -lt 300) {
        throw 'Capture metadata describes blank or undersized rendered content.'
    }
    $assertions = @($proof.assertions | Where-Object AutomationId -ceq $requirement.Control)
    if ($assertions.Count -ne 1 -or $assertions[0].Width -le 0 -or $assertions[0].Height -le 0 -or
        $assertions[0].left -lt 0 -or $assertions[0].top -lt 0 -or
        ($assertions[0].left + $assertions[0].Width) -gt $proof.bounds.Width -or
        ($assertions[0].top + $assertions[0].Height) -gt $proof.bounds.Height) {
        throw 'Required asserted control is missing or cropped in the capture evidence.'
    }
    $captured = [DateTimeOffset]::Parse($proof.capturedAtUtc)
    $matches = @($case.Checkpoints | Where-Object Name -ceq "capture:$($requirement.Image)")
    if ($matches.Count -ne 1 -or $captured -gt [DateTimeOffset]::Parse($matches[0].ObservedAtUtc) -or
        ([DateTimeOffset]::Parse($matches[0].ObservedAtUtc) - $captured).TotalSeconds -gt 30) {
        throw 'Capture is stale or missing its completed case checkpoint.'
    }
}
