using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 文字卡与右上角操作按钮的排他空间契约（1.20.0，1.24.0 随卡片删除按钮扩为三列）：
/// 对半分区（1.18.0）分层解决了命中歧义，但视觉层没有为角按钮预留排他空间——
/// 悬停淡入的置顶/选择/删除按钮压在正文第一、二行的右上角之上。修复为文字卡正文
/// 右侧内缩（派生资源 CardTextReservedRightInset），命中层与图片卡全幅不变。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static System.Windows.Controls.Primitives.ButtonBase[] CardOperationButtons(
        ListBoxItem container) =>
        FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
            .Where(button => Equals(button.CommandParameter, CardGestureZones.TogglePinCommand) ||
                Equals(button.CommandParameter, CardGestureZones.ToggleSelectionCommand) ||
                Equals(button.CommandParameter, CardGestureZones.DeleteCardCommand))
            .ToArray();

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
            Assert.HasCount(3, buttons);

            // 按钮淡入只改 Opacity、不改布局几何，正文布局槽与按钮边界的静态断言
            // 即覆盖悬停/非悬停两种可见态（任何时刻零像素争用）。
            var textSlot = new Rect(
                body.TranslatePoint(new Point(), container),
                new Size(body.ActualWidth, body.ActualHeight));
            var buttonBounds = buttons.Select(button => new Rect(
                button.TranslatePoint(new Point(), container),
                new Size(button.ActualWidth, button.ActualHeight)));
            var strip = buttonBounds.Aggregate((left, right) => Rect.Union(left, right));

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
            var buttonBounds = buttons.Select(button => new Rect(
                button.TranslatePoint(new Point(), container),
                new Size(button.ActualWidth, button.ActualHeight)));
            var strip = buttonBounds.Aggregate((left, right) => Rect.Union(left, right));
            var columns = (GridLength)window.FindResource("CardOperationColumnWidth");
            var inset = (Thickness)window.FindResource("CardTextReservedRightInset");

            // 派生关系锁定（1.24.0 起操作条三列）：按钮条实际宽度 = 三列
            // CardOperationColumnWidth；正文右内缩 = 按钮条宽度 + 4 DIP 间隙。
            // 改操作列宽或列数必须同步派生资源，禁止在别处出现第二个手写数值。
            Assert.AreEqual(
                3 * columns.Value,
                strip.Width,
                0.5,
                "按钮条实际宽度必须等于三列 CardOperationColumnWidth。");
            Assert.AreEqual(
                3 * columns.Value + 4,
                inset.Right,
                0.01,
                "CardTextReservedRightInset 必须 = 三列操作列 + 4 DIP 间隙。");
            Assert.IsTrue(
                inset.Left == 0 && inset.Top == 0 && inset.Bottom == 0,
                "内缩只作用于右侧。");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
