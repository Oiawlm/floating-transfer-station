using System.Runtime.InteropServices;

namespace FloatingTransferStation.Services;

internal static class NativeMethods
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmSettingChange = 0x001A;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmWindowPosChanging = 0x0046;
    internal const int WmHotKey = 0x0312;

    // RegisterHotKey 修饰键：Ctrl + Alt。
    internal const uint ModControl = 0x0002;
    internal const uint ModAlt = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);

    // DWM 窗口效果（Windows 11）。
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwaSystemBackdropType = 38;
    internal const int DwmcwpRound = 2;
    internal const int DwmbtMica = 2;

    // SetWindowPos 标志：迁移只用无 Z 序、无激活的矩形应用；守卫需清除忽略位置/尺寸的标志。
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    // hWndInsertAfter：置顶带的最高层（HWND_TOPMOST）。Topmost 属性只在 HWND
    // 创建时生效一次，其后出现的其他置顶窗口会排到上面，需要周期性重申。
    internal static readonly nint HwndTopmost = -1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    // 物理像素光标位置（虚拟屏幕坐标系）：贴边隐藏回位轮询用，与显示器矩形
    // 同坐标系直比，不做运行时 DPI 换算（窗口离屏后其 CompositionTarget 不可信）。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    // ---- 输入送达（2026-11-03 需求 6：左半裸单击把卡片文字送进外部输入框）----
    // 普适通道：剪贴板＋焦点还原＋模拟 Ctrl+V，禁止按进程名/窗口类适配具体软件。

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>窗口句柄是否属于本进程（送达目标捕获排除自身面板）。</summary>
    internal static bool IsOwnProcessWindow(nint hwnd) =>
        GetWindowThreadProcessId(hwnd, out var processId) != 0 && processId == Environment.ProcessId;

    internal const uint VkControl = 0x11;
    internal const uint VkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>鼠标输入成员：仅用于把 Input 联合体撑到 Win32 INPUT 的真实尺寸
    ///（x64＝40 字节，最大成员 MOUSEINPUT 决定联合体大小）。若联合体只含
    /// KeyboardInput，Marshal.SizeOf 会得到 32≠40，SendInput 直接以
    /// ERROR_INVALID_PARAMETER(87) 拒绝全部注入（2026-11-04 实测）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int DeltaX;
        public int DeltaY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;

        public const uint KeyboardType = 1;
        public const uint KeyEventFKeyUp = 0x0002;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);

    internal delegate bool MonitorEnumProc(nint monitor, nint deviceContext, ref NativeRect bounds, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProc enumerationProc,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPosition
    {
        public nint Hwnd;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }
}
