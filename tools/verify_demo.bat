@echo off
rem Verification helper. Uses a separate state folder (.taskmonitor_test).
rem   tools\verify_demo.bat [speed] [skipselftest]
rem Logs: .taskmonitor_test\selftest.log, run_demo.log, check.log
cd /d "%~dp0.."
set "ROOT=%CD%\.taskmonitor_test"
set "SPEED=1.5"
if not "%~1"=="" set "SPEED=%~1"
if not exist "%ROOT%" mkdir "%ROOT%"
set PYTHONIOENCODING=utf-8
call "%~dp0find_python.bat"
echo %PY% > "%ROOT%\python_path.log"
%PY% --version >> "%ROOT%\python_path.log" 2>&1
if /i not "%~2"=="skipselftest" (
  %PY% demo\selftest.py --speed 8 > "%ROOT%\selftest.log" 2>&1
  call echo SELFTEST_EXIT=%%ERRORLEVEL%%>> "%ROOT%\selftest.log"
)
start "" "src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe" --dir "%ROOT%" --show
%PY% demo\run_demo.py --root "%ROOT%" --out "%ROOT%\demo_out" --speed %SPEED% > "%ROOT%\run_demo.log" 2>&1
"src\TaskMonitor.Check\bin\Release\net10.0\TaskMonitor.Check.exe" --dir "%ROOT%" --watch 5 --count 40 --detail > "%ROOT%\check.log" 2>&1
