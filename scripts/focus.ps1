# Doorbell: the click action of a toast. Registered as the handler for doorbell: links
# (HKCU\Software\Classes\doorbell). It selects the session's tab in Windows Terminal by
# its title, then brings the terminal window to the front. It only focuses windows; it
# never runs anything taken from the link.
#
#   doorbell:focus?hwnd=<terminal window>&title=<session title>&n=<toast id>
param([string]$Uri)

$ErrorActionPreference = 'Stop'
$cache = Join-Path $env:LOCALAPPDATA 'claude-code-doorbell'

function Write-FocusLog([string]$msg) {
    try { Add-Content -Path (Join-Path $cache 'notify.log') -Value "$(Get-Date -Format s) [focus] $msg" } catch {}
}

function ConvertTo-Comparable([string]$s) { (($s -replace '\s+', ' ').Trim()).ToLowerInvariant() }

# Selects the tab whose title is the session title. Claude Code titles a tab with a
# status glyph and the session title, e.g. "* Fix the login page".
function Select-SessionTab([long]$hwnd, [string]$title) {
    $want = ConvertTo-Comparable $title
    if ($want.Length -lt 3) { return $false }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$hwnd)
    $isTab = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::TabItem)
    $match = $null
    foreach ($tab in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isTab)) {
        $name = ConvertTo-Comparable ($tab.Current.Name -replace '^[^\p{L}\p{N}]+\s+', '')
        if ($name -eq $want) { $match = $tab; break }
        if (-not $match -and $name.Length -ge 6 -and ($name.Contains($want) -or $want.Contains($name))) { $match = $tab }
    }
    if (-not $match) { return $false }
    $match.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    return $true
}

try {
    if ($Uri -notmatch '^doorbell:/*focus/?\?(.*)$') { Write-FocusLog "not a focus link: $Uri"; exit 0 }
    $query = @{}
    foreach ($pair in ($Matches[1] -split '&')) {
        $kv = $pair -split '=', 2
        if ($kv.Count -eq 2 -and $kv[0]) { $query[$kv[0]] = [Uri]::UnescapeDataString($kv[1].Replace('+', ' ')) }
    }
    [long]$hwnd = 0
    if (-not [long]::TryParse([string]$query['hwnd'], [ref]$hwnd) -or $hwnd -le 0) {
        Write-FocusLog "clicked, but the pop-up has no terminal window to go to ($Uri)"
        exit 0
    }

    . (Join-Path $PSScriptRoot 'native.ps1')
    Import-DoorbellNative $cache
    if (-not [Doorbell.Native]::IsLiveWindow($hwnd)) { Write-FocusLog "clicked, but window $hwnd is gone"; exit 0 }

    $tab = $false
    try { $tab = [bool](Select-SessionTab $hwnd ([string]$query['title'])) } catch { Write-FocusLog "tab: $_" }
    $how = [Doorbell.Native]::Focus($hwnd)
    Write-FocusLog "clicked $($query['n']): window $hwnd, tab selected: $tab, brought to front: $(if ($how) { $how } else { 'no' })"
} catch { Write-FocusLog "$_" }
