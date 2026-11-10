using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Services;

/// <summary>
/// 放置动画的帧时钟抽象：animator 订阅 <see cref="Frame"/> 逐帧采样，时间戳由
/// <see cref="TimestampMs"/> 提供（ms 单调时钟）。生产实现挂
/// CompositionTarget.Rendering（与垂直刷新同步）；测试注入手动实现逐帧推进
/// （契约 #12 例外条款的注入时钟）。
/// </summary>
internal interface IPlacementAnimationClock
{
    /// <summary>当前单调时间戳（毫秒）。仅用于段内进度与段间重映射，不做绝对时刻断言。</summary>
    long TimestampMs { get; }

    /// <summary>下一帧通知：订阅方在收到通知时按当前时间戳采样并应用一帧。</summary>
    event EventHandler? Frame;
}

/// <summary>生产帧时钟：CompositionTarget.Rendering（UI 线程、与合成节拍同步）。</summary>
internal sealed class RenderingPlacementAnimationClock : IPlacementAnimationClock
{
    private EventHandler? _frame;

    public long TimestampMs => Environment.TickCount64;

    public event EventHandler? Frame
    {
        add
        {
            if (_frame is null)
            {
                CompositionTarget.Rendering += OnRendering;
            }

            _frame += value;
        }
        remove
        {
            _frame -= value;
            if (_frame is null)
            {
                CompositionTarget.Rendering -= OnRendering;
            }
        }
    }

    private void OnRendering(object? sender, EventArgs e) =>
        _frame?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 唯一窗口矩形动画服务（R12 peek，契约 #12 例外通道、#41 动效运行语义）：
/// <see cref="System.Windows.Media.CompositionTarget.Rendering"/> 逐帧插值＋
/// <c>SetWindowPos(SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE)</c> 纯平移帧（宽高恒
/// 不变），绕开 WPF 属性系统。矩形权威仲裁：动画期间窗口矩形权威归本服务——
/// 其他代码应用放置前必须先 <see cref="Cancel(bool)"/>（到达动画终态）再应用；
/// 动画帧不经 <c>_placementTargetRectangle</c> 守卫；WPF Left/Top 账本由调用方仅在
/// 动画完成回调或取消后的放置应用中一次收口。序列保证：起点＝迁移初始态、终点＝
/// 唯一终态、逐帧单调（缓动单调 ⇒ 取整后非严格单调）；<see cref="Redirect"/> 捕获
/// 当前实时进度为新起点、时长按剩余距离比例重映射（封顶原时长），禁止原点重放。
/// </summary>
internal sealed class WindowPlacementAnimator
{
    private readonly nint _hwnd;
    private readonly IPlacementAnimationClock _clock;
    private PhysicalRectangle _from;
    private PhysicalRectangle _to;
    private int _durationMs;
    private long _startedAt;
    private IEasingFunction _easing = FadeAnimation.EnterEasing;
    private Action? _onCompleted;
    private bool _running;

    public WindowPlacementAnimator(nint hwnd, IPlacementAnimationClock clock)
    {
        _hwnd = hwnd;
        _clock = clock;
    }

    /// <summary>是否有动画在途（矩形权威在本服务手上）。</summary>
    public bool IsRunning => _running;

    /// <summary>当前动画终态矩形（无动画在途时无意义）。</summary>
    public PhysicalRectangle Terminal => _to;

    /// <summary>
    /// 开始一段平移动画：from/to 须同尺寸同 Top（纯平移）；先取消在途动画（不落
    /// 终态，由本段 from 接管）。首帧立即应用，末帧精确落在 to。
    /// </summary>
    public void Begin(
        PhysicalRectangle from,
        PhysicalRectangle to,
        int durationMs,
        IEasingFunction easing,
        Action? onCompleted)
    {
        Stop(applyTerminal: false, raiseCompleted: false);
        _from = from;
        _to = to;
        _durationMs = Math.Max(1, durationMs);
        _easing = easing;
        _onCompleted = onCompleted;
        _running = true;
        _startedAt = _clock.TimestampMs;
        _clock.Frame += OnFrame;
        OnFrame(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重定向在途动画到新终态：捕获当前实时进度（按当前时间戳采样，而非末帧值）为
    /// 新起点；时长按剩余距离比例重映射——参考距离＝旧终态→新终态全长（反转场景
    /// 即本方向全行程），剩余/全行程 × 满时长，封顶满时长（同向越过旧终态时不再
    /// 放大）；禁止原点重放。无在途动画时按 <see cref="Begin"/> 语义从当前矩形出发。
    /// </summary>
    public void Redirect(
        PhysicalRectangle to,
        int durationMs,
        IEasingFunction easing,
        Action? onCompleted)
    {
        if (!_running)
        {
            if (!NativeMethods.GetWindowRect(_hwnd, out var current))
            {
                return;
            }

            Begin(
                new PhysicalRectangle(current.Left, current.Top, current.Right, current.Bottom),
                to,
                durationMs,
                easing,
                onCompleted);
            return;
        }

        var now = _clock.TimestampMs;
        var currentLeft = (int)Math.Round(SampleLeft(now));
        var remaining = Math.Abs(to.Left - currentLeft);
        var reference = Math.Abs(to.Left - _to.Left);
        var mapped = reference <= 0 || remaining >= reference
            ? durationMs
            : Math.Max(1, (int)Math.Round(durationMs * (double)remaining / reference));
        _from = _from with { Left = currentLeft, Right = currentLeft + _from.Width };
        _to = to;
        _durationMs = mapped;
        _startedAt = now;
        _easing = easing;
        _onCompleted = onCompleted;
    }

    /// <summary>
    /// 取消动画并交还矩形权威：applyTerminal＝true 时一次 <c>SetWindowPos</c> 落到
    /// 当前动画终态（经终态的权威交接，供 ApplyPlacement 入口调用）；自然完成不经过
    /// 本方法（末帧回调 onCompleted）。取消不回调 onCompleted——取消后的放置由调用方
    /// 自行收口账本。
    /// </summary>
    public void Cancel(bool applyTerminal)
    {
        if (!_running)
        {
            return;
        }

        Stop(applyTerminal, raiseCompleted: false);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_running)
        {
            return;
        }

        var left = SampleLeft(_clock.TimestampMs);
        ApplyFrame(left);
        if (left != _to.Left)
        {
            return;
        }

        // 进度达 1（SampleLeft 钳制到终点）：末帧即终态，完成回调在终态落位后发出。
        Stop(applyTerminal: false, raiseCompleted: true);
    }

    /// <summary>按段参数采样当前帧的物理左缘（ eased 单调 ⇒ 物理域取整后非严格单调）。</summary>
    private double SampleLeft(long timestampMs)
    {
        var elapsed = timestampMs - _startedAt;
        var progress = _durationMs <= 0 ? 1d : Math.Clamp((double)elapsed / _durationMs, 0d, 1d);
        var eased = _easing.Ease(progress);
        return _from.Left + ((_to.Left - _from.Left) * eased);
    }

    private void ApplyFrame(double left) =>
        NativeMethods.SetWindowPos(
            _hwnd,
            0,
            (int)Math.Round(left),
            _from.Top,
            _from.Width,
            _from.Height,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

    private void Stop(bool applyTerminal, bool raiseCompleted)
    {
        if (_running)
        {
            _clock.Frame -= OnFrame;
            if (applyTerminal)
            {
                NativeMethods.SetWindowPos(
                    _hwnd,
                    0,
                    _to.Left,
                    _to.Top,
                    _to.Width,
                    _to.Height,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            }
        }

        _running = false;
        var completed = _onCompleted;
        _onCompleted = null;
        if (raiseCompleted)
        {
            completed?.Invoke();
        }
    }
}
