// TrayIcon.h — 系统托盘（Shell_NotifyIcon + 右键菜单）
// 极简版 M1：托盘常驻，左键双击打开主界面，右键菜单「打开主界面 / 退出」。
#pragma once

#include <shellapi.h>

class TrayIcon
{
public:
    // 主窗口通过 WM_TRAY 转发托盘事件；uID 固定为 1
    static constexpr UINT WM_TRAY = WM_APP + 20;
    static constexpr UINT kTrayId = 1;

    // 右键菜单项命令 ID（必须用 WM_APP 范围，避免与系统消息 WM_CREATE=1/WM_DESTROY=2 冲突）
    enum : UINT { kCmdOpen = WM_APP + 10, kCmdExit = WM_APP + 11 };

    void Create(HWND owner)
    {
        m_hWnd = owner;
        NOTIFYICONDATAW nid{};
        nid.cbSize = sizeof(nid);
        nid.hWnd = owner;
        nid.uID = kTrayId;
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAY;
        nid.hIcon = ::LoadIconW(nullptr, IDI_APPLICATION); // M1 用系统图标，后续换应用资源图标
        wcscpy_s(nid.szTip, L"ScreenTime 极简版");
        ::Shell_NotifyIconW(NIM_ADD, &nid);
    }

    void Destroy()
    {
        NOTIFYICONDATAW nid{};
        nid.cbSize = sizeof(nid);
        nid.hWnd = m_hWnd;
        nid.uID = kTrayId;
        ::Shell_NotifyIconW(NIM_DELETE, &nid);
    }

    // 处理 WM_TRAY：右键弹菜单，左键双击打开
    LRESULT OnTray(UINT uMsg, WPARAM wParam, LPARAM lParam)
    {
        if (uMsg != WM_TRAY || wParam != kTrayId)
            return 0;

        switch (LOWORD(lParam))
        {
        case WM_RBUTTONUP:
            ShowMenu();
            break;
        case WM_LBUTTONDBLCLK:
            ::PostMessageW(m_hWnd, kCmdOpen, 0, 0);
            break;
        }
        return 0;
    }

private:
    HWND m_hWnd = nullptr;

    void ShowMenu()
    {
        HMENU menu = ::CreatePopupMenu();
        ::AppendMenuW(menu, MF_STRING, kCmdOpen, L"打开主界面");
        ::AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        ::AppendMenuW(menu, MF_STRING, kCmdExit, L"退出");

        POINT pt;
        ::GetCursorPos(&pt);
        // 让菜单正确聚焦并支持键盘选择
        ::SetForegroundWindow(m_hWnd);
        UINT cmd = ::TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, 0, m_hWnd, nullptr);
        ::DestroyMenu(menu);

        if (cmd == kCmdOpen)
            ::PostMessageW(m_hWnd, kCmdOpen, 0, 0);
        else if (cmd == kCmdExit)
            ::PostMessageW(m_hWnd, kCmdExit, 0, 0);
    }
};
