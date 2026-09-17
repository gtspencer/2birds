param()

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }

$data = $raw | ConvertFrom-Json

$tool_output = $data.tool_output
if (-not $tool_output) { exit 0 }

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
    $patterns += $trimmed.TrimEnd('/').Replace('\', '/').ToLower()
}
if ($patterns.Count -eq 0) { exit 0 }

function Test-LineIgnored {
    param([string]$Line)
    $normalized = $Line.Replace('\', '/').ToLower()
    foreach ($pat in $patterns) {
        if ($normalized -match "(^|/|\\)$([regex]::Escape($pat))(/|\\|:)") { return $true }
    }
    return $false
}

$lines = $tool_output -split "`n"
$filtered = @()
$removed = 0

foreach ($line in $lines) {
    if (Test-LineIgnored $line) {
        $removed++
    } else {
        $filtered += $line
    }
}

if ($removed -eq 0) { exit 0 }

$new_output = ($filtered -join "`n")
if ($removed -gt 0) {
    $new_output += "`n[.agentignore: $removed result(s) filtered]"
}

$result = @{
    hookSpecificOutput = @{
        hookEventName = "PostToolUse"
        updatedToolOutput = $new_output
    }
}

$result | ConvertTo-Json -Depth 4 -Compress
exit 0
