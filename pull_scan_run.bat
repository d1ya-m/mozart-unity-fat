@echo off
REM Double-click THIS file to pull the newest Quest room scan.
REM It just launches pull_scan.ps1 (PowerShell handles the paths reliably).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pull_scan.ps1"
