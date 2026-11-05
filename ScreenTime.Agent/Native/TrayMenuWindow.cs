using System.Runtime.InteropServices;

namespace ScreenTime.Agent.Native;

/// <summary>
/// 自绘托盘菜单。
///
/// 为什么不用 TrackPopupMenu：Win32 原生菜单在 Win11 上始终按系统「浅色 + 经典」渲染，
/// 试过补 comctl32 v6 清单、进程级 <c>SetPreferredAppMode(ForceDark)</c>、给宿主窗口挂
/// DarkMode_Explorer 主题，菜单照样是白底方角——它压根不吃 WinUI 的主题。
/// 而真用 WinUI 的 MenuFlyout 又必须先有一个 WinUI 窗口，与「采集进程常驻、界面关掉即退出」
/// 的架构冲突。折中办法就是这个：无边框弹窗自己画，视觉上和 WinUI MenuFlyout 对齐
/// （深色底、8px 圆角、34px 行高、悬停高亮、悬停离开即收起）。
/// </summary>
internal sealed class TrayMenuWindow : IDisposable
{
    /// <summary>一项菜单：Id 为 0 表示分隔线。</summary>
    public readonly record struct Entry(string Text, int Id);

    private static readonly string WindowClassName = "ScreenTimeAgentTrayMenu_" + Guid.NewGuid().ToString("N");

    private const uint WS_POPUP = 0x80000000;
    private const uint CS_DROPSHADOW = 0x00020000;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_ERASEBKGND = 0x0014;

    private const int ItemHeight = 34;
    private const int SeparatorHeight = 9;
    private const int PaddingY = 6;
    private const int PaddingX = 14;
    private const int MinWidth = 200;
    private const int CornerRadius = 16;   // 传给 CreateRoundRectRgn 的是直径

    private const uint VK_ESCAPE = 0x1B;
    private const uint TME_LEAVE = 0x00000002;

    private static WndProcDelegate? _procHolder;     // 防 GC
    private static TrayMenuWindow? _current;

    private static readonly IntPtr BgColor = Rgb(0x2B, 0x2B, 0x2B);
    private static readonly IntPtr HoverColor = Rgb(0x3A, 0x3A, 0x3A);
    private static readonly IntPtr TextColor = Rgb(0xE9, 0xE9, 0xE9);
    private static readonly IntPtr SeparatorColor = Rgb(0x3F, 0x3F, 0x3F);

    private readonly List<Entry> _entries;
    private readonly Action<int> _onPick;
    private readonly IntPtr _hFont;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _hWnd;
    private int _hoverIndex = -1;
    private bool _trackingLeave;
    private bool _picked;

    private TrayMenuWindow(List<Entry> entries, Action<int> onPick)
    {
        _entries = entries;
        _onPick = onPick;

        _hFont = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI Variable Text");
        if (_hFont == IntPtr.Zero) _hFont = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI");

        var textWidth = 0;
        var height = PaddingY * 2;
        foreach (var entry in entries)
        {
            if (entry.Id == 0) { height += SeparatorHeight; continue; }
            height += ItemHeight;
            textWidth = Math.Max(textWidth, MeasureText(entry.Text));
        }
        _width = Math.Max(MinWidth, textWidth + PaddingX * 2);
        _height = height;
    }

    /// <summary>在屏幕坐标处弹出菜单（左上角对齐鼠标，超出工作区会往上翻）。返回是否成功弹出。</summary>
    public static bool Show(int screenX, int screenY, IReadOnlyList<Entry> entries, Action<int> onPick, Action<string>? log)
    {
        try
        {
            var menu = new TrayMenuWindow(entries.ToList(), onPick);
            return menu.ShowAt(screenX, screenY, log);
        }
        catch (Exception ex)
        {
            log?.Invoke("menu: 自绘菜单失败 " + ex.Message);
            return false;
        }
    }

    private bool ShowAt(int screenX, int screenY, Action<string>? log)
    {
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_DROPSHADOW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procHolder = WndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
            lpszMenuName = ""
        };
        RegisterClassExW(ref wc);

        // 默认往上弹：托盘在右下角，菜单放鼠标上方更自然；超出屏幕再往下翻
        var y = screenY - _height;
        if (y < 0) y = screenY;
        var x = screenX - _width / 2;
        var work = SystemParameters.WorkArea;
        if (x + _width > work.Right) x = work.Right - _width;
        if (x < work.Left) x = work.Left;
        if (y + _height > work.Bottom) y = work.Bottom - _height;

        _hWnd = CreateWindowExW(0x00000008 /*WS_EX_TOOLWINDOW*/, WindowClassName, "ScreenTime",
            WS_POPUP, x, y, _width, _height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hWnd == IntPtr.Zero)
        {
            log?.Invoke($"menu: CreateWindowEx 失败 err={Marshal.GetLastWin32Error()}");
            return false;
        }

        _current = this;
        SetWindowRgn(_hWnd, CreateRoundRectRgn(0, 0, _width + 1, _height + 1, CornerRadius, CornerRadius), true);
        ShowWindow(_hWnd, 5 /*SW_SHOW*/);
        SetForegroundWindow(_hWnd);
        InvalidateRect(_hWnd, IntPtr.Zero, true);
        log?.Invoke($"menu: 自绘菜单弹出 ({_width}x{_height}) at ({x},{y})");
        return true;
    }

    private int MeasureText(string text)
    {
        var hdc = GetDC(_hWnd == IntPtr.Zero ? IntPtr.Zero : _hWnd);
        var prev = SelectObject(hdc, _hFont);
        GetTextExtentPoint32W(hdc, text, text.Length, out var size);
        SelectObject(hdc, prev);
        ReleaseDC(IntPtr.Zero, hdc);
        return size.cx;
    }

    private int HitTest(int clientY)
    {
        var y = PaddingY;
        for (var i = 0; i < _entries.Count; i++)
        {
            var h = _entries[i].Id == 0 ? SeparatorHeight : ItemHeight;
            if (clientY >= y && clientY < y + h) return _entries[i].Id == 0 ? -1 : i;
            y += h;
        }
        return -1;
    }

    private void Paint(IntPtr hdc)
    {
        var rect = new RECT { Left = 0, Top = 0, Right = _width, Bottom = _height };
        var bg = CreateSolidBrush(BgColor);
        FillRect(hdc, ref rect, bg);
        DeleteObject(bg);

        var prevFont = SelectObject(hdc, _hFont);
        SetBkMode(hdc, 1 /*TRANSPARENT*/);

        var y = PaddingY;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Id == 0)
            {
                var sep = new RECT { Left = PaddingX, Top = y + SeparatorHeight / 2, Right = _width - PaddingX, Bottom = y + SeparatorHeight / 2 + 1 };
                var brush = CreateSolidBrush(SeparatorColor);
                FillRect(hdc, ref sep, brush);
                DeleteObject(brush);
                y += SeparatorHeight;
                continue;
            }

            if (i == _hoverIndex)
            {
                var hover = new RECT { Left = 4, Top = y, Right = _width - 4, Bottom = y + ItemHeight };
                var brush = CreateSolidBrush(HoverColor);
                FillRect(hdc, ref hover, brush);
                DeleteObject(brush);
            }

            SetTextColor(hdc, TextColor);
            var textRect = new RECT { Left = PaddingX, Top = y, Right = _width - PaddingX, Bottom = y + ItemHeight };
            DrawTextW(hdc, entry.Text, -1, ref textRect,
                0x00000024 /*DT_SINGLELINE*/ | 0x00000004 /*DT_VCENTER*/ | 0x00000000 /*DT_LEFT*/);
            y += ItemHeight;
        }

        SelectObject(hdc, prevFont);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is null || self._hWnd != hWnd) return DefWindowProcW(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_PAINT:
            {
                var ps = new PAINTSTRUCT();
                var hdc = BeginPaint(hWnd, ref ps);
                self.Paint(hdc);
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }
            case WM_ERASEBKGND:
                return (IntPtr)1;

            case WM_MOUSEMOVE:
            {
                if (!self._trackingLeave)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = hWnd
                    };
                    TrackMouseEvent(ref tme);
                    self._trackingLeave = true;
                }

                var y = (short)((long)lParam & 0xFFFF);
                var index = self.HitTest(y);
                if (index != self._hoverIndex)
                {
                    self._hoverIndex = index;
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            }

            case WM_MOUSELEAVE:
                self._trackingLeave = false;
                self._hoverIndex = -1;
                InvalidateRect(hWnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                var y = (short)((long)lParam & 0xFFFF);
                var index = self.HitTest(y);
                if (index >= 0)
                {
                    self._picked = true;
                    var id = self._entries[index].Id;
                    self.Close();
                    self._onPick(id);
                }
                return IntPtr.Zero;
            }

            case WM_RBUTTONUP:
                self.Close();
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((uint)wParam == VK_ESCAPE) self.Close();
                return IntPtr.Zero;

            case WM_ACTIVATE:
                // 点到别处（菜单失去激活）就收起，和系统菜单一致
                if (((long)wParam & 0xFFFF) == 0) self.Close();
                return IntPtr.Zero;

            case WM_KILLFOCUS:
                self.Close();
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Close()
    {
        if (_hWnd == IntPtr.Zero) return;
        var hwnd = _hWnd;
        _hWnd = IntPtr.Zero;
        ReleaseCapture();
        DestroyWindow(hwnd);
        if (ReferenceEquals(_current, this)) _current = null;
        if (!_picked) _onPick(int.MinValue);   // 通知调用方「只是关掉了」
    }

    public void Dispose()
    {
        if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd);
        _hWnd = IntPtr.Zero;
        if (_hFont != IntPtr.Zero) DeleteObject(_hFont);
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private static IntPtr Rgb(byte r, byte g, byte b) => (IntPtr)(r | (g << 8) | (b << 16));

    // ---------- Win32 ----------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    private static class SystemParameters
    {
        public static RECT WorkArea
        {
            get
            {
                SystemParametersInfoW(0x0030 /*SPI_GETWORKAREA*/, 0, out RECT rect, 0);
                return rect;
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll")]
    private static extern IntPtr ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(IntPtr crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement, int nOrientation,
        int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
        uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SetTextColor(IntPtr hdc, IntPtr color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
