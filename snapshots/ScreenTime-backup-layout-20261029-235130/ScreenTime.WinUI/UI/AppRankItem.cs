using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace ScreenTime.UI;

/// <summary>
/// 应用排行行。图标从 exe 提取是重活，先显示空占位、后台提取完成后
/// 再填充——需要实现 INPC 才能让绑定刷新。
/// </summary>
public sealed class AppRankItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string Duration { get; }
    public double BarPercent { get; }
    public string ExePath { get; }
    public string ProcessName { get; }
    public string IconSource { get; }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppRankItem(string name, string duration, double barPercent,
        string exePath, string processName, string iconSource)
    {
        Name = name;
        Duration = duration;
        BarPercent = barPercent;
        ExePath = exePath;
        ProcessName = processName;
        IconSource = iconSource;
    }
}
