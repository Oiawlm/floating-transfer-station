using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 文字卡与右上角操作按钮的排他空间契约（1.20.0，1.24.0 扩为三列，1.29.0 随
/// 添加备注按钮扩为四列）：对半分区（1.18.0）分层解决了命中歧义，但视觉层没有
/// 为角按钮预留排他空间——悬停淡入的置顶/选择/删除/添加备注按钮压在正文右上角
/// 之上。修复为文字卡正文右侧内缩（派生资源 CardTextReservedRightInset），
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
            Assert.HasCount(4, buttons);
            // 激活备注钮以在最大态（四列）测量；悬停揭示是触发器语义，本测试
            // 守卫的是几何派生，直接驱动 FadeAnimation 即可。
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

            // 派生关系锁定（1.29.0 起操作条四列，最大态测量）：按钮条实际宽度 =
            // 四列 CardOperationColumnWidth；正文右内缩 = 按钮条宽度 + 4 DIP 间隙。
            // 改操作列宽或列数必须同步派生资源，禁止在别处出现第二个手写数值。
            Assert.AreEqual(
                4 * columns.Value,
                strip.Width,
                0.5,
                "按钮条实际宽度必须等于四列 CardOperationColumnWidth。");
            Assert.AreEqual(
                4 * columns.Value + 4,
                inset.Right,
                0.01,
                "CardTextReservedRightInset 必须 = 四列操作列 + 4 DIP 间隙。");
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
    public void CardOperationStrip_HiddenNoteButtonCollapsesColumn()
    {
        // 1.29.0 塌缩语义锁定：备注钮不可见时（Visibility=Collapsed + Auto 列），
        // 按钮条宽度与三钮时代逐像素一致（=三列、右缘贴 OperationsZone 右缘）；
        // 激活时新列向左生长（=四列、右缘不动、置顶钮整体左移恰好一列）。
        // 纯 Opacity 隐藏会让列宽不塌缩、三钮几何漂移，禁止（契约 #22 修订）。
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

            // 隐藏态（备注钮塌缩）：三钮几何与 1.24.0 时代逐像素一致。
            Assert.AreEqual(
                Visibility.Collapsed, noteButton.Visibility,
                "短卡的备注钮必须塌缩（布局塌缩语义，Auto 列宽归零）。");
            var hiddenStrip = MeasuredOperationStrip(buttons, container);
            Assert.AreEqual(
                3 * columns.Value, hiddenStrip.Width, 0.5,
                "备注钮塌缩后按钮条宽度必须回落三钮时代（三列）。");
            Assert.AreEqual(
                zoneRight, hiddenStrip.Right, 0.5,
                "按钮条右缘必须贴 OperationsZone 右缘（右缘锚定）。");

            // 最大态（备注钮激活）：新列向左生长——四列、右缘不动、置顶钮左移一列。
            FadeAnimation.SetIsActive(noteButton, true);
            CompleteLayout(window);
            var visibleStrip = MeasuredOperationStrip(buttons, container);
            Assert.AreEqual(
                4 * columns.Value, visibleStrip.Width, 0.5,
                "备注钮激活后按钮条宽度必须为四列。");
            Assert.AreEqual(
                hiddenStrip.Right, visibleStrip.Right, 0.5,
                "右缘锚定不得漂移（新列只能向左生长）。");
            var pinButton = buttons.Single(candidate =>
                Equals(candidate.CommandParameter, CardGestureZones.TogglePinCommand));
            Assert.AreEqual(
                visibleStrip.Left + columns.Value,
                pinButton.TranslatePoint(new Point(), container).X, 0.5,
                "备注钮必须独占最左列（置顶钮左缘 = 按钮条左缘 + 一整列）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
