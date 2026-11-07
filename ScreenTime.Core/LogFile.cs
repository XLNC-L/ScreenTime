namespace ScreenTime;

/// <summary>
/// 文件日志的统一入口：超过上限先换名，再追加。
///
/// 之前各处直接 <c>File.AppendAllText</c>，文件只增不减；采集进程常驻，
/// agent.log 一年下来能到几十 MB，真出问题时翻不动，也太容易夹带陈年噪音。
/// 这里保留 <c>xxx.log.1</c> ~ <c>xxx.log.3</c> 三份历史：够定位跨天的
/// 「上一轮发生过什么」（v1.4.4 排查待机断档时，单份 .1 差点把证据滚掉），
/// 总占用最多 8 MB。
/// </summary>
public static class LogFile
{
    /// <summary>单文件上限。超过就滚成 .1（旧的依次后移）。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>保留的历史份数。</summary>
    private const int KeepCount = 3;

    private static readonly object Gate = new();

    /// <summary>按原样追加内容（调用方自己带换行）。任何异常都吞掉——日志不能反过来影响主流程。</summary>
    public static void Append(string path, string text)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            lock (Gate)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                        Roll(path);
                }
                catch
                {
                    // 滚动失败（被占用等）就继续写当前文件，不能因为轮转把日志本身丢了
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, text);
            }
        }
        catch
        {
            // 磁盘满 / 无权限：静默放弃
        }
    }

    /// <summary>追加一行（自动补换行）。</summary>
    public static void AppendLine(string path, string line) => Append(path, line + Environment.NewLine);

    /// <summary>当前文件滚成 .1，旧的依次后移，超出 KeepCount 的丢弃。</summary>
    private static void Roll(string path)
    {
        for (var i = KeepCount; i >= 1; i--)
        {
            var from = i == 1 ? path : $"{path}.{i - 1}";
            var to = $"{path}.{i}";
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
    }
}
