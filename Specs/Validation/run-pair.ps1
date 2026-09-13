param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$Output = "$PSScriptRoot\Results\pair",
    [ValidateSet('lan', 'normal', 'stress')][string]$Profile = 'lan',
    [ValidateSet('walk', 'collision', 'impulse', 'idle', 'fall')][string]$Route = 'walk',
    [int]$Fps = 60,
    [int]$Seconds = 125,
    [int]$Port = 17770,
    [switch]$Inventory
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$launched = @()
try {
    $joinPort = $Port
    if ($Profile -ne 'lan') {
        $joinPort = $Port + 1
        $delay = if ($Profile -eq 'normal') { 50 } else { 100 }
        $jitter = if ($Profile -eq 'normal') { 10 } else { 25 }
        $loss = if ($Profile -eq 'normal') { '0.01' } else { '0.03' }
        $proxyArguments = "`"$PSScriptRoot\udp_profile.py`" --listen $joinPort --target $Port --delay $delay --jitter $jitter --loss $loss --seconds $($Seconds + 25)"
        $launched += Start-Process python -ArgumentList $proxyArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput "$Output\profile.log"
    }
    $inventoryArguments = if ($Inventory) { " -inventoryValidation true" } else { "" }
    $hostArguments = "-batchmode -nographics -mvpMode Host -mvpPort $Port -mvpRoute $Route -mvpFps $Fps -mvpSeconds $($Seconds + 12) -mvpOutput `"$Output\host`" -logFile `"$Output\host-unity.log`""
    $hostArguments += $inventoryArguments
    $hostProcess = Start-Process $Executable -ArgumentList $hostArguments -WindowStyle Hidden -PassThru
    $launched += $hostProcess
    $clientArguments = "-batchmode -nographics -mvpMode Join -mvpPort $joinPort -mvpDelay 5 -mvpRoute $Route -mvpFps $Fps -mvpSeconds $Seconds -mvpOutput `"$Output\join`" -logFile `"$Output\join-unity.log`""
    $clientArguments += $inventoryArguments
    $clientProcess = Start-Process $Executable -ArgumentList $clientArguments -WindowStyle Hidden -PassThru
    $launched += $clientProcess
    $hostProcess.WaitForExit()
    $clientProcess.WaitForExit()
    if ($hostProcess.ExitCode -ne 0 -or $clientProcess.ExitCode -ne 0) { throw 'A validation process failed.' }
    foreach ($role in @('host', 'join')) {
        if (-not (Select-String -LiteralPath "$Output\$role\session.log" -SimpleMatch 'ENTERED_GAME' -Quiet)) {
            throw "$role did not enter gameplay. Inspect its Unity log."
        }
    }
    if ($Inventory) {
        foreach ($role in @('host', 'join')) {
            if (-not (Select-String -LiteralPath "$Output\$role-unity.log" -SimpleMatch 'INVENTORY VALIDATION COMPLETE' -Quiet)) { throw "$role inventory validation did not complete." }
            if (Select-String -LiteralPath "$Output\$role-unity.log" -Pattern 'INVENTORY FAIL|Exception:' -Quiet) { throw "$role inventory validation failed." }
        }
    }
    python "$PSScriptRoot\summarize.py" $Output | Set-Content -Encoding utf8 -LiteralPath "$Output\summary.json"
    Get-Content -LiteralPath "$Output\summary.json"
}
finally {
    foreach ($process in $launched) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    }
}
