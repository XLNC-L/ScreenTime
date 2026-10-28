// main.cpp — ScreenTime 极简版（Win32/WTL）入口
//
// M1 骨架：单实例 + 托盘常驻 + Mica 主窗口。
// 构建（Release）：
//   "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
//   msbuild ScreenTime.Win32.vcxproj /p:Configuration=Release /p:Platform=x64

#include <windows.h>
#include <atlbase.h>
#include <atlapp.h>
#include <atltypes.h>
#include <atlwin.h>
#include "MainFrame.h"

CAppModule _Module;

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR, int)
{
    // 单实例：已有实例直接退出（不重复起托盘）
    HANDLE hMutex = ::CreateMutexW(nullptr, TRUE, L"ScreenTime_Native_SingleInstance");
    if (hMutex == nullptr || ::GetLastError() == ERROR_ALREADY_EXISTS)
        return 0;

    // 高 DPI：Per-Monitor V2，图表/文字清晰度基础
    ::SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    // COM + WTL 初始化
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    _Module.Init(nullptr, hInstance);

    // 主窗口（默认托盘常驻隐藏；--show 用于开发期直接看界面）
    MainFrame wnd;
    CRect rc(0, 0, 1100, 700);
    wnd.Create(
        nullptr,                      // 父窗口
        rc,                           // 初始矩形
        L"ScreenTime",                // 窗口名
        WS_OVERLAPPEDWINDOW,          // 样式
        WS_EX_APPWINDOW,              // 扩展样式
        (UINT)0);                     // 菜单/ID（_U_MENUorID），窗口类自动注册
    const bool showNow = (wcsstr(::GetCommandLineW(), L"--show") != nullptr);
    wnd.ShowWindow(showNow ? SW_SHOW : SW_HIDE);

    // 消息循环
    MSG msg;
    while (::GetMessageW(&msg, nullptr, 0, 0))
    {
        ::TranslateMessage(&msg);
        ::DispatchMessageW(&msg);
    }

    _Module.Term();
    ::CoUninitialize();
    ::CloseHandle(hMutex);
    return (int)msg.wParam;
}
