@echo off
rem Installs the latest Revit Text QC for every Revit version on this PC.
rem Double-click this file with Revit closed. No administrator rights and nothing else to install.
title Revit Text QC installer
echo Installing Revit Text QC...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f = Join-Path $env:TEMP 'install-textqc.ps1'; Invoke-WebRequest 'https://github.com/Surajprem7/RevitTextQC/releases/latest/download/install.ps1' -OutFile $f -UseBasicParsing; try { & $f } finally { Remove-Item $f -Force -ErrorAction SilentlyContinue }"
echo.
pause
