using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 文字卡与右上角操作按钮的排他空间契约（1.20.0，1.24.0 扩为三列，1.29.0 扩为
/// 四列，1.30.0 随 Issue #103 改为右上角固定两列网格）：对半分区（1.18.0）分层
/// 解决了命中歧义，但视觉层没有为角按钮预留排他空间——悬停淡入的置顶/选择/
/// 删除/添加备注按钮压在正文右上角之上。修复为文字卡正文右侧内缩（派生资源
/// CardTextReservedRightInset），按钮网格固定两列 × N 行、隐藏留空不漂移，
/// 命中层与图片卡全幅不变。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static System.Windows.Controls.Primitives.ButtonBase[] CardOperationButtons(
        ListBoxItem container) =>
        FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
            .Where(button => Equals(button.CommandParameter, CardGestureZones.TogglePinCommand) ||
                Equals(button.CommandParameter, CardGestureZones.ToggleSelectionCommand) ||
                Equals(button.CommandParameter, CardGestureZones.DeleteCardCommand) ||
                Equals(button.CommandParameter, CardGestureZones.PluginActionsCommand) ||
                (Equals(button.CommandParameter, CardGestureZones.NoteStripCommand) &&
                    button.Name == "CardOperationNoteButton"))
            .ToArray();

    /// <summary>备注钮激活（FadeAnimation 直驱）后按钮条的实际几何并集。
    /// 塌缩中的元素（Visibility=Collapsed）不计入——它们没有布局槽。</summary>
    private static Rect MeasuredOperationStrip(
        System.Collections.Generic.IEnumerable<System.Windows.Controls.Primitives.ButtonBase> buttons,
        FrameworkElement container) =>
        buttons
            .Where(button => button.Visibility != Visibility.Collapsed)
            .Select(button => new Rect(
                button.TranslatePoint(new Point(), container),
                new Size(button.ActualWidth, button.ActualHeight)))
            .Aggregate((left, right) => Rect.Union(left, right));

    [STATestMethod]
    public void CardText_LayoutSlotNeverOverlapsOperationButtons()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText(new string('避', 160), BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = FindDescendants<ListBoxItem>(list).Single();
            var body = FindDescendants<TextBlock>(list)
                .Single(candidate => candidate.Text?.StartsWith('避') == true);
            var buttons = CardOperationButtons(container);
            // Issue #240 起过滤器含插件动作钮（追加行右格），共五枚。
            Assert.HasCount(5, buttons);
            // 激活备注钮以在最大态（网格完整两列）测量；悬停揭示是触发器语义，
            // 本测试守卫的是几何派生，直接驱动 FadeAnimation 即可。
            FadeAnimation.SetIsActive(
                (System.Windows.Controls.Primitives.ButtonBase)FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
                    .Single(button => button.Name == "CardOperationNoteButton"),
                true);
            CompleteLayout(window);

            // 按钮淡入只改 Opacity、不改布局几何，正文布局槽与按钮边界的静态断言
            // 即覆盖悬停/非悬停两种可见态（任何时刻零像素争用）。
            var textSlot = new Rect(
                body.TranslatePoint(new Point(), container),
                new Size(body.ActualWidth, body.ActualHeight));
            var strip = MeasuredOperationStrip(buttons, container);

            Assert.IsFalse(
                textSlot.Right > strip.Left,
                $"文字布局槽右缘 {textSlot.Right} 侵入操作按钮条左缘 {strip.Left}：" +
                "正文必须为右上角按钮预留排他空间。");
            Assert.IsTrue(
                textSlot.Right <= strip.Left,
                "正文右缘必须在按钮条左缘及其间隙之外（零交集）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardTextReservedInset_DerivesFromMeasuredOperationStrip()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText(new string('派', 160), BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = FindDescendants<ListBoxItem>(list).Single();
            var buttons = CardOperationButtons(container);
            FadeAnimation.SetIsActive(
                (System.Windows.Controls.Primitives.ButtonBase)buttons
                    .Single(button => button.Name == "CardOperationNoteButton"),
                true);
            CompleteLayout(window);
            var strip = MeasuredOperationStrip(buttons, container);
            var columns = (GridLength)window.FindResource("CardOperationColumnWidth");
            var inset = (Thickness)window.FindResource("CardTextReservedRightInset");

            // 派生关系锁定（1.30.0 起操作网格两列，最大态测量）：按钮网格实际宽度 =
            // 两列 CardOperationColumnWidth；正文右内缩 = 网格宽度 + 4 DIP 间隙。
            // 改操作列宽或列数必须同步派生资源，禁止在别处出现第二个手写数值。
            Assert.AreEqual(
                2 * columns.Value,
                strip.Width,
                0.5,
                "按钮网格实际宽度必须等于两列 CardOperationColumnWidth。");
            Assert.AreEqual(
                2 * columns.Value + 4,
                inset.Right,
                0.01,
                "CardTextReservedRightInset 必须 = 两列操作网格 + 4 DIP 间隙。");
            Assert.IsTrue(
                inset.Left == 0 && inset.Top == 0 && inset.Bottom == 0,
                "内缩只作用于右侧。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardOperationStrip_HiddenNoteButtonKeepsGridCells()
    {
        // 1.30.0 固定格子语义锁定（取代 1.29.0 备注列 Auto 塌缩，Issue #103）：
        // 操作网格为固定两列 × N 行（绝对 30 DIP 行列），备注钮不可见时（基态
        // Collapsed）其 (1,1) 格子留空、其余三钮几何逐像素不动，网格恒为两列、
        // 右缘贴 OperationsZone 右缘。绝对行列下 Collapsed 天然留空——
        // UniformGrid（Arrange 跳过 Collapsed 补位重排）与定宽 WrapPanel
        // （换行随宽度漂移）被结构性排除；纯 Opacity 隐藏也禁止（不释放命中）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var shortItem = board.AddText("短卡字", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, shortItem);
            var columns = (GridLength)window.FindResource("CardOperationColumnWidth");
            var noteButton = CardNoteStripButtonOf(container);
            var buttons = CardOperationButtons(container);
            var zone = HalvesZoneOf(container, CardGestureZones.OperationsZone);
            var zoneRight = zone.TranslatePoint(new Point(zone.ActualWidth, 0), container).X;

            static Rect RectOf(
                System.Windows.Controls.Primitives.ButtonBase button,
                FrameworkElement container) =>
                new(
                    button.TranslatePoint(new Point(), container),
                    new Size(button.ActualWidth, button.ActualHeight));
            var others = buttons.Where(button => !ReferenceEquals(button, noteButton))
                .Select(button => RectOf(button, container))
                .ToArray();

            // 隐藏态（备注钮基态塌缩）：格子留空——网格恒两列、右缘锚定。
            Assert.AreEqual(
                Visibility.Collapsed, noteButton.Visibility,
                "短卡的备注钮必须塌缩（格子固定预留、可见性塌缩）。");
            var hiddenStrip = others.Aggregate((left, right) => Rect.Union(left, right));
            Assert.AreEqual(
                2 * columns.Value, hiddenStrip.Width, 0.5,
                "备注钮隐藏时按钮网格宽度必须恒为两列（格子留空不塌缩）。");
            Assert.AreEqual(
                zoneRight, hiddenStrip.Right, 0.5,
                "按钮网格右缘必须贴 OperationsZone 右缘（右缘锚定）。");

            // 最大态（备注钮激活）：其余三钮零位移，备注钮独占右下格 (1,1)。
            FadeAnimation.SetIsActive(noteButton, true);
            CompleteLayout(window);
            var visibleRects = buttons.Where(button => !ReferenceEquals(button, noteButton))
                .Select(button => RectOf(button, container))
                .ToArray();
            for (var i = 0; i < visibleRects.Length; i++)
            {
                Assert.AreEqual(
                    others[i], visibleRects[i],
                    "备注钮显隐不得移动任何其他按钮（格子固定，隐藏留空）。");
            }

            var noteRect = RectOf(noteButton, container);
            Assert.AreEqual(
                hiddenStrip.Left + columns.Value, noteRect.Left, 0.5,
                "备注钮必须独占右列（右下格 (1,1)）。");
            var pinButton = buttons.Single(candidate =>
                Equals(candidate.CommandParameter, CardGestureZones.TogglePinCommand));
            var pinRect = RectOf(pinButton, container);
            Assert.AreEqual(
                noteRect.Top, pinRect.Top, 0.5,
                "备注钮与置顶钮必须同处第二行（行优先网格序）。");
            var deleteButton = buttons.Single(candidate =>
                Equals(candidate.CommandParameter, CardGestureZones.DeleteCardCommand));
            var deleteRect = RectOf(deleteButton, container);
            Assert.AreEqual(
                4, pinRect.Top - deleteRect.Bottom, 0.5,
                "两按钮行之间的行间隙必须恰为 4 DIP（独立间隔行，实际排列位置）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
