@echo off
rem 데모: 모니터를 띄우고 데모 작업 8개를 백그라운드로 실행합니다.
chcp 65001 >nul
cd /d "%~dp0"
set EXE=src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe
if not exist "%EXE%" set EXE=src\TaskMonitor\bin\Debug\net10.0-windows\TaskMonitor.exe
if exist "%EXE%" (start "" "%EXE%" --show) else (echo TaskMonitor.exe 가 없습니다. 먼저 build.ps1 로 빌드하세요.)
python demo\run_demo.py %*
