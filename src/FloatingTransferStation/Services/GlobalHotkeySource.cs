namespace FloatingTransferStation.Services;

/// <summary>
/// 全局快捷键唤起的注册源抽象：MainWindow 依赖它完成系统热键的注册与注销，
/// 测试用替身验证注册时机、失败路径与注销边界。
/// </summary>
public interface IGlobalHotkeySource
{
    bool TryRegister(nint windowHandle, int hotkeyId);

    void Unregister(nint windowHandle, int hotkeyId);
}

/// <summary>
/// Win32 实现：注册 Ctrl + Alt + V 唤起面板。热键默认关闭，
/// 仅用户在设置中显式开启时注册；组合键被其他软件占用时注册失败并如实提示。
/// </summary>
public sealed class Win32GlobalHotkeySource : IGlobalHotkeySource
{
    public const int HotkeyId = 0x4654;
    public const uint VirtualKeyV = 0x56;

    public static string DisplayText => "Ctrl + Alt + V";

    public bool TryRegister(nint windowHandle, int hotkeyId) =>
        NativeMethods.RegisterHotKey(
            windowHandle,
            hotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt,
            VirtualKeyV);

    public void Unregister(nint windowHandle, int hotkeyId) =>
        NativeMethods.UnregisterHotKey(windowHandle, hotkeyId);
}
