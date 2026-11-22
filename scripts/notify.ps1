# Doorbell: Windows notifications for Claude Code
# Windows toast notifications and louder sounds, run from the plugin's hooks.
#
#   -Kind attention  PermissionRequest: Claude needs approval, asks a question
#                    (AskUserQuestion) or has a plan to review (ExitPlanMode).
#                    Orange badge, stays on screen until dismissed, alert sound.
#   -Kind done       Stop: Claude finished its turn. Green badge, short toast, chime.
#   -Kind answered   PostToolUse on AskUserQuestion: removes the question toast and
#                    prints a recap of every question with all of its options.
#
# Toasts show the session name (the /rename name, else Claude's auto title), never
# the question or the command. Reads the hook JSON on stdin and never blocks Claude:
# errors go to %LOCALAPPDATA%\claude-code-doorbell\notify.log.
param(
    [ValidateSet('attention', 'done', 'answered')]
    [string]$Kind = 'attention'
)

$ErrorActionPreference = 'Stop'
$appId = 'ClaudeCodeDoorbell'
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
$cache = Join-Path $env:LOCALAPPDATA 'claude-code-doorbell'
$log = Join-Path $cache 'notify.log'

# --- Settings (environment variables, e.g. from the "env" block of settings.json) ---

function Get-Setting([string]$name, [string]$default) {
    $v = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $default }
    return $v.Trim()
}

function Test-Off([string]$name) { (Get-Setting $name '1') -in @('0', 'false', 'off', 'no') }

function Write-NotifyLog([string]$msg) {
    try {
        if (-not (Test-Path $cache)) { New-Item -ItemType Directory -Force $cache | Out-Null }
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 256KB) { Remove-Item $log -Force }
        Add-Content -Path $log -Value "$(Get-Date -Format s) [$Kind] $msg"
    } catch {}
}

# --- Hook payload ----------------------------------------------------------------

$data = $null
try {
    if ([Console]::IsInputRedirected) {
        [Console]::InputEncoding = New-Object Text.UTF8Encoding $false
        $raw = [Console]::In.ReadToEnd()
        if ($raw.Trim()) { $data = $raw | ConvertFrom-Json }
    }
} catch { Write-NotifyLog "payload: $_" }

function Get-ToastTag {
    $sid = [string]$data.session_id
    if ($sid) { return $sid.Substring(0, [Math]::Min(64, $sid.Length)) }
    return 'claude'
}

function Initialize-Toasts {
    $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
    $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
}

function Write-HookJson($obj) {
    # Raw UTF-8 bytes without a BOM, so Claude Code parses it whatever the console code page.
    $bytes = (New-Object Text.UTF8Encoding $false).GetBytes(($obj | ConvertTo-Json -Compress))
    $out = [Console]::OpenStandardOutput()
    $out.Write($bytes, 0, $bytes.Length)
    $out.Flush()
}

# --- Question recap ----------------------------------------------------------------

function Get-Recap {
    $res = $data.tool_response
    if (-not $res -or -not $res.questions) { $res = $data.tool_input }
    $questions = @($res.questions | Where-Object { $_ })
    if (-not $questions.Count) { return '' }
    $answers = $res.answers
    if (-not $answers) { $answers = $data.tool_input.answers }
    $notes = $res.annotations
    if (-not $notes) { $notes = $data.tool_input.annotations }

    $on = [string][char]0x25CF; $off = [string][char]0x25CB; $arrow = [string][char]0x2192
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($q in $questions) {
        if ($lines.Count) { $lines.Add('') }
        $text = [string]$q.question
        $answer = $null
        if ($answers -and $answers.PSObject.Properties[$text]) { $answer = [string]$answers.PSObject.Properties[$text].Value }
        $labels = @($q.options | ForEach-Object { [string]$_.label })
        $picked = @()
        if ($null -ne $answer) {
            if ($labels -contains $answer) { $picked = @($answer) }
            elseif ($q.multiSelect) { $picked = @($answer -split ', ' | Where-Object { $labels -contains $_ }) }
        }

        $head = if ($q.header) { "[$($q.header)] $text" } else { $text }
        if ($q.multiSelect) { $head += ' (multi-select)' }
        $lines.Add($head)
        foreach ($o in $q.options) {
            $mark = if ($picked -contains [string]$o.label) { $on } else { $off }
            $line = "   $mark $($o.label)"
            if ($o.description) { $line += " - $($o.description)" }
            $lines.Add($line)
        }
        if ($null -eq $answer) { $lines.Add("   $arrow (no answer)") }
        elseif (-not $picked.Count) { $lines.Add("   $arrow Your answer: $answer") }
        if ($notes -and $notes.PSObject.Properties[$text]) {
            $note = [string]$notes.PSObject.Properties[$text].Value.notes
            if ($note) { $lines.Add("   $arrow Notes: $note") }
        }
    }
    # Claude Code prefixes the message with "PostToolUse:AskUserQuestion says:"; the
    # leading blank lines put that label on its own line with a gap before the recap.
    return "`n`n" + ($lines -join "`n")
}

if ($Kind -eq 'answered') {
    try {
        Initialize-Toasts
        [Windows.UI.Notifications.ToastNotificationManager]::History.Remove((Get-ToastTag), 'attention', $appId)
    } catch {}
    if (-not (Test-Off 'CC_NOTIFY_RECAP')) {
        try {
            $recap = Get-Recap
            if ($recap) { Write-HookJson @{ systemMessage = $recap } }
        } catch { Write-NotifyLog "recap: $_" }
    }
    exit 0
}

if ($Kind -eq 'done' -and (Test-Off 'CC_NOTIFY_DONE')) { exit 0 }

# --- Session name ------------------------------------------------------------------

# The /rename name if set, else Claude's auto title. Claude Code keeps re-appending
# these near the end of the transcript, so read only the tail first.
function Get-SessionTitle([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return '' }
    $pattern = '"type":"(custom-title|ai-title)","(?:customTitle|aiTitle)":"((?:[^"\\]|\\.)*)"'
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $n = [int][Math]::Min($fs.Length, 2MB)
        [void]$fs.Seek(-$n, 'End')
        $buf = New-Object byte[] $n
        [void]$fs.Read($buf, 0, $n)
        $isWhole = ($n -eq $fs.Length)
    } finally { $fs.Close() }
    $found = @([regex]::Matches([Text.Encoding]::UTF8.GetString($buf), $pattern))
    if (-not $isWhole -and -not ($found | Where-Object { $_.Groups[1].Value -eq 'custom-title' })) {
        # Rare: a rename that only exists earlier in a big transcript.
        $found += @(Select-String -LiteralPath $path -Pattern '"type":"custom-title"' |
            ForEach-Object { [regex]::Match($_.Line, $pattern) } | Where-Object { $_.Success })
    }
    $pick = @($found | Where-Object { $_.Groups[1].Value -eq 'custom-title' }) | Select-Object -Last 1
    if (-not $pick) { $pick = $found | Select-Object -Last 1 }
    if ($pick) { return [regex]::Unescape($pick.Groups[2].Value) }
    return ''
}

# The short handle Claude Code gives each running session, e.g. "my-app-3f".
function Get-SessionHandle([string]$sid) {
    if (-not $sid) { return '' }
    $config = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    foreach ($f in Get-ChildItem (Join-Path $config 'sessions') -Filter *.json -ErrorAction SilentlyContinue) {
        try {
            $s = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
            if ($s.sessionId -eq $sid -and $s.name) { return [string]$s.name }
        } catch {}
    }
    return ''
}

function Limit-Text([string]$s, [int]$max) {
    if (-not $s) { return '' }
    $s = ($s -replace '\s+', ' ').Trim()
    if ($s.Length -gt $max) { $s = $s.Substring(0, $max - 3) + '...' }
    return $s
}

function ConvertTo-XmlText([string]$s) { [Security.SecurityElement]::Escape($s) }

# --- Assets and app identity ---------------------------------------------------------

# Copies the icon and badges out of the plugin folder, whose path changes with each
# version, so the registered icon and toasts in Action Center keep working after updates.
function Sync-Assets {
    if (-not (Test-Path $cache)) { New-Item -ItemType Directory -Force $cache | Out-Null }
    foreach ($name in 'icon.png', 'badge-done.png', 'badge-question.png', 'badge-approval.png') {
        $src = Join-Path $assets $name
        $dst = Join-Path $cache $name
        if (-not (Test-Path $dst) -or (Get-Item $src).Length -ne (Get-Item $dst).Length) { Copy-Item $src $dst -Force }
    }
}

# Registers the app identity so the toast header shows the app name and icon.
function Register-AppId {
    $name = Get-Setting 'CC_NOTIFY_APP_NAME' 'Claude Code'
    $icon = Join-Path $cache 'icon.png'
    $key = "HKCU:\Software\Classes\AppUserModelId\$appId"
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    $cur = Get-ItemProperty -Path $key
    if ($cur.DisplayName -ne $name) { Set-ItemProperty -Path $key -Name DisplayName -Value $name }
    if ($cur.IconUri -ne $icon) { Set-ItemProperty -Path $key -Name IconUri -Value $icon }
}

# --- Sounds --------------------------------------------------------------------------

# Writes a copy of a 16-bit PCM WAV file with its samples scaled by $db decibels.
function New-BoostedWav([string]$src, [string]$dst, [double]$db) {
    $bytes = [IO.File]::ReadAllBytes($src)
    $ascii = [Text.Encoding]::ASCII
    if ($bytes.Length -lt 12 -or $ascii.GetString($bytes, 0, 4) -ne 'RIFF' -or $ascii.GetString($bytes, 8, 4) -ne 'WAVE') { throw 'not a WAV file' }
    $pos = 12; $pcm16 = $false; $dataStart = -1; $dataLen = 0
    while ($pos + 8 -le $bytes.Length) {
        $id = $ascii.GetString($bytes, $pos, 4)
        $len = [BitConverter]::ToInt32($bytes, $pos + 4)
        if ($id -eq 'fmt ') {
            $format = [BitConverter]::ToUInt16($bytes, $pos + 8)
            $bits = [BitConverter]::ToUInt16($bytes, $pos + 22)
            $pcm16 = (($format -eq 1 -or $format -eq 0xFFFE) -and $bits -eq 16)
        } elseif ($id -eq 'data') {
            $dataStart = $pos + 8
            $dataLen = [Math]::Min($len, $bytes.Length - $dataStart)
            break
        }
        $pos += 8 + $len + ($len % 2)
    }
    if (-not $pcm16 -or $dataStart -lt 0) { throw 'only 16-bit PCM WAV files can be boosted' }

    $gain = [Math]::Pow(10, $db / 20)
    $n = [int][Math]::Floor($dataLen / 2)
    $samples = New-Object 'int16[]' $n
    [Buffer]::BlockCopy($bytes, $dataStart, $samples, 0, $n * 2)
    for ($i = 0; $i -lt $n; $i++) {
        $v = [Math]::Round($samples[$i] * $gain)
        if ($v -gt 32767) { $v = 32767 } elseif ($v -lt -32768) { $v = -32768 }
        $samples[$i] = [int16]$v
    }
    [Buffer]::BlockCopy($samples, 0, $bytes, $dataStart, $n * 2)

    $dir = Split-Path -Parent $dst
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $tmp = "$dst.$PID.tmp"
    [IO.File]::WriteAllBytes($tmp, $bytes)
    Move-Item -LiteralPath $tmp -Destination $dst -Force
}

# The sound to play: a custom file if set, else the Windows sound, boosted by
# CC_NOTIFY_GAIN_DB (default 14 dB). Boosted copies are made once and cached.
function Get-Sound([string]$which) {
    $custom = Get-Setting ('CC_NOTIFY_{0}_SOUND' -f $which.ToUpper()) ''
    $src = if ($custom) { $custom }
           elseif ($which -eq 'done') { Join-Path $env:WINDIR 'Media\Windows Notify.wav' }
           else { Join-Path $env:WINDIR 'Media\Windows Error.wav' }
    if (-not (Test-Path -LiteralPath $src)) { return $null }

    $db = 14.0
    $parsed = 0.0
    if ([double]::TryParse((Get-Setting 'CC_NOTIFY_GAIN_DB' '14'), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) { $db = $parsed }
    if ($db -eq 0) { return $src }

    $id = '{0}-{1}-{2}' -f [IO.Path]::GetFileNameWithoutExtension($src), (Get-Item -LiteralPath $src).Length, $db
    $out = Join-Path $cache ('sounds\' + ($id -replace '[^\w.-]', '_') + '.wav')
    if (Test-Path -LiteralPath $out) { return $out }
    try { New-BoostedWav $src $out $db; return $out }
    catch { Write-NotifyLog "boost $src`: $_"; return $src }
}

# --- What to show ----------------------------------------------------------------------

if ($Kind -eq 'done') {
    $title = 'Claude finished'
    $badge = 'badge-done.png'
} else {
    switch ([string]$data.tool_name) {
        'AskUserQuestion' { $title = 'Claude has a question'; $badge = 'badge-question.png' }
        'ExitPlanMode'    { $title = 'Claude has a plan ready'; $badge = 'badge-approval.png' }
        default           { $title = 'Claude needs your approval'; $badge = 'badge-approval.png' }
    }
}

$sessionTitle = ''
$handle = ''
try { $sessionTitle = Get-SessionTitle ([string]$data.transcript_path) } catch { Write-NotifyLog "title: $_" }
try { $handle = Get-SessionHandle ([string]$data.session_id) } catch { Write-NotifyLog "handle: $_" }
$project = if ($data.cwd) { Split-Path -Leaf ([string]$data.cwd) } else { '' }

$body = if ($sessionTitle) { $sessionTitle } elseif ($handle) { $handle } elseif ($project) { $project } else { 'Claude Code session' }
$footer = if ($handle -and $handle -ne $body) { $handle } elseif ($project -ne $body) { $project } else { '' }

# --- Toast -----------------------------------------------------------------------------

try {
    Sync-Assets
    Register-AppId

    $badgeUri = ([Uri](Join-Path $cache $badge)).AbsoluteUri
    $attribution = ''
    if ($footer) { $attribution = "<text placement=""attribution"">$(ConvertTo-XmlText $footer)</text>" }
    $toastAttrs = ''
    $actions = ''
    if ($Kind -eq 'attention') {
        if (Test-Off 'CC_NOTIFY_PERSIST') {
            $toastAttrs = ' duration="long"'
        } else {
            # The reminder scenario stays on screen until dismissed; it needs a button to do so.
            $toastAttrs = ' scenario="reminder"'
            $actions = '<actions><action content="Dismiss" arguments="dismiss" activationType="system"/></actions>'
        }
    }
    $xml = @"
<toast$toastAttrs>
  <visual>
    <binding template="ToastGeneric">
      <text>$(ConvertTo-XmlText $title)</text>
      <text>$(ConvertTo-XmlText (Limit-Text $body 120))</text>
      $attribution
      <image placement="appLogoOverride" src="$badgeUri"/>
    </binding>
  </visual>
  $actions
  <audio silent="true"/>
</toast>
"@

    Initialize-Toasts
    $history = [Windows.UI.Notifications.ToastNotificationManager]::History
    $tag = Get-ToastTag
    # One toast per session: a finished turn makes a pending question stale, a new
    # question makes the old "finished" toast stale, and removing a same-kind toast
    # first makes Windows pop the new one up instead of swapping it in silently.
    foreach ($group in 'attention', 'done') {
        try { $history.Remove($tag, $group, $appId) } catch {}
    }

    $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
    $doc.LoadXml($xml)
    $toast = New-Object Windows.UI.Notifications.ToastNotification $doc
    $toast.Group = $Kind
    $toast.Tag = $tag
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
} catch { Write-NotifyLog "toast: $_" }

# --- Sound -----------------------------------------------------------------------------

if (-not (Test-Off 'CC_NOTIFY_SOUND')) {
    try {
        $sound = Get-Sound $(if ($Kind -eq 'done') { 'done' } else { 'attention' })
        if ($sound) { (New-Object Media.SoundPlayer $sound).PlaySync() }
    } catch { Write-NotifyLog "sound: $_" }
}
