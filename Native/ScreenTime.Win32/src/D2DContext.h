// D2DContext.h — Direct2D / DirectWrite 基础设施
//
// 职责：
//   - 创建 D2D/DWrite 工厂与 HwndRenderTarget（PMv2 DPI 感知）
//   - 统一创建文字格式（Segoe UI Variable 优先，回退 Segoe UI）
//   - 提供渐变刷/纯色刷等资源工厂，资源在初始化时创建，渲染循环零分配
//
// 设计约束（8px 网格）：
//   字号用 8 的倍数：大数字 40px、正文 16px；间距 8/16/24/32。

#pragma once

#include <d2d1.h>
#include <d2d1helper.h>
#include <dwrite.h>
#include <dwmapi.h>
#include <atlbase.h>
#include <string_view>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")

// 柔和色板：同色系明暗渐变，低饱和、深色主题友好
namespace Palette
{
    // 背景 / 文字
    inline D2D1_COLOR_F Bg()             { return D2D1::ColorF(0x12151C, 0.93f); } // 深底，让 Mica 微微透出
    inline D2D1_COLOR_F Track()          { return D2D1::ColorF(0x232833, 1.0f); }
    inline D2D1_COLOR_F TextPrimary()    { return D2D1::ColorF(0xE8EDF5, 1.0f); }
    inline D2D1_COLOR_F TextSecondary()  { return D2D1::ColorF(0x8A93A6, 1.0f); }
    inline D2D1_COLOR_F TextMuted()      { return D2D1::ColorF(0x5A6272, 1.0f); }

    // 渐变分段色（a = 亮端，b = 暗端）
    struct GradientPair { D2D1_COLOR_F a, b; };
    inline GradientPair Teal()   { return { D2D1::ColorF(0x5FE3C9), D2D1::ColorF(0x2CB39F) }; }
    inline GradientPair Violet() { return { D2D1::ColorF(0xB695F5), D2D1::ColorF(0x7B5CD6) }; }
    inline GradientPair Amber()  { return { D2D1::ColorF(0xFFC982), D2D1::ColorF(0xF5A03F) }; }
    inline GradientPair Pink()   { return { D2D1::ColorF(0xF9A0BA), D2D1::ColorF(0xE96B90) }; }
    inline GradientPair Blue()   { return { D2D1::ColorF(0x7FBBFF), D2D1::ColorF(0x4D90E8) }; }
    inline GradientPair Sage()   { return { D2D1::ColorF(0x9AD99B), D2D1::ColorF(0x55B06B) }; }
    inline GradientPair Slate()  { return { D2D1::ColorF(0x7A8190), D2D1::ColorF(0x4E5563) }; }

    // 由亮端色生成渐变对（暗端 = 亮端 × 0.65，柔和过渡）
    inline GradientPair PairFrom(D2D1_COLOR_F a)
    {
        return { a, D2D1::ColorF(a.r * 0.65f, a.g * 0.65f, a.b * 0.65f, a.a) };
    }

    // 一级分类色板（复刻 WinUI 版 CategoryPalette Dark 主题）
    inline GradientPair CategoryColor(const char* name)
    {
        const std::string_view n = name ? name : "";
        if (n == "开发与创作") return PairFrom(D2D1::ColorF(0xC4B5FD));
        if (n == "办公与文档") return PairFrom(D2D1::ColorF(0xFFB900));
        if (n == "通讯与协作") return PairFrom(D2D1::ColorF(0x4DD9E6));
        if (n == "浏览与资讯") return PairFrom(D2D1::ColorF(0x4CC2FF));
        if (n == "影音与娱乐") return PairFrom(D2D1::ColorF(0xFF99A4));
        if (n == "游戏")       return PairFrom(D2D1::ColorF(0x6CCB5F));
        if (n == "社交与购物") return PairFrom(D2D1::ColorF(0xE689EE));
        if (n == "系统与工具") return PairFrom(D2D1::ColorF(0xA1A1A1));
        return PairFrom(D2D1::ColorF(0x989BA5)); // 未分类
    }

    // 用途色板（复刻 WinUI 版 PurposeBrushFor Dark 主题）
    inline GradientPair PurposeColor(const char* name)
    {
        const std::string_view n = name ? name : "";
        if (n == "工作学习") return PairFrom(D2D1::ColorF(0x4CC2FF));
        if (n == "生活事务") return PairFrom(D2D1::ColorF(0x4DD9E6));
        if (n == "消遣娱乐") return PairFrom(D2D1::ColorF(0xFFB900));
        if (n == "社交通讯") return PairFrom(D2D1::ColorF(0xC4B5FD));
        return PairFrom(D2D1::ColorF(0x989BA5)); // 未判定
    }
}

class D2DContext
{
public:
    ~D2DContext() { ReleaseDeviceResources(); }

    bool Create(HWND hwnd)
    {
        m_hwnd = hwnd;

        HRESULT hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &m_factory);
        if (FAILED(hr)) { m_lastHr = hr; return false; }

        hr = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                 reinterpret_cast<IUnknown**>(&m_dwFactory));
        if (FAILED(hr)) { m_lastHr = hr; return false; }

        if (!CreateDeviceResources())
        {
            ReleaseDeviceResources();
            return false;
        }
        return true;
    }

    HRESULT LastHr() const { return m_lastHr; }

    // 窗口隐藏到托盘时完整释放：渲染目标 + D2D/DWrite 工厂全部放掉，
    // 内存占用回落到纯托盘状态；重新显示时 Create 会重建
    void ReleaseAll()
    {
        m_rt.Release();
        m_factory.Release();
        m_dwFactory.Release();
    }

    void Resize(UINT cx, UINT cy)
    {
        if (m_rt)
            m_rt->Resize(D2D1::SizeU(cx, cy));
    }

    // ---- 渲染会话 ----
    void Begin() { m_rt->BeginDraw(); }
    void End() { m_rt->EndDraw(); }
    void Clear() { m_rt->Clear(Palette::Bg()); }
    ID2D1RenderTarget* RT() const { return m_rt; }
    ID2D1Factory* D2D() const { return m_factory; }
    IDWriteFactory* DWrite() const { return m_dwFactory; }

    // ---- 资源工厂（初始化时调用，不在渲染循环里分配）----

    // 文字格式：优先 Segoe UI Variable（Win11），失败回退 Segoe UI；
    // 默认水平居中，列表类文本可传 LEADING/TRAILING
    CComPtr<IDWriteTextFormat> MakeTextFormat(
        FLOAT sizePx,
        DWRITE_FONT_WEIGHT weight = DWRITE_FONT_WEIGHT_REGULAR,
        DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT_CENTER)
    {
        CComPtr<IDWriteTextFormat> fmt;
        HRESULT hr = m_dwFactory->CreateTextFormat(L"Segoe UI Variable Text", nullptr, weight,
                                                   DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                                   sizePx, L"zh-CN", &fmt);
        if (FAILED(hr))
            m_dwFactory->CreateTextFormat(L"Segoe UI", nullptr, weight,
                                          DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                          sizePx, L"zh-CN", &fmt);
        if (fmt)
        {
            fmt->SetTextAlignment(align);
            fmt->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        }
        return fmt;
    }

    CComPtr<ID2D1SolidColorBrush> MakeSolid(D2D1_COLOR_F color)
    {
        CComPtr<ID2D1SolidColorBrush> brush;
        m_rt->CreateSolidColorBrush(color, &brush);
        return brush;
    }

    // 沿 start → end 的线性渐变（用于环形图分段：内缘 → 外缘，柔和过渡）
    CComPtr<ID2D1LinearGradientBrush> MakeLinearGradient(
        D2D1_COLOR_F a, D2D1_COLOR_F b, D2D1_POINT_2F start, D2D1_POINT_2F end)
    {
        CComPtr<ID2D1GradientStopCollection> stops;
        D2D1_GRADIENT_STOP stopArr[2] = {
            { 0.0f, a },
            { 1.0f, b },
        };
        m_rt->CreateGradientStopCollection(stopArr, 2, &stops);

        CComPtr<ID2D1LinearGradientBrush> brush;
        m_rt->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(start, end), stops, &brush);
        return brush;
    }

    // 径向渐变（用于环形图背后的柔和阴影）
    CComPtr<ID2D1RadialGradientBrush> MakeRadialShadow(D2D1_POINT_2F center, FLOAT radius)
    {
        CComPtr<ID2D1GradientStopCollection> stops;
        D2D1_GRADIENT_STOP stopArr[2] = {
            { 0.0f, D2D1::ColorF(0x000000, 0.28f) },
            { 1.0f, D2D1::ColorF(0x000000, 0.00f) },
        };
        m_rt->CreateGradientStopCollection(stopArr, 2, &stops);

        CComPtr<ID2D1RadialGradientBrush> brush;
        m_rt->CreateRadialGradientBrush(
            D2D1::RadialGradientBrushProperties(center, D2D1::Point2F(), radius, radius),
            stops, &brush);
        return brush;
    }

private:
    bool CreateDeviceResources()
    {
        if (m_rt) return true;

        RECT rc;
        ::GetClientRect(m_hwnd, &rc);

        D2D1_RENDER_TARGET_PROPERTIES rtp = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
            0, 0, D2D1_RENDER_TARGET_USAGE_NONE, D2D1_FEATURE_LEVEL_DEFAULT);
        D2D1_HWND_RENDER_TARGET_PROPERTIES hrtp =
            D2D1::HwndRenderTargetProperties(m_hwnd, D2D1::SizeU(rc.right - rc.left, rc.bottom - rc.top));

        HRESULT hr = m_factory->CreateHwndRenderTarget(rtp, hrtp, &m_rt);
        if (FAILED(hr)) { m_lastHr = hr; return false; }

        // PMv2 感知：按窗口实际 DPI 设定渲染目标，全部布局使用 DIP
        FLOAT dpi = (FLOAT)::GetDpiForWindow(m_hwnd);
        m_rt->SetDpi(dpi, dpi);
        return true;
    }

    void ReleaseDeviceResources()
    {
        m_rt.Release();
    }

    HWND m_hwnd = nullptr;
    HRESULT m_lastHr = S_OK;
    CComPtr<ID2D1Factory> m_factory;
    CComPtr<IDWriteFactory> m_dwFactory;
    CComPtr<ID2D1HwndRenderTarget> m_rt;
};
