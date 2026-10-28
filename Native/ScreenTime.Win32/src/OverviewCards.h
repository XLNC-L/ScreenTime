// OverviewCards.h — 概览卡片行（Direct2D 自绘，复刻原版今日页四卡）
//
// 四张横排卡片：今日总时长 / 前台时长 / 后台播放 / 亮屏时长
// 卡样式：圆角 8、卡片底色（略浅于页底）、标题 12px 次级色 + 数值 28px 主色半粗
// 架构：SetData → CalculateLayout → Render；全部资源初始化缓存，Render 零分配

#pragma once

#include "D2DContext.h"

class OverviewCards
{
public:
    struct Card
    {
        const wchar_t* title = L"";
        const wchar_t* value = L"";
    };

    void SetData(const Card* cards, int count)
    {
        m_count = std::min(count, 4);
        for (int i = 0; i < m_count; ++i) m_cards[i] = cards[i];
    }

    void CalculateLayout(const D2D1_RECT_F& bounds)
    {
        m_bounds = bounds;
        // 4 卡横排，卡间距 12
        const FLOAT gap = 12.f;
        const FLOAT w = (bounds.right - bounds.left - gap * 3.f) / 4.f;
        for (int i = 0; i < 4; ++i)
        {
            m_cardRects[i] = {
                bounds.left + i * (w + gap), bounds.top,
                bounds.left + i * (w + gap) + w, bounds.bottom
            };
        }
    }

    void Render(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt) return;
        EnsureDeviceResources(ctx);

        for (int i = 0; i < m_count; ++i)
        {
            const D2D1_RECT_F& c = m_cardRects[i];

            // 卡片底色（比页底略浅，带 1px 描边层次）
            rt->FillRoundedRectangle(D2D1::RoundedRect(c, 8.f, 8.f), m_cardBrush);
            rt->DrawRoundedRectangle(D2D1::RoundedRect(c, 8.f, 8.f), m_strokeBrush, 1.f);

            // 标题（12px 次级）
            D2D1_RECT_F rcTitle = { c.left + 16.f, c.top + 12.f, c.right - 16.f, c.top + 28.f };
            rt->DrawText(m_cards[i].title, (UINT32)wcslen(m_cards[i].title), m_titleFmt,
                         rcTitle, m_titleBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

            // 数值（28px 半粗主色）
            D2D1_RECT_F rcValue = { c.left + 16.f, c.top + 32.f, c.right - 16.f, c.bottom - 8.f };
            rt->DrawText(m_cards[i].value, (UINT32)wcslen(m_cards[i].value), m_valueFmt,
                         rcValue, m_valueBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }
    }

private:
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        // 卡片底：原版 CardBackgroundFillColorDefault 在深色下 ≈ #1E2129 半透
        m_cardBrush = ctx.MakeSolid(D2D1::ColorF(0x1E2129, 0.85f));
        m_strokeBrush = ctx.MakeSolid(D2D1::ColorF(0x3A4150, 0.6f));
        m_titleBrush = ctx.MakeSolid(Palette::TextSecondary());
        m_valueBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_titleFmt = ctx.MakeTextFormat(12.f);
        m_valueFmt = ctx.MakeTextFormat(28.f, DWRITE_FONT_WEIGHT_SEMI_BOLD);
        m_deviceReady = true;
    }

    D2D1_RECT_F m_bounds{};
    Card m_cards[4]{};
    int m_count = 0;
    D2D1_RECT_F m_cardRects[4]{};
    bool m_deviceReady = false;

    CComPtr<ID2D1SolidColorBrush> m_cardBrush;
    CComPtr<ID2D1SolidColorBrush> m_strokeBrush;
    CComPtr<ID2D1SolidColorBrush> m_titleBrush;
    CComPtr<ID2D1SolidColorBrush> m_valueBrush;
    CComPtr<IDWriteTextFormat> m_titleFmt;
    CComPtr<IDWriteTextFormat> m_valueFmt;
};
