param()

$ErrorActionPreference = 'Stop'

trap {
    [Console]::Error.WriteLine("HOOK INTERNAL ERROR: " + $_.Exception.Message)
    exit 2
}

function Block-Tool {
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
    $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), (New-Object System.Text.UTF8Encoding $false))
    $raw = $stdin.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Block-Tool 'The .agentreadonly guard received no hook input.'
    }
    $data = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    Block-Tool ("The .agentreadonly guard could not parse hook input: " + $_.Exception.Message)
}

$toolName = [string](Get-PropertyValue $data 'tool_name')
$toolInput = Get-PropertyValue $data 'tool_input'

# Only writes are guarded; reads, globs and searches pass through untouched.
$isShellTool = $toolName -eq 'Bash' -or $toolName -eq 'PowerShell'
$isWriteTool = $toolName -match '(?i)(edit|write|patch|create|delete|remove|move|rename|update|insert|replace|save|upload)'
if (-not $isShellTool -and -not $isWriteTool) { exit 0 }

$sessionCwd = [string](Get-PropertyValue $data 'cwd')
if ([string]::IsNullOrWhiteSpace($sessionCwd)) {
    $sessionCwd = (Get-Location).Path
}

$projectRoot = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { $sessionCwd }
$projectRoot = [System.IO.Path]::GetFullPath($projectRoot)
while (-not (Test-Path -LiteralPath (Join-Path $projectRoot '.git'))) {
    $projectRoot = [System.IO.Path]::GetDirectoryName($projectRoot)
    if ([string]::IsNullOrEmpty($projectRoot)) {
        Block-Tool 'The .agentreadonly guard could not determine the Git repository root.'
    }
}

$agentreadonly = Join-Path $projectRoot '.agentreadonly'
if (-not (Test-Path -LiteralPath $agentreadonly -PathType Leaf)) {
    Block-Tool "Required policy file '$agentreadonly' is missing."
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

    # Git Bash paths such as /c/Users/... on Windows.
    if ([System.IO.Path]::DirectorySeparatorChar -eq '\' -and $candidate -match '^/([A-Za-z])(/.*)?$') {
        $candidate = $Matches[1] + ':' + $(if ($Matches[2]) { $Matches[2] } else { '/' })
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

# Use an empty temporary Git worktree so git check-ignore applies only the
# patterns from .agentreadonly, not the repository's .gitignore files.
try {
    $ignoreCache = Join-Path ([System.IO.Path]::GetTempPath()) 'claude-agentreadonly-check'
    $ignoreGitDir = Join-Path $ignoreCache 'git'
    $ignoreWorkTree = Join-Path $ignoreCache 'worktree'

    New-Item -ItemType Directory -Force -Path $ignoreCache | Out-Null
    New-Item -ItemType Directory -Force -Path $ignoreWorkTree | Out-Null

    if (-not (Test-Path -LiteralPath (Join-Path $ignoreGitDir 'HEAD') -PathType Leaf)) {
        # Windows PowerShell turns native stderr into terminating errors under 'Stop'.
        $ErrorActionPreference = 'Continue'
        & git -c init.defaultBranch=main init --bare --quiet $ignoreGitDir 2>$null
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) {
            Block-Tool 'The .agentreadonly guard could not initialize its temporary Git matcher.'
        }
    }
} catch {
    Block-Tool ("The .agentreadonly guard could not initialize its matcher: " + $_.Exception.Message)
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

# Commands that never write to their path arguments. Segments led by anything
# else are treated as potential writes, so unknown commands fail closed.
$readOnlyCommands = @(
    'cat', 'head', 'tail', 'less', 'more', 'grep', 'egrep', 'fgrep', 'rg', 'ls', 'dir', 'wc',
    'diff', 'cmp', 'file', 'stat', 'du', 'pwd', 'cd', 'echo', 'printf', 'which', 'type', 'test',
    '[', 'true', 'false', 'cut', 'jq', 'basename', 'dirname', 'realpath', 'readlink',
    'md5sum', 'sha1sum', 'sha256sum',
    'get-content', 'gc', 'select-string', 'sls', 'get-childitem', 'gci', 'get-item', 'gi',
    'get-itemproperty', 'gp', 'test-path', 'resolve-path', 'rvpa', 'split-path', 'join-path',
    'measure-object', 'measure', 'select-object', 'select', 'where-object', 'where',
    'format-table', 'ft', 'format-list', 'fl', 'format-wide', 'out-string', 'out-host',
    'write-output', 'write-host', 'set-location', 'sl', 'push-location', 'pushd',
    'pop-location', 'popd', 'get-location', 'gl', 'get-filehash', 'compare-object',
    'sort-object', 'group-object', 'convertfrom-json', 'convertto-json', 'get-command', 'gcm'
)
$readOnlyGitCommands = @(
    'status', 'diff', 'log', 'show', 'blame', 'ls-files', 'ls-tree', 'grep', 'rev-parse',
    'cat-file', 'check-ignore', 'describe', 'shortlog', 'help', 'version'
)
$findWriteActions = @('-delete', '-exec', '-execdir', '-ok', '-okdir', '-fprint', '-fprint0', '-fprintf', '-fls')

function Test-ReadOnlySegment {
    param($Words)

    foreach ($word in $Words) {
        # Command/process substitution, script blocks and subexpressions can
        # hide arbitrary writes behind a read-only command name.
        if ($word.Kind -eq 'word' -and $word.Text -match '[$@]\(|`|[(){}]') { return $false }
        if ($word.Kind -eq 'dq' -and $word.Text -match '\$\(|`') { return $false }
    }

    $index = 0
    while ($index -lt $Words.Count -and $Words[$index].Kind -eq 'word' -and $Words[$index].Text -match '^[A-Za-z_][A-Za-z0-9_]*=') {
        $index++
    }
    if ($index -ge $Words.Count) { return $true }

    $name = [System.IO.Path]::GetFileName($Words[$index].Text.Replace('\', '/')).ToLowerInvariant() -replace '\.exe$', ''
    $arguments = @($Words | Select-Object -Skip ($index + 1) | ForEach-Object { $_.Text })

    if ($readOnlyCommands -contains $name) { return $true }

    if ($name -eq 'sed') {
        return -not ($arguments | Where-Object { $_ -match '^(-[^-]*i|--in-place)' })
    }

    if ($name -eq 'find') {
        return -not ($arguments | Where-Object { $findWriteActions -contains $_ })
    }

    if ($name -eq 'git') {
        for ($i = 0; $i -lt $arguments.Count; $i++) {
            if (@('-C', '-c', '--git-dir', '--work-tree', '--namespace') -contains $arguments[$i]) { $i++; continue }
            if ($arguments[$i].StartsWith('-')) { continue }
            return $readOnlyGitCommands -contains $arguments[$i].ToLowerInvariant()
        }
        return $true
    }

    return $false
}

function Add-ShellWriteCandidates {
    param([string]$Command)

    $tokenPattern = '(?<sq>''[^'']*'')|(?<dq>"(?:[^"\\`]|[\\`].)*")|(?<sep>&&|\|\||[;|&\n])|(?<redir>\d*>>?&\d*|&>>?|\d*>>?\|?|\d*<<?<?-?)|(?<word>[^\s;|&"''<>]+)'

    # Each segment is one simple command; segments joined by | share a pipeline.
    $segments = New-Object 'System.Collections.Generic.List[object]'
    $current = New-Object 'System.Collections.Generic.List[object]'
    $pipeline = 0
    foreach ($match in [regex]::Matches($Command, $tokenPattern)) {
        if ($match.Groups['sep'].Success) {
            $segments.Add([pscustomobject]@{ Pipeline = $pipeline; Tokens = $current })
            $current = New-Object 'System.Collections.Generic.List[object]'
            if ($match.Value -ne '|') { $pipeline++ }
            continue
        }

        if ($match.Groups['redir'].Success) {
            $token = [pscustomobject]@{ Kind = 'redir'; Text = $match.Value }
        } elseif ($match.Groups['sq'].Success) {
            $token = [pscustomobject]@{ Kind = 'sq'; Text = $match.Value.Substring(1, $match.Value.Length - 2) }
        } elseif ($match.Groups['dq'].Success) {
            $token = [pscustomobject]@{ Kind = 'dq'; Text = $match.Value.Substring(1, $match.Value.Length - 2) }
        } else {
            $token = [pscustomobject]@{ Kind = 'word'; Text = $match.Value }
        }
        $current.Add($token)
    }
    $segments.Add([pscustomobject]@{ Pipeline = $pipeline; Tokens = $current })

    $pipelineWords = @{}
    $writingPipelines = @{}
    foreach ($segment in $segments) {
        $tokens = $segment.Tokens
        if ($tokens.Count -eq 0) { continue }

        if (-not $pipelineWords.ContainsKey($segment.Pipeline)) {
            $pipelineWords[$segment.Pipeline] = New-Object 'System.Collections.Generic.List[object]'
        }

        $words = New-Object 'System.Collections.Generic.List[object]'
        for ($i = 0; $i -lt $tokens.Count; $i++) {
            $token = $tokens[$i]
            if ($token.Kind -ne 'redir') {
                $words.Add($token)
                $pipelineWords[$segment.Pipeline].Add($token)
                continue
            }

            # Output redirection targets are always writes; input redirection
            # sources and fd duplications such as 2>&1 are not.
            $isOutput = $token.Text.Contains('>') -and $token.Text -notmatch '&\d*$'
            $isDuplication = $token.Text -match '&\d*$'
            if (-not $isDuplication -and ($i + 1) -lt $tokens.Count -and $tokens[$i + 1].Kind -ne 'redir') {
                $i++
                if ($isOutput) { Add-Candidate $tokens[$i].Text }
            }
        }

        if (-not (Test-ReadOnlySegment $words)) {
            $writingPipelines[$segment.Pipeline] = $true
        }
    }

    # A writer can receive paths from earlier pipeline stages (find x | xargs rm,
    # Get-ChildItem x | Remove-Item), so every word in a writing pipeline counts.
    foreach ($pipelineId in $writingPipelines.Keys) {
        foreach ($word in $pipelineWords[$pipelineId]) {
            Add-Candidate $word.Text
            # Quoted payloads such as bash -c "rm file" hide paths inside one token.
            if ($word.Text -match '\s') {
                foreach ($part in ($word.Text -split '\s+')) { Add-Candidate $part }
            }
        }
    }
}

if ($isShellTool) {
    $command = [string](Get-PropertyValue $toolInput 'command')
    if (-not [string]::IsNullOrWhiteSpace($command)) {
        Add-ShellWriteCandidates $command
    }
} else {
    Add-StructuredPathCandidates $toolInput ''
}

# Match every candidate in a single git process; spawning git per token is
# slow enough on Windows to blow the hook timeout.
$probeToCandidate = @{}
$probes = New-Object 'System.Collections.Generic.List[string]'
foreach ($candidate in $candidates) {
    $repoPath = Convert-ToRepoPath $candidate
    if ($null -eq $repoPath) { continue }

    if ([string]::IsNullOrWhiteSpace($repoPath)) { continue }

    # Directory-only patterns such as "Specs/" do not match the bare
    # string "Specs", so also probe the directory form.
    foreach ($probe in @($repoPath, ($repoPath.TrimEnd('/') + '/'))) {
        if ($probeToCandidate.ContainsKey($probe)) { continue }
        $probeToCandidate[$probe] = $candidate
        [void]$probes.Add($probe)
    }
}

if ($probes.Count -eq 0) { exit 0 }

try {
    $ErrorActionPreference = 'Continue'
    $OutputEncoding = New-Object System.Text.UTF8Encoding $false

    # Git keeps the CR from CRLF pattern files, so on Windows a blank line would
    # become a pattern matching every "dir/" probe. Match against an LF copy.
    $normalizedIgnore = Join-Path $ignoreCache "agentreadonly-$PID"
    $patternText = [System.IO.File]::ReadAllText($agentreadonly) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($normalizedIgnore, $patternText, $OutputEncoding)

    # NUL-separate probes: Windows PowerShell pipes CRLF to native stdin, and
    # git would otherwise read the CR as part of each path.
    $matcherOutput = (($probes -join "`0") + "`0") |
        & git "--git-dir=$ignoreGitDir" "--work-tree=$ignoreWorkTree" -c "core.excludesFile=$($normalizedIgnore.Replace('\', '/'))" -c core.ignoreCase=true -c core.quotePath=false check-ignore --no-index --stdin -z 2>$null
    $exitCode = $LASTEXITCODE
    Remove-Item -LiteralPath $normalizedIgnore -Force -ErrorAction SilentlyContinue
    $ignored = @((@($matcherOutput) -join '') -split "`0" | Where-Object { $_ })
    $ErrorActionPreference = 'Stop'
} catch {
    Block-Tool ("The .agentreadonly guard failed while matching paths: " + $_.Exception.Message)
}

if ($exitCode -eq 1) { exit 0 }
if ($exitCode -ne 0 -or $ignored.Count -eq 0) {
    Block-Tool 'The .agentreadonly matcher failed.'
}

$hit = [string]$ignored[0]
$candidate = if ($probeToCandidate.ContainsKey($hit)) { $probeToCandidate[$hit] } else { $hit }
Block-Tool "Path '$candidate' is read-only by .agentreadonly policy."
