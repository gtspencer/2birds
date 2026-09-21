param()

$ErrorActionPreference = 'Stop'

trap {
    [Console]::Error.WriteLine("HOOK INTERNAL ERROR: " + $_.Exception.Message)
    exit 2
}

function Block-Codex {
    param([string]$Reason)
    [Console]::Error.WriteLine("BLOCKED: $Reason")
    exit 2
}

function Get-PropertyValue {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Block-Codex 'The .agentignore guard received no hook input.'
    }
    $data = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    Block-Codex ("The .agentignore guard could not parse hook input: " + $_.Exception.Message)
}

$sessionCwd = [string](Get-PropertyValue $data 'cwd')
if ([string]::IsNullOrWhiteSpace($sessionCwd)) {
    $sessionCwd = (Get-Location).Path
}

try {
    $projectRoot = & git -C $sessionCwd rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$projectRoot)) {
        Block-Codex 'The .agentignore guard could not determine the Git repository root.'
    }
    $projectRoot = [System.IO.Path]::GetFullPath(([string]$projectRoot).Trim())
} catch {
    Block-Codex ("The .agentignore guard could not determine the Git repository root: " + $_.Exception.Message)
}

$agentignore = Join-Path $projectRoot '.agentignore'
$agentignoreForGit = $agentignore.Replace('\', '/')
if (-not (Test-Path -LiteralPath $agentignore -PathType Leaf)) {
    Block-Codex "Required policy file '$agentignore' is missing."
}

$rootComparable = $projectRoot.TrimEnd([char[]]@('\', '/'))
$rootPrefix = $rootComparable + [System.IO.Path]::DirectorySeparatorChar

function Convert-ToRepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $candidate = $Path.Trim()
    $candidate = $candidate.Trim([char[]]@('"', "'", '`'))
    $candidate = $candidate.TrimStart([char[]]@('(', '[', '{', '&'))
    $candidate = $candidate.TrimEnd([char[]]@(')', ']', '}', ';', ','))
    $candidate = $candidate -replace '^\d*(?:>>?|<<?)', ''
    $candidate = $candidate.Trim()

    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    if ($candidate.StartsWith('-')) { return $null }
    if ($candidate -match '^[A-Za-z][A-Za-z0-9+.-]*://') { return $null }

    # Extract the value from common key=value / option=value forms.
    if ($candidate -match '^[^=]+=(.+)$') {
        $candidate = $Matches[1].Trim()
        $candidate = $candidate.Trim([char[]]@('"', "'", '`'))
    }

    # Handle Git revision path syntax such as HEAD:Specs/file.md.
    if ($candidate -notmatch '^[A-Za-z]:' -and $candidate -match '^[^:]+:(.+)$') {
        $candidate = $Matches[1]
    }

    # Remove :line or :line:column suffixes used by some tools.
    if ($candidate -notmatch '^[A-Za-z]:[\\/]' ) {
        $candidate = $candidate -replace ':\d+(?::\d+)?$', ''
    }

    # If the tool supplied a glob, test the concrete prefix before the first wildcard.
    $wildcardIndex = $candidate.IndexOfAny([char[]]@('*', '?', '['))
    if ($wildcardIndex -ge 0) {
        $candidate = $candidate.Substring(0, $wildcardIndex).TrimEnd([char[]]@('\', '/'))
        if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    }

    try {
        if ([System.IO.Path]::IsPathRooted($candidate)) {
            $fullPath = [System.IO.Path]::GetFullPath($candidate)
        } else {
            $fullPath = [System.IO.Path]::GetFullPath((Join-Path $sessionCwd $candidate))
        }
    } catch {
        return $null
    }

    $fullComparable = $fullPath.TrimEnd([char[]]@('\', '/'))

    if ($fullComparable.Equals($rootComparable, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ''
    }

    if (-not $fullComparable.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    return $fullComparable.Substring($rootPrefix.Length).Replace('\', '/')
}

function Test-AlwaysBlockedPath {
    param([string]$RepoPath)

    if ($null -eq $RepoPath) { return $false }

    $normalized = $RepoPath.Replace('\', '/').TrimStart([char[]]@('/')).ToLowerInvariant()

    if ($normalized -eq '.agentignore') { return $true }
    if ($normalized -eq '.git' -or $normalized.StartsWith('.git/')) { return $true }
    if ($normalized -eq '.hooks' -or $normalized.StartsWith('.hooks/')) { return $true }
    if ($normalized -eq '.codex') { return $true }
    if ($normalized -eq '.codex/hooks.json') { return $true }
    if ($normalized -eq '.codex/config.toml') { return $true }

    return $false
}

# Use an empty temporary Git worktree so git check-ignore applies only the
# patterns from .agentignore, not the repository's .gitignore files.
try {
    $ignoreCache = Join-Path ([System.IO.Path]::GetTempPath()) 'codex-agentignore-check'
    $ignoreGitDir = Join-Path $ignoreCache 'git'
    $ignoreWorkTree = Join-Path $ignoreCache 'worktree'

    New-Item -ItemType Directory -Force -Path $ignoreCache | Out-Null
    New-Item -ItemType Directory -Force -Path $ignoreWorkTree | Out-Null

    if (-not (Test-Path -LiteralPath (Join-Path $ignoreGitDir 'HEAD') -PathType Leaf)) {
        & git init --bare --quiet $ignoreGitDir 2>$null
        if ($LASTEXITCODE -ne 0) {
            Block-Codex 'The .agentignore guard could not initialize its temporary Git matcher.'
        }
    }
} catch {
    Block-Codex ("The .agentignore guard could not initialize its matcher: " + $_.Exception.Message)
}

function Test-AgentIgnored {
    param([string]$RepoPath)

    if ($null -eq $RepoPath) { return $false }
    if (Test-AlwaysBlockedPath $RepoPath) { return $true }
    if ([string]::IsNullOrWhiteSpace($RepoPath)) { return $false }

    $probes = @($RepoPath)

    # Directory-only .gitignore patterns such as "Specs/" do not match the
    # bare string "Specs", so also probe the directory form.
    if (-not $RepoPath.EndsWith('/')) {
        $probes += ($RepoPath + '/')
    }

    try {
        $ignored = @(
            $probes |
                & git "--git-dir=$ignoreGitDir" "--work-tree=$ignoreWorkTree" -c "core.excludesFile=$agentignoreForGit" check-ignore --no-index --stdin 2>$null
        )
        $exitCode = $LASTEXITCODE
    } catch {
        Block-Codex ("The .agentignore guard failed while matching '$RepoPath': " + $_.Exception.Message)
    }

    if ($exitCode -eq 0 -and $ignored.Count -gt 0) { return $true }
    if ($exitCode -eq 1) { return $false }

    Block-Codex "The .agentignore matcher failed while checking '$RepoPath'."
}

$candidates = New-Object 'System.Collections.Generic.List[string]'

function Add-Candidate {
    param([string]$Value)
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        [void]$candidates.Add($Value)
    }
}

function Add-StructuredPathCandidates {
    param($Value, [string]$PropertyName)

    if ($null -eq $Value) { return }

    if ($Value -is [string]) {
        if ($PropertyName -match '(?i)(path|file|filename|dir|directory|root|source|destination|target|pattern|glob)') {
            Add-Candidate ([string]$Value)
        }
        return
    }

    if ($Value -is [System.ValueType]) { return }

    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            if ([string]$key -eq 'command') { continue }
            Add-StructuredPathCandidates $Value[$key] ([string]$key)
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        foreach ($item in $Value) {
            Add-StructuredPathCandidates $item $PropertyName
        }
        return
    }

    foreach ($property in $Value.PSObject.Properties) {
        if ($property.Name -eq 'command') { continue }
        Add-StructuredPathCandidates $property.Value $property.Name
    }
}

$toolName = [string](Get-PropertyValue $data 'tool_name')
$toolInput = Get-PropertyValue $data 'tool_input'
Add-StructuredPathCandidates $toolInput ''

$command = [string](Get-PropertyValue $toolInput 'command')

if (-not [string]::IsNullOrWhiteSpace($command)) {
    if ($toolName -eq 'apply_patch') {
        foreach ($match in [regex]::Matches($command, '(?m)^\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*(.+?)\s*$')) {
            Add-Candidate $match.Groups[1].Value
        }
        foreach ($match in [regex]::Matches($command, '(?m)^\*\*\*\s+Move\s+to:\s*(.+?)\s*$')) {
            Add-Candidate $match.Groups[1].Value
        }
    } elseif ($toolName -eq 'Bash') {
        foreach ($match in [regex]::Matches($command, '"([^"]+)"|''([^'']+)''|(\S+)')) {
            if ($match.Groups[1].Success) {
                $token = $match.Groups[1].Value
            } elseif ($match.Groups[2].Success) {
                $token = $match.Groups[2].Value
            } else {
                $token = $match.Groups[3].Value
            }

            Add-Candidate $token

            if ($token -match '^[^=]+=(.+)$') {
                Add-Candidate $Matches[1]
            }
        }
    }
}

$seen = @{}
foreach ($candidate in $candidates) {
    $repoPath = Convert-ToRepoPath $candidate
    if ($null -eq $repoPath) { continue }

    $key = $repoPath.ToLowerInvariant()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true

    if (Test-AgentIgnored $repoPath) {
        Block-Codex "Path '$candidate' is blocked by .agentignore policy."
    }
}

exit 0
