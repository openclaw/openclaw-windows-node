#Requires -Version 5.1
# UIA-Helpers.ps1 — UI Automation helper functions for OpenClaw E2E tests

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Aliases for cleaner code
$script:UIA = [System.Windows.Automation.AutomationElement]
$script:TreeScope = [System.Windows.Automation.TreeScope]
$script:ControlType = [System.Windows.Automation.ControlType]

function Find-OnboardingWindow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "OpenClaw Setup")
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Find-ElementById {
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)][string]$AutomationId
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ElementByName {
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)][string]$Name
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Click-Button {
    param(
        [Parameter(Mandatory)]$Button
    )
    # Try InvokePattern first (regular buttons)
    try {
        $invokePattern = $Button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($invokePattern) { $invokePattern.Invoke(); return }
    } catch {}
    # Try SelectionItemPattern (radio buttons)
    try {
        $selectPattern = $Button.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($selectPattern) { $selectPattern.Select(); return }
    } catch {}
    # Try TogglePattern (toggle switches / checkboxes)
    try {
        $togglePattern = $Button.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($togglePattern) { $togglePattern.Toggle(); return }
    } catch {}
    throw "Element '$($Button.Current.Name)' does not support Invoke, SelectionItem, or Toggle patterns"
}

function Click-ButtonById {
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)][string]$AutomationId
    )
    $element = Find-ElementById -Window $Window -AutomationId $AutomationId
    if (-not $element) {
        throw "Element with AutomationId '$AutomationId' not found"
    }
    Click-Button -Button $element
}

function Get-AllTextElements {
    param(
        [Parameter(Mandatory)]$Window
    )
    $textCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $elements = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    $results = @()
    foreach ($el in $elements) {
        $results += $el.Current.Name
    }
    return $results
}

function Get-PageTexts {
    param(
        [Parameter(Mandatory)]$Window
    )
    $allTexts = Get-AllTextElements -Window $Window
    return $allTexts | Where-Object { $_.Length -gt 2 -and $_.Length -lt 120 }
}

function Toggle-Element {
    param(
        [Parameter(Mandatory)]$Element
    )
    $togglePattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (-not $togglePattern) {
        throw "Element '$($Element.Current.Name)' does not support TogglePattern"
    }
    $togglePattern.Toggle()
}

function Find-FirstToggle {
    param(
        [Parameter(Mandatory)]$Window
    )
    $allCondition = [System.Windows.Automation.Condition]::TrueCondition
    $elements = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $allCondition)
    foreach ($el in $elements) {
        try {
            $pattern = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($pattern) { return $el }
        } catch {}
    }
    return $null
}

function Assert-TextVisible {
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)][string]$ExpectedText
    )
    $texts = Get-PageTexts -Window $Window
    $found = $texts | Where-Object { $_ -match [regex]::Escape($ExpectedText) }
    if (-not $found) {
        $visibleTexts = ($texts | ForEach-Object { "  '$_'" }) -join "`n"
        throw "Expected text '$ExpectedText' not found on page. Visible texts:`n$visibleTexts"
    }
}

function Assert-PageContains {
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)][string[]]$ExpectedTexts
    )
    foreach ($text in $ExpectedTexts) {
        Assert-TextVisible -Window $Window -ExpectedText $text
    }
}

function Wait-ForText {
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)][string]$Text,
        [int]$TimeoutSeconds = 10
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $texts = Get-PageTexts -Window $Window
            $found = $texts | Where-Object { $_ -match [regex]::Escape($Text) }
            if ($found) { return $true }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for text '$Text' after $TimeoutSeconds seconds"
}
