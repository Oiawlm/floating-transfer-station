using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 设置窗口「标签顺序」节（1.25.0）：四个固定标签（三个板卡分类 + 复盘）的显示
/// 顺序调整，只调顺序、不能增删分类；排序入口只在设置窗口（面板标签轨本身不
/// 提供拖拽）。拖拽自研最小实现、零新依赖：把手按下捕获鼠标，拖起原位行半透明
/// 跟随光标，其余行经 TranslateTransform 让位（动效时长取既有 DesignTokens 档，
/// 「界面动效」关闭时退化为瞬时换位），2px 强调色指示线标记落点；浅/深主题用
/// 主题字典画刷，不硬编码颜色。上移/下移按钮为无障碍等价操作（WCAG 2.5.7）。
/// 拖拽会话中的 Esc 由本节优先消费并回弹原序不提交（处理顺序在设置窗口既有
/// Esc 关窗路径之前），捕获丢失/失焦同样回弹。提交经宿主
/// <see cref="ISettingsHost.ApplyCategoryOrderAsync"/>：settings.json 原子保存 +
/// 面板标签轨即时重排；保存失败宿主保持原顺序，重建行即回弹。
/// </summary>
public partial class SettingsWindow
{
    /// <summary>行高即行距（四行连续带状，索引与偏移按此换算）。</summary>
    private const double CategoryOrderPitch = 34;

    private CategoryOrderDragSession? _categoryOrderDrag;
    private bool _isCategoryOrderCommitInFlight;

    private sealed class CategoryOrderDragSession
    {
        public required FrameworkElement CaptureElement { get; init; }
        public required int SourceIndex { get; init; }
        public required double StartY { get; init; }

        /// <summary>落点随拖拽移动更新，初值必须等于起点：纯点击把手（无移动）
        /// 松手时 target==source，不做任何移动。</summary>
        public int TargetIndex { get; set; }
    }

    private void PopulateCategoryOrderSection()
    {
        CategoryOrderList.Children.Clear();
        var order = _host.CurrentDisplayOrder;
        for (var index = 0; index < order.Count; index++)
        {
            CategoryOrderList.Children.Add(CreateCategoryOrderRow(order[index], index, order.Count));
        }
    }

    private Border CreateCategoryOrderRow(BoardCategory category, int index, int count)
    {
        var name = _host.CategoryDisplayName(category);
        var row = new Border
        {
            Height = CategoryOrderPitch,
            CornerRadius = new CornerRadius(4),
            Background = TryFindResource("CardBrush") as Brush,
            BorderBrush = TryFindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            Tag = category
        };
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var handle = new Border
        {
            Width = 24,
            Cursor = Cursors.SizeAll,
            Background = Brushes.Transparent
        };
        handle.Child = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 12,
            Foreground = TryFindResource("SecondaryTextBrush") as Brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(handle, $"拖动「{name}」调整顺序");
        handle.MouseLeftButtonDown += CategoryOrderHandle_MouseLeftButtonDown;
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TryFindResource("PrimaryTextBrush") as Brush
        };
        AutomationProperties.SetName(label, $"标签{name}");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var moveUp = new Button
        {
            Style = TryFindResource("ThemedActionButtonStyle") as Style,
            Content = "↑",
            Width = 32,
            Margin = new Thickness(0, 0, 4, 0),
            IsEnabled = index > 0,
            Tag = category
        };
        AutomationProperties.SetName(moveUp, $"上移「{name}」");
        moveUp.Click += CategoryOrderMoveUpButton_Click;
        Grid.SetColumn(moveUp, 2);
        grid.Children.Add(moveUp);

        var moveDown = new Button
        {
            Style = TryFindResource("ThemedActionButtonStyle") as Style,
            Content = "↓",
            Width = 32,
            IsEnabled = index < count - 1,
            Tag = category
        };
        AutomationProperties.SetName(moveDown, $"下移「{name}」");
        moveDown.Click += CategoryOrderMoveDownButton_Click;
        Grid.SetColumn(moveDown, 3);
        grid.Children.Add(moveDown);

        row.Child = grid;
        return row;
    }

    private async void CategoryOrderMoveUpButton_Click(object sender, RoutedEventArgs e) =>
        await MoveCategoryOrderBy(sender, delta: -1);

    private async void CategoryOrderMoveDownButton_Click(object sender, RoutedEventArgs e) =>
        await MoveCategoryOrderBy(sender, delta: 1);

    /// <summary>上移/下移等价操作（WCAG 2.5.7）：提交后把焦点还给同分类的对应
    /// 按钮，保持键盘连续操作；提交期间忽略重复触发（防快速连点丢步）。</summary>
    private async Task MoveCategoryOrderBy(object sender, int delta)
    {
        if (_categoryOrderDrag is not null ||
            _isCategoryOrderCommitInFlight ||
            sender is not Button { Tag: BoardCategory category })
        {
            return;
        }

        var order = _host.CurrentDisplayOrder.ToList();
        var index = order.IndexOf(category);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return;
        }

        (order[index], order[target]) = (order[target], order[index]);
        _isCategoryOrderCommitInFlight = true;
        try
        {
            await _host.ApplyCategoryOrderAsync(order);
            PopulateCategoryOrderSection();
        }
        finally
        {
            _isCategoryOrderCommitInFlight = false;
        }

        if (TryFindCategoryOrderButton(category, delta) is { } button)
        {
            button.Focus();
        }
    }

    private Button? TryFindCategoryOrderButton(BoardCategory category, int delta)
    {
        var content = delta < 0 ? "↑" : "↓";
        return FindCategoryOrderElements<Button>()
            .FirstOrDefault(button => Equals(button.Tag, category) && Equals(button.Content, content));
    }

    private IEnumerable<T> FindCategoryOrderElements<T>() where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(CategoryOrderList);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(CategoryOrderList, index);
            if (child is T match)
            {
                yield return match;
            }

            var queue = new System.Collections.Generic.Queue<DependencyObject>();
            queue.Enqueue(child);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var children = VisualTreeHelper.GetChildrenCount(current);
                for (var inner = 0; inner < children; inner++)
                {
                    var nested = VisualTreeHelper.GetChild(current, inner);
                    if (nested is T nestedMatch)
                    {
                        yield return nestedMatch;
                    }

                    queue.Enqueue(nested);
                }
            }
        }
    }

    private void CategoryOrderHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_categoryOrderDrag is not null ||
            sender is not FrameworkElement handle ||
            FindAncestorCategoryOrderRow(handle) is not { } row ||
            !CategoryOrderList.Children.Contains(row))
        {
            return;
        }

        _categoryOrderDrag = new CategoryOrderDragSession
        {
            CaptureElement = handle,
            SourceIndex = CategoryOrderList.Children.IndexOf(row),
            StartY = e.GetPosition(CategoryOrderList).Y,
            TargetIndex = CategoryOrderList.Children.IndexOf(row)
        };
        row.Opacity = 0.55;
        foreach (Border child in CategoryOrderList.Children)
        {
            child.RenderTransform = new TranslateTransform();
        }

        CategoryOrderInsertionIndicator.Visibility = Visibility.Visible;
        UpdateCategoryOrderIndicator(_categoryOrderDrag.SourceIndex);

        if (!handle.CaptureMouse())
        {
            // 捕获失败时 LostMouseCapture 不会补发：立即结束会话，杜绝会话泄漏
            // （泄漏会阻断后续拖拽与上移/下移，指示线常驻）。
            CancelCategoryOrderDrag();
            return;
        }

        handle.MouseMove += CategoryOrderDrag_MouseMove;
        handle.MouseLeftButtonUp += CategoryOrderDrag_MouseLeftButtonUp;
        handle.LostMouseCapture += CategoryOrderDrag_LostMouseCapture;
        e.Handled = true;
    }

    private void CategoryOrderDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (_categoryOrderDrag is not { } session)
        {
            return;
        }

        var delta = e.GetPosition(CategoryOrderList).Y - session.StartY;
        if (CategoryOrderList.Children[session.SourceIndex] is Border { RenderTransform: TranslateTransform dragTransform })
        {
            // 拖起行 1:1 跟随光标，不做动画。
            dragTransform.BeginAnimation(TranslateTransform.YProperty, null);
            dragTransform.Y = delta;
        }

        // 落点 = 拖起行实时中心所落的行带。
        var center = session.SourceIndex * CategoryOrderPitch + delta + (CategoryOrderPitch / 2);
        session.TargetIndex = (int)Math.Clamp(
            Math.Floor(center / CategoryOrderPitch),
            0,
            CategoryOrderList.Children.Count - 1);
        UpdateCategoryOrderIndicator(session.TargetIndex);

        for (var index = 0; index < CategoryOrderList.Children.Count; index++)
        {
            if (index == session.SourceIndex ||
                CategoryOrderList.Children[index] is not Border row)
            {
                continue;
            }

            var offset = 0d;
            if (session.TargetIndex > session.SourceIndex &&
                index > session.SourceIndex &&
                index <= session.TargetIndex)
            {
                offset = -CategoryOrderPitch;
            }
            else if (session.TargetIndex < session.SourceIndex &&
                index < session.SourceIndex &&
                index >= session.TargetIndex)
            {
                offset = CategoryOrderPitch;
            }

            SetCategoryOrderRowOffset(row, offset);
        }
    }

    private async void CategoryOrderDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_categoryOrderDrag is not { } session)
        {
            return;
        }

        var sourceIndex = session.SourceIndex;
        var targetIndex = session.TargetIndex;
        EndCategoryOrderDragSession(session.CaptureElement);
        // 先按宿主当前顺序重建行复位拖拽视觉，再提交：保存期间 UI 不停在拖拽态；
        // 保存失败时宿主保持原顺序，重建即回弹。
        PopulateCategoryOrderSection();
        if (targetIndex != sourceIndex)
        {
            var order = _host.CurrentDisplayOrder.ToList();
            var moved = order[sourceIndex];
            order.RemoveAt(sourceIndex);
            order.Insert(targetIndex, moved);
            await _host.ApplyCategoryOrderAsync(order);
            PopulateCategoryOrderSection();
        }

        e.Handled = true;
    }

    private void CategoryOrderDrag_LostMouseCapture(object sender, MouseEventArgs e) =>
        CancelCategoryOrderDrag();

    /// <summary>取消拖拽会话并回弹原序不提交（Esc 与捕获丢失共用）。</summary>
    private void CancelCategoryOrderDrag()
    {
        if (_categoryOrderDrag is not { } session)
        {
            return;
        }

        EndCategoryOrderDragSession(session.CaptureElement);
        PopulateCategoryOrderSection();
    }

    private void EndCategoryOrderDragSession(object detachFrom)
    {
        _categoryOrderDrag = null;
        CategoryOrderInsertionIndicator.Visibility = Visibility.Collapsed;
        if (detachFrom is FrameworkElement element)
        {
            element.ReleaseMouseCapture();
            element.MouseMove -= CategoryOrderDrag_MouseMove;
            element.MouseLeftButtonUp -= CategoryOrderDrag_MouseLeftButtonUp;
            element.LostMouseCapture -= CategoryOrderDrag_LostMouseCapture;
        }
    }

    private void UpdateCategoryOrderIndicator(int targetIndex)
    {
        CategoryOrderInsertionIndicator.Margin = new Thickness(
            0,
            targetIndex * CategoryOrderPitch - 1,
            0,
            0);
    }

    /// <summary>让位行偏移：动效开启时按既有 DesignTokens 档过渡，关闭则瞬时。</summary>
    private void SetCategoryOrderRowOffset(Border row, double offset)
    {
        if (row.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        if (_host.CurrentPreferences.AnimationsEnabled)
        {
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(offset, TimeSpan.FromMilliseconds(DesignTokens.NormalDurationMs))
                {
                    FillBehavior = FillBehavior.Stop
                });
            transform.Y = offset;
        }
        else
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = offset;
        }
    }

    /// <summary>把手在行 Border 的内层 Grid 里，沿可视树向上找带分类标记的行 Border
    /// （把手自身也是 Border，必须按 Tag 过滤而不是按类型停止）。</summary>
    private static Border? FindAncestorCategoryOrderRow(DependencyObject? start)
    {
        for (var current = VisualTreeHelper.GetParent(start!);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border { Tag: BoardCategory } row)
            {
                return row;
            }
        }

        return null;
    }
}
