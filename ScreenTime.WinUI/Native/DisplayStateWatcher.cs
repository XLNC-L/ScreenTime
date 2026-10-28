using System.IO;
using System.Runtime.InteropServices;

namespace ScreenTime.Native;

/// <summary>
/// 监听显示器电源状态（<c>GUID_CONSOLE_DISPLAY_STATE</c>）：0 = 关闭，1 = 开启，2 = 变暗。
///
/// 为什么必须用这个而不是锁屏/睡眠去推：
/// 锁屏后屏幕仍然亮着（显示锁屏界面），而屏幕超时熄灭时系统并没有锁屏。
/// 只有这个通知能真正反映"屏幕亮没亮"。
/// </summary>
public sealed class DisplayStateWatcher : IDisposable
{
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>DEVICE_NOTIFY_CALLBACK：recipient 传 DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS 指针。</summary>
    private const uint DeviceNotifyCallback = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParams
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    /// <summary>
    /// POWER_SETTING 结构体不直接映射成 C# struct：
    /// 末尾的 Value 是「指向值的指针」而不是内联 DWORD，按 struct 读会拿到垃圾。
    /// 这里用固定偏移手动读，见 <see cref="OnNotify"/>。
    /// </summary>
    private delegate uint NotifyCallback(IntPtr context, uint type, IntPtr setting);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSettingRegisterNotification(
        ref Guid settingGuid, uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);

    // 委托必须由字段保活，否则 GC 回收后回调会崩
    private readonly NotifyCallback _callback;
    private IntPtr _paramsPtr;
    private IntPtr _handle;
    private bool _firstCallback = true;

    /// <summary>当前是否亮屏（变暗也算亮）。</summary>
    public bool IsOn { get; private set; } = true;

    /// <summary>状态变化回调，参数 true = 亮。</summary>
    public event Action<bool>? Changed;

    /// <summary>注册失败时为 false，调用方据此决定是否退回估算。</summary>
    public bool Available { get; private set; }

    public DisplayStateWatcher()
    {
        _callback = OnNotify;
    }

    public bool Start()
    {
        if (Available) return true;
        try
        {
            var subscribe = new SubscribeParams
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                Context = IntPtr.Zero,
            };
            _paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SubscribeParams>());
            Marshal.StructureToPtr(subscribe, _paramsPtr, false);

            var guid = ConsoleDisplayState;
            var rc = PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, _paramsPtr, out _handle);
            Available = rc == 0 && _handle != IntPtr.Zero;
            Log(Available ? "注册显示器状态通知成功" : $"注册失败 rc={rc}");
        }
        catch (Exception ex)
        {
            Available = false;
            Log($"注册异常 {ex.GetType().Name}");
        }
        return Available;
    }

    /// <summary>是否还没收到过真实状态（用于判断要不要补种子）。</summary>
    public bool NeedsSeed => _firstCallback;

    /// <summary>
    /// 启动时先用外部判断（如闲置时长）给一个初始状态。
    /// 若已经收到过系统的真实回调，则以真实状态为准，不覆盖。
    /// </summary>
    public void Seed(bool on)
    {
        if (!_firstCallback) return;
        IsOn = on;
    }

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (setting == IntPtr.Zero) return 0;

            // 回调收到的是 POWERBROADCAST_SETTING（不是 POWER_SETTING）：
            //   [0]  GUID  PowerSetting      16 字节
            //   [16] DWORD DataLength        本设置固定为 4
            //   [20] UCHAR Data[1]           值就内联在这里，DWORD 读法即 0/1/2
            // 注意 Data 只有 1 字节声明、靠结构体对齐才占到 4 字节，
            // 偏移 24 之后已经在结构体之外，读出来的是栈垃圾（曾经误当成指针解引用，
            // 直接 AccessViolationException 把进程打死）。
            var length = Marshal.ReadInt32(setting, 16);
            if (length != 4)
            {
                Log($"DataLength 异常 len={length}，忽略本次通知");
                return 0;
            }

            var value = (uint)Marshal.ReadInt32(setting, 20);
            if (value > 2)
            {
                Log($"显示器状态值异常 value={value}");
                return 0;
            }

            if (_firstCallback)
            {
                _firstCallback = false;
                Log($"首次回调解析成功 value={value}（1=亮 0=灭 2=变暗）");
            }

            var on = value != 0;
            if (on == IsOn) return 0;
            IsOn = on;
            Log(on ? "display_on" : "display_off");
            Changed?.Invoke(on);
        }
        catch
        {
            // 回调里不能抛异常
        }
        return 0;
    }

    /// <summary>
    /// 把状态变化记到 %AppData%\ScreenTime\display-state.log。
    /// 一天也就几条，留着方便排查"亮屏时长不对"这类问题。
    /// </summary>
    private static void Log(string line)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "display-state.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    public void Dispose()
    {
        try
        {
            if (_handle != IntPtr.Zero) PowerSettingUnregisterNotification(_handle);
        }
        catch
        {
            // 注销失败无所谓，进程退出时系统会回收
        }
        _handle = IntPtr.Zero;
        Available = false;

        if (_paramsPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_paramsPtr);
            _paramsPtr = IntPtr.Zero;
        }
    }
}
