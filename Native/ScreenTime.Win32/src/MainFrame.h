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
#include "PurposeBar.h"
#include "OverviewCards.h"

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

        // 分类占比（图例模式：小环形图 + 右侧图例）
        std::vector<DonutChart::Segment> segs;
        segs.push_back({ 46.f, Palette::Teal(),   L"Microsoft Edge" });
        segs.push_back({ 32.f, Palette::Violet(), L"WeChat" });
        segs.push_back({ 21.f, Palette::Amber(),  L"Doubao" });
        segs.push_back({ 12.f, Palette::Pink(),   L"IDM" });
        segs.push_back({ 9.f,  Palette::Slate(),  L"其他" });
        m_donut.SetSegments(std::move(segs));
        m_donut.SetEmpty(false);
        m_donut.SetLegendMode(true);

        // 用途分布
        std::vector<PurposeBar::PurposeItem> purposes;
        purposes.push_back({ L"社交媒体", 46.f, Palette::Blue() });
        purposes.push_back({ L"浏览网页", 37.f, Palette::Teal() });
        purposes.push_back({ L"办公效率", 10.f, Palette::Sage() });
        purposes.push_back({ L"视频媒体", 7.f,  Palette::Pink() });
        m_purpose.SetItems(std::move(purposes));

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

        // 概览四卡
        OverviewCards::Card cards[4] = {
            { L"今日总时长", L"2:00" },
            { L"前台时长",   L"1:50" },
            { L"后台播放",   L"0:10" },
            { L"亮屏时长",   L"3:20" },
        };
        m_cards.SetData(cards, 4);
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
            ID2D1RenderTarget* rt = m_d2d.RT();
            m_d2d.Begin();
            m_d2d.Clear();

            // 复刻原版今日页纵向布局（24px 边距）：
            //   页头 → 四概览卡 → 中部两列（左：分类占比+用途分布；右：应用排行）→ 时间线
            RECT rc;
            ::GetClientRect(m_hWnd, &rc);
            const FLOAT L = (FLOAT)rc.left + 24.f;
            const FLOAT T = (FLOAT)rc.top + 24.f;
            const FLOAT R = (FLOAT)rc.right - 24.f;
            const FLOAT B = (FLOAT)rc.bottom - 24.f;

            // 页头
            D2D1_RECT_F header = { L, T, R, T + 48.f };
            DrawHeader(header);

            // 四概览卡
            D2D1_RECT_F cards = { L, T + 56.f, R, T + 140.f };
            m_cards.CalculateLayout(cards);
            m_cards.Render(m_d2d);

            // 中部两列
            const FLOAT midTop = T + 164.f;
            const FLOAT midBot = T + 536.f;
            const FLOAT colGap = 16.f;
            const FLOAT leftColR = L + (R - L - colGap) * 0.46f;
            const FLOAT rightColL = leftColR + colGap;

            // 左列：分类占比（标题 + 环形图 + 图例）
            D2D1_RECT_F donutCard = { L, midTop, leftColR, midTop + 168.f };
            DrawCard(donutCard);
            D2D1_RECT_F donutTitle = { donutCard.left + 16.f, donutCard.top + 14.f,
                                       donutCard.right - 16.f, donutCard.top + 38.f };
            rt->DrawText(L"分类占比", 4, m_sectionFmt, donutTitle, m_textBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
            D2D1_RECT_F donutRing = { donutCard.left + 16.f, donutCard.top + 48.f,
                                      donutCard.left + 136.f, donutCard.top + 168.f };
            m_donut.CalculateLayout(donutRing);
            m_donut.Render(m_d2d);
            D2D1_RECT_F donutLegend = { donutRing.right + 12.f, donutCard.top + 48.f,
                                        donutCard.right - 16.f, donutCard.bottom - 12.f };
            m_donut.RenderLegend(m_d2d, donutLegend);

            // 左列：用途分布
            D2D1_RECT_F purposeCard = { L, midTop + 168.f + 16.f, leftColR, midBot };
            DrawCard(purposeCard);
            D2D1_RECT_F purposeInner = { purposeCard.left + 16.f, purposeCard.top + 14.f,
                                         purposeCard.right - 16.f, purposeCard.bottom - 12.f };
            m_purpose.CalculateLayout(purposeInner);
            m_purpose.Render(m_d2d);

            // 右列：应用排行
            D2D1_RECT_F rankCard = { rightColL, midTop, R, midBot };
            DrawCard(rankCard);
            D2D1_RECT_F rankInner = { rankCard.left + 16.f, rankCard.top + 14.f,
                                      rankCard.right - 16.f, rankCard.bottom - 12.f };
            m_rank.CalculateLayout(rankInner);
            m_rank.Render(m_d2d);

            // 时间线（全宽）
            D2D1_RECT_F timelineCard = { L, midBot + 24.f, R, B };
            DrawCard(timelineCard);
            D2D1_RECT_F timelineInner = { timelineCard.left + 16.f, timelineCard.top + 14.f,
                                          timelineCard.right - 16.f, timelineCard.bottom - 12.f };
            m_timeline.CalculateLayout(timelineInner);
            m_timeline.Render(m_d2d);

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
    PurposeBar m_purpose;
    OverviewCards m_cards;

    // 卡片通用样式（复刻原版 Border：圆角 8、CardBackground + 1px 描边）
    void DrawCard(const D2D1_RECT_F& rc)
    {
        ID2D1RenderTarget* rt = m_d2d.RT();
        if (!m_cardBrush)
        {
            m_cardBrush = m_d2d.MakeSolid(D2D1::ColorF(0x1E2129, 0.85f));
            m_strokeBrush = m_d2d.MakeSolid(D2D1::ColorF(0x3A4150, 0.6f));
            m_sectionFmt = m_d2d.MakeTextFormat(20.f, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
            m_textBrush = m_d2d.MakeSolid(Palette::TextPrimary());
            m_headerFmt = m_d2d.MakeTextFormat(28.f, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
            m_dateFmt = m_d2d.MakeTextFormat(16.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
            m_dateBrush = m_d2d.MakeSolid(Palette::TextSecondary());
        }
        rt->FillRoundedRectangle(D2D1::RoundedRect(rc, 8.f, 8.f), m_cardBrush);
        rt->DrawRoundedRectangle(D2D1::RoundedRect(rc, 8.f, 8.f), m_strokeBrush, 1.f);
    }

    // 页头：标题「今日」+ 日期
    void DrawHeader(const D2D1_RECT_F& rc)
    {
        ID2D1RenderTarget* rt = m_d2d.RT();
        if (!m_cardBrush) DrawCard(rc); // 触发资源初始化
        D2D1_RECT_F rcTitle = { rc.left, rc.top, rc.left + 160.f, rc.top + 32.f };
        rt->DrawText(L"今日", 2, m_headerFmt, rcTitle, m_textBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

        SYSTEMTIME st;
        ::GetLocalTime(&st);
        wchar_t buf[32];
        swprintf_s(buf, L"%d年%02d月%02d日", st.wYear, st.wMonth, st.wDay);
        D2D1_RECT_F rcDate = { rc.left, rc.top + 32.f, rc.left + 300.f, rc.top + 48.f };
        rt->DrawText(buf, (UINT32)wcslen(buf), m_dateFmt, rcDate, m_dateBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

    CComPtr<ID2D1SolidColorBrush> m_cardBrush;
    CComPtr<ID2D1SolidColorBrush> m_strokeBrush;
    CComPtr<ID2D1SolidColorBrush> m_textBrush;
    CComPtr<ID2D1SolidColorBrush> m_dateBrush;
    CComPtr<IDWriteTextFormat> m_sectionFmt;
    CComPtr<IDWriteTextFormat> m_headerFmt;
    CComPtr<IDWriteTextFormat> m_dateFmt;

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
