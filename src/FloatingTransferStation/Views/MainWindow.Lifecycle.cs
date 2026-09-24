using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{

    private void WidthThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var work = CurrentWorkArea();
        _settings = (_settings with { PanelWidth = _settings.PanelWidth - e.HorizontalChange })
            .Normalize(work.Width, work.Height);
        Width = _settings.PanelWidth + WindowSettings.TabWidth + _rightEdgeBleed;
        DockRight();
    }

    private void HeightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var work = CurrentWorkArea();
        _settings = (_settings with { WindowHeight = _settings.WindowHeight + e.VerticalChange })
            .Normalize(work.Width, work.Height);
        Height = _settings.WindowHeight;
        DockRight();
    }

    private void HeaderThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var work = CurrentWorkArea();
        _settings = (_settings with { Top = _settings.Top + e.VerticalChange })
            .Normalize(work.Width, work.Height);
        Top = work.Top + _settings.Top;
    }

    private async void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("窗口尺寸暂未保存。");
        }
    }

    private void DockRight()
    {
        var work = CurrentWorkArea();
        Left = work.Right - (ActualWidth - _rightEdgeBleed);
    }

    /// <summary>重估右缘裁切量（显示器/任务栏/邻接屏可能已变化），同步内缩内容层并按当前面板状态重新贴齐。</summary>
    private void RefreshEdgeBleed()
    {
        if (_rightEdgeBleedProvider is null)
        {
            return;
        }

        _rightEdgeBleed = _rightEdgeBleedProvider.Invoke(this);
        ApplyRightEdgeBleedInset();
        ReapplyCurrentPlacement();
    }

    /// <summary>
    /// 边缘裁切时窗口右缘越出屏幕；内容层按裁切量右内缩，使轨道与面板完整落在屏幕内。
    /// </summary>
    private void ApplyRightEdgeBleedInset()
    {
        if (LayoutRoot is null)
        {
            return;
        }

        LayoutRoot.Margin = new Thickness(0, 0, _rightEdgeBleed, 0);
    }

    private void ReapplyCurrentPlacement()
    {
        var work = CurrentWorkArea();
        ApplyPlacement(_viewModel.IsPanelExpanded
            ? WindowController.Expanded(work, _settings, _rightEdgeBleed)
            : WindowController.Collapsed(
                work,
                _settings,
                _viewModel.DefaultCapturePanel.Category,
                _rightEdgeBleed));
    }

    private static WorkArea CurrentWorkArea()
    {
        var area = SystemParameters.WorkArea;
        return new WorkArea(area.Left, area.Top, area.Width, area.Height);
    }

    private void ApplyPlacement(WindowPlacement placement)
    {
        CancelPanelCollapseExit();
        CancelCollapsedVisualHandoff();
        if (TryApplyPlacementAtomically(placement))
        {
            return;
        }

        ApplyPlacementThroughWindowProperties(placement);
    }

    /// <summary>
    /// 迁移原子性：一次面板状态迁移只允许一次窗口矩形变更。对已创建 HWND 的窗口，
    /// 先用一次 Win32 SetWindowPos 直接应用终态矩形（物理像素），再在守卫下对齐 WPF
    /// 尺寸/位置账本；守卫把对齐期间任何 WM_WINDOWPOSCHANGING 的中间矩形改写回终态
    /// （矩形相等时为 no-op），避免陈旧 DP 混合值重推出可见的中间矩形。
    /// </summary>
    private bool TryApplyPlacementAtomically(WindowPlacement placement)
    {
        var source = _windowSource;
        var handle = source?.Handle ?? 0;
        if (source is null || handle == 0 || source.CompositionTarget is null)
        {
            return false;
        }

        // DIP 数值为权威，仅在调用边界换算一次；两条锚定边各自取整后相减得到跨度，
        // 保证右缘（含边缘裁切）与底缘换算后不因取整漂移。
        var transform = source.CompositionTarget.TransformToDevice;
        var left = (int)Math.Round(placement.Left * transform.M11);
        var top = (int)Math.Round(placement.Top * transform.M22);
        var width = (int)Math.Round((placement.Left + placement.Width) * transform.M11) - left;
        var height = (int)Math.Round((placement.Top + placement.Height) * transform.M22) - top;
        _placementTargetRectangle = (left, top, width, height);
        try
        {
            NativeMethods.SetWindowPos(
                handle,
                0,
                left,
                top,
                width,
                height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            Width = placement.Width;
            Height = placement.Height;
            Left = placement.Left;
            Top = placement.Top;
        }
        finally
        {
            _placementTargetRectangle = null;
        }

        return true;
    }

    /// <summary>构造期（HWND 未创建）的纯属性路径：Show 之前无闪现风险，保持原顺序。</summary>
    private void ApplyPlacementThroughWindowProperties(WindowPlacement placement)
    {
        var expandsHorizontally = placement.Width > ActualWidth;
        var expandsVertically = placement.Height > ActualHeight;
        if (expandsHorizontally)
        {
            Width = placement.Width;
        }

        if (expandsVertically)
        {
            Height = placement.Height;
        }

        Left = placement.Left;
        Top = placement.Top;
        if (!expandsHorizontally)
        {
            Width = placement.Width;
        }

        if (!expandsVertically)
        {
            Height = placement.Height;
        }
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            await _store.SaveSettingsAsync(_settings);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            StopClipboardListening();
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        IsEnabled = false;
        _ = TrySetGlobalHotkey(false);
        var operationCancellation = _windowOperationCancellation;
        try
        {
            operationCancellation.Cancel();
            _reviewSaveTimer.Stop();
            TrackPendingOperation(FlushReviewAsync());
            await DrainPendingOperationsAsync();
            _dailyReviews?.StopWatching();
            await _mutations.SaveForShutdownAsync(() => _store.SaveSettingsAsync(_settings));
            // 关闭序列保存成功后,可撤销删除不再恢复,清理其保留的图片文件。
            _mutations.DiscardUndoableDeletes();
            operationCancellation.Dispose();
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Close));
        }
        catch (Exception)
        {
            operationCancellation.Dispose();
            _windowOperationCancellation = new CancellationTokenSource();
            _isClosing = false;
            IsEnabled = true;
            ShowStatus("退出前保存失败，悬浮中转站暂未关闭。");
        }
    }

    private async Task DrainPendingOperationsAsync()
    {
        Task[] operations;
        lock (_pendingOperationsLock)
        {
            operations = [.. _pendingOperations];
        }

        await Task.WhenAll(operations);
    }

    private void StopClipboardListening()
    {
        if (_windowSource is null)
        {
            return;
        }

        NativeMethods.RemoveClipboardFormatListener(_windowSource.Handle);
        _windowSource.RemoveHook(WndProc);
        _windowSource = null;
    }
}
