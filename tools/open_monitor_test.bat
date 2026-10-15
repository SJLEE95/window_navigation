@echo off
rem Opens the monitor on the verification state folder (.taskmonitor_test).
cd /d "%~dp0.."
start "" "src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe" --dir "%CD%\.taskmonitor_test" --show
