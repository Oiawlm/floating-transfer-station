using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

public partial class MainWindow
{
    private const int CardEntranceStaggerWindowMs = 400;
    private const int CardEntranceMaxStaggerSteps = 8;
    private static readonly TimeSpan CardEntranceDuration =
        TimeSpan.FromMilliseconds(DesignTokens.PanelExpandContentMs);
    private ObservableCollection<BoardItem>? _entranceItems;
    private long _cardEntranceLastAt;
    private int _cardEntranceStaggerCursor;

    private void AttachCardEntrance(ObservableCollection<BoardItem> items)
    {
        if (ReferenceEquals(_entranceItems, items))
        {
            return;
        }

        if (_entranceItems is not null)
        {
            _entranceItems.CollectionChanged -= ActivePanelItems_CollectionChanged;
        }

        _entranceItems = items;
        _entranceItems.CollectionChanged += ActivePanelItems_CollectionChanged;
    }

    private void ActivePanelItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ClientAreaAnimationsEnabled ||
            _isClosing ||
            !_viewModel.IsPanelExpanded ||
            (e.Action != NotifyCollectionChangedAction.Add &&
                e.Action != NotifyCollectionChangedAction.Move) ||
            e.NewItems is not { Count: > 0 })
        {
            return;
        }

        var items = e.NewItems.OfType<BoardItem>().ToArray();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => AnimateCardEntrance(items)));
    }

    private void AnimateCardEntrance(BoardItem[] items)
    {
        foreach (var item in items)
        {
            if (BoardList.ItemContainerGenerator.ContainerFromItem(item)
                is not ListBoxItem container)
            {
                continue;
            }

            // 连续到达的条目按固定步长交错入场；静默窗口后重置，交错步数封顶。
            var now = Environment.TickCount64;
            if (now - _cardEntranceLastAt > CardEntranceStaggerWindowMs)
            {
                _cardEntranceStaggerCursor = 0;
            }

            _cardEntranceLastAt = now;
            var begin = TimeSpan.FromMilliseconds(
                Math.Min(_cardEntranceStaggerCursor++, CardEntranceMaxStaggerSteps) *
                DesignTokens.ItemEntranceStaggerMs);
            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.SetValue(UIElement.OpacityProperty, 1d);
            container.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0d, 1d, CardEntranceDuration)
                {
                    BeginTime = begin,
                    EasingFunction = FadeAnimation.EnterEasing,
                    FillBehavior = FillBehavior.Stop,
                });

            var transform = container.RenderTransform as TranslateTransform ?? new TranslateTransform();
            container.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.SetValue(TranslateTransform.YProperty, 0d);
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(DesignTokens.ContentEntranceOffsetPx, 0d, CardEntranceDuration)
                {
                    BeginTime = begin,
                    EasingFunction = FadeAnimation.EnterEasing,
                    FillBehavior = FillBehavior.Stop,
                });
        }
    }

    private void StopCardEntranceAnimations()
    {
        for (var index = 0; index < BoardList.Items.Count; index++)
        {
            if (BoardList.ItemContainerGenerator.ContainerFromIndex(index)
                is not ListBoxItem container)
            {
                continue;
            }

            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.SetValue(UIElement.OpacityProperty, 1d);
            if (container.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.SetValue(TranslateTransform.YProperty, 0d);
            }
        }
    }
}
