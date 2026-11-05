using System.Collections.ObjectModel;

namespace ScreenTime.UI;

/// <summary>
/// 按 key 把新数据同步进已有的 <see cref="ObservableCollection{T}"/>。
///
/// 之前每次刷新都是 <c>ItemsSource = 新 List</c>，等于整表重建：ListView 的虚拟化
/// 被抵消，滚动位置会跳，应用排行还会把已经提取好的图标全部丢掉重取。
/// 这里只做「删掉没了的 / 移动位置 / 原地更新」，UI 只收到变化的通知。
/// </summary>
public static class ListSync
{
    /// <param name="target">绑在 ItemsSource 上的集合（只赋值一次）。</param>
    /// <param name="next">这次查询出来的最新数据，已按目标顺序排好。</param>
    /// <param name="key">行的唯一标识。</param>
    /// <param name="update">同一 key 命中时的原地更新动作；为空表示保持不变。</param>
    public static void Apply<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> next,
        Func<T, object> key,
        Action<T, T>? update = null)
    {
        var nextKeys = new HashSet<object>();
        foreach (var item in next) nextKeys.Add(key(item));

        // 1) 移除已经不在结果里的行（倒序删，索引不漂移）
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!nextKeys.Contains(key(target[i]))) target.RemoveAt(i);
        }

        // 2) 按新顺序就位
        for (var i = 0; i < next.Count; i++)
        {
            var want = next[i];
            var wantKey = key(want);

            if (i < target.Count && key(target[i]).Equals(wantKey))
            {
                update?.Invoke(target[i], want);
                continue;
            }

            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (!key(target[j]).Equals(wantKey)) continue;
                found = j;
                break;
            }

            if (found >= 0)
            {
                target.Move(found, i);
                update?.Invoke(target[i], want);
            }
            else
            {
                target.Insert(i, want);
            }
        }
    }
}
