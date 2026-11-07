using System.Runtime.InteropServices;

namespace ScreenTime.Native;

/// <summary>
/// UI Automation 客户端 COM 接口（最小子集，用于读取 Chromium 浏览器地址栏）。
/// vtable 顺序严格对应 Windows SDK UIAutomationClient.h。
/// </summary>
internal static class UiaIds
{
    public const int ControlTypeProperty = 30003;
    public const int NameProperty = 30005;
    public const int EditControlType = 50004;
    public const int DocumentControlType = 50030;
    public const int ValuePatternId = 10002;
    public const int TreeScopeDescendants = 4;
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiautomation
{
    int CompareElements(nint a, nint b, out int same);
    int CompareRuntimeIds(nint r1, nint r2, out int same);
    int GetRootElement(out nint root);
    int ElementFromHandle(nint hwnd, out IUiautomationElement element);
    int ElementFromPoint(int x, int y, out nint element);
    int GetFocusedElement(out nint element);
    int GetRootElementBuildCache(nint cache, out nint root);
    int ElementFromHandleBuildCache(nint hwnd, nint cache, out nint element);
    int ElementFromPointBuildCache(int x, int y, nint cache, out nint element);
    int GetFocusedElementBuildCache(nint cache, out nint element);
    int CreateTreeWalker(nint condition, out nint walker);
    int get_ControlViewWalker(out nint walker);
    int get_ContentViewWalker(out nint walker);
    int get_RawViewWalker(out nint walker);
    int get_RawViewCondition(out nint condition);
    int get_ControlViewCondition(out nint condition);
    int get_ContentViewCondition(out nint condition);
    int CreateCacheRequest(out nint cache);
    int CreateTrueCondition(out nint condition);
    int CreateFalseCondition(out nint condition);
    int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value,
        out nint condition);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiautomationElement
{
    int SetFocus();
    int GetRuntimeId(out nint runtimeId);
    int FindFirst(int scope, nint condition, out IUiautomationElement found);
    int FindAll(int scope, nint condition, out nint found);
    int FindFirstBuildCache(int scope, nint condition, nint cache, out nint found);
    int FindAllBuildCache(int scope, nint condition, nint cache, out nint found);
    int BuildUpdatedCache(nint cache, out nint element);
    int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    int GetCurrentPropertyValueEx(int propertyId, int ignoreDefault,
        [MarshalAs(UnmanagedType.Struct)] out object value);
    int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    int GetCachedPropertyValueEx(int propertyId, int ignoreDefault,
        [MarshalAs(UnmanagedType.Struct)] out object value);
    int GetCurrentPatternAs(int patternId, ref Guid riid, out nint pattern);
    int GetCachedPatternAs(int patternId, ref Guid riid, out nint pattern);
    int GetCurrentPattern(int patternId, out nint pattern);
    int GetCachedPattern(int patternId, out nint pattern);
    int GetCachedParent(out nint parent);
    int GetCachedChildren(out nint found);
}

[ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiautomationValuePattern
{
    int SetValue([MarshalAs(UnmanagedType.BStr)] string value);
    int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);
    int get_CurrentIsReadOnly(out int readOnly);
    int get_CachedValue([MarshalAs(UnmanagedType.BStr)] out string value);
    int get_CachedIsReadOnly(out int readOnly);
}

/// <summary>CUIAutomation CoClass。</summary>
[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e"),
 ClassInterface(ClassInterfaceType.None)]
internal class CUiautomationClass
{
}
