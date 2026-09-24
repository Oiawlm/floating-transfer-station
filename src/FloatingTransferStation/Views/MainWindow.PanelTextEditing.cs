using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    private void InitializePanelTextEditing()
    {
        AddHandler(
            Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(Root_PreviewGotKeyboardFocus));
        AddHandler(
            Keyboard.PreviewLostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(Root_PreviewLostKeyboardFocus));
        Deactivated += MainWindow_Deactivated;
        Activated += MainWindow_Activated;
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

    /// <summary>指针离开与收起到点共用：改名草稿还在或面板内编辑控件持有焦点时都不自动收起。</summary>
    private bool IsPanelEditHoldActive() =>
        IsCategoryNameEditActive() ||
        _panelState.IsTextEditingActive ||
        IsPanelTextEditor(Keyboard.FocusedElement);

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
