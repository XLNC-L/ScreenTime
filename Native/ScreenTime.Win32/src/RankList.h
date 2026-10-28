// RankList.h — 应用排行列表（Direct2D 自绘）
//
// 视觉：每行 = 序号色块 + 应用名 + 时长（右对齐）+ 底部 4px 渐变进度条（圆角）。
// 行高 44px（8px 网格），最多显示 6 行，超出滚动后续做。
// 架构：SetItems → Render；设备资源缓存，数据资源惰性重建，Render 零分配。

#pragma once

#include "D2DContext.h"
#include <vector>

class RankList
{
public:
    struct RankItem
    {
        const wchar_t* name = L"";
        float minutes = 0.f;
        Palette::GradientPair gradient;
    };

    void SetItems(std::vector<RankItem> items)
    {
        m_items = std::move(items);
        m_needDataRebuild = true;
    }

    void SetEmpty(bool empty) { m_empty = empty; }

    // 窗口隐藏时释放全部 D2D 资源
    void ReleaseResources()
    {
        m_titleBrush.Release(); m_textBrush.Release(); m_timeBrush.Release();
        m_trackBrush.Release(); m_titleFmt.Release(); m_nameFmt.Release(); m_timeFmt.Release();
        m_barGradients.clear();
        m_deviceReady = false;
        m_needDataRebuild = true;
    }

    void CalculateLayout(const D2D1_RECT_F& bounds)
    {
        m_bounds = bounds;
        m_titleRect = { bounds.left, bounds.top, bounds.right, bounds.top + 24.f };

        // 列表区：标题下方，行高 44px + 行距 4px
        m_listTop = bounds.top + 32.f;
        m_maxRows = (int)((bounds.bottom - m_listTop + 4.f) / 48.f);
        if (m_maxRows < 1) m_maxRows = 1;
        if (m_maxRows > 6) m_maxRows = 6;

        m_needDataRebuild = true;
    }

    void Render(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt) return;

        EnsureDeviceResources(ctx);
        if (m_needDataRebuild) RebuildData(ctx);

        rt->DrawText(L"应用排行", 4, m_titleFmt, m_titleRect, m_titleBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

        if (m_empty || m_items.empty())
        {
            RenderEmpty(rt);
            return;
        }

        // 最大值用于进度条比例
        float maxMin = 1.f;
        for (const auto& it : m_items)
            if (it.minutes > maxMin) maxMin = it.minutes;

        const int rows = std::min((int)m_items.size(), m_maxRows);
        for (int i = 0; i < rows; ++i)
        {
            const auto& item = m_items[i];
            const FLOAT y = m_listTop + i * 48.f;

            // 行底进度条背景（全宽 4px）
            D2D1_RECT_F track = { m_bounds.left, y + 34.f, m_bounds.right, y + 38.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(track, 2.f, 2.f), m_trackBrush);

            // 进度条前景（渐变，按比例）
            if (m_barGradients[i])
            {
                const FLOAT ratio = item.minutes / maxMin;
                D2D1_RECT_F bar = { m_bounds.left, y + 34.f,
                                    m_bounds.left + (m_bounds.right - m_bounds.left) * ratio, y + 38.f };
                rt->FillRoundedRectangle(D2D1::RoundedRect(bar, 2.f, 2.f), m_barGradients[i]);
            }

            // 色点（渐变）
            D2D1_RECT_F dot = { m_bounds.left, y + 4.f, m_bounds.left + 24.f, y + 28.f };
            rt->FillRoundedRectangle(D2D1::RoundedRect(dot, 8.f, 8.f), m_barGradients[i]);

            // 应用名
            D2D1_RECT_F rcName = { m_bounds.left + 34.f, y, m_bounds.left + 240.f, y + 34.f };
            rt->DrawText(item.name, (UINT32)wcslen(item.name), m_nameFmt, rcName, m_textBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

            // 时长（右对齐于行右缘；统一「X 小时 Y 分 / X 分」格式）
            wchar_t buf[32];
            if (item.minutes >= 60.f)
                swprintf_s(buf, L"%d 小时 %d 分", (int)(item.minutes / 60.f), (int)item.minutes % 60);
            else
                swprintf_s(buf, L"%d 分", (int)item.minutes);
            D2D1_RECT_F rcTime = { m_bounds.right - 100.f, y, m_bounds.right, y + 34.f };
            rt->DrawText(buf, (UINT32)wcslen(buf), m_timeFmt, rcTime, m_timeBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }
    }

private:
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        m_titleBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_textBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_timeBrush = ctx.MakeSolid(Palette::TextSecondary());
        m_trackBrush = ctx.MakeSolid(Palette::Track());
        m_titleFmt = ctx.MakeTextFormat(20.f, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_nameFmt = ctx.MakeTextFormat(16.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_timeFmt = ctx.MakeTextFormat(16.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_TRAILING);
        m_deviceReady = true;
    }

    // 数据资源：每行一个渐变刷（进度条 + 色点共用）
    void RebuildData(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        const int n = std::min((int)m_items.size(), m_maxRows);
        m_barGradients.assign(n, nullptr);
        for (int i = 0; i < n; ++i)
        {
            const auto& item = m_items[i];
            const D2D1_POINT_2F g0 = { m_bounds.left, m_listTop + i * 48.f + 36.f };
            const D2D1_POINT_2F g1 = { m_bounds.left + 200.f, m_listTop + i * 48.f + 36.f };
            m_barGradients[i] = ctx.MakeLinearGradient(item.gradient.a, item.gradient.b, g0, g1);
        }
        m_needDataRebuild = false;
    }

    void RenderEmpty(ID2D1RenderTarget* rt)
    {
        D2D1_RECT_F rc = { m_bounds.left, m_listTop, m_bounds.right, m_listTop + 160.f };
        rt->DrawText(L"暂无使用记录", 6, m_nameFmt, rc, m_timeBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

    // ---- 数据 / 布局 / 资源 ----
    std::vector<RankItem> m_items;
    bool m_empty = false;
    bool m_needDataRebuild = true;
    bool m_deviceReady = false;

    D2D1_RECT_F m_bounds{};
    D2D1_RECT_F m_titleRect{};
    FLOAT m_listTop = 0.f;
    int m_maxRows = 6;

    CComPtr<ID2D1SolidColorBrush> m_titleBrush;
    CComPtr<ID2D1SolidColorBrush> m_textBrush;
    CComPtr<ID2D1SolidColorBrush> m_timeBrush;
    CComPtr<ID2D1SolidColorBrush> m_trackBrush;
    CComPtr<IDWriteTextFormat> m_titleFmt;
    CComPtr<IDWriteTextFormat> m_nameFmt;
    CComPtr<IDWriteTextFormat> m_timeFmt;
    std::vector<CComPtr<ID2D1LinearGradientBrush>> m_barGradients;
};
