using System.Runtime.InteropServices;

namespace FloatingTransferStation.Services;

internal static class NativeMethods
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmSettingChange = 0x001A;

    // DWM 窗口效果（Windows 11）。
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwaSystemBackdropType = 38;
    internal const int DwmcwpRound = 2;
    internal const int DwmbtMica = 2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
