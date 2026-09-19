param()

try {
    $raw = [Console]::In.ReadToEnd()
    if (-not $raw) { exit 0 }
    $data = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$session_cwd = [string]$data.cwd
$project_root = $null
if ($session_cwd) {
    $project_root = (& git -C $session_cwd rev-parse --show-toplevel 2>$null | Select-Object -First 1)
}
if ($project_root) {
    $project_root = ([string]$project_root).Trim()
}
if (-not $project_root) {
    $project_root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$project_root = ([System.IO.Path]::GetFullPath($project_root)).TrimEnd('\', '/')
$agentignore = Join-Path $project_root '.agentignore'
if (-not (Test-Path -LiteralPath $agentignore)) { exit 0 }

function Convert-ToRepoPath {
    param([string]$Path)
    if (-not $Path) { return $null }

    $candidate = $Path.Trim().Trim('"', "'")
    if (-not $candidate) { return $null }

    try {
        if ([System.IO.Path]::IsPathRooted($candidate)) {
            $full = [System.IO.Path]::GetFullPath($candidate)
        } elseif ($session_cwd) {
            $full = [System.IO.Path]::GetFullPath((Join-Path $session_cwd $candidate))
        } else {
            $full = [System.IO.Path]::GetFullPath((Join-Path $project_root $candidate))
        }

        $root_prefix = $project_root + [System.IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($root_prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
        return $full.Substring($root_prefix.Length).Replace('\', '/')
    } catch {
        return $null
    }
}

$tool_input = $data.tool_input
$paths = @()
if ($tool_input.file_path) { $paths += [string]$tool_input.file_path }
if ($tool_input.path) { $paths += [string]$tool_input.path }
if ($tool_input.pattern) { $paths += [string]$tool_input.pattern }

if ($tool_input.command) {
    $command = [string]$tool_input.command
    $paths += [regex]::Matches($command, '"([^"]+)"|''([^'']+)''|(\S+)') | ForEach-Object {
        if ($_.Groups[1].Success) { $_.Groups[1].Value }
        elseif ($_.Groups[2].Success) { $_.Groups[2].Value }
        else { $_.Groups[3].Value }
    }
    $paths += [regex]::Matches($command, '(?m)^\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*(.+)$') | ForEach-Object {
        $_.Groups[1].Value.Trim()
    }
}

foreach ($path in $paths) {
    $repo_path = Convert-ToRepoPath $path
    if (-not $repo_path) { continue }

    $ignored = $repo_path | & git -C $project_root check-ignore --no-index --stdin --exclude-from=$agentignore 2>$null
    if ($LASTEXITCODE -eq 0 -and $ignored) {
        [Console]::Error.WriteLine("BLOCKED: Path '$path' is excluded by .agentignore")
        exit 2
    }
}

exit 0
