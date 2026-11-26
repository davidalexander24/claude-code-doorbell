# Doorbell: loads the Doorbell.Native helpers from native.cs (next to this file),
# compiled once into the cache folder so later runs only load the DLL.
function Import-DoorbellNative([string]$CacheDir) {
    if ('Doorbell.Native' -as [type]) { return }
    $src = Join-Path $PSScriptRoot 'native.cs'
    $hash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.Substring(0, 12)
    $dll = Join-Path $CacheDir "Doorbell.Native.$hash.dll"
    if (-not (Test-Path -LiteralPath $dll)) {
        if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Force $CacheDir | Out-Null }
        $tmp = Join-Path $CacheDir "Doorbell.Native.$hash.$PID.dll"
        try {
            Add-Type -TypeDefinition ([IO.File]::ReadAllText($src)) -OutputAssembly $tmp -OutputType Library -ErrorAction Stop
            Move-Item -LiteralPath $tmp -Destination $dll -Force -ErrorAction Stop
        } catch {
            # Another hook may be compiling at the same moment: compile in memory this time.
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            Add-Type -TypeDefinition ([IO.File]::ReadAllText($src))
            return
        }
    }
    Add-Type -Path $dll
}

# Compiles the click handler (focusapp.cs + native.cs) into a small Windows app in the
# cache folder, once per version, and returns its path. Windows only runs a pop-up's
# link when it opens an app, not a script, so the handler has to be an .exe.
function Get-DoorbellFocusApp([string]$CacheDir) {
    $sources = @((Join-Path $PSScriptRoot 'native.cs'), (Join-Path $PSScriptRoot 'focusapp.cs'))
    $text = ($sources | ForEach-Object { [IO.File]::ReadAllText($_) }) -join "`n"
    $bytes = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($text))
    $hash = ([BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 12)
    $exe = Join-Path $CacheDir "Doorbell.Focus.$hash.exe"
    if (Test-Path -LiteralPath $exe) { return $exe }

    if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Force $CacheDir | Out-Null }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
    $references = @(
        [System.Windows.Automation.AutomationElement].Assembly.Location,
        [System.Windows.Automation.ControlType].Assembly.Location,
        [System.Windows.Threading.Dispatcher].Assembly.Location
    )
    $tmp = Join-Path $CacheDir "Doorbell.Focus.$hash.$PID.exe"
    try {
        Add-Type -Path $sources -OutputAssembly $tmp -OutputType WindowsApplication -ReferencedAssemblies $references -ErrorAction Stop
        Move-Item -LiteralPath $tmp -Destination $exe -Force -ErrorAction Stop
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $exe)) { throw }   # else another hook built it first
    }
    # Older builds are no longer registered; remove them unless one is running.
    Get-ChildItem $CacheDir -Filter 'Doorbell.Focus.*.exe' | Where-Object { $_.FullName -ne $exe } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    return $exe
}
