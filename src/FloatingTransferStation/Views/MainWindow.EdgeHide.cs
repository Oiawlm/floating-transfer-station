using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 一次性贴边隐藏（1.20.0）：头部按钮按一次进入待命，鼠标离开后整窗同尺寸纯移出
/// 所有显示器右缘之外（比收起到轨道更彻底）；鼠标移回原位驻留后回到原位原形并自动
/// 解除。触发融入既有收起节奏（Root_MouseLeave → _collapseTimer → tick 分支），
/// 不新增触发器；找回兜底：回位驻留、全局唤起热键、显示器变化立即恢复。
/// </summary>
public partial class MainWindow : Window
{
    // 回位轮询节奏：业界主流 ~100ms（TrafficMonitor、winautohidev2 等一致）；
    // 仅 Docked 期间运行，窗口离屏收不到鼠标事件，轮询是回位检测的唯一可靠途径。
    private static readonly TimeSpan EdgeHideRecallPollInterval = TimeSpan.FromMilliseconds(100);
    // 构建产物哈希轮换注释：Smart App Control 云判定偶发误拦新产物，递增本行后重建即换哈希。v1。

    // 回位驻留时长：连续命中 ≥200ms 才恢复，过滤扫边误触（调研经验值）。
    // 右缘滚动条拖拽等驻留动作可能意外唤回——一次性语义把代价限制为一次，属
    // 可接受权衡；阈值集中此处便于调整。
    private const int EdgeHideRecallDwellMs = 200;

    // 回位区外扩容差（物理像素）：隐藏时刻屏幕内可见矩形四向外扩，容许光标落点偏差。
    private const int EdgeHideRecallTolerancePx = 8;

    private readonly EdgeHideStateMachine _edgeHide = new();
    private readonly DispatcherTimer _edgeHideRecallTimer;

    // 以下三者在 Docked 期间有效：回位区为物理像素（隐藏时刻保存，此后不做任何
    // 运行时 DPI 换算——窗口离屏后其 CompositionTarget 变换不可信）；原放置为
    // DIP 账本值（隐藏时刻窗口仍在屏内，变换仍有效）。
    private PhysicalRectangle _edgeHideRecallZone;
    private WindowPlacement? _edgeHideHiddenPlacement;
    private long _edgeHideRecallEnteredAt;

    private void EdgeHideButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var armed = _edgeHide.ToggleArm();
        UpdateEdgeHideButton();

        // 再按一次取消：若指针已离开面板（自动化路径），按既有节奏恢复自动收起
        // （与解除保持展开同构）。
        if (!armed)
        {
            ReconcileSurfaceAfterSuppressionRelease();
        }
    }

    private void UpdateEdgeHideButton()
    {
        var armed = _edgeHide.IsArmed;
        // SetResourceReference 保持动态解析：主题切换时强调色/次级文字色自动跟随。
        EdgeHideButton.SetResourceReference(
            Control.ForegroundProperty,
            armed ? "AccentBrush" : "SecondaryTextBrush");
        var label = armed
            ? "已准备贴边隐藏，鼠标移开即移出屏幕；再按一次取消"
            : "一次性移出屏幕：鼠标离开后收进屏幕边缘，移回原位自动展开";
        EdgeHideButton.ToolTip = label;
        AutomationProperties.SetName(EdgeHideButton, label);
    }

    /// <summary>
    /// 一次性贴边隐藏提交（收起节奏 tick 内，armed 且无抑制）：整窗同尺寸纯移动到
    /// 所有显示器右缘之外——一次矩形变更（迁移原子性），面板保持展开、滚动位置与
    /// 内容原样冻结。不走收起视觉交接（那是为收起变形设计的，隐藏不需要）。
    /// </summary>
    private bool TryCommitEdgeHide()
    {
        if (_windowSource?.Handle is not { } handle ||
            handle == 0 ||
            _windowSource.CompositionTarget is null ||
            !NativeMethods.GetWindowRect(handle, out var windowRect))
        {
            return false;
        }

        var monitors = ScreenEdgeGeometry.AllMonitors();
        if (monitors.Count == 0 ||
            !_edgeHide.TryDock(_panelState.WouldCollapse))
        {
            return false;
        }

        var visibleAtHide = new PhysicalRectangle(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right,
            windowRect.Bottom);
        _edgeHideRecallZone = WindowController.EdgeRecallZone(
            visibleAtHide,
            EdgeHideRecallTolerancePx);
        _edgeHideHiddenPlacement = new WindowPlacement(Left, Top, Width, Height);
        _edgeHideRecallEnteredAt = 0;

        // ApplyPlacement 以 DIP 为权威、按 CompositionTarget 换算一次：此刻窗口
        // 仍在屏内、变换仍有效；此后窗口离屏，回位判定只走物理像素直比。
        var transform = _windowSource.CompositionTarget.TransformToDevice;
        var hidden = WindowController.EdgeHidden(visibleAtHide, monitors);
        ApplyPlacement(new WindowPlacement(
            hidden.Left / transform.M11,
            hidden.Top / transform.M22,
            hidden.Width / transform.M11,
            hidden.Height / transform.M22));
        UpdateEdgeHideButton();
        ShowStatus("已移出屏幕，鼠标移回原位即可唤回。");
        _edgeHideRecallTimer.Start();
        return true;
    }

    /// <summary>
    /// 回位恢复：一次矩形变更移回隐藏时刻的原放置。面板本就未收起，指针此刻在
    /// 原窗口位置（回位区内），随后的 Root_MouseEnter 与既有展开/保持机制自然
    /// 接管——「移回原位自动展开」由此成立，无需任何新展开逻辑。恢复即解除
    /// 一次性待命并停轮询。
    /// </summary>
    private void RestoreFromEdgeHide()
    {
        if (!_edgeHide.IsDocked || _edgeHideHiddenPlacement is not { } placement)
        {
            return;
        }

        _edgeHide.Reset();
        _edgeHideRecallTimer.Stop();
        _edgeHideRecallEnteredAt = 0;
        ApplyPlacement(placement);
        UpdateEdgeHideButton();
        // 待命常显随解除结束：恢复后头部回到默认悬停显隐（指针真实悬停则保持显示）。
        SetHeaderActionsVisible(HeaderActionRegion.IsMouseOver);
        ShowStatus("已从屏幕边缘唤回。");
    }

    private void EdgeHideRecallTimer_Tick(object? sender, EventArgs e)
    {
        if (!_edgeHide.IsDocked)
        {
            _edgeHideRecallTimer.Stop();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        HandleEdgeHideRecallPoll(cursor.X, cursor.Y, Environment.TickCount64);
    }

    /// <summary>
    /// 回位驻留判定（物理像素直比）：光标进入回位区记起点，连续驻留达阈值才恢复
    /// （过滤扫边误触），离开即重置进度。时间戳由调用方注入，便于模拟轮询测试。
    /// </summary>
    private void HandleEdgeHideRecallPoll(int cursorX, int cursorY, long timestampMs)
    {
        if (!_edgeHideRecallZone.Contains(cursorX, cursorY))
        {
            _edgeHideRecallEnteredAt = 0;
            return;
        }

        if (_edgeHideRecallEnteredAt == 0)
        {
            _edgeHideRecallEnteredAt = timestampMs;
            return;
        }

        if (timestampMs - _edgeHideRecallEnteredAt >= EdgeHideRecallDwellMs)
        {
            RestoreFromEdgeHide();
        }
    }

    /// <summary>
    /// armed 生命周期：仅展开态有意义。面板经其他路径离开展开态（外部拖放收起、
    /// 常规收起兜底）且未进入 Docked 时自动解除，不留悬置 armed。
    /// </summary>
    private void DisarmEdgeHide()
    {
        if (!_edgeHide.IsArmed)
        {
            return;
        }

        _edgeHide.Reset();
        UpdateEdgeHideButton();
    }
}

