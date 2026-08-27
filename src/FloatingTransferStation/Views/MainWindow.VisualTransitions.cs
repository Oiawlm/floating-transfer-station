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

    private void SaveCurrentScrollOffset()
    {
        if (_viewModel.ActivePanel is not { } panel ||
            FindDescendant<ScrollViewer>(BoardList) is not { } viewer)
        {
            return;
        }

        _scrollState.Save(panel.Category, viewer.VerticalOffset);
    }

    private double CurrentScrollOffset() =>
        FindDescendant<ScrollViewer>(BoardList)?.VerticalOffset ?? 0d;

    private void RestoreExplicitScrollOffset(BoardCategory category, double offset)
    {
        _scrollState.Save(category, offset);
        RestoreScrollOffset(category);
    }

    private void RestoreScrollOffset(BoardCategory category)
    {
        var version = ++_scrollRestoreVersion;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (version != _scrollRestoreVersion ||
                    _viewModel.ActivePanel?.Category != category ||
                    FindDescendant<ScrollViewer>(BoardList) is not { } viewer)
                {
                    return;
                }

                viewer.ScrollToVerticalOffset(
                    _scrollState.GetClamped(category, viewer.ScrollableHeight));
            }));
    }

    private void CategoryTab_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsExternalDropRailVisible)
        {
            _expandIntentTimer.Stop();
            _collapseTimer.Stop();
            return;
        }

        if (_panelState.IsDragInProgress)
        {
            _expandIntentTimer.Stop();
            _collapseTimer.Stop();
            return;
        }

        if (sender is not Border { DataContext: CategoryViewModel category })
        {
            return;
        }

        _collapseTimer.Stop();
        _expandIntentTimer.Stop();
        _panelState.EnterSurface();
        if (_panelState.IsExpanded)
        {
            if (_panelState.ActiveCategory == category.Category)
            {
                return;
            }

            SaveCurrentScrollOffset();
            _panelState.Switch(category.Category);
            _viewModel.Activate(category.Category);
            RestoreScrollOffset(category.Category);
            AnimatePanelContent(isCategorySwitch: true);
            return;
        }

        _panelState.BeginHover(category.Category);
        _expandIntentTimer.Start();
    }

    private void CategoryTab_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border { DataContext: CategoryViewModel category } &&
            !_panelState.IsExpanded &&
            _panelState.TryCancelHover(category.Category))
        {
            _expandIntentTimer.Stop();
        }
    }

    private void WindowShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WindowShell.Clip = WindowShellClip.Create(
            e.NewSize.Width,
            e.NewSize.Height,
            WindowShell.CornerRadius.TopLeft);
    }

    private void CategoryTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            sender is not Border { DataContext: CategoryViewModel category })
        {
            return;
        }

        _viewModel.SetDefaultCaptureCategory(category.Category);
        e.Handled = true;
    }

    private void CategoryTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            e.ClickCount < 2 ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null ||
            sender is not Border { DataContext: CategoryViewModel category })
        {
            return;
        }

        _viewModel.BeginCategoryNameEdit(category);
        e.Handled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!category.IsEditingName ||
                    sender is not Border tab ||
                    FindDescendant<TextBox>(tab) is not { } editor)
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }));
    }

    private void CategoryNameEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not CategoryViewModel category ||
            !category.IsEditingName ||
            _activeCategoryNameCompositions.Contains(editor))
        {
            return;
        }

        var limited = BoardCategoryCatalog.LimitDisplayName(editor.Text);
        if (limited == editor.Text)
        {
            return;
        }

        editor.Text = limited;
        editor.CaretIndex = editor.Text.Length;
    }

    private void CategoryNameEditor_CompositionStartedOrUpdated(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox
            {
                DataContext: CategoryViewModel { IsEditingName: true }
            } editor)
        {
            _activeCategoryNameCompositions.Add(editor);
        }
    }

    private void CategoryNameEditor_CompositionCompleted(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox editor)
        {
            _activeCategoryNameCompositions.Remove(editor);
        }
    }

    private void CategoryNameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not CategoryViewModel category)
        {
            return;
        }

        if (_activeCategoryNameCompositions.Contains(editor))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitCategoryNameEdit(category, editor.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _activeCategoryNameCompositions.Remove(editor);
            _viewModel.CancelCategoryNameEdit(category);
            ReconcileSurfaceAfterCategoryNameEdit();
        }
    }

    private void CategoryNameEditor_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: CategoryViewModel category } editor)
        {
            _activeCategoryNameCompositions.Remove(editor);
            CommitCategoryNameEdit(category, editor.Text);
        }
    }

    private void CommitCategoryNameEdit(CategoryViewModel category, string draftName)
    {
        if (!category.IsEditingName)
        {
            return;
        }

        var name = _viewModel.EndCategoryNameEdit(category, draftName);
        ReconcileSurfaceAfterCategoryNameEdit();
        if (name == category.DisplayName)
        {
            return;
        }

        TrackPendingOperation(SaveCategoryNameAsync(category, name));
    }

    private async Task SaveCategoryNameAsync(CategoryViewModel category, string name)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var originalName = category.DisplayName;
            _settings = _settings.WithCategoryName(category.Category, name);
            try
            {
                await _store.SaveSettingsAsync(_settings);
                _viewModel.ApplyCategoryName(category, name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _settings = _settings.WithCategoryName(category.Category, originalName);
                ShowStatus("分类名称未保存，已恢复原名称。");
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void ExpandIntentTimer_Tick(object? sender, EventArgs e)
    {
        _expandIntentTimer.Stop();
        if (_viewModel.IsExternalDropRailVisible ||
            !_panelState.TryCommitHover(out var category))
        {
            return;
        }

        _viewModel.Activate(category);
        ApplyPlacement(WindowController.Expanded(CurrentWorkArea(), _settings));
        _viewModel.SetPanelExpanded(true);
        UpdateStatusPresentation();
        CategoryRail.UpdateLayout();
        AnimateRevealedCategoryTabs();
        RestoreScrollOffset(category);
        AnimatePanelContent(isCategorySwitch: false);
    }

    private void AnimateRevealedCategoryTabs()
    {
        StopCategoryRevealAnimations();
        if (!ClientAreaAnimationsEnabled)
        {
            return;
        }

        foreach (var tab in CategoryTabs())
        {
            if (tab.DataContext is not CategoryViewModel category || category.IsDefaultCapture)
            {
                continue;
            }

            var transform = tab.RenderTransform as TranslateTransform ?? new TranslateTransform();
            tab.RenderTransform = transform;
            tab.BeginAnimation(
                UIElement.OpacityProperty,
                CreateCategoryRevealAnimation(0d, 1d),
                CategoryRevealAnimationHandoffBehavior);
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                CreateCategoryRevealAnimation(CategoryRevealOffset, 0d),
                CategoryRevealAnimationHandoffBehavior);
        }
    }

    private static DoubleAnimation CreateCategoryRevealAnimation(double from, double to) =>
        new(from, to, new Duration(CategoryRevealAnimationDuration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

    private void StopCategoryRevealAnimations()
    {
        foreach (var tab in CategoryTabs())
        {
            tab.BeginAnimation(
                UIElement.OpacityProperty,
                null,
                CategoryRevealAnimationHandoffBehavior);
            tab.SetCurrentValue(UIElement.OpacityProperty, 1d);
            if (tab.RenderTransform is not TranslateTransform transform)
            {
                continue;
            }

            transform.BeginAnimation(
                TranslateTransform.XProperty,
                null,
                CategoryRevealAnimationHandoffBehavior);
            transform.SetCurrentValue(TranslateTransform.XProperty, 0d);
        }
    }

    private IEnumerable<Border> CategoryTabs() =>
        Descendants<Border>(CategoryRail).Where(candidate =>
            candidate.DataContext is CategoryViewModel &&
            candidate.Tag is BoardCategory);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void AnimatePanelContent(bool isCategorySwitch)
    {
        var fullMotion = ClientAreaAnimationsEnabled;
        var duration = fullMotion
            ? isCategorySwitch
                ? SwitchContentAnimationDuration
                : ExpandContentAnimationDuration
            : ReducedMotionContentAnimationDuration;
        var startX = fullMotion && !isCategorySwitch ? 6d : 0d;

        StopPanelContentAnimation();

        PanelContentHost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation
            {
                From = 0d,
                To = 1d,
                Duration = new Duration(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);

        if (startX == 0d)
        {
            return;
        }

        PanelContentTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation
            {
                From = startX,
                To = 0d,
                Duration = new Duration(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopPanelContentAnimation()
    {
        PanelContentHost.BeginAnimation(
            UIElement.OpacityProperty,
            null,
            HandoffBehavior.SnapshotAndReplace);
        PanelContentTransform.BeginAnimation(
            TranslateTransform.XProperty,
            null,
            HandoffBehavior.SnapshotAndReplace);
        PanelContentHost.Opacity = 1d;
        PanelContentTransform.X = 0d;
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        StopPanelContentAnimation();
        _collapseTimer.Stop();
        _panelState.EnterSurface();
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandIntentTimer.Stop();
        _collapseTimer.Stop();
        if (IsCategoryNameEditActive())
        {
            return;
        }

        _panelState.LeaveSurface();
        if (_panelState.IsExpanded)
        {
            _collapseTimer.Start();
        }
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (IsCategoryNameEditActive() || !_panelState.TryCollapse())
        {
            return;
        }

        SaveCurrentScrollOffset();
        StopCategoryRevealAnimations();
        BeginCollapsedVisualHandoff(WindowController.Collapsed(
            CurrentWorkArea(),
            _settings,
            _viewModel.DefaultCapturePanel.Category));
    }

    private bool IsCategoryNameEditActive() =>
        _viewModel.Categories.Any(category => category.IsEditingName);

    private void ReconcileSurfaceAfterCategoryNameEdit()
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

    private void BeginCollapsedVisualHandoff(WindowPlacement placement)
    {
        WindowShell.Opacity = 0d;
        WindowShell.IsHitTestVisible = false;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => CompleteCollapsedVisualHandoff(placement)));
    }

    private void CompleteCollapsedVisualHandoff(WindowPlacement placement)
    {
        ApplyPlacement(placement);
        _viewModel.SetPanelExpanded(false);
        UpdateStatusPresentation();
        WindowShell.Opacity = 1d;
        WindowShell.IsHitTestVisible = true;
    }
}
