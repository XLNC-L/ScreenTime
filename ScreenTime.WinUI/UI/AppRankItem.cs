using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace ScreenTime.UI;

/// <summary>
/// 应用排行行。图标从 exe 提取是重活，先显示空占位、后台提取完成后
/// 再填充——需要实现 INPC 才能让绑定刷新。
/// </summary>
public sealed class AppRankItem : INotifyPropertyChanged
{
    public int Id { get; }
    public string Name { get; }

    private string _duration = "";
    /// <summary>可写：30 秒刷新时原地更新，避免整表重建（重建会丢掉已提取的图标）。</summary>
    public string Duration
    {
        get => _duration;
        private set
        {
            if (_duration == value) return;
            _duration = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        }
    }

    private double _barPercent;
    public double BarPercent
    {
        get => _barPercent;
        private set
        {
            if (Math.Abs(_barPercent - value) < 0.01) return;
            _barPercent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarPercent)));
        }
    }

    public string ExePath { get; }
    public string ProcessName { get; }
    public string IconSource { get; }

    /// <summary>刷新已有的行：只改时长和占比，图标保持不动。</summary>
    public void Update(string duration, double barPercent)
    {
        Duration = duration;
        BarPercent = barPercent;
    }

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

    public AppRankItem(int id, string name, string duration, double barPercent,
        string exePath, string processName, string iconSource)
    {
        Id = id;
        Name = name;
        Duration = duration;
        BarPercent = barPercent;
        ExePath = exePath;
        ProcessName = processName;
        IconSource = iconSource;
    }
}
