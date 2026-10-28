// MainFrame.h — 主窗口（WTL）
// 极简版 M1：Mica 背景 + 托盘事件转发。今日页内容 M2 开始填充。
#pragma once

#include <atlframe.h>
#include <atlctrls.h>
#include <dwmapi.h>
#include "TrayIcon.h"

class MainFrame : public CWindowImpl<MainFrame>
{
public:
    DECLARE_WND_CLASS_EX(L"ScreenTimeMainWnd", CS_HREDRAW | CS_VREDRAW, -1)

    BEGIN_MSG_MAP(MainFrame)
        MESSAGE_HANDLER(TrayIcon::WM_TRAY, OnTray)
        MESSAGE_HANDLER(TrayIcon::kCmdOpen, OnOpen)
        MESSAGE_HANDLER(TrayIcon::kCmdExit, OnExit)
        MESSAGE_HANDLER(WM_CREATE, OnCreate)
        MESSAGE_HANDLER(WM_CLOSE, OnClose)
        MESSAGE_HANDLER(WM_DESTROY, OnDestroy)
        MESSAGE_HANDLER(WM_PAINT, OnPaint)
    END_MSG_MAP()

    LRESULT OnTray(UINT, WPARAM wParam, LPARAM lParam, BOOL&)
    {
        return m_tray.OnTray(TrayIcon::WM_TRAY, wParam, lParam);
    }

    LRESULT OnOpen(UINT, WPARAM, LPARAM, BOOL&)
    {
        ::ShowWindow(m_hWnd, SW_SHOW);
        ::SetForegroundWindow(m_hWnd);
        return 0;
    }

    LRESULT OnExit(UINT, WPARAM, LPARAM, BOOL&)
    {
        ::PostMessageW(m_hWnd, WM_CLOSE, 0, 0);
        return 0;
    }

    LRESULT OnCreate(UINT, WPARAM, LPARAM, BOOL&)
    {
        ApplyMica();
        m_tray.Create(m_hWnd);
        return 0;
    }

    LRESULT OnClose(UINT, WPARAM, LPARAM, BOOL&)
    {
        // M1：关闭即退出；后续可改为「关闭最小化到托盘」
        ::DestroyWindow(m_hWnd);
        return 0;
    }

    LRESULT OnDestroy(UINT, WPARAM, LPARAM, BOOL&)
    {
        m_tray.Destroy();
        ::PostQuitMessage(0);
        return 0;
    }

    LRESULT OnPaint(UINT, WPARAM, LPARAM, BOOL&)
    {
        // M1 占位：深色背景上画一行文字，M2 换成 Direct2D 自绘
        PAINTSTRUCT ps;
        HDC hdc = ::BeginPaint(m_hWnd, &ps);
        RECT rc;
        ::GetClientRect(m_hWnd, &rc);
        ::FillRect(hdc, &rc, (HBRUSH)::GetStockObject(BLACK_BRUSH));
        ::SetTextColor(hdc, RGB(220, 220, 220));
        ::SetBkMode(hdc, TRANSPARENT);
        ::DrawTextW(hdc, L"ScreenTime 极简版 — M1 骨架", -1, &rc,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        ::EndPaint(m_hWnd, &ps);
        return 0;
    }

private:
    TrayIcon m_tray;

    // Win11 22000+：主窗口 Mica 背景 + 跟随深色
    void ApplyMica()
    {
        constexpr int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        constexpr int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        constexpr int DWMSBT_MAINWINDOW = 2;

        BOOL dark = TRUE;
        ::DwmSetWindowAttribute(m_hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
        ::DwmSetWindowAttribute(m_hWnd, DWMWA_SYSTEMBACKDROP_TYPE, &DWMSBT_MAINWINDOW, sizeof(DWMSBT_MAINWINDOW));
    }
};
