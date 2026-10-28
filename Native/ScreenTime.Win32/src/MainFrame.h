// MainFrame.h — 主窗口（WTL）
//
// M2 阶段二：内存优化 + 真实数据。
//   - D2D 懒初始化：托盘常驻时零渲染内存，窗口首次显示才建渲染目标
//   - 关闭 = 隐藏到托盘（释放全部 D2D 资源），托盘点击再显示
//   - 数据层 TodayRepository 只读 SQLite（对齐 WinUI 版口径），--demo 用演示数据

#pragma once

#include <atlframe.h>
#include <atlctrls.h>
#include <dwmapi.h>
#include <shellapi.h>
#include "TrayIcon.h"
#include "D2DContext.h"
#include "DonutChart.h"
#include "Timeline.h"
#include "RankList.h"
#include "PurposeBar.h"
#include "OverviewCards.h"
#include "TodayRepository.h"

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
        MESSAGE_HANDLER(WM_MOUSEWHEEL, OnMouseWheel)
        MESSAGE_HANDLER(TrayIcon::WM_TRAY, OnTray)
        MESSAGE_HANDLER(TrayIcon::kCmdOpen, OnOpen)
        MESSAGE_HANDLER(TrayIcon::kCmdExit, OnExit)
    END_MSG_MAP()

    LRESULT OnTray(UINT, WPARAM wParam, LPARAM lParam, BOOL& bHandled)
    {
        bHandled = TRUE;
        return m_tray.OnTray(TrayIcon::WM_TRAY, wParam, lParam);
    }

    LRESULT OnOpen(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE;
        // 显示前确保渲染与数据就绪（隐藏期间资源已全部释放）
        EnsureD2D();
        ::ShowWindow(m_hWnd, SW_SHOW);
        ::SetForegroundWindow(m_hWnd);
        return 0;
    }

    LRESULT OnExit(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE;
        ::PostMessageW(m_hWnd, WM_CLOSE, 0, 0);
        return 0;
    }

    LRESULT OnMouseWheel(UINT, WPARAM wParam, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE;
        // 光标在应用排行卡内才滚动（其余区域不响应滚轮）
        POINT pt;
        ::GetCursorPos(&pt);
        ::ScreenToClient(m_hWnd, &pt);
        if (pt.x >= m_rankCard.left && pt.x < m_rankCard.right &&
            pt.y >= m_rankCard.top && pt.y < m_rankCard.bottom)
        {
            const int delta = GET_WHEEL_DELTA_WPARAM(wParam) > 0 ? -1 : 1;
            m_rank.ScrollBy(delta);
            ::InvalidateRect(m_hWnd, nullptr, TRUE);
        }
        return 0;
    }

    LRESULT OnCreate(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE;
        ApplyMica();
        m_tray.Create(m_hWnd);
        // D2D 懒初始化：首帧绘制（OnPaint）时按需创建
        return 0;
    }

    LRESULT OnSize(UINT, WPARAM, LPARAM lParam, BOOL& bHandled)
    {
        bHandled = TRUE;
        if (m_d2d.RT())
            m_d2d.Resize(LOWORD(lParam), HIWORD(lParam));
        ::InvalidateRect(m_hWnd, nullptr, TRUE);
        return 0;
    }

    // 禁用系统擦背景，避免闪烁（D2D 自己 Clear）
    LRESULT OnEraseBkgnd(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE;
        return TRUE;
    }

    LRESULT OnClose(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE; // 关键：不设 TRUE 会继续走 DefWindowProc，默认销毁窗口
        // 关闭 = 最小化到托盘；释放全部渲染资源，内存回落
        ::ShowWindow(m_hWnd, SW_HIDE);
        ReleaseD2D();
        return 0;
    }

    LRESULT OnDestroy(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE; // 关键：不设 TRUE 则 PostQuitMessage 不执行，变幽灵进程
        m_tray.Destroy();
        ReleaseD2D();
        ::PostQuitMessage(0);
        return 0;
    }

    LRESULT OnPaint(UINT, WPARAM, LPARAM, BOOL& bHandled)
    {
        bHandled = TRUE;
        PAINTSTRUCT ps;
        ::BeginPaint(m_hWnd, &ps);

        // 懒初始化兜底：--show 直接显示时首帧在这里建 D2D + 读数据
        if (!m_d2d.RT()) EnsureD2D();

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
            m_rankCard = rankCard; // 供滚轮命中判断
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

    // 数据保活：组件只持有 label 指针，wstring 生命周期挂在 MainFrame
    TodayResult m_today;

    // 排行应用图标（ID2D1Bitmap 是设备资源，随窗口隐藏释放；随 MainFrame 生命周期保活）
    std::vector<CComPtr<ID2D1Bitmap>> m_rankIcons;

    // 应用排行卡区域（滚轮命中判断，OnPaint 时更新）
    D2D1_RECT_F m_rankCard{};

    // ---- 渲染生命周期（懒初始化 / 隐藏释放）----

    void EnsureD2D()
    {
        if (m_d2d.RT()) return;

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
            return; // 窗口继续显示 GDI 兜底文案，不退出
        }

        LoadData();
        ::InvalidateRect(m_hWnd, nullptr, TRUE);
    }

    void ReleaseD2D()
    {
        m_donut.ReleaseResources();
        m_timeline.ReleaseResources();
        m_rank.ReleaseResources();
        m_purpose.ReleaseResources();
        m_cards.ReleaseResources();

        m_cardBrush.Release();
        m_strokeBrush.Release();
        m_textBrush.Release();
        m_dateBrush.Release();
        m_sectionFmt.Release();
        m_headerFmt.Release();
        m_dateFmt.Release();

        m_today = TodayResult{}; // 一并释放数据（wstring）
        m_rankIcons.clear();     // 释放应用图标位图
        m_d2d.ReleaseAll();

        // 托盘常驻时把工作集修剪到最小（类似任务管理器最小化行为），
        // 需要时由 OS 按需换回，显示时 D2D 重建会自然重新驻留
        HANDLE proc = ::GetCurrentProcess();
        ::SetProcessWorkingSetSize(proc, (SIZE_T)-1, (SIZE_T)-1);
    }

    // ---- 数据加载：真实库（默认） / 演示数据（--demo）----

    void LoadData()
    {
        if (::wcsstr(::GetCommandLineW(), L"--demo") != nullptr)
        {
            LoadDemoData();
            return;
        }

        TodayRepository repo;
        m_today = repo.Load();
        if (!m_today.ok)
        {
            SetEmptyAll();
            // 写日志便于排查（如数据库被占用/损坏）
            wchar_t tmpPath[MAX_PATH]{}, logPath[MAX_PATH]{};
            if (::GetEnvironmentVariableW(L"TEMP", tmpPath, MAX_PATH) > 0)
            {
                wcscpy_s(logPath, tmpPath);
                wcscat_s(logPath, L"\\screen32_db.log");
                FILE* f = nullptr;
                if (_wfopen_s(&f, logPath, L"w") == 0 && f)
                {
                    std::wstring werr = Utf8ToWide(m_today.error.c_str());
                    fwprintf(f, L"%s\n", werr.c_str());
                    fclose(f);
                }
            }
            return;
        }

        // 概览四卡（分钟粒度，后台 = 总 − 前台）
        const double bgMin = std::floor(m_today.totalSec / 60.0) - std::floor(m_today.fgSec / 60.0);
        OverviewCards::Card cards[4] = {
            { L"今日总时长", FormatDurationMinute(m_today.totalSec) },
            { L"前台时长",   FormatDurationMinute(m_today.fgSec) },
            { L"后台播放",   FormatDurationMinute(std::max(0.0, bgMin) * 60.0) },
            { L"亮屏时长",   FormatDurationMinute(m_today.displaySec) },
        };
        m_cards.SetData(cards, 4);

        // 分类占比（环形图 + 图例；按分类色）
        std::vector<DonutChart::Segment> segs;
        for (const auto& c : m_today.categories)
        {
            // Narrow 返回临时窄串，调用链在同一完整表达式内完成，指针安全
            Palette::GradientPair g = Palette::CategoryColor(Narrow(c.name).c_str());
            segs.push_back({ (float)c.seconds, g, c.name.c_str() });
        }
        m_donut.SetSegments(std::move(segs));
        m_donut.SetEmpty(m_today.categories.empty());
        m_donut.SetLegendMode(true);

        // 用途分布（按用途色；PurposeBar 期望 0-100 百分比）
        float purposeSum = 0.f;
        for (const auto& p : m_today.purposes) purposeSum += (float)p.seconds;
        std::vector<PurposeBar::PurposeItem> purposes;
        for (const auto& p : m_today.purposes)
        {
            const float pct = purposeSum > 0.f ? (float)p.seconds / purposeSum * 100.f : 0.f;
            purposes.push_back({ p.name.c_str(), pct,
                                 Palette::PurposeColor(Narrow(p.name).c_str()) });
        }
        m_purpose.SetItems(std::move(purposes));

        // 应用排行（轮换色板，按时长降序；RankList 期望分钟值；提取 exe 图标）
        std::vector<RankList::RankItem> ranks;
        static const Palette::GradientPair kRotate[] = {
            Palette::Teal(), Palette::Violet(), Palette::Amber(), Palette::Pink(),
            Palette::Blue(), Palette::Sage(),  Palette::Slate(),
        };
        m_rankIcons.clear();
        ID2D1RenderTarget* rt = m_d2d.RT();
        for (size_t i = 0; i < m_today.ranks.size(); ++i)
        {
            CComPtr<ID2D1Bitmap> icon;
            if (rt) icon = ExtractAppIcon(rt, m_today.ranks[i].exePath);
            m_rankIcons.push_back(icon);
            ranks.push_back({ m_today.ranks[i].name.c_str(),
                              (float)(m_today.ranks[i].seconds / 60.0),
                              kRotate[i % 7],
                              icon ? icon.p : nullptr });
        }
        m_rank.SetItems(std::move(ranks));
        m_rank.SetEmpty(m_today.ranks.empty());

        // 时间线（前台片段，自适应范围 + 当前时间游标）
        std::vector<Timeline::TimeSlot> slots;
        for (const auto& s : m_today.slots)
            slots.push_back({ (float)s.startMin, (float)s.endMin,
                              Palette::CategoryColor(Narrow(s.category).c_str()), s.app.c_str() });
        m_timeline.SetSlots(std::move(slots));
        m_timeline.SetEmpty(m_today.slots.empty());
    }

    // 演示数据（--demo，用于无库/空库环境跑通布局）
    void LoadDemoData()
    {
        std::vector<DonutChart::Segment> segs;
        segs.push_back({ 46.f, Palette::Teal(),   L"Microsoft Edge" });
        segs.push_back({ 32.f, Palette::Violet(), L"WeChat" });
        segs.push_back({ 21.f, Palette::Amber(),  L"Doubao" });
        segs.push_back({ 12.f, Palette::Pink(),   L"IDM" });
        segs.push_back({ 9.f,  Palette::Slate(),  L"其他" });
        m_donut.SetSegments(std::move(segs));
        m_donut.SetEmpty(false);
        m_donut.SetLegendMode(true);

        std::vector<PurposeBar::PurposeItem> purposes;
        purposes.push_back({ L"社交媒体", 46.f, Palette::Blue() });
        purposes.push_back({ L"浏览网页", 37.f, Palette::Teal() });
        purposes.push_back({ L"办公效率", 10.f, Palette::Sage() });
        purposes.push_back({ L"视频媒体", 7.f,  Palette::Pink() });
        m_purpose.SetItems(std::move(purposes));

        std::vector<Timeline::TimeSlot> slots;
        slots.push_back({ 550.f, 576.f, Palette::Teal(),   L"Microsoft Edge" });
        slots.push_back({ 600.f, 632.f, Palette::Violet(), L"WeChat" });
        slots.push_back({ 750.f, 762.f, Palette::Pink(),   L"IDM" });
        slots.push_back({ 860.f, 881.f, Palette::Amber(),  L"Doubao" });
        slots.push_back({ 1140.f, 1149.f, Palette::Slate(), L"其他" });
        slots.push_back({ 1205.f, 1225.f, Palette::Teal(), L"Microsoft Edge" });
        m_timeline.SetSlots(std::move(slots));
        m_timeline.SetEmpty(false);

        std::vector<RankList::RankItem> ranks;
        ranks.push_back({ L"Microsoft Edge", 46.f, Palette::Teal() });
        ranks.push_back({ L"WeChat", 32.f, Palette::Violet() });
        ranks.push_back({ L"Doubao", 21.f, Palette::Amber() });
        ranks.push_back({ L"IDM", 12.f, Palette::Pink() });
        ranks.push_back({ L"其他", 9.f, Palette::Slate() });
        ranks.push_back({ L"VS Code", 8.f, Palette::Blue() });
        ranks.push_back({ L"Steam", 6.f, Palette::Sage() });
        ranks.push_back({ L"PotPlayer", 5.f, Palette::Amber() });
        m_rank.SetItems(std::move(ranks));
        m_rank.SetEmpty(false);

        OverviewCards::Card cards[4] = {
            { L"今日总时长", L"2:00" },
            { L"前台时长",   L"1:50" },
            { L"后台播放",   L"0:10" },
            { L"亮屏时长",   L"3:20" },
        };
        m_cards.SetData(cards, 4);
    }

    void SetEmptyAll()
    {
        m_donut.SetSegments({});
        m_donut.SetEmpty(true);
        m_donut.SetLegendMode(true);
        m_purpose.SetItems({});
        m_timeline.SetSlots({});
        m_timeline.SetEmpty(true);
        m_rank.SetItems({});
        m_rank.SetEmpty(true);
        OverviewCards::Card cards[4] = {
            { L"今日总时长", L"暂无数据" },
            { L"前台时长",   L"暂无数据" },
            { L"后台播放",   L"暂无数据" },
            { L"亮屏时长",   L"暂无数据" },
        };
        m_cards.SetData(cards, 4);
    }

    // wstring → 窄串（utf-8），用于分类/用途名查颜色
    static std::string Narrow(const std::wstring& w)
    {
        if (w.empty()) return "";
        const int n = ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string s(n - 1, '\0');
        ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, s.data(), n, nullptr, nullptr);
        return s;
    }

    // 从 exe 路径提取应用图标（32x32），转为 premultiplied BGRA 的 D2D 位图；
    // 任何一步失败都返回空（排行回退到渐变色点），不抛异常
    static CComPtr<ID2D1Bitmap> ExtractAppIcon(ID2D1RenderTarget* rt, const std::wstring& exePath)
    {
        CComPtr<ID2D1Bitmap> bmp;
        if (!rt || exePath.empty()) return bmp;

        SHFILEINFOW sfi{};
        if (!::SHGetFileInfoW(exePath.c_str(), 0, &sfi, sizeof(sfi), SHGFI_ICON | SHGFI_LARGEICON))
            return bmp;
        HICON hIcon = sfi.hIcon;
        if (!hIcon) return bmp;

        ICONINFO ii{};
        if (::GetIconInfo(hIcon, &ii))
        {
            BITMAP bm{};
            if (ii.hbmColor && ::GetObjectW(ii.hbmColor, sizeof(bm), &bm) == sizeof(bm) &&
                bm.bmWidth > 0 && bm.bmHeight > 0)
            {
                const int w = bm.bmWidth, h = bm.bmHeight;
                std::vector<BYTE> px((size_t)w * h * 4);
                BITMAPINFO bi{};
                bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bi.bmiHeader.biWidth = w;
                bi.bmiHeader.biHeight = -h; // top-down
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;
                bi.bmiHeader.biCompression = BI_RGB;

                HDC hdc = ::CreateCompatibleDC(nullptr);
                if (hdc)
                {
                    if (::GetDIBits(hdc, ii.hbmColor, 0, (UINT)h, px.data(), &bi, DIB_RGB_COLORS))
                    {
                        // BGRA straight → premultiplied（D2D 位图要求）
                        for (int i = 0; i < w * h; ++i)
                        {
                            BYTE* p = px.data() + (size_t)i * 4;
                            const BYTE a = p[3];
                            if (a == 0) { p[0] = p[1] = p[2] = 0; continue; }
                            p[0] = (BYTE)((UINT)p[0] * a / 255);
                            p[1] = (BYTE)((UINT)p[1] * a / 255);
                            p[2] = (BYTE)((UINT)p[2] * a / 255);
                        }
                        rt->CreateBitmap(
                            D2D1::SizeU((UINT32)w, (UINT32)h), px.data(), (UINT32)w * 4,
                            D2D1::BitmapProperties(
                                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,
                                                  D2D1_ALPHA_MODE_PREMULTIPLIED)),
                            &bmp);
                    }
                    ::DeleteDC(hdc);
                }
            }
            if (ii.hbmColor) ::DeleteObject(ii.hbmColor);
            if (ii.hbmMask)  ::DeleteObject(ii.hbmMask);
        }
        ::DestroyIcon(hIcon);
        return bmp;
    }

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
