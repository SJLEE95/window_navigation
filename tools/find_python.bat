@echo off
rem Sets PY to a usable python.exe (PATH -> py launcher -> common install folders).
set "PY="
for %%P in (python.exe) do if not "%%~$PATH:P"=="" set "PY=%%~$PATH:P"
if defined PY goto :eof
where py >nul 2>&1 && (set "PY=py -3" & goto :eof)
for /d %%D in ("%LOCALAPPDATA%\Programs\Python\Python3*") do if exist "%%D\python.exe" set "PY=%%D\python.exe"
if defined PY goto :eof
if exist "%USERPROFILE%\anaconda3\python.exe" set "PY=%USERPROFILE%\anaconda3\python.exe"
