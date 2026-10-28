// Timeline.h — 今日时间线（Direct2D 自绘，复刻原版今日页）
//
// 视觉：24px 高横向条带（时段渐变块圆角 4）+ 16px 小时刻度 + 一行详情说明
// 架构：SetSlots → CalculateLayout → Render；资源缓存，数据资源惰性重建，Render 零分配

#pragma once

#include "D2DContext.h"
#include <vector>
#include <algorithm>
#include <cmath>

class Timeline
{
public:
    struct TimeSlot
    {
        float startMin = 0.f;      // 开始分钟（0-1440）
        float endMin = 0.f;        // 结束分钟（0-1440）
        Palette::GradientPair gradient;
        const wchar_t* label = L"";
    };

    void SetSlots(std::vector<TimeSlot> slots)
    {
        m_slots = std::move(slots);
        m_needDataRebuild = true;
    }

    void SetEmpty(bool empty) { m_empty = empty; }

    // 窗口隐藏时释放全部 D2D 资源（内存优化：常驻托盘不占渲染内存）
    void ReleaseResources()
    {
        m_titleBrush.Release(); m_stripBrush.Release(); m_tickBrush.Release();
        m_cursorBrush.Release(); m_titleFmt.Release(); m_tickFmt.Release();
        m_detailFmt.Release(); m_bodyFmt.Release();
        m_slotGradients.clear();
        m_deviceReady = false;
        m_needDataRebuild = true;
    }

    void CalculateLayout(const D2D1_RECT_F& bounds)
    {
        m_bounds = bounds;
        m_titleRect = { bounds.left, bounds.top, bounds.right, bounds.top + 24.f };
        m_stripRect = { bounds.left, bounds.top + 30.f, bounds.right, bounds.top + 54.f };
        m_tickRect = { bounds.left, m_stripRect.bottom + 4.f, bounds.right, m_stripRect.bottom + 20.f };
        m_detailRect = { bounds.left, m_tickRect.bottom + 8.f, bounds.right, m_tickRect.bottom + 24.f };
        m_needDataRebuild = true;
    }

    void Render(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt) return;

        EnsureDeviceResources(ctx);
        if (m_needDataRebuild) RebuildData(ctx);

        rt->DrawText(L"今日时间线", 5, m_titleFmt, m_titleRect, m_titleBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

        if (m_empty || m_slots.empty())
        {
            rt->DrawText(L"暂无使用记录", 6, m_bodyFmt, m_stripRect, m_tickBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
            return;
        }

        // ---- 自适应时间范围（复刻原版）：只覆盖实际使用时段 ----
        double dataStart = 1440, dataEnd = 0;
        for (const auto& s : m_slots)
        {
            dataStart = std::min(dataStart, (double)s.startMin);
            dataEnd = std::max(dataEnd, (double)s.endMin);
        }
        const double nowMin = NowLocalMinutes();
        if (dataEnd < nowMin) dataEnd = nowMin; // 未到"现在"则延伸到当前时刻

        // 根据跨度选择刻度间隔（分钟）
        const double rawHours = (dataEnd - dataStart) / 60.0;
        double tickMin = rawHours > 8 ? 120 : rawHours > 3 ? 60 : rawHours > 1 ? 30 : 15;

        // 范围对齐到刻度
        const double rangeStart = dataStart - std::fmod(dataStart, tickMin);
        const double rangeEnd = rangeStart + std::ceil((dataEnd - rangeStart) / tickMin) * tickMin;
        const double span = std::max(rangeEnd - rangeStart, tickMin);

        const FLOAT stripW = m_stripRect.right - m_stripRect.left;
        // X 返回绝对坐标（相对偏移 + 条带左缘），块/刻度/游标直接使用
        auto X = [&](double t) -> FLOAT {
            const double x = (t - rangeStart) / span * stripW;
            return m_stripRect.left + (FLOAT)std::clamp(x, 0.0, (double)stripW);
        };

        // 条带背景
        rt->FillRoundedRectangle(D2D1::RoundedRect(m_stripRect, 6.f, 6.f), m_stripBrush);

        // 时段块（渐变，块间 1px 呼吸，圆角 4）
        for (size_t i = 0; i < m_slots.size(); ++i)
        {
            const auto& s = m_slots[i];
            if (s.endMin <= s.startMin || !m_slotGradients[i]) continue;

            const FLOAT x0 = X(s.startMin);
            const FLOAT x1 = X(s.endMin);
            const FLOAT w = std::max(x1 - x0 - 1.f, 4.f);
            const D2D1_RECT_F block = { x0, m_stripRect.top + 2.f,
                                        x0 + w, m_stripRect.bottom - 2.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(block, 4.f, 4.f), m_slotGradients[i]);
        }

        // 当前时间游标（白色 2px，仅当在范围内）
        if (nowMin >= rangeStart && nowMin <= rangeEnd)
        {
            const FLOAT cx = X(nowMin);
            rt->DrawLine(D2D1::Point2F(cx, m_stripRect.top - 4.f),
                         D2D1::Point2F(cx, m_stripRect.bottom + 4.f), m_cursorBrush, 2.f);
        }

        // 动态刻度（标签边界对齐，避免首尾截断）
        constexpr FLOAT kLabelW = 30.f;
        for (double t = rangeStart; t <= rangeEnd + 0.001; t += tickMin)
        {
            const FLOAT x = X(t);
            const FLOAT left = std::clamp(x - kLabelW / 2.f, m_stripRect.left,
                                          std::max(m_stripRect.right - kLabelW, m_stripRect.left));
            rt->DrawLine(D2D1::Point2F(x, m_tickRect.top),
                         D2D1::Point2F(x, m_tickRect.bottom), m_tickBrush, 1.f);

            wchar_t buf[16];
            const int hour = (int)(t / 60.0), minute = (int)t % 60;
            swprintf_s(buf, L"%02d:%02d", hour, minute);
            D2D1_RECT_F rcTxt = { left, m_tickRect.top, left + kLabelW, m_tickRect.bottom };
            rt->DrawText(buf, (UINT32)wcslen(buf), m_tickFmt, rcTxt, m_tickBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }

        // 详情：时段总数 + 合计
        double totalMin = 0;
        for (const auto& s : m_slots) totalMin += s.endMin - s.startMin;
        wchar_t buf[96];
        swprintf_s(buf, L"共 %zu 个使用时段 · 合计 %d 分钟", m_slots.size(), (int)totalMin);
        rt->DrawText(buf, (UINT32)wcslen(buf), m_detailFmt, m_detailRect, m_tickBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

private:
    // 当前本地时间的分钟数（0-1440）
    static double NowLocalMinutes()
    {
        SYSTEMTIME st;
        ::GetLocalTime(&st);
        return st.wHour * 60.0 + st.wMinute + st.wSecond / 60.0;
    }
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        m_titleBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_stripBrush = ctx.MakeSolid(Palette::Track());
        m_tickBrush = ctx.MakeSolid(Palette::TextMuted());
        m_cursorBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_titleFmt = ctx.MakeTextFormat(20.f, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_tickFmt = ctx.MakeTextFormat(12.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_CENTER);
        m_detailFmt = ctx.MakeTextFormat(14.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_bodyFmt = ctx.MakeTextFormat(14.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_CENTER);
        m_deviceReady = true;
    }

    // 数据资源：每个时段一个沿时间轴方向的渐变刷（左亮右暗）
    void RebuildData(D2DContext& ctx)
    {
        m_slotGradients.assign(m_slots.size(), nullptr);
        for (size_t i = 0; i < m_slots.size(); ++i)
        {
            const auto& s = m_slots[i];
            if (s.endMin <= s.startMin) continue;
            const D2D1_POINT_2F g0 = { m_stripRect.left, m_stripRect.top };
            const D2D1_POINT_2F g1 = { m_stripRect.right, m_stripRect.bottom };
            m_slotGradients[i] = ctx.MakeLinearGradient(s.gradient.a, s.gradient.b, g0, g1);
        }
        m_needDataRebuild = false;
    }

    // ---- 数据 / 布局 / 资源 ----
    std::vector<TimeSlot> m_slots;
    bool m_empty = false;
    bool m_needDataRebuild = true;
    bool m_deviceReady = false;

    D2D1_RECT_F m_bounds{};
    D2D1_RECT_F m_titleRect{};
    D2D1_RECT_F m_stripRect{};
    D2D1_RECT_F m_tickRect{};
    D2D1_RECT_F m_detailRect{};

    CComPtr<ID2D1SolidColorBrush> m_titleBrush;
    CComPtr<ID2D1SolidColorBrush> m_stripBrush;
    CComPtr<ID2D1SolidColorBrush> m_tickBrush;
    CComPtr<ID2D1SolidColorBrush> m_cursorBrush;
    CComPtr<IDWriteTextFormat> m_titleFmt;
    CComPtr<IDWriteTextFormat> m_tickFmt;
    CComPtr<IDWriteTextFormat> m_detailFmt;
    CComPtr<IDWriteTextFormat> m_bodyFmt;
    std::vector<CComPtr<ID2D1LinearGradientBrush>> m_slotGradients;
};
