@echo off
rem Diagnose auto-detection: builds into .taskmonitor_test\cbin and prints what the detector sees.
cd /d "%~dp0.."
dotnet build src\TaskMonitor.Check\TaskMonitor.Check.csproj -c Release -o .taskmonitor_test\cbin -nologo > .taskmonitor_test\diag_build.log 2>&1
".taskmonitor_test\cbin\TaskMonitor.Check.exe" --discover "%CD%\.taskmonitor_test\diag" > .taskmonitor_test\diag.log 2>&1
