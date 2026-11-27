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
# cache folder, once per version, and returns its path. Windows starts it as a COM
# server when a pop-up is clicked, so it has to be an .exe, built for a fixed platform:
# an "Any CPU" build is marked 32-bit but runs as 64-bit, and COM then never connects
# to it. Its path never changes, because COM keeps launching a server's old path after
# it changes; Doorbell.Focus.txt records which sources the current build came from.
function Get-DoorbellFocusApp([string]$CacheDir) {
    $sources = @((Join-Path $PSScriptRoot 'native.cs'), (Join-Path $PSScriptRoot 'focusapp.cs'))
    $text = ($sources | ForEach-Object { [IO.File]::ReadAllText($_) }) -join "`n"
    $bytes = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($text))
    $hash = ([BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 12)
    $exe = Join-Path $CacheDir 'Doorbell.Focus.exe'
    $stamp = Join-Path $CacheDir 'Doorbell.Focus.txt'
    $built = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
    if ($built -eq $hash -and (Test-Path -LiteralPath $exe)) { return $exe }

    if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Force $CacheDir | Out-Null }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
    $tmp = Join-Path $CacheDir "Doorbell.Focus.$hash.$PID.exe"
    $options = New-Object System.CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = $tmp
    $platform = if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { 'x64' } else { 'x86' }
    $options.CompilerOptions = "/target:winexe /platform:$platform /optimize+"
    foreach ($ref in 'System.dll',
                     [System.Windows.Automation.AutomationElement].Assembly.Location,
                     [System.Windows.Automation.ControlType].Assembly.Location,
                     [System.Windows.Threading.Dispatcher].Assembly.Location) {
        [void]$options.ReferencedAssemblies.Add($ref)
    }
    $result = (New-Object Microsoft.CSharp.CSharpCodeProvider).CompileAssemblyFromFile($options, [string[]]$sources)
    if ($result.Errors.HasErrors) { throw "compile: $(@($result.Errors | Where-Object { -not $_.IsWarning }) -join '; ')" }
    try {
        Move-Item -LiteralPath $tmp -Destination $exe -Force -ErrorAction Stop
        Set-Content -LiteralPath $stamp -Value $hash
    } catch {
        # The old build is running (it lives a second after a click), or another hook is
        # replacing it at the same moment: keep it and update next time.
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $exe)) { throw }
    }
    return $exe
}
