using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 设置窗口「子分组顺序」节（契约 #57 修订，「标签顺序」节〔契约 #21，
/// <see cref="SettingsWindow.CategoryOrder.cs"/>〕的全套镜像）：每个内容分类一个
/// 小节、四行固定槽（槽号即身份，只调顺序、不能增删槽）；拖拽自研最小实现与
/// 上移/下移双通道（WCAG 2.5.7）、Esc 优先回弹、捕获丢失回弹、纯点击不提交、
/// 乐观提交/失败回弹/单飞提交语义逐项同构复用，参数化差异仅＝Tag 承载槽号、
/// 会话/提交携带操作分类、两个分类小节列表容器。行画刷经动态资源引用解析
/// （主题活切换跟随）。门控＝SubgroupDisplay≠Off（整卡置灰非隐藏，
/// <see cref="SetSubgroupOrderAvailability"/>）。
/// </summary>
public partial class SettingsWindow
{
    private SubgroupOrderDragSession? _subgroupOrderDrag;
    private bool _isSubgroupOrderCommitInFlight;
    private SubgroupOrderSection[] _subgroupOrderSections = Array.Empty<SubgroupOrderSection>();

    /// <summary>某内容分类的小节宿主：行列表、指示线与分类名标题（XAML 命名约定
    /// ＝SubgroupOrder{List|Indicator|Title}{分类名}，代码后置按分类寻位）。</summary>
    private sealed record SubgroupOrderSection(
        BoardCategory Category,
        StackPanel List,
        Border Indicator,
        TextBlock Title);

    private sealed class SubgroupOrderDragSession
    {
        public required BoardCategory Category { get; init; }
        public required FrameworkElement CaptureElement { get; init; }
        public required int SourceIndex { get; init; }
        public required double StartY { get; init; }

        /// <summary>落点随拖拽移动更新，初值必须等于起点：纯点击把手（无移动）
        /// 松手时 target==source，不做任何移动。</summary>
        public int TargetIndex { get; set; }
    }

    private void PopulateSubgroupOrderSection()
    {
        if (_subgroupOrderSections.Length == 0)
        {
            _subgroupOrderSections = BoardCategoryCatalog.Ordered
                .Where(BoardCategoryCatalog.IsContentBucket)
                .Select(BuildSubgroupOrderSection)
                .ToArray();
        }

        foreach (var section in _subgroupOrderSections)
        {
            section.List.Children.Clear();
            section.Title.Text = _host.CategoryDisplayName(section.Category);
            var order = _host.CurrentSubgroupOrder(section.Category);
            for (var index = 0; index < order.Count; index++)
            {
                section.List.Children.Add(
                    CreateSubgroupOrderRow(section.Category, order[index], index, order.Count));
            }
        }
    }

    private SubgroupOrderSection BuildSubgroupOrderSection(BoardCategory category) => new(
        category,
        (StackPanel)FindName($"SubgroupOrderList{category}")!,
        (Border)FindName($"SubgroupOrderIndicator{category}")!,
        (TextBlock)FindName($"SubgroupOrderTitle{category}")!);

    /// <summary>门控（契约 #57 修订，D5）：SubgroupDisplay==Off 整卡置灰（非隐藏，
    /// 镜像 <c>SetAiOrganizeAreasAvailability</c> 先例）＋说明行切为引导句；构造
    /// 同步段与三态下拉即时生效后联动，不加新偏好开关。</summary>
    private void SetSubgroupOrderAvailability(SubgroupDisplayMode mode)
    {
        var enabled = mode != SubgroupDisplayMode.Off;
        SubgroupOrderCard.IsEnabled = enabled;
        SubgroupOrderNote.Text = enabled
            ? "调整每个分类内四个分组的显示顺序。"
            : "先在 通用 → 实验性 开启「分类内子分组」。";
    }

    private Border CreateSubgroupOrderRow(BoardCategory category, int slot, int index, int count)
    {
        var name = _host.SubgroupDisplayName(category, slot);
        var row = new Border
        {
            Height = CategoryOrderPitch,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Tag = slot
        };
        // 行画刷经动态资源引用解析（对齐全仓 DynamicResource 语义）：主题活切换即
        // 跟随新字典，不定格在构建瞬间（与「标签顺序」节同构）。
        row.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
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
        var handleGlyph = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        handleGlyph.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
        handle.Child = handleGlyph;
        AutomationProperties.SetName(handle, $"拖动「{name}」调整顺序");
        handle.MouseLeftButtonDown += SubgroupOrderHandle_MouseLeftButtonDown;
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
        AutomationProperties.SetName(label, $"分组{name}");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var moveUp = new Button
        {
            Style = TryFindResource("ThemedActionButtonStyle") as Style,
            Content = "↑",
            Width = 30,
            Height = 24,
            Padding = new Thickness(0),
            FontSize = 14,
            Margin = new Thickness(0, 0, 4, 0),
            IsEnabled = index > 0,
            Tag = (category, slot)
        };
        AutomationProperties.SetName(moveUp, $"上移「{name}」");
        moveUp.Click += SubgroupOrderMoveUpButton_Click;
        Grid.SetColumn(moveUp, 2);
        grid.Children.Add(moveUp);

        var moveDown = new Button
        {
            Style = TryFindResource("ThemedActionButtonStyle") as Style,
            Content = "↓",
            Width = 30,
            Height = 24,
            Padding = new Thickness(0),
            FontSize = 14,
            IsEnabled = index < count - 1,
            Tag = (category, slot)
        };
        AutomationProperties.SetName(moveDown, $"下移「{name}」");
        moveDown.Click += SubgroupOrderMoveDownButton_Click;
        Grid.SetColumn(moveDown, 3);
        grid.Children.Add(moveDown);

        row.Child = grid;
        return row;
    }

    private async void SubgroupOrderMoveUpButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSubgroupOrderBy(sender, delta: -1);

    private async void SubgroupOrderMoveDownButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSubgroupOrderBy(sender, delta: 1);

    /// <summary>统一提交路径（拖拽松手与上移/下移共用，<paramref name="order"/> 为
    /// 该分类目标序）：宿主乐观契约先同步应用目标序，故发起提交后立即重建行即目标
    /// 序（松手同一 UI 消息内上屏）；await 完成（＝持久化完成）后仅当该分类行序与
    /// 宿主当前序不一致时二次重建——唯一来源＝保存失败回弹。单飞语义在本入口收口
    /// （跨两分类共享一旗：顺序清单为全量快照，并发提交与失败回滚的组合会产生持久
    /// 分歧），上一提交仍在途时丢弃本次触发，两小节行复位到宿主当前序。</summary>
    private async Task CommitSubgroupOrderAsync(SubgroupOrderSection section, IReadOnlyList<int> order)
    {
        if (_isSubgroupOrderCommitInFlight)
        {
            PopulateSubgroupOrderSection();
            return;
        }

        _isSubgroupOrderCommitInFlight = true;
        try
        {
            var apply = _host.ApplySubgroupOrderAsync(section.Category, order);
            PopulateSubgroupOrderSection();
            await apply;
            if (_subgroupOrderSections.Any(current => !SubgroupOrderRowsMatchHost(current)))
            {
                PopulateSubgroupOrderSection();
            }
        }
        finally
        {
            _isSubgroupOrderCommitInFlight = false;
        }
    }

    private bool SubgroupOrderRowsMatchHost(SubgroupOrderSection section) =>
        section.List.Children.Cast<Border>()
            .Select(row => (int)row.Tag)
            .SequenceEqual(_host.CurrentSubgroupOrder(section.Category));

    /// <summary>上移/下移等价操作（WCAG 2.5.7）：提交后把焦点还给同分类同槽的对应
    /// 按钮，保持键盘连续操作。</summary>
    private async Task MoveSubgroupOrderBy(object sender, int delta)
    {
        if (_subgroupOrderDrag is not null ||
            _isSubgroupOrderCommitInFlight ||
            sender is not Button { Tag: (BoardCategory category, int slot) })
        {
            return;
        }

        var order = _host.CurrentSubgroupOrder(category).ToList();
        var index = order.IndexOf(slot);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return;
        }

        (order[index], order[target]) = (order[target], order[index]);
        await CommitSubgroupOrderAsync(SectionFor(category), order);

        if (TryFindSubgroupOrderButton(category, slot, delta) is { } button)
        {
            button.Focus();
        }
    }

    private SubgroupOrderSection SectionFor(BoardCategory category) =>
        _subgroupOrderSections.Single(section => section.Category == category);

    private Button? TryFindSubgroupOrderButton(BoardCategory category, int slot, int delta)
    {
        var content = delta < 0 ? "↑" : "↓";
        foreach (var section in _subgroupOrderSections)
        {
            var match = FindSubgroupOrderElements<Button>(section.List)
                .FirstOrDefault(button =>
                    button.Tag is (BoardCategory buttonCategory, int buttonSlot) &&
                    buttonCategory == category &&
                    buttonSlot == slot &&
                    Equals(button.Content, content));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private IEnumerable<T> FindSubgroupOrderElements<T>(StackPanel list) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(list);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(list, index);
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

    private void SubgroupOrderHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_subgroupOrderDrag is not null ||
            sender is not FrameworkElement handle ||
            FindAncestorSubgroupOrderRow(handle) is not { } row)
        {
            return;
        }

        var section = SectionOfRow(row);
        _subgroupOrderDrag = new SubgroupOrderDragSession
        {
            Category = section.Category,
            CaptureElement = handle,
            SourceIndex = section.List.Children.IndexOf(row),
            StartY = e.GetPosition(section.List).Y,
            TargetIndex = section.List.Children.IndexOf(row)
        };
        row.Opacity = 0.55;
        foreach (Border child in section.List.Children)
        {
            child.RenderTransform = new TranslateTransform();
        }

        section.Indicator.Visibility = Visibility.Visible;
        UpdateSubgroupOrderIndicator(section, _subgroupOrderDrag.SourceIndex);

        if (!handle.CaptureMouse())
        {
            // 捕获失败时 LostMouseCapture 不会补发：立即结束会话，杜绝会话泄漏
            // （泄漏会阻断后续拖拽与上移/下移，指示线常驻）。
            CancelSubgroupOrderDrag();
            return;
        }

        handle.MouseMove += SubgroupOrderDrag_MouseMove;
        handle.MouseLeftButtonUp += SubgroupOrderDrag_MouseLeftButtonUp;
        handle.LostMouseCapture += SubgroupOrderDrag_LostMouseCapture;
        e.Handled = true;
    }

    private void SubgroupOrderDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (_subgroupOrderDrag is not { } session)
        {
            return;
        }

        var section = SectionFor(session.Category);
        var delta = e.GetPosition(section.List).Y - session.StartY;
        if (section.List.Children[session.SourceIndex] is Border { RenderTransform: TranslateTransform dragTransform })
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
            section.List.Children.Count - 1);
        UpdateSubgroupOrderIndicator(section, session.TargetIndex);

        for (var index = 0; index < section.List.Children.Count; index++)
        {
            if (index == session.SourceIndex ||
                section.List.Children[index] is not Border row)
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

            SetSubgroupOrderRowOffset(row, offset);
        }
    }

    private async void SubgroupOrderDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_subgroupOrderDrag is not { } session)
        {
            return;
        }

        var section = SectionFor(session.Category);
        var sourceIndex = session.SourceIndex;
        var targetIndex = session.TargetIndex;
        EndSubgroupOrderDragSession(session.CaptureElement);
        if (targetIndex == sourceIndex)
        {
            // 纯点击（无移动）：仅复位拖拽视觉，不提交。
            PopulateSubgroupOrderSection();
        }
        else
        {
            var order = _host.CurrentSubgroupOrder(section.Category).ToList();
            var moved = order[sourceIndex];
            order.RemoveAt(sourceIndex);
            order.Insert(targetIndex, moved);
            await CommitSubgroupOrderAsync(section, order);
        }

        e.Handled = true;
    }

    private void SubgroupOrderDrag_LostMouseCapture(object sender, MouseEventArgs e) =>
        CancelSubgroupOrderDrag();

    /// <summary>取消拖拽会话并回弹原序不提交（Esc 与捕获丢失共用）。</summary>
    private void CancelSubgroupOrderDrag()
    {
        if (_subgroupOrderDrag is not { } session)
        {
            return;
        }

        EndSubgroupOrderDragSession(session.CaptureElement);
        PopulateSubgroupOrderSection();
    }

    private void EndSubgroupOrderDragSession(object detachFrom)
    {
        _subgroupOrderDrag = null;
        foreach (var section in _subgroupOrderSections)
        {
            section.Indicator.Visibility = Visibility.Collapsed;
        }

        if (detachFrom is FrameworkElement element)
        {
            element.ReleaseMouseCapture();
            element.MouseMove -= SubgroupOrderDrag_MouseMove;
            element.MouseLeftButtonUp -= SubgroupOrderDrag_MouseLeftButtonUp;
            element.LostMouseCapture -= SubgroupOrderDrag_LostMouseCapture;
        }
    }

    private void UpdateSubgroupOrderIndicator(SubgroupOrderSection section, int targetIndex) =>
        section.Indicator.Margin = new Thickness(
            0,
            targetIndex * CategoryOrderPitch - 1,
            0,
            0);

    /// <summary>让位行偏移：动效开启时按既有 DesignTokens 档过渡，关闭则瞬时。</summary>
    private void SetSubgroupOrderRowOffset(Border row, double offset)
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

    private SubgroupOrderSection SectionOfRow(Border row) =>
        _subgroupOrderSections.First(section => section.List.Children.Contains(row));

    /// <summary>把手在行 Border 的内层 Grid 里，沿可视树向上找带槽号标记的行 Border
    /// （把手自身也是 Border，必须按 Tag 过滤而不是按类型停止；行 Tag＝槽号 int）。</summary>
    private static Border? FindAncestorSubgroupOrderRow(DependencyObject? start)
    {
        for (var current = VisualTreeHelper.GetParent(start!);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border { Tag: int } row)
            {
                return row;
            }
        }

        return null;
    }
}
