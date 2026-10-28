// MainFrame.h — 主窗口（WTL）
//
// M2 阶段一：Direct2D 自绘主界面（环形图）。托盘事件转发保持 M1 逻辑。

#pragma once

#include <atlframe.h>
#include <atlctrls.h>
#include <dwmapi.h>
#include "TrayIcon.h"
#include "D2DContext.h"
#include "DonutChart.h"
#include "Timeline.h"
#include "RankList.h"

class MainFrame : public CWindowImpl<MainFrame>
{
public:
    DECLARE_WND_CLASS_EX(L"ScreenTimeMainWnd", CS_HREDRAW | CS_VREDRAW, -1)

    BEGIN_MSG_MAP(MainFrame)
        // 系统消息优先注册，避免被自定义消息号抢占
        MESSAGE_HANDLER(WM_CREATE, OnCreate)
        MESSAGE_HANDLER(WM_SIZE, OnSize)
        MESSAGE_HANDLER(WM_ERASEBKGND, OnEraseBkgnd)
        MESSAGE_HANDLER(WM_CLOSE, OnClose)
        MESSAGE_HANDLER(WM_DESTROY, OnDestroy)
        MESSAGE_HANDLER(WM_PAINT, OnPaint)
        MESSAGE_HANDLER(TrayIcon::WM_TRAY, OnTray)
        MESSAGE_HANDLER(TrayIcon::kCmdOpen, OnOpen)
        MESSAGE_HANDLER(TrayIcon::kCmdExit, OnExit)
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

        // M2 阶段一：演示数据（后续接 SQLite 真实读取，空数据走占位图）
        if (!m_d2d.Create(m_hWnd))
        {
            // 记录失败原因到 %TEMP%\screen32_d2d.log，便于排查
            wchar_t tmpPath[MAX_PATH]{}, logPath[MAX_PATH]{};
            if (::GetEnvironmentVariableW(L"TEMP", tmpPath, MAX_PATH) > 0)
            {
                wcscpy_s(logPath, tmpPath);
                wcscat_s(logPath, L"\\screen32_d2d.log");
                wchar_t msg[160];
                swprintf_s(msg, L"CreateHwndRenderTarget HRESULT=0x%08X", m_d2d.LastHr());
                FILE* f = nullptr;
                if (_wfopen_s(&f, logPath, L"w") == 0 && f)
                {
                    fwprintf(f, L"%s\n", msg);
                    fclose(f);
                }
            }
            return -1; // D2D 不可用，窗口关闭（极简版依赖 D2D 自绘）
        }

        std::vector<DonutChart::Segment> segs;
        segs.push_back({ 46.f, Palette::Teal(),   L"Microsoft Edge" });
        segs.push_back({ 32.f, Palette::Violet(), L"WeChat" });
        segs.push_back({ 21.f, Palette::Amber(),  L"Doubao" });
        segs.push_back({ 12.f, Palette::Pink(),   L"IDM" });
        segs.push_back({ 9.f,  Palette::Slate(),  L"其他" });
        m_donut.SetSegments(std::move(segs));
        m_donut.SetEmpty(false);

        // 时间线演示时段（合计 120 分钟，与环形图一致；分钟 0-1440）
        std::vector<Timeline::TimeSlot> slots;
        slots.push_back({ 550.f, 576.f, Palette::Teal(),   L"Microsoft Edge" });
        slots.push_back({ 600.f, 632.f, Palette::Violet(), L"WeChat" });
        slots.push_back({ 750.f, 762.f, Palette::Pink(),   L"IDM" });
        slots.push_back({ 860.f, 881.f, Palette::Amber(),  L"Doubao" });
        slots.push_back({ 1140.f, 1149.f, Palette::Slate(), L"其他" });
        slots.push_back({ 1205.f, 1225.f, Palette::Teal(), L"Microsoft Edge" });
        m_timeline.SetSlots(std::move(slots));
        m_timeline.SetEmpty(false);

        // 排行演示数据
        std::vector<RankList::RankItem> ranks;
        ranks.push_back({ L"Microsoft Edge", 46.f, Palette::Teal() });
        ranks.push_back({ L"WeChat", 32.f, Palette::Violet() });
        ranks.push_back({ L"Doubao", 21.f, Palette::Amber() });
        ranks.push_back({ L"IDM", 12.f, Palette::Pink() });
        ranks.push_back({ L"其他", 9.f, Palette::Slate() });
        m_rank.SetItems(std::move(ranks));
        m_rank.SetEmpty(false);
        return 0;
    }

    LRESULT OnSize(UINT, WPARAM, LPARAM lParam, BOOL&)
    {
        const UINT cx = LOWORD(lParam);
        const UINT cy = HIWORD(lParam);
        m_d2d.Resize(cx, cy);
        ::InvalidateRect(m_hWnd, nullptr, TRUE);
        return 0;
    }

    // 禁用系统擦背景，避免闪烁（D2D 自己 Clear）
    LRESULT OnEraseBkgnd(UINT, WPARAM, LPARAM, BOOL&)
    {
        return TRUE;
    }

    LRESULT OnClose(UINT, WPARAM, LPARAM, BOOL&)
    {
        // M1 阶段关闭即退出；后续可改为「最小化到托盘」
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
        PAINTSTRUCT ps;
        ::BeginPaint(m_hWnd, &ps);

        if (m_d2d.RT())
        {
            m_d2d.Begin();
            m_d2d.Clear();

            // 三栏布局（8px 网格边距 24px）：
            //   左栏 = 环形图，右栏上 = 时间线，右栏下 = 排行列表
            RECT rc;
            ::GetClientRect(m_hWnd, &rc);
            const FLOAT L = (FLOAT)rc.left + 24.f;
            const FLOAT T = (FLOAT)rc.top + 24.f;
            const FLOAT R = (FLOAT)rc.right - 24.f;
            const FLOAT B = (FLOAT)rc.bottom - 24.f;

            const FLOAT leftW = (R - L) * 0.40f;    // 左栏宽
            const FLOAT rightX = L + leftW + 24.f;  // 右栏起点
            const FLOAT timeH = (B - T) * 0.52f;    // 时间线区高

            const D2D1_RECT_F donutBounds = { L, T, L + leftW, B };
            const D2D1_RECT_F timelineBounds = { rightX, T, R, T + timeH };
            const D2D1_RECT_F rankBounds = { rightX, T + timeH + 24.f, R, B };

            m_donut.CalculateLayout(donutBounds);
            m_donut.Render(m_d2d);

            m_timeline.CalculateLayout(timelineBounds);
            m_timeline.Render(m_d2d);

            m_rank.CalculateLayout(rankBounds);
            m_rank.Render(m_d2d);

            m_d2d.End();
        }
        else
        {
            // 兜底：D2D 初始化失败时的简单 GDI 文案
            RECT rc;
            ::GetClientRect(m_hWnd, &rc);
            HDC hdc = ::GetDC(m_hWnd);
            ::SetBkColor(hdc, RGB(18, 21, 28));
            ::SetTextColor(hdc, RGB(220, 220, 220));
            ::DrawTextW(hdc, L"Direct2D 初始化失败", -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            ::ReleaseDC(m_hWnd, hdc);
        }

        ::EndPaint(m_hWnd, &ps);
        return 0;
    }

private:
    TrayIcon m_tray;
    D2DContext m_d2d;
    DonutChart m_donut;
    Timeline m_timeline;
    RankList m_rank;

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
