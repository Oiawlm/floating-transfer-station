using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 放置动画的手动帧时钟（契约 #12 例外条款测试通道）：AdvanceTo 推进时间戳并
/// 触发一帧——测试由此逐帧驱动 WindowPlacementAnimator，断言单调/终态唯一/重映射。
/// </summary>
internal sealed class ManualPlacementAnimationClock : IPlacementAnimationClock
{
    private EventHandler? _frame;

    public long TimestampMs { get; private set; }

    public event EventHandler? Frame
    {
        add => _frame += value;
        remove => _frame -= value;
    }

    /// <summary>推进到指定时间戳并同步触发一帧（时间戳必须单调不减）。</summary>
    public void AdvanceTo(long timestampMs)
    {
        if (timestampMs < TimestampMs)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampMs), "时间戳必须单调不减。");
        }

        TimestampMs = timestampMs;
        _frame?.Invoke(this, EventArgs.Empty);
    }
}
