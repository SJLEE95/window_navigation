@echo off
rem TaskMonitor Release build. Output log: build.log
cd /d "%~dp0"
echo [%date% %time%] build start > build.log
dotnet --version >> build.log 2>&1
dotnet build TaskMonitor.slnx -c Release -nologo >> build.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build.log
