@echo off
rem Builds a separate copy into .taskmonitor_test\bin (works even while the main TaskMonitor.exe is running)
rem and starts it on the .taskmonitor_test state folder.
cd /d "%~dp0.."
if not exist ".taskmonitor_test" mkdir ".taskmonitor_test"
dotnet build src\TaskMonitor\TaskMonitor.csproj -c Release -o .taskmonitor_test\bin -nologo > .taskmonitor_test\build_test.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> .taskmonitor_test\build_test.log
if exist ".taskmonitor_test\bin\TaskMonitor.exe" start "" ".taskmonitor_test\bin\TaskMonitor.exe" --dir "%CD%\.taskmonitor_test" --show
