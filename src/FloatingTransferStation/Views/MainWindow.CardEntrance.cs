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
        // 集合变化统一交给编辑会话刷新的锚点判定（判定源唯一）：锚点被移除/
        // 集合重置 → 终结提交；新增/移动只改变锚点位置 → 覆盖层重定位跟随。
        // 延迟到 Loaded 优先级：容器生成滞后于集合事件，同步判定会拿到
        // 中间态的旧容器。
        if (_editingCardItemId is not null)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(RefreshCardEditSession));
        }

        // 入场动画只属于真正新入库的内容(剪贴板捕获、拖入走单条 Add)。
        // 程序性重排与批量恢复通过 BoardItemCollection.ReplaceAll 发单个 Reset,
        // 不在此重演入场,避免大批量操作触发逐条动画风暴。
        if (!ClientAreaAnimationsEnabled ||
            _isClosing ||
            !_viewModel.IsPanelExpanded ||
            e.Action != NotifyCollectionChangedAction.Add ||
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
