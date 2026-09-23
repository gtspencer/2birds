param(
    [Parameter(Mandatory)][ValidateSet('Record', 'Stop', 'Analyze')][string]$Operation,
    [Parameter(Mandatory)][string]$Config,
    [string]$UnityCli = 'unity',
    [string]$ProjectPath = (Get-Location).Path
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

$template = Join-Path $PSScriptRoot ($Operation.ToLowerInvariant() + '.cs')
$escapedPath = $configPath.Replace('"', '""')
$header = 'var options = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(@"' + $escapedPath + '"));'
$evalPath = Join-Path $outputDirectory ($Operation.ToLowerInvariant() + '-eval-' + [Guid]::NewGuid().ToString('N') + '.cs')
[IO.File]::WriteAllText($evalPath, $header + [Environment]::NewLine + [IO.File]::ReadAllText($template))
& $UnityCli command eval_file --project-path $projectRoot --file $evalPath --json
if ($LASTEXITCODE -ne 0) { throw "Unity CLI failed. Check capture status before retrying; the editor operation may still be running. Config: $configPath" }
