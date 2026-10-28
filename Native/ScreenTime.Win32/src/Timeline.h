// Timeline.h — 今日时间线（Direct2D 自绘，复刻原版今日页）
//
// 视觉：24px 高横向条带（时段渐变块圆角 4）+ 16px 小时刻度 + 一行详情说明
// 架构：SetSlots → CalculateLayout → Render；资源缓存，数据资源惰性重建，Render 零分配

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

        // 条带背景（深色圆角）
        rt->FillRoundedRectangle(D2D1::RoundedRect(m_stripRect, 6.f, 6.f), m_stripBrush);

        // 时段块（渐变，块间 1px 呼吸）
        const FLOAT stripW = m_stripRect.right - m_stripRect.left;
        for (size_t i = 0; i < m_slots.size(); ++i)
        {
            const auto& s = m_slots[i];
            if (s.endMin <= s.startMin || !m_slotGradients[i]) continue;

            const FLOAT x0 = m_stripRect.left + (s.startMin / 1440.f) * stripW;
            const FLOAT x1 = m_stripRect.left + (s.endMin / 1440.f) * stripW;
            const FLOAT w = std::max(x1 - x0 - 1.f, 4.f); // 最小可见宽度
            const D2D1_RECT_F block = { x0, m_stripRect.top + 2.f,
                                        x0 + w, m_stripRect.bottom - 2.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(block, 4.f, 4.f), m_slotGradients[i]);
        }

        // 时间刻度：0/6/12/18/24
        constexpr FLOAT kHours[5] = { 0.f, 6.f, 12.f, 18.f, 24.f };
        for (FLOAT h : kHours)
        {
            const FLOAT x = m_stripRect.left + (h / 24.f) * stripW;
            rt->DrawLine(D2D1::Point2F(x, m_tickRect.top),
                         D2D1::Point2F(x, m_tickRect.bottom), m_tickBrush, 1.f);

            wchar_t buf[16];
            swprintf_s(buf, L"%02d:00", (int)h);
            D2D1_RECT_F rcTxt = { x - 24.f, m_tickRect.top, x + 24.f, m_tickRect.bottom };
            rt->DrawText(buf, (UINT32)wcslen(buf), m_tickFmt, rcTxt, m_tickBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }

        // 详情：时段总数 + 最活跃
        float totalMin = 0.f;
        size_t activeIdx = 0;
        float activeMin = 0.f;
        for (size_t i = 0; i < m_slots.size(); ++i)
        {
            const float d = m_slots[i].endMin - m_slots[i].startMin;
            totalMin += d;
            if (d > activeMin) { activeMin = d; activeIdx = i; }
        }
        wchar_t buf[96];
        swprintf_s(buf, L"共 %zu 个使用时段 · 合计 %d 分钟 · 最活跃：%s（%d 分钟）",
                   m_slots.size(), (int)totalMin, m_slots[activeIdx].label, (int)activeMin);
        rt->DrawText(buf, (UINT32)wcslen(buf), m_detailFmt, m_detailRect, m_tickBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

private:
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        m_titleBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_stripBrush = ctx.MakeSolid(Palette::Track());
        m_tickBrush = ctx.MakeSolid(Palette::TextMuted());
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
    CComPtr<IDWriteTextFormat> m_titleFmt;
    CComPtr<IDWriteTextFormat> m_tickFmt;
    CComPtr<IDWriteTextFormat> m_detailFmt;
    CComPtr<IDWriteTextFormat> m_bodyFmt;
    std::vector<CComPtr<ID2D1LinearGradientBrush>> m_slotGradients;
};
