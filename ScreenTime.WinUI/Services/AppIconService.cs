using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ScreenTime.Services;

/// <summary>从可执行文件提取关联图标并转为 WinUI 图像源。</summary>
public static class AppIconService
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string exePath) => GetIcon(exePath, null);

    /// <summary>
    /// 优先用显式图标源（UWP 包内导出的 PNG、快捷方式等）；
    /// 没有时用记录的可执行路径；路径失效（历史记录指向旧构建目录）时，
    /// 回退到同名运行中进程的真实路径，避免排行榜里出现空白图标。
    /// </summary>
    public static ImageSource? GetIcon(string exePath, string? processName, string? iconSource = null)
    {
        if (!string.IsNullOrWhiteSpace(iconSource) && File.Exists(iconSource))
        {
            if (Cache.TryGetValue(iconSource, out var cachedIcon)) return cachedIcon;
            var fromSource = LoadImageFile(iconSource);
            Cache[iconSource] = fromSource;
            if (fromSource != null) return fromSource;
        }

        var path = ResolvePath(exePath, processName);
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? result = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    using var bmp = icon.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.SetSource(ms.AsRandomAccessStream());
                    result = bitmap;
                }
            }
        }
        catch
        {
            result = null;
        }

        Cache[path] = result;
        return result;
    }

    /// <summary>直接读取一张图片文件（UWP 图标已由 UwpAppService 导出为 PNG）。</summary>
    private static ImageSource? LoadImageFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            // UWP 的 Square44x44Logo 四周留了一大圈透明边（实测「计算器」图形只占 31%），
            // 不裁的话同样尺寸下看着比 Win32 图标小一圈。
            using var ms = new MemoryStream(TrimTransparentPadding(bytes) ?? bytes);
            var bitmap = new BitmapImage();
            bitmap.SetSource(ms.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
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
}
