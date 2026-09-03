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
