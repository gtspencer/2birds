@echo off
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0block-git-writes.codex.hook.ps1"
exit /b %ERRORLEVEL%