using System.Runtime.InteropServices;

namespace ScreenTime.Native;

/// <summary>
/// 通过 Core Audio 枚举当前正在输出声音的进程（按真实音频峰值判断，
/// 暂停/静音时峰值为 0）。
/// </summary>
public sealed class AudioMonitor
{
    private const double PeakThreshold = 0.0001;

    private const int ClsctxAll = 0x17;
    private static readonly Guid ClsIdMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IIdIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IIdIAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid IIdIAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out nint ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out nint ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint audioSessionGuid, int crossProcess, out IAudioSessionControl? sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(nint audioSessionGuid, int crossProcess, nint audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(nint client);
        [PreserveSig] int UnregisterSessionNotification(nint client);
        [PreserveSig] int RegisterDuckNotification(nint sessionId, nint client);
        [PreserveSig] int UnregisterDuckNotification(nint client);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionCount, out IAudioSessionControl sessionControl);
    }

    // 正确的 IAudioSessionControl：9 个方法
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, nint eventContext);
        [PreserveSig] int GetGroupingParam(out nint grouping);
        [PreserveSig] int SetGroupingParam(nint grouping, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(nint client);
        [PreserveSig] int UnregisterAudioSessionNotification(nint client);
    }

    // 正确的 IAudioSessionControl2：9 + 5 = 14 个方法
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, nint eventContext);
        [PreserveSig] int GetGroupingParam(out nint grouping);
        [PreserveSig] int SetGroupingParam(nint grouping, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(nint client);
        [PreserveSig] int UnregisterAudioSessionNotification(nint client);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out int pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(int u32ChannelCount, [In] nint afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out int pdwHardwareSupportMask);
    }

    /// <summary>返回当前正在输出声音的进程 PID 集合。</summary>
    public HashSet<uint> GetActiveProcessIds()
    {
        var result = new HashSet<uint>();
        object? enumeratorObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsIdMMDeviceEnumerator);
            if (type == null) return result;
            enumeratorObj = Activator.CreateInstance(type);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator) return result;

            // 0 = eRender, 0 = eConsole（默认输出设备）
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device == null)
                return result;

            var iid = IIdIAudioSessionManager2;
            if (device.Activate(ref iid, ClsctxAll, 0, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr)
                return result;

            if (mgr.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum == null)
                return result;

            sessionEnum.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out var session) != 0 || session == null) continue;
                var punk = nint.Zero;
                var p2 = nint.Zero;
                var pm = nint.Zero;
                try
                {
                    // 显式 QueryInterface 到 IAudioSessionControl2，再取真实 PID
                    punk = Marshal.GetIUnknownForObject(session);
                    var ctrl2Iid = IIdIAudioSessionControl2;
                    if (Marshal.QueryInterface(punk, ref ctrl2Iid, out p2) != 0 || p2 == 0) continue;
                    var c2 = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(p2);
                    c2.GetProcessId(out var pid);
                    if (pid == 0) continue; // 系统声音会话，不单独统计

                    // 峰值：显式 QueryInterface 到 IAudioMeterInformation
                    var meterIid = IIdIAudioMeterInformation;
                    if (Marshal.QueryInterface(punk, ref meterIid, out pm) == 0 && pm != 0)
                    {
                        var meter = (IAudioMeterInformation)Marshal.GetObjectForIUnknown(pm);
                        if (meter.GetPeakValue(out var peak) == 0 && peak > PeakThreshold)
                            result.Add(pid);
                    }
                }
                finally
                {
                    if (pm != 0) Marshal.Release(pm);
                    if (p2 != 0) Marshal.Release(p2);
                    if (punk != 0) Marshal.Release(punk);
                    Marshal.ReleaseComObject(session);
                }
            }
        }
        catch
        {
            // 音频服务不可用等情况，忽略本次采样
        }
        finally
        {
            if (enumeratorObj != null)
            {
                try { Marshal.ReleaseComObject(enumeratorObj); } catch { }
            }
        }
        return result;
    }
}
