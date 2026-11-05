namespace ScreenTime;

/// <summary>
/// 文件日志的统一入口：超过上限先换名，再追加。
///
/// 之前各处直接 <c>File.AppendAllText</c>，文件只增不减；采集进程常驻，
/// agent.log 一年下来能到几十 MB，真出问题时翻不动，也太容易夹带陈年噪音。
/// 这里只保留一份 <c>xxx.log.1</c>：够定位「上一轮发生过什么」，又不至于失控。
/// </summary>
public static class LogFile
{
    /// <summary>单文件上限。超过就滚成 .1（覆盖旧的）。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

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
                        File.Move(path, path + ".1", overwrite: true);
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
}
