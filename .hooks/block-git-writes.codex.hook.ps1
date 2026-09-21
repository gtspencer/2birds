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
        Block-Codex 'The Git policy hook received no hook input.'
    }
    $data = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    Block-Codex ("The Git policy hook could not parse hook input: " + $_.Exception.Message)
}

$toolName = [string](Get-PropertyValue $data 'tool_name')
if ($toolName -ne 'Bash') {
    exit 0
}

$toolInput = Get-PropertyValue $data 'tool_input'
$command = [string](Get-PropertyValue $toolInput 'command')
if ([string]::IsNullOrWhiteSpace($command)) {
    exit 0
}

function Split-Tokens {
    param([string]$Text)

    $result = @()
    foreach ($match in [regex]::Matches($Text, '"([^"]*)"|''([^'']*)''|(\S+)')) {
        if ($match.Groups[1].Success) {
            $token = $match.Groups[1].Value
        } elseif ($match.Groups[2].Success) {
            $token = $match.Groups[2].Value
        } else {
            $token = $match.Groups[3].Value
        }

        $token = $token.Trim()
        $token = $token.Trim([char[]]@('"', "'", '`'))
        $token = $token.TrimEnd([char[]]@(')', ']', '}', ';', ','))
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $result += $token
        }
    }

    return $result
}

function Get-GitInvocation {
    param([string]$ArgumentText)

    $tokens = @(Split-Tokens $ArgumentText)
    if ($tokens.Count -eq 0) {
        return [pscustomobject]@{ Subcommand = ''; Args = @() }
    }

    $i = 0
    while ($i -lt $tokens.Count) {
        $token = [string]$tokens[$i]
        $lower = $token.ToLowerInvariant()

        # Global read-only meta options.
        if ($lower -eq '--version' -or $lower -eq '--help' -or $lower -eq '-h') {
            return [pscustomobject]@{ Subcommand = '__meta__'; Args = @() }
        }

        # Global options that consume the following argument.
        if ($lower -eq '-c' -or $lower -eq '-C' -or
            $lower -eq '--git-dir' -or $lower -eq '--work-tree' -or
            $lower -eq '--namespace' -or $lower -eq '--super-prefix' -or
            $lower -eq '--exec-path' -or $lower -eq '--config-env') {
            $i += 2
            continue
        }

        # Global options whose value is attached with '='.
        if ($lower -match '^--(?:git-dir|work-tree|namespace|super-prefix|exec-path|config-env)=') {
            $i++
            continue
        }

        # Other global flags, for example --no-pager or --literal-pathspecs.
        if ($lower.StartsWith('-')) {
            $i++
            continue
        }

        $remaining = @()
        if (($i + 1) -lt $tokens.Count) {
            $remaining = @($tokens[($i + 1)..($tokens.Count - 1)])
        }

        return [pscustomobject]@{
            Subcommand = $lower
            Args = $remaining
        }
    }

    return [pscustomobject]@{ Subcommand = ''; Args = @() }
}

function Test-ReadOnlyBranch {
    param([object[]]$GitArguments)

    if ($GitArguments.Count -eq 0) { return $true }

    $lower = @($GitArguments | ForEach-Object { ([string]$_).ToLowerInvariant() })

    # Common listing-only forms.
    if ($lower.Count -eq 1 -and
        ($lower[0] -eq '--show-current' -or
         $lower[0] -eq '--list' -or
         $lower[0] -eq '-a' -or
         $lower[0] -eq '--all' -or
         $lower[0] -eq '-r' -or
         $lower[0] -eq '--remotes' -or
         $lower[0] -eq '-v' -or
         $lower[0] -eq '-vv' -or
         $lower[0] -eq '--verbose')) {
        return $true
    }

    # --list explicitly keeps following positional arguments in listing mode.
    if ($lower -contains '--list') {
        $dangerous = @(
            '-d', '-D', '--delete', '-m', '-M', '--move', '-c', '-C', '--copy',
            '--edit-description', '--set-upstream-to', '--unset-upstream',
            '--create-reflog', '--track', '--no-track', '-f', '--force'
        )
        foreach ($arg in $GitArguments) {
            $value = [string]$arg
            if ($dangerous -contains $value) { return $false }
            if ($value -match '^(--set-upstream-to|--track|--no-track)=') { return $false }
        }
        return $true
    }

    # -a / --all / -r / --remotes are listing modes. Only permit additional
    # output-format flags, not arbitrary positional branch names.
    if ($lower -contains '-a' -or $lower -contains '--all' -or
        $lower -contains '-r' -or $lower -contains '--remotes') {
        foreach ($arg in $lower) {
            if ($arg -notin @('-a', '--all', '-r', '--remotes', '-v', '-vv', '--verbose', '--no-color', '--color')) {
                return $false
            }
        }
        return $true
    }

    return $false
}

function Test-ReadOnlyTag {
    param([object[]]$GitArguments)

    if ($GitArguments.Count -eq 0) { return $true }
    $first = ([string]$GitArguments[0]).ToLowerInvariant()
    return ($first -eq '-l' -or $first -eq '--list')
}

function Test-ReadOnlyRemote {
    param([object[]]$GitArguments)

    if ($GitArguments.Count -eq 0) { return $true }
    $first = ([string]$GitArguments[0]).ToLowerInvariant()

    if ($GitArguments.Count -eq 1 -and ($first -eq '-v' -or $first -eq '--verbose')) {
        return $true
    }

    return ($first -eq 'get-url' -or $first -eq 'show')
}

function Test-ReadOnlyConfig {
    param([object[]]$GitArguments)

    if ($GitArguments.Count -eq 0) { return $true }

    $writeFlags = @(
        '--add', '--replace-all', '--unset', '--unset-all', '--rename-section',
        '--remove-section', '--edit', '-e'
    )
    foreach ($arg in $GitArguments) {
        $lower = ([string]$arg).ToLowerInvariant()
        if ($writeFlags -contains $lower) { return $false }
    }

    $readActionFlags = @(
        '--get', '--get-all', '--get-regexp', '--get-urlmatch',
        '--list', '-l', '--get-color', '--get-colorbool'
    )
    foreach ($arg in $GitArguments) {
        $lower = ([string]$arg).ToLowerInvariant()
        if ($readActionFlags -contains $lower) { return $true }
    }

    # Strip options and their known option-values, then count positional args.
    # `git config key` reads; `git config key value` writes.
    $positionals = @()
    $i = 0
    while ($i -lt $GitArguments.Count) {
        $arg = [string]$GitArguments[$i]
        $lower = $arg.ToLowerInvariant()

        if ($lower -in @('--file', '-f', '--blob', '--type')) {
            $i += 2
            continue
        }
        if ($lower -match '^--(?:file|blob|type)=') {
            $i++
            continue
        }
        if ($lower.StartsWith('-')) {
            $i++
            continue
        }

        $positionals += $arg
        $i++
    }

    return ($positionals.Count -le 1)
}

function Test-ReadOnlyGitInvocation {
    param([string]$Subcommand, [object[]]$GitArguments)

    if ([string]::IsNullOrWhiteSpace($Subcommand) -or $Subcommand -eq '__meta__') {
        return $true
    }

    $alwaysReadOnly = @(
        'status', 'log', 'diff', 'show', 'blame', 'grep',
        'ls-files', 'ls-tree', 'rev-parse', 'rev-list', 'cat-file',
        'name-rev', 'shortlog', 'merge-base', 'for-each-ref', 'show-ref',
        'check-ignore', 'check-attr', 'check-mailmap', 'describe',
        'range-diff', 'cherry', 'patch-id', 'diff-tree', 'diff-files',
        'diff-index', 'show-index', 'count-objects', 'help', 'version', 'var'
    )

    if ($alwaysReadOnly -contains $Subcommand) { return $true }

    switch ($Subcommand) {
        'branch' {
            return (Test-ReadOnlyBranch $GitArguments)
        }
        'tag' {
            return (Test-ReadOnlyTag $GitArguments)
        }
        'remote' {
            return (Test-ReadOnlyRemote $GitArguments)
        }
        'config' {
            return (Test-ReadOnlyConfig $GitArguments)
        }
        'stash' {
            if ($GitArguments.Count -eq 0) { return $false }
            $first = ([string]$GitArguments[0]).ToLowerInvariant()
            return ($first -eq 'list' -or $first -eq 'show')
        }
        'worktree' {
            if ($GitArguments.Count -eq 0) { return $false }
            return (([string]$GitArguments[0]).ToLowerInvariant() -eq 'list')
        }
        'submodule' {
            if ($GitArguments.Count -eq 0) { return $true }
            $first = ([string]$GitArguments[0]).ToLowerInvariant()
            return ($first -eq 'status' -or $first -eq 'summary')
        }
        'reflog' {
            if ($GitArguments.Count -eq 0) { return $true }
            return (([string]$GitArguments[0]).ToLowerInvariant() -eq 'show')
        }
        'notes' {
            if ($GitArguments.Count -eq 0) { return $false }
            $first = ([string]$GitArguments[0]).ToLowerInvariant()
            return ($first -eq 'list' -or $first -eq 'show')
        }
        default {
            # Unknown commands and aliases are blocked so a Git alias cannot
            # bypass the policy by pointing at commit/reset/etc.
            return $false
        }
    }
}

# Find every git invocation in a chained shell command. This catches forms
# such as `git status && git commit`, `cmd /c git commit`, `git.exe push`,
# and absolute paths ending in git.exe.
$gitMatches = [regex]::Matches(
    $command,
    '(?is)(?<![A-Za-z0-9_.-])git(?:\.exe)?["'']?(?=\s|$)(?<args>[^;&|\r\n]*)'
)

foreach ($match in $gitMatches) {
    $argumentText = [string]$match.Groups['args'].Value
    $invocation = Get-GitInvocation $argumentText

    if (-not (Test-ReadOnlyGitInvocation $invocation.Subcommand $invocation.Args)) {
        $display = if ([string]::IsNullOrWhiteSpace($invocation.Subcommand)) { 'git' } else { 'git ' + $invocation.Subcommand }
        Block-Codex "$display is blocked. Only explicitly read-only Git operations are allowed."
    }
}

exit 0
