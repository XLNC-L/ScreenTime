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
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.SetSource(ms.AsRandomAccessStream());
            return bitmap;
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
