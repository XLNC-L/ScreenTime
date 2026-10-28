// DonutChart.h — 环形图（Donut Chart）自绘组件
//
// 架构：
//   SetSegments(...)    数据 + 角度 + 动画状态（渲染循环不分配）
//   CalculateLayout()   圆心/半径/厚度 —— 尺寸变化时调用
//   Render(ctx)         纯绘制：阴影 → 轨道环 → 分段弧 → 中心文字 / 空态
//   UpdateAnimation()   预留：300ms ease-out 缓动（current → target）
//
// 资源生命周期（全部缓存为成员，Render 内零创建）：
//   - 设备资源（轨道刷/阴影/文字格式/圆角笔触/文字色刷）：首次渲染前一次创建
//   - 数据资源（每段渐变刷 + 弧几何）：数据或布局变化后，Render 前惰性重建
//
// 视觉：
//   - 分段弧圆角端点，段间 2° 间隙
//   - 每段渐变沿半径方向（内亮 → 外暗），同色系柔和过渡
//   - 图表背后径向渐变阴影
//   - 8px 网格字号：中心数字 40px、标签 16px

#pragma once

#include "D2DContext.h"
#include <vector>
#include <cmath>

class DonutChart
{
public:
    struct Segment
    {
        float value = 0.f;              // 时长（分钟）
        Palette::GradientPair gradient; // 该段渐变对
        const wchar_t* label = L"";     // 应用名（供后续图例扩展）
    };

    // ---- 动画预留：为 300ms ease-out 缓动准备 ----
    // current = 当前显示值，target = 目标值。静态阶段 current == target。
    // 后续接入动画：每帧 UpdateAnimation(dt)，若 NeedsRepaint() 则 InvalidateRect。
    struct AnimState
    {
        float current = 0.f;
        float target = 0.f;
        float from = 0.f;       // ease 起点值
        float elapsed = 0.f;    // 已耗时（秒）
        bool dirty = false;     // 需要动画过渡
    };

    void SetSegments(std::vector<Segment> segs)
    {
        m_segs = std::move(segs);

        // 重建动画状态：静态阶段 current == target（动画接入后再区分显示值/目标值）
        m_anims.resize(m_segs.size());
        for (size_t i = 0; i < m_segs.size(); ++i)
        {
            auto& a = m_anims[i];
            a.target = m_segs[i].value;
            a.current = a.target;
            a.from = a.current;
            a.dirty = false;
            a.elapsed = 0.f;
        }

        RecomputeAngles();
        m_needSegRebuild = true;
    }

    void SetEmpty(bool empty) { m_empty = empty; }
    bool IsEmpty() const { return m_empty; }

    // 图例模式（复刻原版「分类占比」卡）：只画环，不画中心文字；图例用 RenderLegend 单独画
    void SetLegendMode(bool on) { m_legendMode = on; }

    // 布局：尺寸变化时调用；更新半径并标记数据资源待重建
    void CalculateLayout(const D2D1_RECT_F& bounds)
    {
        const FLOAT w = bounds.right - bounds.left;
        const FLOAT h = bounds.bottom - bounds.top;
        m_center = D2D1::Point2F((bounds.left + bounds.right) / 2.f,
                                 (bounds.top + bounds.bottom) / 2.f);

        m_radiusOuter = std::min(w, h) / 2.f - 24.f; // 留阴影空间（24px）
        if (m_radiusOuter < 40.f) m_radiusOuter = 40.f;
        m_thickness = m_radiusOuter * 0.24f;
        m_rCenter = m_radiusOuter - m_thickness / 2.f;

        m_needSegRebuild = true;
    }

    // 渲染入口：内部惰性创建/重建资源，绘制路径零分配
    void Render(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt) return;

        EnsureDeviceResources(ctx);
        if (m_needSegRebuild) RebuildSegments(ctx);

        // 1) 柔和阴影（径向渐变，最底层）
        if (m_shadow)
            rt->FillEllipse(D2D1::Ellipse(m_center, m_radiusOuter + 16.f, m_radiusOuter + 16.f), m_shadow);

        // 2) 轨道环（深色底环）
        if (m_trackBrush && m_roundCap)
            rt->DrawEllipse(D2D1::Ellipse(m_center, m_rCenter, m_rCenter),
                            m_trackBrush, m_thickness, m_roundCap);

        if (m_empty)
        {
            RenderEmpty(rt);
            return;
        }

        // 3) 分段弧（每段一个径向渐变刷 + 圆角端点）
        for (size_t i = 0; i < m_segs.size(); ++i)
        {
            if (!m_geos[i] || !m_gradients[i] || m_angles[i].sweep <= 0.001f) continue;
            rt->DrawGeometry(m_geos[i], m_gradients[i], m_thickness, m_roundCap);
        }

        // 4) 中心文字（图例模式下不画）
        if (!m_legendMode)
            RenderCenterText(rt);
    }

    // 图例（复刻原版）：色块 12x12 + 应用名 + 百分比，行高 20
    void RenderLegend(D2DContext& ctx, const D2D1_RECT_F& bounds)
    {
        ID2D1RenderTarget* rt = ctx.RT();
        if (!rt || m_segs.empty()) return;
        EnsureDeviceResources(ctx);

        float total = 0.f;
        for (auto& a : m_anims) total += a.current;
        if (total <= 0.f) total = 1.f;

        const FLOAT rowH = 20.f;
        for (size_t i = 0; i < m_segs.size(); ++i)
        {
            const FLOAT y = bounds.top + (FLOAT)i * rowH;
            if (y + rowH > bounds.bottom) break;

            // 色块（渐变）
            D2D1_RECT_F dot = { bounds.left, y + 4.f, bounds.left + 12.f, y + 16.f };
            if (i < m_geos.size() && m_gradients[i])
                rt->FillRoundedRectangle(D2D1::RoundedRect(dot, 3.f, 3.f), m_gradients[i]);

            // 名称（截断）
            D2D1_RECT_F rcName = { bounds.left + 18.f, y, bounds.left + 150.f, y + rowH };
            rt->DrawText(m_segs[i].label, (UINT32)wcslen(m_segs[i].label), m_legendNameFmt,
                         rcName, m_labelBrush,
                         D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);

            // 百分比（右侧）
            wchar_t buf[16];
            swprintf_s(buf, L"%d%%", (int)(m_segs[i].value / total * 100.f));
            D2D1_RECT_F rcPct = { bounds.right - 60.f, y, bounds.right, y + rowH };
            rt->DrawText(buf, (UINT32)wcslen(buf), m_legendPctFmt, rcPct, m_labelBrush,
                         D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }
    }

    // 预留：300ms ease-out。静态阶段直接同步；后续每帧调用并检查 NeedsRepaint()
    void UpdateAnimation(float dt)
    {
        constexpr float kDuration = 0.300f;
        bool animating = false;
        for (auto& a : m_anims)
        {
            if (!a.dirty) continue;
            a.elapsed += dt;
            float t = std::min(1.f, a.elapsed / kDuration);
            float eased = 1.f - std::pow(1.f - t, 3.f); // cubic ease-out
            a.current = a.from + (a.target - a.from) * eased;
            if (t >= 1.f)
            {
                a.current = a.target;
                a.dirty = false;
            }
            else
            {
                animating = true;
            }
        }
        if (animating) RecomputeAngles();
        m_pending = animating;
    }

    bool NeedsRepaint() const { return m_pending; }

private:
    // ---- 设备资源：首次渲染前创建一次，全部缓存 ----
    void EnsureDeviceResources(D2DContext& ctx)
    {
        if (m_deviceReady) return;
        ID2D1RenderTarget* rt = ctx.RT();

        m_trackBrush = ctx.MakeSolid(Palette::Track());
        m_numBrush = ctx.MakeSolid(Palette::TextPrimary());
        m_labelBrush = ctx.MakeSolid(Palette::TextSecondary());
        m_emptyIconBrush = ctx.MakeSolid(Palette::TextMuted());
        m_textBig = ctx.MakeTextFormat(40.f, DWRITE_FONT_WEIGHT_SEMI_BOLD);
        m_textSmall = ctx.MakeTextFormat(16.f);
        m_legendNameFmt = ctx.MakeTextFormat(14.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_LEADING);
        m_legendPctFmt = ctx.MakeTextFormat(14.f, DWRITE_FONT_WEIGHT_REGULAR, DWRITE_TEXT_ALIGNMENT_TRAILING);

        D2D1_STROKE_STYLE_PROPERTIES strokeProps = {};
        strokeProps.startCap = D2D1_CAP_STYLE_ROUND;
        strokeProps.endCap = D2D1_CAP_STYLE_ROUND;
        strokeProps.lineJoin = D2D1_LINE_JOIN_ROUND;
        ctx.D2D()->CreateStrokeStyle(strokeProps, nullptr, 0, &m_roundCap);

        m_deviceReady = true;
    }

    // ---- 角度：从 12 点方向起，顺时针；段间 2° 间隙 ----
    void RecomputeAngles()
    {
        constexpr float kStartRad = -3.14159265f / 2.f;
        constexpr float kGapRad = 2.f * 3.14159265f / 360.f;

        m_angles.assign(m_anims.size(), Angle{});
        float total = 0.f;
        for (auto& a : m_anims) total += a.current;

        float angle = kStartRad;
        for (size_t i = 0; i < m_anims.size(); ++i)
        {
            const float sweep = (total > 0.f) ? (m_anims[i].current / total) * 2.f * 3.14159265f : 0.f;
            m_angles[i] = { angle, sweep };
            const bool isLast = (i + 1 == m_anims.size());
            angle += sweep + (isLast ? 0.f : kGapRad);
        }
    }

    // ---- 数据资源：渐变刷 + 弧几何（数据/布局变化后惰性重建）----
    void RebuildSegments(D2DContext& ctx)
    {
        ID2D1RenderTarget* rt = ctx.RT();

        // 阴影刷依赖圆心/半径，布局变化时一并重建
        m_shadow = ctx.MakeRadialShadow(m_center, m_radiusOuter + 16.f);

        m_gradients.assign(m_segs.size(), nullptr);
        m_geos.assign(m_segs.size(), nullptr);

        const FLOAT rInner = m_rCenter - m_thickness / 2.f;
        const FLOAT rOuter = m_rCenter + m_thickness / 2.f;

        for (size_t i = 0; i < m_segs.size(); ++i)
        {
            const auto& seg = m_segs[i];
            const float start = m_angles[i].start;
            const float sweep = m_angles[i].sweep;
            if (sweep <= 0.001f) continue;

            // 渐变沿该段中间角的径向（内缘亮 → 外缘暗）
            const float mid = start + sweep / 2.f;
            const D2D1_POINT_2F gStart = { m_center.x + std::cos(mid) * rInner,
                                           m_center.y + std::sin(mid) * rInner };
            const D2D1_POINT_2F gEnd = { m_center.x + std::cos(mid) * rOuter,
                                         m_center.y + std::sin(mid) * rOuter };
            m_gradients[i] = ctx.MakeLinearGradient(seg.gradient.a, seg.gradient.b, gStart, gEnd);

            // 弧几何（中心线半径 m_rCenter，顺时针）
            const D2D1_POINT_2F p0 = { m_center.x + std::cos(start) * m_rCenter,
                                        m_center.y + std::sin(start) * m_rCenter };
            const D2D1_POINT_2F p1 = { m_center.x + std::cos(start + sweep) * m_rCenter,
                                        m_center.y + std::sin(start + sweep) * m_rCenter };

            CComPtr<ID2D1PathGeometry> geo;
            CComPtr<ID2D1GeometrySink> sink;
            if (FAILED(ctx.D2D()->CreatePathGeometry(&geo))) continue;
            if (FAILED(geo->Open(&sink))) continue;

            sink->BeginFigure(p0, D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddArc(D2D1::ArcSegment(
                p1,
                D2D1::SizeF(m_rCenter, m_rCenter),
                0.f,
                D2D1_SWEEP_DIRECTION_CLOCKWISE,
                (sweep > 3.14159265f) ? D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE_SMALL));
            sink->EndFigure(D2D1_FIGURE_END_OPEN);
            sink->Close();
            m_geos[i] = geo;
        }

        m_needSegRebuild = false;
    }

    // ---- 空态：柔和「暂无数据」占位 ----
    void RenderEmpty(ID2D1RenderTarget* rt)
    {
        // 中央"空"符号：圆环 + 斜线
        const FLOAT iconR = m_rCenter * 0.28f;
        rt->DrawEllipse(D2D1::Ellipse(m_center, iconR, iconR), m_emptyIconBrush, 2.f);
        const FLOAT d = iconR * 0.7f;
        rt->DrawLine(D2D1::Point2F(m_center.x + d, m_center.y - d),
                     D2D1::Point2F(m_center.x - d, m_center.y + d), m_emptyIconBrush, 2.f);

        // 文案 16px
        D2D1_RECT_F rcText = { m_center.x - 160.f, m_center.y + iconR + 12.f,
                               m_center.x + 160.f, m_center.y + iconR + 36.f };
        rt->DrawText(L"暂无数据", 4, m_textSmall, rcText, m_labelBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

    // ---- 中心文字：总时长 + 标签 ----
    void RenderCenterText(ID2D1RenderTarget* rt)
    {
        float total = 0.f;
        for (auto& a : m_anims) total += a.current;

        wchar_t buf[64];
        if (total >= 60.f)
            swprintf_s(buf, L"%d:%02d", (int)(total / 60.f), (int)total % 60);
        else
            swprintf_s(buf, L"%d 分", (int)total);

        // 大数字 40px
        D2D1_RECT_F rcNum = { m_center.x - 160.f, m_center.y - 48.f,
                              m_center.x + 160.f, m_center.y + 16.f };
        rt->DrawText(buf, (UINT32)wcslen(buf), m_textBig, rcNum, m_numBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);

        // 标签 16px
        D2D1_RECT_F rcLabel = { m_center.x - 160.f, m_center.y + 20.f,
                                m_center.x + 160.f, m_center.y + 44.f };
        rt->DrawText(L"今日屏幕时间", 6, m_textSmall, rcLabel, m_labelBrush,
                     D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
    }

    // ---- 数据 / 状态 ----
    std::vector<Segment> m_segs;
    std::vector<AnimState> m_anims;
    bool m_empty = false;
    bool m_pending = false;
    bool m_legendMode = false;

    // ---- 布局缓存 ----
    D2D1_POINT_2F m_center = {};
    FLOAT m_radiusOuter = 0.f;
    FLOAT m_thickness = 0.f;
    FLOAT m_rCenter = 0.f;

    struct Angle { float start = 0.f, sweep = 0.f; };
    std::vector<Angle> m_angles;

    // ---- 资源（全部缓存，Render 零创建）----
    bool m_deviceReady = false;
    bool m_needSegRebuild = true;
    CComPtr<ID2D1SolidColorBrush> m_trackBrush;
    CComPtr<ID2D1SolidColorBrush> m_numBrush;
    CComPtr<ID2D1SolidColorBrush> m_labelBrush;
    CComPtr<ID2D1SolidColorBrush> m_emptyIconBrush;
    CComPtr<ID2D1RadialGradientBrush> m_shadow;
    CComPtr<ID2D1StrokeStyle> m_roundCap;
    CComPtr<IDWriteTextFormat> m_textBig;
    CComPtr<IDWriteTextFormat> m_textSmall;
    CComPtr<IDWriteTextFormat> m_legendNameFmt;
    CComPtr<IDWriteTextFormat> m_legendPctFmt;
    std::vector<CComPtr<ID2D1LinearGradientBrush>> m_gradients;
    std::vector<CComPtr<ID2D1PathGeometry>> m_geos;
};
