param()

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }

$data = $raw | ConvertFrom-Json

$project_root = $env:CLAUDE_PROJECT_DIR
if (-not $project_root -and $data.cwd) { $project_root = $data.cwd }
if (-not $project_root) { $project_root = (git rev-parse --show-toplevel 2>$null) }
if (-not $project_root) { exit 0 }

$project_root = $project_root.TrimEnd('\', '/')

$agentignore = Join-Path $project_root '.agentignore'
if (-not (Test-Path $agentignore)) { exit 0 }

$patterns = @()
foreach ($line in (Get-Content $agentignore)) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
    $patterns += $trimmed
}
if ($patterns.Count -eq 0) { exit 0 }

function Get-RelPath {
    param([string]$Base, [string]$Full)
    $b = $Base.TrimEnd('\', '/').Replace('\', '/').ToLower()
    $f = $Full.Replace('\', '/').ToLower()
    if ($f.StartsWith($b + '/')) {
        return $Full.Substring($Base.TrimEnd('\', '/').Length + 1).Replace('\', '/')
    }
    return $null
}

function Test-Ignored {
    param([string]$FilePath)
    if (-not $FilePath) { return $false }

    $resolved = $FilePath
    if ([System.IO.Path]::IsPathRooted($FilePath)) {
        $resolved = Get-RelPath $project_root $FilePath
        if ($null -eq $resolved) { return $false }
    }
    $resolved = $resolved -replace '\\','/'
    $resolved = $resolved.TrimStart('/')

    foreach ($pat in $patterns) {
        $p = $pat -replace '\\','/'
        $p = $p.TrimEnd('/')

        if ($resolved -ieq $p) { return $true }
        if ($resolved -like "$p/*") { return $true }

        $segments = $resolved -split '/'
        for ($i = 0; $i -lt $segments.Count; $i++) {
            if ($segments[$i] -ieq $p) { return $true }
        }
    }
    return $false
}

$tool_input = $data.tool_input

$paths_to_check = @()
if ($tool_input.file_path) { $paths_to_check += $tool_input.file_path }
if ($tool_input.path) { $paths_to_check += $tool_input.path }
if ($tool_input.pattern) { $paths_to_check += $tool_input.pattern }
if ($tool_input.command) {
    $cmd = $tool_input.command
    $tokens = $cmd -split '\s+'
    foreach ($tok in $tokens) {
        $clean = $tok.Trim('"', "'")
        if ($clean -and $clean -notmatch '^-') {
            $paths_to_check += $clean
        }
    }
}

foreach ($p in $paths_to_check) {
    if (Test-Ignored $p) {
        [Console]::Error.WriteLine("BLOCKED: Path '$p' is excluded by .agentignore")
        exit 2
    }
}

exit 0
