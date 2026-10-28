// Timeline.h — 今日时间线（Direct2D 自绘）
//
// 视觉：24 小时横向条带，各应用时段为圆角渐变块（沿时间轴左亮右暗），
//       下方时间刻度（0/6/12/18/24 点）+ 最长三个时段的图例。
// 架构：SetSlots → CalculateLayout → Render；设备资源缓存，数据资源惰性重建，Render 零分配。
// 空态：无时段时画「暂无使用记录」占位。

#pragma once

#include "D2DContext.h"
#include <vector>
#include <algorithm>

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

    void CalculateLayout(const D2D1_RECT_F& bounds)
    {
        m_bounds = bounds;
        m_titleRect = { bounds.left, bounds.top, bounds.right, bounds.top + 24.f };

        // 条带：标题下方，高 120px
        m_stripRect = { bounds.left, bounds.top + 32.f, bounds.right, bounds.top + 32.f + 120.f };

        // 刻度行：条带下方
        m_tickRect = { bounds.left, m_stripRect.bottom + 4.f, bounds.right, m_stripRect.bottom + 24.f };

        // 图例行：刻度下方
        m_legendRect = { bounds.left, m_tickRect.bottom + 12.f, bounds.right, m_tickRect.bottom + 36.f };

        m_needDataRebuild = true; // 布局变化需重建（条带几何相关资源——渐变方向依赖条带矩形）
    }

    void Render(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt) return;

        EnsureDeviceResources(ctx);
        if (m_needDataRebuild) RebuildData(ctx);

        // 标题
        rt->DrawText(L"今日时间线", 5, m_titleFmt, m_titleRect, m_titleBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

        if (m_empty || m_slots.empty())
        {
            RenderEmpty(rt);
            return;
        }

        // 条带背景（深色圆角）
        rt->FillRoundedRectangle(D2D1::RoundedRect(m_stripRect, 12.f, 12.f), m_stripBrush);

        // 时段块（圆角渐变，块间留 2px 呼吸）
        const FLOAT stripW = m_stripRect.right - m_stripRect.left;
        const FLOAT totalMin = 1440.f;
        for (size_t i = 0; i < m_slots.size(); ++i)
        {
            const auto& s = m_slots[i];
            if (s.endMin <= s.startMin || !m_slotGradients[i]) continue;

            const FLOAT x0 = m_stripRect.left + (s.startMin / totalMin) * stripW;
            const FLOAT x1 = m_stripRect.left + (s.endMin / totalMin) * stripW;
            const FLOAT w = std::max(x1 - x0 - 2.f, 6.f); // 最小可见宽度
            const D2D1_RECT_F block = { x0, m_stripRect.top + 6.f,
                                        x0 + w, m_stripRect.bottom - 6.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(block, 8.f, 8.f), m_slotGradients[i]);
        }

        // 时间刻度：0/6/12/18/24
        constexpr FLOAT kHours[5] = { 0.f, 6.f, 12.f, 18.f, 24.f };
        for (FLOAT h : kHours)
        {
            const FLOAT x = m_stripRect.left + (h / 24.f) * stripW;
            rt->DrawLine(D2D1::Point2F(x, m_tickRect.top),
                         D2D1::Point2F(x, m_tickRect.bottom), m_tickBrush, 1.f);

            wchar_t buf[16];
            swprintf_s(buf, L"%02d:00", (int)h); // 0/6/12/18/24：末刻度显示 24:00
            D2D1_RECT_F rcTxt = { x - 20.f, m_tickRect.top, x + 20.f, m_tickRect.bottom };
            rt->DrawText(buf, (UINT32)wcslen(buf), m_tickFmt, rcTxt, m_tickBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }

        RenderLegend(rt);
    }

private:
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        m_titleBrush = ctx.MakeSolid(Palette::TextSecondary());
        m_stripBrush = ctx.MakeSolid(Palette::Track());
        m_tickBrush = ctx.MakeSolid(Palette::TextMuted());
        m_legendBrush = ctx.MakeSolid(Palette::TextSecondary());
        m_titleFmt = ctx.MakeTextFormat(16.f, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_tickFmt = ctx.MakeTextFormat(12.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_CENTER);
        m_legendFmt = ctx.MakeTextFormat(12.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_deviceReady = true;
    }

    // 数据资源：每个时段一个沿时间轴方向的渐变刷（左亮右暗）
    void RebuildData(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        m_slotGradients.assign(m_slots.size(), nullptr);
        for (size_t i = 0; i < m_slots.size(); ++i)
        {
            const auto& s = m_slots[i];
            if (s.endMin <= s.startMin) continue;
            const D2D1_POINT_2F g0 = { m_stripRect.left, m_stripRect.top + 8.f };
            const D2D1_POINT_2F g1 = { m_stripRect.right, m_stripRect.bottom - 8.f };
            m_slotGradients[i] = ctx.MakeLinearGradient(s.gradient.a, s.gradient.b, g0, g1);
        }
        m_needDataRebuild = false;
    }

    void RenderEmpty(ID2D1RenderTarget* rt)
    {
        D2D1_RECT_F rc = { m_bounds.left, m_stripRect.top, m_bounds.right, m_stripRect.bottom };
        rt->FillRoundedRectangle(D2D1::RoundedRect(m_stripRect, 12.f, 12.f), m_stripBrush);
        rt->DrawText(L"暂无使用记录", 6, m_titleFmt, rc, m_tickBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

    // 图例：取时长最长的前 3 个时段（色点 + 应用名）
    void RenderLegend(ID2D1RenderTarget* rt)
    {
        std::vector<size_t> order(m_slots.size());
        for (size_t i = 0; i < order.size(); ++i) order[i] = i;
        std::sort(order.begin(), order.end(), [&](size_t a, size_t b) {
            return (m_slots[a].endMin - m_slots[a].startMin) > (m_slots[b].endMin - m_slots[b].startMin);
        });

        FLOAT cursorX = m_legendRect.left;
        const int kMax = std::min<size_t>(3, order.size());
        for (int i = 0; i < kMax; ++i)
        {
            const auto& s = m_slots[order[i]];

            // 色点（复用该时段渐变刷，柔和观感）
            D2D1_RECT_F dotRect = { cursorX, m_legendRect.top + 4.f,
                                    cursorX + 12.f, m_legendRect.top + 16.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(dotRect, 4.f, 4.f), m_slotGradients[order[i]]);

            // 应用名
            D2D1_RECT_F rcTxt = { cursorX + 18.f, m_legendRect.top, cursorX + 130.f, m_legendRect.bottom };
            rt->DrawText(s.label, (UINT32)wcslen(s.label), m_legendFmt, rcTxt, m_legendBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
            cursorX += 148.f;
        }
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
    D2D1_RECT_F m_legendRect{};

    CComPtr<ID2D1SolidColorBrush> m_titleBrush;
    CComPtr<ID2D1SolidColorBrush> m_stripBrush;
    CComPtr<ID2D1SolidColorBrush> m_tickBrush;
    CComPtr<ID2D1SolidColorBrush> m_legendBrush;
    CComPtr<IDWriteTextFormat> m_titleFmt;
    CComPtr<IDWriteTextFormat> m_tickFmt;
    CComPtr<IDWriteTextFormat> m_legendFmt;
    std::vector<CComPtr<ID2D1LinearGradientBrush>> m_slotGradients;
};
