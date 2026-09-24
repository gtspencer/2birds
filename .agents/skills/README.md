# Agent Skills

This folder is the single source of truth for agent skills. Each skill is a folder containing a `SKILL.md`.

- **Codex** reads `.agents/skills` directly.
- **Claude Code** reads `.claude/skills`, which is a symlink to `../.agents/skills`.

Only edit skills here. Never copy them into `.claude/skills`.

## Setup after cloning (Windows)

The symlink is committed to git, but Windows only checks it out as a real link if symlinks are enabled:

1. Turn on **Developer Mode** (Settings > System > For developers).
2. Enable symlinks for this repo and re-checkout the link:

   ```powershell
   git config core.symlinks true
   Remove-Item .claude\skills -Force
   git checkout -- .claude/skills
   ```

3. Verify it resolves:

   ```powershell
   Get-Item .claude\skills | Select-Object LinkType, Target
   # LinkType: SymbolicLink, Target: ..\.agents\skills
   ```

If `.claude\skills` is a small text file containing `../.agents/skills`, symlinks weren't enabled when you cloned. Repeat step 2.

### Fallback: junction (no Developer Mode)

A junction works locally without Developer Mode, but it shows up as a change in git, so don't commit it:

```powershell
Remove-Item .claude\skills -Force
New-Item -ItemType Junction -Path .claude\skills -Target .agents\skills
git update-index --assume-unchanged .claude/skills
```

## macOS / Linux

Git creates the symlink automatically, so no setup is needed.

## Adding a skill

Create `.agents/skills/<skill-name>/SKILL.md` with `name` and `description` frontmatter. Both tools pick it up automatically.
