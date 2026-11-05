using System.Runtime.InteropServices;

namespace ScreenTime.Agent.Native;

/// <summary>
/// 自绘托盘菜单（支持二级子菜单）。
///
/// 为什么不用 TrackPopupMenu：Win32 原生菜单在 Win11 上始终按系统「浅色 + 经典」渲染，
/// 补 comctl32 v6 清单、进程级 <c>SetPreferredAppMode(ForceDark)</c>、给宿主窗口挂
/// DarkMode_Explorer 主题都试过，一律无效——它不吃 WinUI 的主题。真用 MenuFlyout 又得
/// 先有 WinUI 窗口，与「采集进程常驻、界面关掉即退出」冲突，所以在托盘进程里自己画。
///
/// 窗口必须带 <c>WS_EX_TOOLWINDOW</c>(0x80)：无边框弹窗如果不带这个扩展样式，
/// Windows 会把它当成普通应用窗口，任务栏就会多出一个条目（之前 w 参数误写成 0x8，
/// 那是 WS_EX_TOPMOST，所以任务栏一直多一个 ScreenTime）。
/// </summary>
internal sealed class TrayMenuWindow : IDisposable
{
    /// <summary>一项菜单。<c>Id</c> 为 0 表示分隔线，<c>Children</c> 非空表示带子菜单。</summary>
    public readonly record struct Entry(string Text, int Id, IReadOnlyList<Entry>? Children = null);

    private const string WindowClassName = "ScreenTimeAgentTrayMenu_" + "v2";

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint CS_DROPSHADOW = 0x00020000;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_ACTIVATE = 0x0006;
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
    private const int ArrowWidth = 22;
    private const int MinWidth = 176;
    private const int CornerDiameter = 16;

    private const uint VK_ESCAPE = 0x1B;
    private const uint TME_LEAVE = 0x00000002;

    private static WndProcDelegate? _procHolder;
    /// <summary>按窗口句柄路由消息：父菜单和子菜单同时存在，不能只记「最后一个」。</summary>
    private static readonly Dictionary<IntPtr, TrayMenuWindow> OpenWindows = new();
    private static TrayMenuSession? _session;

    private static readonly IntPtr BgColor = Rgb(0x2B, 0x2B, 0x2B);
    private static readonly IntPtr HoverColor = Rgb(0x3A, 0x3A, 0x3A);
    private static readonly IntPtr TextColor = Rgb(0xE9, 0xE9, 0xE9);
    private static readonly IntPtr SeparatorColor = Rgb(0x3F, 0x3F, 0x3F);

    private readonly List<Entry> _entries;
    private readonly IntPtr _hFont;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _hWnd;
    private int _hoverIndex = -1;
    private int _openSubIndex = -1;
    private bool _trackingLeave;
    private bool _closing;

    private TrayMenuWindow(List<Entry> entries)
    {
        _entries = entries;
        _hFont = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI Variable Text");
        if (_hFont == IntPtr.Zero) _hFont = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI");

        var textWidth = 0;
        var height = PaddingY * 2;
        foreach (var entry in entries)
        {
            if (entry.Id == 0) { height += SeparatorHeight; continue; }
            height += ItemHeight;
            var w = MeasureText(entry.Text) + (entry.Children is { Count: > 0 } ? ArrowWidth : 0);
            textWidth = Math.Max(textWidth, w);
        }
        _width = Math.Max(MinWidth, textWidth + PaddingX * 2);
        _height = height;
    }

    /// <summary>弹出菜单根节点。</summary>
    public static bool Show(int screenX, int screenY, IReadOnlyList<Entry> entries, Action<int> onPick, Action<string>? log)
    {
        try
        {
            _session?.CloseAll();
            var session = new TrayMenuSession(onPick, log);
            _session = session;
            return session.OpenRoot(entries, screenX, screenY);
        }
        catch (Exception ex)
        {
            log?.Invoke("menu: 自绘菜单失败 " + ex.Message);
            return false;
        }
    }

    private bool ShowAt(int screenX, int screenY, bool above, Action<string>? log)
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

        var y = above ? screenY - _height : screenY;
        var x = screenX - _width / 2;

        var work = SystemParameters.WorkArea;
        if (x + _width > work.Right) x = work.Right - _width;
        if (x < work.Left) x = work.Left;
        if (y + _height > work.Bottom) y = work.Bottom - _height;
        if (y < work.Top) y = work.Top;

        _hWnd = CreateWindowExW(WS_EX_TOOLWINDOW, WindowClassName, "ScreenTime",
            WS_POPUP, x, y, _width, _height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hWnd == IntPtr.Zero)
        {
            log?.Invoke($"menu: CreateWindowEx 失败 err={Marshal.GetLastWin32Error()}");
            return false;
        }

        OpenWindows[_hWnd] = this;
        SetWindowRgn(_hWnd, CreateRoundRectRgn(0, 0, _width + 1, _height + 1, CornerDiameter, CornerDiameter), true);
        ShowWindow(_hWnd, 5 /*SW_SHOW*/);
        SetForegroundWindow(_hWnd);
        InvalidateRect(_hWnd, IntPtr.Zero, true);
        return true;
    }

    private int MeasureText(string text)
    {
        var hdc = GetDC(_hWnd);
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

    private int ItemTop(int index)
    {
        var y = PaddingY;
        for (var i = 0; i < index; i++)
            y += _entries[i].Id == 0 ? SeparatorHeight : ItemHeight;
        return y;
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

            if (i == _hoverIndex || i == _openSubIndex)
            {
                var hover = new RECT { Left = 4, Top = y, Right = _width - 4, Bottom = y + ItemHeight };
                var brush = CreateSolidBrush(HoverColor);
                FillRect(hdc, ref hover, brush);
                DeleteObject(brush);
            }

            SetTextColor(hdc, TextColor);
            var textRect = new RECT
            {
                Left = PaddingX,
                Top = y,
                Right = _width - PaddingX - (entry.Children is { Count: > 0 } ? ArrowWidth : 0),
                Bottom = y + ItemHeight
            };
            DrawTextW(hdc, entry.Text, -1, ref textRect, 0x00000024 /*DT_SINGLELINE|DT_VCENTER*/);

            if (entry.Children is { Count: > 0 })
            {
                var arrowRect = new RECT { Left = _width - PaddingX - ArrowWidth, Top = y, Right = _width - PaddingX, Bottom = y + ItemHeight };
                DrawTextW(hdc, "›", -1, ref arrowRect, 0x00000026 /*DT_SINGLELINE|DT_VCENTER|DT_RIGHT*/);
            }

            y += ItemHeight;
        }

        SelectObject(hdc, prevFont);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = OpenWindows.TryGetValue(hWnd, out var window) ? window : null;
        if (self is null) return DefWindowProcW(hWnd, msg, wParam, lParam);

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
                _session?.NotifyEntered(self);

                var y = (short)(((long)lParam >> 16) & 0xFFFF);   // 高 16 位才是 Y，低位是 X
                var index = self.HitTest(y);
                if (index != self._hoverIndex)
                {
                    self._hoverIndex = index;
                    // 从带子菜单的行移开到别的行 → 收起子菜单
                    if (index != self._openSubIndex) _session?.CloseSubsOf(self, ref self._openSubIndex);
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                }

                // 悬停到带箭头那行就展开子菜单（和系统菜单一致，不需要点击）
                if (index >= 0 && self._entries[index].Children is { Count: > 0 } children && index != self._openSubIndex)
                {
                    self._openSubIndex = index;
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                    _session?.OpenSub(self, children, self._width - 4, self.ItemTop(index) - PaddingY, self._hWnd);
                }
                return IntPtr.Zero;
            }

            case WM_MOUSELEAVE:
                self._trackingLeave = false;
                self._hoverIndex = -1;
                InvalidateRect(hWnd, IntPtr.Zero, false);
                _session?.NotifyLeft(self);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                var y = (short)(((long)lParam >> 16) & 0xFFFF);   // 高 16 位才是 Y，低位是 X
                var index = self.HitTest(y);
                if (index >= 0)
                {
                    var entry = self._entries[index];
                    if (entry.Children is { Count: > 0 }) return IntPtr.Zero;   // 父项点击不动作，交给悬停
                    _session?.Pick(entry.Id);
                }
                return IntPtr.Zero;
            }

            case WM_RBUTTONUP:
                _session?.CloseAll();
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((uint)wParam == VK_ESCAPE) _session?.CloseAll();
                return IntPtr.Zero;

            case WM_ACTIVATE:
                if (((long)wParam & 0xFFFF) == 0) _session?.ScheduleForegroundCheck();
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void DestroyNow()
    {
        if (_closing) return;
        _closing = true;
        var hwnd = _hWnd;
        _hWnd = IntPtr.Zero;
        if (hwnd != IntPtr.Zero) OpenWindows.Remove(hwnd);
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        if (_hFont != IntPtr.Zero) DeleteObject(_hFont);
    }

    public void Dispose() => DestroyNow();

    private static IntPtr Rgb(byte r, byte g, byte b) => (IntPtr)(r | (g << 8) | (b << 16));

    // ---------- 会话：管根菜单 + 子菜单的打开/关闭 ----------

    private sealed class TrayMenuSession
    {
        private readonly Action<int> _onPick;
        private readonly Action<string>? _log;
        private readonly List<TrayMenuWindow> _windows = new();
        private Timer? _leaveTimer;
        private Timer? _foregroundTimer;

        public TrayMenuSession(Action<int> onPick, Action<string>? log)
        {
            _onPick = onPick;
            _log = log;
        }

        public bool OpenRoot(IReadOnlyList<Entry> entries, int x, int y)
        {
            var window = new TrayMenuWindow(entries.ToList());
            if (!window.ShowAt(x, y, above: true, _log))
            {
                window.Dispose();
                return false;
            }
            _windows.Add(window);
            _log?.Invoke($"menu: 自绘菜单弹出 ({window._width}x{window._height})");
            return true;
        }

        /// <summary>在父窗口右侧展开子菜单。</summary>
        public void OpenSub(TrayMenuWindow parent, IReadOnlyList<Entry> entries, int clientX, int clientY, IntPtr parentHwnd)
        {
            var index = _windows.IndexOf(parent);
            if (index < 0) return;
            CloseFrom(index + 1);

            GetWindowRect(parentHwnd, out var parentRect);
            var sub = new TrayMenuWindow(entries.ToList());
            var x = parentRect.Left + clientX;
            var y = parentRect.Top + clientY;

            // 右边放不下就翻到父菜单左侧
            var work = SystemParameters.WorkArea;
            if (x + sub._width > work.Right) x = parentRect.Left - sub._width + 4;

            if (!sub.ShowAt(x, y, above: false, _log))
            {
                sub.Dispose();
                return;
            }
            _windows.Add(sub);
        }

        public void CloseSubsOf(TrayMenuWindow owner, ref int openIndex)
        {
            var index = _windows.IndexOf(owner);
            if (index < 0) return;
            CloseFrom(index + 1);
            openIndex = -1;
        }

        public void NotifyEntered(TrayMenuWindow window)
        {
            _leaveTimer?.Dispose();
            _leaveTimer = null;
        }


        /// <summary>鼠标离开某个窗口：给 250ms 缓冲，让指针有机会移进子菜单。</summary>
        public void NotifyLeft(TrayMenuWindow window)
        {
            _leaveTimer?.Dispose();
            _leaveTimer = new Timer(_ =>
            {
                try
                {
                    if (CursorInAnyWindow()) return;
                    CloseAll();
                }
                catch { }
            }, null, 250, Timeout.Infinite);
        }

        public void ScheduleForegroundCheck()
        {
            _foregroundTimer?.Dispose();
            // 展开子菜单会抢一次前台，这里稍微等一拍再判断，避免自己把自己关掉
            _foregroundTimer = new Timer(_ =>
            {
                try
                {
                    var fg = GetForegroundWindow();
                    foreach (var w in _windows)
                        if (w._hWnd == fg) return;
                    CloseAll();
                }
                catch { }
            }, null, 120, Timeout.Infinite);
        }

        private bool CursorInAnyWindow()
        {
            GetCursorPos(out var pt);
            foreach (var w in _windows)
            {
                if (w._hWnd == IntPtr.Zero) continue;
                GetWindowRect(w._hWnd, out var rect);
                if (pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom)
                    return true;
            }
            return false;
        }

        public void Pick(int id)
        {
            CloseAll();
            if (id > 0) _onPick(id);
        }

        public void CloseAll()
        {
            _leaveTimer?.Dispose();
            _leaveTimer = null;
            _foregroundTimer?.Dispose();
            _foregroundTimer = null;
            for (var i = _windows.Count - 1; i >= 0; i--) _windows[i].Dispose();
            _windows.Clear();
            if (ReferenceEquals(_session, this)) _session = null;
        }

        private void CloseFrom(int index)
        {
            for (var i = _windows.Count - 1; i >= index; i--)
            {
                _windows[i].Dispose();
                _windows.RemoveAt(i);
            }
        }
    }

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
    private struct POINT
    {
        public int X, Y;
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

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
