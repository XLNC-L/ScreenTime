// PurposeBar.h — 用途分布（Direct2D 自绘，复刻原版今日页）
//
// 视觉：标题 + 12px 高横向分段条形（圆角渐变段，首尾圆角 6）+ 图例 2 列
// 架构：SetItems → CalculateLayout → Render；资源缓存，数据资源惰性重建

#pragma once

#include "D2DContext.h"
#include <vector>

class PurposeBar
{
public:
    struct PurposeItem
    {
        const wchar_t* label = L"";
        float percent = 0.f;              // 0-100
        Palette::GradientPair gradient;
    };

    void SetItems(std::vector<PurposeItem> items)
    {
        m_items = std::move(items);
        m_needDataRebuild = true;
    }

    // 窗口隐藏时释放全部 D2D 资源
    void ReleaseResources()
    {
        m_titleBrush.Release(); m_bodyBrush.Release(); m_tickBrush.Release();
        m_titleFmt.Release(); m_bodyFmt.Release(); m_legendFmt.Release();
        m_segBrushes.clear();
        m_deviceReady = false;
        m_needDataRebuild = true;
    }

    void CalculateLayout(const D2D1_RECT_F& bounds)
    {
        m_bounds = bounds;
        m_titleRect = { bounds.left, bounds.top, bounds.right, bounds.top + 24.f };
        m_barRect = { bounds.left, bounds.top + 32.f, bounds.right, bounds.top + 44.f };
        m_legendRect = { bounds.left, bounds.top + 52.f, bounds.right, bounds.bottom };
        m_needDataRebuild = true;
    }

    void Render(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt) return;
        EnsureDeviceResources(ctx);
        if (m_needDataRebuild) RebuildData(ctx);

        rt->DrawText(L"用途分布", 4, m_titleFmt, m_titleRect, m_titleBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

        if (m_items.empty())
        {
            rt->DrawText(L"暂无数据", 4, m_bodyFmt, m_barRect, m_tickBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
            return;
        }

        // 分段条形（首尾圆角 6，中间平直）
        FLOAT x = m_barRect.left;
        const FLOAT fullW = m_barRect.right - m_barRect.left;
        for (size_t i = 0; i < m_items.size(); ++i)
        {
            const FLOAT segW = (m_items[i].percent / 100.f) * fullW;
            if (segW <= 0.5f || !m_segBrushes[i]) { continue; }

            const D2D1_RECT_F seg = { x, m_barRect.top, x + segW, m_barRect.bottom };
            const bool first = (i == 0);
            const bool last = (i + 1 == m_items.size() || m_items[i + 1].percent <= 0.f);
            if (first || last)
            {
                rt->FillRoundedRectangle(D2D1::RoundedRect(seg, 6.f, 6.f), m_segBrushes[i]);
            }
            else
            {
                rt->FillRectangle(seg, m_segBrushes[i]);
            }
            x += segW;
        }

        // 图例：2 列
        const int rows = (int)((m_items.size() + 1) / 2);
        const FLOAT colW = (m_legendRect.right - m_legendRect.left) / 2.f;
        for (size_t i = 0; i < m_items.size(); ++i)
        {
            const int r = (int)(i / 2);
            const int c = (int)(i % 2);
            const FLOAT y = m_legendRect.top + r * 20.f;
            if (y + 20.f > m_legendRect.bottom) break;
            const FLOAT x0 = m_legendRect.left + c * colW;

            // 色块
            D2D1_RECT_F dot = { x0, y + 4.f, x0 + 12.f, y + 16.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(dot, 3.f, 3.f), m_segBrushes[i]);

            // 名称
            D2D1_RECT_F rcName = { x0 + 18.f, y, x0 + colW - 44.f, y + 20.f };
            rt->DrawText(m_items[i].label, (UINT32)wcslen(m_items[i].label), m_legendFmt,
                         rcName, m_bodyBrush,
                         D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);

            // 百分比
            wchar_t buf[16];
            swprintf_s(buf, L"%d%%", (int)m_items[i].percent);
            D2D1_RECT_F rcPct = { x0 + colW - 44.f, y, x0 + colW, y + 20.f };
            rt->DrawText(buf, (UINT32)wcslen(buf), m_legendFmt, rcPct, m_tickBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }
    }

private:
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        m_titleBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_bodyBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_tickBrush = ctx.MakeSolid(Palette::TextMuted());
        m_titleFmt = ctx.MakeTextFormat(20.f, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_bodyFmt = ctx.MakeTextFormat(14.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_legendFmt = ctx.MakeTextFormat(14.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_deviceReady = true;
    }

    void RebuildData(D2DContext& ctx)
    {
        m_segBrushes.assign(m_items.size(), nullptr);
        for (size_t i = 0; i < m_items.size(); ++i)
        {
            const auto& it = m_items[i];
            const D2D1_POINT_2F g0 = { m_barRect.left, m_barRect.top };
            const D2D1_POINT_2F g1 = { m_barRect.right, m_barRect.bottom };
            m_segBrushes[i] = ctx.MakeLinearGradient(it.gradient.a, it.gradient.b, g0, g1);
        }
        m_needDataRebuild = false;
    }

    std::vector<PurposeItem> m_items;
    bool m_needDataRebuild = true;
    bool m_deviceReady = false;

    D2D1_RECT_F m_bounds{};
    D2D1_RECT_F m_titleRect{};
    D2D1_RECT_F m_barRect{};
    D2D1_RECT_F m_legendRect{};

    CComPtr<ID2D1SolidColorBrush> m_titleBrush;
    CComPtr<ID2D1SolidColorBrush> m_bodyBrush;
    CComPtr<ID2D1SolidColorBrush> m_tickBrush;
    CComPtr<IDWriteTextFormat> m_titleFmt;
    CComPtr<IDWriteTextFormat> m_bodyFmt;
    CComPtr<IDWriteTextFormat> m_legendFmt;
    std::vector<CComPtr<ID2D1LinearGradientBrush>> m_segBrushes;
};
