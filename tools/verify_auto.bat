@echo off
rem Rebuild, restart the monitor on the default folder, start 3 plain demo jobs, and log what the monitor records.
rem Close the running monitor first (bar right-click - exit).
cd /d "%~dp0.."
call build.bat
start "" "src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe"
call tools\run_plain_demo.bat
"src\TaskMonitor.Check\bin\Release\net10.0\TaskMonitor.Check.exe" --detail --watch 8 --count 8 > .taskmonitor_test\auto_check.log 2>&1
