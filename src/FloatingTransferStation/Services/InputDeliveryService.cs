using System.Runtime.InteropServices;
using System.Windows;

namespace FloatingTransferStation.Services;

/// <summary>一次送达的结局（状态条提示与测试断言用）。</summary>
public enum InputDeliveryOutcome
{
    /// <summary>已送达：负载写入、目标前台化、粘贴已注入、原剪贴板已恢复。</summary>
    Delivered,

    /// <summary>送达完成但原剪贴板恢复失败（粘贴已生效，剪贴板保留送达负载）。</summary>
    DeliveredRestoreFailed,

    /// <summary>没有可用送达目标（未捕获到外部前台窗口或目标已关闭）。</summary>
    NoTarget,

    /// <summary>负载写入剪贴板失败（剪贴板被占用等，未触碰焦点）。</summary>
    ClipboardWriteFailed,

    /// <summary>目标窗口前台化失败（焦点还原未达成，粘贴未注入，剪贴板已还原）。</summary>
    FocusFailed,

    /// <summary>粘贴注入被系统拒绝（UIPI 限制等，剪贴板已还原）。</summary>
    SendFailed
}

/// <summary>
/// 输入送达的操作系统接缝：前台窗口、窗口校验、焦点还原、Ctrl+V 注入、剪贴板
/// 备份/写/恢复与节奏延时。生产实现走 Win32/WPF；测试注入假件断言序列与失败
/// 分支（真实剪贴板与真实输入注入不进测试）。
/// </summary>
public interface IInputDeliveryNative
{
    nint GetForegroundWindow();

    bool IsWindow(nint hwnd);

    bool IsOwnProcessWindow(nint hwnd);

    bool SetForegroundWindow(nint hwnd);

    /// <summary>注入一次 Ctrl+V 按键序列；返回是否全部注入成功。</summary>
    bool SendControlV();

    IDataObject? GetClipboard();

    bool WriteClipboard(IDataObject data);

    bool SetClipboard(IDataObject data);

    Task DelayAsync(TimeSpan delay);
}

/// <summary>Win32/WPF 生产实现（剪贴板异常与 SendInput 结果如实返回）。</summary>
internal sealed class Win32InputDeliveryNative : IInputDeliveryNative
{
    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool IsWindow(nint hwnd) => hwnd != 0 && NativeMethods.IsWindow(hwnd);

    public bool IsOwnProcessWindow(nint hwnd) => NativeMethods.IsOwnProcessWindow(hwnd);

    public bool SetForegroundWindow(nint hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    public bool SendControlV()
    {
        NativeMethods.Input Down(uint key) => new()
        {
            Type = NativeMethods.Input.KeyboardType,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput { VirtualKey = key }
            }
        };

        NativeMethods.Input Up(uint key) => new()
        {
            Type = NativeMethods.Input.KeyboardType,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = key,
                    Flags = NativeMethods.Input.KeyEventFKeyUp
                }
            }
        };

        var inputs = new[]
        {
            Down(NativeMethods.VkControl),
            Down(NativeMethods.VkV),
            Up(NativeMethods.VkV),
            Up(NativeMethods.VkControl)
        };
        return NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.Input>()) == inputs.Length;
    }

    public IDataObject? GetClipboard()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } current)
            {
                return null;
            }

            // 系统剪贴板返回的数据对象视图不能整体回写（SetDataObject 回写实测
            // 得到空剪贴板——延迟渲染视图不随对象迁移，2026-11-04 实测）。备份
            // 必须物化：把送达通道可能覆盖的常用格式复制进新的 DataObject。
            var snapshot = new DataObject();
            var hasAny = false;
            if (current.GetDataPresent(DataFormats.UnicodeText) &&
                current.GetData(DataFormats.UnicodeText) is string text)
            {
                snapshot.SetText(text);
                hasAny = true;
            }

            if (current.GetDataPresent(DataFormats.FileDrop) &&
                current.GetData(DataFormats.FileDrop) is string[] files)
            {
                snapshot.SetFileDropList([.. files]);
                hasAny = true;
            }

            if (current.GetDataPresent(DataFormats.Bitmap) &&
                current.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                snapshot.SetImage(bitmap);
                hasAny = true;
            }

            return hasAny ? snapshot : null;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool WriteClipboard(IDataObject data) => TryClipboard(() => Clipboard.SetDataObject(data, true));

    public bool SetClipboard(IDataObject data) => TryClipboard(() => Clipboard.SetDataObject(data, true));

    public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);

    private static bool TryClipboard(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// 输入送达子系统（2026-11-03 需求 6，契约 #36 第三递送通道）：把卡片文字经
/// 「剪贴板＋焦点还原＋模拟 Ctrl+V」普适通道送进用户当前正在使用的外部输入框
///（微信/浏览器等任意目标，禁止按进程名/窗口类适配）。送达目标＝面板
/// MouseEnter／卡片按压时刻捕获的外部前台窗口快照（本进程窗口不capture，保证
/// 落在用户刚才打字的窗口）；负载与复制通道同构（携带内部条目标记，自动采集
/// 跳过，不产生重复卡）。送达后延时恢复用户原剪贴板内容。
/// </summary>
public sealed class InputDeliveryService
{
    private readonly IInputDeliveryNative _native;

    // 目标前台化（含系统切换动画）与粘贴生效的节奏间隔：过短会与目标应用的
    // 焦点/粘贴处理竞态，过长拖慢连发；实测取值，evidence 录屏验证。
    private static readonly TimeSpan ForegroundSettleDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PasteSettleDelay = TimeSpan.FromMilliseconds(250);

    private nint _targetWindow;

    public InputDeliveryService(IInputDeliveryNative? native = null)
    {
        _native = native ?? new Win32InputDeliveryNative();
    }

    /// <summary>当前是否有可送达目标（捕获过外部前台窗口且窗口仍存在）。</summary>
    public bool HasTarget => _native.IsWindow(_targetWindow);

    /// <summary>
    /// 捕获送达目标：当前前台窗口属于其他进程时更新快照；前台是本进程（面板
    /// 已被点击激活）或无前台时保留既有快照——用户刚才打字的窗口才是送达目标。
    /// 面板 MouseEnter 与卡片按压时刻各调用一次（按压激活面板前的最后一次
    /// 刷新总是来自外部前台）。
    /// </summary>
    public void CaptureForegroundTarget()
    {
        var foreground = _native.GetForegroundWindow();
        if (foreground != 0 && !_native.IsOwnProcessWindow(foreground))
        {
            _targetWindow = foreground;
        }
    }

    /// <summary>
    /// 完整送达序列：备份原剪贴板→写负载→目标前台化→延时→注入 Ctrl+V→延时→
    /// 恢复原剪贴板。任一步失败按 <see cref="InputDeliveryOutcome"/> 归因返回，
    /// 已写剪贴板的失败路径先恢复备份再返回（不把送达负载遗留给用户）。
    /// </summary>
    public async Task<InputDeliveryOutcome> DeliverAsync(IDataObject payload)
    {
        if (!_native.IsWindow(_targetWindow))
        {
            return InputDeliveryOutcome.NoTarget;
        }

        var backup = _native.GetClipboard();
        if (!_native.WriteClipboard(payload))
        {
            return InputDeliveryOutcome.ClipboardWriteFailed;
        }

        if (!_native.SetForegroundWindow(_targetWindow))
        {
            RestoreClipboard(backup);
            return InputDeliveryOutcome.FocusFailed;
        }

        await _native.DelayAsync(ForegroundSettleDelay);
        if (!_native.SendControlV())
        {
            RestoreClipboard(backup);
            return InputDeliveryOutcome.SendFailed;
        }

        await _native.DelayAsync(PasteSettleDelay);
        return backup is null || _native.SetClipboard(backup)
            ? InputDeliveryOutcome.Delivered
            : InputDeliveryOutcome.DeliveredRestoreFailed;
    }

    private void RestoreClipboard(IDataObject? backup)
    {
        if (backup is not null)
        {
            _native.SetClipboard(backup);
        }
    }
}
