using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>覆盖交互原因：＋菜单与模态对话框的生命周期会重叠（菜单关闭交接给对话框），
/// 迟到的菜单关闭不得清掉对话框的持有，按原因独立记账。</summary>
public enum PanelOverlayReason
{
    Menu,
    ModalDialog
}

public sealed class PanelStateMachine
{
    private bool _pointerInside;
    private bool _dragInProgress;
    private bool _textEditingActive;
    private bool _panelHoldActive;
    private readonly HashSet<PanelOverlayReason> _overlayReasons = [];
    private bool _reexpandSuppressionArmed;
    private int _reexpandSuppressionLeft;
    private int _reexpandSuppressionTop;
    private int _reexpandSuppressionRight;
    private int _reexpandSuppressionBottom;

    public BoardCategory? ActiveCategory { get; private set; }
    public BoardCategory? PendingCategory { get; private set; }
    public bool IsDragInProgress => _dragInProgress;
    public bool IsTextEditingActive => _textEditingActive;
    public bool IsPanelHoldActive => _panelHoldActive;
    public bool HasOverlayInteraction => _overlayReasons.Count > 0;
    public bool IsExpanded { get; private set; }

    /// <summary>再展开抑制窗（#30）是否布防：一次已提交的启动收起会话尚未观测到
    /// 指针离开收起前原区域。</summary>
    public bool IsReexpandSuppressed => _reexpandSuppressionArmed;

    public void BeginHover(BoardCategory category)
    {
        Validate(category);
        _pointerInside = true;
        PendingCategory = category;
    }

    public bool TryCancelHover(BoardCategory category)
    {
        Validate(category);
        if (PendingCategory != category)
        {
            return false;
        }

        PendingCategory = null;
        return true;
    }

    public bool TryCommitHover(out BoardCategory category)
    {
        if (!_pointerInside || PendingCategory is not { } pending)
        {
            category = default;
            return false;
        }

        category = pending;
        Switch(pending);
        return true;
    }

    public void Switch(BoardCategory category)
    {
        Validate(category);
        ActiveCategory = category;
        PendingCategory = null;
        IsExpanded = true;
        // 权威展开（#30）：面板再次展开即终止再展开抑制会话——抑制闩属于「一次已
        // 提交的启动收起」，新展开会话不再受旧收起的离场观测约束（热键等展开路径
        // 由此单点清闩）。
        _reexpandSuppressionArmed = false;
    }

    public void EnterSurface() => _pointerInside = true;

    public void LeaveSurface()
    {
        _pointerInside = false;
        PendingCategory = null;
    }

    public void BeginDrag() => _dragInProgress = true;

    public void EndDrag() => _dragInProgress = false;

    /// <summary>面板内文本编辑控件获得键盘焦点期间，收起被显式抑制（IME 候选窗会补发假的指针离开）。</summary>
    public void BeginTextEditing() => _textEditingActive = true;

    public void EndTextEditing() => _textEditingActive = false;

    /// <summary>用户显式断言面板保持展开（会话级、不持久化），抑制指针离开引发的自动收起。</summary>
    public void BeginPanelHold() => _panelHoldActive = true;

    public void EndPanelHold() => _panelHoldActive = false;

    /// <summary>覆盖交互 hold（#30）：＋菜单/模态对话框是独立 HWND，指针移入即补发假
    /// MouseLeave，打开期间收起被抑制（Begin/End 幂等，同因重复不累计）。</summary>
    public void BeginOverlayInteraction(PanelOverlayReason reason) => _overlayReasons.Add(reason);

    public void EndOverlayInteraction(PanelOverlayReason reason) => _overlayReasons.Remove(reason);

    public void CollapseForExternalDrop()
    {
        PendingCategory = null;
        IsExpanded = false;
    }

    /// <summary>启动后排队收起的强制提交（#30，Issue #162）：抑制条件已在等待期逐一
    /// 重验（面板仍展开/同会话/无文本编辑/无覆盖交互（＋菜单/模态对话框）/非编辑模式/
    /// 保持展开关/无更新失败），指针在内不是抑制项——收起后指针停留期间的悬停抑制由
    /// 再展开抑制窗另行承载。</summary>
    public void ForceCollapse()
    {
        PendingCategory = null;
        IsExpanded = false;
    }

    public bool TryCollapse()
    {
        if (_pointerInside || _dragInProgress || _textEditingActive || _panelHoldActive ||
            HasOverlayInteraction || !IsExpanded)
        {
            return false;
        }

        IsExpanded = false;
        return true;
    }

    /// <summary>只读探针：当前条件若调用 TryCollapse 是否会提交收起，不改变状态。</summary>
    public bool WouldCollapse =>
        !_pointerInside && !_dragInProgress && !_textEditingActive && !_panelHoldActive &&
        !HasOverlayInteraction && IsExpanded;

    /// <summary>布防再展开抑制窗（#30）：在启动收起提交点记录收起前面板原区域
    /// （屏幕物理像素，含左上不含右下，与 Win32 矩形语义一致）。矩形退化（捕获
    /// 失败形态）＝不布防——抑制是体验增强而非安全机制，绝不留下无观测依据的闩
    /// （fail-open，Issue #170）。</summary>
    public void BeginReexpandSuppression(int left, int top, int right, int bottom)
    {
        if (right <= left || bottom <= top)
        {
            return;
        }

        _reexpandSuppressionLeft = left;
        _reexpandSuppressionTop = top;
        _reexpandSuppressionRight = right;
        _reexpandSuppressionBottom = bottom;
        _reexpandSuppressionArmed = true;
    }

    /// <summary>轮询观测一次指针位置（#30）：布防期间指针在收起前原区域外即解除
    /// 抑制并返回 true（本次观测完成了离场解除）；未布防或指针仍在原区域内返回
    /// false。窗口收起后仅把手区接收鼠标消息，原区域的离场只能靠采样式轮询观测
    /// （快速离场回场可能漏观测，抑制保持到下次观测离场——定义语义）。</summary>
    public bool ObservePointer(int x, int y)
    {
        if (!_reexpandSuppressionArmed)
        {
            return false;
        }

        if (x >= _reexpandSuppressionLeft && x < _reexpandSuppressionRight &&
            y >= _reexpandSuppressionTop && y < _reexpandSuppressionBottom)
        {
            return false;
        }

        _reexpandSuppressionArmed = false;
        return true;
    }

    private static void Validate(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
