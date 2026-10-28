using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ScreenTime.Services;

/// <summary>
/// 从可执行文件提取关联图标并转为 WinUI 图像源。
///
/// 提取链路拆成两段：
///  - <see cref="GetIconBytes"/>：提取 + PNG 编码 + 透明裁剪，是重活，任意线程可调（内部缓存字节）；
///  - <see cref="BytesToImage"/>：字节转 BitmapImage，必须留在 UI 线程，解码限 48px 内存很小。
/// 页面排行先显示空占位，后台线程取字节、UI 线程解码填充，首屏不再卡。
/// </summary>
public static class AppIconService
{
    /// <summary>提取字节缓存：路径 → PNG 字节（ConcurrentDictionary 供后台线程安全访问）。</summary>
    private static readonly ConcurrentDictionary<string, byte[]?> BytesCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UI 线程 ImageSource 缓存，避免同一路径反复解码。</summary>
    private static readonly Dictionary<string, ImageSource?> UiCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object UiLock = new();

    /// <summary>显示尺寸 24 DIP；2x 缩放下 48 物理像素，解码限此尺寸即可，256 原图不占内存。</summary>
    private const int DecodePx = 48;

    public static ImageSource? GetIcon(string exePath) => GetIcon(exePath, null);

    /// <summary>UI 线程调用：取缓存字节并转 BitmapImage（未缓存时同步提取，可能卡，慎用）。</summary>
    public static ImageSource? GetIcon(string exePath, string? processName, string? iconSource = null)
    {
        var key = KeyFor(exePath, processName, iconSource);
        if (key.Length == 0) return null;

        var cached = GetCachedIcon(exePath, processName, iconSource);
        if (cached != null) return cached;

        var img = BytesToImage(GetIconBytes(exePath, processName, iconSource));
        lock (UiLock)
        {
            UiCache[key] = img;
        }
        return img;
    }

    /// <summary>UI 线程调用：只在缓存命中时返回 ImageSource，未命中返回 null（交给后台提取）。</summary>
    public static ImageSource? GetCachedIcon(string exePath, string? processName, string? iconSource = null)
    {
        var key = KeyFor(exePath, processName, iconSource);
        if (key.Length == 0) return null;
        lock (UiLock)
        {
            return UiCache.TryGetValue(key, out var c) ? c : null;
        }
    }

    private static string KeyFor(string exePath, string? processName, string? iconSource)
    {
        if (!string.IsNullOrWhiteSpace(iconSource)) return iconSource;
        if (!string.IsNullOrWhiteSpace(exePath)) return exePath;
        return processName ?? "";
    }

    /// <summary>
    /// 任意线程调用：取图标的 PNG 字节。
    /// 优先用显式图标源（UWP 包内导出的 PNG、快捷方式等）；
    /// 没有时用记录的可执行路径（高分辨率提取，256 → 128 → 64 → 32 逐级回退）；
    /// 路径失效（历史记录指向旧构建目录）时，回退到同名运行中进程的真实路径。
    /// </summary>
    public static byte[]? GetIconBytes(string exePath, string? processName, string? iconSource = null)
    {
        if (!string.IsNullOrWhiteSpace(iconSource) && File.Exists(iconSource))
        {
            if (BytesCache.TryGetValue(iconSource, out var cachedSource)) return cachedSource;
            var bytes = LoadPngBytes(iconSource);
            BytesCache[iconSource] = bytes;
            if (bytes != null) return bytes;
        }

        var path = ResolvePath(exePath, processName);
        if (string.IsNullOrEmpty(path)) return null;
        if (BytesCache.TryGetValue(path, out var cached)) return cached;

        byte[]? result = null;
        try
        {
            if (File.Exists(path))
            {
                result = ExtractHighResIcon(path) ?? ExtractAssociatedIconBytes(path);
            }
        }
        catch
        {
            result = null;
        }

        BytesCache[path] = result;
        return result;
    }

    /// <summary>UI 线程调用：PNG 字节转 BitmapImage，解码尺寸限 <see cref="DecodePx"/>。</summary>
    public static ImageSource? BytesToImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage { DecodePixelWidth = DecodePx };
            bitmap.SetSource(ms.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>UI 线程调用：解码并写入 ImageSource 缓存，排行榜重建后能立即回显。</summary>
    public static ImageSource? BytesToImageCached(byte[]? bytes, string exePath, string? processName, string? iconSource)
    {
        var img = BytesToImage(bytes);
        var key = KeyFor(exePath, processName, iconSource);
        if (img != null && key.Length > 0)
        {
            lock (UiLock)
            {
                UiCache[key] = img;
            }
        }
        return img;
    }

    /// <summary>直接读取一张图片文件（UWP 图标已由 UwpAppService 导出为 PNG）。</summary>
    private static byte[]? LoadPngBytes(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            // UWP 的 Square44x44Logo 四周留了一大圈透明边（实测「计算器」图形只占 31%），
            // 不裁的话同样尺寸下看着比 Win32 图标小一圈。
            return TrimTransparentPadding(bytes) ?? bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 exe/dll/ico 提取大图标并转 PNG 字节。
    /// <see cref="Icon.ExtractAssociatedIcon"/> 最多只能拿到 32×32，高 DPI 下拉伸发糊。
    /// 用 SHDefExtractIcon（large 按请求尺寸返回）——不要用 PrivateExtractIcons：
    /// 部分 Windows 11 版本的 shell32 已不导出该符号，调用会抛 EntryPointNotFoundException，
    /// 导致整条图标链路静默失效（所有图标空白）。
    /// </summary>
    private static byte[]? ExtractHighResIcon(string path)
    {
        foreach (var size in new[] { 256, 32 })
        {
            var large = IntPtr.Zero;
            var small = IntPtr.Zero;
            try
            {
                if (SHDefExtractIcon(path, 0, 0, out large, out small, (uint)size) != 0 || large == IntPtr.Zero)
                    continue;

                using var icon = System.Drawing.Icon.FromHandle(large);
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                if (ms.Length > 0) return ms.ToArray();
            }
            catch
            {
                // 该尺寸提取失败，继续回退
            }
            finally
            {
                // SHDefExtractIcon 创建的句柄必须显式销毁，FromHandle 不拥有它
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }
        return null;
    }

    /// <summary>兜底：ExtractAssociatedIcon 的 32×32。</summary>
    private static byte[]? ExtractAssociatedIconBytes(string path)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon == null) return null;
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// 裁掉四周全透明的像素，再补成正方形画布（留约 4% 边距）。
    /// 图形本来就占满画布时返回 null，避免无谓重绘。
    /// 用 LockBits 而不是 GetPixel：300×300 的图标逐像素取色要几百毫秒。
    /// </summary>
    private static byte[]? TrimTransparentPadding(byte[] png)
    {
        try
        {
            using var input = new MemoryStream(png);
            using var src = new System.Drawing.Bitmap(input);

            var rect = new System.Drawing.Rectangle(0, 0, src.Width, src.Height);
            var data = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
            try
            {
                var stride = Math.Abs(data.Stride);
                var buffer = new byte[stride * src.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                for (var y = 0; y < src.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < src.Width; x++)
                    {
                        // Format32bppArgb 在内存里是 BGRA，alpha 落在第 4 个字节
                        if (buffer[row + x * 4 + 3] <= 8) continue;
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            finally
            {
                src.UnlockBits(data);
            }

            if (maxX < minX || maxY < minY) return null; // 全透明，交给调用方用原图

            var w = maxX - minX + 1;
            var h = maxY - minY + 1;
            if (w >= src.Width * 0.9 && h >= src.Height * 0.9) return null; // 本来就很满

            var side = Math.Max(8, (int)Math.Ceiling(Math.Max(w, h) * 1.08));
            using var dst = new System.Drawing.Bitmap(side, side);
            using (var g = System.Drawing.Graphics.FromImage(dst))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src,
                    new System.Drawing.Rectangle((side - w) / 2, (side - h) / 2, w, h),
                    new System.Drawing.Rectangle(minX, minY, w, h),
                    System.Drawing.GraphicsUnit.Pixel);
            }

            using var output = new MemoryStream();
            dst.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePath(string exePath, string? processName)
    {
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath)) return exePath;

        if (string.IsNullOrEmpty(processName)) return "";

        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(file) && File.Exists(file)) return file;
                }
                catch
                {
                    // 受保护进程读不到模块路径，继续尝试下一个
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // 忽略：取不到路径时按无图标处理
        }
        return "";
    }

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string lpszFile, int nIconIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
