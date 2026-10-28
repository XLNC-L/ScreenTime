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
    /// 优先用记录的可执行路径；路径失效（历史记录指向旧构建目录）时，
    /// 回退到同名运行中进程的真实路径，避免排行榜里出现空白图标。
    /// </summary>
    public static ImageSource? GetIcon(string exePath, string? processName)
    {
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
