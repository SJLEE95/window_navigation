@echo off
rem Start TaskMonitor (default state folder %LOCALAPPDATA%\TaskMonitor) and open the list window.
cd /d "%~dp0"
start "" "src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe" --show
