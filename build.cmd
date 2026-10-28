@echo off
rem ScreenTime 构建 + 发布脚本
rem 用法：直接双击，或在终端执行 build.cmd
setlocal
cd /d "%~dp0"

set CONFIG=Release
set OUT=%~dp0ScreenTime-WinUI

echo [1/2] 编译 ScreenTime.WinUI (%CONFIG%) ...
dotnet build ScreenTime.WinUI\ScreenTime.WinUI.csproj -c %CONFIG% --nologo
if errorlevel 1 goto fail

echo.
echo [2/2] 发布到 %OUT% ...
rem 框架依赖 + WindowsAppSDK 自带运行时：目标机需要安装 .NET 8 桌面运行时
dotnet publish ScreenTime.WinUI\ScreenTime.WinUI.csproj -c %CONFIG% -r win-x64 ^
    --self-contained false -o "%OUT%" --nologo
if errorlevel 1 goto fail

echo.
echo 构建完成：%OUT%\ScreenTime.exe
goto :eof

:fail
echo.
echo 构建失败，请检查上面的错误信息。
exit /b 1
