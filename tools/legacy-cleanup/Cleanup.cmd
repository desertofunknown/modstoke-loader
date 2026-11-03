@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Remove-LegacyModstoke.ps1" -Apply
pause
