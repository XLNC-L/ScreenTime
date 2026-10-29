@echo off
rem ScreenTime 构建 + 发布脚本
rem 用法：直接双击，或在终端执行 build.cmd
rem
rem v1.2.0 起是双进程架构：ScreenTime.WinUI（界面）+ ScreenTime.Agent（采集+托盘），
rem 两个都要发到同一个目录，否则托盘和采集会缺文件。
setlocal
cd /d "%~dp0"

set CONFIG=Release
set OUT=%~dp0ScreenTime-WinUI

echo [1/3] 编译 ScreenTime.WinUI (%CONFIG%) ...
dotnet build ScreenTime.WinUI\ScreenTime.WinUI.csproj -c %CONFIG% --nologo
if errorlevel 1 goto fail

echo.
echo [2/3] 编译 ScreenTime.Agent (%CONFIG%) ...
dotnet build ScreenTime.Agent\ScreenTime.Agent.csproj -c %CONFIG% --nologo
if errorlevel 1 goto fail

echo.
echo [3/3] 发布到 %OUT% ...
rem 框架依赖 + WindowsAppSDK 自带运行时：目标机需要安装 .NET 8 桌面运行时
dotnet publish ScreenTime.WinUI\ScreenTime.WinUI.csproj -c %CONFIG% -r win-x64 ^
    --self-contained false -o "%OUT%" --nologo
if errorlevel 1 goto fail
dotnet publish ScreenTime.Agent\ScreenTime.Agent.csproj -c %CONFIG% -r win-x64 ^
    --self-contained false -o "%OUT%" --nologo
if errorlevel 1 goto fail

echo.
echo 构建完成：
echo   UI    %OUT%\ScreenTime.exe
echo   Agent %OUT%\ScreenTime.Agent.exe
echo.
echo 打安装包（需要 Inno Setup 6.3+）：
echo   ISCC.exe /DAppVersion=x.y.z installer\ScreenTime.iss
goto :eof

:fail
echo.
echo 构建失败，请检查上面的错误信息。
exit /b 1
