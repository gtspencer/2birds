param(
    [Parameter(Mandatory)][ValidateSet('Record', 'Stop', 'Analyze', 'Hitches')][string]$Operation,
    [Parameter(Mandatory)][string]$Config,
    [string]$UnityCli = 'unity',
    [string]$ProjectPath = (Get-Location).Path,
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$configPath = (Resolve-Path -LiteralPath $Config).Path
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
$options = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
$outputDirectory = [string]$options.outputDirectory
if (-not $outputDirectory) { throw 'outputDirectory is required.' }
if (-not [IO.Path]::IsPathRooted($outputDirectory)) {
    $outputDirectory = Join-Path $projectRoot $outputDirectory
}
$outputDirectory = [IO.Path]::GetFullPath($outputDirectory)
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

function Invoke-Snippet([string]$Name) {
    $template = Join-Path $PSScriptRoot ($Name.ToLowerInvariant() + '.cs')
    $escapedPath = $configPath.Replace('"', '""')
    $header = 'var options = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(@"' + $escapedPath + '"));'
    $evalPath = Join-Path $outputDirectory ($Name.ToLowerInvariant() + '-eval-' + [Guid]::NewGuid().ToString('N') + '.cs')
    [IO.File]::WriteAllText($evalPath, $header + [Environment]::NewLine + [IO.File]::ReadAllText($template))
    & $UnityCli command eval_file --project-path $projectRoot --file $evalPath --timeout $TimeoutSeconds --json
    if ($LASTEXITCODE -ne 0) { throw "Unity CLI failed. Check output files before retrying; the editor operation may still be running. Config: $configPath" }
}

function Invoke-Hitches {
    Invoke-Snippet 'Hitches'
    $report = Join-Path $outputDirectory ($options.runName + '-hitches.json')
    $failure = Join-Path $outputDirectory ($options.runName + '-hitches-error.txt')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $report) -and -not (Test-Path -LiteralPath $failure)) {
        if ((Get-Date) -gt $deadline) { throw "Hitch report not written within $TimeoutSeconds s: $report" }
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path -LiteralPath $failure) { throw "Hitch analysis failed: $(Get-Content -Raw -LiteralPath $failure)" }
    Write-Output "Hitch report: $report"
}

if ($Operation -eq 'Hitches') { Invoke-Hitches; return }
Invoke-Snippet $Operation
if ($Operation -ne 'Stop') { return }

# Stop is handled on the next editor update; wait for the final save, then run hitch detection.
$status = Join-Path $outputDirectory ($options.runName + '-status.json')
$errorFile = Join-Path $outputDirectory ($options.runName + '-error.txt')
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $errorFile) { throw "Recorder error: $(Get-Content -Raw -LiteralPath $errorFile)" }
    if ((Test-Path -LiteralPath $status) -and (Get-Content -Raw -LiteralPath $status | ConvertFrom-Json).finished) { break }
    Start-Sleep -Milliseconds 500
}
if (-not ((Get-Content -Raw -LiteralPath $status | ConvertFrom-Json).finished)) { throw "Recording did not finish within 120 s: $status" }
Invoke-Hitches
