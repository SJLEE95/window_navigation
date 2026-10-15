@echo off
rem Auto-detect demo: starts 3 plain Python jobs that write no state files.
cd /d "%~dp0.."
call "%~dp0find_python.bat"
if not exist ".taskmonitor_test" mkdir ".taskmonitor_test"
%PY% demo\run_plain_demo.py --out "%CD%\.taskmonitor_test\demo_out" > "%CD%\.taskmonitor_test\run_plain_demo.log" 2>&1
