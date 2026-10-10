using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    // 复盘编辑器的 IME 组合标记：组合期的 Esc 属输入法操作（取消候选），不触发退出编辑。
    private bool _isReviewEditorComposing;

    private void InitializePanelTextEditing()
    {
        AddHandler(
            Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(Root_PreviewGotKeyboardFocus));
        AddHandler(
            Keyboard.PreviewLostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(Root_PreviewLostKeyboardFocus));
        AddHandler(
            TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler(ReviewEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler(ReviewEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler(ReviewEditor_CompositionCompleted),
            true);
        Deactivated += MainWindow_Deactivated;
        Activated += MainWindow_Activated;
    }

    // 复盘编辑器内按 Esc 退出编辑态：释放键盘焦点（移交宿主窗口），面板去留交给
    // 既有焦点链（Root_PreviewLostKeyboardFocus 统一清算保持原因并重估表面——
    // 指针在面板内时维持展开，否则按既有节奏收起）。复盘是防抖自动保存，
    // 没有取消语义，因此这里不引入提交/取消会话。
    private void ReviewEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_isReviewEditorComposing)
        {
            return;
        }

        e.Handled = true;
        // WPF 会把「聚焦焦点域本体」重定向回域内 FocusedElement（此处即编辑器），
        // 先清掉域内记录再聚焦宿主窗口，键盘焦点才会真正离开编辑器；
        // 随后的 PreviewLostKeyboardFocus 由既有链清算编辑保持原因并重估表面。
        FocusManager.SetFocusedElement(this, null);
        Focus();
    }

    private void ReviewEditor_CompositionStartedOrUpdated(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource == ReviewEditor)
        {
            _isReviewEditorComposing = true;
        }
    }

    private void ReviewEditor_CompositionCompleted(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource == ReviewEditor)
        {
            _isReviewEditorComposing = false;
        }
    }

    private void ReviewEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _isReviewEditorComposing = false;
    }

    private void Root_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsPanelTextEditor(e.NewFocus))
        {
            _panelState.BeginTextEditing();
        }
    }

    private void Root_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点在两个编辑控件之间转移时保持抑制，由随后的 Got 处理器继续持有该原因。
        if (!IsPanelTextEditor(e.OldFocus) || IsPanelTextEditor(e.NewFocus))
        {
            return;
        }

        _panelState.EndTextEditing();
        if (!_isClosing)
        {
            ReconcileSurfaceAfterEditing();
        }
    }

    // 窗口失活（点击其他应用、Alt+Tab）时 WM_KILLFOCUS 未必以路由焦点事件到达这里；
    // 显式清算编辑保持原因并重估表面，避免抑制原因卡死导致面板此后不再自动收起。
    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_panelState.IsTextEditingActive)
        {
            return;
        }

        _panelState.EndTextEditing();
        if (!_isClosing)
        {
            ReconcileSurfaceAfterEditing();
        }
    }

    // 复激时 WPF 可能静默恢复编辑器焦点（不走路由事件），延迟到输入优先级补记保持原因。
    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (IsPanelTextEditor(Keyboard.FocusedElement))
                {
                    _panelState.BeginTextEditing();
                }
            }));
    }

    /// <summary>指针离开与收起到点共用：改名草稿还在、卡片/面板编辑进行中或搜索态进行中时都不自动收起。</summary>
    private bool IsPanelEditHoldActive() =>
        IsCategoryNameEditActive() ||
        _panelState.IsTextEditingActive ||
        _editingCardItemId is not null ||
        IsPanelTextEditor(Keyboard.FocusedElement) ||
        _viewModel.IsSearchActive;

    // 编辑器键盘焦点是比指针位置更强的使用意图信号：IME 组合窗/候选窗是独立 HWND，
    // 出现在指针下方时系统会补发假的 MouseLeave，判定因此基于焦点而非指针位置。
    private bool IsPanelTextEditor(IInputElement? focus) =>
        focus is TextBoxBase editor && IsAncestorOf(editor);

    /// <summary>编辑保持结束后的表面重估：指针仍在面板内则维持，否则按既有节奏恢复收起。</summary>
    private void ReconcileSurfaceAfterEditing()
    {
        if (IsMouseOver)
        {
            _panelState.EnterSurface();
            return;
        }

        _panelState.LeaveSurface();
        if (_panelState.IsExpanded)
        {
            _collapseTimer.Start();
        }
    }
}
