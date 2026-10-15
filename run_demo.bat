@echo off
rem Demo: start the monitor and launch 8 demo tasks in the background.
rem   run_demo.bat [--speed 2]
cd /d "%~dp0"
set "EXE=src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe"
if not exist "%EXE%" set "EXE=src\TaskMonitor\bin\Debug\net10.0-windows\TaskMonitor.exe"
if exist "%EXE%" (start "" "%EXE%" --show) else (echo TaskMonitor.exe not found. Run build.bat first.)
set PYTHONIOENCODING=utf-8
call "%~dp0tools\find_python.bat"
%PY% demo\run_demo.py %*
