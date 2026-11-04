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
/// 备份/写/恢复、剪贴板序号与节奏延时。生产实现走 Win32/WPF；测试注入假件断言
/// 序列与失败分支（真实剪贴板与真实输入注入不进测试）。
/// </summary>
public interface IInputDeliveryNative
{
    nint GetForegroundWindow();

    /// <summary>前台线程的焦点窗口句柄（0＝前台尚无焦点）——注入就绪谓词的
    /// 焦点半边：前台记账在 SetForegroundWindow 即翻转，而目标处理激活、设置
    /// 焦点在其线程被调度后（W1 D4 修复后验证实测：前台已＝目标而焦点＝0）。</summary>
    nint GetForegroundFocus();

    bool IsWindow(nint hwnd);

    bool IsOwnProcessWindow(nint hwnd);

    bool SetForegroundWindow(nint hwnd);

    /// <summary>注入一次 Ctrl+V 按键序列；返回是否全部注入成功。</summary>
    bool SendControlV();

    IDataObject? GetClipboard();

    bool WriteClipboard(IDataObject data);

    bool SetClipboard(IDataObject data);

    /// <summary>系统剪贴板序号（剪贴板内容每次变更递增，读取不变更）——恢复前
    /// 判定「送达窗口期内剪贴板是否被外部改写」的守卫依据。</summary>
    uint GetClipboardSequenceNumber();

    Task DelayAsync(TimeSpan delay);
}

/// <summary>Win32/WPF 生产实现（剪贴板异常与 SendInput 结果如实返回）。</summary>
internal sealed class Win32InputDeliveryNative : IInputDeliveryNative
{
    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public nint GetForegroundFocus() => NativeMethods.GetForegroundFocusWindow();

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

    public uint GetClipboardSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

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
/// 时序可靠性（2026-11-05 修复，W1）：注入前以「目标真正取得前台且焦点就绪」
/// 谓词替代固定延时（前台记账在 SetForegroundWindow 即翻转、目标处理激活在其
/// 线程被调度后才完成；盲注入＝按键落错队列丢失或稳定期起算过早与目标延迟读取
/// 竞态，evidence-w1 D2/D4）；恢复剪贴板前以剪贴板序号守卫防覆写（送达窗口期内
/// 用户/他应用改写了剪贴板则不恢复）；剪贴板写/恢复对占用做有界重试。
/// </summary>
public sealed class InputDeliveryService
{
    private readonly IInputDeliveryNative _native;
    private readonly TimeSpan _foregroundWaitTimeout;

    // 前台谓词轮询：间隔决定注入对「OS 确认前台」的反应延迟（快路径谓词已成立时
    // 零等待）；上界 2s 覆盖重目标窗口的激活（含系统切换动画与 DWM 忙），超时＝
    // 焦点还原未达成，归因 FocusFailed（不盲注入）。
    private static readonly TimeSpan DefaultForegroundWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(20);

    // 粘贴生效的稳定期：注入按键已进入目标输入队列（谓词保证），此处只等目标
    // 处理按键并读取剪贴板，之后才恢复用户原剪贴板——过短则目标迟读（UI 线程忙）
    // 会粘到恢复后的原内容，过长拖慢串行化的下一次送达；实测取值。
    private static readonly TimeSpan PasteSettleDelay = TimeSpan.FromMilliseconds(250);

    // 剪贴板被占用（CLIPBRD_E_CANT_OPEN，常见于目标粘贴期间持有剪贴板）时的
    // 有界重试：写失败重试 2 次、恢复失败重试 2 次，间隔给占用方释放的机会。
    private const int ClipboardRetryCount = 2;
    private static readonly TimeSpan ClipboardRetryInterval = TimeSpan.FromMilliseconds(40);

    private nint _targetWindow;

    public InputDeliveryService(IInputDeliveryNative? native = null)
        : this(native, null)
    {
    }

    /// <summary>测试可注入前台谓词上界（超时分支无需真实等待 2s）。</summary>
    internal InputDeliveryService(IInputDeliveryNative? native, TimeSpan? foregroundWaitTimeout)
    {
        _native = native ?? new Win32InputDeliveryNative();
        _foregroundWaitTimeout = foregroundWaitTimeout ?? DefaultForegroundWaitTimeout;
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
    /// 完整送达序列：备份原剪贴板→写负载→目标前台化→有界等待目标真正取得前台→
    /// 注入 Ctrl+V→粘贴稳定期→守卫恢复原剪贴板。任一步失败按
    /// <see cref="InputDeliveryOutcome"/> 归因返回，已写剪贴板的失败路径先尽力恢复
    /// 备份再返回（不把送达负载遗留给用户；恢复同样受序号守卫——窗口期内剪贴板
    /// 已被外部改写时跳过，不覆写新内容）。
    /// </summary>
    public async Task<InputDeliveryOutcome> DeliverAsync(IDataObject payload)
    {
        if (!_native.IsWindow(_targetWindow))
        {
            return InputDeliveryOutcome.NoTarget;
        }

        var backup = _native.GetClipboard();
        if (!await TryWriteClipboardWithRetryAsync(payload))
        {
            return InputDeliveryOutcome.ClipboardWriteFailed;
        }

        var sequenceAtWrite = _native.GetClipboardSequenceNumber();
        if (!_native.SetForegroundWindow(_targetWindow) || !await WaitUntilTargetForegroundAsync())
        {
            await RestoreClipboardGuardedAsync(backup, sequenceAtWrite);
            return InputDeliveryOutcome.FocusFailed;
        }

        if (!_native.SendControlV())
        {
            await RestoreClipboardGuardedAsync(backup, sequenceAtWrite);
            return InputDeliveryOutcome.SendFailed;
        }

        await _native.DelayAsync(PasteSettleDelay);
        if (_native.GetClipboardSequenceNumber() != sequenceAtWrite)
        {
            // 送达窗口期内剪贴板已被外部改写（用户复制了新内容等）：粘贴已生效，
            // 恢复＝覆写用户新内容，跳过。
            return InputDeliveryOutcome.Delivered;
        }

        return backup is null || await TryRestoreClipboardWithRetryAsync(backup)
            ? InputDeliveryOutcome.Delivered
            : InputDeliveryOutcome.DeliveredRestoreFailed;
    }

    /// <summary>
    /// 有界等待目标真正取得前台且焦点就绪。谓词成立即可注入：目标线程已持有输入
    /// 队列、激活已处理完（焦点非空），注入按键即被目标处理；只看前台记账不看
    /// 焦点时，稳定期会在目标尚未就绪的时刻起算——恢复先于目标读取剪贴板，粘到
    /// 的是恢复后的原内容（W1 D4 修复后验证实测：前台已＝目标而焦点＝0）。
    /// 超时＝焦点还原未达成。
    /// </summary>
    private async Task<bool> WaitUntilTargetForegroundAsync()
    {
        var deadline = Environment.TickCount64 + (long)_foregroundWaitTimeout.TotalMilliseconds;
        while (_native.GetForegroundWindow() != _targetWindow || _native.GetForegroundFocus() == 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await _native.DelayAsync(ForegroundPollInterval);
        }

        return true;
    }

    private async Task<bool> TryWriteClipboardWithRetryAsync(IDataObject payload)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (_native.WriteClipboard(payload))
            {
                return true;
            }

            if (attempt >= ClipboardRetryCount)
            {
                return false;
            }

            await _native.DelayAsync(ClipboardRetryInterval);
        }
    }

    private Task<bool> TryRestoreClipboardWithRetryAsync(IDataObject backup) =>
        RetryClipboardAsync(() => _native.SetClipboard(backup));

    private async Task<bool> RetryClipboardAsync(Func<bool> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (operation())
            {
                return true;
            }

            if (attempt >= ClipboardRetryCount)
            {
                return false;
            }

            await _native.DelayAsync(ClipboardRetryInterval);
        }
    }

    /// <summary>失败分支的尽力恢复：序号守卫（外部已改写则跳过）＋有界重试。</summary>
    private Task RestoreClipboardGuardedAsync(IDataObject? backup, uint sequenceAtWrite) =>
        backup is not null && _native.GetClipboardSequenceNumber() == sequenceAtWrite
            ? TryRestoreClipboardWithRetryAsync(backup)
            : Task.CompletedTask;
}
