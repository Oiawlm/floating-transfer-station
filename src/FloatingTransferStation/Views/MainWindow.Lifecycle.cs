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
        Width = _settings.PanelWidth + WindowSettings.TabWidth;
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
        Left = work.Right - ActualWidth;
    }

    private static WorkArea CurrentWorkArea()
    {
        var area = SystemParameters.WorkArea;
        return new WorkArea(area.Left, area.Top, area.Width, area.Height);
    }

    private void ApplyPlacement(WindowPlacement placement)
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
        var operationCancellation = _windowOperationCancellation;
        try
        {
            operationCancellation.Cancel();
            await DrainPendingOperationsAsync();
            await _mutations.SaveForShutdownAsync(() => _store.SaveSettingsAsync(_settings));
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
