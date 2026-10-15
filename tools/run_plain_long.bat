@echo off
rem Starts one plain Python job (no state file) for ~2 minutes, to check auto-detection.
cd /d "%~dp0.."
call "%~dp0find_python.bat"
if not exist ".taskmonitor_test\demo_out\plain" mkdir ".taskmonitor_test\demo_out\plain"
start "plain sample12" /min %PY% demo\plain_job.py --input sample12 --log "%CD%\.taskmonitor_test\demo_out\plain\sample12.log" --frames 600
