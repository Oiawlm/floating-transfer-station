using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    private void PanelHoldButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_panelState.IsPanelHoldActive)
        {
            _panelState.EndPanelHold();
            UpdatePanelHoldButton();
            if (!_isClosing)
            {
                ReconcileSurfaceAfterPanelHold();
            }

            return;
        }

        // 固定 = 用户断言面板保持展开：作废任何待提交的收起与行进中的计时。
        _panelState.BeginPanelHold();
        CancelPanelCollapseExit();
        _collapseTimer.Stop();
        UpdatePanelHoldButton();
    }

    private void UpdatePanelHoldButton()
    {
        var held = _panelState.IsPanelHoldActive;
        // SetResourceReference 保持动态解析：主题切换时强调色/次级文字色自动跟随。
        PanelHoldButton.SetResourceReference(
            Control.ForegroundProperty,
            held ? "AccentBrush" : "SecondaryTextBrush");
        var label = held ? "已保持展开，点击恢复自动收起" : "保持展开，暂停自动收起";
        PanelHoldButton.ToolTip = label;
        AutomationProperties.SetName(PanelHoldButton, label);
    }

    /// <summary>解除保持后的表面重估：指针仍在面板内则维持展开，否则按既有节奏恢复自动收起。</summary>
    private void ReconcileSurfaceAfterPanelHold()
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

    /// <summary>
    /// 外部拖放会话结束后，若面板保持展开仍然有效，按既有展开序列回展：
    /// 恢复原分类（ActiveCategory 为空时回落默认接收分类）、展开几何与滚动位置。
    /// </summary>
    private void RestoreHeldPanelAfterExternalDrop()
    {
        var category = _panelState.ActiveCategory ?? _viewModel.DefaultCapturePanel.Category;
        _panelState.Switch(category);
        ActivatePanel(category);
        ApplyPlacement(WindowController.Expanded(CurrentWorkArea(), _settings, _rightEdgeBleed));
        _viewModel.SetPanelExpanded(true);
        UpdateStatusPresentation();
        CategoryRail.UpdateLayout();
        RestoreScrollOffset(category);
        AnimatePanelContent(isCategorySwitch: false);
    }
}
