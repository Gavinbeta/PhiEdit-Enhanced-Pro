@echo off
REM 运行 PhiEdit Enhanced Pro
chcp 65001 >nul
setlocal
cd /d "%~dp0"
start "" "src\PhiEdit.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\PhiEdit.App.exe"
exit /b 0
