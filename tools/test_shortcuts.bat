@echo off
rem Build the console checker into .taskmonitor_test\cbin (does not touch the running monitor)
rem and run the shortcut self-test. Log: .taskmonitor_test\shortcut_test.log
cd /d "%~dp0.."
if not exist ".taskmonitor_test" mkdir ".taskmonitor_test"
dotnet build src\TaskMonitor.Check\TaskMonitor.Check.csproj -c Release -o .taskmonitor_test\cbin -nologo > .taskmonitor_test\shortcut_build.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> .taskmonitor_test\shortcut_build.log
".taskmonitor_test\cbin\TaskMonitor.Check.exe" --shortcut-selftest "%CD%\.taskmonitor_test\shortcut_test" > .taskmonitor_test\shortcut_test.log 2>&1
