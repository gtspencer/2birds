$input = $args[0] | ConvertFrom-Json
$cmd = $input.tool_input.command

$blocked = @(
  '^git\s+commit',
  '^git\s+push',
  '^git\s+merge',
  '^git\s+rebase',
  '^git\s+cherry-pick',
  '^git\s+revert',
  '^git\s+tag',
  '^git\s+reset',
  '^git\s+checkout\s',
  '^git\s+restore',
  '^git\s+clean',
  '^git\s+stash',
  '^git\s+am\b',
  '^git\s+apply',
  '^git\s+mv',
  '^git\s+rm',
  '^git\s+branch\s+-[dDmM]',
  '^git\s+add',
  '^git\s+init'
)

foreach ($pattern in $blocked) {
  if ($cmd -match $pattern) {
    Write-Output '{"decision":"block","reason":"Git write operations are blocked. Only read-only git commands (status, log, diff, show, blame, etc.) are allowed."}'
    exit 0
  }
}
