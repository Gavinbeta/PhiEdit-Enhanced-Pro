@echo off
REM PhiEdit Enhanced Pro 构建脚本
REM Windows App SDK 的 XAML 编译器在控制台代码页非 UTF-8 时会静默失败，
REM 因此先切换到 UTF-8 代码页再执行构建。
chcp 65001 >nul
setlocal
cd /d "%~dp0"

dotnet build PhiEdit.sln -c Debug %*
if errorlevel 1 (
    echo.
    echo 构建失败。
    exit /b 1
)

echo.
echo 构建成功。可执行文件：
echo   src\PhiEdit.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\PhiEdit.App.exe
exit /b 0
