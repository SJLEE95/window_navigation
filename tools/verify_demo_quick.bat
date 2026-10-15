@echo off
rem Same as verify_demo.bat but skips the Python selftest.
call "%~dp0verify_demo.bat" 1.5 skipselftest
