using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 一次性贴边隐藏 peek 化（R12，契约 #17，Issue #353）：头部按钮按一次进入待命，
/// 鼠标离开后整窗同尺寸平移收缩到右缘 peek 态——绝大部分出屏、屏内仅留左侧把手
/// （宽＝DesignTokens.EdgePeekHandleWidthPx，空间记忆锚点）；移上把手经 60ms 意图
/// 事务（#42 同族）展开，恢复即回原放置并自动解除（一次性）。触发融入既有收起
/// 节奏（Root_MouseLeave → _collapseTimer → tick 分支），不新增触发器；找回兜底：
/// 把手 hover、全局唤起热键、显示器变化立即展开。窗口矩形动画经唯一
/// WindowPlacementAnimator（契约 #12 例外通道）：动画期间矩形权威归 animator，
/// ApplyPlacement 入口先取消动画（到达终态）再应用；WPF Left/Top 账本仅在动画
/// 完成/取消时一次收口。与「保持展开」的互斥（R2）沿 DockingIntentMutex 原样。
/// </summary>
public partial class MainWindow : Window
{
    private readonly EdgeHideStateMachine _edgeHide = new();
    private readonly DockingIntentMutex _dockingIntentMutex = new();

    // 把手 hover 展开的意图事务计时（#42 同族）：布防 → 到期重验指针仍在窗口/把手
    // 内才提交展开；离开即取消，防扫边误触由事务重验达成。
    private readonly DispatcherTimer _edgePeekExpandIntentTimer;

    // peek 期间有效：原放置为 DIP 账本值（收缩时刻窗口仍在屏内、账本仍权威），
    // 展开动画以它为终态。
    private WindowPlacement? _edgePeekOriginalPlacement;

    private WindowPlacementAnimator? _edgePeekAnimator;

    /// <summary>测试注入帧时钟（null＝生产 CompositionTarget.Rendering）；必须在
    /// 首次 peek 之前设置——animator 创建时捕获时钟（契约 #12 例外条款测试通道）。</summary>
    internal IPlacementAnimationClock? PlacementAnimationClockOverride { get; set; }

    private void EdgeHideButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var armed = _edgeHide.ToggleArm();
        UpdateEdgeHideButton();

        // 互斥（R2，后激活者胜）：激活贴边待命即解除「保持展开」，两按钮任何时刻
        // 不同时点亮（视觉态与功能态一致）。
        if (armed && _dockingIntentMutex.Request(DockingIntent.EdgeHide) == DockingIntent.PanelHold)
        {
            _panelState.EndPanelHold();
            UpdatePanelHoldButton();
        }

        // 再按一次取消：让出互斥组；若指针已离开面板（自动化路径），按既有节奏恢复
        // 自动收起（与解除保持展开同构）。
        if (!armed)
        {
            _dockingIntentMutex.Clear(DockingIntent.EdgeHide);
            ReconcileSurfaceAfterSuppressionRelease();
        }
    }

    private void UpdateEdgeHideButton()
    {
        var active = _edgeHide.IsArmed || _edgeHide.IsPeek;
        // SetResourceReference 保持动态解析：主题切换时强调色/次级文字色自动跟随。
        EdgeHideButton.SetResourceReference(
            Control.ForegroundProperty,
            active ? "AccentBrush" : "SecondaryTextBrush");
        string label;
        if (_edgeHide.IsPeek)
        {
            label = "已收进屏幕边缘：移上把手或用唤起快捷键展开";
        }
        else if (_edgeHide.IsArmed)
        {
            label = "已准备收进屏幕边缘，鼠标移开即缩成把手；再按一次取消";
        }
        else
        {
            label = "一次性收进屏幕边缘：鼠标离开后缩成边缘把手，移上把手自动展开";
        }

        EdgeHideButton.ToolTip = label;
        AutomationProperties.SetName(EdgeHideButton, label);
    }

    /// <summary>
    /// 一次性贴边隐藏提交（收起节奏 tick 内，armed 且无抑制）：整窗同尺寸平移收缩
    /// 到 peek 几何（与工作区交集宽＝把手宽），面板保持展开、滚动位置与内容原样
    /// 冻结。收缩经 animator 167ms 加速出场（#12 例外通道）；减动效一次直达终态
    /// （把手即时出现）。不走收起视觉交接（那是为收起变形设计的，收缩不需要）。
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

        if (!_edgeHide.TryPeek(_panelState.WouldCollapse))
        {
            return false;
        }

        // DIP→物理换算一次完成（此刻窗口仍在屏内、变换有效）；peek 锚定收缩时刻
        // 所在工作区右缘（工作区物理矩形按同一变换换算）。
        var transform = _windowSource.CompositionTarget.TransformToDevice;
        var work = CurrentWorkArea();
        var workArea = new PhysicalRectangle(
            (int)Math.Round(work.Left * transform.M11),
            (int)Math.Round(work.Top * transform.M22),
            (int)Math.Round(work.Right * transform.M11),
            (int)Math.Round((work.Top + work.Height) * transform.M22));
        var handleWidthPx = Math.Max(
            1,
            (int)Math.Round(DesignTokens.EdgePeekHandleWidthPx * transform.M11));
        var visibleAtCommit = new PhysicalRectangle(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right,
            windowRect.Bottom);
        var peek = WindowController.EdgePeekPlacement(visibleAtCommit, workArea, handleWidthPx);
        var peekPlacement = new WindowPlacement(
            peek.Left / transform.M11,
            peek.Top / transform.M22,
            Width,
            Height);

        // 原放置以 DIP 账本保存（此刻仍权威）；WPF Left/Top 账本仅在动画完成/取消时
        // 一次收口（Width/Height 恒不变）。
        _edgePeekOriginalPlacement = new WindowPlacement(Left, Top, Width, Height);
        UpdateEdgeHideButton();

        if (ClientAreaAnimationsEnabled)
        {
            EnsureEdgePeekAnimator().Begin(
                visibleAtCommit,
                peek,
                DesignTokens.NormalDurationMs,
                FadeAnimation.ExitEasing,
                onCompleted: () => OnEdgePeekCollapseCompleted(peekPlacement));
        }
        else
        {
            ApplyPlacement(peekPlacement);
            ShowEdgePeekHandle(animate: false);
        }

        ShowStatus("已收进屏幕边缘，鼠标移到把手即可展开。");
        return true;
    }

    /// <summary>收缩动画完成：WPF Left/Top 账本一次收口；把手视觉 83ms 淡入登场。</summary>
    private void OnEdgePeekCollapseCompleted(WindowPlacement peekPlacement)
    {
        Left = peekPlacement.Left;
        Top = peekPlacement.Top;
        ShowEdgePeekHandle(animate: true);
    }

    /// <summary>
    /// 把手视觉层登场（peek 态专用）：83ms FastDurationMs 淡入；减动效即时出现。
    /// 入场淡入达成柔和收束（DWM 不透明窗无整窗 opacity，「83ms 淡出尾巴」对
    /// HWND 本体不可实现——分层透明已被弃用）。
    /// </summary>
    private void ShowEdgePeekHandle(bool animate)
    {
        EdgePeekHandleLayer.Visibility = Visibility.Visible;
        if (!animate || !ClientAreaAnimationsEnabled)
        {
            StopEdgePeekHandleAnimation();
            EdgePeekHandleLayer.Opacity = 1d;
            return;
        }

        var entrance = new DoubleAnimation(
            0d,
            1d,
            TimeSpan.FromMilliseconds(DesignTokens.FastDurationMs))
        {
            EasingFunction = FadeAnimation.EnterEasing,
            FillBehavior = FillBehavior.Stop,
        };
        EdgePeekHandleLayer.BeginAnimation(UIElement.OpacityProperty, null);
        EdgePeekHandleLayer.SetValue(UIElement.OpacityProperty, 0d);
        EdgePeekHandleLayer.BeginAnimation(UIElement.OpacityProperty, entrance);
    }

    private void HideEdgePeekHandle()
    {
        StopEdgePeekHandleAnimation();
        EdgePeekHandleLayer.Visibility = Visibility.Collapsed;
    }

    private void StopEdgePeekHandleAnimation()
    {
        EdgePeekHandleLayer.BeginAnimation(UIElement.OpacityProperty, null);
        EdgePeekHandleLayer.Opacity = 1d;
    }

    /// <summary>
    /// 把手 hover / 指针回入窗口（含收缩途中反向救援）：布防展开意图事务——60ms
    /// 后重验指针仍在窗口内才提交展开（#42 同族；窗口同一 HWND 持续可命中）。
    /// </summary>
    private void ArmEdgePeekExpandIntent()
    {
        if (!_edgeHide.IsPeek)
        {
            return;
        }

        _edgePeekExpandIntentTimer.Stop();
        _edgePeekExpandIntentTimer.Start();
    }

    private void EdgePeekExpandIntentTimer_Tick(object? sender, EventArgs e)
    {
        _edgePeekExpandIntentTimer.Stop();
        if (!_edgeHide.IsPeek ||
            _windowSource?.Handle is not { } handle || handle == 0 ||
            !NativeMethods.GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        // 意图事务重验：到期时指针仍在窗口内（含把手）才提交展开；已离开则本次
        // 意图作废（可由下一次 MouseEnter 重新布防）。
        if (!NativeMethods.GetCursorPos(out var cursor) ||
            !new PhysicalRectangle(
                windowRect.Left,
                windowRect.Top,
                windowRect.Right,
                windowRect.Bottom).Contains(cursor.X, cursor.Y))
        {
            return;
        }

        RestoreFromEdgeHide();
    }

    /// <summary>
    /// 退出 peek 相位的记账单点（相位/互斥/把手视觉/意图计时），不含几何——几何由
    /// 调用路径自行应用（展开恢复、显示器变化重贴齐、外部拖放轨揭示等）。
    /// </summary>
    private void ExitEdgePeekState()
    {
        if (!_edgeHide.IsPeek)
        {
            return;
        }

        _edgeHide.Reset();
        _dockingIntentMutex.Clear(DockingIntent.EdgeHide);
        _edgePeekExpandIntentTimer.Stop();
        HideEdgePeekHandle();
        UpdateEdgeHideButton();
    }

    /// <summary>
    /// peek 展开：恢复即解除（一次性）在展开提交点生效；动画经 animator 250ms
    /// 减速入场（#12 例外通道）回到原放置，完成回调一次收口 WPF 账本；展开途中
    /// 不中断（完成→常规 250ms 收起接管）。减动效/无句柄时一次直达。混合 DPI
    /// 多屏下 peek 期间 WM_DPICHANGED 改变窗口 DPI 上下文时，落回原显示器的
    /// 自校正兜底与既有路径一致。
    /// </summary>
    private void RestoreFromEdgeHide()
    {
        if (!_edgeHide.IsPeek || _edgePeekOriginalPlacement is not { } placement)
        {
            return;
        }

        ExitEdgePeekState();
        // 待命/peek 常显随解除结束：恢复后头部回到默认悬停显隐（指针真实悬停则
        // 保持显示）。
        SetHeaderActionsVisible(HeaderActionRegion.IsMouseOver);

        if (!ClientAreaAnimationsEnabled ||
            _windowSource?.Handle is not { } handle || handle == 0 ||
            _windowSource.CompositionTarget is null ||
            !NativeMethods.GetWindowRect(handle, out var currentRect))
        {
            ApplyPlacement(placement);
            ShowStatus("已从屏幕边缘唤回。");
            return;
        }

        // 终态与 ApplyPlacement 的物理换算同式（两锚定边各自取整），动画终点与
        // 直达路径落点一致。
        var transform = _windowSource.CompositionTarget.TransformToDevice;
        var left = (int)Math.Round(placement.Left * transform.M11);
        var top = (int)Math.Round(placement.Top * transform.M22);
        var target = new PhysicalRectangle(
            left,
            top,
            (int)Math.Round((placement.Left + placement.Width) * transform.M11),
            (int)Math.Round((placement.Top + placement.Height) * transform.M22));
        var current = new PhysicalRectangle(
            currentRect.Left,
            currentRect.Top,
            currentRect.Right,
            currentRect.Bottom);
        var animator = EnsureEdgePeekAnimator();
        if (animator.IsRunning)
        {
            // 收缩途中反向救援/热键打断：捕获当前实时进度为新起点、时长按剩余距离
            // 比例重映射（封顶 250ms），禁止原点重放。
            animator.Redirect(
                target,
                DesignTokens.SlowDurationMs,
                FadeAnimation.EnterEasing,
                () => OnEdgePeekExpandCompleted(placement));
        }
        else
        {
            animator.Begin(
                current,
                target,
                DesignTokens.SlowDurationMs,
                FadeAnimation.EnterEasing,
                () => OnEdgePeekExpandCompleted(placement));
        }
    }

    /// <summary>展开动画完成：WPF Left/Top 账本一次收口（Width/Height 恒不变）。</summary>
    private void OnEdgePeekExpandCompleted(WindowPlacement placement)
    {
        Left = placement.Left;
        Top = placement.Top;
        ShowStatus("已从屏幕边缘唤回。");
    }

    private WindowPlacementAnimator EnsureEdgePeekAnimator()
    {
        if (_edgePeekAnimator is { } animator)
        {
            return animator;
        }

        animator = new WindowPlacementAnimator(
            _windowSource?.Handle ?? 0,
            PlacementAnimationClockOverride ?? new RenderingPlacementAnimationClock());
        _edgePeekAnimator = animator;
        return animator;
    }

    /// <summary>
    /// armed 生命周期：仅展开态有意义。面板经其他路径离开展开态（外部拖放收起、
    /// 常规收起兜底）且未进入 Peek 时自动解除，不留悬置 armed；保持展开激活时
    /// 的互斥挤出（R2）复用本单点，同步让出贴边意图的互斥组持有权。
    /// </summary>
    private void DisarmEdgeHide()
    {
        if (!_edgeHide.IsArmed)
        {
            return;
        }

        _edgeHide.Reset();
        _dockingIntentMutex.Clear(DockingIntent.EdgeHide);
        UpdateEdgeHideButton();
    }
}
