@echo off
rem Shows how the monitor reads a job folder's own status files (status.json, manifest.json, ...).
rem Usage: drag a batch/output folder onto this file, or: tools\diag_workstatus.bat "C:\path\to\batch"
rem Log: .taskmonitor_test\workstatus.log
cd /d "%~dp0.."
if not exist ".taskmonitor_test" mkdir ".taskmonitor_test"
dotnet build src\TaskMonitor.Check\TaskMonitor.Check.csproj -c Release -o .taskmonitor_test\cbin -nologo > .taskmonitor_test\workstatus_build.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> .taskmonitor_test\workstatus_build.log
".taskmonitor_test\cbin\TaskMonitor.Check.exe" --workstatus "%~1" > .taskmonitor_test\workstatus.log 2>&1
